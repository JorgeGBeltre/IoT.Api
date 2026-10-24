using FluentAssertions;
using IoT.Domain.Entities;
using IoT.Infrastructure.Mqtt;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// Hallazgos de gravedad media y baja del informe de revisión: huecos del ACL y cotas de
/// entrada.
/// </summary>
public class OtaInputBoundsAndAclGapsTests
{
    private const string Mine = "A1B2C3D4E5F6";
    private const string Other = "FFEEDDCCBBAA";

    // -----------------------------------------------------------------
    // MEDIUM-12: el canal de configuración del firmware GPS
    // -----------------------------------------------------------------

    [Fact]
    public void ADeviceCanReadItsOwnScopedConfigChannel()
    {
        // El firmware GPS se suscribía al literal "config", compartido por TODOS los equipos:
        // cualquiera suscrito recibía la configuración de los demás. Ahora usa
        // "config/<chipId>", y el ACL tiene que reconocerlo como canal propio de bajada —si
        // no, el equipo se queda sin poder recibir su configuración (era una regresión).
        MqttTopicAcl.CanSubscribe($"config/{Mine}", Mine).Should().Be(MqttTopicAcl.Decision.Allow);
        MqttTopicAcl.CanSubscribe($"config/{Other}", Mine).Should().Be(MqttTopicAcl.Decision.Deny);
    }

    [Fact]
    public void TheConfigChannelIsDownlinkSoNoDeviceCanWriteIt()
    {
        // Es el servidor quien configura al equipo. Si un dispositivo pudiera publicar ahí,
        // podría reconfigurar a otro (y a sí mismo, saltándose la plataforma).
        MqttTopicAcl.CanPublish($"config/{Mine}", isServerOrigin: false, Mine)
            .Should().Be(MqttTopicAcl.Decision.Deny);
        MqttTopicAcl.CanPublish($"config/{Other}", isServerOrigin: false, Mine)
            .Should().Be(MqttTopicAcl.Decision.Deny);
        MqttTopicAcl.CanPublish($"config/{Mine}", isServerOrigin: false, null, isBootstrapCredential: true)
            .Should().Be(MqttTopicAcl.Decision.Deny);

        MqttTopicAcl.CanPublish($"config/{Mine}", isServerOrigin: true, null)
            .Should().Be(MqttTopicAcl.Decision.Allow);
    }

    [Fact]
    public void TheBareConfigTopicCannotBeReadByAnyone()
    {
        // El tópico sin identificador sigue sin poder leerse: es lo que hacía que la
        // configuración de un equipo llegara a todos.
        MqttTopicAcl.CanSubscribe("config", Mine).Should().Be(MqttTopicAcl.Decision.Deny);
        MqttTopicAcl.CanSubscribe("config", null, isBootstrapCredential: true)
            .Should().Be(MqttTopicAcl.Decision.Deny);
    }

    // -----------------------------------------------------------------
    // LOW-2: el ChipId se usa literal como segmento de tópico
    // -----------------------------------------------------------------

    [Theory]
    // Una barra partiría el tópico y rompería la comprobación de propiedad del ACL.
    [InlineData("CHIP/OTRO")]
    [InlineData("../CHIP")]
    // Comodines MQTT: convertirían una suscripción propia en una a los tópicos de TODOS.
    [InlineData("CHIP+")]
    [InlineData("#")]
    [InlineData("+")]
    [InlineData("CHIP#")]
    // Espacios y caracteres de control.
    [InlineData("CHIP CON ESPACIO")]
    [InlineData("CHIP\tTAB")]
    [InlineData("CHIP\nSALTO")]
    public void ADeviceCannotBeRegisteredWithAChipIdThatBreaksTopicOwnership(string chipId)
    {
        var result = Device.Create(Guid.NewGuid(), chipId, "11:22:33:44:55:66", "Equipo", "ESP32", "Espressif");

        result.IsFailure.Should().BeTrue("el ChipId se usa literalmente como segmento de tópico MQTT");
        result.Error.Code.Should().Be("Device.InvalidChipId");
    }

    [Theory]
    // Los ChipId reales de los firmwares: no se rompe ninguno.
    [InlineData("A1B2C3D4E5F6")]            // EfuseMac de Fligo/Toour
    [InlineData("ESP8266_A1B2C3")]          // GPS, con prefijo
    [InlineData("GLP-001")]                 // device_id del GLP
    [InlineData("351969562721598")]         // IMEI de un tracker
    [InlineData("device_switch1")]
    public void RealisticChipIdsAreAccepted(string chipId)
    {
        var result = Device.Create(Guid.NewGuid(), chipId, "11:22:33:44:55:66", "Equipo", "ESP32", "Espressif");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void AChipIdWithAWildcardWouldHaveGrantedAccessToEveryDevice()
    {
        // Demostración de por qué importa: si un ChipId "#" se hubiera podido registrar, su
        // dueño habría pedido "ota/#" como "su" tópico.
        Device.Create(Guid.NewGuid(), "#", "11:22:33:44:55:66", "Equipo", "ESP32", "Espressif")
            .IsFailure.Should().BeTrue();

        // Y el ACL, por su parte, deniega el comodín aunque alguien lo colara en base.
        MqttTopicAcl.CanSubscribe("ota/#", "#").Should().Be(MqttTopicAcl.Decision.Deny);
    }
}
