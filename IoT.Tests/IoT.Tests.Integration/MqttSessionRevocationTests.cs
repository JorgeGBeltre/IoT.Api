using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Commands;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Domain.Enums;
using IoT.Infrastructure.Mqtt;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// MEDIUM-4: revocación en caliente de sesiones MQTT.
///
/// El broker sólo comprobaba la credencial en el CONNECT. Una vez dentro, la sesión y sus
/// suscripciones sobrevivían a cualquier cambio en el registro: suspender un equipo o
/// rotarle la ApiKey no le quitaba el acceso hasta que él decidiera reconectar, que en un
/// tracker con keepalive largo son días. El gateway TCP sí re-validaba cada 60 s.
/// </summary>
public sealed class MqttSessionRevocationTests
{
    private static Device DeviceConClave(string fingerprint = "aaaa1111")
    {
        var device = Device.Create(
            Guid.NewGuid(), "A1B2C3D4E5F6", "11:22:33:44:55:66", "Equipo", "ESP32", "Espressif").Value;
        device.SetMqttApiKey("hash-irrelevante", fingerprint).IsSuccess.Should().BeTrue();
        return device;
    }

    // -----------------------------------------------------------------
    // La regla: qué invalida una sesión ya abierta
    // -----------------------------------------------------------------

    [Fact]
    public void UnaSesionDeUnEquipoActivoYConLaMismaClaveSigueValida()
    {
        // Negativo de control, y el caso dominante: si esto se rompiera, el bucle de
        // re-validación echaría a TODOS los equipos cada ciclo.
        var device = DeviceConClave("aaaa1111");

        MqttSessionRevocation.ReasonToRevoke(device, "aaaa1111").Should().BeNull();
    }

    [Fact]
    public void RotarLaClaveInvalidaLaSesionAbiertaConLaAnterior()
    {
        var device = DeviceConClave("bbbb2222");

        MqttSessionRevocation.ReasonToRevoke(device, "aaaa1111")
            .Should().Contain("rotó");
    }

    [Theory]
    [InlineData(DeviceAdminStatus.Suspended)]
    [InlineData(DeviceAdminStatus.Revoked)]
    public void UnEquipoSuspendidoORevocadoPierdeLaSesion(DeviceAdminStatus estado)
    {
        var device = DeviceConClave();
        device.SetAdminStatus(estado);

        MqttSessionRevocation.ReasonToRevoke(device, "aaaa1111")
            .Should().Contain(estado.ToString());
    }

    [Fact]
    public void UnEquipoBorradoDelRegistroPierdeLaSesion()
    {
        // La sesión seguiría publicando telemetría de un equipo que ya no existe.
        MqttSessionRevocation.ReasonToRevoke(null, "aaaa1111")
            .Should().Contain("ya no está registrado");
    }

    [Fact]
    public void QuitarleLaClaveAUnEquipoLoEcha()
    {
        // Sin esto, revocar la credencial sin poner otra dejaba la sesión viva.
        var device = Device.Create(
            Guid.NewGuid(), "A1B2C3D4E5F6", "11:22:33:44:55:66", "Equipo", "ESP32", "Espressif").Value;

        device.HasMqttApiKey.Should().BeFalse();
        MqttSessionRevocation.ReasonToRevoke(device, null)
            .Should().Contain("ya no tiene ApiKey");
    }

    [Fact]
    public void SinHuellaDeSesionNoSeInventaUnaRotacion()
    {
        // Una sesión abierta antes de que existiera este control no tiene huella guardada.
        // No poder comparar NO es prueba de que la clave haya cambiado: echarla sería
        // cerrar sesiones buenas en cada ciclo.
        var device = DeviceConClave("bbbb2222");

        MqttSessionRevocation.ReasonToRevoke(device, null).Should().BeNull();
        MqttSessionRevocation.ReasonToRevoke(device, string.Empty).Should().BeNull();
    }

    // -----------------------------------------------------------------
    // Rotar la ApiKey cierra la sesión en el acto
    // -----------------------------------------------------------------

    [Fact]
    public async Task RotarLaApiKeyRevocaLaSesionDespuesDeGuardar()
    {
        var device = DeviceConClave("aaaa1111");
        var orden = new List<string>();

        var repo = new Mock<IDeviceRepository>();
        repo.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var keys = new Mock<IDeviceApiKeyService>();
        keys.Setup(k => k.Generate()).Returns(("texto-plano", "hash-nuevo", "cccc3333"));

        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => orden.Add("guardar")).ReturnsAsync(1);

        var revoker = new Mock<IMqttSessionRevoker>();
        revoker.Setup(r => r.RevokeSessionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => orden.Add("revocar")).ReturnsAsync(true);

        var info = new Mock<IMqttConnectionInfoProvider>();
        info.SetupGet(i => i.PublicHost).Returns("broker.local");
        info.SetupGet(i => i.Port).Returns(1883);
        info.SetupGet(i => i.Tls).Returns(false);

        var handler = new RotateDeviceApiKeyCommandHandler(
            repo.Object, keys.Object, info.Object, uow.Object, revoker.Object);

        var result = await handler.Handle(
            new RotateDeviceApiKeyCommand(device.TenantId, device.Id, "admin"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        revoker.Verify(r => r.RevokeSessionAsync(device.ChipId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        // El orden importa: si se cerrara la sesión ANTES de guardar, el equipo podía
        // reconectar en ese hueco con la clave vieja, que seguiría siendo la válida.
        orden.Should().Equal("guardar", "revocar");
    }

    [Fact]
    public async Task SiNoHayBrokerEnProcesoLaRotacionNoFalla()
    {
        // El revocador es opcional: en un despliegue donde el broker embebido no corre en
        // este proceso, rotar la clave tiene que seguir funcionando (la re-validación
        // periódica de la instancia que sí tiene la sesión la cerrará).
        var device = DeviceConClave("aaaa1111");

        var repo = new Mock<IDeviceRepository>();
        repo.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);
        var keys = new Mock<IDeviceApiKeyService>();
        keys.Setup(k => k.Generate()).Returns(("texto-plano", "hash-nuevo", "cccc3333"));
        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        var info = new Mock<IMqttConnectionInfoProvider>();
        info.SetupGet(i => i.PublicHost).Returns("broker.local");

        var handler = new RotateDeviceApiKeyCommandHandler(
            repo.Object, keys.Object, info.Object, uow.Object, sessionRevoker: null);

        var result = await handler.Handle(
            new RotateDeviceApiKeyCommand(device.TenantId, device.Id, "admin"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }
}
