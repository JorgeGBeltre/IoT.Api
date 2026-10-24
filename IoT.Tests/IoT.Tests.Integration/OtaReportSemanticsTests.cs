using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Services;
using IoT.Protocols.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// Semántica de los reportes que publica el dispositivo durante una actualización OTA.
/// </summary>
public class OtaReportSemanticsTests
{
    // -----------------------------------------------------------------
    // "ts" es el uptime del equipo, no un epoch
    // -----------------------------------------------------------------

    [Fact]
    public void TheTsFieldIsDeviceUptimeAndMustNotBecomeTheEventDate()
    {
        // La librería del dispositivo publica millis(), los milisegundos desde que ARRANCÓ
        // (MQTTOTAv5.cpp:1037,1064,1091,1111). Interpretarlo como epoch daba fechas de 1970,
        // y como el pipeline hace device.RecordTelemetry(ev.RecordedAt), cada reporte OTA
        // mandaba el last-seen del equipo a 1970: la UI lo mostraba como no visto justo
        // mientras se estaba actualizando.
        const string progress = """
        {"device":"A1B2C3D4E5F6","version":"1.0.4","progress":40,"bytes":838860,"ts":128394}
        """;

        var before = DateTime.UtcNow.AddSeconds(-5);
        var result = new OtaDeviceAdapter().Decode("ota/A1B2C3D4E5F6/progress", progress);
        var after = DateTime.UtcNow.AddSeconds(5);

        result.IsSuccess.Should().BeTrue();
        var ev = result.Value;

        ev.RecordedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after,
            "la fecha del evento es la de recepción, no un uptime convertido a epoch");
        ev.RecordedAt.Year.Should().NotBe(1970);

        // El dato sigue estando, pero como lo que es: tiempo encendido.
        ev.NumericMetrics.Should().ContainKey("device_uptime_ms").WhoseValue.Should().Be(128394);
    }

    [Fact]
    public void TheOtaAdapterDoesNotInventADeviceIdentifier()
    {
        // "UNKNOWN-OTA-DEVICE" creaba un dispositivo fantasma y una fila de auditoría por
        // cada reporte. Sin identidad, manda la autenticada de la conexión.
        const string anonymous = """{"version":"1.0.4","progress":10}""";

        var ev = new OtaDeviceAdapter().Decode("ota/progress", anonymous).Value;

        ev.DeviceIdentifier.Should().NotBe("UNKNOWN-OTA-DEVICE");
        ev.DeviceIdentifier.Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // El éxito exige PRUEBA de lo instalado
    // -----------------------------------------------------------------

    private sealed record Harness(
        FirmwareOtaService Service,
        Device Device,
        Mock<IFleetNotificationService> Notifications);

    private static Harness BuildHarness()
    {
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "REPORT_CHIP_42", "11:22:33:44:55:66", "Equipo", "ESP32", "Espressif", "1.0.0").Value;
        device.StartOta("1.1.0", 5);

        var devRepo = new Mock<IDeviceRepository>();
        devRepo.Setup(r => r.GetByChipIdGlobalAsync("REPORT_CHIP_42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        var notifications = new Mock<IFleetNotificationService>();

        var service = new FirmwareOtaService(
            new Mock<IFirmwareVersionRecordRepository>().Object,
            devRepo.Object,
            new Mock<IUnitOfWork>().Object,
            new Mock<IMqttPublisher>().Object,
            notifications.Object,
            new Mock<IServiceScopeFactory>().Object,
            new Mock<ILogger<FirmwareOtaService>>().Object);

        return new Harness(service, device, notifications);
    }

    [Fact]
    public async Task ASuccessReportWithoutTheVersionFieldDoesNotCloseTheCampaign()
    {
        // ESTE era el hueco: al omitir "version", la comprobación de coincidencia se saltaba
        // y la campaña se cerraba como COMPLETADA sin que el equipo demostrara nada. La
        // librería real siempre incluye la versión, así que exigirla no rompe nada legítimo.
        var h = BuildHarness();

        await h.Service.HandleDeviceOtaReportAsync("REPORT_CHIP_42", "success", """{"success":true,"bytes":1048576}""");

        h.Device.OtaCompleted.Should().BeFalse("sin versión reportada no hay prueba de lo instalado");
        h.Device.FirmwareVersion.Should().Be("1.0.0", "la versión instalada no debe cambiar");
        h.Device.OtaErrorMessage.Should().NotBeNull();
        h.Device.OtaErrorMessage.Should().Contain("sin indicar la versión");
    }

    [Fact]
    public async Task ASuccessReportWithTheMatchingVersionClosesTheCampaign()
    {
        var h = BuildHarness();

        await h.Service.HandleDeviceOtaReportAsync("REPORT_CHIP_42", "success", """{"version":"1.1.0","success":true}""");

        h.Device.OtaCompleted.Should().BeTrue();
        h.Device.FirmwareVersion.Should().Be("1.1.0");
        h.Device.OtaErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task ASuccessReportWithADifferentVersionFailsTheCampaign()
    {
        // El equipo flasheó algo distinto de lo que se le mandó.
        var h = BuildHarness();

        await h.Service.HandleDeviceOtaReportAsync("REPORT_CHIP_42", "success", """{"version":"9.9.9","success":true}""");

        h.Device.OtaCompleted.Should().BeFalse();
        h.Device.FirmwareVersion.Should().Be("1.0.0");
        h.Device.OtaErrorMessage.Should().Contain("9.9.9");
    }

    [Fact]
    public async Task TheLeadingVTolerance()
    {
        // Lo único que varía legítimamente entre lo enviado y lo reportado es la 'v' y la caja.
        var h = BuildHarness();

        await h.Service.HandleDeviceOtaReportAsync("REPORT_CHIP_42", "success", """{"version":"V1.1.0","success":true}""");

        h.Device.OtaCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task AReportThatArrivesWithNoCampaignInProgressIsIgnored()
    {
        var h = BuildHarness();
        h.Device.FailOta("algo falló antes");

        await h.Service.HandleDeviceOtaReportAsync("REPORT_CHIP_42", "success", """{"version":"1.1.0","success":true}""");

        h.Device.OtaCompleted.Should().BeFalse("un reporte obsoleto no puede reabrir ni cerrar una campaña");
    }
}
