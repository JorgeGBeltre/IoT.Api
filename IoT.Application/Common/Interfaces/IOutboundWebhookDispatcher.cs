namespace IoT.Application.Common.Interfaces;

/// <summary>
/// Despachador de webhooks salientes hacia endpoints externos configurados por el tenant (TenantSettings.WebhookUrl).
/// </summary>
public interface IOutboundWebhookDispatcher
{
    Task DispatchAsync(Guid tenantId, string eventType, object payload, CancellationToken cancellationToken = default);
}
