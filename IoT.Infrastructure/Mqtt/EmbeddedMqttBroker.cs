using System.Text;
using System.Threading.Channels;
using IoT.Application.Common.Interfaces;
using IoT.Application.Ingestion.Commands;
using IoT.Domain.Repositories;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace IoT.Infrastructure.Mqtt;

/// <summary>
/// Broker MQTT EMBEBIDO (portado de References/Mqtt-Broker). El backend ES el broker:
/// acepta conexiones de dispositivos en el puerto configurado (1883 por defecto),
/// autentica por ApiKey (MQTT 5 UserProperties o legacy Username), mapea ChipId↔ClientId
/// en Redis, e intercepta cada publish para alimentar el pipeline de ingesta EXISTENTE
/// (IngestTelemetryCommand: dedup + tenant + chequeo de aprovisionamiento con auditoría).
///
/// Implementa <see cref="IMqttPublisher"/> para el push dirigido server→device
/// (comandos / OTA) mediante InjectApplicationMessage.
/// </summary>
public sealed class EmbeddedMqttBroker : BackgroundService, IMqttPublisher
{
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMqttClientMap _clientMap;
    private readonly ILogger<EmbeddedMqttBroker> _logger;

    private MqttServer? _server;
    private readonly Channel<InboundMessage> _channel =
        Channel.CreateBounded<InboundMessage>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

    private readonly string[] _apiKeys;
    private readonly bool _allowAnonymous;
    private readonly int _port;

    private readonly record struct InboundMessage(string Topic, string Payload, string? MessageId, string ClientId);

    public EmbeddedMqttBroker(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        IMqttClientMap clientMap,
        ILogger<EmbeddedMqttBroker> logger)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _clientMap = clientMap;
        _logger = logger;

