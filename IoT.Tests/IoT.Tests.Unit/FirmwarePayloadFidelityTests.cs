using FluentAssertions;
using IoT.Protocols.Adapters;
using IoT.Protocols.Security;
using Xunit;

namespace IoT.Tests.Unit;

/// <summary>
/// Fidelidad de los adaptadores frente a las tramas EXACTAS que emiten los firmwares
/// reales de References/Dispositivos. Cada payload de estos tests se transcribió del
/// serializador del firmware, no de la documentación: varios campos se estaban perdiendo
/// porque el adaptador esperaba nombres que el firmware nunca llegó a publicar.
/// </summary>
public class FirmwarePayloadFidelityTests
{
    // Trama real de GLP_Presure/src/Infrastructure/Serialization/ArduinoJsonPayloadSerializer.cpp
    private const string GlpFirmwarePayload = """
    {
      "Device": {
        "ChipId": "A1B2C3D4E5F6",
        "ChipType": "ESP32-WROOM-32",
        "MacAddress": "A1:B2:C3:D4:E5:F6",
        "FirmwareVersion": "1.4.0",
        "DeviceId": "GLP-001",
        "IMEI": "",
        "IMSI": "",
        "status": "connected",
        "TypeConexion": "WIFI",
        "IPAddress": "192.168.1.44"
      },
      "Timestamp": "2026-10-23T14:05:00Z",
      "Details": {
        "sensors_raw":      { "level_mm": 812.0, "pressure_bar": 7.42, "temp_celsius": 29.4 },
        "sensors_filtered": { "level_mm": 809.5, "pressure_bar": 7.39, "temp_celsius": 29.2, "kalman_uncertainty_mm": 1.8 },
        "volume": {
          "liters": 1180.4, "gallons": 311.8, "percentage": 62.3,
          "density_kg_l": 0.508, "mass_kg": 599.6, "liters_15c": 1192.7, "anomaly": false
        },
        "tank": { "radius_cm": 60.0, "length_cm": 240.0 },
        "gps":  { "lat": 18.4861, "lng": -69.9312, "valid": true },
        "rssi": -58,
        "free_heap": 182344,
        "uptime": 73920,
        "health": { "ultrasonic": 100.0, "pressure": 92.5, "temperature": 100.0 },
        "vapor_check": { "p_measured": 7.39, "p_antoine": 7.51, "residual": -0.12 },
        "consumption": { "l_per_day": 23.7, "remaining_days": 49.8 },
        "acoustic": { "c_used_ms": 258.3, "source": "reflector", "healthy": true }
      }
    }
    """;

    [Fact]
    public void GlpPressureAdapter_MapsThePositionTheFirmwareActuallySends()
    {
        // El adaptador fijaba Position=null incondicionalmente, así que el tanque nunca
        // aparecía en el mapa pese a que el firmware envía Details.gps.{lat,lng,valid}.
        var result = new GlpPressureAdapter().Decode("glp/telemetria/GLP-001", GlpFirmwarePayload);

        result.IsSuccess.Should().BeTrue();
        var ev = result.Value;

        ev.Position.Should().NotBeNull();
        ev.Position!.Latitude.Should().Be(18.4861);
        ev.Position!.Longitude.Should().Be(-69.9312);
    }

    [Fact]
    public void GlpPressureAdapter_MapsConsumptionAndHealth_UsingTheFirmwareFieldNames()
    {
        // El adaptador leía Details.autonomy.{consumption_l_day,estimated_days} y
        // Details.diagnostics.{ultrasonic_health,...}; el firmware publica
        // Details.consumption.{l_per_day,remaining_days} y Details.health.{...}.
        var ev = new GlpPressureAdapter().Decode("glp/telemetria/GLP-001", GlpFirmwarePayload).Value;

        ev.NumericMetrics["consumption_l_day"].Should().Be(23.7);
        ev.NumericMetrics["estimated_autonomy_days"].Should().Be(49.8);
        ev.NumericMetrics["sensor_health_ultrasonic"].Should().Be(100.0);
        ev.NumericMetrics["sensor_health_pressure"].Should().Be(92.5);
        ev.NumericMetrics["sensor_health_temp"].Should().Be(100.0);
    }

