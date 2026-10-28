using System.Globalization;
using System.Text.Json;
using IoT.Domain.Common;
using IoT.Domain.ValueObjects;
using IoT.Protocols.Contracts;
using Jint;
using Jint.Native;
using Jint.Native.Object;

namespace IoT.Protocols.Dynamic;

/// <summary>
/// Convertidor dinámico de datos de subida (Uplink) ejecutado en un entorno sandboxed de JavaScript (Jint).
/// Permite integrar sensores y pasarelas de terceros (LoRaWAN, Sigfox, webhooks HTTP) sin recompilar el backend.
/// </summary>
public sealed class ScriptableUplinkConverter : IUplinkConverter
{
    private readonly string _converterId;
    private readonly string _script;
    private readonly Func<TransportMessage, bool>? _predicate;

    public string ConverterId => _converterId;

    public ScriptableUplinkConverter(string converterId, string script, Func<TransportMessage, bool>? predicate = null)
    {
        if (string.IsNullOrWhiteSpace(converterId))
            throw new ArgumentException("El ConverterId no puede ser nulo ni vacío.", nameof(converterId));
        if (string.IsNullOrWhiteSpace(script))
            throw new ArgumentException("El script JavaScript no puede ser nulo ni vacío.", nameof(script));

        _converterId = converterId;
        _script = script;
        _predicate = predicate;
    }

