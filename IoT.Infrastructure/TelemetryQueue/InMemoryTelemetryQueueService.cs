using System.Runtime.CompilerServices;
using System.Threading.Channels;
using IoT.Application.Common.Interfaces;
using IoT.Application.Common.Observability;
using IoT.Application.Common.TelemetryQueue;
using Microsoft.Extensions.Logging;

namespace IoT.Infrastructure.TelemetryQueue;

/// <summary>
/// Implementación particionada en memoria de la cola de telemetría (ADR-016).
/// Aplica el patrón "Sequential by Originator" distribuyendo los mensajes en N canales independientes.
/// </summary>
public sealed class InMemoryTelemetryQueueService : ITelemetryQueuePublisher, ITelemetryQueueConsumer
{
    private readonly int _partitionCount;
    private readonly Channel<TelemetryQueueMessage>[] _channels;
    private readonly ILogger<InMemoryTelemetryQueueService> _logger;
    private long _messageIdSequence;

    public int PartitionCount => _partitionCount;

    public InMemoryTelemetryQueueService(
        ILogger<InMemoryTelemetryQueueService> logger,
        int partitionCount = 8)
    {
        _logger = logger;
        _partitionCount = Math.Max(1, partitionCount);
        _channels = new Channel<TelemetryQueueMessage>[_partitionCount];

        for (int i = 0; i < _partitionCount; i++)
        {
            _channels[i] = Channel.CreateBounded<TelemetryQueueMessage>(
                new BoundedChannelOptions(5000)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleWriter = false,
                    SingleReader = true
                });
        }
    }

    public async Task<bool> EnqueueAsync(TelemetryQueueItem item, CancellationToken cancellationToken = default)
    {
        string originatorKey = OriginatorPartitionResolver.ResolveOriginatorKey(
            item.Topic, item.TransportDeviceIdentifier);
        int partition = OriginatorPartitionResolver.GetPartitionIndex(originatorKey, _partitionCount);

        string messageId = $"mem-{partition}-{Interlocked.Increment(ref _messageIdSequence)}";
        var message = new TelemetryQueueMessage(messageId, partition, item);

        await _channels[partition].Writer.WriteAsync(message, cancellationToken);

        IoTMetrics.TelemetryQueueEnqueuedCounter.Add(1,
            new KeyValuePair<string, object?>("partition", partition),
            new KeyValuePair<string, object?>("engine", "inmemory"));

        _logger.LogTrace("Enqueued telemetry item for originator '{Originator}' into partition {Partition}",
            originatorKey, partition);

        return true;
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

        var reader = _channels[partition].Reader;
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            while (reader.TryRead(out var msg))
            {
                yield return msg;
            }
        }
    }

    public Task AcknowledgeAsync(TelemetryQueueMessage message, CancellationToken cancellationToken = default)
    {
        // En memoria, la extracción del canal ya retira el mensaje.
        return Task.CompletedTask;
    }
}
