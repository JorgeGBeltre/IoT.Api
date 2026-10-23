using System.Web;
using IoT.Domain.Common;
using IoT.Protocols.Contracts;
using IoT.Protocols.Mapping;
using IoT.Protocols.Models;
using IoT.Protocols.Strategies;

namespace IoT.Protocols.Http;

/// <summary>
/// Estrategia para dispositivos HTTP que transmiten telemetría mediante parámetros URL-encoded o query string
/// (común en localizadores GPS y módulos IoT de bajo consumo que usan HTTP GET/POST).
/// </summary>
public sealed class HttpUrlEncodedTelemetryStrategy : ITelemetryStrategy
{
    public string StrategyName => "HttpUrlEncodedTelemetryStrategy";
    public TransportFamily Transport => TransportFamily.Http;

    public bool CanHandle(TransportMessage message)
    {
        var isHttp = !string.IsNullOrWhiteSpace(message.TransportHint) &&
                     message.TransportHint.StartsWith("http", StringComparison.OrdinalIgnoreCase);

        var payloadStr = message.Payload?.Trim() ?? string.Empty;
        var isUrlEncoded = payloadStr.Contains('=') && !payloadStr.StartsWith('{') && !payloadStr.StartsWith('<');

        return isHttp && isUrlEncoded;
    }

    public Result<CanonicalDeviceTelemetry> Parse(TransportMessage message)
    {
        try
        {
            var payloadStr = message.Payload ?? string.Empty;
            var parsed = HttpUtility.ParseQueryString(payloadStr);

            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (string? key in parsed)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                var val = parsed[key];
                if (val != null) dict[key] = val;
            }

            var canonical = new CanonicalDeviceTelemetry();
            TelemetryAutoMapper.PopulateFromDictionary(canonical, dict);

            if (string.IsNullOrWhiteSpace(canonical.ClientId) &&
                string.IsNullOrWhiteSpace(canonical.ChipId) &&
                !string.IsNullOrWhiteSpace(message.DeviceIdentifier))
            {
                canonical.ClientId = message.DeviceIdentifier;
            }

            return Result<CanonicalDeviceTelemetry>.Success(canonical);
        }
        catch (Exception ex)
        {
            return Result<CanonicalDeviceTelemetry>.Failure(
                new Error("HttpUrlEncodedTelemetryStrategy.ParseError", $"Error parseando trama URL-encoded HTTP: {ex.Message}"));
        }
    }
}
