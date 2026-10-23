using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using IoT.Domain.Common;
using IoT.Domain.ValueObjects;
using IoT.Protocols.Contracts;
using IoT.Protocols.Routing;

namespace IoT.Protocols.Adapters;

/// <summary>
/// Adaptador de protocolo para rastreadores Coban (TK103, TK303G, etc.).
/// Decodifica tramas ASCII estándar basadas en IMEI y formato NMEA/GPS:
/// imei:353451044508750,001,0809231929,13554900601,F,055403.000,A,2233.1870,N,11354.3067,E,...;
/// y paquetes de heartbeat (##,imei:...,A;).
/// </summary>
public sealed partial class CobanProtocolAdapter : IDeviceProtocolAdapter
{
    public string ProtocolName => "Coban";

    private readonly string _model;

    public CobanProtocolAdapter(string model = "TK103")
    {
        _model = model;
    }

    public bool CanHandle(TransportMessage message)
    {
        if (string.Equals(message.TransportProtocol, "TCP-COBAN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Source, "coban", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Source, _model, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return CanHandle(message.TopicOrRoute, message.Payload);
    }

    public bool CanHandle(string topic, string payload)
    {
        var lowerTopic = topic.ToLowerInvariant();
        if (lowerTopic.Contains("coban") ||
            lowerTopic.Contains("tk103") ||
            lowerTopic.Contains("tk303"))
        {
            return true;
        }

        var trimmed = payload.Trim();
        if (trimmed.StartsWith("imei:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("##,imei:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.StartsWith('{') && payload.Contains("coban", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public Result<NormalizedTelemetryEvent> Decode(TransportMessage message)
    {
        var result = Decode(message.TopicOrRoute, message.Payload);
        if (result.IsSuccess)
        {
            result.Value.MessageId = message.MessageId;
            result.Value.ReceivedAt = message.ReceivedAt;
        }
        return result;
    }

    public Result<NormalizedTelemetryEvent> Decode(string topic, string payload)
    {
        try
        {
            string chipId = ExtractChipIdFromTopic(topic);
            DateTime recordedAt = DateTime.UtcNow;
            GeoPosition? position = null;
            var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["manufacturer"] = "Coban",
                ["model"] = _model,
                ["protocol"] = "Coban"
            };

            var trimmed = payload.Trim();

            // 1. Trama nativa ASCII de Coban
            if (trimmed.StartsWith("imei:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("##,imei:", StringComparison.OrdinalIgnoreCase))
            {
                // Heartbeat / Login (e.g. ##,imei:353451044508750,A;)
                if (trimmed.StartsWith("##,imei:", StringComparison.OrdinalIgnoreCase))
                {
                    attributes["packet_type"] = "heartbeat";
                    var match = ImeiRegex().Match(trimmed);
                    if (match.Success)
                    {
                        chipId = match.Groups[1].Value;
                    }
                }
                // Trama de telemetría completa
                else
                {
                    var clean = trimmed.TrimEnd(';', '\r', '\n');
                    var parts = clean.Split(',');

                    // Extraer IMEI del primer token "imei:353451044508750"
                    if (parts.Length > 0 && parts[0].StartsWith("imei:", StringComparison.OrdinalIgnoreCase))
                    {
                        chipId = parts[0]["imei:".Length..].Trim();
                    }

                    // Tokens de GPS (formato Coban):
                    // [0] imei:...
                    // [1] keyword (001)
                    // [2] date (yyMMddHHmm)
                    // [3] phone
                    // [4] fix (F/L)
                    // [5] time (HHmmss.fff)
                    // [6] status (A/V)
                    // [7] lat (ddmm.mmmm)
                    // [8] N/S
                    // [9] lon (dddmm.mmmm)
                    // [10] E/W
                    // [11] speed (knots o km/h)
                    // [12] direction
                    // [13] altitude
                    // [14] ACC (1/0)
                    // [15] door (1/0)
                    // [16] fuel %
                    // [18] temperature
                    if (parts.Length >= 11)
                    {
                        bool isValid = parts.Length > 6 && string.Equals(parts[6], "A", StringComparison.OrdinalIgnoreCase);

                        // Marca de tiempo del DISPOSITIVO (antes se usaba siempre la de recepción):
                        // fecha[2]=yyMMddHHmm + hora[5]=HHmmss(.fff) → yyMMddHHmmss (UTC/GMT0).
                        string dPart = parts[2].Trim();
                        string tPart = parts.Length > 5 ? parts[5].Trim() : string.Empty;
                        if (dPart.Length >= 6 && tPart.Length >= 6 &&
                            DateTime.TryParseExact(dPart[..6] + tPart[..6], "yyMMddHHmmss",
                                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var cobanDt))
                        {
                            recordedAt = cobanDt.ToUniversalTime();
                        }

                        double lat = ParseNmeaCoordinate(parts[7], parts.Length > 8 ? parts[8] : "N");
                        double lon = ParseNmeaCoordinate(parts[9], parts.Length > 10 ? parts[10] : "E");

                        float speed = 0f;
                        if (parts.Length > 11 && float.TryParse(parts[11], NumberStyles.Float, CultureInfo.InvariantCulture, out float spd))
                        {
                            // Convertir de nudos a km/h
                            speed = (float)(spd * 1.852);
                        }

                        float heading = 0f;
                        if (parts.Length > 12 && float.TryParse(parts[12], NumberStyles.Float, CultureInfo.InvariantCulture, out float hdg))
                        {
                            heading = hdg;
                        }

                        float? altitude = null;
                        if (parts.Length > 13 && float.TryParse(parts[13], NumberStyles.Float, CultureInfo.InvariantCulture, out float alt))
                        {
                            altitude = alt;
                        }

                        var posRes = GeoPosition.Create(lat, lon, speed, heading, altitude, null, isValid);
                        if (posRes.IsSuccess)
                        {
                            position = posRes.Value;
                        }

                        metrics["speed_kph"] = speed;
                        metrics["heading_deg"] = heading;
                        if (altitude.HasValue) metrics["altitude_m"] = altitude.Value;

                        // ACC
                        if (parts.Length > 14 && int.TryParse(parts[14], out int acc))
                        {
                            metrics["ignition_acc"] = acc == 1 ? 1.0 : 0.0;
                        }

                        // Door
                        if (parts.Length > 15 && int.TryParse(parts[15], out int door))
                        {
                            metrics["door_open"] = door == 1 ? 1.0 : 0.0;
                        }

                        // Fuel %
                        if (parts.Length > 16)
                        {
                            var fuelStr = parts[16].Replace("%", "").Trim();
                            if (double.TryParse(fuelStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double fuel))
                            {
                                metrics["fuel_pct"] = fuel;
                            }
                        }

                        // Temperature
                        if (parts.Length > 18 && double.TryParse(parts[18], NumberStyles.Float, CultureInfo.InvariantCulture, out double temp))
                        {
                            metrics["temperature_c"] = temp;
                        }
                    }
                }
            }
            // 2. Formato JSON (Bridge)
            else if (trimmed.StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                if (string.IsNullOrEmpty(chipId))
                {
                    if (root.TryGetProperty("imei", out var c1)) chipId = c1.GetString() ?? string.Empty;
                    else if (root.TryGetProperty("chipId", out var c2)) chipId = c2.GetString() ?? string.Empty;
                }

                if (root.TryGetProperty("latitude", out var lt) && lt.TryGetDouble(out double lat) &&
                    root.TryGetProperty("longitude", out var ln) && ln.TryGetDouble(out double lon))
                {
                    float? speed = root.TryGetProperty("speed", out var sp) && sp.TryGetSingle(out float s) ? s : null;
                    float? heading = root.TryGetProperty("heading", out var hd) && hd.TryGetSingle(out float h) ? h : null;
                    var posRes = GeoPosition.Create(lat, lon, speed, heading, null, null, true);
                    if (posRes.IsSuccess) position = posRes.Value;
                    if (speed.HasValue) metrics["speed_kph"] = speed.Value;
                    if (heading.HasValue) metrics["heading_deg"] = heading.Value;
                }

                if (root.TryGetProperty("acc", out var acc))
                    metrics["ignition_acc"] = acc.GetInt32() == 1 ? 1.0 : 0.0;
            }

            if (string.IsNullOrEmpty(chipId))
                chipId = $"COBAN-{_model.ToUpperInvariant()}-UNKNOWN";

            return new NormalizedTelemetryEvent
            {
                DeviceIdentifier = chipId,
                RecordedAt = recordedAt,
                ReceivedAt = DateTime.UtcNow,
                Position = position,
                NumericMetrics = metrics,
                Attributes = attributes,
                RawPayload = payload
            };
        }
        catch (Exception ex)
        {
            return Result<NormalizedTelemetryEvent>.Failure(new Error("CobanProtocolAdapter.Error", ex.Message));
        }
    }

    private static double ParseNmeaCoordinate(string value, string hemisphere)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0.0;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double raw))
            return 0.0;

        // Si ya viene en formato decimal (-180 a 180 con punto normal)
        if (raw >= -180.0 && raw <= 180.0 && !value.Contains('.'))
            return raw;

        // Formato ddmm.mmmm o dddmm.mmmm
        double degrees = Math.Floor(raw / 100.0);
        double minutes = raw - (degrees * 100.0);
        double decimalVal = degrees + (minutes / 60.0);

        if (string.Equals(hemisphere, "S", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(hemisphere, "W", StringComparison.OrdinalIgnoreCase))
        {
            decimalVal = -decimalVal;
        }

        return decimalVal;
    }

    private static string ExtractChipIdFromTopic(string topic)
    {
        if (TopicPatternMatcher.TryExtractParameters("coban/{deviceId}/telemetry", topic, out var p1))
            return p1["deviceId"];
        if (TopicPatternMatcher.TryExtractParameters("tcp/coban/{deviceId}", topic, out var p2))
            return p2["deviceId"];
        if (TopicPatternMatcher.TryExtractParameters("devices/{deviceId}/gps", topic, out var p3))
            return p3["deviceId"];
        return string.Empty;
    }

    [GeneratedRegex(@"imei:(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ImeiRegex();
}
