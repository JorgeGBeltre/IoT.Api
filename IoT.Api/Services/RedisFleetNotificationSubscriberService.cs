using IoT.Infrastructure.Redis;
using StackExchange.Redis;

namespace IoT.Api.Services;

/// <summary>
/// Suscribe al canal Redis Pub/Sub <see cref="RedisFleetNotificationPublisher.ChannelName"/>
/// para retransmitir inmediatamente por SignalR (FleetHub) los eventos en tiempo real
/// procesados fuera de proceso por IoT.Worker (telemetría, estado físico de relés, botones,
/// confirmaciones de comandos y alertas).
/// </summary>
public sealed class RedisFleetNotificationSubscriberService : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly SignalRFleetNotificationService _signalRNotifications;
    private readonly ILogger<RedisFleetNotificationSubscriberService> _logger;

    public RedisFleetNotificationSubscriberService(
        IConnectionMultiplexer redis,
        SignalRFleetNotificationService signalRNotifications,
        ILogger<RedisFleetNotificationSubscriberService> logger)
    {
        _redis = redis;
        _signalRNotifications = signalRNotifications;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ChannelMessageQueue? queue = null;
        try
        {
            var subscriber = _redis.GetSubscriber();
            queue = await subscriber.SubscribeAsync(RedisChannel.Literal(RedisFleetNotificationPublisher.ChannelName));
            _logger.LogInformation("Subscribed to cross-process realtime SignalR bridge channel '{Channel}'.",
                RedisFleetNotificationPublisher.ChannelName);

            await foreach (var msg in queue.WithCancellation(stoppingToken))
            {
                if (!msg.Message.HasValue)
                {
                    continue;
                }

                try
                {
                    var env = RedisFleetNotificationPublisher.DeserializeEnvelope(msg.Message.ToString());
                    if (env is null)
                    {
                        continue;
                    }

                    await DispatchEnvelopeAsync(env, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error dispatching cross-process fleet notification to SignalR.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cross-process Redis SignalR bridge stopped unexpectedly.");
        }
        finally
        {
            if (queue is not null)
            {
                try { await queue.UnsubscribeAsync(); } catch { /* ignore on shutdown */ }
            }
        }
    }

    internal Task DispatchEnvelopeAsync(RedisFleetNotificationEnvelope env, CancellationToken ct) =>
        env.Kind switch
        {
            "PositionUpdated" => _signalRNotifications.NotifyPositionUpdatedAsync(
                env.TenantId,
                env.DeviceId,
                env.ChipId ?? string.Empty,
                env.DeviceName ?? string.Empty,
                env.Latitude ?? 0,
                env.Longitude ?? 0,
                env.SpeedKph,
                env.Heading,
                env.TimestampUtc,
                ct),

            "TelemetryReceived" => _signalRNotifications.NotifyTelemetryReceivedAsync(
                env.TenantId,
                env.DeviceId,
                env.ChipId ?? string.Empty,
                env.Metrics ?? new Dictionary<string, double>(),
                env.TimestampUtc,
                ct),

            "DeviceAlert" => _signalRNotifications.NotifyDeviceAlertAsync(
                env.TenantId,
                env.DeviceId,
                env.ChipId ?? string.Empty,
                env.AlertType ?? "Event",
                env.Message ?? string.Empty,
                env.TimestampUtc,
                ct),

            "CommandStatusUpdated" => _signalRNotifications.NotifyCommandStatusUpdatedAsync(
                env.TenantId,
                env.DeviceId,
                env.CommandId ?? Guid.Empty,
                env.Status ?? "Confirmed",
                env.ResultJson,
                env.TimestampUtc,
                ct),

            "OtaProgress" => _signalRNotifications.NotifyOtaProgressAsync(
                env.TenantId,
                env.DeviceId,
                env.ChipId ?? string.Empty,
                env.Version ?? string.Empty,
                env.CurrentChunk ?? 0,
                env.TotalChunks ?? 0,
                env.Percentage ?? 0,
                env.Status ?? string.Empty,
                env.ErrorMessage,
                ct),

            "DeviceStatusChanged" => _signalRNotifications.NotifyDeviceStatusChangedAsync(
                env.TenantId,
                env.DeviceId,
                env.ChipId ?? string.Empty,
                env.Status ?? "Online",
                env.TimestampUtc,
                ct),

            _ => Task.CompletedTask
        };
}

