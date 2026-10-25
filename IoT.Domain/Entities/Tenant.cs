using IoT.Domain.Common;

namespace IoT.Domain.Entities;

public sealed class Tenant : AggregateRoot<Guid>
{
    public string Name { get; private set; }
    public string Code { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    // Credencial de bootstrap MQTT ACOTADA A ESTE TENANT. Existe para publicadores que
    // hablan por VARIOS equipos del tenant (simulador, herramientas del operador), que no
    // pueden usar una ApiKey por dispositivo. Antes esta credencial era una lista global en
    // configuración, la MISMA para todos los tenants: cualquiera que la tuviera inyectaba
    // telemetría atribuida a dispositivos de CUALQUIER tenant (MEDIUM-2). Al colgarla del
    // tenant, el broker sabe a qué tenant pertenece la sesión y la ingesta rechaza toda
    // trama cuyo dispositivo resuelto sea de otro tenant. Se guarda sólo el hash y una
    // huella corta, igual que la ApiKey por dispositivo; el texto plano se muestra una vez.
    public string? BootstrapApiKeyHash { get; private set; }
    public string? BootstrapApiKeyFingerprint { get; private set; }
    public DateTime? BootstrapApiKeyIssuedAt { get; private set; }
    public string? BootstrapApiKeyIssuedBy { get; private set; }

    public bool HasBootstrapApiKey => !string.IsNullOrEmpty(BootstrapApiKeyHash);

    private Tenant(Guid id, string name, string code) : base(id)
    {
        Name = name;
        Code = code;
        IsActive = true;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public static Result<Tenant> Create(string name, string code)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result<Tenant>.Failure(new Error("Tenant.InvalidName", "Tenant name cannot be empty."));

        if (string.IsNullOrWhiteSpace(code))
            return Result<Tenant>.Failure(new Error("Tenant.InvalidCode", "Tenant code cannot be empty."));

        return new Tenant(Guid.NewGuid(), name.Trim(), code.Trim().ToUpperInvariant());
    }

    public static Result<Tenant> CreateWithId(Guid id, string name, string code)
    {
        if (id == Guid.Empty)
            return Result<Tenant>.Failure(new Error("Tenant.InvalidId", "Tenant ID cannot be empty."));

        if (string.IsNullOrWhiteSpace(name))
            return Result<Tenant>.Failure(new Error("Tenant.InvalidName", "Tenant name cannot be empty."));

        if (string.IsNullOrWhiteSpace(code))
            return Result<Tenant>.Failure(new Error("Tenant.InvalidCode", "Tenant code cannot be empty."));

        return new Tenant(id, name.Trim(), code.Trim().ToUpperInvariant());
    }

    public void Deactivate()
    {
        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Activate()
    {
        IsActive = true;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Asigna o rota la credencial de bootstrap del tenant. Recibe el hash (no el texto
    /// plano) y una huella corta no reversible; el texto plano lo genera y lo muestra una
    /// sola vez la capa de aplicación, igual que con la ApiKey por dispositivo.
    /// </summary>
    public Result SetBootstrapApiKey(string hash, string fingerprint, string? issuedBy = null)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return Result.Failure(new Error("Tenant.InvalidBootstrapHash", "El hash de la credencial de bootstrap es obligatorio."));
        if (string.IsNullOrWhiteSpace(fingerprint))
            return Result.Failure(new Error("Tenant.InvalidBootstrapFingerprint", "La huella de la credencial de bootstrap es obligatoria."));

        BootstrapApiKeyHash = hash;
        BootstrapApiKeyFingerprint = fingerprint;
        BootstrapApiKeyIssuedAt = DateTime.UtcNow;
        BootstrapApiKeyIssuedBy = issuedBy;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>
    /// Revoca la credencial de bootstrap del tenant. A partir de aquí ningún publicador
    /// multi-equipo puede autenticarse por ese tenant hasta que se emita una nueva.
    /// </summary>
    public void ClearBootstrapApiKey()
    {
        BootstrapApiKeyHash = null;
        BootstrapApiKeyFingerprint = null;
        BootstrapApiKeyIssuedAt = null;
        BootstrapApiKeyIssuedBy = null;
        UpdatedAt = DateTime.UtcNow;
    }
}
