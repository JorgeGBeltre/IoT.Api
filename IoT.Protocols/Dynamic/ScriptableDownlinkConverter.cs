using System.Text;
using IoT.Domain.Common;
using IoT.Protocols.Contracts;
using Jint;
using Jint.Native;
using Jint.Native.Object;

namespace IoT.Protocols.Dynamic;

/// <summary>
/// Convertidor dinámico de comandos de bajada (Downlink) ejecutado en JavaScript (Jint).
/// Permite transformar comandos lógicos a formatos propietarios (JSON, texto AT o bytes Base64).
/// </summary>
public sealed class ScriptableDownlinkConverter : IDownlinkConverter
{
    private readonly string _converterId;
    private readonly string _script;
    private readonly Func<DownlinkCommandRequest, bool>? _predicate;

    public string ConverterId => _converterId;

    public ScriptableDownlinkConverter(string converterId, string script, Func<DownlinkCommandRequest, bool>? predicate = null)
    {
        if (string.IsNullOrWhiteSpace(converterId))
            throw new ArgumentException("El ConverterId no puede ser nulo ni vacío.", nameof(converterId));
        if (string.IsNullOrWhiteSpace(script))
            throw new ArgumentException("El script JavaScript no puede ser nulo ni vacío.", nameof(script));

        _converterId = converterId;
        _script = script;
        _predicate = predicate;
    }

    public bool CanHandle(DownlinkCommandRequest command)
    {
        if (_predicate != null)
            return _predicate(command);

        if (command.Metadata != null && command.Metadata.TryGetValue("converter", out var conv) && string.Equals(conv, _converterId, StringComparison.OrdinalIgnoreCase))
            return true;

        return true;
    }

    public Result<DownlinkWireFrame> ConvertDownlink(DownlinkCommandRequest command)
    {
        try
        {
            var engine = new Engine(options =>
            {
                options.LimitMemory(4 * 1024 * 1024);
                options.TimeoutInterval(TimeSpan.FromSeconds(2));
                options.MaxStatements(5000);
            });

            engine.Execute(_script);

            var encodeFunc = engine.GetValue("encode");
            if (encodeFunc.IsUndefined() || !encodeFunc.IsCallable())
            {
                return Result<DownlinkWireFrame>.Failure(new Error(
                    "ScriptError",
                    "El script debe definir una función llamada 'encode(commandName, params, sequence)'."));
            }

            // Inyectar parámetros
            JsValue paramsObj = JsValue.Undefined;
            if (!string.IsNullOrWhiteSpace(command.PayloadJson))
            {
                var jsonParser = engine.GetValue("JSON").AsObject().Get("parse").AsFunctionInstance();
                paramsObj = jsonParser.Call(JsValue.Undefined, [command.PayloadJson]);
            }
            else
            {
                paramsObj = new JsObject(engine);
            }

            var result = engine.Invoke(encodeFunc, command.CommandName, paramsObj, command.Sequence);
            if (result.IsNull() || result.IsUndefined() || !result.IsObject())
            {
                return Result<DownlinkWireFrame>.Failure(new Error(
                    "InvalidResult",
                    "La función 'encode' debe retornar un objeto con 'topic' y 'payload'."));
            }

            var obj = result.AsObject();
            string topic = obj.Get("topic").AsString();
            var payloadVal = obj.Get("payload");

            byte[] rawBytes;
            string? textPayload = null;
            bool isBinary = false;

            if (obj.Get("isBinary").IsBoolean() && obj.Get("isBinary").AsBoolean())
            {
                isBinary = true;
                string base64 = payloadVal.AsString();
                rawBytes = System.Convert.FromBase64String(base64);
            }
            else if (payloadVal.IsString())
            {
                textPayload = payloadVal.AsString();
                rawBytes = Encoding.UTF8.GetBytes(textPayload);
            }
            else
            {
                var jsonStringify = engine.GetValue("JSON").AsObject().Get("stringify").AsFunctionInstance();
                textPayload = jsonStringify.Call(JsValue.Undefined, [payloadVal]).AsString();
                rawBytes = Encoding.UTF8.GetBytes(textPayload);
            }

            return Result<DownlinkWireFrame>.Success(new DownlinkWireFrame(topic, rawBytes, textPayload, isBinary));
        }
        catch (Exception ex)
        {
            return Result<DownlinkWireFrame>.Failure(new Error("ScriptExecutionException", ex.Message));
        }
    }
}
