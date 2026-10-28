using IoT.Application.Protocols;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Protocols.Contracts;
using IoT.Protocols.Dynamic;

namespace IoT.Infrastructure.Protocols;

/// <summary>
/// Implementación de <see cref="ITenantConverterResolver"/>: busca el convertidor dinámico habilitado
/// del tenant para una clave de perfil y, si existe, devuelve un <see cref="IProtocolConnector"/>
/// concreto cuyo convertidor de subida es un <see cref="ScriptableUplinkConverter"/> construido con el
/// script del tenant (y un <see cref="ScriptableDownlinkConverter"/> si hay script de bajada).
///
/// La construcción se cachea por (tenant, clave, hash) en <see cref="ITenantConverterScriptCache"/>;
/// la lectura de base sólo ocurre al expirar el TTL. Las ausencias se cachean en negativo para no
/// consultar en cada trama a los tenants que no personalizan el perfil.
/// </summary>
public sealed class TenantConverterResolver : ITenantConverterResolver
{
    private readonly IProtocolConverterScriptRepository _repository;
    private readonly ITenantConverterScriptCache _cache;

    public TenantConverterResolver(IProtocolConverterScriptRepository repository, ITenantConverterScriptCache cache)
    {
        _repository = repository;
        _cache = cache;
    }

    public async Task<IProtocolConnector?> ResolveUplinkConnectorAsync(Guid tenantId, string profileKey, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || string.IsNullOrWhiteSpace(profileKey))
            return null;

        var key = ProtocolConverterScript.NormalizeKey(profileKey);

        if (_cache.TryGetFresh(tenantId, key, out var cached))
            return cached;

        var script = await _repository.GetByTenantAndProfileKeyAsync(tenantId, key, ct);
        if (script is null || !script.IsEnabled || string.IsNullOrWhiteSpace(script.UplinkScript))
        {
            _cache.StoreNegative(tenantId, key);
            return null;
        }

        var hash = ITenantConverterScriptCache.ComputeHash(script.UplinkScript, script.DownlinkScript);
        return _cache.StorePositive(tenantId, key, hash, () => BuildConnector(key, script));
    }

    private static IProtocolConnector BuildConnector(string profileKey, ProtocolConverterScript script)
    {
        var uplink = new ScriptableUplinkConverter(profileKey, script.UplinkScript);
        ScriptableDownlinkConverter? downlink = string.IsNullOrWhiteSpace(script.DownlinkScript)
            ? null
            : new ScriptableDownlinkConverter(profileKey, script.DownlinkScript!);

        return new ProtocolConnector(
            connectorName: $"tenant-script:{profileKey}",
            transportType: "DYNAMIC",
            uplinkConverter: uplink,
            downlinkConverter: downlink);
    }
}
