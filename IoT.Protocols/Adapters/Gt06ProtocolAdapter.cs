using System.Globalization;
using System.Text.Json;
using IoT.Domain.Common;
using IoT.Domain.ValueObjects;
using IoT.Protocols.Binary;
using IoT.Protocols.Contracts;
using IoT.Protocols.Routing;

namespace IoT.Protocols.Adapters;

/// <summary>
/// Adaptador de protocolo universal para la familia GT06 y sus variantes:
/// SEEWORLD (R12L-F, R11), Concox (GT06N, TR06), Benway (BW02, BW08, BW09, ET300),
/// X3Tech (NT20) y Mictrack (M558G-V2).
/// Soporta tramas binarias directas (0x78 0x78 / 0x79 0x79), hexadecimales y JSON bridge.
/// </summary>
public sealed class Gt06ProtocolAdapter : IDeviceProtocolAdapter
{
    public string ProtocolName => "GT06";

    private readonly string _manufacturer;
    private readonly string _model;

    public Gt06ProtocolAdapter(string manufacturer = "SEEWORLD", string model = "GT06")
    {
        _manufacturer = manufacturer;
        _model = model;
    }

    public bool CanHandle(TransportMessage message)
    {
        if (string.Equals(message.TransportProtocol, "TCP-GT06", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.TransportProtocol, "TCP-R12LF", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Source, "gt06", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Source, _model, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Source, _manufacturer, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return CanHandle(message.TopicOrRoute, message.Payload);
    }

    public bool CanHandle(string topic, string payload)
    {
        var lowerTopic = topic.ToLowerInvariant();
        if (lowerTopic.Contains("gt06") ||
            lowerTopic.Contains("seeworld") ||
            lowerTopic.Contains("r12") ||
            lowerTopic.Contains("r11") ||
            lowerTopic.Contains("concox") ||
            lowerTopic.Contains("tr06") ||
            lowerTopic.Contains("benway") ||
            lowerTopic.Contains("bw0") ||
            lowerTopic.Contains("et300") ||
            lowerTopic.Contains("x3tech") ||
            lowerTopic.Contains("nt20") ||
            lowerTopic.Contains("mictrack") ||
            lowerTopic.Contains("m558"))
        {
            return true;
        }

        var trimmed = payload.Trim();
        if (trimmed.StartsWith("7878", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("7979", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.StartsWith('{'))
        {
            return payload.Contains("gt06", StringComparison.OrdinalIgnoreCase) ||
                   payload.Contains("r12l", StringComparison.OrdinalIgnoreCase) ||
                   payload.Contains("r11", StringComparison.OrdinalIgnoreCase) ||
                   payload.Contains("concox", StringComparison.OrdinalIgnoreCase) ||
                   payload.Contains("benway", StringComparison.OrdinalIgnoreCase) ||
                   payload.Contains("mictrack", StringComparison.OrdinalIgnoreCase);
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
                ["manufacturer"] = _manufacturer,
                ["model"] = _model,
                ["protocol"] = "GT06"
            };

            var trimmed = payload.Trim();

            // 1. Trama hexadecimal de GT06 (7878...0D0A)
            if (trimmed.StartsWith("7878", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("7979", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    byte[] binaryData = Convert.FromHexString(trimmed);
                    if (R12LFFrame.TryParse(binaryData, out var frame, out _) && frame != null)
                    {
                        if (frame.ProtocolNumber == R12LFFrame.ProtocolLogin)
                        {
                            chipId = frame.ExtractImeiFromLoginPacket();
                            attributes["packet_type"] = "login";
                        }
                        else if (frame.ProtocolNumber == R12LFFrame.ProtocolLocation ||
                                 frame.ProtocolNumber == R12LFFrame.ProtocolAlarm ||
                                 frame.ProtocolNumber == 0x22 ||
                                 frame.ProtocolNumber == 0x26)
                        {
                            byte canonical = (frame.ProtocolNumber == 0x22) ? R12LFFrame.ProtocolLocation :
                                             (frame.ProtocolNumber == 0x26) ? R12LFFrame.ProtocolAlarm :
                                             frame.ProtocolNumber;
                            // #5: se conserva la trama aunque no haya fix (posición marcada
                            // IsValid=false) y se extrae la celda LBS. #3: alarm_type del 0x16.
                            var (pos, time, extras) = frame.DecodeLocationPacketDetailed(canonical);
                            position = pos;
                            recordedAt = time;
                            bool isAlarm = canonical == R12LFFrame.ProtocolAlarm;
                            attributes["packet_type"] = isAlarm ? "alarm" : "location";
                            if (isAlarm)
                            {
                                // is_alarm como métrica garantiza que el registro (con su
                                // RawPayload JSONB) se persista, portando el alarm_type.
                                attributes["is_alarm"] = "True";
                                metrics["is_alarm"] = 1;
                            }
                            if (!string.IsNullOrWhiteSpace(extras.AlarmType))
                            {
                                attributes["alarm_type"] = extras.AlarmType!;
                                attributes["alarm_code"] = extras.AlarmType!;
                            }
                            if (extras.Lbs is { } cell)
                                attributes["lbs_cell"] = $"{cell.Mcc}-{cell.Mnc}-{cell.Lac}-{cell.CellId}";
                            if (position?.SpeedKph.HasValue == true)
                                metrics["speed_kph"] = position.SpeedKph.Value;
                            if (position?.Heading.HasValue == true)
                                metrics["heading_deg"] = position.Heading.Value;
                        }
                        else if (frame.ProtocolNumber == R12LFFrame.ProtocolStatus)
                        {
                            attributes["packet_type"] = "heartbeat";
                            if (frame.InformationContent.Length >= 3)
                            {
                                byte terminalInfo = frame.InformationContent[0];
                                byte voltageLevel = frame.InformationContent[1];
                                byte gsmSignal = frame.InformationContent[2];

                                bool acc = (terminalInfo & 0x02) != 0;
                                metrics["ignition_acc"] = acc ? 1.0 : 0.0;
                                // #8: VoltageLevel CRUDO del GT06 (escala ordinal 0-6, NO
                                // porcentaje). Misma convención que Gt06TcpHandler.
                                metrics["battery_level"] = voltageLevel;
                                metrics["csq"] = gsmSignal;
                            }
                        }
                        else if (frame.ProtocolNumber == R12LFFrame.ProtocolStringInfo)
                        {
                            attributes["packet_type"] = "command_response";
                            if (frame.InformationContent.Length >= 5)
                            {
                                int cmdLen = frame.InformationContent[0];
                                int textLen = Math.Max(0, cmdLen - 4);
                                if (frame.InformationContent.Length >= 5 + textLen)
                                {
                                    attributes["command_response"] = System.Text.Encoding.ASCII.GetString(frame.InformationContent, 5, textLen);
                                }
                            }
                        }
                        else if (frame.ProtocolNumber == R12LFFrame.ProtocolIccid ||
                                 frame.ProtocolNumber == R12LFFrame.ProtocolImsi)
                        {
                            var (iccidImei, iccidImsi, iccidVal) = frame.DecodeIccidPacket();
                            attributes["packet_type"] = frame.ProtocolNumber == R12LFFrame.ProtocolIccid ? "iccid_info" : "imsi_info";
                            if (!string.IsNullOrEmpty(iccidImei))
                                chipId = iccidImei;
                            if (!string.IsNullOrEmpty(iccidImei))
                                attributes["imei"] = iccidImei;
                            if (!string.IsNullOrEmpty(iccidImsi))
                                attributes["imsi"] = iccidImsi;
                            if (!string.IsNullOrEmpty(iccidVal))
                                attributes["iccid"] = iccidVal;
                        }
                    }
                }
                catch
                {
                    // Fallback si no es hex válido
                }
            }
            // 2. Formato JSON (Bridge de microservicio Gateway TCP a MQTT/HTTP)
            else if (trimmed.StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                if (string.IsNullOrEmpty(chipId))
                {
                    if (root.TryGetProperty("chipId", out var c1)) chipId = c1.GetString() ?? string.Empty;
                    else if (root.TryGetProperty("imei", out var c2)) chipId = c2.GetString() ?? string.Empty;
                    else if (root.TryGetProperty("Device", out var dElem) && dElem.TryGetProperty("ChipId", out var c3))
                        chipId = c3.GetString() ?? string.Empty;
                }

                if (root.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTime(out var dt))
                    recordedAt = dt.ToUniversalTime();
                else if (root.TryGetProperty("Timestamp", out var ts2) && ts2.TryGetDateTime(out var dt2))
                    recordedAt = dt2.ToUniversalTime();

                double lat = 0.0, lon = 0.0;
                bool hasLat = root.TryGetProperty("latitude", out var lt) && lt.TryGetDouble(out lat);
                bool hasLon = root.TryGetProperty("longitude", out var ln) && ln.TryGetDouble(out lon);

                // Nested in Details / gps_data
                if (!hasLat && root.TryGetProperty("Details", out var det))
                {
                    if (det.TryGetProperty("gps_data", out var gd) ||
                        (det.TryGetProperty("accident", out var acc) && acc.TryGetProperty("gps_data", out gd)))
                    {
                        hasLat = gd.TryGetProperty("latitude", out lt) && lt.TryGetDouble(out lat);
                        hasLon = gd.TryGetProperty("longitude", out ln) && ln.TryGetDouble(out lon);
                    }
                }

                if (hasLat && hasLon)
                {
                    float? speed = root.TryGetProperty("speed", out var sp) && sp.TryGetSingle(out var s) ? s : null;
                    float? heading = root.TryGetProperty("heading", out var hd) && hd.TryGetSingle(out var h) ? h : null;
                    var posRes = GeoPosition.Create(lat, lon, speed, heading, null, null, true);
                    if (posRes.IsSuccess) position = posRes.Value;
                    if (speed.HasValue) metrics["speed_kph"] = speed.Value;
                    if (heading.HasValue) metrics["heading_deg"] = heading.Value;
                }

                if (root.TryGetProperty("ignition", out var ign))
                    metrics["ignition_acc"] = (ign.ValueKind == JsonValueKind.True || (ign.ValueKind == JsonValueKind.Number && ign.GetInt32() == 1)) ? 1.0 : 0.0;
                else if (root.TryGetProperty("acc", out var accElem))
                    metrics["ignition_acc"] = (accElem.ValueKind == JsonValueKind.True || (accElem.ValueKind == JsonValueKind.Number && accElem.GetInt32() == 1)) ? 1.0 : 0.0;

                if (root.TryGetProperty("battery_level", out var bat) && bat.TryGetDouble(out var batVal))
                    metrics["battery_level"] = batVal;

                if (root.TryGetProperty("gsm_signal", out var gsm) && gsm.TryGetDouble(out var gsmVal))
                    metrics["csq"] = gsmVal;

                if (root.TryGetProperty("command_response", out var cmdResp) && cmdResp.ValueKind == JsonValueKind.String)
                {
                    attributes["packet_type"] = "command_response";
                    attributes["command_response"] = cmdResp.GetString() ?? string.Empty;
                }

                if (root.TryGetProperty("is_alarm", out var alm) && alm.ValueKind == JsonValueKind.True)
                    attributes["packet_type"] = "alarm";

                if (root.TryGetProperty("imsi", out var imsiElem) && imsiElem.ValueKind == JsonValueKind.String)
                    attributes["imsi"] = imsiElem.GetString()!;

                if (root.TryGetProperty("iccid", out var iccidElem) && iccidElem.ValueKind == JsonValueKind.String)
                    attributes["iccid"] = iccidElem.GetString()!;
            }

            if (string.IsNullOrEmpty(chipId))
                chipId = $"{_model.ToUpperInvariant()}-UNKNOWN";

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
            return Result<NormalizedTelemetryEvent>.Failure(new Error("Gt06ProtocolAdapter.Error", ex.Message));
        }
    }

    private static string ExtractChipIdFromTopic(string topic)
    {
        if (TopicPatternMatcher.TryExtractParameters("tcp/{model}/{deviceId}", topic, out var p1))
            return p1["deviceId"];
        if (TopicPatternMatcher.TryExtractParameters("devices/{deviceId}/gps", topic, out var p2))
            return p2["deviceId"];
        if (TopicPatternMatcher.TryExtractParameters("devices/{deviceId}/heartbeat", topic, out var p3))
            return p3["deviceId"];
        if (TopicPatternMatcher.TryExtractParameters("{brand}/{deviceId}/telemetry", topic, out var p4))
            return p4["deviceId"];
        return string.Empty;
    }
}
