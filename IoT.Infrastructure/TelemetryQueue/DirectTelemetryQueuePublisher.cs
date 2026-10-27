using IoT.Application.Common.Interfaces;
using IoT.Application.Ingestion.Commands;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IoT.Infrastructure.TelemetryQueue;

/// <summary>
/// Publicador directo que envía inmediatamente al pipeline de Mediator sin encolar (modo Direct / diagnóstico).
/// </summary>
public sealed class DirectTelemetryQueuePublisher : ITelemetryQueuePublisher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DirectTelemetryQueuePublisher> _logger;

    public DirectTelemetryQueuePublisher(
        IServiceScopeFactory scopeFactory,
        ILogger<DirectTelemetryQueuePublisher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<bool> EnqueueAsync(TelemetryQueueItem item, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            var command = new IngestTelemetryCommand(
                Topic: item.Topic,
                Payload: item.Payload,
                Source: item.Source,
                TransportProtocol: item.TransportProtocol,
                TransportDeviceIdentifier: item.TransportDeviceIdentifier,
                TransportMessageId: item.TransportMessageId,
                CredentialTenantId: item.CredentialTenantId);

            var result = await mediator.Send(command, cancellationToken);
            if (result.IsFailure)
            {
                _logger.LogWarning("Direct telemetry ingestion failed for topic '{Topic}': {Error}",
                    item.Topic, result.Error.Message);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in direct telemetry ingestion for topic '{Topic}'", item.Topic);
            return false;
        }
    }
}
