using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// MEDIUM-7: una campaña OTA no puede quedar colgada para siempre. Tres defensas:
/// presencia al arrancar, caducidad de los trozos, y un vigilante que cierra las campañas sin
/// señales. Aquí se cubren la presencia, el vigilante (predicado) y la señal de vida de los
/// reportes del equipo.
/// </summary>
public sealed class OtaWatchdogAndPresenceTests
{
    // -----------------------------------------------------------------
    // Vigilante: predicado de antigüedad
    // -----------------------------------------------------------------

    [Fact]
    public void UnaCampanaSinSenalesMasAllaDelUmbralEstaRancia()
    {
        var now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        OtaCampaignWatchdogService.IsCampaignStale(now.AddMinutes(-20), now, TimeSpan.FromMinutes(15))
            .Should().BeTrue();
    }

    [Fact]
    public void UnaCampanaQueAcabaDeDarSenalesNoEstaRancia()
    {
        var now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        OtaCampaignWatchdogService.IsCampaignStale(now.AddMinutes(-2), now, TimeSpan.FromMinutes(15))
            .Should().BeFalse("una campaña viva, aunque lenta, no debe cerrarse");
    }

    [Fact]
    public void SinMarcaDeTiempoNoSeJuzga()
    {
        var now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        OtaCampaignWatchdogService.IsCampaignStale(null, now, TimeSpan.FromMinutes(15))
            .Should().BeFalse();
    }

    // -----------------------------------------------------------------
    // Señal de vida: un reporte del equipo refresca la actividad
    // -----------------------------------------------------------------

    [Fact]
    public void TouchOtaActivitySoloRefrescaSiHayCampanaEnCurso()
    {
        var device = Device.Create(Guid.NewGuid(), "WD_CHIP", "AA:BB:CC:11:22:61", "D", "ESP32", "x").Value;

        // Sin campaña en curso no hace nada.
        device.TouchOtaActivity();
        device.OtaUpdatedAt.Should().BeNull();

        device.StartOta("1.0.0", 1);
        var afterStart = device.OtaUpdatedAt;
        afterStart.Should().NotBeNull();

        device.TouchOtaActivity();
        device.OtaUpdatedAt.Should().BeOnOrAfter(afterStart!.Value);
        device.OtaInProgress.Should().BeTrue("tocar la actividad no cierra ni cambia la campaña");
    }

    [Theory]
    [InlineData("progress")]
    [InlineData("ack")]      // un ack de trozo es señal de vida, antes se descartaba en silencio
    [InlineData("state")]
    public async Task UnReporteDuranteLaCampanaSePersisteComoSenalDeVida(string accion)
    {
        // Antes: progress sólo difundía (no tocaba la marca) y ack/state caían en un hueco sin
        // rama else y se descartaban EN SILENCIO (ni Update ni Save). Ahora cualquiera de los
        // tres refresca la actividad y se persiste, para que el vigilante no mate una campaña
        // viva. Se verifica el comportamiento observable: persiste y NO cierra la campaña.
        var device = Device.Create(Guid.NewGuid(), "WD_RPT", "AA:BB:CC:11:22:62", "D", "ESP32", "x").Value;
        device.StartOta("1.0.0", 10);

        var dev = new Mock<IDeviceRepository>();
        dev.Setup(r => r.GetByChipIdGlobalAsync("WD_RPT", It.IsAny<CancellationToken>())).ReturnsAsync(device);
        var uow = new Mock<IUnitOfWork>();

        var service = new FirmwareOtaService(
            new Mock<IFirmwareVersionRecordRepository>().Object, dev.Object, uow.Object,
            new Mock<IMqttPublisher>().Object, new Mock<IFleetNotificationService>().Object,
            new Mock<IServiceScopeFactory>().Object, new Mock<ILogger<FirmwareOtaService>>().Object);

        await service.HandleDeviceOtaReportAsync("WD_RPT", accion, "{}");

        device.OtaInProgress.Should().BeTrue("un progreso/ack/estado no cierra la campaña");
        device.OtaUpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        dev.Verify(r => r.Update(device), Times.Once);
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // -----------------------------------------------------------------
    // Presencia: no se emite a un equipo apagado
    // -----------------------------------------------------------------

    private static (FirmwareOtaService Service, Mock<IDeviceRepository> Dev, string FullPath)
        ArmarConFirmwareYMapa(Guid tenantId, Guid deviceId, IMqttClientMap? map, out Device device)
    {
        device = Device.Create(tenantId, "PRES_CHIP", "AA:BB:CC:11:22:63", "D", "ESP32", "x").Value;
        string relativeSrc = System.IO.Path.Combine(
            FirmwareStorage.RootFolder, FirmwareStorage.FirmwareFolder, tenantId.ToString(), "3.0.0", "fw.bin");
        var firmware = FirmwareVersionRecord.Create(tenantId, "3.0.0", relativeSrc, 2048, "sha", 1).Value;
        string fullPath = System.IO.Path.Combine(AppContext.BaseDirectory, relativeSrc);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        System.IO.File.WriteAllBytes(fullPath, new byte[] { 0xE9, 0x01, 0x02, 0x03 });

        var fw = new Mock<IFirmwareVersionRecordRepository>();
        fw.Setup(r => r.GetByVersionAsync(tenantId, "3.0.0", It.IsAny<CancellationToken>())).ReturnsAsync(firmware);
        var dev = new Mock<IDeviceRepository>();
        dev.Setup(r => r.GetByIdAsync(deviceId, It.IsAny<CancellationToken>())).ReturnsAsync(device);
        dev.Setup(r => r.TryStartOtaCampaignAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Firmware:RequireDeviceOnline"] = "true"
        }).Build();

        var service = new FirmwareOtaService(
            fw.Object, dev.Object, new Mock<IUnitOfWork>().Object, new Mock<IMqttPublisher>().Object,
            new Mock<IFleetNotificationService>().Object, new Mock<IServiceScopeFactory>().Object,
            new Mock<ILogger<FirmwareOtaService>>().Object, configuration: config, clientMap: map);

        return (service, dev, fullPath);
    }