        _port = int.TryParse(configuration["MqttBroker:Port"], out var p) ? p : 1883;
        _allowAnonymous = bool.TryParse(configuration["MqttBroker:AllowAnonymous"], out var anon) && anon;
        _apiKeys = configuration.GetSection("MqttBroker:ApiKeys").Get<string[]>() ?? Array.Empty<string>();
    }

    // =========================================================================
    // Hosted service lifecycle
    // =========================================================================
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(_port)
            .Build();

        var factory = new MqttServerFactory();
        _server = factory.CreateMqttServer(options);

        _server.ValidatingConnectionAsync += OnValidatingConnection;
        _server.InterceptingPublishAsync += OnInterceptingPublish;
        _server.ClientConnectedAsync += OnClientConnected;
        _server.ClientDisconnectedAsync += OnClientDisconnected;

        await _server.StartAsync();
        _logger.LogInformation(
            "Broker MQTT embebido iniciado en puerto {Port}. Anónimo={Anon}, ApiKeys configuradas={KeyCount}",
            _port, _allowAnonymous, _apiKeys.Length);

        // Consumidor del canal → pipeline de ingesta
        try
        {
            await foreach (var msg in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                await DispatchToPipelineAsync(msg, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // apagado ordenado
        }
        finally
        {
            if (_server is not null)
            {
                await _server.StopAsync();
                _logger.LogInformation("Broker MQTT embebido detenido.");
            }
        }
    }

    private async Task DispatchToPipelineAsync(InboundMessage msg, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            // Reutiliza el pipeline existente (dedup, tenant, provisioning con auditoría).
            await mediator.Send(new IngestTelemetryCommand(msg.Topic, msg.Payload, "real"), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando mensaje MQTT entrante del tópico {Topic}", msg.Topic);
        }
    }

    // =========================================================================
    // Autenticación de conexión (ApiKey) + mapeo ChipId↔ClientId
    // =========================================================================
    private async Task OnValidatingConnection(ValidatingConnectionEventArgs e)
    {
        string? apiKey;
        string? chipId;

        if (e.ProtocolVersion == MQTTnet.Formatter.MqttProtocolVersion.V500)
        {
            apiKey = e.UserProperties?
                .FirstOrDefault(pp => string.Equals(pp.Name, "ApiKey", StringComparison.OrdinalIgnoreCase))?.Value;
            chipId = e.UserProperties?
                .FirstOrDefault(pp => string.Equals(pp.Name, "ChipId", StringComparison.OrdinalIgnoreCase))?.Value;
        }
        else
        {
            // Legacy (3.1 / 3.1.1): Username = ApiKey, ClientId = ChipId
            apiKey = e.UserName;
            chipId = e.ClientId;
        }

        if (!_allowAnonymous)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                _logger.LogWarning("Conexión rechazada ({ClientId}): ApiKey no provista.", e.ClientId);
                e.ReasonCode = MqttConnectReasonCode.NotAuthorized;
                return;
            }

            // (a) ApiKey global de bootstrap (p.ej. simulador/dev), o
            // (b) ApiKey por dispositivo: el ChipId debe estar REGISTRADO y la clave
            //     coincidir con el hash almacenado. Un ChipId no registrado se rechaza
            //     (modelo de adopción: la plataforma no "ve" dispositivos no provisionados).
            bool isGlobalKey = _apiKeys.Length > 0 && _apiKeys.Contains(apiKey);
            bool isDeviceKey = false;

            if (!isGlobalKey)
            {
                string lookupChip = !string.IsNullOrWhiteSpace(chipId) ? chipId! : e.ClientId;
                if (!string.IsNullOrWhiteSpace(lookupChip))
                {
                    using var scope = _scopeFactory.CreateScope();
                    var deviceRepo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
                    var apiKeySvc = scope.ServiceProvider.GetRequiredService<IDeviceApiKeyService>();
                    var device = await deviceRepo.GetByChipIdGlobalAsync(lookupChip);
                    if (device is not null && device.HasMqttApiKey &&
                        apiKeySvc.Verify(apiKey, device.MqttApiKeyHash!))
                    {
                        isDeviceKey = true;
                    }
                }
            }

            if (!isGlobalKey && !isDeviceKey)
            {
                _logger.LogWarning(
                    "Conexión rechazada ({ClientId}): credencial inválida o dispositivo no registrado.",
                    e.ClientId);
                e.ReasonCode = MqttConnectReasonCode.NotAuthorized;
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(chipId))
            chipId = e.ClientId;

        await _clientMap.SetOrUpdateAsync(chipId, e.ClientId);
        _logger.LogInformation("Dispositivo conectado: ClientId={ClientId}, ChipId={ChipId}", e.ClientId, chipId);
        e.ReasonCode = MqttConnectReasonCode.Success;
    }

    // =========================================================================
    // Intercepción de publicaciones → cola acotada
    // =========================================================================
    private Task OnInterceptingPublish(InterceptingPublishEventArgs e)
    {
        try
        {
            string topic = e.ApplicationMessage.Topic;
            string payload = e.ApplicationMessage.Payload.Length > 0
                ? Encoding.UTF8.GetString(e.ApplicationMessage.Payload)
                : string.Empty;

            string? messageId = e.ApplicationMessage.UserProperties?
                .FirstOrDefault(pp => string.Equals(pp.Name, "message-id", StringComparison.OrdinalIgnoreCase))?.Value;

            // No encolar mensajes server-originated (push de comandos/OTA).
            if (!string.Equals(e.ClientId, "internal-server", StringComparison.Ordinal))
            {
                // El canal es acotado (500). Si está lleno (pico de reconexión / BD lenta),
                // TryWrite devuelve false y la trama se descarta: hay que loguearlo para tener
                // visibilidad del backpressure en vez de perder telemetría en silencio.
                if (!_channel.Writer.TryWrite(new InboundMessage(topic, payload, messageId, e.ClientId)))
                {
                    _logger.LogWarning(
                        "Canal de ingesta MQTT lleno (cap {Capacity}); se descartó una trama de {ClientId} (tópico {Topic}). Backpressure.",
                        500, e.ClientId, topic);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error interceptando publicación MQTT.");
        }
        return Task.CompletedTask;
    }

    // =========================================================================
    // Presencia (online/offline)
    // =========================================================================
    private async Task OnClientConnected(ClientConnectedEventArgs e)
    {
        _logger.LogInformation("Cliente MQTT conectado: {ClientId}", e.ClientId);
        await Task.CompletedTask;
    }

    private async Task OnClientDisconnected(ClientDisconnectedEventArgs e)
    {
        _logger.LogInformation("Cliente MQTT desconectado: {ClientId}", e.ClientId);
        var chipId = await _clientMap.GetChipIdByClientIdAsync(e.ClientId);
        if (!string.IsNullOrEmpty(chipId))
        {
            await _clientMap.RemoveAsync(chipId);
        }
    }

    // =========================================================================
    // IMqttPublisher — push dirigido server→device vía InjectApplicationMessage
    // =========================================================================
    public Task PublishAsync(string topic, string payload, int qos = 1, bool retain = false, CancellationToken cancellationToken = default)
        => PublishAsync(topic, payload, new MqttPublishOptions { Qos = qos, Retain = retain }, cancellationToken);

    public async Task PublishAsync(string topic, string payload, MqttPublishOptions options, CancellationToken cancellationToken = default)
    {
        if (_server is null)
        {
            _logger.LogWarning("PublishAsync invocado antes de iniciar el broker embebido.");
            return;
        }

        var qos = options.Qos switch
        {
            0 => MqttQualityOfServiceLevel.AtMostOnce,
            2 => MqttQualityOfServiceLevel.ExactlyOnce,
            _ => MqttQualityOfServiceLevel.AtLeastOnce
        };

        var builder = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .WithQualityOfServiceLevel(qos)
            .WithRetainFlag(options.Retain);

        if (options.MessageExpiryInterval.HasValue)
            builder.WithMessageExpiryInterval(options.MessageExpiryInterval.Value);
        if (!string.IsNullOrEmpty(options.ResponseTopic))
            builder.WithResponseTopic(options.ResponseTopic);
        if (options.CorrelationData is not null)
            builder.WithCorrelationData(options.CorrelationData);
        if (options.UserProperties is not null)
            foreach (var (k, v) in options.UserProperties)
                builder.WithUserProperty(k, v);

        await _server.InjectApplicationMessage(
            new InjectedMqttApplicationMessage(builder.Build()) { SenderClientId = "internal-server" });
    }
}
