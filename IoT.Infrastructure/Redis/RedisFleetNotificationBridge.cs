using System.Text.Json;
using IoT.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace IoT.Infrastructure.Redis;

public sealed record RedisFleetNotificationEnvelope(
    string Kind,
    Guid TenantId,
    Guid DeviceId,
    string? ChipId = null,
    string? DeviceName = null,
    double? Latitude = null,
    double? Longitude = null,
    float? SpeedKph = null,
    float? Heading = null,
    Dictionary<string, double>? Metrics = null,
    string? AlertType = null,
    string? Message = null,
    Guid? CommandId = null,
    string? Status = null,
    string? ResultJson = null,
    string? Version = null,
    int? CurrentChunk = null,
    int? TotalChunks = null,
    int? Percentage = null,
    string? ErrorMessage = null,
    DateTime TimestampUtc = default);

public sealed class RedisFleetNotificationPublisher : IFleetNotificationService
{
    public const string ChannelName = "iot:fleet:realtime";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisFleetNotificationPublisher> _logger;

    public RedisFleetNotificationPublisher(
        IConnectionMultiplexer redis,
        ILogger<RedisFleetNotificationPublisher> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public Task NotifyPositionUpdatedAsync(
        Guid tenantId,
        Guid deviceId,
        string chipId,
        string deviceName,
        double latitude,
        double longitude,
        float? speedKph,
        float? heading,
        DateTime recordedAt,
        CancellationToken cancellationToken = default) =>
        PublishEnvelopeAsync(new RedisFleetNotificationEnvelope(
            Kind: "PositionUpdated",
            TenantId: tenantId,
            DeviceId: deviceId,
            ChipId: chipId,
            DeviceName: deviceName,
            Latitude: latitude,
            Longitude: longitude,
            SpeedKph: speedKph,
            Heading: heading,
            TimestampUtc: recordedAt));

    public Task NotifyTelemetryReceivedAsync(
        Guid tenantId,
        Guid deviceId,
        string chipId,
        IReadOnlyDictionary<string, double> metrics,
        DateTime recordedAt,
        CancellationToken cancellationToken = default) =>
        PublishEnvelopeAsync(new RedisFleetNotificationEnvelope(
            Kind: "TelemetryReceived",
            TenantId: tenantId,
            DeviceId: deviceId,
            ChipId: chipId,
            Metrics: metrics is Dictionary<string, double> dict ? dict : new Dictionary<string, double>(metrics),
            TimestampUtc: recordedAt));

    public Task NotifyDeviceAlertAsync(
        Guid tenantId,
        Guid deviceId,
        string chipId,
        string alertType,
        string message,
        DateTime timestamp,
        CancellationToken cancellationToken = default) =>
        PublishEnvelopeAsync(new RedisFleetNotificationEnvelope(
            Kind: "DeviceAlert",
            TenantId: tenantId,
            DeviceId: deviceId,
            ChipId: chipId,
            AlertType: alertType,
            Message: message,
            TimestampUtc: timestamp));

    public Task NotifyCommandStatusUpdatedAsync(
        Guid tenantId,
        Guid deviceId,
        Guid commandId,
        string status,
        string? resultJson,
        DateTime updatedAt,
        CancellationToken cancellationToken = default) =>
        PublishEnvelopeAsync(new RedisFleetNotificationEnvelope(
            Kind: "CommandStatusUpdated",
            TenantId: tenantId,
            DeviceId: deviceId,
            CommandId: commandId,
            Status: status,
            ResultJson: resultJson,
            TimestampUtc: updatedAt));

    public Task NotifyOtaProgressAsync(
        Guid tenantId,
        Guid deviceId,
        string chipId,
        string version,
        int currentChunk,
        int totalChunks,
        int percentage,
        string status,
        string? errorMessage = null,
        CancellationToken cancellationToken = default) =>
        PublishEnvelopeAsync(new RedisFleetNotificationEnvelope(
            Kind: "OtaProgress",
            TenantId: tenantId,
            DeviceId: deviceId,
            ChipId: chipId,
            Version: version,
            CurrentChunk: currentChunk,
            TotalChunks: totalChunks,
            Percentage: percentage,
            Status: status,
            ErrorMessage: errorMessage,
            TimestampUtc: DateTime.UtcNow));

    public Task NotifyDeviceStatusChangedAsync(
        Guid tenantId,
        Guid deviceId,
        string chipId,
        string status,
        DateTime timestamp,
        CancellationToken cancellationToken = default) =>
        PublishEnvelopeAsync(new RedisFleetNotificationEnvelope(
            Kind: "DeviceStatusChanged",
            TenantId: tenantId,
            DeviceId: deviceId,
            ChipId: chipId,
            Status: status,
            TimestampUtc: timestamp));

    public static string SerializeEnvelope(RedisFleetNotificationEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, JsonOptions);

    public static RedisFleetNotificationEnvelope? DeserializeEnvelope(string json) =>
        JsonSerializer.Deserialize<RedisFleetNotificationEnvelope>(json, JsonOptions);

    private async Task PublishEnvelopeAsync(RedisFleetNotificationEnvelope envelope)
    {
        try
        {
            var subscriber = _redis.GetSubscriber();
            if (subscriber is null)
            {
                return;
            }

            string payload = SerializeEnvelope(envelope);
            await subscriber.PublishAsync(RedisChannel.Literal(ChannelName), payload, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish cross-process fleet notification ({Kind}) for device {DeviceId}.",
                envelope.Kind, envelope.DeviceId);
        }
    }
}

