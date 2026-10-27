using IoT.Application.Common.Interfaces;
using IoT.Application.Common.Observability;
using IoT.Application.Ingestion.Commands;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IoT.Worker;

/// <summary>
/// Hosted service que consume mensajes particionados de telemetría asegurando orden FIFO estricto
/// por dispositivo (ThingsBoard Pattern 2: Sequential by Originator, ADR-016).
/// </summary>
public sealed class TelemetryStreamConsumerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITelemetryQueueConsumer _consumer;
    private readonly ILogger<TelemetryStreamConsumerWorker> _logger;

    public TelemetryStreamConsumerWorker(
        IServiceScopeFactory scopeFactory,
        ITelemetryQueueConsumer consumer,
        ILogger<TelemetryStreamConsumerWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _consumer = consumer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int partitionCount = _consumer.PartitionCount;
        _logger.LogInformation(
            "Starting Telemetry Stream Consumer Worker with {PartitionCount} sequential originator partitions...",
            partitionCount);

        var consumerTasks = new Task[partitionCount];
        for (int p = 0; p < partitionCount; p++)
        {
            int partitionIndex = p;
            consumerTasks[p] = Task.Run(() => RunPartitionConsumerAsync(partitionIndex, stoppingToken), stoppingToken);
        }

        try
        {
            await Task.WhenAll(consumerTasks);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Apagado ordenado
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telemetry Stream Consumer Worker encountered an unexpected error.");
        }

        _logger.LogInformation("Telemetry Stream Consumer Worker stopped.");
    }

    private async Task RunPartitionConsumerAsync(int partition, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Partition {Partition} consumer loop started.", partition);

        try
        {
            await foreach (var message in _consumer.ReadPartitionAsync(partition, stoppingToken))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                    var command = new IngestTelemetryCommand(
                        Topic: message.Item.Topic,
                        Payload: message.Item.Payload,
                        Source: message.Item.Source,
                        TransportProtocol: message.Item.TransportProtocol,
                        TransportDeviceIdentifier: message.Item.TransportDeviceIdentifier,
                        TransportMessageId: message.Item.TransportMessageId,
                        CredentialTenantId: message.Item.CredentialTenantId);

                    var result = await mediator.Send(command, stoppingToken);

                    await _consumer.AcknowledgeAsync(message, stoppingToken);

                    IoTMetrics.TelemetryQueueProcessedCounter.Add(1,
                        new KeyValuePair<string, object?>("partition", partition),
                        new KeyValuePair<string, object?>("status", result.IsSuccess ? "success" : "failed"));

                    if (result.IsFailure)
                    {
                        _logger.LogWarning(
                            "Telemetry ingestion failed on partition {Partition} for topic '{Topic}': {Error}",
                            partition, message.Item.Topic, result.Error.Message);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Unexpected error processing message {MessageId} on partition {Partition}",
                        message.MessageId, partition);

                    // Acknowledge para no entrar en bucle venenoso de procesamiento continuo
                    await _consumer.AcknowledgeAsync(message, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Cierre normal
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error on partition {Partition} consumer loop", partition);
        }

        _logger.LogInformation("Partition {Partition} consumer loop stopped.", partition);
    }
}
