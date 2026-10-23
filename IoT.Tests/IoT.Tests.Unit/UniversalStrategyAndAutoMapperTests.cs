using System.Text.Json;
using FluentAssertions;
using IoT.Protocols.Adapters;
using IoT.Protocols.Contracts;
using IoT.Protocols.Http;
using IoT.Protocols.Mapping;
using IoT.Protocols.Models;
using IoT.Protocols.Mqtt;
using IoT.Protocols.Strategies;
using IoT.Protocols.Tcp;
using Xunit;

namespace IoT.Tests.Unit;

public class UniversalStrategyAndAutoMapperTests
{
    private readonly StrategyBasedProtocolAdapter _adapter = new();

    // =========================================================================
    // 1. Verificación exhaustiva de parámetros de Jorge vía AutoMapper
    // =========================================================================

    [Fact]
    public void AutoMapper_MapsAllUserSpecifiedParameters_FromSpanishAndEnglishJson()
    {
        // JSON que combina los parámetros exactos solicitados por Jorge:
        // Voltaje, latitude, longitude, speed, satellites, timestamp, isValid,
        // chipId, chipType, macAddress, clientID, firmwareVersion,
        // impact_detected, rollover_detected, impact_magnitude, roll_angle, pitch_angle, detection_time
        string payload = """
        {
          "chipId": "ESP32-CHIP-9988",
          "chipType": "ESP32-WROOM-32D",
          "macAddress": "24:6F:28:B2:A1:C0",
          "clientID": "CLIENT-CORP-42",
          "firmwareVersion": "v2.4.1",
          "timestamp": "2026-10-22T13:00:00Z",
          "latitude": 18.4861,
          "longitude": -69.9312,
          "altitude": 14.5,
          "speed": 62.4,
          "course": 95.0,
          "satellites": 11,
          "isValid": true,
          "Voltaje": 13.8,
          "battery": 95.5,
          "impact_detected": true,
          "rollover_detected": false,
          "impact_magnitude": 1850.7,
          "roll_angle": 12.3,
          "pitch_angle": 4.1,
          "detection_time": 1729602000,
          "temperature": 28.5,
          "pressure": 1013.25,
          "relayState": true
        }
        """;

        var canonical = TelemetryAutoMapper.MapFromJson(payload);

        // Aserciones de Device Info
        canonical.ChipId.Should().Be("ESP32-CHIP-9988");
        canonical.ChipType.Should().Be("ESP32-WROOM-32D");
        canonical.MacAddress.Should().Be("24:6F:28:B2:A1:C0");
        canonical.ClientId.Should().Be("CLIENT-CORP-42");
        canonical.FirmwareVersion.Should().Be("v2.4.1");

        // Aserciones GPS
        canonical.Latitude.Should().Be(18.4861);
        canonical.Longitude.Should().Be(-69.9312);
        canonical.Altitude.Should().Be(14.5f);
        canonical.Speed.Should().Be(62.4f);
        canonical.Course.Should().Be(95.0f);
        canonical.Satellites.Should().Be(11);
        canonical.IsValid.Should().BeTrue();
        canonical.Timestamp.Should().Be(new DateTime(2026, 10, 22, 13, 0, 0, DateTimeKind.Utc));

        // Aserciones Eléctricas & Batería
        canonical.Voltage.Should().Be(13.8);
        canonical.Battery.Should().Be(95.5);

        // Aserciones de Accidentes & Cinemática
        canonical.ImpactDetected.Should().BeTrue();
        canonical.RolloverDetected.Should().BeFalse();
        canonical.ImpactMagnitude.Should().Be(1850.7f);
        canonical.RollAngle.Should().Be(12.3f);
        canonical.PitchAngle.Should().Be(4.1f);
        canonical.DetectionTime.Should().Be(1729602000ul);

        // Aserciones de Sensores y Actuadores
        canonical.Temperature.Should().Be(28.5);
        canonical.Pressure.Should().Be(1013.25);
        canonical.RelayState.Should().BeTrue();

        // Conversión a NormalizedTelemetryEvent para dominio
        var normalized = canonical.ToNormalizedEvent("fallback-id");
        // El ChipId manda sobre el ClientId: es la clave con la que el pipeline resuelve el
        // dispositivo (índice único global sobre ChipId). Con la precedencia anterior, una
        // trama con ambos se buscaba por el ClientId y se rechazaba como no aprovisionada.
        normalized.DeviceIdentifier.Should().Be("ESP32-CHIP-9988");
        normalized.Position.Should().NotBeNull();
        normalized.Position!.Latitude.Should().Be(18.4861);
        normalized.Position!.Longitude.Should().Be(-69.9312);
        // Orden de argumentos de GeoPosition.Create: antes se pasaban por posición y la
        // altitud entraba como velocidad, la velocidad como rumbo y el rumbo como altitud.
        normalized.Position!.SpeedKph.Should().Be(62.4f);
        normalized.Position!.Heading.Should().Be(95.0f);
        normalized.Position!.AltitudeM.Should().Be(14.5f);
        normalized.Position!.Satellites.Should().Be(11);
        normalized.Position!.IsValid.Should().BeTrue();
        normalized.NumericMetrics["voltage"].Should().Be(13.8);
        normalized.NumericMetrics["impact_magnitude"].Should().BeApproximately(1850.7, 0.01);
        normalized.NumericMetrics["relay_state"].Should().Be(1.0);
        normalized.Attributes["impact_detected"].Should().Be("True");
        normalized.Attributes["mac_address"].Should().Be("24:6F:28:B2:A1:C0");
    }

