using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using IoT.Protocols.Contracts;

namespace IoT.Infrastructure.Protocols;

/// <summary>
/// Caché de proceso de los conectores de convertidor dinámico resueltos, por (tenant, claveDePerfil).
/// Evita tocar la base y re-construir el convertidor (con su AST preparado) en cada trama de la ruta
/// caliente. Entradas:
///   - POSITIVA: un <see cref="IProtocolConnector"/> construido para un hash de script concreto;
///   - NEGATIVA: ausencia de convertidor habilitado (connector null), para no re-consultar la base
///     en cada trama de un tenant que no personaliza ese perfil.
/// Ambas caducan por TTL (corto): como el endpoint de administración y la ingesta pueden vivir en
/// procesos distintos (API vs Worker), el TTL es el mecanismo de invalidación entre procesos. Si el
/// hash del script cambia, se reconstruye; si no, se reutiliza el conector cacheado.
///
/// Aislamiento: la clave incluye el TenantId, así que el conector de un tenant jamás se sirve a otro.
/// </summary>
public interface ITenantConverterScriptCache
{
    bool TryGetFresh(Guid tenantId, string profileKey, out IProtocolConnector? connector);
    IProtocolConnector? StorePositive(Guid tenantId, string profileKey, string scriptHash, Func<IProtocolConnector?> build);
    void StoreNegative(Guid tenantId, string profileKey);
    static string ComputeHash(string uplinkScript, string? downlinkScript)
    {
        var raw = uplinkScript + "\u0000" + (downlinkScript ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}

public sealed class TenantConverterScriptCache : ITenantConverterScriptCache
{
    private sealed class Entry
    {
        public string? Hash;
        public IProtocolConnector? Connector;
        public long ExpiresAtTicks;
    }

    private readonly ConcurrentDictionary<(Guid, string), Entry> _entries = new();
    private readonly TimeSpan _ttl;

    public TenantConverterScriptCache() : this(TimeSpan.FromSeconds(30)) { }

    public TenantConverterScriptCache(TimeSpan ttl) => _ttl = ttl;

    public bool TryGetFresh(Guid tenantId, string profileKey, out IProtocolConnector? connector)
    {
        connector = null;
        if (_entries.TryGetValue((tenantId, profileKey), out var entry))
        {
            if (entry.ExpiresAtTicks >= DateTime.UtcNow.Ticks)
            {
                connector = entry.Connector; // puede ser null (entrada negativa fresca)
                return true;
            }
        }
        return false;
    }

    public IProtocolConnector? StorePositive(Guid tenantId, string profileKey, string scriptHash, Func<IProtocolConnector?> build)
    {
        var key = (tenantId, profileKey);
        var expires = DateTime.UtcNow.Add(_ttl).Ticks;

        // Reutiliza el conector si el hash no cambió; sólo reconstruye (re-prepara el AST) si cambió.
        var entry = _entries.AddOrUpdate(
            key,
            _ => new Entry { Hash = scriptHash, Connector = build(), ExpiresAtTicks = expires },
            (_, existing) =>
            {
                if (!string.Equals(existing.Hash, scriptHash, StringComparison.Ordinal) || existing.Connector == null)
                {
                    existing.Connector = build();
                    existing.Hash = scriptHash;
                }
                existing.ExpiresAtTicks = expires;
                return existing;
            });

        return entry.Connector;
    }

    public void StoreNegative(Guid tenantId, string profileKey)
    {
        var expires = DateTime.UtcNow.Add(_ttl).Ticks;
        _entries[(tenantId, profileKey)] = new Entry { Hash = null, Connector = null, ExpiresAtTicks = expires };
    }
}
