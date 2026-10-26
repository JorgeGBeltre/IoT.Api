using System.Globalization;
using System.Text.Json;
using IoT.Protocols.Contracts;
using IoT.Protocols.Models;

namespace IoT.Protocols.Mapping;

/// <summary>
/// Motor central de mapeo y normalización para telemetría y tramas IoT.
/// Reemplazo estático nativo y de alto rendimiento de AutoMapper (elimina vulnerabilidad GHSA-rvv3-g6hj-g44x).
/// Traduce dinámicamente cualquier payload JSON, diccionario o DTO a CanonicalDeviceTelemetry
/// y a NormalizedTelemetryEvent reconociendo todas las métricas posibles y sus alias.
/// </summary>
public static class TelemetryAutoMapper
{
    private static readonly SimpleMapper DefaultMapper = new();

    public static ISimpleMapper Mapper => DefaultMapper;

    /// <summary>
    /// Mapea un objeto fuente al modelo canónico de telemetría.
    /// </summary>
    public static CanonicalDeviceTelemetry MapToCanonical<TSource>(TSource source) where TSource : class
    {
        return DefaultMapper.Map<CanonicalDeviceTelemetry>(source);
    }

    /// <summary>
    /// Mapea un payload JSON dinámico hacia CanonicalDeviceTelemetry reconociendo estructuras
    /// planas, anidadas ({ Device: ..., Details: ... }), y alias en inglés y español.
    /// </summary>
    public static CanonicalDeviceTelemetry MapFromJson(string jsonPayload, string? fallbackDeviceId = null)
    {
        var canonical = new CanonicalDeviceTelemetry();
        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            if (!string.IsNullOrWhiteSpace(fallbackDeviceId)) canonical.ClientId = fallbackDeviceId;
            return canonical;
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonPayload);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                // Si contiene formato anidado con "Device" o "device"
                if (root.TryGetProperty("Device", out var devProp) || root.TryGetProperty("device", out devProp))
                {
                    if (devProp.ValueKind == JsonValueKind.Object)
                    {
                        var devDto = JsonSerializer.Deserialize<DeviceInfoDto>(devProp.GetRawText(), JsonOptions);
                        if (devDto != null)
                        {
                            MapDeviceInfo(devDto, canonical);
                        }
                    }
                }

                // Si contiene timestamp raíz
                if (root.TryGetProperty("Timestamp", out var tsProp) || root.TryGetProperty("timestamp", out tsProp) ||
                    root.TryGetProperty("time", out tsProp) || root.TryGetProperty("recorded_at", out tsProp))
                {
                    if (tsProp.ValueKind == JsonValueKind.String)
                    {
                        canonical.Timestamp = ParseDate(tsProp.GetString());
                    }
                    else if (tsProp.ValueKind == JsonValueKind.Number && tsProp.TryGetInt64(out var epoch))
                    {
                        canonical.Timestamp = epoch > 10_000_000_000
                            ? DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime
                            : DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
                    }
                }

                // Si contiene sub-objeto Details / details / telemetry / data
                JsonElement detailsElement = default;
                if (root.TryGetProperty("Details", out var d1) && d1.ValueKind == JsonValueKind.Object) detailsElement = d1;
                else if (root.TryGetProperty("details", out var d2) && d2.ValueKind == JsonValueKind.Object) detailsElement = d2;
                else if (root.TryGetProperty("telemetry", out var d3) && d3.ValueKind == JsonValueKind.Object) detailsElement = d3;
                else if (root.TryGetProperty("data", out var d4) && d4.ValueKind == JsonValueKind.Object) detailsElement = d4;

                if (detailsElement.ValueKind == JsonValueKind.Object)
                {
                    PopulateFromJsonElement(canonical, detailsElement);
                }