    [Fact]
    public void AutoMapper_MapsDirectTypesHStructures()
    {
        var devInfo = new DeviceInfoDto
        {
            ChipId = "ESP-8266-XYZ",
            ChipType = "ESP8266",
            MacAddress = "AA:BB:CC:11:22:33",
            ClientID = "FLEET-001",
            FirmwareVersion = "v1.0.0"
        };

        var gpsData = new GpsDataDto
        {
            Latitude = 18.5,
            Longitude = -69.9,
            Altitude = 20.0f,
            Speed = 45.0f,
            Course = 180.0f,
            Satellites = 8,
            IsValid = true,
            Timestamp = "2026-10-22T14:30:00Z"
        };

        var accident = new AccidentDataDto
        {
            Impact_detected = true,
            Rollover_detected = false,
            Impact_magnitude = 3120.4f,
            Roll_angle = 18.5f,
            Pitch_angle = 6.2f,
            Detection_time = 1729603800
        };

        var canonical = TelemetryAutoMapper.MapToCanonical(devInfo);
        TelemetryAutoMapper.Mapper.Map(gpsData, canonical);
        TelemetryAutoMapper.Mapper.Map(accident, canonical);

        canonical.ChipId.Should().Be("ESP-8266-XYZ");
        canonical.ClientId.Should().Be("FLEET-001");
        canonical.Latitude.Should().Be(18.5);
        canonical.Speed.Should().Be(45.0f);
        canonical.ImpactDetected.Should().BeTrue();
        canonical.ImpactMagnitude.Should().Be(3120.4f);
        canonical.DetectionTime.Should().Be(1729603800ul);
    }

    [Fact]
    public void CanonicalTelemetry_PreservesPosition_WhenAltitudeIsNegativeOrCourseOutOfRange()
    {
        // Ruido GPS real: altitud bajo el nivel del mar y rumbo fuera de [0,360].
        // Antes, al pasar los argumentos por posición, la altitud negativa entraba como
        // velocidad y GeoPosition.Create fallaba ('Speed cannot be negative'), con lo que
        // se descartaba la posición COMPLETA (lat/lon incluidos) en silencio.
        var canonical = new CanonicalDeviceTelemetry
        {
            ChipId = "TRACKER-NEG-ALT",
            Latitude = 18.47,
            Longitude = -69.91,
            Altitude = -5.0f,
            Speed = 40.0f,
            Course = 400.0f,
            Satellites = 9,
            IsValid = true
        };

        var normalized = canonical.ToNormalizedEvent();

        normalized.Position.Should().NotBeNull();
        normalized.Position!.Latitude.Should().Be(18.47);
        normalized.Position!.AltitudeM.Should().Be(-5.0f);
        normalized.Position!.SpeedKph.Should().Be(40.0f);
        normalized.Position!.Heading.Should().Be(40.0f, "el rumbo se normaliza en módulo 360");
    }

