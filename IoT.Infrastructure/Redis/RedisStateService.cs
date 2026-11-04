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

    private const string MonotonicStateVersionLua = """
        local cur = tonumber(redis.call('GET', KEYS[1]))
        local incoming = tonumber(ARGV[1])
        if cur ~= nil and incoming < cur then
            return 1
        end
        redis.call('SET', KEYS[1], tostring(incoming), 'EX', 2592000)
        return 0
        """;

    public async Task<bool> IsDuplicateMessageAsync(string deviceIdentifier, DateTime recordedAt, string payloadHash, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        string key = $"idempotency:{deviceIdentifier}:{recordedAt.Ticks}:{payloadHash}";
        bool wasSet = await _db.StringSetAsync(key, "1", ttl, When.NotExists);
        return !wasSet;
    }

    public async Task<bool> IsStaleDeviceStateVersionAsync(Guid tenantId, Guid deviceId, long stateVersion, CancellationToken cancellationToken = default)
    {
        if (stateVersion <= 0)
        {
            return false;
        }

        string key = $"statever:tenant:{tenantId}:unit:{deviceId}";
        try
        {
            var res = await _db.ScriptEvaluateAsync(
                MonotonicStateVersionLua,
                new RedisKey[] { key },
                new RedisValue[] { stateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) });

            return !res.IsNull && (int)res == 1;
        }
        catch
        {
            return false;
        }
    }
}
