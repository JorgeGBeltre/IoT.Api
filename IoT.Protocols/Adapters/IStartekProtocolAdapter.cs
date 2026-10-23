using System.Globalization;
using System.Text.Json;
using IoT.Domain.Common;
using IoT.Domain.ValueObjects;
using IoT.Protocols.Contracts;
using IoT.Protocols.Routing;

namespace IoT.Protocols.Adapters;

/// <summary>
/// Adaptador de protocolo para rastreadores iStartek:
/// VT100, VT100-L, VT200, VT200-L, VT900 y PT60.
/// Decodifica tramas ASCII que inician con && o $$:
/// &&<pack-no><len>,<ID>,000,<alm>,<data>,<MMddyy HH:mm:ss>,<A/V>,<lat>,<lon>,<sats>,<hdop>,<speed>,<course>,<alt>,<odo>,...
/// </summary>
public sealed class IStartekProtocolAdapter : IDeviceProtocolAdapter
{
    public string ProtocolName => "iStartek";

    private readonly string _model;

    public IStartekProtocolAdapter(string model = "VT100")
    {
        _model = model;
    }

    public bool CanHandle(TransportMessage message)
    {
        if (string.Equals(message.TransportProtocol, "TCP-ISTARTEK", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.TransportProtocol, "UDP-ISTARTEK", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Source, "istartek", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Source, _model, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return CanHandle(message.TopicOrRoute, message.Payload);
    }

    public bool CanHandle(string topic, string payload)
    {
        var lowerTopic = topic.ToLowerInvariant();
        if (lowerTopic.Contains("istartek") ||
            lowerTopic.Contains("vt100") ||
            lowerTopic.Contains("vt200") ||
            lowerTopic.Contains("vt900") ||
            lowerTopic.Contains("pt60"))
        {
            return true;
        }

        var trimmed = payload.Trim();
        if (trimmed.StartsWith("&&") || trimmed.StartsWith("$$"))
        {
            return true;
        }

        if (trimmed.StartsWith('{') && payload.Contains("istartek", StringComparison.OrdinalIgnoreCase))
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
                ["manufacturer"] = "iStartek",
                ["model"] = _model,
                ["protocol"] = "iStartek"
            };

            var trimmed = payload.Trim();

            // 1. Trama nativa ASCII iStartek (inicia con && o $$)
            if (trimmed.StartsWith("&&") || trimmed.StartsWith("$$"))
            {
                var clean = trimmed.TrimStart('&', '$').TrimEnd('\r', '\n');
                // Remover checksum final (*XX) si existe
                int starIdx = clean.LastIndexOf('*');
                if (starIdx > 0)
                {
                    clean = clean[..starIdx];
                }

                var parts = clean.Split(',');
                if (parts.Length >= 2)
                {
                    chipId = parts[1].Trim();
                }

                // Tokens estándar iStartek:
                // [0] pack-info
                // [1] ID / IMEI
                // [2] cmd (000)
                // [3] alm-code
                // [4] alm-data
                // [5] date-time (MMddyy HH:mm:ss)
                // [6] fix_flag (A/V)
                // [7] lat
                // [8] lon
                // [9] sat-quantity
                // [10] hdop
                // [11] speed
                // [12] course
                // [13] altitude
                // [14] odometer
                // [16] CSQ
                if (parts.Length >= 9)
                {
                    // Fecha y hora. La spec iStartek V1.6 es ambigua: el Apéndice A define
                    // 'YYMMDDHHmmss' (12 díg, GMT0) pero otra sección muestra 'MMDDYY hh:mm:ss'.
                    // Probamos ambos formatos (y la variante sin hora) para no perder la marca.
                    string dtStr = parts.Length > 5 ? parts[5].Trim() : string.Empty;
                    string[] dtFormats = { "yyMMddHHmmss", "MMddyy HH:mm:ss", "MMddyy" };
                    if (DateTime.TryParseExact(dtStr, dtFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDt))
                    {
                        recordedAt = parsedDt.ToUniversalTime();
                    }

                    bool isValid = parts.Length > 6 && string.Equals(parts[6].Trim(), "A", StringComparison.OrdinalIgnoreCase);

                    if (double.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                        double.TryParse(parts[8], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                    {
                        float speed = 0f;
                        if (parts.Length > 11 && float.TryParse(parts[11], NumberStyles.Float, CultureInfo.InvariantCulture, out float spd))
                            speed = spd;

                        float heading = 0f;
                        if (parts.Length > 12 && float.TryParse(parts[12], NumberStyles.Float, CultureInfo.InvariantCulture, out float hdg))
                            heading = hdg;

                        float? altitude = null;
                        if (parts.Length > 13 && float.TryParse(parts[13], NumberStyles.Float, CultureInfo.InvariantCulture, out float alt))
                            altitude = alt;

                        short? sats = null;
                        if (parts.Length > 9 && short.TryParse(parts[9], out short s))
                            sats = s;

                        var posRes = GeoPosition.Create(lat, lon, speed, heading, altitude, sats, isValid);
                        if (posRes.IsSuccess) position = posRes.Value;

                        metrics["speed_kph"] = speed;
                        metrics["heading_deg"] = heading;
                        if (altitude.HasValue) metrics["altitude_m"] = altitude.Value;
                        if (sats.HasValue) metrics["satellites"] = sats.Value;
                    }

                    // HDOP
                    if (parts.Length > 10 && double.TryParse(parts[10], NumberStyles.Float, CultureInfo.InvariantCulture, out double hdop))
                        metrics["hdop"] = hdop;

                    // Odómetro
                    if (parts.Length > 14 && double.TryParse(parts[14], NumberStyles.Float, CultureInfo.InvariantCulture, out double odo))
                        metrics["odometer_m"] = odo;

                    // CSQ
                    if (parts.Length > 16 && double.TryParse(parts[16], NumberStyles.Float, CultureInfo.InvariantCulture, out double csq))
                        metrics["csq"] = csq;

                    // Entrada ACC (in-sta en index 18)
                    if (parts.Length > 18)
                    {
                        var inSta = parts[18].Trim();
                        if (int.TryParse(inSta, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int inVal))
                        {
                            bool acc = (inVal & 0x01) != 0;
                            metrics["ignition_acc"] = acc ? 1.0 : 0.0;
                        }
                    }

                    // Voltajes (index 20). Formato spec: '<ext-V|bat-V|...>' → el PRIMER
                    // campo es el voltaje EXTERNO y el segundo el de BATERÍA (estaban invertidos).
                    if (parts.Length > 20 && parts[20].Contains('|'))
                    {
                        var vParts = parts[20].Split('|');
                        if (vParts.Length > 0 && double.TryParse(vParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double extV))
                            metrics["external_mv"] = extV;
                        if (vParts.Length > 1 && double.TryParse(vParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double batV))
                            metrics["battery_mv"] = batV;
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
            }

            if (string.IsNullOrEmpty(chipId))
                chipId = $"ISTARTEK-{_model.ToUpperInvariant()}-UNKNOWN";

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
            return Result<NormalizedTelemetryEvent>.Failure(new Error("IStartekProtocolAdapter.Error", ex.Message));
        }
    }

    private static string ExtractChipIdFromTopic(string topic)
    {
        if (TopicPatternMatcher.TryExtractParameters("istartek/{deviceId}/telemetry", topic, out var p1))
            return p1["deviceId"];
        if (TopicPatternMatcher.TryExtractParameters("tcp/istartek/{deviceId}", topic, out var p2))
            return p2["deviceId"];
        if (TopicPatternMatcher.TryExtractParameters("devices/{deviceId}/gps", topic, out var p3))
            return p3["deviceId"];
        return string.Empty;
    }
}