    [Fact]
    public void GlpPressureAdapter_MapsEveryRemainingFirmwareField()
    {
        // Antes se descartaban por completo: rssi, free_heap, uptime, densidad, anomalía,
        // geometría del tanque, contraste con presión de vapor y modelo acústico.
        var ev = new GlpPressureAdapter().Decode("glp/telemetria/GLP-001", GlpFirmwarePayload).Value;

        ev.NumericMetrics["rssi"].Should().Be(-58);
        ev.NumericMetrics["free_heap"].Should().Be(182344);
        ev.NumericMetrics["uptime_s"].Should().Be(73920);
        ev.NumericMetrics["density_kg_l"].Should().Be(0.508);
        ev.NumericMetrics["volume_anomaly"].Should().Be(0);
        ev.NumericMetrics["tank_radius_cm"].Should().Be(60.0);
        ev.NumericMetrics["tank_length_cm"].Should().Be(240.0);
        ev.NumericMetrics["vapor_p_antoine_bar"].Should().Be(7.51);
        ev.NumericMetrics["vapor_residual_bar"].Should().BeApproximately(-0.12, 0.0001);
        ev.NumericMetrics["acoustic_sound_speed_ms"].Should().Be(258.3);
        ev.NumericMetrics["acoustic_healthy"].Should().Be(1);
        ev.Attributes["acoustic_source"].Should().Be("reflector");

        // Lo que ya funcionaba debe seguir funcionando.
        ev.NumericMetrics["level_mm"].Should().Be(809.5);
        ev.NumericMetrics["volume_liters"].Should().Be(1180.4);
        ev.NumericMetrics["fill_percentage"].Should().Be(62.3);
    }

    [Fact]
    public void GlpPressureAdapter_IgnoresTheZeroZeroPosition_OfAGpsWithoutFix()
    {
        string noFix = GlpFirmwarePayload.Replace(
            """ "gps":  { "lat": 18.4861, "lng": -69.9312, "valid": true }, """.Trim(),
            """ "gps":  { "lat": 0.0, "lng": 0.0, "valid": false }, """.Trim());

        var ev = new GlpPressureAdapter().Decode("glp/telemetria/GLP-001", noFix).Value;

        ev.Position.Should().BeNull("(0,0) es el valor por defecto de un GPS sin fix, no una posición");
    }

    // Trama real de FligoV0.0.1 (deviceConfigValidation), con la contraseña WiFi en claro:
    // References/Dispositivos/FligoV0.0.1/src/Infrastructure/Serialization/ArduinoJsonPayloadSerializer.cpp:106-123
    private const string FligoConfigPayload = """
    {
      "Device": { "ChipId": "CHIP123", "MacAddress": "AA:BB:CC:DD:EE:FF", "SIM_Status": "READY" },
      "Timestamp": "2026-10-23T14:10:00Z",
      "Details": {
        "DriverDni": "001-1234567-8",
        "TransportCode": "RUTA-27",
        "Language": "es",
        "NetworkSSID": "WIFI_FLOTA",
        "NetworkPassword": "secreto123",
        "Placa": "A123456",
        "Modelo": "Hiace",
        "Marca": "Toyota"
      }
    }
    """;

    [Fact]
    public void TicketValidatorAdapter_HandlesEveryFixedTopicTheFirmwarePublishes()
    {
        // El firmware publica en tres tópicos FIJOS (MqttTopics.h:28-30) y sólo se
        // reconocía "TicketValidationRequest"; los otros dos caían en el adaptador
        // genérico, que los volcaba sin semántica.
        var adapter = new TicketValidatorAdapter();

        adapter.CanHandle("TicketValidationRequest", FligoConfigPayload).Should().BeTrue();
        adapter.CanHandle("DeviceConfigValidationRequest", FligoConfigPayload).Should().BeTrue();
        adapter.CanHandle("ErrorUpdateFirmwareDevice", """{"Device":{"ChipId":"CHIP123"},"Details":{"IsError":true}}""")
            .Should().BeTrue();
    }

    [Theory]
    // Reportes del firmware ya con ámbito por dispositivo (MqttTopics.h de Fligo y Toour,
    // idénticos salvo el namespace). Antes eran tres tópicos FIJOS compartidos por TODOS los
    // equipos: cualquiera podía suscribirse y leer los reportes ajenos, incluido el de
    // configuración, que lleva la contraseña WiFi del cliente.
    [InlineData("devices/CHIP123/ticket-validation")]
    [InlineData("devices/CHIP123/config-validation")]
    [InlineData("devices/CHIP123/ota-error")]
    public void TicketValidatorAdapter_HandlesThePerDeviceReportTopics(string topic)
    {
        new TicketValidatorAdapter().CanHandle(topic, FligoConfigPayload).Should().BeTrue();
    }