    [Fact]
    public void CanonicalTelemetry_OmitsImpossibleSpeed_ButKeepsTheRestOfThePosition()
    {
        var canonical = new CanonicalDeviceTelemetry
        {
            ChipId = "TRACKER-NEG-SPEED",
            Latitude = 18.47,
            Longitude = -69.91,
            Speed = -12.0f,
            IsValid = false
        };

        var normalized = canonical.ToNormalizedEvent();

        normalized.Position.Should().NotBeNull();
        normalized.Position!.SpeedKph.Should().BeNull("una velocidad negativa es imposible, pero no invalida lat/lon");
        normalized.Position!.IsValid.Should().BeFalse("un fix GPS inválido no debe difundirse como válido");
    }

    // =========================================================================
    // 2. Estrategia MQTT: Tópicos y payloads anidados
    // =========================================================================

    [Theory]
    // <prefijo>/<id> y <prefijo>/<id>/<sufijo>
    [InlineData("telemetry/ESP32-AABBCC", "ESP32-AABBCC")]
    [InlineData("devices/DEV-1/telemetry", "DEV-1")]
    // <prefijo>/<sub>/<id>: el identificador NO está en el segundo segmento
    [InlineData("glp/telemetria/GLP-001", "GLP-001")]
    [InlineData("glp/status/GLP-001", "GLP-001")]
    [InlineData("glp/config/GLP-001", "GLP-001")]
    // OTA: el id va en medio, el sufijo es el tipo de reporte
    [InlineData("ota/A1B2C3D4E5F6/progress", "A1B2C3D4E5F6")]
    [InlineData("ota/A1B2C3D4E5F6/status", "A1B2C3D4E5F6")]
    public void MqttStrategy_ExtractsDeviceIdFromTopic_EvenWhenItIsNotTheSecondSegment(
        string topic, string expectedDeviceId)
    {
        var msg = TransportMessage.FromMqtt(topic: topic, payload: """{"temperature": 21.0}""");

        var result = new MqttTelemetryStrategy().Parse(msg);

        result.IsSuccess.Should().BeTrue();
        result.Value.ToNormalizedEvent().DeviceIdentifier.Should().Be(expectedDeviceId);
    }

    [Theory]
    // Tópicos FIJOS sin identidad: el último segmento es una palabra de protocolo.
    [InlineData("telemetry/accidents")]
    // Tópico de un solo segmento (relé: "device_switch1_state" con payload "on"/"off").
    [InlineData("device_switch1_state")]
    // Prefijo desconocido: no se puede suponer dónde está el identificador.
    [InlineData("foo/bar/baz")]
    public void MqttStrategy_DoesNotInventADeviceId_WhenTheTopicCarriesNoIdentity(string topic)
    {
        // Devolver el último segmento fabricaba dispositivos fantasma ('accidents',
        // 'progress', 'baz') que el pipeline rechazaba por "no aprovisionado", escribiendo
        // una fila de auditoría por cada trama. Sin identidad en el tópico ni en el payload,
        // la identidad debe venir de la conexión autenticada (TransportDeviceIdentifier).
        var msg = TransportMessage.FromMqtt(topic: topic, payload: """{"temperature": 21.0}""");

        var result = new MqttTelemetryStrategy().Parse(msg);

        result.IsSuccess.Should().BeTrue();
        var deviceId = result.Value.ToNormalizedEvent().DeviceIdentifier;
        deviceId.Should().NotBe(topic.Split('/')[^1]);
        deviceId.Should().Be("unknown-device", "sin identidad fiable queda el marcador que el pipeline rechaza");
    }

