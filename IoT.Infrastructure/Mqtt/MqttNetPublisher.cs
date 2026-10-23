using System.Text;
using IoT.Application.Common.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;

namespace IoT.Infrastructure.Mqtt;

public sealed class MqttNetPublisher : IMqttPublisher, IAsyncDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<MqttNetPublisher> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private IMqttClient? _mqttClient;

    public MqttNetPublisher(IConfiguration configuration, ILogger<MqttNetPublisher> logger)
    {
        _configuration = configuration;
        _logger = logger;
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

            string host = _configuration["Mqtt:Host"] ?? "localhost";
            int port = int.TryParse(_configuration["Mqtt:Port"], out var p) ? p : 1883;
            string? username = _configuration["Mqtt:Username"] ?? _configuration["MQTT_USER"];
            string? password = _configuration["Mqtt:Password"] ?? _configuration["MQTT_PASSWORD"];
            bool useTls = bool.TryParse(_configuration["Mqtt:UseTls"], out var tls) && tls;
            string clientId = $"IoT-Api-Publisher-{Guid.NewGuid():N}";

            var optionsBuilder = new MqttClientOptionsBuilder()
                .WithTcpServer(host, port)
                .WithClientId(clientId)
                .WithCleanSession(true)
                .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500);

            if (!string.IsNullOrWhiteSpace(username))
            {
                optionsBuilder.WithCredentials(username, password);
            }

            if (useTls)
            {
                optionsBuilder.WithTlsOptions(o => o.UseTls());
            }

            var options = optionsBuilder.Build();

            _logger.LogInformation("Connecting MQTT Publisher (v5.0) to {Host}:{Port} with ClientId {ClientId}...", host, port, clientId);
            await _mqttClient.ConnectAsync(options, cancellationToken);
            _logger.LogInformation("MQTT Publisher connected successfully.");
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public Task PublishAsync(string topic, string payload, int qos = 1, bool retain = false, CancellationToken cancellationToken = default)
    {
        return PublishAsync(topic, payload, new MqttPublishOptions { Qos = qos, Retain = retain }, cancellationToken);
    }

    public async Task PublishAsync(string topic, string payload, MqttPublishOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            var mqttQualityOfService = options.Qos switch
            {
                0 => MqttQualityOfServiceLevel.AtMostOnce,
                2 => MqttQualityOfServiceLevel.ExactlyOnce,
                _ => MqttQualityOfServiceLevel.AtLeastOnce
            };

            var msgBuilder = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes(payload))
                .WithQualityOfServiceLevel(mqttQualityOfService)
                .WithRetainFlag(options.Retain);

            if (options.MessageExpiryInterval.HasValue)
            {
                msgBuilder.WithMessageExpiryInterval(options.MessageExpiryInterval.Value);
            }

            if (!string.IsNullOrWhiteSpace(options.ContentType))
            {
                msgBuilder.WithContentType(options.ContentType);
            }

            if (!string.IsNullOrWhiteSpace(options.ResponseTopic))
            {
                msgBuilder.WithResponseTopic(options.ResponseTopic);
            }

            if (options.CorrelationData != null && options.CorrelationData.Length > 0)
            {
                msgBuilder.WithCorrelationData(options.CorrelationData);
            }

            if (options.UserProperties != null)
            {
                foreach (var (k, v) in options.UserProperties)
                {
                    msgBuilder.WithUserProperty(k, v);
                }
            }

            if (_mqttClient != null)
            {
                await _mqttClient.PublishAsync(msgBuilder.Build(), cancellationToken);
                _logger.LogDebug("Published MQTTv5 message to {Topic} (QoS: {Qos}, Retain: {Retain}, Expiry: {Expiry})",
                    topic, options.Qos, options.Retain, options.MessageExpiryInterval);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish MQTT message to topic {Topic}", topic);
            throw;
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
        }
        _connectionLock.Dispose();
    }
}
