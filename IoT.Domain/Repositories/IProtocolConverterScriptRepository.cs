using IoT.Domain.Entities;

namespace IoT.Domain.Repositories;

/// <summary>
/// Acceso a los convertidores dinámicos por tenant (<see cref="ProtocolConverterScript"/>). Todas las
/// lecturas/escrituras quedan acotadas al tenant por el filtro global de EF y, en caliente, por la
/// RLS de PostgreSQL (política <c>tenant_isolation</c>): un repositorio nunca ve ni escribe scripts
/// de otro tenant.
/// </summary>
public interface IProtocolConverterScriptRepository
{
    /// <summary>Convertidor de un tenant para una clave de perfil ya normalizada (o null si no existe).</summary>
    Task<ProtocolConverterScript?> GetByTenantAndProfileKeyAsync(Guid tenantId, string profileKey, CancellationToken ct);

    /// <summary>Todos los convertidores del tenant actual.</summary>
    Task<IReadOnlyList<ProtocolConverterScript>> GetByTenantAsync(Guid tenantId, CancellationToken ct);

    Task AddAsync(ProtocolConverterScript script, CancellationToken ct);

    void Update(ProtocolConverterScript script);
}
