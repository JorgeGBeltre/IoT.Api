using System.Collections.Concurrent;
using System.Globalization;
using IoT.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace IoT.Infrastructure.Redis;

public sealed class RedisCommandSequenceGenerator : ICommandSequenceGenerator
{
    private const string MonotonicLuaScript = """
        local cur = tonumber(redis.call('GET', KEYS[1]) or '0')
        local floor = tonumber(ARGV[1])
        local nextVal = cur + 1
        if floor > nextVal then
            nextVal = floor
        end
        redis.call('SET', KEYS[1], tostring(nextVal), 'EX', 2592000)
        return nextVal
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisCommandSequenceGenerator> _logger;
    private readonly ConcurrentDictionary<Guid, long> _localFallback = new();

    public RedisCommandSequenceGenerator(
        IConnectionMultiplexer redis,
        ILogger<RedisCommandSequenceGenerator> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<long> NextSequenceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        long floorMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string key = $"iot:cmd:seq:{deviceId:N}";

        try
        {
            var db = _redis.GetDatabase();
            var result = await db.ScriptEvaluateAsync(
                MonotonicLuaScript,
                new RedisKey[] { key },
                new RedisValue[] { floorMs.ToString(CultureInfo.InvariantCulture) });

            if (!result.IsNull && long.TryParse(result.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long seq) && seq > 0)
            {
                _localFallback.AddOrUpdate(deviceId, seq, (_, prev) => Math.Max(prev, seq));
                return seq;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis sequence generator degraded for device {DeviceId}; using monotonic in-process fallback.", deviceId);
        }

        return _localFallback.AddOrUpdate(
            deviceId,
            floorMs,
            (_, prev) => Math.Max(prev + 1, floorMs));
    }
}