    [Theory]
    // Equipos todavía sin re-flashear: sus tramas no deben perderse durante la transición.
    [InlineData("TicketValidationRequest")]
    [InlineData("DeviceConfigValidationRequest")]
    [InlineData("ErrorUpdateFirmwareDevice")]
    public void TicketValidatorAdapter_StillHandlesTheLegacyFixedTopics(string topic)
    {
        new TicketValidatorAdapter().CanHandle(topic, FligoConfigPayload).Should().BeTrue();
    }

    [Fact]
    public void TicketValidatorAdapter_IgnoresAnotherFirmwaresDeviceScopedTopic()
    {
        // No debe quedarse con cualquier cosa que cuelgue de devices/<id>/: el estado del
        // relé, por ejemplo, lo decodifica RelaySwitchAdapter.
        new TicketValidatorAdapter()
            .CanHandle("devices/CHIP123/device_switch1_state", "on")
            .Should().BeFalse();
    }

    [Fact]
    public void TelemetryRedaction_RemovesCredentialsFromAttributesAndRawPayload()
    {
        // La contraseña WiFi del cliente se guardaba en Attributes y, además, la trama
        // entera se persistía en la columna jsonb RawPayload y se devolvía en el histórico.
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NetworkSSID"] = "WIFI_FLOTA",
            ["NetworkPassword"] = "secreto123",
            ["api_key"] = "ak_live_123",
            ["gpio_pin"] = "17",
            ["pin"] = "4821"
        };

        TelemetryRedaction.RedactAttributes(attributes);

        attributes["NetworkPassword"].Should().Be("[REDACTED]");
        attributes["api_key"].Should().Be("[REDACTED]");
        attributes["pin"].Should().Be("[REDACTED]");
        attributes["NetworkSSID"].Should().Be("WIFI_FLOTA", "el nombre de la red no es un secreto");
        attributes["gpio_pin"].Should().Be("17", "un número de pin GPIO no es una credencial");

