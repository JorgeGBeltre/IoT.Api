using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Commands;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using Moq;
using Xunit;

namespace IoT.Tests.IoT.Tests.Unit;

/// <summary>
/// Aprovisionamiento en un solo paso (emparejamiento rápido). Verifica que compone el alta
/// pre-registro + acuñación de ApiKey por-dispositivo + binding a Customer y devuelve el paquete
/// que la app inyecta (clientId = ChipId, cuenta = ApiKey en texto plano UNA vez, broker). El
/// contrato que el broker valida de verdad: ClientId = ChipId y la ApiKey en usuario/clave; por
/// eso MqttClientId = ChipId y MqttUsername/MqttPasswordPlaintext llevan la clave. Re-emparejar
/// un ChipId del tenant rota la clave y cierra la sesión; un ChipId de otro tenant se rechaza.
/// </summary>
public sealed class ProvisionDeviceCommandTests
{
    private static readonly Guid T1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Chip = "0000A1B2C3D4";

    private sealed record Mocks(
        Mock<IDeviceRepository> Devices,
        Mock<ICustomerRepository> Customers,
        Mock<IDeviceApiKeyService> ApiKeys,
        Mock<IMqttConnectionInfoProvider> Conn,
        Mock<ITenantContext> Tenant,
        Mock<IUnitOfWork> Uow,
        Mock<IMqttSessionRevoker> Revoker);

