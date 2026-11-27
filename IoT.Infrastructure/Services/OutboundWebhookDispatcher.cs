using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IoT.Infrastructure.Services;

public sealed class OutboundWebhookDispatcher : IOutboundWebhookDispatcher
{
    private readonly ITenantSettingsRepository _tenantSettingsRepository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OutboundWebhookDispatcher> _logger;
    private readonly bool _allowPrivateNetworks;

    public OutboundWebhookDispatcher(
        ITenantSettingsRepository tenantSettingsRepository,
        IHttpClientFactory httpClientFactory,
        ILogger<OutboundWebhookDispatcher> logger,
        IConfiguration? configuration = null)
    {
        _tenantSettingsRepository = tenantSettingsRepository;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _allowPrivateNetworks = configuration?.GetValue<bool>("Webhook:AllowPrivateNetworks", false) ?? false;
    }

    public static bool IsDisallowedTargetAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            return b[0] == 0
                || b[0] == 10
                || b[0] == 127
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2))
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19 || (b[1] == 51 && b[2] == 100)))
                || (b[0] == 203 && b[1] == 0 && b[2] == 113)
                || b[0] >= 224;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
            {
                return true;
            }

            byte[] b = ip.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC; // fc00::/7 Unique Local Address
        }

        return true;
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

            if (!_allowPrivateNetworks)
            {
                if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
                    (IPAddress.TryParse(uri.Host, out var literalIp) && IsDisallowedTargetAddress(literalIp)))
                {
                    _logger.LogWarning("Blocked SSRF webhook target '{WebhookUrl}' for tenant {TenantId}.", settings.WebhookUrl, tenantId);
                    return;
                }
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
