using System.Text;
using System.Text.Json;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Repositories;
using Microsoft.Extensions.Logging;

namespace IoT.Infrastructure.Services;

public sealed class OutboundWebhookDispatcher : IOutboundWebhookDispatcher
{
    private readonly ITenantSettingsRepository _tenantSettingsRepository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OutboundWebhookDispatcher> _logger;

    public OutboundWebhookDispatcher(
        ITenantSettingsRepository tenantSettingsRepository,
        IHttpClientFactory httpClientFactory,
        ILogger<OutboundWebhookDispatcher> logger)
    {
        _tenantSettingsRepository = tenantSettingsRepository;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task DispatchAsync(Guid tenantId, string eventType, object payload, CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await _tenantSettingsRepository.GetByTenantAsync(tenantId, cancellationToken);
            if (settings == null || string.IsNullOrWhiteSpace(settings.WebhookUrl))
            {
                return;
            }

            if (!Uri.TryCreate(settings.WebhookUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                _logger.LogWarning("Tenant {TenantId} has an invalid webhook URL configured: '{WebhookUrl}'. Skipping dispatch.",
                    tenantId, settings.WebhookUrl);
                return;
            }

            var client = _httpClientFactory.CreateClient("OutboundWebhookClient");
            client.Timeout = TimeSpan.FromSeconds(5);

            string json = JsonSerializer.Serialize(new
            {
                tenantId,
                eventType,
                timestamp = DateTime.UtcNow,
                data = payload
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            request.Headers.Add("X-IoT-Event", eventType);
            request.Headers.Add("X-IoT-TenantId", tenantId.ToString());
            request.Headers.Add("X-IoT-Timestamp", DateTime.UtcNow.ToString("O"));

            var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Webhook dispatch to {Url} failed with status {StatusCode} for tenant {TenantId} event {EventType}",
                    settings.WebhookUrl, response.StatusCode, tenantId, eventType);
            }
            else
            {
                _logger.LogInformation("Webhook dispatched successfully to {Url} for tenant {TenantId} event {EventType}",
                    settings.WebhookUrl, tenantId, eventType);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to dispatch outbound webhook for tenant {TenantId} event {EventType}",
                tenantId, eventType);
        }
    }
}
