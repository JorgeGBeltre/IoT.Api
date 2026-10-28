using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Commands;
using IoT.Application.Devices.Queries;
using IoT.Application.Ingestion;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Protocols.Contracts;
using Moq;
using Xunit;

namespace IoT.Tests.Unit;

/// <summary>
/// P1b (ADR-014): la API de atributos de dispositivo y el mapeo de atributos CLIENT en la ingesta.
/// </summary>
public class DeviceAttributesApplicationTests
{
    private static readonly Guid Tenant = Guid.Parse("c1111111-1111-1111-1111-111111111111");

    private static Device NewDevice(Guid tenantId, string chip = "ATTR-UNIT-1")
    {
        var device = Device.Create(tenantId, chip, "AA:BB:CC:DD:EE:01", chip, "Model", "Mfr").Value;
        device.AssignToCustomer(Guid.NewGuid());
        return device;
    }

    private static (Mock<IDeviceRepository> dev, Mock<IDeviceAttributeRepository> attr, Mock<ITenantContext> tenant, Mock<IUnitOfWork> uow) Mocks(Guid tenantId)
    {
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(tenantId);
        return (new Mock<IDeviceRepository>(), new Mock<IDeviceAttributeRepository>(), tenant, new Mock<IUnitOfWork>());
    }

    // ---- SetDeviceAttributesCommand ---------------------------------------------------------------

