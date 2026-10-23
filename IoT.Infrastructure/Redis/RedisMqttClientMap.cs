using IoT.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace IoT.Infrastructure.Redis;

/// <summary>
/// Implementación Redis del mapa ChipId↔ClientId con ÍNDICE INVERSO.
///
/// Claves:
///   <c>mqtt:client:{chipId}</c>    → clientId  (directo: push server→device)
///   <c>mqtt:clientid:{clientId}</c> → chipId    (inverso: presencia en la desconexión)
///
/// El índice inverso evita el SCAN O(N) + GET por clave que se hacía en CADA desconexión
/// MQTT (hot path con churn de reconexiones de miles de trackers): ahora es un GET O(1).
/// Ambas claves se mantienen en pareja (alta, cambio de clientId y baja) para no dejar
/// huérfanas; se conserva un fallback de SCAN sólo para mapeos creados antes de este índice.
/// </summary>
public sealed class RedisMqttClientMap : IMqttClientMap
{
    private const string KeyPrefix = "mqtt:client:";
    private const string ReverseKeyPrefix = "mqtt:clientid:";

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

        var db = Db;

        // Si el dispositivo se reconectó con OTRO clientId, borrar el inverso anterior
        // para no dejar una clave huérfana apuntando a este chipId.
        var previousClientId = await db.StringGetAsync(ForwardKey(chipId));
        if (previousClientId.HasValue && previousClientId != clientId)
        {
            await db.KeyDeleteAsync(ReverseKey(previousClientId!));
        }

        await db.StringSetAsync(ForwardKey(chipId), clientId, expiry);
        await db.StringSetAsync(ReverseKey(clientId), chipId, expiry);
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
            // Camino normal: O(1) por el índice inverso.
            var reverse = await Db.StringGetAsync(ReverseKey(clientId));
            if (reverse.HasValue) return reverse.ToString();

            // Fallback (sólo mapeos previos a la existencia del índice): SCAN una vez y
            // rellenar el inverso para que la siguiente búsqueda ya sea O(1).
            return await ScanForChipIdAndBackfillAsync(clientId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo resolver ChipId inverso para ClientId {ClientId}", clientId);
            return null;
        }
    }

    public async Task RemoveAsync(string chipId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chipId)) return;

        var db = Db;
        // Leer el clientId asociado ANTES de borrar el directo, para poder borrar su inverso.
        var clientId = await db.StringGetAsync(ForwardKey(chipId));
        await db.KeyDeleteAsync(ForwardKey(chipId));
        if (clientId.HasValue)
        {
            await db.KeyDeleteAsync(ReverseKey(clientId!));
        }
    }

    private async Task<string?> ScanForChipIdAndBackfillAsync(string clientId)
    {
        var endpoint = _redis.GetEndPoints().FirstOrDefault();
        if (endpoint is null) return null;

        var server = _redis.GetServer(endpoint);
        foreach (var key in server.Keys(pattern: $"{KeyPrefix}*"))
        {
            var value = await Db.StringGetAsync(key);
            if (value == clientId)
            {
                string chipId = key.ToString()[KeyPrefix.Length..];
                _logger.LogDebug(
                    "ClientId {ClientId} resuelto por SCAN (mapeo previo al índice inverso); rellenando índice.",
                    clientId);
                await Db.StringSetAsync(ReverseKey(clientId), chipId);
                return chipId;
            }
        }
        return null;
    }
}
