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

    /// <summary>
    /// TTL de rescate del mapeo ChipId↔ClientId en Redis. Si el proceso muere sin disparar
    /// los eventos de desconexión (OOM-kill, SIGKILL, corte de red del nodo), las claves
    /// quedarían vivas para siempre y el push se dirigiría a sesiones inexistentes. Con TTL
    /// las entradas abandonadas se recogen solas; cada reconexión lo renueva. 0 = sin TTL.
    /// </summary>
    private readonly TimeSpan? _clientMapTtl;

    /// <summary>
    /// <summary>
    /// Tamaño máximo de una trama entrante, en bytes. Ninguna telemetría legítima de estos
    /// dispositivos se acerca: la mayor es el reporte de estado del relé, de unos pocos KiB.
    /// Configurable con <c>MqttBroker:MaxInboundPayloadBytes</c>.
    /// </summary>
    private readonly long _maxInboundPayloadBytes;

    /// <summary>
    /// ClientId → ChipId VERIFICADO contra el hash de la ApiKey del dispositivo. Sólo se
    /// puebla cuando la credencial es por dispositivo; con la ApiKey global de bootstrap
    /// (simulador) un mismo publicador habla por varios equipos y no hay identidad única.
    /// Permite que el pipeline use la identidad ya autenticada cuando el tópico no la lleva.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _authenticatedChipIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Clientes conectados con la ApiKey GLOBAL de bootstrap (simulador y herramientas del
    /// operador). No representan a un equipo concreto: pueden publicar telemetría en nombre
    /// de varios, pero no leer el canal de bajada de ninguno.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _bootstrapClientIds = new(StringComparer.Ordinal);

    /// <summary>
    /// ClientIds de sesiones EXTERNAS aceptadas. Sirve para distinguir una publicación del
    /// servidor (inyectada en proceso, sin sesión) de una que llega por la red: el privilegio
    /// no puede depender del nombre, que lo elige quien conecta.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _connectedClientIds = new(StringComparer.Ordinal);

    private readonly record struct InboundMessage(
        string Topic,
        string Payload,
        string? MessageId,
        string ClientId,
        string? AuthenticatedChipId);

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

        // Por defecto 7 días: muy por encima de cualquier intervalo de reconexión real de un
        // tracker (keepalive en minutos), así que no caduca mapeos vivos, pero garantiza que
        // una entrada abandonada no sobreviva indefinidamente.
        _maxInboundPayloadBytes =
            long.TryParse(configuration["MqttBroker:MaxInboundPayloadBytes"], out var maxPayload) && maxPayload > 0
                ? maxPayload
                : 256L * 1024;

        int ttlHours = int.TryParse(configuration["MqttBroker:ClientMapTtlHours"], out var h) ? h : 168;
        _clientMapTtl = ttlHours > 0 ? TimeSpan.FromHours(ttlHours) : null;
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
        _server.InterceptingSubscriptionAsync += OnInterceptingSubscription;
        _server.ClientConnectedAsync += OnClientConnected;
        _server.ClientDisconnectedAsync += OnClientDisconnected;

        await _server.StartAsync();

        if (_allowAnonymous)
        {
            _logger.LogWarning(
                "BROKER EN MODO ANÓNIMO: no se exige ApiKey, así que NINGUNA conexión tiene " +
                "identidad probada. El ACL de tópicos no puede comprobar la propiedad, de modo " +
                "que los equipos no podrán leer sus canales de bajada (comandos, firmware). " +
                "Sólo para desarrollo; en producción MqttBroker:AllowAnonymous debe ser false.");
        }
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
            await mediator.Send(
                new IngestTelemetryCommand(
                    msg.Topic,
                    msg.Payload,
                    "real",
                    TransportDeviceIdentifier: msg.AuthenticatedChipId,
                    TransportMessageId: msg.MessageId),
                ct);
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
        // NINGUNA conexión externa puede llamarse como el cliente interno del servidor.
        // El ClientId lo elige quien conecta, así que sin este rechazo bastaba conectar con
        // ese nombre —con la ApiKey global, o en modo anónimo— para que el ACL de tópicos
        // concediera privilegio de SERVIDOR y permitiera publicar firmware a cualquier equipo.
        if (string.Equals(e.ClientId, MqttTopicAcl.ServerClientId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Conexión rechazada: el ClientId '{ClientId}' está reservado al servidor.", e.ClientId);
            e.ReasonCode = MqttConnectReasonCode.ClientIdentifierNotValid;
            return;
        }

        string? apiKey;
        string? chipId;
        // Verdadero sólo si la conexión se autenticó con la ApiKey PROPIA del dispositivo;
        // en ese caso el ChipId queda probado y puede propagarse al pipeline.
        bool authenticatedPerDevice = false;

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
                        // El ClientId DEBE ser el del propio dispositivo. En MQTT 5 el ChipId
                        // viaja en una UserProperty y el ClientId es independiente, así que sin
                        // esta comprobación un equipo podía autenticarse con SU credencial y
                        // elegir el ClientId de OTRO. Consecuencias: (a) expulsaba a la víctima
                        // por takeover de forma sostenida; (b) con sesión persistente
                        // (setCleanStart(false), que GLP y Relay usan) al reanudar esa sesión
                        // heredaba sus SUSCRIPCIONES sin que el ACL volviera a evaluarse —sólo
                        // corre en el paquete SUBSCRIBE—, y con ellas el firmware de la víctima.
                        //
                        // Los cinco firmwares reales ya usan ClientId == ChipId: Fligo y Toour
                        // lo envían explícitamente como UserProperty, y Relay, GLP y GPS caen en
                        // el respaldo chipId = ClientId. Esta comprobación no les quita nada.
                        if (!string.Equals(e.ClientId, device.ChipId, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning(
                                "Conexión rechazada: la credencial autentica al dispositivo {ChipId} pero el ClientId es '{ClientId}'. El ClientId debe ser el ChipId del equipo.",
                                device.ChipId, e.ClientId);
                            e.ReasonCode = MqttConnectReasonCode.ClientIdentifierNotValid;
                            return;
                        }

                        isDeviceKey = true;
                        // Identidad probada: la del REGISTRO, no la que declaró el cliente.
                        chipId = device.ChipId;
                        authenticatedPerDevice = true;
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

        if (authenticatedPerDevice)
        {
            _authenticatedChipIds[e.ClientId] = chipId;
            _bootstrapClientIds.TryRemove(e.ClientId, out _);
        }
        else
        {
            // En modo anónimo tampoco hay identidad por dispositivo, pero SÍ hay que poder
            // publicar: si no, el ACL denegaba toda publicación con ámbito y el entorno de
            // desarrollo quedaba roto en silencio. Se trata como credencial global: puede
            // publicar en nombre de varios equipos, nunca leer el canal de ninguno ni
            // escribir en un canal de bajada.
            _bootstrapClientIds[e.ClientId] = 1;
            // ApiKey global o anónimo: no hay identidad única por conexión. Se limpia
            // cualquier resto de una conexión anterior con el mismo ClientId.
            _authenticatedChipIds.TryRemove(e.ClientId, out _);
        }

        // El mapeo es para push dirigido y presencia: útil, pero NO es parte de la
        // autenticación. Si Redis está caído o lento, una excepción aquí rechazaría TODAS
        // las conexiones de dispositivos (la credencial ya se validó). Se degrada en vez
        // de cortar la ingesta: el dispositivo entra y publica; sólo el push queda ciego.
        try
        {
            await _clientMap.SetOrUpdateAsync(chipId, e.ClientId, _clientMapTtl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "No se pudo registrar el mapeo ChipId={ChipId}↔ClientId={ClientId} en Redis; " +
                "la conexión se acepta pero el push dirigido no funcionará hasta la próxima reconexión.",
                chipId, e.ClientId);
        }

        _connectedClientIds[e.ClientId] = 1;
        _logger.LogInformation("Dispositivo conectado: ClientId={ClientId}, ChipId={ChipId}", e.ClientId, chipId);
        e.ReasonCode = MqttConnectReasonCode.Success;
    }

    // =========================================================================
    // ACL por tópico: un dispositivo sólo accede a LO SUYO
    // =========================================================================
    private Task OnInterceptingSubscription(InterceptingSubscriptionEventArgs e)
    {
        // Las suscripciones del propio servidor no se filtran.
        if (string.Equals(e.ClientId, MqttTopicAcl.ServerClientId, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        _authenticatedChipIds.TryGetValue(e.ClientId, out var chipId);
        bool isBootstrap = _bootstrapClientIds.ContainsKey(e.ClientId);
        string topic = e.TopicFilter.Topic;

        switch (MqttTopicAcl.CanSubscribe(topic, chipId, isBootstrap))
        {
            case MqttTopicAcl.Decision.Deny:
                _logger.LogWarning(
                    "Suscripción RECHAZADA: el cliente {ClientId} (ChipId {ChipId}) pidió '{Topic}', que no le pertenece.",
                    e.ClientId, chipId ?? "sin autenticar", topic);
                e.ProcessSubscription = false;
                e.Response.ReasonCode = MqttSubscribeReasonCode.UnspecifiedError;
                break;

            case MqttTopicAcl.Decision.AllowSharedLegacy:
                // Tópico sin identificador que ciertos firmwares ya desplegados usan tal
                // cual: se permite por compatibilidad, pero NO aísla entre equipos.
                _logger.LogWarning(
                    "Suscripción a tópico COMPARTIDO '{Topic}' por {ClientId}: sin aislamiento entre dispositivos.",
                    topic, e.ClientId);
                break;
        }

        return Task.CompletedTask;
    }

    // =========================================================================
    // Intercepción de publicaciones → cola acotada
    // =========================================================================
    private Task OnInterceptingPublish(InterceptingPublishEventArgs e)
    {
        try
        {
            string topic = e.ApplicationMessage.Topic;

            // ACL de publicación: los tópicos de BAJADA (firmware y comandos) son del
            // servidor. Sin esto, un equipo autenticado podía publicar en el ota/ de OTRO y
            // colarle una imagen de firmware, o inyectarle comandos.
            //
            // El origen "servidor" se prueba por AUSENCIA de sesión externa con ese ClientId,
            // no por su nombre: el ClientId lo elige quien conecta, así que confiar en él
            // dejaba que cualquiera con la ApiKey global se hiciera pasar por el servidor.
            // (Además, OnValidatingConnection ya rechaza ese ClientId reservado.)
            bool isServerOrigin =
                string.Equals(e.ClientId, MqttTopicAcl.ServerClientId, StringComparison.Ordinal) &&
                !_connectedClientIds.ContainsKey(e.ClientId);

            _authenticatedChipIds.TryGetValue(e.ClientId, out var publisherChipId);
            var publishDecision = MqttTopicAcl.CanPublish(
                topic, isServerOrigin, publisherChipId, _bootstrapClientIds.ContainsKey(e.ClientId));
            if (publishDecision == MqttTopicAcl.Decision.Deny)
            {
                _logger.LogWarning(
                    "Publicación RECHAZADA: el cliente {ClientId} (ChipId {ChipId}) intentó publicar en '{Topic}'.",
                    e.ClientId, publisherChipId ?? "sin autenticar", topic);
                e.ProcessPublish = false;
                return Task.CompletedTask;
            }
            // Cota de tamaño ANTES de materializar el payload en string: sin ella un equipo
            // podía mandar megabytes por trama, que se convertían a string, se guardaban en la
            // columna jsonb RawPayload y multiplicaban la memoria del canal de ingesta.
            long payloadLength = e.ApplicationMessage.Payload.Length;
            if (payloadLength > _maxInboundPayloadBytes)
            {
                _logger.LogWarning(
                    "Trama MQTT descartada de {ClientId} en '{Topic}': {Size} bytes supera el máximo de {Max}.",
                    e.ClientId, topic, payloadLength, _maxInboundPayloadBytes);
                e.ProcessPublish = false;
                return Task.CompletedTask;
            }

            string payload = payloadLength > 0
                ? Encoding.UTF8.GetString(e.ApplicationMessage.Payload)
                : string.Empty;

            string? messageId = e.ApplicationMessage.UserProperties?
                .FirstOrDefault(pp => string.Equals(pp.Name, "message-id", StringComparison.OrdinalIgnoreCase))?.Value;

            // No encolar mensajes server-originated (push de comandos/OTA).
            if (!string.Equals(e.ClientId, MqttTopicAcl.ServerClientId, StringComparison.Ordinal))
            {
                // El canal es acotado (500). Si está lleno (pico de reconexión / BD lenta),
                // TryWrite devuelve false y la trama se descarta: hay que loguearlo para tener
                // visibilidad del backpressure en vez de perder telemetría en silencio.
                _authenticatedChipIds.TryGetValue(e.ClientId, out var authenticatedChipId);

                if (!_channel.Writer.TryWrite(new InboundMessage(topic, payload, messageId, e.ClientId, authenticatedChipId)))
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
        // TAKEOVER: el dispositivo ya reconectó con el MISMO ClientId y el broker cerró la
        // sesión anterior. El mapeo que hay en Redis es el de la conexión NUEVA (ya escrito
        // por OnValidatingConnection), así que darlo de baja aquí dejaría al dispositivo
        // conectado pero invisible para push y presencia. Caso dominante en trackers, que
        // usan el IMEI como ClientId fijo y reconectan al recuperar cobertura.
        if (e.DisconnectType == MqttClientDisconnectType.Takeover)
        {
            _logger.LogInformation(
                "Sesión MQTT anterior cerrada por takeover: {ClientId} (el mapeo vivo se conserva).",
                e.ClientId);
            return;
        }

        _logger.LogInformation("Cliente MQTT desconectado: {ClientId} ({DisconnectType})", e.ClientId, e.DisconnectType);

        // Se libera la identidad autenticada de ESTA conexión. En el takeover de arriba no
        // se toca, porque la entrada ya pertenece a la conexión nueva (mismo ClientId).
        _authenticatedChipIds.TryRemove(e.ClientId, out _);
        _bootstrapClientIds.TryRemove(e.ClientId, out _);
        _connectedClientIds.TryRemove(e.ClientId, out _);

        var chipId = await _clientMap.GetChipIdByClientIdAsync(e.ClientId);
        if (!string.IsNullOrEmpty(chipId))
        {
            // Baja CONDICIONAL: sólo si el mapeo sigue apuntando a ESTA conexión. Protege
            // contra desconexiones que llegan tarde, después de que el dispositivo ya se
            // registró de nuevo (con el mismo ClientId o con otro).
            await _clientMap.RemoveAsync(chipId, e.ClientId);
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
            new InjectedMqttApplicationMessage(builder.Build()) { SenderClientId = MqttTopicAcl.ServerClientId });
    }
}
