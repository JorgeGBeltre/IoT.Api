using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// MEDIUM-14: no existía ninguna vía para borrar una versión de firmware (el almacén sólo
/// crecía) ni para cancelar una campaña en curso sin esperar la ventana de rescate. Aquí se
/// cubren el borrado (con su guard de "versión en uso" y el desenlace del binario) y la
/// cancelación.
/// </summary>
public sealed class FirmwareVersionDeleteAndCancelTests
{
    private static string WriteFirmwareFile(Guid tenantId, string version, out string relativeSrc)
    {
        relativeSrc = System.IO.Path.Combine(
            FirmwareStorage.RootFolder, FirmwareStorage.FirmwareFolder, tenantId.ToString(), version, "fw.bin");
        string fullPath = System.IO.Path.Combine(AppContext.BaseDirectory, relativeSrc);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        System.IO.File.WriteAllBytes(fullPath, new byte[] { 0xE9, 0x01, 0x02, 0x03 });
        return fullPath;
    }

    private static FirmwareOtaService Service(
        Mock<IFirmwareVersionRecordRepository> fw,
        Mock<IDeviceRepository> dev,
        Mock<IUnitOfWork> uow) =>
        new(fw.Object, dev.Object, uow.Object, new Mock<IMqttPublisher>().Object,
            new Mock<IFleetNotificationService>().Object, new Mock<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>().Object,
            new Mock<ILogger<FirmwareOtaService>>().Object);

    // -----------------------------------------------------------------
    // Borrado de versión
    // -----------------------------------------------------------------