        var redacted = TelemetryRedaction.RedactPayload(FligoConfigPayload);
        redacted.Should().NotContain("secreto123");
        redacted.Should().Contain("[REDACTED]");
        redacted.Should().Contain("WIFI_FLOTA", "sólo se redacta el secreto, no el resto de la trama");
        redacted.Should().Contain("RUTA-27");
    }

    [Fact]
    public void TelemetryRedaction_LeavesNonJsonFramesUntouched()
    {
        // Tramas ASCII de trackers y volcados hexadecimales binarios: no son JSON y no
        // transportan credenciales; reescribirlas rompería el histórico.
        const string coban = "imei:353451044508750,tracker,0809231929,,F,112909.397,A,2234.4669,N,11354.3287,E,0.11,0;";
        TelemetryRedaction.RedactPayload(coban).Should().Be(coban);

        const string gt06 = "78780D01086471700328820200018CDD0D0A";
        TelemetryRedaction.RedactPayload(gt06).Should().Be(gt06);
    }

    // Trama real del firmware de accidentes, transcrita de
    // References/Dispositivos/GPS/src/application/services/AccidentService.cpp:30-39.
    // Tópico FIJO "telemetry/accidents": no lleva identificador, el ChipId va en "id".
    private const string GpsAccidentPayload = """
    {
      "type": "accident",
      "impact": true,
      "rollover": false,
      "id": "ESP8266-A1B2C3",
      "timestamp": "2026-10-23T14:20:30Z"
    }
    """;

    [Fact]
    public void GpsAccidentAdapter_TakesTheChipIdFromTheRootIdKey_NotFromTheFixedTopic()
    {
        // La plantilla "telemetry/{deviceId}" devolvía "accidents" como ChipId: cada
        // accidente se rechazaba por dispositivo no aprovisionado y escribía auditoría.
        var adapter = new GpsAccidentAdapter();

        adapter.CanHandle("telemetry/accidents", GpsAccidentPayload).Should().BeTrue();

        var ev = adapter.Decode("telemetry/accidents", GpsAccidentPayload).Value;

        ev.DeviceIdentifier.Should().Be("ESP8266-A1B2C3");
        ev.DeviceIdentifier.Should().NotBe("accidents");
        ev.RecordedAt.Should().Be(new DateTime(2026, 10, 23, 14, 20, 30, DateTimeKind.Utc));
    }

    [Fact]
    public void GpsAccidentAdapter_MapsTheFlatImpactAndRolloverFlags()
    {
        // El adaptador sólo leía Details.accident.{impact_detected,rollover_detected},
        // que este firmware nunca emite: la alerta llegaba sin semántica.
        var ev = new GpsAccidentAdapter().Decode("telemetry/accidents", GpsAccidentPayload).Value;

        ev.Attributes["event_type"].Should().Be("accident");
        ev.Attributes["impact_detected"].Should().Be("True");
        ev.Attributes["rollover_detected"].Should().Be("False");
        ev.NumericMetrics["impact_detected"].Should().Be(1);
        ev.NumericMetrics["rollover_detected"].Should().Be(0);
        ev.Attributes["alarm_code"].Should().Be("impact");
    }

    [Fact]
    public void GpsAccidentAdapter_FlagsRolloverAndCombinedAlarms()
    {
        var rollover = GpsAccidentPayload
            .Replace("\"impact\": true", "\"impact\": false")
            .Replace("\"rollover\": false", "\"rollover\": true");
        new GpsAccidentAdapter().Decode("telemetry/accidents", rollover).Value
            .Attributes["alarm_code"].Should().Be("rollover");

        var both = GpsAccidentPayload.Replace("\"rollover\": false", "\"rollover\": true");
        new GpsAccidentAdapter().Decode("telemetry/accidents", both).Value
            .Attributes["alarm_code"].Should().Be("impact_and_rollover");
    }

    [Fact]
    public void GpsAccidentAdapter_DoesNotInventADeviceId_WhenThePayloadCarriesNone()
    {
        // Antes caía en el marcador "Unknown-Device".
        string anonymous = """{"type":"accident","impact":true,"rollover":false}""";

        var ev = new GpsAccidentAdapter().Decode("telemetry/accidents", anonymous).Value;

        ev.DeviceIdentifier.Should().BeEmpty();
    }

    [Theory]
    [InlineData("on", 1.0)]
    [InlineData("off", 0.0)]
    [InlineData("1", 1.0)]
    [InlineData("0", 0.0)]
    [InlineData("true", 1.0)]
    [InlineData("false", 0.0)]
    public void RelaySwitchAdapter_DecodesThePlainTextStateTheFirmwarePublishes(string payload, double expected)
    {
        // El firmware publica la cadena DESNUDA "on"/"off" en "device_switch1_state"
        // (Relay_Wifi_Esp32/src/Application/Services/TelemetryService.cpp:42-45).
        // JsonDocument.Parse lanzaba excepción y cada conmutación del relé se perdía.
        var adapter = new RelaySwitchAdapter();

        adapter.CanHandle("device_switch1_state", payload).Should().BeTrue();

        var result = adapter.Decode("device_switch1_state", payload);

        result.IsSuccess.Should().BeTrue();
        result.Value.NumericMetrics["relay_state"].Should().Be(expected);
        result.Value.NumericMetrics["device_switch1_state_binary"].Should().Be(expected);
        result.Value.Attributes["device_switch1_state"].Should().Be(expected == 1.0 ? "on" : "off");
    }

    [Fact]
    public void RelaySwitchAdapter_LeavesIdentityToTheAuthenticatedConnection_ForGlobalTopics()
    {
        // Los tópicos de este firmware son globales y el payload es texto plano: no hay
        // ChipId en ninguna parte. Antes se inventaba el marcador "Relay-Switch".
        var ev = new RelaySwitchAdapter().Decode("device_switch1_state", "on").Value;

        ev.DeviceIdentifier.Should().BeEmpty();
    }

    [Fact]
    public void RelaySwitchAdapter_DecodesPeriodicTelemetry_WithTheFlatRelaysObject()
    {
        // La telemetría periódica usa Details.relays PLANO ("<id>": "on"), mientras el
        // mensaje de status usa Details.devices ANIDADO. Sólo se entendía el segundo, así
        // que la telemetría caía al fallback genérico y generaba métricas con otros nombres
        // para el MISMO equipo, partiendo sus series en dos.
        const string telemetry = """
        {
          "Device": { "ChipId": "RELAY-99", "MacAddress": "AA:BB:CC:00:11:22", "FirmwareVersion": "2.1.0" },
          "Timestamp": "2026-10-23T14:30:00Z",
          "Details": {
            "rssi": -61,
            "free_heap": 150120,
            "uptime": 48000,
            "relays": { "RELAY-99-01": "on", "RELAY-99-02": "off" }
          }
        }
        """;

        var adapter = new RelaySwitchAdapter();
        adapter.CanHandle("telemetry", telemetry).Should().BeTrue("el tópico es global; la marca está en el payload");

        var ev = adapter.Decode("telemetry", telemetry).Value;

        ev.DeviceIdentifier.Should().Be("RELAY-99");
        ev.NumericMetrics["RELAY-99-01_state_binary"].Should().Be(1.0);
        ev.NumericMetrics["RELAY-99-02_state_binary"].Should().Be(0.0);
        ev.Attributes["RELAY-99-01_state"].Should().Be("on");
        ev.NumericMetrics["rssi_dbm"].Should().Be(-61);
        ev.NumericMetrics["free_heap_bytes"].Should().Be(150120);
        ev.NumericMetrics["uptime_seconds"].Should().Be(48000);
    }

    [Fact]
    public void RelaySwitchAdapter_UsesTheSameMetricNames_ForStatusAndTelemetry()
    {
        // Las dos formas del mismo firmware deben producir la MISMA serie por relé.
        const string status = """
        {
          "Device": { "ChipId": "RELAY-99" },
          "Timestamp": "2026-10-23T14:31:00Z",
          "Details": {
            "sync_completed": true,
            "devices_count": 2,
            "wifi_status": "connected",
            "rssi": -61,
            "free_heap": 150120,
            "uptime": 48000,
            "devices": {
              "RELAY-99-01": { "state": "on", "desired_state": "on", "pin": 26 },
              "RELAY-99-02": { "state": "off", "desired_state": "off", "pin": 27 }
            }
          }
        }
        """;

        var ev = new RelaySwitchAdapter().Decode("status", status).Value;

        ev.NumericMetrics["RELAY-99-01_state_binary"].Should().Be(1.0);
        ev.NumericMetrics["RELAY-99-02_state_binary"].Should().Be(0.0);
        ev.NumericMetrics["RELAY-99-01_gpio_pin"].Should().Be(26);
        ev.NumericMetrics["devices_count"].Should().Be(2);
        ev.Attributes["sync_completed"].Should().Be("True");
        ev.Attributes["wifi_status"].Should().Be("connected");
    }

    [Fact]
    public void RelaySwitchAdapter_KeepsUnknownPlainTextInsteadOfLosingTheFrame()
    {
        var ev = new RelaySwitchAdapter().Decode("device_switch1/get", "status").Value;

        ev.Attributes["device_switch1_message"].Should().Be("status");
    }

    [Fact]
    public void GlpPressureAdapter_HandlesTheOfflineWillMessage()
    {
        // Will message retenido que el equipo deja en el broker:
        // GLP_Presure/src/Infrastructure/Messaging/mqtt_client.cpp:41-43.
        // El perfil sólo declaraba glp/telemetria/+, así que caía al fallback universal,
        // que además tomaba "status" como identificador (el id está en el TERCER segmento).
        const string will = """{"status":"offline","device_id":"GLP-001"}""";

        var adapter = new GlpPressureAdapter();
        adapter.CanHandle("glp/status/GLP-001", will).Should().BeTrue();

        var ev = adapter.Decode("glp/status/GLP-001", will).Value;

        ev.DeviceIdentifier.Should().Be("GLP-001", "el id va en device_id, no en el segundo segmento del tópico");
        ev.Attributes["connection_status"].Should().Be("offline");
        ev.NumericMetrics["online"].Should().Be(0);
    }

    [Fact]
    public void GlpPressureAdapter_DoesNotInventADeviceId_WhenTheTopicAndPayloadHaveNone()
    {
        // Antes caía en el marcador fijo "GLP-Device", que el pipeline trataba como un
        // dispositivo nuevo: rechazo + fila de auditoría por cada trama.
        string anonymous = """
        {
          "Timestamp": "2026-10-23T14:05:00Z",
          "Details": { "sensors_filtered": { "pressure_bar": 7.39 } }
        }
        """;

        var ev = new GlpPressureAdapter().Decode("glp/telemetria", anonymous).Value;

        ev.DeviceIdentifier.Should().BeEmpty();
    }
}
