namespace IoT.Application.Common.Interfaces;

/// <summary>
/// Mensaje recibido desde una partición de la cola o stream de telemetría (ADR-016).
/// </summary>
public sealed record TelemetryQueueMessage(
    string MessageId,
    int Partition,
    TelemetryQueueItem Item);

/// <summary>
/// Contrato para el consumo ordenado de telemetría particionada por origen (ThingsBoard Pattern 2).
/// Permite al Worker suscribir tareas independientes a cada partición asegurando orden FIFO estricto.
/// </summary>
public interface ITelemetryQueueConsumer
{
    /// <summary>
    /// Número de particiones configuradas.
    /// </summary>
    int PartitionCount { get; }

    /// <summary>
    /// Lee secuencialmente los mensajes de la partición indicada.
    /// </summary>
    IAsyncEnumerable<TelemetryQueueMessage> ReadPartitionAsync(
        int partition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirma (ACK) el procesamiento exitoso del mensaje en la cola/stream subyacente.
    /// </summary>
    Task AcknowledgeAsync(
        TelemetryQueueMessage message,
        CancellationToken cancellationToken = default);
}