    [Fact]
    public async Task DeletingAVersionUnlinksTheBinaryAndMarksTheRecord()
    {
        var tenantId = Guid.NewGuid();
        string fullPath = WriteFirmwareFile(tenantId, "1.0.0", out string relativeSrc);
        try
        {
            var record = FirmwareVersionRecord.Create(tenantId, "1.0.0", relativeSrc, 4, "sha", 1).Value;

            var fw = new Mock<IFirmwareVersionRecordRepository>();
            fw.Setup(r => r.GetByVersionAsync(tenantId, "1.0.0", It.IsAny<CancellationToken>())).ReturnsAsync(record);
            var dev = new Mock<IDeviceRepository>();
            dev.Setup(r => r.GetDevicesWithOtaInProgressAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<Device>());
            var uow = new Mock<IUnitOfWork>();

            var result = await Service(fw, dev, uow).DeleteFirmwareVersionAsync(tenantId, "1.0.0", "admin");

            result.IsSuccess.Should().BeTrue();
            System.IO.File.Exists(fullPath).Should().BeFalse("el binario se desenlaza del disco");
            fw.Verify(r => r.Remove(record), Times.Once);
            uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath); }
    }

    [Fact]
    public async Task CannotDeleteAVersionWhileItIsBeingRolledOut()
    {
        var tenantId = Guid.NewGuid();
        string fullPath = WriteFirmwareFile(tenantId, "2.0.0", out string relativeSrc);
        try
        {
            var record = FirmwareVersionRecord.Create(tenantId, "2.0.0", relativeSrc, 4, "sha", 1).Value;
            // Un equipo del tenant está actualizándose a ESA versión.
            var busy = Device.Create(tenantId, "BUSY", "AA:BB:CC:11:22:A0", "D", "ESP32", "x").Value;
            busy.StartOta("2.0.0", 1);

            var fw = new Mock<IFirmwareVersionRecordRepository>();
            fw.Setup(r => r.GetByVersionAsync(tenantId, "2.0.0", It.IsAny<CancellationToken>())).ReturnsAsync(record);
            var dev = new Mock<IDeviceRepository>();
            dev.Setup(r => r.GetDevicesWithOtaInProgressAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { busy });
            var uow = new Mock<IUnitOfWork>();

            var result = await Service(fw, dev, uow).DeleteFirmwareVersionAsync(tenantId, "2.0.0", "admin");

            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("Firmware.VersionInUse");
            // No se le quita el binario a un rollout vivo, ni se marca el registro.
            System.IO.File.Exists(fullPath).Should().BeTrue();
            fw.Verify(r => r.Remove(It.IsAny<FirmwareVersionRecord>()), Times.Never);
            uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }
        finally { if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath); }
    }

    [Fact]
    public async Task ACampaignOfAnotherTenantDoesNotBlockDeletion()
    {
        // El guard de "en uso" es POR TENANT: un equipo de otro tenant actualizándose a una
        // versión del mismo número no debe impedir borrar la de ESTE tenant.
        var tenantId = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        string fullPath = WriteFirmwareFile(tenantId, "3.0.0", out string relativeSrc);
        try
        {
            var record = FirmwareVersionRecord.Create(tenantId, "3.0.0", relativeSrc, 4, "sha", 1).Value;
            var otherBusy = Device.Create(otherTenant, "OTHER", "AA:BB:CC:11:22:A1", "D", "ESP32", "x").Value;
            otherBusy.StartOta("3.0.0", 1);

            var fw = new Mock<IFirmwareVersionRecordRepository>();
            fw.Setup(r => r.GetByVersionAsync(tenantId, "3.0.0", It.IsAny<CancellationToken>())).ReturnsAsync(record);
            var dev = new Mock<IDeviceRepository>();
            dev.Setup(r => r.GetDevicesWithOtaInProgressAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { otherBusy });
            var uow = new Mock<IUnitOfWork>();

            var result = await Service(fw, dev, uow).DeleteFirmwareVersionAsync(tenantId, "3.0.0", "admin");

            result.IsSuccess.Should().BeTrue();
            fw.Verify(r => r.Remove(record), Times.Once);
        }
        finally { if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath); }
    }

    [Fact]
    public async Task DeletingAnUnknownVersionFails()
    {
        var tenantId = Guid.NewGuid();
        var fw = new Mock<IFirmwareVersionRecordRepository>();
        fw.Setup(r => r.GetByVersionAsync(tenantId, "9.9.9", It.IsAny<CancellationToken>()))
            .ReturnsAsync((FirmwareVersionRecord?)null);

        var result = await Service(fw, new Mock<IDeviceRepository>(), new Mock<IUnitOfWork>())
            .DeleteFirmwareVersionAsync(tenantId, "9.9.9", "admin");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Firmware.NotFound");
    }

    // -----------------------------------------------------------------
    // Cancelación de una campaña
    // -----------------------------------------------------------------

    [Fact]
    public async Task CancellingAnInProgressCampaignTakesTheDeviceOutOfUpdating()
    {
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "CANCEL", "AA:BB:CC:11:22:A2", "D", "ESP32", "x").Value;
        device.StartOta("1.0.0", 5);
        device.OtaInProgress.Should().BeTrue();

        var dev = new Mock<IDeviceRepository>();
        dev.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);
        var uow = new Mock<IUnitOfWork>();

        var result = await Service(new Mock<IFirmwareVersionRecordRepository>(), dev, uow)
            .CancelDeviceOtaAsync(tenantId, device.Id, "admin");

        result.IsSuccess.Should().BeTrue();
        device.OtaInProgress.Should().BeFalse("cancelar saca al equipo de 'actualizando'");
        dev.Verify(r => r.Update(device), Times.Once);
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancellingWhenThereIsNoCampaignIsIdempotent()
    {
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "IDLE", "AA:BB:CC:11:22:A3", "D", "ESP32", "x").Value;
        device.OtaInProgress.Should().BeFalse();

        var dev = new Mock<IDeviceRepository>();
        dev.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);
        var uow = new Mock<IUnitOfWork>();

        var result = await Service(new Mock<IFirmwareVersionRecordRepository>(), dev, uow)
            .CancelDeviceOtaAsync(tenantId, device.Id, "admin");

        result.IsSuccess.Should().BeTrue("cancelar algo que no corre no es un error");
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
