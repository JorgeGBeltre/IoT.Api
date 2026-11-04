using System.Globalization;
using System.Runtime.CompilerServices;
using IoT.Application.Common.Interfaces;
using IoT.Application.Common.Observability;
using IoT.Application.Common.TelemetryQueue;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace IoT.Infrastructure.TelemetryQueue;

/// <summary>
/// Implementación de telemetría secuencial por origen mediante Redis Streams (ADR-016).
/// Particiona los eventos en N streams independientes 'iot:telemetry:stream:{partition}'.
/// Cada stream es consumido de forma estrictamente secuencial mediante Consumer Groups con ACK.
/// </summary>
public sealed class RedisTelemetryStreamService : ITelemetryQueuePublisher, ITelemetryQueueConsumer
{
    private const string DefaultConsumerGroup = "iot-telemetry-processors";
    private const string WakeChannelName = "iot:telemetry:wake";
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisTelemetryStreamService> _logger;
    private readonly int _partitionCount;
    private readonly int _maxStreamLength;
    private readonly string _consumerGroup;
    private readonly string _consumerName;
    private readonly SemaphoreSlim[] _partitionSignals;
    private int _wakeSubscribed;

    public int PartitionCount => _partitionCount;

    public RedisTelemetryStreamService(
        IConnectionMultiplexer redis,
        ILogger<RedisTelemetryStreamService> logger,
        int partitionCount = 8,
        int maxStreamLength = 10000,
        string? consumerGroup = null,
        string? consumerName = null)
    {
        _redis = redis;
        _logger = logger;
        _partitionCount = Math.Max(1, partitionCount);
        _maxStreamLength = Math.Max(100, maxStreamLength);
        _consumerGroup = consumerGroup ?? DefaultConsumerGroup;
        _consumerName = consumerName ?? $"worker-{Environment.MachineName}-{Guid.NewGuid():N}";
        _partitionSignals = new SemaphoreSlim[_partitionCount];
        for (int i = 0; i < _partitionCount; i++)
        {
            _partitionSignals[i] = new SemaphoreSlim(0, 1);
        }
    }

    private void SignalPartition(int partition)
    {
        if ((uint)partition >= (uint)_partitionSignals.Length)
        {
            return;
        }

        var sem = _partitionSignals[partition];
        if (sem.CurrentCount == 0)
        {
            try { sem.Release(); } catch (SemaphoreFullException) { }
        }
    }

