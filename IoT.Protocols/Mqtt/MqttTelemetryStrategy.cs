using IoT.Domain.Common;
using IoT.Protocols.Contracts;
using IoT.Protocols.Mapping;
using IoT.Protocols.Models;
using IoT.Protocols.Routing;
using IoT.Protocols.Strategies;

namespace IoT.Protocols.Mqtt;

/// <summary>
/// Estrategia de telemetría para dispositivos que comunican mediante MQTT.
/// Procesa tópicos como devices/{deviceId}/telemetry, event/{chipId}, status/{chipId}, etc.
/// y utiliza AutoMapper para normalizar métricas planas y anidadas (Device / Details).
/// </summary>
public sealed class MqttTelemetryStrategy : ITelemetryStrategy
{
    public string StrategyName => "MqttTelemetryStrategy";
    public TransportFamily Transport => TransportFamily.Mqtt;

    public bool CanHandle(TransportMessage message)
    {
        // Si el transporte es explícitamente HTTP o TCP, esta estrategia NO debe procesarlo
        if (!string.IsNullOrWhiteSpace(message.TransportHint) &&
            (message.TransportHint.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
             message.TransportHint.StartsWith("tcp", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // 1. Verificar TransportHint / TransportProtocol
        if (!string.IsNullOrWhiteSpace(message.TransportHint) &&
            message.TransportHint.StartsWith("mqtt", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. Verificar si viene con metadata de topic MQTT
        if (message.Headers.ContainsKey("topic") || !string.IsNullOrWhiteSpace(message.TopicOrRoute))
        {
            if (message.TopicOrRoute.Contains('/') || message.TopicOrRoute.StartsWith("devices", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 3. Fallback: payload parece JSON con caracteres '{' (solo si no es HTTP/TCP)
        if (!string.IsNullOrWhiteSpace(message.Payload) && message.Payload.TrimStart().StartsWith('{'))
        {
            return true;
        }

        return false;
    }

    public Result<CanonicalDeviceTelemetry> Parse(TransportMessage message)
    {
        try
        {
            var payloadStr = message.Payload;
            var topic = message.Headers.TryGetValue("topic", out var t) ? t : message.TopicOrRoute;

            var deviceIdFromTopic = ExtractDeviceIdFromTopic(topic);
            var fallbackDeviceId = !string.IsNullOrWhiteSpace(deviceIdFromTopic)
                ? deviceIdFromTopic
                : message.DeviceIdentifier;

            var canonical = TelemetryAutoMapper.MapFromJson(payloadStr, fallbackDeviceId);

            // Si vino topic, agregarlo a los atributos
            if (!string.IsNullOrWhiteSpace(topic))
            {
                canonical.AdditionalAttributes["mqtt_topic"] = topic;
            }

            return Result<CanonicalDeviceTelemetry>.Success(canonical);
        }
        catch (Exception ex)
        {
            return Result<CanonicalDeviceTelemetry>.Failure(
                new Error("MqttTelemetryStrategy.ParseError", $"Error parseando telemetría MQTT: {ex.Message}"));
        }
    }

    // La identidad del tópico se resuelve en TopicIdentity, compartido con los
    // adaptadores: NO se devuelve el último segmento de un tópico desconocido, que
    // fabricaba identificadores basura ("progress", "get", "device_switch1_state").
    private static string? ExtractDeviceIdFromTopic(string topic) =>
        TopicIdentity.ExtractDeviceId(topic);
}
