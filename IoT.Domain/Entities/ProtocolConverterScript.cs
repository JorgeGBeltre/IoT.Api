using IoT.Domain.Common;

namespace IoT.Domain.Entities;

/// <summary>
/// Convertidor dinámico de datos (Uplink/Downlink) definido POR TENANT para un perfil/protocolo
/// concreto, como script JavaScript sandboxed (Jint). Es la pieza que permite la adopción estilo
/// ThingsBoard: un tenant integra un sensor o pasarela de terceros sin recompilar el backend,
/// cargando un script que decodifica su trama a la telemetría canónica de la plataforma.
///
/// Es CONFIGURACIÓN a nivel de tenant (no de subárbol de customer): un script de decodificación
/// describe "cómo habla este protocolo para este tenant", igual que <see cref="MqttBrokerConfig"/>
/// o <see cref="TenantSettings"/>. Por eso implementa <see cref="ITenantScoped"/> y su aislamiento
/// es la política RLS estándar <c>tenant_isolation</c> por <c>TenantId</c>, no el recorte por
/// subárbol de las entidades <c>ICustomerScoped</c>.
///
/// El contenido del script (JS) NO se valida aquí: el Dominio no depende de Jint. La validación en
/// sandbox (sintaxis + límites de memoria/tiempo/sentencias) se hace en la capa de aplicación/endpoint
/// ANTES de persistir (ver ScriptConverterValidator). Esta entidad sólo garantiza invariantes
/// estructurales (tenant, clave de perfil y tamaño acotado del script).
/// </summary>
public sealed class ProtocolConverterScript : AggregateRoot<Guid>, ITenantScoped
{
    public Guid TenantId { get; private set; }

    /// <summary>
    /// Clave del perfil/protocolo al que aplica el convertidor (p.ej. el <c>ProfileId</c> del
    /// registro, o el hint de transporte como "TCP-QUECLINK" / "MQTT"). Se normaliza a minúsculas
    /// y recortada; es única dentro del tenant.
    /// </summary>
    public string ProfileKey { get; private set; }

    public string UplinkScript { get; private set; }
    public string? DownlinkScript { get; private set; }
    public bool IsEnabled { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public const int MaxProfileKeyLength = 128;

    /// <summary>
    /// Tope de tamaño del script de usuario. Acota el coste de parseo/preparación del AST (una sola
    /// vez por instancia cacheada) y evita almacenar entradas desproporcionadas; el límite de
    /// EJECUCIÓN lo imponen aparte memoria/tiempo/sentencias del sandbox Jint.
    /// </summary>
    public const int MaxScriptLength = 64 * 1024;

    private ProtocolConverterScript(
        Guid id,
        Guid tenantId,
        string profileKey,
        string uplinkScript,
        string? downlinkScript,
        bool isEnabled) : base(id)
    {
        TenantId = tenantId;
        ProfileKey = profileKey;
        UplinkScript = uplinkScript;
        DownlinkScript = downlinkScript;
        IsEnabled = isEnabled;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public static Result<ProtocolConverterScript> Create(
        Guid tenantId,
        string profileKey,
        string uplinkScript,
        string? downlinkScript,
        bool isEnabled)
    {
        if (tenantId == Guid.Empty)
            return Result<ProtocolConverterScript>.Failure(new Error("ConverterScript.InvalidTenant", "TenantId no puede estar vacío."));

        var normalizedKey = NormalizeKey(profileKey);
        var keyCheck = ValidateKey(normalizedKey);
        if (keyCheck.IsFailure) return Result<ProtocolConverterScript>.Failure(keyCheck.Error);

        var bodyCheck = ValidateScripts(uplinkScript, downlinkScript);
        if (bodyCheck.IsFailure) return Result<ProtocolConverterScript>.Failure(bodyCheck.Error);

        return new ProtocolConverterScript(
            Guid.NewGuid(), tenantId, normalizedKey, uplinkScript.Trim(), NormalizeDownlink(downlinkScript), isEnabled);
    }

    public Result Update(string uplinkScript, string? downlinkScript, bool isEnabled)
    {
        var bodyCheck = ValidateScripts(uplinkScript, downlinkScript);
        if (bodyCheck.IsFailure) return bodyCheck;

        UplinkScript = uplinkScript.Trim();
        DownlinkScript = NormalizeDownlink(downlinkScript);
        IsEnabled = isEnabled;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>Clave canónica: recortada y en minúsculas, para una unicidad por tenant bien definida.</summary>
    public static string NormalizeKey(string? profileKey) =>
        (profileKey ?? string.Empty).Trim().ToLowerInvariant();

    private static string? NormalizeDownlink(string? downlinkScript) =>
        string.IsNullOrWhiteSpace(downlinkScript) ? null : downlinkScript.Trim();

    private static Result ValidateKey(string normalizedKey)
    {
        if (string.IsNullOrWhiteSpace(normalizedKey))
            return Result.Failure(new Error("ConverterScript.InvalidProfileKey", "La clave de perfil/protocolo es obligatoria."));
        if (normalizedKey.Length > MaxProfileKeyLength)
            return Result.Failure(new Error("ConverterScript.InvalidProfileKey", $"La clave de perfil no puede superar {MaxProfileKeyLength} caracteres."));
        return Result.Success();
    }

    private static Result ValidateScripts(string uplinkScript, string? downlinkScript)
    {
        if (string.IsNullOrWhiteSpace(uplinkScript))
            return Result.Failure(new Error("ConverterScript.EmptyUplink", "El script de subida (uplink) es obligatorio."));
        if (uplinkScript.Length > MaxScriptLength)
            return Result.Failure(new Error("ConverterScript.UplinkTooLarge", $"El script de subida no puede superar {MaxScriptLength} bytes."));
        if (downlinkScript is { Length: > MaxScriptLength })
            return Result.Failure(new Error("ConverterScript.DownlinkTooLarge", $"El script de bajada no puede superar {MaxScriptLength} bytes."));
        return Result.Success();
    }
}