    [Fact]
    public void MqttStrategy_ProcessesNestedDeviceDetailsPayload_AndExtractsTopicChipId()
    {
        var msg = TransportMessage.FromMqtt(
            topic: "devices/DEVICE-ESP32-MQTT/telemetry",
            payload: """
            {
              "Device": {
                "ChipId": "DEVICE-ESP32-MQTT",
                "FirmwareVersion": "3.0.1"
              },
              "Timestamp": "2026-10-22T12:00:00Z",
              "Details": {
                "voltage": 12.6,
                "current": 2.1,
                "power": 26.46,
                "speed": 75.0,
                "latitude": 18.4900,
                "longitude": -69.9400
              }
            }
            """);

        var strategy = new MqttTelemetryStrategy();
        strategy.CanHandle(msg).Should().BeTrue();

        var result = strategy.Parse(msg);
        result.IsSuccess.Should().BeTrue();

        var canonical = result.Value;
        canonical.ChipId.Should().Be("DEVICE-ESP32-MQTT");
        canonical.Voltage.Should().Be(12.6);
        canonical.Current.Should().Be(2.1);
        canonical.Power.Should().Be(26.46);
        canonical.Speed.Should().Be(75.0f);
        canonical.Latitude.Should().Be(18.4900);
        canonical.Longitude.Should().Be(-69.9400);
    }

    // =========================================================================
    // 3. Estrategias HTTP: JSON, URL-Encoded y Tramas Delimitadas
    // =========================================================================

    [Fact]
    public void HttpStrategy_ProcessesJsonWebhook_WithHeaders()
    {
        var headers = new Dictionary<string, string>
        {
            ["X-Device-Id"] = "HTTP-TRACKER-01",
            ["Content-Type"] = "application/json"
        };

        var msg = TransportMessage.FromHttp(
            route: "/api/v1/telemetry/ingest",
            payload: """
            {
              "lat": 18.4500,
              "lon": -69.9100,
              "speed": 48.5,
              "voltaje": 11.9,
              "bateria": 78.0
            }
            """,
            headers: headers,
            deviceId: "HTTP-TRACKER-01");

        var strategy = new HttpJsonTelemetryStrategy();
        strategy.CanHandle(msg).Should().BeTrue();

        var result = strategy.Parse(msg);
        result.IsSuccess.Should().BeTrue();

        var canonical = result.Value;
        canonical.ClientId.Should().Be("HTTP-TRACKER-01");
        canonical.Latitude.Should().Be(18.4500);
        canonical.Longitude.Should().Be(-69.9100);
        canonical.Speed.Should().Be(48.5f);
        canonical.Voltage.Should().Be(11.9);
        canonical.Battery.Should().Be(78.0);
    }

    [Fact]
    public void HttpStrategy_ProcessesUrlEncodedQueryParams_UsedByGpsTrackers()
    {
        // Muchos GPS comerciales transmiten HTTP GET/POST con query params URL-encoded:
        string urlEncodedPayload = "chipId=GPS-URL-55&latitude=18.4861&longitude=-69.9312&speed=80.5&voltaje=24.2&satellites=9&isValid=true";

        var msg = TransportMessage.FromHttp(
            route: "/api/v1/tracker/gps",
            payload: urlEncodedPayload);

        var strategy = new HttpUrlEncodedTelemetryStrategy();
        strategy.CanHandle(msg).Should().BeTrue();

        var result = strategy.Parse(msg);
        result.IsSuccess.Should().BeTrue();

        var canonical = result.Value;
        canonical.ChipId.Should().Be("GPS-URL-55");
        canonical.Latitude.Should().Be(18.4861);
        canonical.Longitude.Should().Be(-69.9312);
        canonical.Speed.Should().Be(80.5f);
        canonical.Voltage.Should().Be(24.2);
        canonical.Satellites.Should().Be(9);
        canonical.IsValid.Should().BeTrue();
    }

