using System.Text.Json;
using IoT.Domain.Common;
using IoT.Protocols.Contracts;
using IoT.Protocols.Routing;

namespace IoT.Protocols.Adapters;

public sealed class RelaySwitchAdapter : IDeviceProtocolAdapter
{
    public string ProtocolName => "MQTT-Relay-Switch";

    public bool CanHandle(string topic, string payload)
    {
        // El firmware publica el estado del relé en "<base>_state" (guion bajo, p.ej.
        // "device_switch1_state") y se suscribe a "<base>/state" y "<base>/command".
        // Sólo se reconocía la forma con barra, así que el estado real del actuador
        // (texto plano "on"/"off") lo acababa resolviendo el adaptador genérico.
        if (topic.StartsWith("relays/", StringComparison.OrdinalIgnoreCase) ||
            topic.EndsWith("/commands/ack", StringComparison.OrdinalIgnoreCase) ||
            topic.EndsWith("/state", StringComparison.OrdinalIgnoreCase) ||
            topic.EndsWith("_state", StringComparison.OrdinalIgnoreCase) ||
            topic.EndsWith("/report", StringComparison.OrdinalIgnoreCase) ||
            topic.Contains("switch", StringComparison.OrdinalIgnoreCase) ||
            topic.Contains("relay", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // La telemetría periódica llega al tópico GLOBAL "telemetry" (sin chipId ni nada
        // que delate al relé en el nombre), así que hay que reconocerla por el payload:
        // su marca es el objeto "relays".
        return HasRelayPayloadMarkers(payload);
    }

    internal static bool HasRelayPayloadMarkers(string payload) =>
        payload.Contains("devices_count", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("channel_count", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("\"switches\"", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("desired_state", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("device_switch", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("\"relays\"", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("LAN_ZEROCONF_SWITCH", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("DEVICE_COMMAND", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("DEVICE_SET_COMMAND", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("BUTTON_PRESS", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("RELAY_TOGGLE_ALL", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("TIMER_TRIGGER", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("TIMERS_UPDATED", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("WIFI_UPDATED", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("WIFI_UPDATE_FAILED", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("PULSE_AUTO_OFF", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("sledOnline", StringComparison.OrdinalIgnoreCase);

    public Result<NormalizedTelemetryEvent> Decode(string topic, string payload)
    {
        // El estado del relé llega como TEXTO PLANO, no como JSON
        // (Relay_Wifi_Esp32/src/Application/Services/TelemetryService.cpp:42-45 publica
        // la cadena desnuda "on"/"off"). JsonDocument.Parse lanzaba excepción y cada
        // conmutación del actuador se perdía.
        if (!LooksLikeJson(payload))
        {
            return DecodePlainTextState(topic, payload);
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            string chipId = string.Empty;
            if (root.TryGetProperty("Device", out var devElem) && devElem.TryGetProperty("ChipId", out var chipElem))
                chipId = chipElem.GetString() ?? string.Empty;
            else if (root.TryGetProperty("deviceId", out var rootDevIdElem) && rootDevIdElem.ValueKind == JsonValueKind.String)
                chipId = rootDevIdElem.GetString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(chipId) && TopicPatternMatcher.TryExtractParameters("devices/{deviceId}/commands/ack", topic, out var ackTopicParams))
                chipId = ackTopicParams["deviceId"];

            // Sin identidad NO se inventa un marcador ("Relay-Switch" creaba un dispositivo
            // fantasma): los tópicos de este firmware son globales y no llevan ChipId, así
            // que manda la identidad autenticada de la conexión.

            DateTime recordedAt = DateTime.UtcNow;
            if (root.TryGetProperty("Timestamp", out var tsElem) && tsElem.TryGetDateTime(out var parsedTs))
                recordedAt = parsedTs.ToUniversalTime();
            else if (root.TryGetProperty("timestamp", out var tsLowerElem) && tsLowerElem.TryGetDateTime(out var parsedLowerTs))
                recordedAt = parsedLowerTs.ToUniversalTime();

            var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int detectedChannelCount = 0;

            bool isAckMessage = topic.EndsWith("/commands/ack", StringComparison.OrdinalIgnoreCase);
            string ackStatus = string.Empty;
            if ((root.TryGetProperty("commandId", out var cmdIdElem) || root.TryGetProperty("command_id", out cmdIdElem)) &&
                cmdIdElem.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(cmdIdElem.GetString()))
            {
                attributes["ack_command_id"] = cmdIdElem.GetString()!;
                isAckMessage = true;
            }

            if (isAckMessage)
            {
                if (root.TryGetProperty("status", out var stElem) && stElem.ValueKind == JsonValueKind.String)
                {
                    ackStatus = stElem.GetString() ?? string.Empty;
                    attributes["ack_status"] = ackStatus;
                }
                if (root.TryGetProperty("error", out var errElem) && errElem.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(errElem.GetString()))
                {
                    attributes["ack_error"] = errElem.GetString()!;
                }
                if (root.TryGetProperty("sequence", out var seqElem) && seqElem.TryGetDouble(out var seqVal))
                {
                    metrics["command_sequence"] = seqVal;
                }
                if (root.TryGetProperty("execUs", out var execElem) && execElem.TryGetDouble(out var execUs))
                {
                    metrics["cmd_exec_us"] = execUs;
                }
                if (root.TryGetProperty("duplicate", out var dupElem) && (dupElem.ValueKind is JsonValueKind.True or JsonValueKind.False))
                {
                    attributes["ack_duplicate"] = dupElem.GetBoolean() ? "true" : "false";
                }
            }

            bool isRejectedAck = isAckMessage &&
                (ackStatus.Equals("STALE_REJECTED", StringComparison.OrdinalIgnoreCase) ||
                 ackStatus.Equals("FAILED", StringComparison.OrdinalIgnoreCase) ||
                 ackStatus.Equals("REJECTED", StringComparison.OrdinalIgnoreCase) ||
                 ackStatus.Equals("ERROR", StringComparison.OrdinalIgnoreCase));

            if (root.TryGetProperty("Device", out var devMetaElem) &&
                devMetaElem.TryGetProperty("FirmwareVersion", out var fwElem) &&
                fwElem.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(fwElem.GetString()))
            {
                attributes["firmware_version"] = fwElem.GetString()!;
            }

            ExtractStateVersionAndSource(root, metrics, attributes);
            ExtractPowerAndConfigFields(root, metrics, attributes);
            if (!isRejectedAck)
            {
                detectedChannelCount = Math.Max(detectedChannelCount, ExtractExplicitChannelCount(root));
                detectedChannelCount = Math.Max(detectedChannelCount, ExtractSwitchesArray(root, metrics, attributes));

                if (detectedChannelCount == 0 &&
                    (root.TryGetProperty("reportedState", out var repElem) || root.TryGetProperty("state", out repElem)))
                {
                    int ch = 1;
                    if (root.TryGetProperty("channel", out var chRootElem) && chRootElem.TryGetInt32(out int parsedCh) && parsedCh is >= 1 and <= 16)
                        ch = parsedCh;

                    string? repStr = repElem.ValueKind switch
                    {
                        JsonValueKind.True => "on",
                        JsonValueKind.False => "off",
                        JsonValueKind.String => repElem.GetString(),
                        _ => null
                    };
                    if (!string.IsNullOrWhiteSpace(repStr))
                    {
                        SetSwitchState(metrics, attributes, $"device_switch{ch}", repStr!, ch);
                        detectedChannelCount = Math.Max(detectedChannelCount, ch);
                    }
                }
            }

            if (root.TryGetProperty("params", out var paramsElem) && paramsElem.ValueKind == JsonValueKind.Object)
            {
                ExtractStateVersionAndSource(paramsElem, metrics, attributes);
                ExtractPowerAndConfigFields(paramsElem, metrics, attributes);
                if (!isRejectedAck)
                {
                    detectedChannelCount = Math.Max(detectedChannelCount, ExtractExplicitChannelCount(paramsElem));
                    detectedChannelCount = Math.Max(detectedChannelCount, ExtractSwitchesArray(paramsElem, metrics, attributes));
                }
            }

            if (root.TryGetProperty("Details", out var detailsElem))
            {
                ExtractStateVersionAndSource(detailsElem, metrics, attributes);
                ExtractPowerAndConfigFields(detailsElem, metrics, attributes);
                if (!isRejectedAck)
                {
                    detectedChannelCount = Math.Max(detectedChannelCount, ExtractExplicitChannelCount(detailsElem));
                    detectedChannelCount = Math.Max(detectedChannelCount, ExtractSwitchesArray(detailsElem, metrics, attributes));
                }

                if (detailsElem.TryGetProperty("timers", out var timersElem) && timersElem.ValueKind == JsonValueKind.Array)
                {
                    attributes["timers_sync_json"] = timersElem.GetRawText();
                }

                if (detailsElem.TryGetProperty("rssi", out var rssiElem) && rssiElem.TryGetDouble(out var rssi))
                {
                    metrics["rssi_dbm"] = rssi;
                    metrics["wifi_rssi"] = rssi;
                }

                if (detailsElem.TryGetProperty("free_heap", out var heapElem) && heapElem.TryGetDouble(out var heap))
                    metrics["free_heap_bytes"] = heap;

                if (detailsElem.TryGetProperty("uptime", out var upElem) && upElem.TryGetDouble(out var uptime))
                {
                    metrics["uptime_seconds"] = uptime;
                    metrics["uptime_sec"] = uptime;
                }

                if (detailsElem.TryGetProperty("sync_completed", out var syncElem) &&
                    (syncElem.ValueKind == JsonValueKind.True || syncElem.ValueKind == JsonValueKind.False))
                {
                    attributes["sync_completed"] = syncElem.GetBoolean().ToString();
                }

                if (detailsElem.TryGetProperty("wifi_status", out var wifiElem) && wifiElem.ValueKind == JsonValueKind.String)
                    attributes["wifi_status"] = wifiElem.GetString()!;

                // Forma del mensaje de STATUS: Details.devices = { "<id>": {state, desired_state, pin} }
                // (Relay_Wifi_Esp32/.../ArduinoJsonPayloadSerializer.cpp:33-39).
                if (detailsElem.TryGetProperty("devices", out var switchesElem) && switchesElem.ValueKind == JsonValueKind.Object)
                {
                    int ordinal = 0;
                    foreach (var prop in switchesElem.EnumerateObject())
                    {
                        var switchData = prop.Value;
                        if (switchData.ValueKind != JsonValueKind.Object) continue;
                        ordinal++;

                        if (switchData.TryGetProperty("state", out var stateElem))
                        {
                            int ch = SetSwitchState(metrics, attributes, prop.Name, stateElem.GetString() ?? "off", ordinal);
                            detectedChannelCount = Math.Max(detectedChannelCount, ch);
                        }

                        if (switchData.TryGetProperty("desired_state", out var desElem))
                            attributes[$"{prop.Name}_desired_state"] = desElem.GetString() ?? "off";

                        if (switchData.TryGetProperty("pin", out var pinElem) && pinElem.TryGetDouble(out var pin))
                            metrics[$"{prop.Name}_gpio_pin"] = pin;
                    }
                    detectedChannelCount = Math.Max(detectedChannelCount, ordinal);
                }

                // Forma del mensaje de TELEMETRÍA periódica: Details.relays = { "<id>": "on" }
                // (mismo serializador, líneas 102-105). Es PLANA, no anidada: el adaptador
                // sólo entendía la forma de status, así que la telemetría periódica caía al
                // fallback genérico y producía métricas con otros nombres para el MISMO
                // equipo, partiendo sus series históricas en dos.
                if (detailsElem.TryGetProperty("relays", out var relaysElem) && relaysElem.ValueKind == JsonValueKind.Object)
                {
                    int ordinal = 0;
                    foreach (var prop in relaysElem.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            ordinal++;
                            int ch = SetSwitchState(metrics, attributes, prop.Name, prop.Value.GetString() ?? "off", ordinal);
                            detectedChannelCount = Math.Max(detectedChannelCount, ch);
                        }
                        else if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            ordinal++;
                            int ch = SetSwitchState(metrics, attributes, prop.Name, prop.Value.GetBoolean() ? "on" : "off", ordinal);
                            detectedChannelCount = Math.Max(detectedChannelCount, ch);
                        }
                    }
                    detectedChannelCount = Math.Max(detectedChannelCount, ordinal);
                }

                // Eventos de conmutación del relé (events/<chipId>): Details.info = { event, description, state }
                if (detailsElem.TryGetProperty("info", out var infoElem) && infoElem.ValueKind == JsonValueKind.Object)
                {
                    ExtractStateVersionAndSource(infoElem, metrics, attributes);
                    string evName = string.Empty;
                    string evDesc = string.Empty;
                    if (infoElem.TryGetProperty("event", out var evNameElem) && evNameElem.ValueKind == JsonValueKind.String)
                    {
                        evName = evNameElem.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(evName))
                        {
                            attributes["last_event"] = evName;
                        }
                    }

                    if (infoElem.TryGetProperty("description", out var evDescElem) && evDescElem.ValueKind == JsonValueKind.String)
                    {
                        evDesc = evDescElem.GetString() ?? string.Empty;
                    }

                    if (infoElem.TryGetProperty("state", out var evStateElem) && evStateElem.ValueKind == JsonValueKind.String)
                    {
                        var evState = evStateElem.GetString();
                        if (!string.IsNullOrWhiteSpace(evState))
                        {
                            bool isBulkAllEvent =
                                string.Equals(evName, "RELAY_TOGGLE_ALL", StringComparison.OrdinalIgnoreCase) ||
                                evDesc.Contains("all:on", StringComparison.OrdinalIgnoreCase) ||
                                evDesc.Contains("all:off", StringComparison.OrdinalIgnoreCase);

                            if (isBulkAllEvent)
                            {
                                attributes["last_bulk_state"] = evState.ToLowerInvariant();
                            }
                            else
                            {
                                int eventChannel = ExtractEventChannelIndex(infoElem, evDesc);
                                SetSwitchState(metrics, attributes, $"device_switch{eventChannel}", evState, eventChannel);
                            }
                        }
                    }
                }
            }

            if (detectedChannelCount >= 1)
            {
                metrics["channel_count"] = detectedChannelCount;
                metrics["devices_count"] = detectedChannelCount;
                attributes["channel_count"] = detectedChannelCount.ToString();
            }

            return new NormalizedTelemetryEvent
            {
                DeviceIdentifier = chipId,
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
            return Result<NormalizedTelemetryEvent>.Failure(new Error("RelaySwitchAdapter.ParseError", ex.Message));
        }
    }

    /// <summary>
    /// Publica el estado de un relé con los MISMOS nombres por los dos caminos (status y
    /// telemetría periódica), para que las series históricas del equipo no se partan.
    /// </summary>
    private static int SetSwitchState(
        Dictionary<string, double> metrics,
        Dictionary<string, string> attributes,
        string switchName,
        string state,
        int fallbackOrdinal = 1)
    {
        bool on = state.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                  state == "1" ||
                  state.Equals("true", StringComparison.OrdinalIgnoreCase);

        double val = on ? 1.0 : 0.0;
        attributes[$"{switchName}_state"] = on ? "on" : "off";
        metrics[$"{switchName}_state_binary"] = val;

        int ch = ExtractChannelIndex(switchName, fallbackOrdinal);
        metrics[$"switch_{ch}"] = val;
        metrics[$"relay_state_{ch}"] = val;
        metrics[$"relay_{ch}_state_binary"] = val;
        if (ch == 1)
        {
            metrics["relay_state"] = val;
        }
        return ch;
    }

    private static int ExtractExplicitChannelCount(JsonElement elem)
    {
        if (elem.ValueKind != JsonValueKind.Object)
            return 0;

        foreach (string key in new[] { "channel_count", "devices_count", "channels" })
        {
            if (elem.TryGetProperty(key, out var valElem))
            {
                if (valElem.ValueKind == JsonValueKind.Number && valElem.TryGetInt32(out int n) && n is >= 1 and <= 16)
                    return n;
                if (valElem.ValueKind == JsonValueKind.String && int.TryParse(valElem.GetString(), out int s) && s is >= 1 and <= 16)
                    return s;
            }
        }
        return 0;
    }

    private static int ExtractSwitchesArray(
        JsonElement elem,
        Dictionary<string, double> metrics,
        Dictionary<string, string> attributes)
    {
        if (elem.ValueKind != JsonValueKind.Object ||
            !elem.TryGetProperty("switches", out var arrElem) ||
            arrElem.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        int maxCh = 0;
        int idx = 0;
        foreach (var item in arrElem.EnumerateArray())
        {
            idx++;
            if (item.ValueKind != JsonValueKind.Object) continue;

            int ch = idx;
            if (item.TryGetProperty("outlet", out var outElem) && outElem.ValueKind == JsonValueKind.Number && outElem.TryGetInt32(out int outlet))
            {
                ch = Math.Clamp(outlet + 1, 1, 16);
            }
            else if (item.TryGetProperty("channel", out var chElem) && chElem.ValueKind == JsonValueKind.Number && chElem.TryGetInt32(out int channelNum))
            {
                ch = Math.Clamp(channelNum <= 0 ? channelNum + 1 : channelNum, 1, 16);
            }

            string swVal = "off";
            if (item.TryGetProperty("switch", out var swProp))
            {
                swVal = swProp.ValueKind switch
                {
                    JsonValueKind.String => swProp.GetString() ?? "off",
                    JsonValueKind.True => "on",
                    JsonValueKind.False => "off",
                    JsonValueKind.Number when swProp.TryGetInt32(out int v) => v != 0 ? "on" : "off",
                    _ => "off"
                };
            }
            else if (item.TryGetProperty("state", out var stProp) && stProp.ValueKind == JsonValueKind.String)
            {
                swVal = stProp.GetString() ?? "off";
            }

            SetSwitchState(metrics, attributes, $"device_switch{ch}", swVal, ch);
            maxCh = Math.Max(maxCh, ch);
        }

        return Math.Max(maxCh, idx);
    }

    private static int ExtractEventChannelIndex(JsonElement infoElem, string description)
    {
        if (infoElem.TryGetProperty("channel", out var chElem) && chElem.ValueKind == JsonValueKind.Number && chElem.TryGetInt32(out int ch) && ch is >= 1 and <= 16)
            return ch;
        if (infoElem.TryGetProperty("outlet", out var outElem) && outElem.ValueKind == JsonValueKind.Number && outElem.TryGetInt32(out int outlet) && outlet is >= 0 and < 16)
            return outlet + 1;
        if (infoElem.TryGetProperty("device_id", out var devIdElem) && devIdElem.ValueKind == JsonValueKind.String)
            return ExtractChannelIndex(devIdElem.GetString() ?? string.Empty, 1);

        if (!string.IsNullOrWhiteSpace(description))
        {
            int swIdx = description.IndexOf("switch", StringComparison.OrdinalIgnoreCase);
            if (swIdx >= 0)
            {
                int start = swIdx + 6;
                int end = start;
                while (end < description.Length && char.IsDigit(description[end])) end++;
                if (end > start && int.TryParse(description.AsSpan(start, end - start), out int parsedSw) && parsedSw is >= 1 and <= 16)
                    return parsedSw;
            }

            int dashIdx = description.IndexOf("-0", StringComparison.Ordinal);
            if (dashIdx >= 0 && dashIdx + 2 < description.Length && char.IsDigit(description[dashIdx + 2]))
            {
                int end = dashIdx + 2;
                while (end < description.Length && char.IsDigit(description[end])) end++;
                if (int.TryParse(description.AsSpan(dashIdx + 1, end - (dashIdx + 1)), out int parsedDash) && parsedDash is >= 1 and <= 16)
                    return parsedDash;
            }
        }

        return 1;
    }

    private static int ExtractChannelIndex(string switchName, int fallbackOrdinal = 1)
    {
        int safeFallback = fallbackOrdinal is >= 1 and <= 16 ? fallbackOrdinal : 1;
        if (string.IsNullOrWhiteSpace(switchName))
        {
            return safeFallback;
        }

        // Sufijo "<chipId>-01", "<chipId>-02", etc. (status/telemetría JSON)
        int dashIdx = switchName.LastIndexOf('-');
        if (dashIdx >= 0 && dashIdx < switchName.Length - 1 &&
            int.TryParse(switchName.AsSpan(dashIdx + 1), out int dashChannel) &&
            dashChannel is >= 1 and <= 16)
        {
            return dashChannel;
        }

        // Sufijo numérico directo: "device_switch1", "switch_2", "relay3"
        int digitStart = switchName.Length;
        while (digitStart > 0 && char.IsDigit(switchName[digitStart - 1]))
        {
            digitStart--;
        }

        if (digitStart < switchName.Length &&
            int.TryParse(switchName.AsSpan(digitStart), out int trailingChannel) &&
            trailingChannel is >= 1 and <= 16)
        {
            return trailingChannel;
        }

        return safeFallback;
    }

    private static void ExtractStateVersionAndSource(
        JsonElement elem,
        Dictionary<string, double> metrics,
        Dictionary<string, string> attributes)
    {
        if (elem.ValueKind != JsonValueKind.Object)
            return;

        if ((elem.TryGetProperty("state_version", out var svElem) || elem.TryGetProperty("stateVersion", out svElem)) &&
            svElem.TryGetDouble(out double sv) && sv > 0)
        {
            metrics["state_version"] = sv;
            attributes["state_version"] = ((long)sv).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (elem.TryGetProperty("source", out var srcElem) &&
            srcElem.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(srcElem.GetString()))
        {
            attributes["last_source"] = srcElem.GetString()!;
        }
    }

    private static void ExtractPowerAndConfigFields(
        JsonElement elem,
        Dictionary<string, double> metrics,
        Dictionary<string, string> attributes)
    {
        if (elem.ValueKind != JsonValueKind.Object)
            return;

        if ((elem.TryGetProperty("voltageV", out var vElem) || elem.TryGetProperty("voltage_v", out vElem) || elem.TryGetProperty("voltage", out vElem)) &&
            vElem.TryGetDouble(out var vVal))
        {
            metrics["voltage_v"] = vVal;
        }

        if ((elem.TryGetProperty("currentA", out var cElem) || elem.TryGetProperty("current_a", out cElem) || elem.TryGetProperty("current", out cElem)) &&
            cElem.TryGetDouble(out var cVal))
        {
            metrics["current_a"] = cVal;
        }

        if ((elem.TryGetProperty("powerW", out var pElem) || elem.TryGetProperty("activePowerW", out pElem) || elem.TryGetProperty("power_w", out pElem) || elem.TryGetProperty("power", out pElem)) &&
            pElem.TryGetDouble(out var pVal))
        {
            metrics["power_w"] = pVal;
        }

        if ((elem.TryGetProperty("energyWh", out var eElem) || elem.TryGetProperty("energy_wh", out eElem)) &&
            eElem.TryGetDouble(out var eVal))
        {
            metrics["energy_wh"] = eVal;
        }
        else if ((elem.TryGetProperty("energyKwh", out var kElem) || elem.TryGetProperty("energy_kwh", out kElem)) &&
                 kElem.TryGetDouble(out var kVal))
        {
            metrics["energy_kwh"] = kVal;
            metrics["energy_wh"] = kVal * 1000.0;
        }

        foreach (var attrKey in new[] { "startup", "pulse", "sledOnline", "bind_token", "fwVersion", "rstReason", "wifi_ssid", "wifi_bssid", "local_ip" })
        {
            if (elem.TryGetProperty(attrKey, out var aElem))
            {
                if (aElem.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(aElem.GetString()))
                    attributes[attrKey] = aElem.GetString()!;
                else if (aElem.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    attributes[attrKey] = aElem.GetBoolean() ? "on" : "off";
                else if (aElem.ValueKind == JsonValueKind.Number)
                    attributes[attrKey] = aElem.GetRawText();
            }
        }

        if (elem.TryGetProperty("pulseWidth", out var pwElem) && pwElem.TryGetDouble(out var pwVal))
        {
            metrics["pulse_width_ms"] = pwVal;
            attributes["pulseWidth"] = ((int)pwVal).ToString();
        }

        if (elem.TryGetProperty("account_bound", out var abElem))
        {
            if (abElem.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                attributes["account_bound"] = abElem.GetBoolean() ? "true" : "false";
            }
            else if (abElem.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(abElem.GetString()))
            {
                attributes["account_bound"] = abElem.GetString()!;
            }
        }
    }

    private static bool LooksLikeJson(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return false;
        char first = payload.TrimStart()[0];
        return first == '{' || first == '[';
    }

    /// <summary>
    /// Decodifica el estado del relé publicado como texto plano. Acepta las mismas formas
    /// que el propio firmware reconoce en sus comandos
    /// (RelayFirmwareController.cpp:154-155: "on"/"1"/"true" y "off"/"0"/"false").
    /// </summary>
    private static Result<NormalizedTelemetryEvent> DecodePlainTextState(string topic, string payload)
    {
        string value = payload.Trim().Trim('"');

        bool? state = value.ToLowerInvariant() switch
        {
            "on" or "1" or "true" => true,
            "off" or "0" or "false" => false,
            _ => null
        };

        var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string extractedChipId = string.Empty;
        string switchName = topic.Trim('/');

        // Soportar tanto tópicos por equipo ("devices/<chipId>/device_switch1/report")
        // como tópicos globales heredados ("device_switch1_state").
        if (switchName.StartsWith("devices/", StringComparison.OrdinalIgnoreCase) ||
            switchName.StartsWith("relays/", StringComparison.OrdinalIgnoreCase))
        {
            int firstSlash = switchName.IndexOf('/');
            int secondSlash = firstSlash >= 0 ? switchName.IndexOf('/', firstSlash + 1) : -1;
            if (secondSlash > firstSlash + 1)
            {
                extractedChipId = switchName[(firstSlash + 1)..secondSlash];
                switchName = switchName[(secondSlash + 1)..];
            }
        }

        foreach (var suffix in new[] { "/report", "_state", "/state", "/command", "/get", "/set" })
        {
            if (switchName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                switchName = switchName[..^suffix.Length];
                break;
            }
        }
        switchName = switchName.Trim('/');
        if (string.IsNullOrWhiteSpace(switchName)) switchName = "switch";

        if (state.HasValue)
        {
            SetSwitchState(metrics, attributes, switchName, state.Value ? "on" : "off");
        }
        else
        {
            // Otros textos planos del firmware (p.ej. "status" en <base>/get): se conserva
            // el valor como atributo en vez de perder la trama.
            attributes[$"{switchName}_message"] = value;
        }

        return new NormalizedTelemetryEvent
        {
            // Para tópicos globales sin prefijo devices/<chipId>/, queda vacía para que la
            // aporte la conexión autenticada (TransportDeviceIdentifier).
            DeviceIdentifier = extractedChipId,
            RecordedAt = DateTime.UtcNow,
            ReceivedAt = DateTime.UtcNow,
            Position = null,
            NumericMetrics = metrics,
            Attributes = attributes,
            RawPayload = payload
        };
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
