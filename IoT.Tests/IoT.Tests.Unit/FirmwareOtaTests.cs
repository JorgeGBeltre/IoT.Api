using System.Text.Json;
using FluentAssertions;
using IoT.Domain.Entities;
using IoT.Protocols.Adapters;
using IoT.Protocols.Ota;
using Xunit;

namespace IoT.Tests.IoT.Tests.Unit;

public sealed class FirmwareOtaTests
{
    [Fact]
    public void OtaDeviceAdapter_CanHandle_OtaTopicsAndLegacyError()
    {
        var adapter = new OtaDeviceAdapter();

        adapter.CanHandle("ota/ESP32_DEV_01/progress", "{}").Should().BeTrue();
        adapter.CanHandle("ota/ESP32_DEV_01/error", "{}").Should().BeTrue();
        adapter.CanHandle("ota/ESP32_DEV_01/success", "{}").Should().BeTrue();
        adapter.CanHandle("ota/ESP32_DEV_01/state", "{}").Should().BeTrue();
        adapter.CanHandle("ota/ESP32_DEV_01/ack", "{}").Should().BeTrue();
        adapter.CanHandle("devices/ESP32_DEV_01/error-update-firmware-device", "{}").Should().BeTrue();
        adapter.CanHandle("error-update-firmware-device", "{}").Should().BeTrue();
        adapter.CanHandle("some/custom/topic", "{\"EventType\":\"UpdateFirmwareDevice\"}").Should().BeTrue();

        adapter.CanHandle("devices/123/telemetry", "{\"latitude\":18.4}").Should().BeFalse();
    }

    [Fact]
    public void OtaDeviceAdapter_Decode_ProgressPayload_ExtractsMetricsAndAction()
    {
        var adapter = new OtaDeviceAdapter();
        string topic = "ota/ESP32_TRACKER/progress";
        string payload = "{\"device\":\"ESP32_TRACKER\",\"version\":\"1.0.5\",\"progress\":65,\"bytes\":32768}";

        var result = adapter.Decode(topic, payload);

        result.IsSuccess.Should().BeTrue();
        var ev = result.Value;
        ev.DeviceIdentifier.Should().Be("ESP32_TRACKER");
        ev.Attributes["packet_type"].Should().Be("ota_event");
        ev.Attributes["ota_action"].Should().Be("progress");
        ev.Attributes["firmware_version"].Should().Be("1.0.5");
        ev.NumericMetrics["ota_progress"].Should().Be(65);
        ev.NumericMetrics["ota_bytes"].Should().Be(32768);
    }

    [Fact]
    public void OtaDeviceAdapter_Decode_ErrorPayload_ExtractsErrorMessage()
    {
        var adapter = new OtaDeviceAdapter();
        string topic = "ota/ESP32_SENSOR/error";
        string payload = "{\"device\":\"ESP32_SENSOR\",\"version\":\"1.0.5\",\"error\":\"Flash write failed at 0x10000\"}";

        var result = adapter.Decode(topic, payload);

        result.IsSuccess.Should().BeTrue();
        var ev = result.Value;
        ev.DeviceIdentifier.Should().Be("ESP32_SENSOR");
        ev.Attributes["ota_action"].Should().Be("error");
        ev.Attributes["ota_error"].Should().Be("Flash write failed at 0x10000");
    }

    [Fact]
    public void OtaDeviceAdapter_Decode_LegacyBrokerErrorFormat()
    {
        var adapter = new OtaDeviceAdapter();
        string topic = "error-update-firmware-device";
        string payload = "{\"device\":{\"chipId\":\"ESP32_LEGACY\"},\"details\":{\"errorMessage\":\"SHA256 mismatch\"}}";

        var result = adapter.Decode(topic, payload);

        result.IsSuccess.Should().BeTrue();
        var ev = result.Value;
        ev.DeviceIdentifier.Should().Be("ESP32_LEGACY");
        ev.Attributes["ota_action"].Should().Be("error");
        ev.Attributes["ota_error"].Should().Be("SHA256 mismatch");
    }

