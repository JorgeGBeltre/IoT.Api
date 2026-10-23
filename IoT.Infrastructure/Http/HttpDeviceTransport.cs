using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IoT.Domain.Common;
using IoT.Protocols.Contracts;
using Microsoft.Extensions.Logging;

namespace IoT.Infrastructure.Http;

public sealed class HttpDeviceTransport : IDeviceTransport
{
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger<HttpDeviceTransport> _logger;

    // Command queue for polling HTTP devices: ChipId -> ConcurrentQueue<BufferedHttpCommand>
    private readonly ConcurrentDictionary<string, ConcurrentQueue<BufferedHttpCommand>> _pollingBuffer = new(StringComparer.OrdinalIgnoreCase);

    public string TransportName => "HTTP";
    public bool IsConnected => true; // HTTP is stateless; endpoint availability defines connection
    public Func<TransportMessage, Task<Result>>? OnMessageReceived { get; set; }

    public HttpDeviceTransport(ILogger<HttpDeviceTransport> logger, IHttpClientFactory? httpClientFactory = null)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("HttpDeviceTransport initialized and ready to receive push/webhook traffic.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _pollingBuffer.Clear();
        _logger.LogInformation("HttpDeviceTransport stopped.");
        return Task.CompletedTask;
    }

    public async Task<Result> IngestAsync(
        string route,
        string payload,
        Dictionary<string, string>? headers = null,
        string? messageId = null,
        CancellationToken cancellationToken = default)
    {
        if (OnMessageReceived == null)
        {
            return Result.Failure(new Error("HttpTransport.NoHandler", "No ingestion handler attached to HttpDeviceTransport."));
        }

        var transportMessage = TransportMessage.Create(
            topicOrRoute: route,
            payload: payload,
            messageId: messageId,
            headers: headers,
            transportProtocol: "HTTP",
            source: "http");

        return await OnMessageReceived.Invoke(transportMessage);
    }

    public async Task<Result> PublishCommandAsync(
        string targetTopicOrRoute,
        string payload,
        DeviceTransportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // 1. If target is a valid HTTP URL, push directly via HttpClient
        if (Uri.TryCreate(targetTopicOrRoute, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return await PushCommandViaHttpAsync(uri, payload, options, cancellationToken);
        }

        // 2. Otherwise buffer command for polling device (route is chipId or devices/{chipId}/commands)
        string deviceKey = ExtractDeviceIdentifier(targetTopicOrRoute);
        var queue = _pollingBuffer.GetOrAdd(deviceKey, _ => new ConcurrentQueue<BufferedHttpCommand>());

        var buffered = new BufferedHttpCommand(
            CommandId: options?.CorrelationId ?? Guid.NewGuid().ToString("N"),
            Payload: payload,
            CreatedAt: DateTime.UtcNow,
            ExpiresAt: options?.MessageExpiry.HasValue == true
                ? DateTime.UtcNow.Add(options.MessageExpiry.Value)
                : DateTime.UtcNow.AddMinutes(10));

        queue.Enqueue(buffered);
        _logger.LogInformation("Buffered HTTP command for polling device '{DeviceKey}' (Expires: {Expiry})",
            deviceKey, buffered.ExpiresAt);

        return Result.Success();
    }

    public BufferedHttpCommand? PollNextCommand(string deviceIdentifier)
    {
        if (_pollingBuffer.TryGetValue(deviceIdentifier, out var queue))
        {
            while (queue.TryDequeue(out var cmd))
            {
                if (cmd.ExpiresAt > DateTime.UtcNow)
                {
                    return cmd;
                }
                _logger.LogDebug("Expired buffered HTTP command {CommandId} discarded for device {Device}",
                    cmd.CommandId, deviceIdentifier);
            }
        }
        return null;
    }

    private async Task<Result> PushCommandViaHttpAsync(
        Uri targetUri,
        string payload,
        DeviceTransportOptions? options,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory?.CreateClient("IoT.HttpDeviceTransport") ?? new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, targetUri)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            if (options?.CorrelationId != null)
            {
                request.Headers.Add("X-Correlation-ID", options.CorrelationId);
            }

            if (options?.UserProperties != null)
            {
                foreach (var (k, v) in options.UserProperties)
                {
                    request.Headers.TryAddWithoutValidation($"X-IoT-{k}", v);
                }
            }

            var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("HTTP command dispatched successfully to {Uri} (Status: {StatusCode})",
                    targetUri, response.StatusCode);
                return Result.Success();
            }

            _logger.LogWarning("HTTP command push rejected by {Uri} (Status: {StatusCode})", targetUri, response.StatusCode);
            return Result.Failure(new Error("HttpTransport.Rejected", $"HTTP endpoint returned {response.StatusCode}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTTP command push failed to {Uri}", targetUri);
            return Result.Failure(new Error("HttpTransport.DispatchFailed", ex.Message));
        }
    }

    private static string ExtractDeviceIdentifier(string route)
    {
        if (route.Contains('/'))
        {
            var parts = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Equals("devices", StringComparison.OrdinalIgnoreCase))
            {
                return parts[1];
            }
            return parts[^1];
        }
        return route;
    }
}

public sealed record BufferedHttpCommand(
    string CommandId,
    string Payload,
    DateTime CreatedAt,
    DateTime ExpiresAt);