                // Procesar también las propiedades del root
                PopulateFromJsonElement(canonical, root);
            }
        }
        catch
        {
            // Silencioso ante JSON inválido; se usa fallback
        }

        if (string.IsNullOrWhiteSpace(canonical.ClientId) &&
            string.IsNullOrWhiteSpace(canonical.ChipId) &&
            !string.IsNullOrWhiteSpace(fallbackDeviceId))
        {
            canonical.ClientId = fallbackDeviceId;
        }

        return canonical;
    }

    public static void MapDeviceInfo(DeviceInfoDto src, CanonicalDeviceTelemetry dest)
    {
        if (src == null || dest == null) return;
        if (src.ChipId != null) dest.ChipId = src.ChipId;
        if (src.ChipType != null) dest.ChipType = src.ChipType;
        if (src.MacAddress != null) dest.MacAddress = src.MacAddress;
        if (src.ClientID != null) dest.ClientId = src.ClientID;
        if (src.FirmwareVersion != null) dest.FirmwareVersion = src.FirmwareVersion;
    }

    public static void MapGpsData(GpsDataDto src, CanonicalDeviceTelemetry dest)
    {
        if (src == null || dest == null) return;
        dest.Latitude = src.Latitude;
        dest.Longitude = src.Longitude;
        dest.Altitude = src.Altitude;
        dest.Speed = src.Speed;
        dest.Course = src.Course;
        dest.Satellites = src.Satellites;
        dest.IsValid = src.IsValid;
        if (src.Timestamp != null)
        {
            var dt = ParseDate(src.Timestamp);
            if (dt.HasValue) dest.Timestamp = dt.Value;
        }
    }

    public static void MapAccidentData(AccidentDataDto src, CanonicalDeviceTelemetry dest)
    {
        if (src == null || dest == null) return;
        dest.ImpactDetected = src.Impact_detected;
        dest.RolloverDetected = src.Rollover_detected;
        dest.ImpactMagnitude = src.Impact_magnitude;
        dest.RollAngle = src.Roll_angle;
        dest.PitchAngle = src.Pitch_angle;
        dest.DetectionTime = src.Detection_time;
    }

    public static void MapMqttDeviceMessage(MqttDeviceMessageDto src, CanonicalDeviceTelemetry dest)
    {
        if (src == null || dest == null) return;
        if (src.Device != null)
        {
            MapDeviceInfo(src.Device, dest);
        }
        if (!string.IsNullOrWhiteSpace(src.Timestamp) && dest.Timestamp == null)
        {
            dest.Timestamp = ParseDate(src.Timestamp);
        }
        if (src.Details != null)
        {
            PopulateFromDictionary(dest, src.Details);
        }
    }

    /// <summary>
    /// Pobla CanonicalDeviceTelemetry desde un JsonElement reconociendo claves y alias de forma recursiva.
    /// </summary>
    public static void PopulateFromJsonElement(CanonicalDeviceTelemetry target, JsonElement element, string prefix = "")
    {
        foreach (var property in element.EnumerateObject())
        {
            var key = property.Name.Trim();
            var val = property.Value;

            if (val.ValueKind == JsonValueKind.Object)
            {
                // Manejar sub-objetos especializados (p.ej. relay_1, volume, sensors_filtered, accident, gps_data)
                var nestedPrefix = string.IsNullOrEmpty(prefix) ? key : $"{prefix}_{key}";

                foreach (var child in val.EnumerateObject())
                {
                    var childKey = child.Name.Trim();
                    var childVal = child.Value;

                    // Mapeos especiales de contexto
                    if (key.Equals("volume", StringComparison.OrdinalIgnoreCase))
                    {
                        if (childKey.Equals("liters", StringComparison.OrdinalIgnoreCase) && TryGetDouble(childVal, out var l))
                            target.AdditionalMetrics["volume_liters"] = l;
                        else if (childKey.Equals("percentage", StringComparison.OrdinalIgnoreCase) && TryGetDouble(childVal, out var pct))
                            target.AdditionalMetrics["fill_percentage"] = pct;
                    }
                    else if (key.Equals("autonomy", StringComparison.OrdinalIgnoreCase))
                    {
                        if (childKey.Equals("estimated_days", StringComparison.OrdinalIgnoreCase) && TryGetDouble(childVal, out var days))
                            target.AdditionalMetrics["estimated_autonomy_days"] = days;
                    }
                    else if (key.StartsWith("relay", StringComparison.OrdinalIgnoreCase))
                    {
                        if (childKey.Equals("state", StringComparison.OrdinalIgnoreCase))
                        {
                            var s = childVal.GetString() ?? childVal.ToString();
                            target.AdditionalAttributes[$"{key}_state"] = s;
                            target.AdditionalMetrics[$"{key}_state_binary"] = (s.Equals("on", StringComparison.OrdinalIgnoreCase) || s == "1") ? 1.0 : 0.0;
                        }
                    }

                    // Registrar clave compuesta (p.ej. sensors_filtered_level_mm o level_mm)
                    ApplyProperty(target, childKey, childVal);
                }

                PopulateFromJsonElement(target, val, nestedPrefix);
            }
            else
            {
                ApplyProperty(target, key, val);
            }
        }
    }

    /// <summary>
    /// Pobla CanonicalDeviceTelemetry desde un diccionario genérico.
    /// </summary>
    public static void PopulateFromDictionary(CanonicalDeviceTelemetry target, IDictionary<string, object> dict)
    {
        foreach (var (k, v) in dict)
        {
            if (v == null) continue;
            ApplyPropertyObject(target, k, v);
        }
    }

    private static void ApplyProperty(CanonicalDeviceTelemetry target, string key, JsonElement val)
    {
        var normalizedKey = key.ToLowerInvariant().Replace("-", "_").Replace(" ", "");

        // 1. Identificadores
        if (normalizedKey is "chipid" or "chip_id" or "chip")
        {
            if (val.ValueKind == JsonValueKind.String) target.ChipId = val.GetString();
            else if (val.ValueKind == JsonValueKind.Number) target.ChipId = val.ToString();
            return;
        }
        // Alias de identidad que usan los firmwares REALES en la raíz de su trama, cada uno
        // con un nombre distinto: "device_id" (will de GLP, mqtt_client.cpp:43), "id"
        // (accidente del GPS, AccidentService.cpp:34) y "device" (familia MQTTOTAv5,
        // MQTTOTAv5.cpp:1003). Sin ellos la identidad se perdía y la trama se rechazaba.
        // No sobreescriben un ChipId explícito, que es más fiable, y sólo se aceptan como
        // valor escalar (en varias tramas "Device" es un objeto anidado).
        if (normalizedKey is "deviceid" or "device_id" or "device" or "id")
        {
            if (string.IsNullOrWhiteSpace(target.ChipId))
            {
                if (val.ValueKind == JsonValueKind.String) target.ChipId = val.GetString();
                else if (val.ValueKind == JsonValueKind.Number) target.ChipId = val.ToString();
            }
            return;
        }
        if (normalizedKey is "chiptype" or "chip_type") { target.ChipType = val.GetString(); return; }
        if (normalizedKey is "macaddress" or "mac_address" or "mac") { target.MacAddress = val.GetString(); return; }
        if (normalizedKey is "clientid" or "client_id" or "client") { target.ClientId = val.GetString(); return; }
        if (normalizedKey is "firmwareversion" or "firmware_version" or "version" or "fw_version") { target.FirmwareVersion = val.GetString(); return; }
        if (normalizedKey is "imei") { target.Imei = val.GetString(); return; }
        if (normalizedKey is "card_id" or "cardid") { target.CardId = val.GetString(); return; }
        if (normalizedKey is "alarm_code" or "alarm") { target.AlarmCode = val.GetString(); return; }

        // 2. GPS / Ubicación
        if (normalizedKey is "latitude" or "lat" or "latitud") { if (TryGetDouble(val, out var d)) target.Latitude = d; return; }
        if (normalizedKey is "longitude" or "lon" or "lng" or "long" or "longitud") { if (TryGetDouble(val, out var d)) target.Longitude = d; return; }
        if (normalizedKey is "altitude" or "alt" or "altura") { if (TryGetDouble(val, out var d)) target.Altitude = (float)d; return; }
        if (normalizedKey is "speed" or "velocidad" or "vel") { if (TryGetDouble(val, out var d)) target.Speed = (float)d; return; }
        if (normalizedKey is "course" or "heading" or "rumbo") { if (TryGetDouble(val, out var d)) target.Course = (float)d; return; }
        if (normalizedKey is "satellites" or "sats" or "satelites") { if (TryGetInt(val, out var i)) target.Satellites = i; return; }
        if (normalizedKey is "isvalid" or "is_valid" or "valido") { if (TryGetBool(val, out var b)) target.IsValid = b; return; }

        // 3. Accidentes & Cinemática
        // "impact" y "rollover" SIN el sufijo _detected son los nombres que publica el
        // firmware de accidentes (AccidentService.cpp:32-33).
        if (normalizedKey is "impact_detected" or "impactdetected" or "impacto" or "impact") { if (TryGetBool(val, out var b)) target.ImpactDetected = b; return; }
        if (normalizedKey is "rollover_detected" or "rolloverdetected" or "volcadura" or "rollover") { if (TryGetBool(val, out var b)) target.RolloverDetected = b; return; }
        if (normalizedKey is "impact_magnitude" or "impactmagnitude") { if (TryGetDouble(val, out var d)) target.ImpactMagnitude = (float)d; return; }
        if (normalizedKey is "roll_angle" or "rollangle" or "roll") { if (TryGetDouble(val, out var d)) target.RollAngle = (float)d; return; }
        if (normalizedKey is "pitch_angle" or "pitchangle" or "pitch") { if (TryGetDouble(val, out var d)) target.PitchAngle = (float)d; return; }
        if (normalizedKey is "detection_time" or "detectiontime") { if (TryGetULong(val, out var ul)) target.DetectionTime = ul; return; }
        if (normalizedKey is "acc_x" or "accx") { if (TryGetDouble(val, out var d)) target.AccX = (float)d; return; }
        if (normalizedKey is "acc_y" or "accy") { if (TryGetDouble(val, out var d)) target.AccY = (float)d; return; }
        if (normalizedKey is "acc_z" or "accz") { if (TryGetDouble(val, out var d)) target.AccZ = (float)d; return; }

        // 4. Parámetros Eléctricos & Batería
        if (normalizedKey is "voltage" or "voltaje" or "volts" or "v") { if (TryGetDouble(val, out var d)) target.Voltage = d; return; }
        if (normalizedKey is "battery" or "bateria" or "bat" or "batt" or "battery_level") { if (TryGetDouble(val, out var d)) target.Battery = d; return; }
        if (normalizedKey is "current" or "corriente" or "amperes" or "amps") { if (TryGetDouble(val, out var d)) target.Current = d; return; }
        if (normalizedKey is "power" or "potencia" or "watts") { if (TryGetDouble(val, out var d)) target.Power = d; return; }
        if (normalizedKey is "energy" or "energia" or "kwh") { if (TryGetDouble(val, out var d)) target.Energy = d; return; }
        if (normalizedKey is "frequency" or "frecuencia" or "hz") { if (TryGetDouble(val, out var d)) target.Frequency = d; return; }

        // 5. Sensores Ambientales & Fluidos
        if (normalizedKey is "temperature" or "temperatura" or "temp" or "celsius") { if (TryGetDouble(val, out var d)) target.Temperature = d; return; }
        if (normalizedKey is "pressure" or "presion" or "psi" or "bar" or "kpa") { if (TryGetDouble(val, out var d)) target.Pressure = d; return; }
        if (normalizedKey is "humidity" or "humedad" or "hum") { if (TryGetDouble(val, out var d)) target.Humidity = d; return; }
        if (normalizedKey is "gas_level" or "gaslevel" or "tank_level" or "level" or "nivel") { if (TryGetDouble(val, out var d)) target.GasLevel = d; return; }
        if (normalizedKey is "volume" or "volumen" or "liters" or "litros") { if (TryGetDouble(val, out var d)) target.Volume = d; return; }
        if (normalizedKey is "flow_rate" or "flowrate" or "flujo") { if (TryGetDouble(val, out var d)) target.FlowRate = d; return; }
        if (normalizedKey is "rssi" or "signal" or "senial") { if (TryGetInt(val, out var i)) target.Rssi = i; return; }

        // 6. Actuadores & Control
        if (normalizedKey is "relay_state" or "relaystate" or "relay" or "rele" or "state" or "estado")
        {
            if (TryGetBool(val, out var b)) target.RelayState = b;
            else if (TryGetDouble(val, out var d)) target.RelayState = d > 0;
            return;
        }
        if (normalizedKey is "switch_state" or "switchstate" or "switch")
        {
            if (TryGetBool(val, out var b)) target.SwitchState = b;
            else if (TryGetDouble(val, out var d)) target.SwitchState = d > 0;
            return;
        }
        if (normalizedKey is "access_granted" or "accessgranted" or "acceso") { if (TryGetBool(val, out var b)) target.AccessGranted = b; return; }
        if (normalizedKey is "door_open" or "dooropen" or "puerta") { if (TryGetBool(val, out var b)) target.DoorOpen = b; return; }

        // 7. Flota & Diagnósticos
        if (normalizedKey is "odometer" or "odometro" or "km") { if (TryGetDouble(val, out var d)) target.Odometer = d; return; }
        if (normalizedKey is "engine_hours" or "enginehours" or "horas") { if (TryGetDouble(val, out var d)) target.EngineHours = d; return; }
        if (normalizedKey is "fuel_level" or "fuellevel" or "combustible") { if (TryGetDouble(val, out var d)) target.FuelLevel = d; return; }
        if (normalizedKey is "ignition" or "ignicion" or "contacto") { if (TryGetBool(val, out var b)) target.Ignition = b; return; }
        if (normalizedKey is "emergency" or "panic" or "panico" or "sos") { if (TryGetBool(val, out var b)) target.Emergency = b; return; }

        // 8. Atributos o métricas genéricas no catalogadas
        if (val.ValueKind == JsonValueKind.Number && TryGetDouble(val, out var num))
        {
            target.AdditionalMetrics[key] = num;
        }
        else if (val.ValueKind == JsonValueKind.String)
        {
            target.AdditionalAttributes[key] = val.GetString() ?? string.Empty;
        }
        else if (val.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            target.AdditionalAttributes[key] = val.GetBoolean() ? "true" : "false";
        }
    }

    private static void ApplyPropertyObject(CanonicalDeviceTelemetry target, string key, object value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        ApplyProperty(target, key, doc.RootElement);
    }

    private static bool TryGetDouble(JsonElement val, out double result)
    {
        if (val.ValueKind == JsonValueKind.Number && val.TryGetDouble(out result)) return true;
        if (val.ValueKind == JsonValueKind.String && double.TryParse(val.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out result)) return true;
        result = 0;
        return false;
    }

    private static bool TryGetInt(JsonElement val, out int result)
    {
        if (val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out result)) return true;
        if (val.ValueKind == JsonValueKind.String && int.TryParse(val.GetString(), out result)) return true;
        result = 0;
        return false;
    }

    private static bool TryGetULong(JsonElement val, out ulong result)
    {
        if (val.ValueKind == JsonValueKind.Number && val.TryGetUInt64(out result)) return true;
        if (val.ValueKind == JsonValueKind.String && ulong.TryParse(val.GetString(), out result)) return true;
        result = 0;
        return false;
    }

    private static bool TryGetBool(JsonElement val, out bool result)
    {
        if (val.ValueKind is JsonValueKind.True) { result = true; return true; }
        if (val.ValueKind is JsonValueKind.False) { result = false; return true; }
        if (val.ValueKind == JsonValueKind.Number && val.TryGetDouble(out var d)) { result = d > 0; return true; }
        if (val.ValueKind == JsonValueKind.String)
        {
            var str = val.GetString()?.Trim().ToLowerInvariant();
            if (str is "true" or "1" or "yes" or "on" or "si") { result = true; return true; }
            if (str is "false" or "0" or "no" or "off") { result = false; return true; }
        }
        result = false;
        return false;
    }

    internal static DateTime? ParseDate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        if (DateTime.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)) return dt;
        if (long.TryParse(input, out var epoch))
        {
            return epoch > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime
                : DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
        }
        return null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

