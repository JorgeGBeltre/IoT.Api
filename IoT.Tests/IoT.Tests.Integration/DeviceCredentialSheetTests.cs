using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Commands;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// La ficha de credenciales que la plataforma entrega al operador tiene que servir tal cual.
///
/// Decía "Usuario MQTT = ChipId" y "Contraseña = la ApiKey", pero el broker lee la ApiKey del
/// USERNAME (es lo que mandan los firmwares: Relay hace
/// <c>mqtt.connect(g_clientId, g_apiKey, "")</c> y el GPS
/// <c>connect(clientID, API_KEY, "")</c>). Un equipo configurado según la ficha —el GLP, que
/// tiene usuario y contraseña editables— era rechazado con NotAuthorized. Comprobado en vivo
/// contra el broker real: ApiKey en el username → Success; ChipId en el username y la clave
/// en la contraseña → NotAuthorized.
///
/// Ningún test cubría este reparto, que es por lo que el desajuste sobrevivió.
/// </summary>
public sealed class DeviceCredentialSheetTests
{
    private static (RotateDeviceApiKeyCommandHandler Handler, Device Device, string PlainKey) Armar()
    {
        var device = Device.Create(
            Guid.NewGuid(), "A1B2C3D4E5F6", "11:22:33:44:55:66", "Equipo", "ESP32", "Espressif").Value;

        const string plainKey = "clave-en-texto-plano";

        var repo = new Mock<IDeviceRepository>();
        repo.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var keys = new Mock<IDeviceApiKeyService>();
        keys.Setup(k => k.Generate()).Returns((plainKey, "hash", "ffff9999"));

        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var info = new Mock<IMqttConnectionInfoProvider>();
        info.SetupGet(i => i.PublicHost).Returns("broker.local");
        info.SetupGet(i => i.Port).Returns(1883);
        info.SetupGet(i => i.Tls).Returns(false);

        return (new RotateDeviceApiKeyCommandHandler(repo.Object, keys.Object, info.Object, uow.Object),
                device, plainKey);
    }

    [Fact]
    public async Task ElClientIdDeLaFichaEsElChipId()
    {
        // El broker EXIGE que el ClientId sea el ChipId del equipo (si no, un dispositivo
        // podía autenticarse con su clave y quedarse con el ClientId de otro). La ficha
        // tiene que decirlo, porque es un campo que el operador configura.
        var (handler, device, _) = Armar();

        var result = await handler.Handle(
            new RotateDeviceApiKeyCommand(device.TenantId, device.Id, "admin"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.MqttClientId.Should().Be(device.ChipId);
    }

    [Fact]
    public async Task LaFichaPoneLaApiKeyEnElUsuarioYEnLaContrasena()
    {
        // En los dos a propósito: cada firmware la manda en un campo distinto y el broker
        // acepta ambos, así que la misma ficha vale para todos. Lo que NO puede volver a
        // pasar es que el usuario sea el ChipId: eso es lo que el broker rechaza.
        var (handler, device, plainKey) = Armar();

        var result = await handler.Handle(
            new RotateDeviceApiKeyCommand(device.TenantId, device.Id, "admin"), CancellationToken.None);

        result.Value.MqttUsername.Should().Be(plainKey);
        result.Value.MqttPasswordPlaintext.Should().Be(plainKey);
        result.Value.MqttUsername.Should().NotBe(device.ChipId);
    }

    [Fact]
    public async Task LaHuellaYElChipIdSiguenExpuestosParaIdentificarAlEquipo()
    {
        // El ChipId sigue en la ficha como dato del equipo (y como ClientId); lo que cambia
        // es que ya no se presenta como el usuario de la conexión.
        var (handler, device, _) = Armar();

        var result = await handler.Handle(
            new RotateDeviceApiKeyCommand(device.TenantId, device.Id, "admin"), CancellationToken.None);

        result.Value.ChipId.Should().Be(device.ChipId);
        result.Value.MqttPasswordFingerprint.Should().Be("ffff9999");
    }
}
