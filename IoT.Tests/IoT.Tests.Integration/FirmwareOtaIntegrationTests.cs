using System.Text;
using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

public sealed class FirmwareOtaIntegrationTests
{
    [Fact]
    public async Task FirmwareOtaService_UploadFirmware_SavesRecordWithCalculatedChunks()
    {
        var tenantId = Guid.NewGuid();
        var fwRepoMock = new Mock<IFirmwareVersionRecordRepository>();
        var devRepoMock = new Mock<IDeviceRepository>();
        var uowMock = new Mock<IUnitOfWork>();
        var mqttMock = new Mock<IMqttPublisher>();
        var notifMock = new Mock<IFleetNotificationService>();
        var scopeMock = new Mock<IServiceScopeFactory>();
        var loggerMock = new Mock<ILogger<FirmwareOtaService>>();

        FirmwareVersionRecord? savedRecord = null;
        fwRepoMock.Setup(r => r.AddAsync(It.IsAny<FirmwareVersionRecord>(), It.IsAny<CancellationToken>()))
            .Callback<FirmwareVersionRecord, CancellationToken>((r, _) => savedRecord = r)
            .Returns(Task.CompletedTask);

        var service = new FirmwareOtaService(
            fwRepoMock.Object,
            devRepoMock.Object,
            uowMock.Object,
            mqttMock.Object,
            notifMock.Object,
            scopeMock.Object,
            loggerMock.Object);

        // Dummy firmware file: 4096 bytes (2 chunks of 2048) starting with 0xE9
        byte[] fwData = new byte[4096];
        fwData[0] = 0xE9; // ESP32 magic byte
        using var stream = new MemoryStream(fwData);

        var result = await service.UploadFirmwareAsync(
            tenantId: tenantId,
            fileStream: stream,
            fileName: "esp32_firmware_v1.0.bin",
            version: "1.0.0",
            targetModel: "ESP32",
            featureDescription: "Initial release");

        result.IsSuccess.Should().BeTrue();
        result.Value.FirmwareVersion.Should().Be("1.0.0");
        result.Value.TotalChunks.Should().Be(2);
        result.Value.FileSizeBytes.Should().Be(4096);
        result.Value.Sha256.Should().NotBeNullOrEmpty();

        savedRecord.Should().NotBeNull();
        savedRecord!.TotalChunks.Should().Be(2);
        uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FirmwareOtaService_StartDeviceOta_SetsDeviceStateAndNotifies()
    {
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var device = Device.Create(tenantId, "TEST_OTA_CHIP", "AA:BB:CC:11:22:33", "Device 1", "ESP32", "Espressif").Value;

        // El firmware debe vivir DENTRO del almacén del tenant: storage/firmware/{tenant}/...
        // Antes este fixture lo dejaba como "firmware.bin" en la raíz de la aplicación, que
        // es precisamente lo que la contención de rutas rechaza (una ruta guardada fuera del
        // almacén podía apuntar a cualquier archivo del servidor y acabar publicada al
        // dispositivo). Se usa la misma estructura que genera la subida real.
        string relativeSrc = Path.Combine(
            FirmwareStorage.RootFolder, FirmwareStorage.FirmwareFolder,
            tenantId.ToString(), "1.2.0", "firmware_test.bin");

        var firmware = FirmwareVersionRecord.Create(
            tenantId: tenantId,
            firmwareVersion: "1.2.0",
            src: relativeSrc,
            fileSizeBytes: 2048,
            sha256: "dummy-sha256",
            totalChunks: 1).Value;

        string fullPath = Path.Combine(AppContext.BaseDirectory, relativeSrc);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, new byte[] { 0xE9, 0x01, 0x02, 0x03 });

        try
        {
            var fwRepoMock = new Mock<IFirmwareVersionRecordRepository>();
            var devRepoMock = new Mock<IDeviceRepository>();
            var uowMock = new Mock<IUnitOfWork>();
            var mqttMock = new Mock<IMqttPublisher>();
            var notifMock = new Mock<IFleetNotificationService>();
            var scopeMock = new Mock<IServiceScopeFactory>();
            var loggerMock = new Mock<ILogger<FirmwareOtaService>>();

            devRepoMock.Setup(r => r.GetByIdAsync(deviceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(device);
            fwRepoMock.Setup(r => r.GetByVersionAsync(tenantId, "1.2.0", It.IsAny<CancellationToken>()))
                .ReturnsAsync(firmware);
            // La toma de la campaña es ahora una escritura condicional ATÓMICA en el
            // repositorio (no muta el objeto Device en memoria). Devolver true = la campaña se
            // tomó. Antes el test afirmaba device.OtaInProgress sobre la instancia en memoria,
            // que el servicio mutaba con StartOta; eso ya no ocurre, así que se verifica el
            // contrato del claim en su lugar.
            // El servicio reclama por device.Id (la identidad de la entidad resuelta), que en
            // este fixture NO es el mismo Guid que deviceId.
            devRepoMock.Setup(r => r.TryStartOtaCampaignAsync(
                    tenantId, device.Id, "1.2.0", 1, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var service = new FirmwareOtaService(
                fwRepoMock.Object,
                devRepoMock.Object,
                uowMock.Object,
                mqttMock.Object,
                notifMock.Object,
                scopeMock.Object,
                loggerMock.Object);

            var startResult = await service.StartDeviceOtaAsync(tenantId, deviceId, "1.2.0");

            startResult.IsSuccess.Should().BeTrue();
            // El estado se tomó de forma atómica, con la versión y el nº de trozos correctos.
            devRepoMock.Verify(r => r.TryStartOtaCampaignAsync(
                tenantId, device.Id, "1.2.0", 1, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);

            notifMock.Verify(n => n.NotifyOtaProgressAsync(
                tenantId,
                device.Id,
                device.ChipId,
                "1.2.0",
                0,
                1,
                0,
                "Starting",
                null,
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (File.Exists(fullPath)) File.Delete(fullPath);
        }
    }

    [Fact]
    public async Task FirmwareOtaService_HandleDeviceOtaReport_ProcessesErrorAndSuccess()
    {
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "REPORT_CHIP_01", "11:22:33:44:55:66", "Test Device", "ESP32", "Espressif", "1.0.0").Value;
        device.StartOta("1.1.0", 5);

        var fwRepoMock = new Mock<IFirmwareVersionRecordRepository>();
        var devRepoMock = new Mock<IDeviceRepository>();
        var uowMock = new Mock<IUnitOfWork>();
        var mqttMock = new Mock<IMqttPublisher>();
        var notifMock = new Mock<IFleetNotificationService>();
        var scopeMock = new Mock<IServiceScopeFactory>();
        var loggerMock = new Mock<ILogger<FirmwareOtaService>>();

        devRepoMock.Setup(r => r.GetByChipIdGlobalAsync("REPORT_CHIP_01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(device);

        var service = new FirmwareOtaService(
            fwRepoMock.Object,
            devRepoMock.Object,
            uowMock.Object,
            mqttMock.Object,
            notifMock.Object,
            scopeMock.Object,
            loggerMock.Object);

        // 1. Report Error
        string errorPayload = "{\"error\":\"Flash write timeout\",\"device\":\"REPORT_CHIP_01\"}";
        await service.HandleDeviceOtaReportAsync("REPORT_CHIP_01", "error", errorPayload);

        device.OtaInProgress.Should().BeFalse();
        device.OtaCompleted.Should().BeFalse();
        device.OtaErrorMessage.Should().Be("Flash write timeout");

        // 2. Report Success. Hay que RELANZAR la campaña: el reporte de error la cerró, y un
        // "success" que llega sin OTA en curso es un reporte obsoleto (o forjado) que el
        // servicio descarta a propósito para que un equipo no pueda marcar como completada
        // una actualización que nadie lanzó.
        device.StartOta("1.1.0", 5);

        string successPayload = "{\"version\":\"1.1.0\",\"success\":true}";
        await service.HandleDeviceOtaReportAsync("REPORT_CHIP_01", "success", successPayload);

        device.OtaInProgress.Should().BeFalse();
        device.OtaCompleted.Should().BeTrue();
        device.FirmwareVersion.Should().Be("1.1.0");
        device.OtaErrorMessage.Should().BeNull();
    }
}