    private async Task EnsureWakeSubscriptionAsync()
    {
        if (Interlocked.CompareExchange(ref _wakeSubscribed, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var subscriber = _redis.GetSubscriber();
            if (subscriber is null)
            {
                return;
            }

            await subscriber.SubscribeAsync(RedisChannel.Literal(WakeChannelName), (_, value) =>
            {
                if (value.HasValue && int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
                {
                    SignalPartition(p);
                }
            });
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _wakeSubscribed, 0);
            _logger.LogDebug(ex, "Could not subscribe to Redis telemetry wake channel; using fallback 50ms poll.");
        }
    }

    private static string GetStreamKey(int partition) => $"iot:telemetry:stream:{partition}";

    public async Task<bool> EnqueueAsync(TelemetryQueueItem item, CancellationToken cancellationToken = default)
    {
        try
        {
            string originatorKey = OriginatorPartitionResolver.ResolveOriginatorKey(
                item.Topic, item.TransportDeviceIdentifier);
            int partition = OriginatorPartitionResolver.GetPartitionIndex(originatorKey, _partitionCount);
            string streamKey = GetStreamKey(partition);

            var db = _redis.GetDatabase();

            var entries = new NameValueEntry[]
            {
                new("topic", item.Topic),
                new("payload", item.Payload),
                new("source", item.Source),
                new("proto", item.TransportProtocol ?? string.Empty),
                new("chipId", item.TransportDeviceIdentifier ?? string.Empty),
                new("msgId", item.TransportMessageId ?? string.Empty),
                new("tenantId", item.CredentialTenantId?.ToString() ?? string.Empty),
                new("enqueuedAt", item.EnqueuedAt == default
                    ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                    : item.EnqueuedAt.ToString("O", CultureInfo.InvariantCulture))
            };

            await db.StreamAddAsync(streamKey, entries, maxLength: _maxStreamLength, useApproximateMaxLength: true);

            SignalPartition(partition);
            try
            {
                var subscriber = _redis.GetSubscriber();
                if (subscriber is not null)
                {
                    await subscriber.PublishAsync(
                        RedisChannel.Literal(WakeChannelName),
                        partition.ToString(CultureInfo.InvariantCulture),
                        CommandFlags.FireAndForget);
                }
            }
            catch
            {
                // Best-effort wake signal; partition consumer has 50ms fallback timeout.
            }

            IoTMetrics.TelemetryQueueEnqueuedCounter.Add(1,
                new KeyValuePair<string, object?>("partition", partition),
                new KeyValuePair<string, object?>("engine", "redis"));

            _logger.LogTrace("Enqueued telemetry item for originator '{Originator}' to Redis Stream {StreamKey}",
                originatorKey, streamKey);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue telemetry message to Redis Streams on topic '{Topic}'", item.Topic);
            return false;
        }
    }

    public async IAsyncEnumerable<TelemetryQueueMessage> ReadPartitionAsync(
        int partition,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (partition < 0 || partition >= _partitionCount)
        {
            throw new ArgumentOutOfRangeException(nameof(partition),
                $"Partition must be between 0 and {_partitionCount - 1}.");
        }

        string streamKey = GetStreamKey(partition);
        var db = _redis.GetDatabase();

        await EnsureConsumerGroupCreatedAsync(db, streamKey);
        await EnsureWakeSubscriptionAsync();

        while (!cancellationToken.IsCancellationRequested)
        {
            StreamEntry[]? entries = null;
            try
            {
                // Lee nuevos mensajes no entregados ('>') en el stream de esta partición
                entries = await db.StreamReadGroupAsync(
                    streamKey,
                    _consumerGroup,
                    _consumerName,
                    position: StreamPosition.NewMessages,
                    count: 10);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Transient error reading Redis Stream {StreamKey}. Retrying in 1s...", streamKey);
                try { await Task.Delay(1000, cancellationToken); } catch (OperationCanceledException) { break; }
                continue;
            }

            if (entries is null || entries.Length == 0)
            {
                try
                {
                    await _partitionSignals[partition].WaitAsync(TimeSpan.FromMilliseconds(50), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            foreach (var entry in entries)
            {
                var item = ParseStreamEntry(entry);
                if (item is not null)
                {
                    yield return new TelemetryQueueMessage(entry.Id.ToString(), partition, item);
                }
                else
                {
                    // Mensaje malformado o corrupto: se hace ACK para no bloquear la partición
                    await db.StreamAcknowledgeAsync(streamKey, _consumerGroup, entry.Id);
                }
            }
        }
    }

    public async Task AcknowledgeAsync(TelemetryQueueMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            string streamKey = GetStreamKey(message.Partition);
            var db = _redis.GetDatabase();
            await db.StreamAcknowledgeAsync(streamKey, _consumerGroup, message.MessageId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ACK Redis Stream message {MessageId} on partition {Partition}",
                message.MessageId, message.Partition);
        }
    }

    private async Task EnsureConsumerGroupCreatedAsync(IDatabase db, string streamKey)
    {
        try
        {
            await db.StreamCreateConsumerGroupAsync(streamKey, _consumerGroup, StreamPosition.Beginning, createStream: true);
            _logger.LogInformation("Created consumer group '{Group}' on stream '{StreamKey}'", _consumerGroup, streamKey);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase))
        {
            // El grupo ya existía previamente, comportamiento normal.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notice while ensuring consumer group '{Group}' on stream '{StreamKey}'", _consumerGroup, streamKey);
        }
    }

    private static TelemetryQueueItem? ParseStreamEntry(StreamEntry entry)
    {
        var dict = entry.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString());

        if (!dict.TryGetValue("topic", out var topic) || !dict.TryGetValue("payload", out var payload))
        {
            return null;
        }

        dict.TryGetValue("source", out var source);
        dict.TryGetValue("proto", out var proto);
        dict.TryGetValue("chipId", out var chipId);
        dict.TryGetValue("msgId", out var msgId);
        dict.TryGetValue("tenantId", out var tenantIdStr);
        dict.TryGetValue("enqueuedAt", out var enqueuedAtStr);

        Guid? tenantId = Guid.TryParse(tenantIdStr, out var g) ? g : null;
        DateTimeOffset enqueuedAt = DateTimeOffset.TryParse(enqueuedAtStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt
            : DateTimeOffset.UtcNow;

        return new TelemetryQueueItem(
            Topic: topic,
            Payload: payload,
            Source: string.IsNullOrWhiteSpace(source) ? "real" : source,
            TransportProtocol: string.IsNullOrWhiteSpace(proto) ? null : proto,
            TransportDeviceIdentifier: string.IsNullOrWhiteSpace(chipId) ? null : chipId,
            TransportMessageId: string.IsNullOrWhiteSpace(msgId) ? null : msgId,
            CredentialTenantId: tenantId,
            EnqueuedAt: enqueuedAt);
    }
}