    private static Mocks NewMocks()
    {
        var devices = new Mock<IDeviceRepository>();
        devices.Setup(r => r.AddAsync(It.IsAny<Device>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var customers = new Mock<ICustomerRepository>();

        var apiKeys = new Mock<IDeviceApiKeyService>();
        apiKeys.Setup(s => s.Generate()).Returns(("iotk_PLAINTEXT", "HASHHEX", "FPFPFPFP"));

        var conn = new Mock<IMqttConnectionInfoProvider>();
        conn.SetupGet(c => c.PublicHost).Returns("broker.example.com");
        conn.SetupGet(c => c.Port).Returns(8883);
        conn.SetupGet(c => c.Tls).Returns(true);

        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(T1);

        var revoker = new Mock<IMqttSessionRevoker>();
        revoker.Setup(r => r.RevokeSessionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return new Mocks(devices, customers, apiKeys, conn, tenant, new Mock<IUnitOfWork>(), revoker);
    }

    private static ProvisionDeviceCommandHandler BuildHandler(Mocks m) =>
        new(m.Devices.Object, m.Customers.Object, m.ApiKeys.Object, m.Conn.Object,
            m.Tenant.Object, m.Uow.Object, m.Revoker.Object);

    [Fact]
    public async Task Handle_NewChipId_RegistersMintsAndReturnsInjectBundle()
    {
        var m = NewMocks();
        var rootId = Guid.NewGuid();
        m.Devices.Setup(r => r.GetByChipIdAsync(T1, Chip, It.IsAny<CancellationToken>())).ReturnsAsync((Device?)null);
        m.Devices.Setup(r => r.ExistsByChipIdGlobalAsync(Chip, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        m.Tenant.SetupGet(t => t.CustomerId).Returns((Guid?)null);           // sesión raíz / token sin customer
        m.Customers.Setup(r => r.GetRootCustomerIdAsync(T1, It.IsAny<CancellationToken>())).ReturnsAsync(rootId);

        Device? added = null;
        m.Devices.Setup(r => r.AddAsync(It.IsAny<Device>(), It.IsAny<CancellationToken>()))
            .Callback<Device, CancellationToken>((d, _) => added = d)
            .Returns(Task.CompletedTask);

        var handler = BuildHandler(m);
        var result = await handler.Handle(
            new ProvisionDeviceCommand(T1, Chip, MacAddress: null, Name: null, Model: null,
                Manufacturer: null, Capabilities: new[] { CapabilityType.Switch }, IssuedBy: "operador@t1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Created.Should().BeTrue();

        var cred = result.Value.Credentials;
        cred.MqttClientId.Should().Be(Chip, "el broker exige que el ClientId sea el ChipId");
        cred.MqttUsername.Should().Be("iotk_PLAINTEXT", "la cuenta a inyectar es la ApiKey en texto plano");
        cred.MqttPasswordPlaintext.Should().Be("iotk_PLAINTEXT");
        cred.MqttPasswordFingerprint.Should().Be("FPFPFPFP");
        cred.MqttTopicPrefix.Should().Be($"devices/{Chip}");
        cred.BrokerHost.Should().Be("broker.example.com");
        cred.BrokerPort.Should().Be(8883);
        cred.BrokerTls.Should().BeTrue();

        added.Should().NotBeNull();
        added!.ChipId.Should().Be(Chip);
        added.Name.Should().Be($"Dispositivo {Chip}", "si la app no manda nombre se deriva del ChipId");
        added.MacAddress.Should().Be(Chip, "si la app no manda MAC se usa el ChipId (ambos del eFuse MAC)");
        added.CustomerId.Should().Be(rootId, "sin nodo de sesión se asigna el Customer raíz del tenant");
        added.HasMqttApiKey.Should().BeTrue();
        added.MqttApiKeyFingerprint.Should().Be("FPFPFPFP");
        added.HasCapability(CapabilityType.Switch).Should().BeTrue();

        m.Devices.Verify(r => r.AddAsync(It.IsAny<Device>(), It.IsAny<CancellationToken>()), Times.Once);
        m.Uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ExistingChipIdInTenant_RePairsRotatingKeyAndRevokesSession()
    {
        var m = NewMocks();
        var existing = Device.Create(T1, Chip, "24:6F:28:00:00:01", "Relé Cocina", "Relay-Wifi", "VESIS").Value;
        existing.AssignToCustomer(Guid.NewGuid());
        existing.SetMqttApiKey("OLD-HASH", "OLDFP", "antes");
        m.Devices.Setup(r => r.GetByChipIdAsync(T1, Chip, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        m.ApiKeys.Setup(s => s.Generate()).Returns(("iotk_NEW", "NEW-HASH", "NEWFP"));

        var handler = BuildHandler(m);
        var result = await handler.Handle(
            new ProvisionDeviceCommand(T1, Chip, null, null, null, null, Capabilities: null, IssuedBy: "operador@t1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Created.Should().BeFalse("el equipo ya existía: se re-empareja");
        result.Value.Credentials.DeviceId.Should().Be(existing.Id);
        result.Value.Credentials.MqttUsername.Should().Be("iotk_NEW", "re-emparejar rota la clave");
        result.Value.Credentials.MqttPasswordFingerprint.Should().Be("NEWFP");
        existing.MqttApiKeyFingerprint.Should().Be("NEWFP");

        m.Devices.Verify(r => r.Update(existing), Times.Once);
        m.Devices.Verify(r => r.AddAsync(It.IsAny<Device>(), It.IsAny<CancellationToken>()), Times.Never);
        m.Devices.Verify(r => r.ExistsByChipIdGlobalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "si ya es del tenant no se consulta la unicidad global");
        m.Uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        m.Revoker.Verify(r => r.RevokeSessionAsync(Chip, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once,
            "rotar la clave debe cerrar la sesión viva");
    }

    [Fact]
    public async Task Handle_ChipIdOwnedByAnotherTenant_FailsChipIdTaken()
    {
        var m = NewMocks();
        m.Devices.Setup(r => r.GetByChipIdAsync(T1, Chip, It.IsAny<CancellationToken>())).ReturnsAsync((Device?)null);
        m.Devices.Setup(r => r.ExistsByChipIdGlobalAsync(Chip, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var handler = BuildHandler(m);
        var result = await handler.Handle(
            new ProvisionDeviceCommand(T1, Chip, null, null, null, null, null, "operador@t1"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Device.ChipIdTaken");
        m.Devices.Verify(r => r.AddAsync(It.IsAny<Device>(), It.IsAny<CancellationToken>()), Times.Never,
            "no se reclama un equipo registrado en otro tenant");
        m.Uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenTenantHasNoRootCustomer_FailsNoCustomerNode()
    {
        var m = NewMocks();
        m.Devices.Setup(r => r.GetByChipIdAsync(T1, Chip, It.IsAny<CancellationToken>())).ReturnsAsync((Device?)null);
        m.Devices.Setup(r => r.ExistsByChipIdGlobalAsync(Chip, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        m.Tenant.SetupGet(t => t.CustomerId).Returns((Guid?)null);
        m.Customers.Setup(r => r.GetRootCustomerIdAsync(T1, It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);

        var handler = BuildHandler(m);
        var result = await handler.Handle(
            new ProvisionDeviceCommand(T1, Chip, null, null, null, null, null, "operador@t1"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Device.NoCustomerNode");
        m.Devices.Verify(r => r.AddAsync(It.IsAny<Device>(), It.IsAny<CancellationToken>()), Times.Never);
        m.Uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_HonorsExplicitNameMacAndSessionCustomer()
    {
        var m = NewMocks();
        var sessionCustomer = Guid.NewGuid();
        m.Devices.Setup(r => r.GetByChipIdAsync(T1, Chip, It.IsAny<CancellationToken>())).ReturnsAsync((Device?)null);
        m.Devices.Setup(r => r.ExistsByChipIdGlobalAsync(Chip, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        m.Tenant.SetupGet(t => t.CustomerId).Returns(sessionCustomer);

        Device? added = null;
        m.Devices.Setup(r => r.AddAsync(It.IsAny<Device>(), It.IsAny<CancellationToken>()))
            .Callback<Device, CancellationToken>((d, _) => added = d)
            .Returns(Task.CompletedTask);

        var handler = BuildHandler(m);
        var result = await handler.Handle(
            new ProvisionDeviceCommand(T1, Chip, "24:6F:28:00:00:02", "Relé Patio", "Relay-Wifi", "VESIS", null, "operador@t1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        added.Should().NotBeNull();
        added!.Name.Should().Be("Relé Patio");
        added.MacAddress.Should().Be("24:6F:28:00:00:02");
        added.Model.Should().Be("Relay-Wifi");
        added.Manufacturer.Should().Be("VESIS");
        added.CustomerId.Should().Be(sessionCustomer, "con nodo de sesión no se resuelve el raíz");
        m.Customers.Verify(r => r.GetRootCustomerIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
