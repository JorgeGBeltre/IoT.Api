using System.Text;
using IoT.Application.Commands.Commands;
using IoT.Application.Ingestion.Commands;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;

namespace IoT.Worker;

public sealed class TelemetryIngestionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TelemetryIngestionWorker> _logger;
    private IMqttClient? _mqttClient;

    public TelemetryIngestionWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<TelemetryIngestionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting IoT Telemetry Ingestion Worker...");

        string host = _configuration["Mqtt:Host"] ?? "localhost";
        int port = int.TryParse(_configuration["Mqtt:Port"], out var p) ? p : 1883;
        string? username = _configuration["Mqtt:Username"] ?? _configuration["MQTT_USER"];
        string? password = _configuration["Mqtt:Password"] ?? _configuration["MQTT_PASSWORD"];
        bool useTls = bool.TryParse(_configuration["Mqtt:UseTls"], out var tls) && tls;
        string clientId = $"IoT-Ingestion-Worker-{Guid.NewGuid():N}";

        var factory = new MqttClientFactory();
        _mqttClient = factory.CreateMqttClient();

        _mqttClient.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                string topic = e.ApplicationMessage.Topic;
                string payload = e.ApplicationMessage.Payload.Length > 0
                    ? Encoding.UTF8.GetString(e.ApplicationMessage.Payload)
                    : string.Empty;

                _logger.LogDebug("Received MQTT message on topic: {Topic}", topic);

                using var scope = _scopeFactory.CreateScope();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                // E5-01: Check if message is a command response/ACK
                if (IsCommandAckTopic(topic))
                {
                    var ackCommand = ParseCommandAck(topic, payload, e.ApplicationMessage);
                    if (ackCommand is not null)
                    {
                        var ackResult = await mediator.Send(ackCommand, stoppingToken);
                        if (ackResult.IsFailure)
                        {
                            _logger.LogWarning("Command ACK processing failed for topic {Topic}: {Error}", topic, ackResult.Error.Message);
                        }
                        return;
                    }
                }

                var result = await mediator.Send(new IngestTelemetryCommand(topic, payload), stoppingToken);
                if (result.IsFailure)
                {
                    _logger.LogWarning("Ingestion failed for topic {Topic}: {Error}", topic, result.Error.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error processing incoming MQTT message on topic {Topic}", e.ApplicationMessage?.Topic);
            }
        };

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

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_mqttClient.IsConnected)
                {
                    _logger.LogInformation("Connecting to MQTT Broker (v5.0) at {Host}:{Port}...", host, port);
                    await _mqttClient.ConnectAsync(options, stoppingToken);

                    // E3-03: Subscribe to declarative profile topic patterns instead of root wildcard '#'
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var routingService = scope.ServiceProvider.GetService<IoT.Protocols.Routing.ITopicRoutingService>();
                        var patterns = routingService?.GetAllTelemetrySubscriptionPatterns()
                            ?? new[] { "devices/+/telemetry", "devices/+/gps", "telemetry/#", "glp/telemetria/#", "relays/devices/+/action" };

                        foreach (var pattern in patterns)
                        {
                            _logger.LogInformation("Subscribing to telemetry topic pattern: {Pattern}", pattern);
                            await _mqttClient.SubscribeAsync(pattern, cancellationToken: stoppingToken);
                        }

                        // E5-01: Subscribe to command ACK and response topics
                        var commandAckPatterns = new[] { "devices/+/commands/ack", "devices/+/commands/response", "+/cmd/+/ack" };
                        foreach (var ackPattern in commandAckPatterns)
                        {
                            _logger.LogInformation("Subscribing to command ACK pattern: {Pattern}", ackPattern);
                            await _mqttClient.SubscribeAsync(ackPattern, cancellationToken: stoppingToken);
                        }
                    }
                    _logger.LogInformation("Successfully subscribed to telemetry and command response topic patterns.");
                }

                await Task.Delay(5000, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("MQTT connection lost or broker unreachable ({Host}:{Port}): {Message}. Retrying in 5s...", host, port, ex.Message);
                await Task.Delay(5000, stoppingToken);
            }
        }

        if (_mqttClient != null && _mqttClient.IsConnected)
        {
            await _mqttClient.DisconnectAsync(cancellationToken: CancellationToken.None);
        }
    }

    private static bool IsCommandAckTopic(string topic)
    {
        return topic.EndsWith("/commands/ack", StringComparison.OrdinalIgnoreCase) ||
               topic.EndsWith("/commands/response", StringComparison.OrdinalIgnoreCase) ||
               (topic.Contains("/cmd/") && topic.EndsWith("/ack", StringComparison.OrdinalIgnoreCase));
    }

    private static AcknowledgeDeviceCommand? ParseCommandAck(string topic, string payload, MqttApplicationMessage msg)
    {
        Guid? commandId = null;
        string? clientRequestId = null;
        string status = "confirmed";
        string? result = null;
        string? error = null;

        // Try extract from MQTT 5 User Properties
        if (msg.UserProperties != null)
        {
            foreach (var prop in msg.UserProperties)
            {
                if (prop.Name.Equals("command_id", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(prop.Value, out var cid))
                    commandId = cid;
                if (prop.Name.Equals("client_request_id", StringComparison.OrdinalIgnoreCase))
                    clientRequestId = prop.Value;
            }
        }

        // Try extract from CorrelationData
        if (!commandId.HasValue && msg.CorrelationData != null && msg.CorrelationData.Length > 0)
        {
            string corr = Encoding.UTF8.GetString(msg.CorrelationData);
            if (Guid.TryParse(corr, out var cid)) commandId = cid;
        }

        // Try extract from JSON payload
        if (payload.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (!commandId.HasValue)
                {
                    if (root.TryGetProperty("command_id", out var cElem) && Guid.TryParse(cElem.GetString(), out var cid))
                        commandId = cid;
                    else if (root.TryGetProperty("commandId", out var cElem2) && Guid.TryParse(cElem2.GetString(), out var cid2))
                        commandId = cid2;
                }

                if (string.IsNullOrEmpty(clientRequestId))
                {
                    if (root.TryGetProperty("client_request_id", out var crElem))
                        clientRequestId = crElem.GetString();
                    else if (root.TryGetProperty("clientRequestId", out var crElem2))
                        clientRequestId = crElem2.GetString();
                }

                if (root.TryGetProperty("status", out var stElem))
                    status = stElem.GetString() ?? "confirmed";

                if (root.TryGetProperty("result", out var rElem))
                    result = rElem.ToString();
                else if (root.TryGetProperty("payload", out var pElem))
                    result = pElem.ToString();

                if (root.TryGetProperty("error", out var errElem))
                    error = errElem.GetString();
            }
            catch
            {
                result = payload;
            }
        }
        else if (!string.IsNullOrWhiteSpace(payload))
        {
            result = payload;
        }

        if (!commandId.HasValue && string.IsNullOrEmpty(clientRequestId))
        {
            return null;
        }

        return new AcknowledgeDeviceCommand(
            CommandId: commandId,
            ClientRequestId: clientRequestId,
            Status: status,
            ResultPayload: result,
            ErrorMessage: error);
    }
}