    [Fact]
    public void FirmwareVersionRecord_Create_ValidatesInputsCorrectly()
    {
        var tenantId = Guid.NewGuid();
        var recordRes = FirmwareVersionRecord.Create(
            tenantId: tenantId,
            firmwareVersion: "2.1.0",
            src: "storage/firmware/test/firmware.bin",
            fileSizeBytes: 1048576,
            sha256: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            totalChunks: 512,
            targetModel: "ESP32-S3",
            featureDescription: "Initial release with BLE provisioning");

        recordRes.IsSuccess.Should().BeTrue();
        var record = recordRes.Value;
        record.TenantId.Should().Be(tenantId);
        record.FirmwareVersion.Should().Be("2.1.0");
        record.TotalChunks.Should().Be(512);
        record.IsActive.Should().BeTrue();
        record.IsDeleted.Should().BeFalse();

        // Soft delete
        record.MarkAsDeleted();
        record.IsDeleted.Should().BeTrue();
        record.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Device_OtaMethods_LifecycleTracking()
    {
        var tenantId = Guid.NewGuid();
        var device = Device.Create(tenantId, "ESP32_OTA_DEV", "AA:BB:CC:DD:EE:FF", "Pump Unit", "ESP32", "Espressif", "1.0.0").Value;

        device.OtaInProgress.Should().BeFalse();
        device.FirmwareVersion.Should().Be("1.0.0");

        // 1. Start OTA
        device.StartOta("1.1.0", totalChunks: 10);
        device.OtaInProgress.Should().BeTrue();
        device.OtaTargetVersion.Should().Be("1.1.0");
        device.OtaTotalChunks.Should().Be(10);
        device.OtaLastChunkSent.Should().Be(0);
        device.OtaCompleted.Should().BeFalse();
        device.OtaErrorMessage.Should().BeNull();

        // 2. Progress
        device.UpdateOtaProgress(5, 10);
        device.OtaLastChunkSent.Should().Be(5);
        device.OtaInProgress.Should().BeTrue();

        // 3. Complete
        device.CompleteOta("1.1.0");
        device.OtaInProgress.Should().BeFalse();
        device.OtaCompleted.Should().BeTrue();
        device.FirmwareVersion.Should().Be("1.1.0");

        // 4. Fail and Reset
        device.StartOta("1.2.0", 15);
        device.FailOta("Checksum verification failed");
        device.OtaInProgress.Should().BeFalse();
        device.OtaCompleted.Should().BeFalse();
        device.OtaErrorMessage.Should().Be("Checksum verification failed");

        device.ResetOta();
        device.OtaErrorMessage.Should().BeNull();
        device.OtaLastChunkSent.Should().Be(0);
    }

    [Fact]
    public void OtaChunker_BuildChunkPayload_ContainsBothSnakeCaseAndPascalCaseKeys()
    {
        byte[] dummyChunk = new byte[64];
        dummyChunk[0] = 0xAA;
        dummyChunk[^1] = 0xBB;

        string payload = OtaChunker.BuildChunkPayload(
            firmwareVersion: "3.0.0",
            partIndex: 1,
            totalParts: 5,
            chunk: dummyChunk,
            sha256Hex: "dummy-sha256",
            timestampUtc: new DateTime(2026, 10, 23, 18, 0, 0, DateTimeKind.Utc));

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        root.GetProperty("EventType").GetString().Should().Be("UpdateFirmwareDevice");

        var details = root.GetProperty("Details");
        // Modern MQTTOTAv5 keys
        details.GetProperty("firmware_version").GetString().Should().Be("3.0.0");
        details.GetProperty("part_index").GetInt32().Should().Be(1);
        details.GetProperty("total_parts").GetInt32().Should().Be(5);
        details.GetProperty("base64").GetString().Should().NotBeNullOrEmpty();
        details.GetProperty("sha256").GetString().Should().Be("dummy-sha256");

        // Legacy / Mqtt-Broker scalar keys
        details.GetProperty("FirmwareVersion").GetString().Should().Be("3.0.0");
        details.GetProperty("PartIndex").GetInt32().Should().Be(1);
        details.GetProperty("TotalParts").GetInt32().Should().Be(5);
        // Note: Base64 is only included once as "base64" (which MQTTOTAv5 and Mqtt-Broker both support)
        // to fit within the ESP32's StaticJsonDocument<4096> pool.
        details.TryGetProperty("Base64Part", out _).Should().BeFalse();
    }
}