    public bool CanHandle(TransportMessage message)
    {
        if (_predicate != null)
            return _predicate(message);

        // Por defecto, si el tópico o metadata indica este convertidor
        if (message.Headers.TryGetValue("converter", out var conv) && string.Equals(conv, _converterId, StringComparison.OrdinalIgnoreCase))
            return true;

        if (message.TopicOrRoute.Contains(_converterId, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public Result<NormalizedTelemetryEvent> ConvertUplink(TransportMessage message)
    {
        try
        {
            var engine = new Engine(options =>
            {
                options.LimitMemory(4 * 1024 * 1024); // Máximo 4MB por ejecución
                options.TimeoutInterval(TimeSpan.FromSeconds(2)); // Timeout estricto de 2s
                options.MaxStatements(5000); // Protección contra bucles infinitos
            });

            // Inyectar el script del usuario
            engine.Execute(_script);

            // Preparar metadata como objeto JS
            var metadataObj = new JsObject(engine);
            foreach (var kvp in message.Headers)
            {
                metadataObj.Set(kvp.Key, kvp.Value);
            }

            // Invocar la función decode(payload, topic, metadata)
            var decodeFunc = engine.GetValue("decode");
            if (decodeFunc.IsUndefined() || !decodeFunc.IsCallable())
            {
                return Result<NormalizedTelemetryEvent>.Failure(new Error(
                    "ScriptError",
                    "El script debe definir una función llamada 'decode(payload, topic, metadata)'."));
            }

            var result = engine.Invoke(decodeFunc, message.Payload, message.TopicOrRoute, metadataObj);
            if (result.IsNull() || result.IsUndefined() || !result.IsObject())
            {
                return Result<NormalizedTelemetryEvent>.Failure(new Error(
                    "InvalidResult",
                    "La función 'decode' debe retornar un objeto con los datos normalizados."));
            }

            var obj = result.AsObject();

            // Extraer DeviceIdentifier / deviceId
            string deviceId = GetStringProperty(obj, "deviceId")
                ?? GetStringProperty(obj, "deviceIdentifier")
                ?? (message.Headers.TryGetValue("device_id", out var hId) ? hId : string.Empty);

            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return Result<NormalizedTelemetryEvent>.Failure(new Error(
                    "MissingDeviceId",
                    "El convertidor no produjo un identificador de dispositivo válido (campo 'deviceId')."));
            }

            // Extraer timestamp
            DateTime recordedAt = DateTime.UtcNow;
            var tsVal = obj.Get("timestamp");
            if (!tsVal.IsUndefined() && !tsVal.IsNull())
            {
                if (tsVal.IsDate())
                {
                    recordedAt = tsVal.AsDate().ToDateTime();
                }
                else if (tsVal.IsString() && DateTime.TryParse(tsVal.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsedDt))
                {
                    recordedAt = parsedDt;
                }
                else if (tsVal.IsNumber())
                {
                    long unixEpoch = (long)tsVal.AsNumber();
                    recordedAt = unixEpoch > 9999999999
                        ? DateTimeOffset.FromUnixTimeMilliseconds(unixEpoch).UtcDateTime
                        : DateTimeOffset.FromUnixTimeSeconds(unixEpoch).UtcDateTime;
                }
            }

            // Extraer telemetría numérica
            var numericMetrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var telemetryVal = obj.Get("telemetry");
            if (telemetryVal.IsObject())
            {
                var telemObj = telemetryVal.AsObject();
                foreach (var prop in telemObj.GetOwnProperties())
                {
                    var val = prop.Value.Value;
                    if (val.IsNumber())
                    {
                        numericMetrics[prop.Key.AsString()] = val.AsNumber();
                    }
                    else if (val.IsBoolean())
                    {
                        numericMetrics[prop.Key.AsString()] = val.AsBoolean() ? 1.0 : 0.0;
                    }
                }
            }

            // Extraer atributos
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var attrVal = obj.Get("attributes");
            if (attrVal.IsObject())
            {
                var attrObj = attrVal.AsObject();
                foreach (var prop in attrObj.GetOwnProperties())
                {
                    var val = prop.Value.Value;
                    attributes[prop.Key.AsString()] = val.ToString();
                }
            }

            // Extraer posición GPS (opcional)
            GeoPosition? position = null;
            var posVal = obj.Get("position");
            if (posVal.IsObject())
            {
                var posObj = posVal.AsObject();
                double? lat = GetDoubleProperty(posObj, "latitude") ?? GetDoubleProperty(posObj, "lat");
                double? lon = GetDoubleProperty(posObj, "longitude") ?? GetDoubleProperty(posObj, "lon") ?? GetDoubleProperty(posObj, "lng");

                if (lat.HasValue && lon.HasValue)
                {
                    double speed = GetDoubleProperty(posObj, "speed") ?? 0.0;
                    double heading = GetDoubleProperty(posObj, "heading") ?? 0.0;
                    double altitude = GetDoubleProperty(posObj, "altitude") ?? 0.0;
                    bool isValid = GetBoolProperty(posObj, "isValid") ?? true;

                    var posResult = GeoPosition.Create(
                        lat.Value,
                        lon.Value,
                        (float)speed,
                        (float)heading,
                        (float)altitude,
                        satellites: null,
                        isValid: isValid);
                    if (posResult.IsSuccess)
                    {
                        position = posResult.Value;
                    }
                }
            }

            var evt = new NormalizedTelemetryEvent
            {
                DeviceIdentifier = deviceId,
                RecordedAt = recordedAt,
                ReceivedAt = DateTime.UtcNow,
                Position = position,
                NumericMetrics = numericMetrics,
                Attributes = attributes,
                RawPayload = message.Payload
            };

            return Result<NormalizedTelemetryEvent>.Success(evt);
        }
        catch (Exception ex)
        {
            return Result<NormalizedTelemetryEvent>.Failure(new Error("ScriptExecutionException", ex.Message));
        }
    }

    private static string? GetStringProperty(ObjectInstance obj, string key)
    {
        var val = obj.Get(key);
        return val.IsUndefined() || val.IsNull() ? null : val.AsString();
    }

    private static double? GetDoubleProperty(ObjectInstance obj, string key)
    {
        var val = obj.Get(key);
        return val.IsNumber() ? val.AsNumber() : null;
    }

    private static bool? GetBoolProperty(ObjectInstance obj, string key)
    {
        var val = obj.Get(key);
        return val.IsBoolean() ? val.AsBoolean() : null;
    }
}
