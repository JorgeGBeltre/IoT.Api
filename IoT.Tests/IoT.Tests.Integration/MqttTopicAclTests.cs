using FluentAssertions;
using IoT.Infrastructure.Mqtt;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// Aislamiento por tópico del broker embebido. Antes sólo se validaba la CONEXIÓN: una vez
/// dentro, un dispositivo podía suscribirse a los tópicos de otro. Con la OTA eso es grave,
/// porque el firmware se publica troceado en <c>ota/{chipId}</c>.
/// </summary>
public class MqttTopicAclTests
{
    private const string Mine = "A1B2C3D4E5F6";
    private const string Other = "FFEEDDCCBBAA";

    // ---------------------------------------------------------------------
    // Dispositivo autenticado con SU ApiKey
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("ota/A1B2C3D4E5F6")]
    [InlineData("ota/A1B2C3D4E5F6/progress")]
    [InlineData("event/A1B2C3D4E5F6")]
    [InlineData("commands/A1B2C3D4E5F6")]
    [InlineData("devices/A1B2C3D4E5F6/telemetry")]
    [InlineData("glp/config/A1B2C3D4E5F6")]
    // Tópicos del relé tras el cambio de firmware: ahora llevan el chipId.
    [InlineData("devices/A1B2C3D4E5F6/device_switch1/command")]
    [InlineData("devices/A1B2C3D4E5F6/device_switch1_state")]
    [InlineData("status/A1B2C3D4E5F6")]
    public void ADeviceMaySubscribeToItsOwnTopics(string topic)
    {
        MqttTopicAcl.CanSubscribe(topic, Mine).Should().Be(MqttTopicAcl.Decision.Allow);
    }

    [Theory]
    // EL ATAQUE: suscribirse al canal de firmware de otro equipo y descargarse su imagen.
    [InlineData("ota/FFEEDDCCBBAA")]
    [InlineData("ota/FFEEDDCCBBAA/progress")]
    // Interceptar los comandos o el estado de otro equipo.
    [InlineData("event/FFEEDDCCBBAA")]
    [InlineData("commands/FFEEDDCCBBAA")]
    [InlineData("devices/FFEEDDCCBBAA/telemetry")]
    [InlineData("devices/FFEEDDCCBBAA/device_switch1/command")]
    [InlineData("glp/config/FFEEDDCCBBAA")]
    [InlineData("status/FFEEDDCCBBAA")]
    public void ADeviceMayNotSubscribeToAnotherDevicesTopics(string topic)
    {
        MqttTopicAcl.CanSubscribe(topic, Mine).Should().Be(MqttTopicAcl.Decision.Deny);
    }

    [Theory]
    // Un comodín sobre un prefijo con ámbito por dispositivo abarcaría a TODOS los equipos.
    [InlineData("ota/#")]
    [InlineData("ota/+")]
    [InlineData("ota/+/progress")]
    [InlineData("#")]
    [InlineData("event/#")]
    [InlineData("commands/#")]
    [InlineData("devices/+/telemetry")]
    public void WildcardsOverPerDeviceTopicsAreRefused(string topic)
    {
        MqttTopicAcl.CanSubscribe(topic, Mine).Should().Be(MqttTopicAcl.Decision.Deny);
    }

    [Fact]
    public void AWildcardUnderOwnIdentifierIsAllowed()
    {
        MqttTopicAcl.CanSubscribe($"ota/{Mine}/#", Mine).Should().Be(MqttTopicAcl.Decision.Allow);
    }

    [Theory]
    [InlineData("ota/A1B2C3D4E5F6")]
    [InlineData("event/A1B2C3D4E5F6")]
    public void WithoutAProvenIdentityPerDeviceTopicsAreRefused(string topic)
    {
        MqttTopicAcl.CanSubscribe(topic, null).Should().Be(MqttTopicAcl.Decision.Deny);
    }

    // ---------------------------------------------------------------------
    // Tópicos COMPARTIDOS: ya no se pueden leer
    // ---------------------------------------------------------------------

    [Theory]
    // Los tópicos pelados que usaba el relé ANTES del cambio de firmware. Ya no los usa
    // nadie y leerlos nunca fue legítimo: eran el agujero de aislamiento.
    [InlineData("telemetry")]
    [InlineData("events")]
    [InlineData("commands")]
    [InlineData("ota")]
    [InlineData("status")]
    // Tópicos fijos de Fligo/Toour: ahí se publican reportes de configuración de OTROS
    // equipos, así que la lectura se deniega aunque la publicación siga permitida.
    [InlineData("TicketValidationRequest")]
    [InlineData("DeviceConfigValidationRequest")]
    [InlineData("ErrorUpdateFirmwareDevice")]
    public void SharedTopicsCanNoLongerBeRead(string topic)
    {
        MqttTopicAcl.CanSubscribe(topic, Mine).Should().Be(MqttTopicAcl.Decision.Deny);
        MqttTopicAcl.CanSubscribe(topic, null).Should().Be(MqttTopicAcl.Decision.Deny);
    }

    [Theory]
    // Firmwares que todavía publican en tópicos fijos (Fligo y Toour, aún sin cambiar).
    // Se permite la subida y se MARCA como compartida para que quede en el log.
    [InlineData("TicketValidationRequest")]
    [InlineData("DeviceConfigValidationRequest")]
    [InlineData("ErrorUpdateFirmwareDevice")]
    public void LegacyFixedTopicsMayStillBePublished(string topic)
    {
        MqttTopicAcl.CanPublish(topic, isServerOrigin: false, Mine).Should().Be(MqttTopicAcl.Decision.AllowSharedLegacy);
    }

    // ---------------------------------------------------------------------
    // Canal de bajada: sólo el servidor
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("ota/FFEEDDCCBBAA")]
    [InlineData("event/FFEEDDCCBBAA")]
    [InlineData("commands/FFEEDDCCBBAA")]
    public void ADeviceMayNotPublishDownlinkTopics(string topic)
    {
        MqttTopicAcl.CanPublish(topic, isServerOrigin: false, Mine).Should().Be(MqttTopicAcl.Decision.Deny);
    }

    [Fact]
    public void ADeviceMayNotPublishIntoItsOwnDownlinkChannelEither()
    {
        // ota/<yo> sin sufijo es lo que el SERVIDOR usa para enviar los trozos. Si el propio
        // equipo pudiera escribir ahí, podría alimentarse a sí mismo una imagen cualquiera.
        MqttTopicAcl.CanPublish($"ota/{Mine}", isServerOrigin: false, Mine).Should().Be(MqttTopicAcl.Decision.Deny);
        MqttTopicAcl.CanPublish($"event/{Mine}", isServerOrigin: false, Mine).Should().Be(MqttTopicAcl.Decision.Deny);
    }

    [Theory]
    [InlineData("ota/A1B2C3D4E5F6/progress")]
    [InlineData("ota/A1B2C3D4E5F6/error")]
    [InlineData("ota/A1B2C3D4E5F6/success")]
    [InlineData("ota/A1B2C3D4E5F6/state")]
    public void ADeviceMayPublishItsOwnOtaReports(string topic)
    {
        MqttTopicAcl.CanPublish(topic, isServerOrigin: false, Mine).Should().Be(MqttTopicAcl.Decision.Allow);
    }

    [Fact]
    public void ADeviceMayNotPublishReportsOnBehalfOfAnother()
    {
        MqttTopicAcl.CanPublish($"ota/{Other}/success", isServerOrigin: false, Mine)
            .Should().Be(MqttTopicAcl.Decision.Deny);
    }

    [Fact]
    public void TheServerMayPublishAnywhere_BecauseItSendsFirmwareAndCommands()
    {
        MqttTopicAcl.CanPublish($"ota/{Other}", isServerOrigin: true, null)
            .Should().Be(MqttTopicAcl.Decision.Allow);
        MqttTopicAcl.CanPublish($"event/{Other}", isServerOrigin: true, null)
            .Should().Be(MqttTopicAcl.Decision.Allow);
    }

    // ---------------------------------------------------------------------
    // Credencial GLOBAL de bootstrap (simulador / herramientas del operador)
    // ---------------------------------------------------------------------

    [Theory]
    // El simulador publica telemetría en nombre de varios equipos: es su función. El ACL lo
    // PERMITE a propósito y NO comprueba el tenant: es una clase estática sin BD y NO es la
    // frontera de aislamiento entre tenants. Esa frontera vive en la INGESTA
    // (IngestTelemetryCommand: rechaza la trama si el dispositivo resuelto es de otro tenant
    // que el de la credencial), probada en BootstrapCredentialTenantScopeTests. Que estos
    // casos no sean Deny aquí NO es un permiso para inyectar entre tenants (MEDIUM-2): ese
    // rechazo ocurre aguas abajo.
    //
    // Se afirma la decisión EXACTA, no "cualquiera de las dos permisivas": un tópico con dueño
    // reconocido (devices/<id>, glp/<sub>/<id>) da Allow, y uno sin identificador (telemetry
    // pelado, relay/... cuyo prefijo no es de ámbito) da AllowSharedLegacy. Un BeOneOf(Allow,
    // AllowSharedLegacy) pasaba con cualquiera de las dos semánticas y no fijaba ninguna, así
    // que no habría cazado un trasvase silencioso entre ellas.
    [InlineData("telemetry", MqttTopicAcl.Decision.AllowSharedLegacy)]
    [InlineData("glp/telemetria/GLP-TANK01", MqttTopicAcl.Decision.Allow)]
    [InlineData("relay/telemetria/RELAY-CTRL01", MqttTopicAcl.Decision.AllowSharedLegacy)]
    [InlineData("devices/FFEEDDCCBBAA/telemetry", MqttTopicAcl.Decision.Allow)]
    public void TheBootstrapCredentialMayPublishTelemetryForAnyDevice(string topic, MqttTopicAcl.Decision expected)
    {
        MqttTopicAcl.CanPublish(topic, isServerOrigin: false, null, isBootstrapCredential: true)
            .Should().Be(expected);
    }

    [Theory]
    // Pero NO es un dispositivo: no tiene por qué leer el canal de nadie...
    [InlineData("ota/A1B2C3D4E5F6")]
    [InlineData("event/A1B2C3D4E5F6")]
    [InlineData("commands/A1B2C3D4E5F6")]
    [InlineData("telemetry")]
    public void TheBootstrapCredentialMayNotSubscribeToAnything(string topic)
    {
        MqttTopicAcl.CanSubscribe(topic, null, isBootstrapCredential: true)
            .Should().Be(MqttTopicAcl.Decision.Deny);
    }

    [Theory]
    // ...ni inyectar firmware o comandos: la bajada es exclusiva del servidor.
    [InlineData("ota/A1B2C3D4E5F6")]
    [InlineData("event/A1B2C3D4E5F6")]
    [InlineData("commands/A1B2C3D4E5F6")]
    public void TheBootstrapCredentialMayNotPublishDownlink(string topic)
    {
        MqttTopicAcl.CanPublish(topic, isServerOrigin: false, null, isBootstrapCredential: true)
            .Should().Be(MqttTopicAcl.Decision.Deny);
    }

    // ---------------------------------------------------------------------
    // Canales de bajada que NO están en el primer segmento
    // ---------------------------------------------------------------------

    [Theory]
    // El relé se SUSCRIBE a estos (Relay_Wifi_Esp32, tras pasar a tópicos por dispositivo):
    // son órdenes del servidor. Clasificarlos como subida dejaba que otro cliente accionara
    // el relé de un equipo ajeno.
    [InlineData("devices/A1B2C3D4E5F6/device_switch1/command")]
    [InlineData("devices/A1B2C3D4E5F6/device_switch1/state")]
    // El relé se suscribe al "<base>_state" (guion bajo): el servidor fija ahí el estado
    // deseado y el firmware OBEDECE. Es la bajada que, clasificada como subida, dejaba
    // accionar físicamente el relé de un equipo ajeno (CRÍTICO-1). Debe ser servidor-only,
    // incluida la credencial global publicando en el "_state" de OTRO equipo.
    [InlineData("devices/A1B2C3D4E5F6/device_switch1_state")]
    // El GLP se suscribe a su canal de configuración.
    [InlineData("glp/config/GLP-001")]
    public void DownlinkTopicsOutsideTheFirstSegmentAreServerOnly(string topic)
    {
        string owner = MqttTopicAcl.DeviceIdOf(topic)!;

        // Ni el propio equipo puede darse órdenes a sí mismo...
        MqttTopicAcl.CanPublish(topic, isServerOrigin: false, owner).Should().Be(MqttTopicAcl.Decision.Deny);
        // ...ni otro equipo...
        MqttTopicAcl.CanPublish(topic, isServerOrigin: false, Other).Should().Be(MqttTopicAcl.Decision.Deny);
        // ...ni la credencial global de bootstrap.
        MqttTopicAcl.CanPublish(topic, isServerOrigin: false, null, isBootstrapCredential: true)
            .Should().Be(MqttTopicAcl.Decision.Deny);

        // El servidor sí: es quien manda los comandos.
        MqttTopicAcl.CanPublish(topic, isServerOrigin: true, null)
            .Should().Be(MqttTopicAcl.Decision.Allow);
    }

    [Theory]
    // Lo que el firmware SÍ publica debe seguir permitido. El relé reporta su estado en
    // "<base>/report"; "<base>_state" y "/state" son canales del SERVIDOR (el firmware
    // obedece lo que llegue ahí), así que NO aparecen en esta lista.
    [InlineData("devices/A1B2C3D4E5F6/device_switch1/report")]
    [InlineData("devices/A1B2C3D4E5F6/device_switch1/get")]
    [InlineData("devices/A1B2C3D4E5F6/telemetry")]
    [InlineData("telemetry/A1B2C3D4E5F6")]
    [InlineData("status/A1B2C3D4E5F6")]
    [InlineData("events/A1B2C3D4E5F6")]
    [InlineData("glp/telemetria/A1B2C3D4E5F6")]
    [InlineData("glp/status/A1B2C3D4E5F6")]
    public void ADeviceMayStillPublishItsOwnUplinkTopics(string topic)
    {
        MqttTopicAcl.CanPublish(topic, isServerOrigin: false, Mine).Should().Be(MqttTopicAcl.Decision.Allow);
    }

    [Fact]
    public void BothStateTopicsAreDownlinkAndOnlyReportIsUplink()
    {
        // Este test afirmaba lo contrario —que "_state" era subida— y así fijaba por escrito
        // el fallo que permitía accionar el relé de un equipo ajeno. El firmware OBEDECE lo
        // que llegue a los dos tópicos de estado; sólo "/report" es suyo.
        MqttTopicAcl.CanPublish($"devices/{Mine}/device_switch1_state", isServerOrigin: false, Mine)
            .Should().Be(MqttTopicAcl.Decision.Deny);
        MqttTopicAcl.CanPublish($"devices/{Mine}/device_switch1/state", isServerOrigin: false, Mine)
            .Should().Be(MqttTopicAcl.Decision.Deny);
        MqttTopicAcl.CanPublish($"devices/{Mine}/device_switch1/report", isServerOrigin: false, Mine)
            .Should().Be(MqttTopicAcl.Decision.Allow);
    }

    [Theory]
    // Reportes de Fligo/Toour tras migrar a ámbito por dispositivo: el dueño los publica...
    [InlineData("devices/A1B2C3D4E5F6/ticket-validation")]
    [InlineData("devices/A1B2C3D4E5F6/config-validation")]
    [InlineData("devices/A1B2C3D4E5F6/ota-error")]
    public void TheValidatorReportsAreUplinkForTheirOwner(string topic)
    {
        MqttTopicAcl.CanPublish(topic, isServerOrigin: false, Mine).Should().Be(MqttTopicAcl.Decision.Allow);
        // ...y sólo su dueño: el reporte de configuración lleva la contraseña WiFi del cliente.
        MqttTopicAcl.CanPublish(topic, isServerOrigin: false, Other).Should().Be(MqttTopicAcl.Decision.Deny);
        MqttTopicAcl.CanSubscribe(topic, Other).Should().Be(MqttTopicAcl.Decision.Deny);
        // Su dueño sí puede leerlos (p.ej. para confirmar lo que envió).
        MqttTopicAcl.CanSubscribe(topic, Mine).Should().Be(MqttTopicAcl.Decision.Allow);
    }

    [Fact]
    public void TheValidatorReportSuffixesAreNotMistakenForDownlink()
    {
        // "config-validation" NO es "config": si se clasificara como bajada, el equipo no
        // podría publicar su propio reporte.
        MqttTopicAcl.CanPublish($"devices/{Mine}/config-validation", isServerOrigin: false, Mine)
            .Should().Be(MqttTopicAcl.Decision.Allow);
        // Mientras que "config" a secas sí es bajada y es exclusivo del servidor.
        MqttTopicAcl.CanPublish($"devices/{Mine}/config", isServerOrigin: false, Mine)
            .Should().Be(MqttTopicAcl.Decision.Deny);
    }

    // ---------------------------------------------------------------------
    // Suplantación del servidor
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("ota/FFEEDDCCBBAA")]
    [InlineData("event/FFEEDDCCBBAA")]
    [InlineData("commands/FFEEDDCCBBAA")]
    [InlineData("glp/config/GLP-001")]
    [InlineData("devices/FFEEDDCCBBAA/device_switch1/command")]
    public void NamingYourselfLikeTheServerGrantsNothing(string topic)
    {
        // EL BYPASS: el ClientId lo elige quien conecta. Si el privilegio de servidor se
        // dedujera del nombre, bastaba conectar como "internal-server" —con la ApiKey global,
        // o en modo anónimo— para publicar firmware a cualquier equipo. El privilegio viene
        // ahora de que la publicación se inyecte en proceso, sin sesión externa detrás.
        MqttTopicAcl.CanPublish(topic, isServerOrigin: false, MqttTopicAcl.ServerClientId)
            .Should().Be(MqttTopicAcl.Decision.Deny);

        MqttTopicAcl.CanPublish(topic, isServerOrigin: false, MqttTopicAcl.ServerClientId,
            isBootstrapCredential: true).Should().Be(MqttTopicAcl.Decision.Deny);

        // Y tampoco puede leer el canal de nadie.
        MqttTopicAcl.CanSubscribe(topic, MqttTopicAcl.ServerClientId)
            .Should().Be(MqttTopicAcl.Decision.Deny);
    }

    [Fact]
    public void TheServerClientIdIsTheExpectedConstant()
    {
        // ESTE test sólo fija el VALOR de la constante (es el ClientId reservado del servidor).
        // NO comprueba el rechazo en el CONNECT: eso vive en OnValidatingConnection del broker
        // y se ejercita de verdad en EmbeddedMqttBrokerIntegrationTests, con un cliente MQTT
        // real conectándose como "internal-server". (Antes este test se llamaba
        // "...TheBrokerRefusesOnConnect" y su comentario afirmaba cubrir ese rechazo, cuando
        // el cuerpo sólo compara una cadena: pasaba aunque se quitara el rechazo del broker.)
        MqttTopicAcl.ServerClientId.Should().Be("internal-server");
    }

    // ---------------------------------------------------------------------
    // CRITICO-1 del informe: accionamiento fisico del rele por el canal _state
    // ---------------------------------------------------------------------

    [Fact]
    public void NobodyButTheServerCanDriveARelayThroughTheUnderscoreStateTopic()
    {
        // El firmware OBEDECE lo que llegue a "<base>_state": aplica el estado al relé
        // (RelayFirmwareController, rama topic == stateTopic -> applyDesiredState). Mientras
        // ese sufijo estuvo clasificado como SUBIDA, cualquiera autorizado a publicar
        // telemetría podía CERRAR UN RELÉ en una instalación ajena. La credencial global lo
        // podía hacer sobre cualquier equipo de cualquier tenant.
        string victim = $"devices/{Other}/device_switch1_state";
        string own = $"devices/{Mine}/device_switch1_state";

        MqttTopicAcl.CanPublish(victim, isServerOrigin: false, Mine).Should().Be(MqttTopicAcl.Decision.Deny);
        MqttTopicAcl.CanPublish(victim, isServerOrigin: false, null, isBootstrapCredential: true)
            .Should().Be(MqttTopicAcl.Decision.Deny, "la credencial global no puede accionar el relé de nadie");
        // Ni siquiera el propio equipo: ese canal es del servidor.
        MqttTopicAcl.CanPublish(own, isServerOrigin: false, Mine).Should().Be(MqttTopicAcl.Decision.Deny);

        MqttTopicAcl.CanPublish(victim, isServerOrigin: true, null).Should().Be(MqttTopicAcl.Decision.Allow);
    }

    [Fact]
    public void TheDeviceReportsItsRelayStateOnItsOwnReportTopic()
    {
        // El equipo ya no publica en "_state": reporta en "<base>/report", que sigue siendo
        // subida suya. Si esto se clasificara como bajada, el relé se quedaría mudo.
        MqttTopicAcl.CanPublish($"devices/{Mine}/device_switch1/report", isServerOrigin: false, Mine)
            .Should().Be(MqttTopicAcl.Decision.Allow);
        MqttTopicAcl.CanPublish($"devices/{Other}/device_switch1/report", isServerOrigin: false, Mine)
            .Should().Be(MqttTopicAcl.Decision.Deny);
    }

    [Theory]
    // Variantes del sufijo: cualquier cosa que acabe en _state es canal del servidor.
    [InlineData("devices/A1B2C3D4E5F6/device_switch1_state")]
    [InlineData("devices/A1B2C3D4E5F6/rele_principal_state")]
    [InlineData("devices/A1B2C3D4E5F6/zona3/salida_state")]
    public void EveryUnderscoreStateSuffixIsTreatedAsDownlink(string topic)
    {
        MqttTopicAcl.CanPublish(topic, isServerOrigin: false, Mine).Should().Be(MqttTopicAcl.Decision.Deny);
        MqttTopicAcl.CanPublish(topic, isServerOrigin: true, null).Should().Be(MqttTopicAcl.Decision.Allow);
    }

    // ---------------------------------------------------------------------
    // Resolución del dueño del tópico
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("ota/A1B2C3D4E5F6", "A1B2C3D4E5F6")]
    [InlineData("ota/A1B2C3D4E5F6/progress", "A1B2C3D4E5F6")]
    [InlineData("glp/telemetria/GLP-001", "GLP-001")]
    [InlineData("glp/status/GLP-001", "GLP-001")]
    [InlineData("devices/DEV-1/telemetry", "DEV-1")]
    [InlineData("commands", null)]
    [InlineData("device_switch1_state", null)]
    public void IdentifiesTheOwnerOfATopic(string topic, string? expectedOwner)
    {
        MqttTopicAcl.DeviceIdOf(topic).Should().Be(expectedOwner);
    }

    [Fact]
    public void TheComparisonIsCaseInsensitive()
    {
        // El ChipId del EfuseMac se formatea en mayúsculas, pero no todos los firmwares lo
        // anuncian igual en el tópico.
        MqttTopicAcl.CanSubscribe("ota/a1b2c3d4e5f6", Mine).Should().Be(MqttTopicAcl.Decision.Allow);
    }
}
