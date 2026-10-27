using IoT.Domain.Common;
using IoT.Domain.Enums;

namespace IoT.Domain.Entities;

/// <summary>
/// Par clave-valor asociado a un dispositivo, con un <see cref="AttributeScope"/> (modelo de
/// atributos de ThingsBoard, fase P1a). Es un dato PROPIEDAD DEL DISPOSITIVO: <see cref="ITenantScoped"/>
/// (aislamiento por tenant de ADR-003 vía el filtro global de EF) y con FK a <c>devices</c>; su recorte
/// por subárbol (ADR-013) lo da la RLS de PostgreSQL a través del <c>Customer</c> del device dueño,
/// igual que <c>positions</c>/<c>telemetry_records</c> (no es <see cref="ICustomerScoped"/>: no lleva
/// <c>CustomerId</c> propio).
///
/// <para>El <see cref="Value"/> se persiste como <c>text</c> (ADR-014 D2): almacén clave-valor que
/// acepta cadenas y escalares directos (<c>"42"</c>, <c>"true"</c>, <c>"online"</c>) y también la cadena
/// vacía (un valor null se normaliza a <c>""</c>), sin exigir JSON válido. Un único valor por combinación
/// (<c>TenantId</c>, <c>DeviceId</c>, <c>Scope</c>, <c>Key</c>), garantizado por índice único.</para>
/// </summary>
public sealed class DeviceAttribute : AggregateRoot<Guid>, ITenantScoped
{
    public Guid TenantId { get; private set; }
    public Guid DeviceId { get; private set; }
    public AttributeScope Scope { get; private set; }
    public string Key { get; private set; }
    /// <summary>Valor como cadena libre (columna <c>text</c>); acepta escalares/strings directos y "".</summary>
    public string Value { get; private set; }
    public DateTime LastUpdatedAt { get; private set; }

    private DeviceAttribute(
        Guid id,
        Guid tenantId,
        Guid deviceId,
        AttributeScope scope,
        string key,
        string value) : base(id)
    {
        TenantId = tenantId;
        DeviceId = deviceId;
        Scope = scope;
        Key = key;
        Value = value;
        LastUpdatedAt = DateTime.UtcNow;
    }

    public static Result<DeviceAttribute> Create(
        Guid tenantId,
        Guid deviceId,
        AttributeScope scope,
        string key,
        string value)
    {
        if (tenantId == Guid.Empty)
            return Result<DeviceAttribute>.Failure(new Error("DeviceAttribute.InvalidTenant", "TenantId cannot be empty."));

        if (deviceId == Guid.Empty)
            return Result<DeviceAttribute>.Failure(new Error("DeviceAttribute.InvalidDevice", "DeviceId cannot be empty."));

        if (string.IsNullOrWhiteSpace(key))
            return Result<DeviceAttribute>.Failure(new Error("DeviceAttribute.InvalidKey", "Attribute key is required."));

        return new DeviceAttribute(
            Guid.NewGuid(),
            tenantId,
            deviceId,
            scope,
            key.Trim(),
            value ?? string.Empty);
    }

    /// <summary>Reemplaza el valor y refresca <see cref="LastUpdatedAt"/>.</summary>
    public void UpdateValue(string value)
    {
        Value = value ?? string.Empty;
        LastUpdatedAt = DateTime.UtcNow;
    }
}
