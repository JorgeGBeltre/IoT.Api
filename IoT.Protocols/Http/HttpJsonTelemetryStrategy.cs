using IoT.Domain.Common;
using IoT.Protocols.Contracts;
using IoT.Protocols.Mapping;
using IoT.Protocols.Models;
using IoT.Protocols.Strategies;

namespace IoT.Protocols.Http;

/// <summary>
/// Estrategia para dispositivos HTTP que envían telemetría en formato JSON (REST / Webhooks).
/// </summary>
public sealed class HttpJsonTelemetryStrategy : ITelemetryStrategy
{
    public string StrategyName => "HttpJsonTelemetryStrategy";
    public TransportFamily Transport => TransportFamily.Http;

    public bool CanHandle(TransportMessage message)
    {
        var isHttp = !string.IsNullOrWhiteSpace(message.TransportHint) &&
                     message.TransportHint.StartsWith("http", StringComparison.OrdinalIgnoreCase);

        var isJson = !string.IsNullOrWhiteSpace(message.Payload) && message.Payload.TrimStart().StartsWith('{');

        return isHttp && isJson;
    }

    public Result<CanonicalDeviceTelemetry> Parse(TransportMessage message)
    {
        try
        {
            var payloadStr = message.Payload;

            // Obtener identificador desde headers HTTP o DeviceIdentifier
            var deviceId = message.Headers.TryGetValue("X-Device-Id", out var h1) ? h1 :
                           message.Headers.TryGetValue("X-Chip-Id", out var h2) ? h2 :
                           message.DeviceIdentifier;

            var canonical = TelemetryAutoMapper.MapFromJson(payloadStr, deviceId);

            if (message.Headers.TryGetValue("Content-Type", out var ct))
            {
                canonical.AdditionalAttributes["http_content_type"] = ct;
            }

            return Result<CanonicalDeviceTelemetry>.Success(canonical);
        }
        catch (Exception ex)
        {
            return Result<CanonicalDeviceTelemetry>.Failure(
                new Error("HttpJsonTelemetryStrategy.ParseError", $"Error parseando telemetría HTTP JSON: {ex.Message}"));
        }
    }
}
