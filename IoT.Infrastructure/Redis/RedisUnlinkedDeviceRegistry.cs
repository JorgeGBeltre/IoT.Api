using System.Collections.Concurrent;
using System.Text.Json;
using IoT.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace IoT.Infrastructure.Redis;

public sealed class RedisUnlinkedDeviceRegistry : IUnlinkedDeviceRegistry
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(30);
    private const string KeyPrefix = "iot:device:unlinked:";

    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<RedisUnlinkedDeviceRegistry> _logger;
    private readonly ConcurrentDictionary<string, UnlinkedDeviceTombstone> _memoryCache =
        new(StringComparer.OrdinalIgnoreCase);

    public RedisUnlinkedDeviceRegistry(
        ILogger<RedisUnlinkedDeviceRegistry> logger,
        IConnectionMultiplexer? redis = null)
    {
        _logger = logger;
        _redis = redis;
    }

    public async Task RecordUnlinkedAsync(
        UnlinkedDeviceTombstone tombstone,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tombstone.ChipId))
        {
            return;
        }

        string normalized = NormalizeChipId(tombstone.ChipId);
        _memoryCache[normalized] = tombstone;

        if (_redis is null || !_redis.IsConnected)
        {
            return;
        }

        try
        {
            var db = _redis.GetDatabase();
            string json = JsonSerializer.Serialize(tombstone);
            await db.StringSetAsync(KeyPrefix + normalized, json, DefaultTtl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo persistir el tombstone de desvinculación en Redis para {ChipId}; se mantiene en memoria.", normalized);
        }
    }

    public async Task<UnlinkedDeviceTombstone?> GetAsync(
        string chipId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(chipId))
        {
            return null;
        }

        string normalized = NormalizeChipId(chipId);
        if (_memoryCache.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        if (_redis is null || !_redis.IsConnected)
        {
            return null;
        }

        try
        {
            var db = _redis.GetDatabase();
            var raw = await db.StringGetAsync(KeyPrefix + normalized);
            if (!raw.HasValue)
            {
                return null;
            }

            var parsed = JsonSerializer.Deserialize<UnlinkedDeviceTombstone>(raw.ToString());
            if (parsed is not null)
            {
                _memoryCache[normalized] = parsed;
            }
            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo consultar el tombstone de desvinculación en Redis para {ChipId}.", normalized);
            return null;
        }
    }

    public async Task ClearAsync(
        string chipId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(chipId))
        {
            return;
        }

        string normalized = NormalizeChipId(chipId);
        _memoryCache.TryRemove(normalized, out _);

        if (_redis is null || !_redis.IsConnected)
        {
            return;
        }

        try
        {
            var db = _redis.GetDatabase();
            await db.KeyDeleteAsync(KeyPrefix + normalized);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo limpiar el tombstone de desvinculación en Redis para {ChipId}.", normalized);
        }
    }

    private static string NormalizeChipId(string chipId) => chipId.Trim().ToUpperInvariant();
}

