using IoT.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace IoT.Infrastructure.Redis;

/// <summary>
/// Implementación Redis del mapa ChipId↔ClientId con ÍNDICE INVERSO ATÓMICO.
///
/// Claves:
///   <c>mqtt:client:{chipId}</c>     → clientId  (directo: push server→device)
///   <c>mqtt:clientid:{clientId}</c> → chipId    (inverso: presencia en la desconexión)
///
/// El índice inverso evita el SCAN O(N) + GET por clave que se hacía en CADA desconexión
/// MQTT (hot path con churn de reconexiones de miles de trackers): ahora es un GET O(1).
///
/// Ambas mutaciones (alta y baja) se ejecutan con un script Lua, por dos razones:
///  1. ATOMICIDAD: son secuencias GET→DEL→SET; partidas en varios round-trips, dos
///     conexiones concurrentes del mismo dispositivo (o de dos dispositivos que se
///     intercambian el clientId) dejaban claves huérfanas o borraban el mapeo vivo.
///  2. BORRADO CONDICIONAL: la baja compara el clientId esperado contra el valor actual
///     y NO borra si ya no coincide. Esto es lo que impide que la desconexión tardía de
///     una sesión antigua (takeover al reconectar) mate el mapeo de la conexión nueva.
///
/// Nota de despliegue: los scripts construyen la clave inversa dentro de Lua a partir del
/// valor leído, así que asumen una instancia Redis NO particionada (single node, el modo
/// del docker-compose). Si algún día se migra a Redis Cluster hay que fijar ambas claves
/// al mismo hash slot con hashtags.
/// </summary>
public sealed class RedisMqttClientMap : IMqttClientMap
{
    private const string KeyPrefix = "mqtt:client:";
    private const string ReverseKeyPrefix = "mqtt:clientid:";

    /// <summary>
    /// Alta/actualización atómica del par de claves.
    /// KEYS[1]=directo(chipId)  KEYS[2]=inverso(clientId)
    /// ARGV[1]=chipId  ARGV[2]=clientId  ARGV[3]=ttl en segundos (0 = sin TTL)
    /// ARGV[4]=prefijo inverso  ARGV[5]=prefijo directo
    /// </summary>
    private const string SetScript = @"
local prevClient = redis.call('GET', KEYS[1])
if prevClient and prevClient ~= ARGV[2] then
  -- el chip se reconectó con OTRO clientId: borrar su inverso anterior
  redis.call('DEL', ARGV[4] .. prevClient)
end
local prevChip = redis.call('GET', KEYS[2])
if prevChip and prevChip ~= ARGV[1] then
  -- el clientId quedó reasignado a otro chip: borrar el directo del chip anterior
  redis.call('DEL', ARGV[5] .. prevChip)
end
local ttl = tonumber(ARGV[3])
if ttl and ttl > 0 then
  redis.call('SET', KEYS[1], ARGV[2], 'EX', ttl)
  redis.call('SET', KEYS[2], ARGV[1], 'EX', ttl)
else
  redis.call('SET', KEYS[1], ARGV[2])
  redis.call('SET', KEYS[2], ARGV[1])
end
return 1";

    /// <summary>
    /// Baja atómica y CONDICIONAL del par de claves.
    /// KEYS[1]=directo(chipId)
    /// ARGV[1]=clientId esperado ('' = cualquiera)  ARGV[2]=prefijo inverso
    /// Devuelve 1 si borró, 0 si no había mapeo, -1 si el mapeo apunta a otro clientId
    /// (desconexión obsoleta: se preserva el mapeo vivo).
    /// </summary>
    private const string RemoveScript = @"
local current = redis.call('GET', KEYS[1])
if not current then
  return 0
end
if ARGV[1] ~= '' and current ~= ARGV[1] then
  return -1
end
redis.call('DEL', KEYS[1])
redis.call('DEL', ARGV[2] .. current)
return 1";

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisMqttClientMap> _logger;

    public RedisMqttClientMap(IConnectionMultiplexer redis, ILogger<RedisMqttClientMap> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    private IDatabase Db => _redis.GetDatabase();

    private static string ForwardKey(string chipId) => $"{KeyPrefix}{chipId}";
    private static string ReverseKey(string clientId) => $"{ReverseKeyPrefix}{clientId}";

    public async Task SetOrUpdateAsync(string chipId, string clientId, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chipId) || string.IsNullOrWhiteSpace(clientId)) return;

        var ttlSeconds = expiry.HasValue ? (long)Math.Max(1, expiry.Value.TotalSeconds) : 0;

        await Db.ScriptEvaluateAsync(
            SetScript,
            new RedisKey[] { ForwardKey(chipId), ReverseKey(clientId) },
            new RedisValue[] { chipId, clientId, ttlSeconds, ReverseKeyPrefix, KeyPrefix });
    }

    public async Task<string?> GetClientIdAsync(string chipId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chipId)) return null;
        var value = await Db.StringGetAsync(ForwardKey(chipId));
        return value.HasValue ? value.ToString() : null;
    }

    public async Task<string?> GetChipIdByClientIdAsync(string clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return null;
        try
        {
            // O(1) por el índice inverso. No hay fallback por SCAN: el inverso se escribe
            // siempre en la misma operación atómica que el directo, así que un miss
            // significa "no hay mapeo" (o ya se dio de baja), no "hay que buscarlo".
            var reverse = await Db.StringGetAsync(ReverseKey(clientId));
            return reverse.HasValue ? reverse.ToString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo resolver ChipId inverso para ClientId {ClientId}", clientId);
            return null;
        }
    }

    public async Task<bool> RemoveAsync(string chipId, string? expectedClientId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chipId)) return false;

        try
        {
            var result = await Db.ScriptEvaluateAsync(
                RemoveScript,
                new RedisKey[] { ForwardKey(chipId) },
                new RedisValue[] { expectedClientId ?? string.Empty, ReverseKeyPrefix });

            var code = (long)result;
            if (code == -1)
            {
                _logger.LogDebug(
                    "Baja de mapeo ignorada para ChipId {ChipId}: el mapeo ya apunta a otra conexión " +
                    "(desconexión obsoleta de ClientId {ClientId}).", chipId, expectedClientId);
                return false;
            }

            return code == 1;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo dar de baja el mapeo MQTT de ChipId {ChipId}", chipId);
            return false;
        }
    }
}
