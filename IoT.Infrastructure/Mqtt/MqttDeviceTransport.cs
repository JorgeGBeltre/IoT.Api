using System.Buffers;
using System.Text;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Protocols.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;

namespace IoT.Infrastructure.Mqtt;

public sealed class MqttDeviceTransport : IDeviceTransport, IMqttPublisher, IAsyncDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<MqttDeviceTransport> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private IMqttClient? _mqttClient;

    public string TransportName => "MQTT";
    public bool IsConnected => _mqttClient != null && _mqttClient.IsConnected;
    public Func<TransportMessage, Task<Result>>? OnMessageReceived { get; set; }

    public MqttDeviceTransport(IConfiguration configuration, ILogger<MqttDeviceTransport> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);

        string subscriptionTopic = _configuration["Mqtt:SubscriptionTopic"] ?? "#";
        var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(subscriptionTopic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .Build();

        await _mqttClient!.SubscribeAsync(subscribeOptions, cancellationToken);
        _logger.LogInformation("MqttDeviceTransport subscribed to '{Topic}'", subscriptionTopic);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_mqttClient != null && _mqttClient.IsConnected)
        {
            await _mqttClient.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cancellationToken);
            _logger.LogInformation("MqttDeviceTransport disconnected.");
        }
    }

    public async Task<Result> PublishCommandAsync(
        string targetTopicOrRoute,
        string payload,
        DeviceTransportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            var qosLevel = (options?.Qos ?? 1) switch
            {
                0 => MqttQualityOfServiceLevel.AtMostOnce,
                2 => MqttQualityOfServiceLevel.ExactlyOnce,
                _ => MqttQualityOfServiceLevel.AtLeastOnce
            };

            var msgBuilder = new MqttApplicationMessageBuilder()
                .WithTopic(targetTopicOrRoute)
                .WithPayload(Encoding.UTF8.GetBytes(payload))
                .WithQualityOfServiceLevel(qosLevel)
                .WithRetainFlag(options?.Retain ?? false);

            if (options?.MessageExpiry.HasValue == true)
            {
                msgBuilder.WithMessageExpiryInterval((uint)options.MessageExpiry.Value.TotalSeconds);
            }

            if (options?.CorrelationId != null)
            {
                msgBuilder.WithCorrelationData(Encoding.UTF8.GetBytes(options.CorrelationId));
            }

            if (options?.UserProperties != null)
            {
                foreach (var (k, v) in options.UserProperties)
                {
                    msgBuilder.WithUserProperty(k, v);
                }
            }

            var result = await _mqttClient!.PublishAsync(msgBuilder.Build(), cancellationToken);

            if (result.ReasonCode == MqttClientPublishReasonCode.Success)
            {
                return Result.Success();
            }

            return Result.Failure(new Error("MqttTransport.PublishRejected", $"MQTT broker rejected publish: {result.ReasonCode}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish command via MqttDeviceTransport to {Topic}", targetTopicOrRoute);
            return Result.Failure(new Error("MqttTransport.PublishFailed", ex.Message));
        }
    }

    public async Task PublishAsync(string topic, string payloadJson, int qos = 1, bool retain = false, CancellationToken cancellationToken = default)
    {
        var options = new DeviceTransportOptions(Qos: qos, Retain: retain);
        await PublishCommandAsync(topic, payloadJson, options, cancellationToken);
    }

    public async Task PublishAsync(string topic, string payloadJson, MqttPublishOptions options, CancellationToken cancellationToken = default)
    {
        var transportOptions = new DeviceTransportOptions(
            Qos: options.Qos,
            Retain: options.Retain,
            MessageExpiry: options.MessageExpiryInterval.HasValue ? TimeSpan.FromSeconds(options.MessageExpiryInterval.Value) : null,
            CorrelationId: options.CorrelationData != null ? Encoding.UTF8.GetString(options.CorrelationData) : null,
            UserProperties: options.UserProperties);

        await PublishCommandAsync(topic, payloadJson, transportOptions, cancellationToken);
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_mqttClient != null && _mqttClient.IsConnected)
            return;

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_mqttClient != null && _mqttClient.IsConnected)
                return;

            var factory = new MqttClientFactory();
            _mqttClient ??= factory.CreateMqttClient();

            // Wire message reception
            _mqttClient.ApplicationMessageReceivedAsync += HandleIncomingApplicationMessageAsync;

            string host = _configuration["Mqtt:Host"] ?? "localhost";
            int port = int.TryParse(_configuration["Mqtt:Port"], out var p) ? p : 1883;
            string? username = _configuration["Mqtt:Username"] ?? _configuration["MQTT_USER"];
            string? password = _configuration["Mqtt:Password"] ?? _configuration["MQTT_PASSWORD"];
            string? apiKey = _configuration["Mqtt:ApiKey"] ?? _configuration["MQTT_API_KEY"];
            string? chipId = _configuration["Mqtt:ChipId"];
            bool useTls = bool.TryParse(_configuration["Mqtt:UseTls"], out var tls) && tls;
            string clientId = !string.IsNullOrWhiteSpace(chipId)
                ? chipId
                : $"IoT-DeviceTransport-{Guid.NewGuid():N}";

            var optionsBuilder = new MqttClientOptionsBuilder()
                .WithTcpServer(host, port)
                .WithClientId(clientId)
                .WithCleanSession(true)
                .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500);

            // El broker embebido autentica por ApiKey. En MQTT 5 va como UserProperty;
            // en 3.1.1 (fallback) el Username = ApiKey. Enviamos ambos para compatibilidad.
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                optionsBuilder.WithUserProperty("ApiKey", apiKey);
                if (!string.IsNullOrWhiteSpace(chipId))
                    optionsBuilder.WithUserProperty("ChipId", chipId);
                username ??= apiKey;
            }

            if (!string.IsNullOrWhiteSpace(username))
            {
                optionsBuilder.WithCredentials(username, password);
            }

            if (useTls)
            {
                optionsBuilder.WithTlsOptions(o => o.UseTls());
            }

            var options = optionsBuilder.Build();
            _logger.LogInformation("Connecting MqttDeviceTransport to {Host}:{Port} (TLS: {UseTls})", host, port, useTls);
            await _mqttClient.ConnectAsync(options, cancellationToken);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task HandleIncomingApplicationMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        if (OnMessageReceived == null)
            return;

        string topic = args.ApplicationMessage.Topic;
        string payload = args.ApplicationMessage.Payload.Length > 0
            ? Encoding.UTF8.GetString(args.ApplicationMessage.Payload)
            : string.Empty;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (args.ApplicationMessage.UserProperties != null)
        {
            foreach (var prop in args.ApplicationMessage.UserProperties)
            {
                headers[prop.Name] = prop.Value;
            }
        }

        string? messageId = null;
        if (headers.TryGetValue("message-id", out var mid))
        {
            messageId = mid;
        }

        var transportMessage = new TransportMessage
        {
            TopicOrRoute = topic,
            Payload = payload,
            MessageId = messageId,
            ReceivedAt = DateTime.UtcNow,
            Headers = headers,
            TransportProtocol = "MQTT",
            Source = "real"
        };

        try
        {
            await OnMessageReceived(transportMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing OnMessageReceived in MqttDeviceTransport for topic '{Topic}'", topic);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_mqttClient != null)
        {
            if (_mqttClient.IsConnected)
            {
                await _mqttClient.DisconnectAsync();
            }
            _mqttClient.Dispose();
            _mqttClient = null;
        }
        _connectionLock.Dispose();
    }
}