    [Fact]
    public async Task NoSeArrancaLaOtaSiElEquipoNoEstaConectado()
    {
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var map = new Mock<IMqttClientMap>();
        map.Setup(m => m.GetClientIdAsync("PRES_CHIP", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null); // no conectado
        var (service, dev, fullPath) = ArmarConFirmwareYMapa(tenantId, deviceId, map.Object, out _);
        try
        {
            var result = await service.StartDeviceOtaAsync(tenantId, deviceId, "3.0.0");

            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("OTA.DeviceOffline");
            // No se llega a reclamar la campaña si el equipo está apagado.
            dev.Verify(r => r.TryStartOtaCampaignAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally { if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath); }
    }

    [Fact]
    public async Task SiElMapaDePresenciaFallaSeArrancaIgual()
    {
        // Degradar ABRIENDO: la disponibilidad del OTA no debe depender de la salud de Redis;
        // el vigilante y la caducidad cubren un equipo que se caiga después.
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var map = new Mock<IMqttClientMap>();
        map.Setup(m => m.GetClientIdAsync("PRES_CHIP", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Redis caído"));
        var (service, dev, fullPath) = ArmarConFirmwareYMapa(tenantId, deviceId, map.Object, out var device);
        try
        {
            var result = await service.StartDeviceOtaAsync(tenantId, deviceId, "3.0.0");

            result.IsSuccess.Should().BeTrue("un fallo de Redis no debe bloquear el OTA");
            dev.Verify(r => r.TryStartOtaCampaignAsync(
                tenantId, device.Id, "3.0.0", 1, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath); }
    }

    [Fact]
    public async Task SiElEquipoEstaConectadoLaOtaArranca()
    {
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var map = new Mock<IMqttClientMap>();
        map.Setup(m => m.GetClientIdAsync("PRES_CHIP", It.IsAny<CancellationToken>()))
            .ReturnsAsync("PRES_CHIP"); // conectado
        var (service, dev, fullPath) = ArmarConFirmwareYMapa(tenantId, deviceId, map.Object, out var device);
        try
        {
            var result = await service.StartDeviceOtaAsync(tenantId, deviceId, "3.0.0");

            result.IsSuccess.Should().BeTrue();
            dev.Verify(r => r.TryStartOtaCampaignAsync(
                tenantId, device.Id, "3.0.0", 1, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath); }
    }
}
