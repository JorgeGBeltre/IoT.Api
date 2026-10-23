using System.Text.Json;
using IoT.Domain.Common;
using IoT.Protocols.Contracts;
using IoT.Protocols.Routing;

namespace IoT.Protocols.Adapters;

public sealed class GlpPressureAdapter : IDeviceProtocolAdapter
{
    public string ProtocolName => "MQTT-PressureLevel-EKF";

    public bool CanHandle(string topic, string payload)
    {
        if (topic.StartsWith("glp/telemetria", StringComparison.OrdinalIgnoreCase) ||
            TopicPatternMatcher.MatchesMqttTopic("glp/telemetria/+", topic))
        {
            return true;
        }

        return payload.Contains("sensors_filtered", StringComparison.OrdinalIgnoreCase) &&
               payload.Contains("pressure_bar", StringComparison.OrdinalIgnoreCase);
    }

    public Result<NormalizedTelemetryEvent> Decode(string topic, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            string deviceId = string.Empty;
            if (TopicPatternMatcher.TryExtractParameters("glp/telemetria/{deviceId}", topic, out var p))
                deviceId = p["deviceId"];

            if (string.IsNullOrEmpty(deviceId) && root.TryGetProperty("Device", out var devElem))
            {
                if (devElem.TryGetProperty("DeviceId", out var dIdElem))
                    deviceId = dIdElem.GetString() ?? string.Empty;
                else if (devElem.TryGetProperty("ChipId", out var chipElem))
                    deviceId = chipElem.GetString() ?? string.Empty;
            }

            if (string.IsNullOrEmpty(deviceId))
                deviceId = "GLP-Device";

            DateTime recordedAt = DateTime.UtcNow;
            if (root.TryGetProperty("Timestamp", out var tsElem) && tsElem.TryGetDateTime(out var parsedTs))
                recordedAt = parsedTs.ToUniversalTime();

            var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("Details", out var detailsElem))
            {
                // Raw readings
                if (detailsElem.TryGetProperty("sensors_raw", out var rawElem))
                {
                    if (rawElem.TryGetProperty("level_mm", out var l)) metrics["level_mm_raw"] = l.GetDouble();
                    if (rawElem.TryGetProperty("pressure_bar", out var pr)) metrics["pressure_bar_raw"] = pr.GetDouble();
                    if (rawElem.TryGetProperty("temp_celsius", out var t)) metrics["temp_celsius_raw"] = t.GetDouble();
                }

                // EKF Filtered readings
                if (detailsElem.TryGetProperty("sensors_filtered", out var fltElem))
                {
                    if (fltElem.TryGetProperty("level_mm", out var l)) metrics["level_mm"] = l.GetDouble();
                    if (fltElem.TryGetProperty("pressure_bar", out var pr)) metrics["pressure_bar"] = pr.GetDouble();
                    if (fltElem.TryGetProperty("temp_celsius", out var t)) metrics["temp_celsius"] = t.GetDouble();
                    if (fltElem.TryGetProperty("kalman_uncertainty_mm", out var u)) metrics["kalman_uncertainty_mm"] = u.GetDouble();
                }

                // Volume & mass
                if (detailsElem.TryGetProperty("volume", out var volElem))
                {
                    if (volElem.TryGetProperty("liters", out var lt)) metrics["volume_liters"] = lt.GetDouble();
                    if (volElem.TryGetProperty("gallons", out var gl)) metrics["volume_gallons"] = gl.GetDouble();
                    if (volElem.TryGetProperty("percentage", out var pct)) metrics["fill_percentage"] = pct.GetDouble();
                    if (volElem.TryGetProperty("mass_kg", out var kg)) metrics["mass_kg"] = kg.GetDouble();
                    if (volElem.TryGetProperty("liters_15c", out var v15)) metrics["volume_liters_15c"] = v15.GetDouble();
                }

                // Autonomy
                if (detailsElem.TryGetProperty("autonomy", out var autElem))
                {
                    if (autElem.TryGetProperty("consumption_l_day", out var c)) metrics["consumption_l_day"] = c.GetDouble();
                    if (autElem.TryGetProperty("estimated_days", out var d)) metrics["estimated_autonomy_days"] = d.GetDouble();
                }

                // Diagnostics
                if (detailsElem.TryGetProperty("diagnostics", out var diagElem))
                {
                    if (diagElem.TryGetProperty("ultrasonic_health", out var uh)) metrics["sensor_health_ultrasonic"] = uh.GetDouble();
                    if (diagElem.TryGetProperty("pressure_health", out var ph)) metrics["sensor_health_pressure"] = ph.GetDouble();
                    if (diagElem.TryGetProperty("temp_health", out var th)) metrics["sensor_health_temp"] = th.GetDouble();
                }
            }

            return new NormalizedTelemetryEvent
            {
                DeviceIdentifier = deviceId,
                RecordedAt = recordedAt,
                ReceivedAt = DateTime.UtcNow,
                Position = null,
                NumericMetrics = metrics,
                Attributes = attributes,
                RawPayload = payload
            };
        }
        catch (Exception ex)
        {
            return Result<NormalizedTelemetryEvent>.Failure(new Error("GlpPressureAdapter.ParseError", ex.Message));
        }
    }

    public bool CanHandle(TransportMessage message) => CanHandle(message.TopicOrRoute, message.Payload);

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
}