    [Fact]
    public void HttpStrategy_ProcessesCustomDelimitedTrackerFrame()
    {
        // Tramas separadas por comas enviadas en body HTTP:
        // IMEI, TIMESTAMP, LAT, LON, SPEED, COURSE, SATS, VOLT
        string customFrame = "867530900123456,2026-10-22T13:15:00Z,18.4720,-69.9250,55.0,210.0,12,12.8";

        var msg = TransportMessage.FromHttp(
            route: "/api/v1/device/frame",
            payload: customFrame);

        var strategy = new HttpCustomFrameTelemetryStrategy();
        strategy.CanHandle(msg).Should().BeTrue();

        var result = strategy.Parse(msg);
        result.IsSuccess.Should().BeTrue();

        var canonical = result.Value;
        canonical.Imei.Should().Be("867530900123456");
        canonical.Latitude.Should().Be(18.4720);
        canonical.Longitude.Should().Be(-69.9250);
        canonical.Speed.Should().Be(55.0f);
        canonical.Course.Should().Be(210.0f);
        canonical.Satellites.Should().Be(12);
        canonical.Voltage.Should().Be(12.8);
    }

    // =========================================================================
    // 4. Adaptador Universal: Autodetección y Despacho Multi-Transporte
    // =========================================================================

    [Fact]
    public void UniversalAdapter_DispatchesMqttAndHttpSeamlessly_WithoutModelSpecificAdapters()
    {
        // 1. Mensaje MQTT de sensor nuevo no catalogado previamente
        var mqttMsg = TransportMessage.FromMqtt(
            topic: "sensors/new-industrial-flow/telemetry",
            payload: """
            {
              "chipId": "FLOW-SENSOR-900",
              "voltage": 24.0,
              "temperature": 45.2,
              "pressure": 5.8,
              "flow_rate": 120.5,
              "volume": 9850.0
            }
            """);

        _adapter.CanHandle(mqttMsg).Should().BeTrue();
        var mqttResult = _adapter.Decode(mqttMsg);
        mqttResult.IsSuccess.Should().BeTrue();
        var mqttEvent = mqttResult.Value;
        mqttEvent.DeviceIdentifier.Should().Be("FLOW-SENSOR-900");
        mqttEvent.NumericMetrics["voltage"].Should().Be(24.0);
        mqttEvent.NumericMetrics["flow_rate"].Should().Be(120.5);
        mqttEvent.Attributes["ingestion_strategy"].Should().Be("MqttTelemetryStrategy");
        mqttEvent.Attributes["transport_family"].Should().Be("Mqtt");

        // 2. Mensaje HTTP de nuevo dispositivo de control de acceso
        var httpMsg = TransportMessage.FromHttp(
            route: "/api/v1/access-control",
            payload: """
            {
              "chipId": "DOOR-CONTROLLER-4",
              "card_id": "RFID-887766",
              "access_granted": true,
              "voltage": 12.1
            }
            """);

        _adapter.CanHandle(httpMsg).Should().BeTrue();
        var httpResult = _adapter.Decode(httpMsg);
        httpResult.IsSuccess.Should().BeTrue();
        var httpEvent = httpResult.Value;
        httpEvent.DeviceIdentifier.Should().Be("DOOR-CONTROLLER-4");
        httpEvent.Attributes["card_id"].Should().Be("RFID-887766");
        httpEvent.Attributes["access_granted"].Should().Be("True");
        httpEvent.NumericMetrics["voltage"].Should().Be(12.1);
        httpEvent.Attributes["ingestion_strategy"].Should().Be("HttpJsonTelemetryStrategy");
        httpEvent.Attributes["transport_family"].Should().Be("Http");
    }
}