/// <summary>
/// Contrato simple para invocaciones de mapeo compatibles con la firma previa de IMapper.
/// </summary>
public interface ISimpleMapper
{
    TDest Map<TDest>(object source);
    void Map(object source, object destination);
}

internal sealed class SimpleMapper : ISimpleMapper
{
    public TDest Map<TDest>(object source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        if (typeof(TDest) == typeof(CanonicalDeviceTelemetry))
        {
            var target = new CanonicalDeviceTelemetry();
            Map(source, target);
            return (TDest)(object)target;
        }

        if (typeof(TDest) == typeof(NormalizedTelemetryEvent))
        {
            if (source is CanonicalDeviceTelemetry canonical)
            {
                return (TDest)(object)canonical.ToNormalizedEvent(null, null, null);
            }
        }

        throw new NotSupportedException($"Mapping from {source.GetType().Name} to {typeof(TDest).Name} is not supported.");
    }

    public void Map(object source, object destination)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (destination == null) throw new ArgumentNullException(nameof(destination));

        if (destination is CanonicalDeviceTelemetry canonical)
        {
            if (source is DeviceInfoDto devInfo)
            {
                TelemetryAutoMapper.MapDeviceInfo(devInfo, canonical);
                return;
            }
            if (source is GpsDataDto gps)
            {
                TelemetryAutoMapper.MapGpsData(gps, canonical);
                return;
            }
            if (source is AccidentDataDto acc)
            {
                TelemetryAutoMapper.MapAccidentData(acc, canonical);
                return;
            }
            if (source is MqttDeviceMessageDto msg)
            {
                TelemetryAutoMapper.MapMqttDeviceMessage(msg, canonical);
                return;
            }
            if (source is IDictionary<string, object> dict)
            {
                TelemetryAutoMapper.PopulateFromDictionary(canonical, dict);
                return;
            }
        }

        throw new NotSupportedException($"Mapping from {source.GetType().Name} to {destination.GetType().Name} is not supported.");
    }
}
