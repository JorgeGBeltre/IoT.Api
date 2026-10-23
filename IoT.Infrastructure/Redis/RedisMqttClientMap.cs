using IoT.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace IoT.Infrastructure.Redis;

/// <summary>
/// Implementación Redis del mapa ChipId↔ClientId (clave <c>mqtt:client:{chipId}</c>).
/// Portado de References/Mqtt-Broker RedisClientMapping, adaptado al
/// IConnectionMultiplexer ya registrado en la plataforma.
/// </summary>
public sealed class RedisMqttClientMap : IMqttClientMap
{
    private const string KeyPrefix = "mqtt:client:";
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisMqttClientMap> _logger;

    public RedisMqttClientMap(IConnectionMultiplexer redis, ILogger<RedisMqttClientMap> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    private IDatabase Db => _redis.GetDatabase();

    public Task SetOrUpdateAsync(string chipId, string clientId, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chipId)) return Task.CompletedTask;
        return Db.StringSetAsync($"{KeyPrefix}{chipId}", clientId, expiry);
    }

    public async Task<string?> GetClientIdAsync(string chipId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chipId)) return null;
        var value = await Db.StringGetAsync($"{KeyPrefix}{chipId}");
        return value.HasValue ? value.ToString() : null;
    }

    public async Task<string?> GetChipIdByClientIdAsync(string clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return null;
        try
        {
            var endpoint = _redis.GetEndPoints().FirstOrDefault();
            if (endpoint is null) return null;
            var server = _redis.GetServer(endpoint);
            foreach (var key in server.Keys(pattern: $"{KeyPrefix}*"))
            {
                var value = await Db.StringGetAsync(key);
                if (value == clientId)
                {
                    return key.ToString().Replace(KeyPrefix, string.Empty);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo resolver ChipId inverso para ClientId {ClientId}", clientId);
        }
        return null;
    }

    public Task RemoveAsync(string chipId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chipId)) return Task.CompletedTask;
        return Db.KeyDeleteAsync($"{KeyPrefix}{chipId}");
    }
}
