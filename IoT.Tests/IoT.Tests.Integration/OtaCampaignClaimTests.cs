using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// MEDIUM-6 / LOW-1 / LOW-4: la toma de la campaña OTA tiene que ser atómica (una sola la
/// gana) y no puede haber dos bombas vivas por dispositivo. Estos tests cubren el contrato de
/// servicio (reclamación + dedup + fallo si 0) y el registro de cancelación.
/// </summary>
public sealed class OtaCampaignClaimTests
{
    // ---------------------------------------------------------------------
    // Servicio: la reclamación manda sobre si se arranca o no
    // ---------------------------------------------------------------------

    private static (FirmwareOtaService Service, Mock<IDeviceRepository> Dev, Mock<IFleetNotificationService> Notif, string FullPath)
        ArmarConFirmware(Guid tenantId, Guid deviceId, out Device device)
    {
        device = Device.Create(tenantId, "CLAIM_CHIP", "AA:BB:CC:11:22:44", "Device", "ESP32", "Espressif").Value;

        string relativeSrc = System.IO.Path.Combine(
            FirmwareStorage.RootFolder, FirmwareStorage.FirmwareFolder,
            tenantId.ToString(), "2.0.0", "fw.bin");
        var firmware = FirmwareVersionRecord.Create(tenantId, "2.0.0", relativeSrc, 2048, "sha", 1).Value;
        string fullPath = System.IO.Path.Combine(AppContext.BaseDirectory, relativeSrc);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        System.IO.File.WriteAllBytes(fullPath, new byte[] { 0xE9, 0x01, 0x02, 0x03 });

        var fw = new Mock<IFirmwareVersionRecordRepository>();
        fw.Setup(r => r.GetByVersionAsync(tenantId, "2.0.0", It.IsAny<CancellationToken>())).ReturnsAsync(firmware);
        var dev = new Mock<IDeviceRepository>();
        dev.Setup(r => r.GetByIdAsync(deviceId, It.IsAny<CancellationToken>())).ReturnsAsync(device);
        var notif = new Mock<IFleetNotificationService>();

        var service = new FirmwareOtaService(
            fw.Object, dev.Object, new Mock<IUnitOfWork>().Object, new Mock<IMqttPublisher>().Object,
            notif.Object, new Mock<IServiceScopeFactory>().Object, new Mock<ILogger<FirmwareOtaService>>().Object);

        return (service, dev, notif, fullPath);
    }

    [Fact]
    public async Task SiLaCampanaYaEstabaTomadaNoSeArrancaOtraBomba()
    {
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var (service, dev, notif, fullPath) = ArmarConFirmware(tenantId, deviceId, out _);
        try
        {
            // La reclamación atómica dice que NO se pudo tomar (otra petición la tiene).
            dev.Setup(r => r.TryStartOtaCampaignAsync(
                    tenantId, deviceId, "2.0.0", 1, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            var result = await service.StartDeviceOtaAsync(tenantId, deviceId, "2.0.0");

            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("OTA.AlreadyInProgress");
            // No se notifica un "Starting" fantasma ni se lanza la bomba si no se tomó.
            notif.Verify(n => n.NotifyOtaProgressAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), "Starting", It.IsAny<string?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally { if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath); }
    }

    // ---------------------------------------------------------------------
    // Grupo: deduplica y no miente cuando no arranca ninguno
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ElGrupoDeduplicaDispositivosYFallaSiNingunoArranca()
    {
        var tenantId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var devA = Device.Create(tenantId, "GRP_A", "AA:BB:CC:11:22:51", "A", "ESP32", "x").Value;
        var devB = Device.Create(tenantId, "GRP_B", "AA:BB:CC:11:22:52", "B", "ESP32", "x").Value;

        var dev = new Mock<IDeviceRepository>();
        // devA aparece DOS veces en el grupo: no debe arrancarse dos veces.
        dev.Setup(r => r.GetByGroupIdAsync(tenantId, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { devA, devA, devB });
        dev.Setup(r => r.GetByIdAsync(devA.Id, It.IsAny<CancellationToken>())).ReturnsAsync(devA);
        dev.Setup(r => r.GetByIdAsync(devB.Id, It.IsAny<CancellationToken>())).ReturnsAsync(devB);

        // Firmware no encontrado: cada StartDeviceOtaAsync falla antes de reclamar, así que
        // 0 arrancados. Es la vía más simple para ejercitar "fallar si ninguno arranca".
        var fw = new Mock<IFirmwareVersionRecordRepository>();
        fw.Setup(r => r.GetByVersionAsync(tenantId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FirmwareVersionRecord?)null);

        var service = new FirmwareOtaService(
            fw.Object, dev.Object, new Mock<IUnitOfWork>().Object, new Mock<IMqttPublisher>().Object,
            new Mock<IFleetNotificationService>().Object, new Mock<IServiceScopeFactory>().Object,
            new Mock<ILogger<FirmwareOtaService>>().Object);

        var result = await service.StartGroupOtaAsync(tenantId, groupId, "2.0.0");

        result.IsFailure.Should().BeTrue("ningún dispositivo del grupo pudo arrancar");
        result.Error.Code.Should().Be("Group.NoneStarted");
        // Dedup: devA se procesó UNA sola vez pese a aparecer dos.
        dev.Verify(r => r.GetByIdAsync(devA.Id, It.IsAny<CancellationToken>()), Times.Once);
        dev.Verify(r => r.GetByIdAsync(devB.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---------------------------------------------------------------------
    // Registro de cancelación: como mucho una bomba viva por dispositivo
    // ---------------------------------------------------------------------

    [Fact]
    public void ArrancarUnaCampanaNuevaCancelaLaAnteriorDelMismoDispositivo()
    {
        using var registry = new OtaCampaignRegistry();
        var deviceId = Guid.NewGuid();

        var first = registry.StartCampaign(deviceId, CancellationToken.None);
        first.IsCancellationRequested.Should().BeFalse();

        var second = registry.StartCampaign(deviceId, CancellationToken.None);

        // La primera queda cancelada; la segunda sigue viva.
        first.IsCancellationRequested.Should().BeTrue();
        second.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void CampanasDeDispositivosDistintosNoSeInterfieren()
    {
        using var registry = new OtaCampaignRegistry();
        var a = registry.StartCampaign(Guid.NewGuid(), CancellationToken.None);
        var b = registry.StartCampaign(Guid.NewGuid(), CancellationToken.None);

        a.IsCancellationRequested.Should().BeFalse();
        b.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void EndCampaignConUnTokenViejoNoTocaLaCampanaNueva()
    {
        using var registry = new OtaCampaignRegistry();
        var deviceId = Guid.NewGuid();

        var first = registry.StartCampaign(deviceId, CancellationToken.None);
        var second = registry.StartCampaign(deviceId, CancellationToken.None);

        // La bomba vieja termina y llama a EndCampaign con SU token: no debe retirar la nueva.
        registry.EndCampaign(deviceId, first);

        // Una tercera campaña debe seguir cancelando a la segunda (sigue registrada).
        var third = registry.StartCampaign(deviceId, CancellationToken.None);
        second.IsCancellationRequested.Should().BeTrue();
        third.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void ElApagadoCancelaLasCampanasVivas()
    {
        using var shutdown = new CancellationTokenSource();
        using var registry = new OtaCampaignRegistry();

        var token = registry.StartCampaign(Guid.NewGuid(), shutdown.Token);
        token.IsCancellationRequested.Should().BeFalse();

        shutdown.Cancel();
        token.IsCancellationRequested.Should().BeTrue("el token está ligado al apagado ordenado");
    }
}
