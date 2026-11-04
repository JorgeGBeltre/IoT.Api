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

    [Fact]
    public async Task Handle_MultiModeProvisioning_BuildsAllTransportBundlesAndPersistsAttributes()
    {
        var m = NewMocks();
        var rootId = Guid.NewGuid();
        var attrs = new Mock<IDeviceAttributeRepository>();
        m.Devices.Setup(r => r.GetByChipIdAsync(T1, Chip, It.IsAny<CancellationToken>())).ReturnsAsync((Device?)null);
        m.Devices.Setup(r => r.ExistsByChipIdGlobalAsync(Chip, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        m.Customers.Setup(r => r.GetRootCustomerIdAsync(T1, It.IsAny<CancellationToken>())).ReturnsAsync(rootId);

        var handler = new ProvisionDeviceCommandHandler(
            m.Devices.Object,
            m.Customers.Object,
            m.ApiKeys.Object,
            m.Conn.Object,
            m.Tenant.Object,
            m.Uow.Object,
            m.Revoker.Object,
            attrs.Object);

        var deviceParams = new Dictionary<string, string?>
        {
            ["startup"] = "stay",
            ["pulse"] = "on",
            ["pulseWidth"] = "1500",
            ["sledOnline"] = "off",
            ["apn"] = "internet.itelcel.com"
        };

        var result = await handler.Handle(
            new ProvisionDeviceCommand(
                T1,
                Chip,
                "24:6F:28:AA:BB:CC",
                "Interruptor Principal",
                "MQTT-Relay-BLE",
                "Custom IoT",
                new[] { CapabilityType.Switch },
                "operador@t1",
                FirmwareVersion: "3.7.1",
                PairingMode: "softap",
                DeviceKey: "a2a11068-7a28-411f-8c04-d9637c19b1e2",
                BindToken: "bt_custom_token_123",
                WifiSsid: "PlantaWiFi",
                WifiPassword: "WifiPassword123",
                DeviceParameters: deviceParams),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.DeviceKey.Should().Be("a2a11068-7a28-411f-8c04-d9637c19b1e2");
        result.Value.BindToken.Should().Be("bt_custom_token_123");
        result.Value.PairingMode.Should().Be("softap");
        result.Value.Bundles.Should().NotBeNull();

        var bundles = result.Value.Bundles!;
        bundles.BleSchemaAJson.Should().Contain("\"ssid\":\"PlantaWiFi\"");
        bundles.BleSchemaAJson.Should().Contain("\"account\":\"iotk_PLAINTEXT\"");
        bundles.BleSchemaAJson.Should().Contain("\"bind_token\":\"bt_custom_token_123\"");
        bundles.BleSchemaAKeyValue.Should().Contain("ssid=PlantaWiFi;pass=WifiPassword123;server=broker.example.com;port=8883;account=iotk_PLAINTEXT;bind_token=bt_custom_token_123;apn=internet.itelcel.com");
        bundles.BleSchemaBJson.Should().Contain("\"action\":\"provision\"");
        bundles.SoftApPayloadJson.Should().Contain("\"mqtt_broker\":\"broker.example.com\"");
        bundles.SoftApPayloadJson.Should().Contain("\"devicekey\":\"a2a11068-7a28-411f-8c04-d9637c19b1e2\"");
        bundles.SerialAtCommands.Should().Contain(c => c.StartsWith("AT+PROV="));

        // Verificar que el sobre cifrado LAN /zeroconf/config se descifra exactamente con el mismo deviceKey
        global::IoT.Application.Common.Security.LanZeroconfCrypto.TryExtractDataJson(
            bundles.LanZeroconfConfigEncryptedJson,
            "a2a11068-7a28-411f-8c04-d9637c19b1e2",
            out var decryptedLanJson,
            out var decryptErr).Should().BeTrue(decryptErr);
        decryptedLanJson.Should().Contain("\"ssid\":\"PlantaWiFi\"");
        decryptedLanJson.Should().Contain("\"account\":\"iotk_PLAINTEXT\"");
        decryptedLanJson.Should().Contain("\"startup\":\"stay\"");

        attrs.Verify(a => a.UpsertAsync(T1, It.IsAny<Guid>(), AttributeScope.Server, "device_key", "a2a11068-7a28-411f-8c04-d9637c19b1e2", It.IsAny<CancellationToken>()), Times.Once);
        attrs.Verify(a => a.UpsertAsync(T1, It.IsAny<Guid>(), AttributeScope.Server, "bind_token", "bt_custom_token_123", It.IsAny<CancellationToken>()), Times.Once);
        attrs.Verify(a => a.UpsertAsync(T1, It.IsAny<Guid>(), AttributeScope.Shared, "startup", "stay", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ClaimDeviceCommand_ValidatesBindTokenInConstantTime_AndRejectsMismatch()
    {
        var m = NewMocks();
        var attrs = new Mock<IDeviceAttributeRepository>();
        var existing = Device.Create(T1, Chip, "24:6F:28:00:00:01", "Sensor GLP", "MQTT-PressureLevelSensor", "Custom IoT").Value;
        existing.AssignToCustomer(Guid.NewGuid());

        m.Devices.Setup(r => r.GetByChipIdAsync(T1, Chip, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        attrs.Setup(a => a.GetByDeviceAndScopeAsync(existing.Id, AttributeScope.Server, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                DeviceAttribute.Create(T1, existing.Id, AttributeScope.Server, "bind_token", "bt_secret_9999").Value
            });
        attrs.Setup(a => a.GetByDeviceAndScopeAsync(existing.Id, AttributeScope.Client, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DeviceAttribute>());

        var claimHandler = new ClaimDeviceCommandHandler(
            m.Devices.Object,
            m.Customers.Object,
            attrs.Object,
            m.ApiKeys.Object,
            m.Conn.Object,
            m.Tenant.Object,
            m.Uow.Object,
            m.Revoker.Object);

        // 1. Token incorrecto -> rechazado
        var badRes = await claimHandler.Handle(
            new ClaimDeviceCommand(T1, Chip, "bt_wrong_0000"),
            CancellationToken.None);
        badRes.IsFailure.Should().BeTrue();
        badRes.Error.Code.Should().Be("Device.InvalidClaimSecret");

        // 2. Token correcto -> reclamado con rotación de ApiKey
        var okRes = await claimHandler.Handle(
            new ClaimDeviceCommand(T1, Chip, "bt_secret_9999"),
            CancellationToken.None);
        okRes.IsSuccess.Should().BeTrue();
        okRes.Value.Credentials.MqttUsername.Should().Be("iotk_PLAINTEXT");
        okRes.Value.BindToken.Should().Be("bt_secret_9999");
    }

    [Theory]
    [InlineData("192.168.4.1", 80, true)]
    [InlineData("10.10.7.1", 80, true)]
    [InlineData("192.168.1.50", 8081, true)]
    [InlineData("172.16.0.25", 8081, true)]
    [InlineData("vesis_a1b2c3.local", 8081, true)]
    [InlineData("127.0.0.1", 8081, false)]
    [InlineData("169.254.169.254", 80, false)]
    [InlineData("8.8.8.8", 8081, false)]
    [InlineData("http://192.168.4.1/ap", 80, false)]
    public void LanZeroconfConnectorService_ValidateLanTargetHost_EnforcesRfc1918AndBlocksSsrf(
        string host,
        int port,
        bool expectedAllowed)
    {
        var httpFactory = new Mock<System.Net.Http.IHttpClientFactory>();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<global::IoT.Infrastructure.Services.LanZeroconfConnectorService>>();
        var svc = new global::IoT.Infrastructure.Services.LanZeroconfConnectorService(httpFactory.Object, logger.Object);

        var res = svc.ValidateLanTargetHost(host, port);
        res.IsSuccess.Should().Be(expectedAllowed);
    }

    [Fact]
    public async Task Handle_ReProvisioningMisclassifiedRelay_UpdatesModelNameAndReplacesStaleGpsCapabilityWithSwitch()
    {
        var m = NewMocks();
        var existing = Device.Create(T1, Chip, "2C:BC:BB:05:1B:B8", "sala1", "Telemetry / GPS Module", "Custom IoT").Value;
        existing.AssignToCustomer(Guid.NewGuid());
        existing.AssignCapability(CapabilityType.Gps);
        existing.SetMqttApiKey("OLD-HASH", "OLDFP", "antes");

        m.Devices.Setup(r => r.GetByChipIdAsync(T1, Chip, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        m.ApiKeys.Setup(s => s.Generate()).Returns(("iotk_RELAY", "NEW-HASH", "NEWFP"));

        var handler = BuildHandler(m);
        var result = await handler.Handle(
            new ProvisionDeviceCommand(
                T1,
                Chip,
                MacAddress: "2C:BC:BB:05:1B:B8",
                Name: "Sala Interruptor",
                Model: "Relay-Wifi",
                Manufacturer: "VESIS",
                Capabilities: null,
                IssuedBy: "operador@t1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        existing.Name.Should().Be("Sala Interruptor");
        existing.Model.Should().Be("Relay-Wifi");
        existing.Manufacturer.Should().Be("VESIS");
        existing.HasCapability(CapabilityType.Switch).Should().BeTrue();
        existing.HasCapability(CapabilityType.Gps).Should().BeFalse("un relé re-aprovisionado debe limpiar la capacidad GPS errónea");
    }

    [Theory]
    [InlineData("Relay-Wifi", "set_switch", "{\"switch\":\"on\",\"state\":true}", "switch1:on")]
    [InlineData("Smart Switch", "set_channel", "{\"channel\":2,\"switch\":\"off\"}", "switch2:off")]
    [InlineData("MQTT-Relay-BLE", "SET_POWER", "{\"channel\":1,\"state\":\"ON\"}", "switch1:on")]
    [InlineData("Relay-Wifi", "SET_POWER_STATE", "{\"state\":0}", "switch1:off")]
    [InlineData("Relay-Wifi", "SET_POWER_ALL", "{\"state\":\"ON\"}", "switch1:on")]
    [InlineData("Relay-Wifi", "toggle_all", null, "toggle_all")]
    [InlineData("Relay-Wifi", "reboot", null, "reset")]
    public void TopicRoutingService_RelaySwitchModels_RouteToCommandsTopicAndEncodeNativePayload(
        string model,
        string commandName,
        string? payloadJson,
        string expectedPayload)
    {
        var routing = new global::IoT.Protocols.Routing.TopicRoutingService(
            global::IoT.Protocols.Profiles.ProtocolRegistry.CreateDefault());

        routing.BuildCommandTopic(model, "B81B05BBBC2C").Should().Be("commands/B81B05BBBC2C");
        routing.BuildCommandPayload(model, commandName, payloadJson).Should().Be(expectedPayload);
    }

    [Theory]
    [InlineData("localhost", "192.168.100.7", "192.168.100.7")]
    [InlineData("127.0.0.1", "192.168.100.7", "192.168.100.7")]
    [InlineData("192.168.2.178", "192.168.100.149", "192.168.100.149")]
    [InlineData("192.168.2.178", "192.168.2.200", "192.168.2.178")]
    [InlineData("broker.example.com", "192.168.100.7", "broker.example.com")]
    [InlineData("localhost", "bad/host:inject", "localhost")]
    public void MqttConnectionInfoProvider_ResolveEffectiveHost_HandlesLanSubnetAndPreservesPublicDomains(
        string configured,
        string? requestHost,
        string expected)
    {
        global::IoT.Infrastructure.Mqtt.MqttConnectionInfoProvider
            .ResolveEffectiveHost(configured, requestHost)
            .Should()
            .Be(expected);
    }

    [Fact]
    public async Task Handle_NormalizesMacFormattedChipId_AndNeverPersistsSensitiveWifiParameters()
    {
        var m = NewMocks();
        var attrs = new Mock<IDeviceAttributeRepository>();
        m.Customers.Setup(c => c.GetRootCustomerIdAsync(T1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        m.Devices.Setup(r => r.GetByChipIdAsync(T1, "2CBCBB051BB8", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Device?)null);
        m.Devices.Setup(r => r.ExistsByChipIdGlobalAsync("2CBCBB051BB8", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var handler = new ProvisionDeviceCommandHandler(
            m.Devices.Object,
            m.Customers.Object,
            m.ApiKeys.Object,
            m.Conn.Object,
            m.Tenant.Object,
            m.Uow.Object,
            m.Revoker.Object,
            attrs.Object);

        var parameters = new Dictionary<string, string?>
        {
            ["room"] = "Sala",
            ["wifi_pass"] = "TopSecret123",
            ["wifiPassword"] = "TopSecret123",
            ["apn_pass"] = "CarrierSecret"
        };

        var result = await handler.Handle(
            new ProvisionDeviceCommand(
                T1,
                "2c:bc:bb:05:1b:b8",
                MacAddress: "2c:bc:bb:05:1b:b8",
                Name: "Interruptor Sala",
                Model: "Relay-Wifi",
                Manufacturer: "VESIS",
                Capabilities: new[] { CapabilityType.Switch },
                IssuedBy: "operador@t1",
                WifiSsid: null,
                WifiPassword: null,
                DeviceParameters: parameters),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Credentials.ChipId.Should().Be("2CBCBB051BB8");
        attrs.Verify(a => a.UpsertAsync(T1, It.IsAny<Guid>(), AttributeScope.Shared, "room", "Sala", It.IsAny<CancellationToken>()), Times.Once);
        attrs.Verify(a => a.UpsertAsync(T1, It.IsAny<Guid>(), It.IsAny<AttributeScope>(), "wifi_pass", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        attrs.Verify(a => a.UpsertAsync(T1, It.IsAny<Guid>(), It.IsAny<AttributeScope>(), "wifiPassword", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        attrs.Verify(a => a.UpsertAsync(T1, It.IsAny<Guid>(), It.IsAny<AttributeScope>(), "apn_pass", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void TolerantCapabilityListJsonConverter_DeserializesStringEnums_Integers_AndFrontendAliases()
    {
        var opts = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        opts.Converters.Add(new global::IoT.Application.Common.Serialization.TolerantCapabilityListJsonConverter());
        opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

        var fromMobileStrings = System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<CapabilityType>?>(
            "[\"Switch\"]",
            opts);
        fromMobileStrings.Should().Equal(CapabilityType.Switch);

        var fromWebMixed = System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<CapabilityType>?>(
            "[1, \"SmartSwitch\", \"GlpSensor\", \"Accelerometer\"]",
            opts);
        fromWebMixed.Should().Equal(
            CapabilityType.Gps,
            CapabilityType.Switch,
            CapabilityType.LevelTelemetry);
    }
}
