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
/// MEDIUM-16: cuatro métodos públicos del servicio OTA no tenían NINGÚN test. StartGroupOtaAsync
/// ya quedó cubierto al arreglar MEDIUM-6 (dedup + fallo si 0). Aquí se cubren los otros tres:
/// RollbackDeviceOtaAsync (la vía de emergencia), GetFirmwareRecordsAsync y GetDeviceOtaStatusAsync.
/// </summary>
public sealed class FirmwareOtaQueryAndRollbackTests
{
    private static FirmwareOtaService Build(
        Mock<IFirmwareVersionRecordRepository> fw,
        Mock<IDeviceRepository> dev,
        Mock<IUnitOfWork>? uow = null) =>
        new(fw.Object, dev.Object, (uow ?? new Mock<IUnitOfWork>()).Object,
            new Mock<IMqttPublisher>().Object, new Mock<IFleetNotificationService>().Object,
            new Mock<IServiceScopeFactory>().Object, new Mock<ILogger<FirmwareOtaService>>().Object);

    // -----------------------------------------------------------------
    // GetFirmwareRecordsAsync
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetFirmwareRecords_ReturnsTheTenantRecordsAsDtos()
    {
        var tenantId = Guid.NewGuid();
        var r1 = FirmwareVersionRecord.Create(tenantId, "1.0.0", "storage/firmware/x/1.0.0/a.bin", 10, "sha1", 1).Value;
        var r2 = FirmwareVersionRecord.Create(tenantId, "1.1.0", "storage/firmware/x/1.1.0/b.bin", 20, "sha2", 1).Value;

        var fw = new Mock<IFirmwareVersionRecordRepository>();
        fw.Setup(r => r.GetRecordsAsync(tenantId, null, 0, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { r2, r1 });

        var result = await Build(fw, new Mock<IDeviceRepository>()).GetFirmwareRecordsAsync(tenantId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value.Select(d => d.FirmwareVersion).Should().BeEquivalentTo(new[] { "1.0.0", "1.1.0" });
    }

    // -----------------------------------------------------------------
    // GetDeviceOtaStatusAsync
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetDeviceOtaStatus_ReportsProgressPercentage()
    {
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "STATUS_CHIP", "AA:BB:CC:11:22:B0", "D", "ESP32", "x").Value;
        device.StartOta("2.0.0", 5);
        device.UpdateOtaProgress(3, 5); // 3 de 5 trozos

        var dev = new Mock<IDeviceRepository>();
        dev.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var result = await Build(new Mock<IFirmwareVersionRecordRepository>(), dev)
            .GetDeviceOtaStatusAsync(tenantId, device.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.OtaInProgress.Should().BeTrue();
        result.Value.OtaTargetVersion.Should().Be("2.0.0");
        result.Value.Percentage.Should().Be(60);
        result.Value.ChipId.Should().Be("STATUS_CHIP");
    }

    [Fact]
    public async Task GetDeviceOtaStatus_DoesNotLeakAcrossTenants()
    {
        var ownerTenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var device = Device.Create(ownerTenant, "X", "AA:BB:CC:11:22:B1", "D", "ESP32", "x").Value;

        var dev = new Mock<IDeviceRepository>();
        dev.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var result = await Build(new Mock<IFirmwareVersionRecordRepository>(), dev)
            .GetDeviceOtaStatusAsync(otherTenant, device.Id);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Device.NotFound");
    }

    // -----------------------------------------------------------------
    // RollbackDeviceOtaAsync
    // -----------------------------------------------------------------

    [Fact]
    public async Task RollbackWithoutVersion_FailsWhenThereIsNoOtherVersion()
    {
        // El equipo está en 1.0.0 y es la ÚNICA versión registrada: no hay a dónde volver.
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "RB1", "AA:BB:CC:11:22:B2", "D", "ESP32", "x", "1.0.0").Value;

        var dev = new Mock<IDeviceRepository>();
        dev.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);
        var fw = new Mock<IFirmwareVersionRecordRepository>();
        fw.Setup(r => r.GetRecordsAsync(tenantId, null, 0, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { FirmwareVersionRecord.Create(tenantId, "1.0.0", "storage/firmware/x/1.0.0/a.bin", 10, "s", 1).Value });

        var result = await Build(fw, dev).RollbackDeviceOtaAsync(tenantId, device.Id);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Rollback.NoPreviousVersion");
    }

    [Fact]
    public async Task RollbackWithAForeignRecordIdDoesNotLeakAndFails()
    {
        // Pasar el Id de un registro de OTRO tenant no debe revelar su existencia ni usarse.
        var tenantId = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var device = Device.Create(tenantId, "RB2", "AA:BB:CC:11:22:B3", "D", "ESP32", "x").Value;
        var foreignRecord = FirmwareVersionRecord.Create(otherTenant, "9.9.9", "storage/firmware/y/9.9.9/c.bin", 10, "s", 1).Value;

        var dev = new Mock<IDeviceRepository>();
        dev.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);
        var fw = new Mock<IFirmwareVersionRecordRepository>();
        fw.Setup(r => r.GetByIdAsync(foreignRecord.Id, It.IsAny<CancellationToken>())).ReturnsAsync(foreignRecord);

        var result = await Build(fw, dev).RollbackDeviceOtaAsync(tenantId, device.Id, firmwareRecordId: foreignRecord.Id);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Rollback.TargetNotFound");
    }

    [Fact]
    public async Task RollbackWithoutVersion_SelectsThePreviousVersionNotTheCurrentOne()
    {
        // El equipo está en 1.5.0; registros [1.5.0, 1.4.0]. El rollback debe elegir 1.4.0
        // (la anterior), NO re-desplegar 1.5.0 (el bug que arregló la ola 2). Se comprueba por
        // la versión con la que delega en StartDeviceOtaAsync: GetByVersionAsync("1.4.0").
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "RB3", "AA:BB:CC:11:22:B4", "D", "ESP32", "x", "1.5.0").Value;

        var dev = new Mock<IDeviceRepository>();
        dev.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);
        var fw = new Mock<IFirmwareVersionRecordRepository>();
        fw.Setup(r => r.GetRecordsAsync(tenantId, null, 0, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                FirmwareVersionRecord.Create(tenantId, "1.5.0", "storage/firmware/x/1.5.0/e.bin", 10, "s", 1).Value,
                FirmwareVersionRecord.Create(tenantId, "1.4.0", "storage/firmware/x/1.4.0/d.bin", 10, "s", 1).Value
            });
        // La delegación en StartDeviceOtaAsync busca la versión elegida; devolverla null hace
        // que termine ahí, pero deja probada la SELECCIÓN.
        fw.Setup(r => r.GetByVersionAsync(tenantId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FirmwareVersionRecord?)null);

        await Build(fw, dev).RollbackDeviceOtaAsync(tenantId, device.Id);

        fw.Verify(r => r.GetByVersionAsync(tenantId, "1.4.0", It.IsAny<CancellationToken>()), Times.Once);
        fw.Verify(r => r.GetByVersionAsync(tenantId, "1.5.0", It.IsAny<CancellationToken>()), Times.Never);
    }
}
