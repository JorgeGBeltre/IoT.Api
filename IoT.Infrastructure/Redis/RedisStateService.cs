using System.Text.Json;
using IoT.Application.Common.Interfaces;
using StackExchange.Redis;
using DomainGeoPosition = IoT.Domain.ValueObjects.GeoPosition;

namespace IoT.Infrastructure.Redis;

public sealed class RedisStateService : IRedisStateService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;

    public RedisStateService(IConnectionMultiplexer redis)
    {
        _redis = redis;
        _db = redis.GetDatabase();
    }

    public async Task<bool> TryUpdateLatestPositionAsync(
        Guid tenantId,
        Guid deviceId,
        string deviceIdentifier,
        DomainGeoPosition position,
        DateTime recordedAt,
        CancellationToken cancellationToken = default)
    {
        string key = $"gps:tenant:{tenantId}:unit:{deviceId}:latest";

        var existingJson = await _db.StringGetAsync(key);
        long version = 1;

        if (existingJson.HasValue)
        {
            try
            {
                var existing = JsonSerializer.Deserialize<LivePositionSnapshot>(existingJson.ToString());
                if (existing != null)
                {
                    // Out-of-order check: if incoming packet is older, do NOT overwrite latest position in Redis
                    if (recordedAt < existing.RecordedAt)
                    {
                        return false;
                    }
                    version = existing.Version + 1;
                }
            }
            catch
            {
                // Fallback on corrupt JSON
            }
        }

        var newState = new LivePositionSnapshot
        {
            DeviceId = deviceId,
            DeviceIdentifier = deviceIdentifier,
            Latitude = position.Latitude,
            Longitude = position.Longitude,
            SpeedKph = position.SpeedKph,
            Heading = position.Heading,
            RecordedAt = recordedAt,
            ReceivedAt = DateTime.UtcNow,
            Version = version
        };

        string serialized = JsonSerializer.Serialize(newState);
        await _db.StringSetAsync(key, serialized);

        string setKey = $"gps:tenant:{tenantId}:units";
        await _db.SetAddAsync(setKey, deviceId.ToString());

        return true;
    }

    public async Task<LivePositionSnapshot?> GetLatestPositionAsync(Guid tenantId, Guid deviceId, CancellationToken cancellationToken = default)
    {
        string key = $"gps:tenant:{tenantId}:unit:{deviceId}:latest";
        var val = await _db.StringGetAsync(key);
        if (!val.HasValue) return null;

        return JsonSerializer.Deserialize<LivePositionSnapshot>(val.ToString());
    }

    public async Task<IReadOnlyList<LivePositionSnapshot>> GetFleetSnapshotAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        string setKey = $"gps:tenant:{tenantId}:units";
        var members = await _db.SetMembersAsync(setKey);

        if (members == null || members.Length == 0)
            return [];

        var list = new List<LivePositionSnapshot>(members.Length);
        foreach (var m in members)
        {
            if (Guid.TryParse(m.ToString(), out var devId))
            {
                var pos = await GetLatestPositionAsync(tenantId, devId, cancellationToken);
                if (pos != null)
                    list.Add(pos);
            }
        }

        return list;
    }

    public async Task<bool> IsDuplicateMessageAsync(string deviceIdentifier, DateTime recordedAt, string payloadHash, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        string key = $"idempotency:{deviceIdentifier}:{recordedAt.Ticks}:{payloadHash}";
        bool wasSet = await _db.StringSetAsync(key, "1", ttl, When.NotExists);
        return !wasSet;
    }
}
