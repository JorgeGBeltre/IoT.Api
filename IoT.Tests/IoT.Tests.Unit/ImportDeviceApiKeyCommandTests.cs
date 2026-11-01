using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Commands;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Security;
using Moq;
using Xunit;

namespace IoT.Tests.IoT.Tests.Unit;

/// <summary>
/// Importación de una ApiKey MQTT YA conocida como credencial per-device (para flotas cuyo
/// firmware trae una clave de fábrica compartida; p.ej. Toour/Fligo). Verifica que fija el
/// hash+huella de la clave dada, actualiza, cierra la sesión viva y devuelve sólo la huella;
/// y que la derivación coincide con la de una clave acuñada (de modo que el equipo autentica
/// por la vía per-device del broker con esa misma clave).
/// </summary>
public sealed class ImportDeviceApiKeyCommandTests
{
    private static readonly Guid T1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid T2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string Chip = "0000A1B2C3D4";
    private const string FleetKey = "BkuAB4Ps+49yuMdA@cUEmg-$nt-*ZwUn";

    private sealed record Mocks(
        Mock<IDeviceRepository> Devices,
        Mock<IDeviceApiKeyService> ApiKeys,
        Mock<IUnitOfWork> Uow,
        Mock<IMqttSessionRevoker> Revoker);

    private static Mocks NewMocks()
    {
        var devices = new Mock<IDeviceRepository>();
        var apiKeys = new Mock<IDeviceApiKeyService>();
        var revoker = new Mock<IMqttSessionRevoker>();
        revoker.Setup(r => r.RevokeSessionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return new Mocks(devices, apiKeys, new Mock<IUnitOfWork>(), revoker);
    }

    private static ImportDeviceApiKeyCommandHandler BuildHandler(Mocks m) =>
        new(m.Devices.Object, m.ApiKeys.Object, m.Uow.Object, m.Revoker.Object);

    private static Device NewDevice(Guid tenantId)
    {
        var d = Device.Create(tenantId, Chip, "24:6F:28:00:00:01", "Tracker Flota", "MQTT-LTE-TicketValidator", "Toour").Value;
        d.AssignToCustomer(Guid.NewGuid());
        return d;
    }

    [Fact]
    public async Task Handle_SetsKnownKey_TrimsInput_UpdatesRevokesAndReturnsFingerprintOnly()
    {
        var m = NewMocks();
        var device = NewDevice(T1);
        m.Devices.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);
        // La derivación se consulta con la clave YA recortada.
        m.ApiKeys.Setup(s => s.DeriveFromKnownKey(FleetKey)).Returns(("HASHHEX", "FPFPFPFP"));

        var handler = BuildHandler(m);
        var result = await handler.Handle(
            // Con espacios alrededor: el handler debe recortarlos antes de derivar.
            new ImportDeviceApiKeyCommand(T1, device.Id, $"  {FleetKey}  ", IssuedBy: "operador@t1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("FPFPFPFP", "sólo se devuelve la huella, nunca la clave en claro");

        device.HasMqttApiKey.Should().BeTrue();
        device.MqttApiKeyHash.Should().Be("HASHHEX");
        device.MqttApiKeyFingerprint.Should().Be("FPFPFPFP");
        device.MqttApiKeyIssuedBy.Should().Be("operador@t1");

        m.ApiKeys.Verify(s => s.DeriveFromKnownKey(FleetKey), Times.Once, "se deriva con la clave recortada");
        m.ApiKeys.Verify(s => s.Generate(), Times.Never, "importar NO acuña una clave aleatoria");
        m.Devices.Verify(r => r.Update(device), Times.Once);
        m.Uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        m.Revoker.Verify(r => r.RevokeSessionAsync(Chip, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once,
            "cambiar la credencial debe cerrar la sesión viva");
    }

    [Fact]
    public async Task Handle_DeviceNotFound_FailsWithoutMutations()
    {
        var m = NewMocks();
        var id = Guid.NewGuid();
        m.Devices.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((Device?)null);

        var result = await BuildHandler(m).Handle(
            new ImportDeviceApiKeyCommand(T1, id, FleetKey, "operador@t1"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Device.NotFound");
        m.Devices.Verify(r => r.Update(It.IsAny<Device>()), Times.Never);
        m.Uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        m.Revoker.Verify(r => r.RevokeSessionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_DeviceOfAnotherTenant_FailsNotFound_NoLeak()
    {
        var m = NewMocks();
        var device = NewDevice(T2); // pertenece a OTRO tenant
        m.Devices.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var result = await BuildHandler(m).Handle(
            new ImportDeviceApiKeyCommand(T1, device.Id, FleetKey, "operador@t1"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Device.NotFound", "no se revela que el equipo existe en otro tenant");
        device.HasMqttApiKey.Should().BeFalse();
        m.Devices.Verify(r => r.Update(It.IsAny<Device>()), Times.Never);
        m.Uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_BlankKey_FailsInvalidApiKey_WithoutLoadingDevice(string blank)
    {
        var m = NewMocks();

        var result = await BuildHandler(m).Handle(
            new ImportDeviceApiKeyCommand(T1, Guid.NewGuid(), blank, "operador@t1"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Device.InvalidApiKey");
        m.Devices.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        m.Uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// La pieza que hace que el esquema funcione: una clave importada es indistinguible de una
    /// acuñada frente al broker. La derivación real = (SHA-256 hex, primeros 8 hex) y la clave
    /// VERIFICA contra el hash guardado, así que el firmware que presenta esa clave autentica por
    /// la vía per-device (Verify compara en tiempo constante).
    /// </summary>
    [Fact]
    public void DeriveFromKnownKey_MatchesComputeHashAndFingerprint_AndVerifies()
    {
        var svc = new DeviceApiKeyService();

        var (hash, fingerprint) = svc.DeriveFromKnownKey(FleetKey);

        hash.Should().Be(svc.ComputeHash(FleetKey), "misma derivación que Generate (SHA-256 hex)");
        fingerprint.Should().Be(hash[..8]);
        fingerprint.Should().HaveLength(8);
        svc.Verify(FleetKey, hash).Should().BeTrue("el equipo que presente la clave de flota debe autenticar contra el hash importado");
        svc.Verify("clave-equivocada", hash).Should().BeFalse();
    }
}