    [Fact]
    public async Task Set_WithClientScope_IsRejectedAsReadOnly_AndNeverTouchesRepository()
    {
        var (dev, attr, tenant, uow) = Mocks(Tenant);
        var handler = new SetDeviceAttributesCommandHandler(dev.Object, attr.Object, tenant.Object, uow.Object);

        var cmd = new SetDeviceAttributesCommand(
            Guid.NewGuid(), AttributeScope.Client,
            new Dictionary<string, string?> { ["battery"] = "95" });

        var result = await handler.Handle(cmd, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Attributes.ClientReadOnly");
        dev.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        attr.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<AttributeScope>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(AttributeScope.Server)]
    [InlineData(AttributeScope.Shared)]
    public async Task Set_WithServerOrShared_UpsertsEachPair_AndSavesOnce(AttributeScope scope)
    {
        var (dev, attr, tenant, uow) = Mocks(Tenant);
        var device = NewDevice(Tenant);
        dev.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var handler = new SetDeviceAttributesCommandHandler(dev.Object, attr.Object, tenant.Object, uow.Object);
        var cmd = new SetDeviceAttributesCommand(device.Id, scope, new Dictionary<string, string?>
        {
            ["note"] = "revision anual",
            ["policy"] = "P-42"
        });

        var result = await handler.Handle(cmd, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // El TenantId sellado es el del DEVICE resuelto, no entrada del cliente.
        attr.Verify(r => r.UpsertAsync(device.TenantId, device.Id, scope, "note", "revision anual", It.IsAny<CancellationToken>()), Times.Once);
        attr.Verify(r => r.UpsertAsync(device.TenantId, device.Id, scope, "policy", "P-42", It.IsAny<CancellationToken>()), Times.Once);
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Set_OnDeviceNotVisible_ReturnsNotFound_WithoutWriting()
    {
        var (dev, attr, tenant, uow) = Mocks(Tenant);
        dev.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Device?)null);

        var handler = new SetDeviceAttributesCommandHandler(dev.Object, attr.Object, tenant.Object, uow.Object);
        var cmd = new SetDeviceAttributesCommand(Guid.NewGuid(), AttributeScope.Server, new Dictionary<string, string?> { ["note"] = "x" });

        var result = await handler.Handle(cmd, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Device.NotFound");
        attr.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<AttributeScope>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Set_OnDeviceOfAnotherTenant_ReturnsNotFound()
    {
        var (dev, attr, tenant, uow) = Mocks(Tenant);
        var foreign = NewDevice(Guid.NewGuid()); // otro tenant
        dev.Setup(r => r.GetByIdAsync(foreign.Id, It.IsAny<CancellationToken>())).ReturnsAsync(foreign);

        var handler = new SetDeviceAttributesCommandHandler(dev.Object, attr.Object, tenant.Object, uow.Object);
        var cmd = new SetDeviceAttributesCommand(foreign.Id, AttributeScope.Server, new Dictionary<string, string?> { ["note"] = "x" });

        var result = await handler.Handle(cmd, CancellationToken.None);

        result.Error.Code.Should().Be("Device.NotFound");
    }

    [Fact]
    public async Task Set_WithNoAttributes_IsRejected()
    {
        var (dev, attr, tenant, uow) = Mocks(Tenant);
        var handler = new SetDeviceAttributesCommandHandler(dev.Object, attr.Object, tenant.Object, uow.Object);

        var result = await handler.Handle(
            new SetDeviceAttributesCommand(Guid.NewGuid(), AttributeScope.Server, new Dictionary<string, string?>()),
            CancellationToken.None);

        result.Error.Code.Should().Be("Attributes.NoAttributes");
    }

    // ---- GetDeviceAttributesQuery -----------------------------------------------------------------

    [Fact]
    public async Task Get_GroupsAttributesByScope()
    {
        var dev = new Mock<IDeviceRepository>();
        var attr = new Mock<IDeviceAttributeRepository>();
        var device = NewDevice(Tenant);
        dev.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var rows = new List<DeviceAttribute>
        {
            DeviceAttribute.Create(Tenant, device.Id, AttributeScope.Client, "battery", "95").Value,
            DeviceAttribute.Create(Tenant, device.Id, AttributeScope.Client, "firmware_version", "v1").Value,
            DeviceAttribute.Create(Tenant, device.Id, AttributeScope.Shared, "reportIntervalSec", "60").Value,
            DeviceAttribute.Create(Tenant, device.Id, AttributeScope.Server, "note", "hola").Value,
        };
        attr.Setup(r => r.ListByDeviceAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(rows);

        var handler = new GetDeviceAttributesQueryHandler(dev.Object, attr.Object);
        var result = await handler.Handle(new GetDeviceAttributesQuery(Tenant, device.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var dto = result.Value;
        dto.DeviceId.Should().Be(device.Id);
        dto.Client.Should().HaveCount(2);
        dto.Client.Select(c => c.Key).Should().ContainInOrder("battery", "firmware_version");
        dto.Shared.Should().ContainSingle().Which.Key.Should().Be("reportIntervalSec");
        dto.Server.Should().ContainSingle().Which.Value.Should().Be("hola");
    }

    [Fact]
    public async Task Get_OnDeviceNotVisible_ReturnsNotFound()
    {
        var dev = new Mock<IDeviceRepository>();
        var attr = new Mock<IDeviceAttributeRepository>();
        dev.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Device?)null);

        var handler = new GetDeviceAttributesQueryHandler(dev.Object, attr.Object);
        var result = await handler.Handle(new GetDeviceAttributesQuery(Tenant, Guid.NewGuid()), CancellationToken.None);

        result.Error.Code.Should().Be("Device.NotFound");
        attr.Verify(r => r.ListByDeviceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- DeleteDeviceAttributeCommand -------------------------------------------------------------

    [Fact]
    public async Task Delete_WithClientScope_IsRejected()
    {
        var (dev, attr, tenant, uow) = Mocks(Tenant);
        var handler = new DeleteDeviceAttributeCommandHandler(dev.Object, attr.Object, tenant.Object, uow.Object);

        var result = await handler.Handle(new DeleteDeviceAttributeCommand(Guid.NewGuid(), AttributeScope.Client, "battery"), CancellationToken.None);

        result.Error.Code.Should().Be("Attributes.ClientReadOnly");
        attr.Verify(r => r.DeleteAsync(It.IsAny<Guid>(), It.IsAny<AttributeScope>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_ExistingServerAttribute_SavesAndSucceeds()
    {
        var (dev, attr, tenant, uow) = Mocks(Tenant);
        var device = NewDevice(Tenant);
        dev.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);
        attr.Setup(r => r.DeleteAsync(device.Id, AttributeScope.Server, "note", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var handler = new DeleteDeviceAttributeCommandHandler(dev.Object, attr.Object, tenant.Object, uow.Object);
        var result = await handler.Handle(new DeleteDeviceAttributeCommand(device.Id, AttributeScope.Server, "note"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_NonExistingAttribute_ReturnsNotFound_WithoutSaving()
    {
        var (dev, attr, tenant, uow) = Mocks(Tenant);
        var device = NewDevice(Tenant);
        dev.Setup(r => r.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);
        attr.Setup(r => r.DeleteAsync(device.Id, AttributeScope.Server, "nope", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var handler = new DeleteDeviceAttributeCommandHandler(dev.Object, attr.Object, tenant.Object, uow.Object);
        var result = await handler.Handle(new DeleteDeviceAttributeCommand(device.Id, AttributeScope.Server, "nope"), CancellationToken.None);

        result.Error.Code.Should().Be("Attributes.NotFound");
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- DeviceClientAttributeMapping (ingesta CLIENT) --------------------------------------------

    [Fact]
    public void Mapping_Extract_MapsOnlyStableStringMetadata_ExcludesVolatileNumericMetrics()
    {
        var ev = new NormalizedTelemetryEvent
        {
            DeviceIdentifier = "CHIP-1",
            Attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["firmware_version"] = "v2.4.1",
                ["hardware_version"] = "HW-rev3",
                ["serial_number"] = "SN-2026-9901",
                ["protocol_version"] = "1.2",
                ["imei"] = "860000000000001",
                ["iccid"] = "8934071100000000001",
                ["imsi"] = "310150123456789",
                ["mac_address"] = "AA:BB:CC",   // NO es atributo client: identidad de transporte
                ["impact_detected"] = "True"     // NO es atributo client: evento de telemetría
            },
            NumericMetrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["battery"] = 95.5,              // VOLÁTIL: va a telemetry_records, NO a atributo client
                ["rssi"] = -67,                  // VOLÁTIL: idem
                ["temperature"] = 28.5,          // NO es atributo client: serie ambiental
                ["speed"] = 60                    // NO es atributo client
            }
        };

        var mapped = DeviceClientAttributeMapping.Extract(ev);

        // Sólo metadatos ESTABLES de identidad/firmware (cadenas). Ninguna métrica numérica volátil.
        mapped.Keys.Should().BeEquivalentTo(new[] { "firmware_version", "hardware_version", "serial_number", "protocol_version", "imei", "iccid", "imsi" });
        mapped["firmware_version"].Should().Be("v2.4.1");
        mapped["hardware_version"].Should().Be("HW-rev3");
        mapped["serial_number"].Should().Be("SN-2026-9901");
        mapped["protocol_version"].Should().Be("1.2");
        mapped.Should().NotContainKey("battery", "battery es volátil: serie en telemetry_records, no atributo client");
        mapped.Should().NotContainKey("rssi", "rssi es volátil: serie en telemetry_records, no atributo client");
    }

    [Fact]
    public void Mapping_ComputeChanges_OnlyReturnsMissingOrChanged()
    {
        var reported = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["firmware_version"] = "v2",   // cambia
            ["battery"] = "95",            // igual -> se omite
            ["rssi"] = "-70"               // nuevo
        };
        var current = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["firmware_version"] = "v1",
            ["battery"] = "95"
        };

        var changes = DeviceClientAttributeMapping.ComputeChanges(reported, current);

        changes.Select(c => c.Key).Should().BeEquivalentTo(new[] { "firmware_version", "rssi" });
        changes.Should().NotContain(c => c.Key == "battery");
    }

    [Fact]
    public void Mapping_ComputeChanges_WhenNothingChanged_ReturnsEmpty()
    {
        var reported = new Dictionary<string, string>(StringComparer.Ordinal) { ["battery"] = "95", ["rssi"] = "-70" };
        var current = new Dictionary<string, string>(StringComparer.Ordinal) { ["battery"] = "95", ["rssi"] = "-70" };

        DeviceClientAttributeMapping.ComputeChanges(reported, current).Should().BeEmpty();
    }
}
