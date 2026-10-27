using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Ingestion.Commands;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Protocols;
using IoT.Protocols.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace IoT.Tests.Unit;

/// <summary>
/// P1b (ADR-014): la ingesta refleja como atributos CLIENT los campos que el EQUIPO reporta, y SÓLO
/// escribe cuando el valor cambia (no reescribe en cada trama). Se usa un adaptador de protocolo
/// SIMULADO para controlar el evento decodificado y así aislar el cableado del handler.
/// </summary>
public class IngestTelemetryClientAttributesTests
{
    private const string Chip = "ATTR-INGEST-1";

    private static Device NewActiveDevice()
    {
        var device = Device.Create(Guid.NewGuid(), Chip, "AA:BB:CC:DD:EE:10", Chip, "Model", "Mfr").Value;
        device.AssignToCustomer(Guid.NewGuid());
        return device;
    }

    private static NormalizedTelemetryEvent ReportedEvent() => new()
    {
        DeviceIdentifier = Chip,
        RecordedAt = DateTime.UtcNow,
        ReceivedAt = DateTime.UtcNow,
        Attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["firmware_version"] = "v1" },
        NumericMetrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["battery"] = 95 },
    };

    private static (IngestTelemetryCommandHandler handler, Mock<IDeviceAttributeRepository> attr) BuildHandler(
        Device device, IReadOnlyList<DeviceAttribute> existingClient)
    {
        var adapter = new Mock<ICompositeProtocolAdapter>();
        adapter.Setup(a => a.Decode(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Result<NormalizedTelemetryEvent>.Success(ReportedEvent()));

        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByChipIdGlobalAsync(Chip, It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var stateCache = new Mock<IStateCacheService>();
        stateCache.Setup(c => c.IsDuplicateMessageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var attr = new Mock<IDeviceAttributeRepository>();
        attr.Setup(r => r.GetByDeviceAndScopeAsync(device.Id, AttributeScope.Client, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingClient);

        var handler = new IngestTelemetryCommandHandler(
            adapter.Object,
            deviceRepo.Object,
            new Mock<ITenantRepository>().Object,
            stateCache.Object,
            new Mock<ITelemetryStore>().Object,
            new Mock<IUnitOfWork>().Object,
            NullLogger<IngestTelemetryCommandHandler>.Instance,
            attributeRepository: attr.Object);

        return (handler, attr);
    }

    [Fact]
    public async Task Ingest_FirstReport_UpsertsStableMetadata_NotVolatileMetrics()
    {
        var device = NewActiveDevice();
        var (handler, attr) = BuildHandler(device, Array.Empty<DeviceAttribute>());

        var result = await handler.Handle(new IngestTelemetryCommand("telemetry", "{}"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // El metadato estable (firmware_version) SÍ se upserta como atributo client.
        attr.Verify(r => r.UpsertAsync(device.TenantId, device.Id, AttributeScope.Client, "firmware_version", "v1", It.IsAny<CancellationToken>()), Times.Once);
        // La métrica volátil (battery) NO: va a telemetry_records, no al atributo client (ADR-014 D7).
        attr.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), AttributeScope.Client, "battery", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ingest_WhenValuesUnchanged_DoesNotRewrite()
    {
        var device = NewActiveDevice();
        var existing = new List<DeviceAttribute>
        {
            DeviceAttribute.Create(device.TenantId, device.Id, AttributeScope.Client, "firmware_version", "v1").Value,
            DeviceAttribute.Create(device.TenantId, device.Id, AttributeScope.Client, "battery", "95").Value,
        };
        var (handler, attr) = BuildHandler(device, existing);

        var result = await handler.Handle(new IngestTelemetryCommand("telemetry", "{}"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        attr.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<AttributeScope>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ingest_WhenOneValueChanged_UpsertsOnlyThatOne()
    {
        var device = NewActiveDevice();
        var existing = new List<DeviceAttribute>
        {
            DeviceAttribute.Create(device.TenantId, device.Id, AttributeScope.Client, "firmware_version", "v0").Value, // cambió
            DeviceAttribute.Create(device.TenantId, device.Id, AttributeScope.Client, "battery", "95").Value,          // igual
        };
        var (handler, attr) = BuildHandler(device, existing);

        var result = await handler.Handle(new IngestTelemetryCommand("telemetry", "{}"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        attr.Verify(r => r.UpsertAsync(device.TenantId, device.Id, AttributeScope.Client, "firmware_version", "v1", It.IsAny<CancellationToken>()), Times.Once);
        attr.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), AttributeScope.Client, "battery", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
