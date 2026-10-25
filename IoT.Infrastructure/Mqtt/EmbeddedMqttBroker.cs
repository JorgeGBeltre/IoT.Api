using System.Text;
using System.Threading.Channels;
using IoT.Application.Common.Interfaces;
using IoT.Application.Ingestion.Commands;
using IoT.Domain.Enums;
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
public sealed class EmbeddedMqttBroker : BackgroundService, IMqttPublisher, IMqttSessionRevoker
{
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMqttClientMap _clientMap;
    private readonly ILogger<EmbeddedMqttBroker> _logger;

    private MqttServer? _server;

    /// <summary>
    /// Canal de telemetría de VOLUMEN. Se escribe con TryWrite (no se bloquea el pipeline del
    /// broker esperando a una base lenta): si está lleno, la trama se descarta con un warning.
    /// Para la telemetría eso es aceptable —llega otra en segundos—, pero NO para los reportes
    /// que cierran una campaña OTA, que van por <see cref="_controlChannel"/> aparte.
    /// </summary>
    private readonly Channel<InboundMessage> _channel =
        Channel.CreateBounded<InboundMessage>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

    /// <summary>
    /// Canal de PRIORIDAD para los reportes de control OTA (<c>ota/&lt;id&gt;/success|error</c>),
    /// que son los únicos que cierran una campaña y que el firmware publica en QoS 0 (sin
    /// reintento): si se perdieran en el canal de volumen, el equipo quedaría "actualizando"
    /// aunque hubiera terminado (MEDIUM-15). Tráfico escaso (uno por campaña), consumidor
    /// propio, así que una ráfaga de telemetría no puede desplazarlos.
    /// </summary>
    private readonly Channel<InboundMessage> _controlChannel =
        Channel.CreateBounded<InboundMessage>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

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
    /// Clientes conectados con una credencial de bootstrap (simulador y herramientas del
    /// operador). No representan a un equipo concreto: pueden publicar telemetría en nombre
    /// de varios, pero no leer el canal de bajada de ninguno. En modo anónimo también se
    /// marca aquí (sin tenant: ver <see cref="_bootstrapTenantIds"/>).
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _bootstrapClientIds = new(StringComparer.Ordinal);

    /// <summary>
    /// ClientId → tenant al que está ACOTADA la credencial de bootstrap de esa sesión. Es lo
    /// que cierra MEDIUM-2: la credencial de bootstrap ya no es una lista global igual para
    /// todos los tenants, sino una por tenant (colgada de la entidad Tenant), y aquí se
    /// recuerda a qué tenant pertenece la sesión para que la ingesta rechace toda trama cuyo
    /// dispositivo resuelto sea de OTRO tenant. Una sesión anónima NO entra aquí (queda sin
    /// tenant): asignarle uno abriría una vía de inyección sin credencial.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Guid> _bootstrapTenantIds = new(StringComparer.Ordinal);

    /// <summary>
    /// ClientIds de sesiones EXTERNAS aceptadas. Sirve para distinguir una publicación del
    /// servidor (inyectada en proceso, sin sesión) de una que llega por la red: el privilegio
    /// no puede depender del nombre, que lo elige quien conecta.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _connectedClientIds = new(StringComparer.Ordinal);

    /// <summary>
    /// ClientId → huella de la ApiKey con la que se autenticó la sesión. Es lo que permite
    /// detectar una rotación de credencial sin guardar el hash en memoria: si la huella del
    /// registro ya no coincide con la de la sesión, esa sesión entró con la clave vieja.
    /// Sólo se puebla para credenciales por dispositivo.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _sessionKeyFingerprints = new(StringComparer.Ordinal);

    /// <summary>
    /// Cada cuánto se vuelven a validar las sesiones YA establecidas. El broker sólo
    /// comprobaba la credencial en el CONNECT, así que suspender un equipo o rotarle la
    /// ApiKey no le quitaba el acceso: su sesión y sus suscripciones seguían vivas hasta que
    /// él decidiera reconectar (un tracker con keepalive largo, días). El gateway TCP sí
    /// re-valida cada 60 s; esto equipara el MQTT. Configurable con
    /// <c>MqttBroker:AuthRecheckSeconds</c>; 0 o negativo lo desactiva.
    /// </summary>
    private readonly TimeSpan _authRecheckInterval;

    private readonly record struct InboundMessage(
        string Topic,
        string Payload,
        string? MessageId,
        string ClientId,
        string? AuthenticatedChipId,
        Guid? CredentialTenantId);

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

        // Por defecto 7 días: muy por encima de cualquier intervalo de reconexión real de un
        // tracker (keepalive en minutos), así que no caduca mapeos vivos, pero garantiza que
        // una entrada abandonada no sobreviva indefinidamente.
        _maxInboundPayloadBytes =
            long.TryParse(configuration["MqttBroker:MaxInboundPayloadBytes"], out var maxPayload) && maxPayload > 0
                ? maxPayload
                : 256L * 1024;

        _authRecheckInterval =
            int.TryParse(configuration["MqttBroker:AuthRecheckSeconds"], out var recheck)
                ? TimeSpan.FromSeconds(recheck)
                : TimeSpan.FromSeconds(60);

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
            "Broker MQTT embebido iniciado en puerto {Port}. Anónimo={Anon}. Las credenciales de bootstrap se resuelven por tenant contra la base de datos.",
            _port, _allowAnonymous);

        // El bucle de re-validación corre en paralelo al consumidor del canal. Cuelga de su
        // PROPIO token enlazado: si el consumidor termina por una razón distinta a la
        // cancelación, el finally tiene que poder pararlo en vez de quedarse esperándolo.
        using var recheckCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var recheckLoop = RunAuthRecheckLoopAsync(recheckCts.Token);

        // Consumidor del canal de PRIORIDAD (reportes de cierre OTA) en paralelo al de volumen:
        // así un backlog de telemetría no retrasa el procesado de un success/error.
        var controlConsumer = ConsumeChannelAsync(_controlChannel, stoppingToken);

        // Consumidor del canal de volumen → pipeline de ingesta
        try
        {
            await ConsumeChannelAsync(_channel, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // apagado ordenado
        }
        finally
        {
            // Se para y se esperan los bucles para no dejar tareas huérfanas tocando el
            // servidor mientras se detiene. Ninguno lanza fuera de la cancelación.
            recheckCts.Cancel();
            await recheckLoop;
            try { await controlConsumer; } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }

            if (_server is not null)
            {
                await _server.StopAsync();
                _logger.LogInformation("Broker MQTT embebido detenido.");
            }
        }
    }

    private async Task ConsumeChannelAsync(Channel<InboundMessage> channel, CancellationToken stoppingToken)
    {
        await foreach (var msg in channel.Reader.ReadAllAsync(stoppingToken))
        {
            await DispatchToPipelineAsync(msg, stoppingToken);
        }
    }

    /// <summary>
    /// ¿Es un reporte de CIERRE de campaña del dispositivo (<c>ota/&lt;id&gt;/success|error</c>)?
    /// Son los únicos que cierran una OTA y viajan en QoS 0; van por el canal de prioridad para
    /// que una ráfaga de telemetría no los descarte. El progreso y el estado intermedio NO
    /// entran aquí: perder uno sólo omite una actualización de la UI.
    /// </summary>
    public static bool IsOtaClosingReport(string topic)
    {
        var parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 &&
               parts[0].Equals("ota", StringComparison.OrdinalIgnoreCase) &&
               (parts[^1].Equals("success", StringComparison.OrdinalIgnoreCase) ||
                parts[^1].Equals("error", StringComparison.OrdinalIgnoreCase));
    }

    // =========================================================================
    // Re-validación periódica de las sesiones establecidas
    // =========================================================================

    /// <summary>
    /// Vuelve a validar cada sesión autenticada por dispositivo y cierra las que ya no
    /// deberían existir: equipo suspendido o revocado, equipo borrado del registro, o
    /// credencial rotada (la huella de la sesión no coincide con la del registro).
    ///
    /// Es la red de seguridad de la revocación: cubre cualquier cambio en el registro,
    /// incluidos los que no pasan por <see cref="IMqttSessionRevoker"/> (una edición directa
    /// en base de datos, o un cambio hecho por otra instancia del backend).
    /// </summary>
    private async Task RunAuthRecheckLoopAsync(CancellationToken stoppingToken)
    {
        if (_authRecheckInterval <= TimeSpan.Zero)
        {
            _logger.LogWarning(
                "Re-validación periódica de sesiones MQTT DESACTIVADA (MqttBroker:AuthRecheckSeconds<=0): " +
                "suspender un equipo o rotarle la ApiKey no cerrará su sesión abierta.");
            return;
        }

        try
        {
            using var timer = new PeriodicTimer(_authRecheckInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RecheckAuthenticatedSessionsAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // apagado ordenado
        }
        catch (Exception ex)
        {
            // Que el bucle muera no debe tumbar la ingesta, pero sí tiene que verse: a
            // partir de aquí las revocaciones dejan de aplicarse a sesiones abiertas.
            _logger.LogError(ex,
                "El bucle de re-validación de sesiones MQTT se detuvo por un error; las sesiones " +
                "abiertas ya no se re-validarán hasta reiniciar el servicio.");
        }
    }

    /// <summary>
    /// Un ciclo de re-validación. Cuesta una lectura por sesión autenticada, igual que el
    /// gateway TCP, así que con muchos equipos conviene subir
    /// <c>MqttBroker:AuthRecheckSeconds</c>. Sólo ve las sesiones de ESTA instancia: con
    /// varias réplicas, cada una revalida las suyas, que es justo lo que hace que la
    /// revocación acabe aplicándose aunque la petición entre por otra réplica.
    /// </summary>
    private async Task RecheckAuthenticatedSessionsAsync(CancellationToken ct)
    {
        // Se toma una foto: el diccionario cambia mientras se itera (conexiones y
        // desconexiones), y una sesión que desaparezca sola ya no necesita revocarse.
        var sessions = _authenticatedChipIds.ToArray();
        if (sessions.Length == 0) return;

        using var scope = _scopeFactory.CreateScope();
        var deviceRepo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();

        foreach (var (clientId, chipId) in sessions)
        {
            ct.ThrowIfCancellationRequested();

            // La sesión pudo cerrarse entre la foto y ahora.
            if (!_connectedClientIds.ContainsKey(clientId)) continue;

            var device = await deviceRepo.GetByChipIdGlobalAsync(chipId, ct);

            _sessionKeyFingerprints.TryGetValue(clientId, out var sessionFingerprint);
            var motivo = MqttSessionRevocation.ReasonToRevoke(device, sessionFingerprint);

            if (motivo is not null)
            {
                await DisconnectSessionAsync(clientId, chipId, motivo);
            }
        }
    }

    /// <summary>
    /// Cierra una sesión concreta y suelta su estado en memoria. No lanza: el cierre es
    /// saneamiento y no debe tumbar ni el bucle ni la operación que lo pidió.
    /// </summary>
    private async Task<bool> DisconnectSessionAsync(string clientId, string chipId, string reason)
    {
        try
        {
            if (_server is null) return false;

            _logger.LogWarning(
                "Sesión MQTT REVOCADA: {ClientId} (ChipId {ChipId}); motivo: {Reason}.",
                clientId, chipId, reason);

            await _server.DisconnectClientAsync(clientId, new MqttServerClientDisconnectOptions
            {
                ReasonCode = MqttDisconnectReasonCode.NotAuthorized,
                ReasonString = reason
            });

            // El estado se suelta aquí además de en OnClientDisconnected: si el evento se
            // retrasa, una sesión revocada no debe seguir contando como autenticada.
            _authenticatedChipIds.TryRemove(clientId, out _);
            _sessionKeyFingerprints.TryRemove(clientId, out _);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "No se pudo cerrar la sesión MQTT {ClientId} (ChipId {ChipId}).", clientId, chipId);
            return false;
        }
    }

    // =========================================================================
    // IMqttSessionRevoker — revocación inmediata
    // =========================================================================
    public async Task<bool> RevokeSessionAsync(string chipId, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(chipId)) return false;

        bool any = false;

        // Se busca por ChipId AUTENTICADO, no por ClientId: aunque hoy el CONNECT exige que
        // coincidan, la correspondencia la decide la autenticación, no el nombre del cliente.
        foreach (var (clientId, sessionChipId) in _authenticatedChipIds.ToArray())
        {
            if (!string.Equals(sessionChipId, chipId, StringComparison.OrdinalIgnoreCase)) continue;
            if (await DisconnectSessionAsync(clientId, chipId, reason)) any = true;
        }

        return any;
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
                    TransportMessageId: msg.MessageId,
                    CredentialTenantId: msg.CredentialTenantId),
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

        // La ApiKey se busca en varios sitios porque cada firmware la manda donde le
        // conviene: Relay y GPS en el username con contraseña vacía, Fligo y Toour en una
        // UserProperty de MQTT 5, y el GLP tiene usuario y contraseña editables, así que el
        // operador la pone donde diga la ficha de credenciales. Se aceptan todos: lo que se
        // verifica es la clave contra el hash del dispositivo, no en qué campo viajó.
        string? apiKey;
        string? apiKeyAlt = null;
        string? chipId;
        // Verdadero sólo si la conexión se autenticó con la ApiKey PROPIA del dispositivo;
        // en ese caso el ChipId queda probado y puede propagarse al pipeline.
        bool authenticatedPerDevice = false;
        string? deviceKeyFingerprint = null;
        // Tenant al que acota la credencial de bootstrap, si la conexión entró por esa vía.
        // Se declara aquí para poder leerlo en la asignación de estado tras el bloque de
        // validación (que vive dentro de if (!_allowAnonymous)).
        Guid? bootstrapTenantId = null;

        if (e.ProtocolVersion == MQTTnet.Formatter.MqttProtocolVersion.V500)
        {
            apiKey = e.UserProperties?
                .FirstOrDefault(pp => string.Equals(pp.Name, "ApiKey", StringComparison.OrdinalIgnoreCase))?.Value;
            chipId = e.UserProperties?
                .FirstOrDefault(pp => string.Equals(pp.Name, "ChipId", StringComparison.OrdinalIgnoreCase))?.Value;

            // Un cliente MQTT 5 que no use la UserProperty (una herramienta genérica, o un
            // firmware futuro) sigue pudiendo autenticarse por los campos de siempre.
            if (string.IsNullOrEmpty(apiKey)) apiKey = e.UserName;
            apiKeyAlt = e.Password;
        }
        else
        {
            // Legacy (3.1 / 3.1.1): ClientId = ChipId, y la ApiKey en el username (lo que
            // mandan Relay y GPS) o en la contraseña (como la pone el operador en el GLP
            // siguiendo la ficha de credenciales).
            apiKey = e.UserName;
            apiKeyAlt = e.Password;
            chipId = e.ClientId;
        }

        if (!_allowAnonymous)
        {
            // Candidatas en orden: primero donde la manda el firmware, después el otro
            // campo. Se descartan las vacías para no llamar a Verify con cadena vacía.
            var apiKeyCandidates = new[] { apiKey, apiKeyAlt }
                .Where(k => !string.IsNullOrEmpty(k))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (apiKeyCandidates.Length == 0)
            {
                _logger.LogWarning("Conexión rechazada ({ClientId}): ApiKey no provista.", e.ClientId);
                e.ReasonCode = MqttConnectReasonCode.NotAuthorized;
                return;
            }

            // Dos credenciales posibles, resueltas contra la BASE DE DATOS (ya no hay lista
            // global en configuración, que era igual para todos los tenants — MEDIUM-2):
            // (a) ApiKey POR DISPOSITIVO: el ChipId debe estar REGISTRADO y la clave coincidir
            //     con el hash almacenado. Un ChipId no registrado se rechaza (modelo de
            //     adopción: la plataforma no "ve" dispositivos no provisionados).
            // (b) Credencial de BOOTSTRAP de un tenant: publicador multi-equipo (simulador,
            //     herramientas del operador), ACOTADO a ese tenant.
            bool isDeviceKey = false;

            using (var scope = _scopeFactory.CreateScope())
            {
                var deviceRepo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
                var apiKeySvc = scope.ServiceProvider.GetRequiredService<IDeviceApiKeyService>();

                // (a) por dispositivo
                string lookupChip = !string.IsNullOrWhiteSpace(chipId) ? chipId! : e.ClientId;
                if (!string.IsNullOrWhiteSpace(lookupChip))
                {
                    var device = await deviceRepo.GetByChipIdGlobalAsync(lookupChip);
                    if (device is not null && device.HasMqttApiKey &&
                        apiKeyCandidates.Any(k => apiKeySvc.Verify(k!, device.MqttApiKeyHash!)))
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

                        if (device.AdminStatus != DeviceAdminStatus.Active)
                        {
                            _logger.LogWarning(
                                "Conexión rechazada ({ClientId}): el dispositivo {ChipId} está en estado {Status}.",
                                e.ClientId, device.ChipId, device.AdminStatus);
                            e.ReasonCode = MqttConnectReasonCode.NotAuthorized;
                            return;
                        }

                        isDeviceKey = true;
                        // Identidad probada: la del REGISTRO, no la que declaró el cliente.
                        chipId = device.ChipId;
                        authenticatedPerDevice = true;
                        // Huella de la clave con la que entra: la re-validación periódica la
                        // compara con la del registro para detectar una rotación.
                        deviceKeyFingerprint = device.MqttApiKeyFingerprint;
                    }
                }

                // (b) credencial de bootstrap de un tenant. Se busca por el HASH de la clave
                // (lectura indexada), no se barre: el tenant resultante acota la sesión.
                if (!isDeviceKey)
                {
                    var tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
                    foreach (var k in apiKeyCandidates)
                    {
                        var tenant = await tenantRepo.GetByBootstrapApiKeyHashAsync(apiKeySvc.ComputeHash(k!));
                        if (tenant is null) continue;

                        if (!tenant.IsActive)
                        {
                            _logger.LogWarning(
                                "Conexión rechazada ({ClientId}): credencial de bootstrap del tenant {TenantId}, que está inactivo.",
                                e.ClientId, tenant.Id);
                            e.ReasonCode = MqttConnectReasonCode.NotAuthorized;
                            return;
                        }

                        bootstrapTenantId = tenant.Id;
                        break;
                    }
                }
            }

            if (!isDeviceKey && bootstrapTenantId is null)
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

        // Todas las escrituras de estado por sesión usan el patrón SET-O-REMOVE INCONDICIONAL
        // en las DOS ramas, porque el ClientId se reutiliza por diseño (ClientId == ChipId ==
        // IMEI): si una rama que no acota a un tenant NO borrara la entrada del otro caso, una
        // sesión nueva heredaría el tenant o la identidad de la que ocupó antes ese ClientId.
        if (authenticatedPerDevice)
        {
            _authenticatedChipIds[e.ClientId] = chipId;
            _bootstrapClientIds.TryRemove(e.ClientId, out _);
            _bootstrapTenantIds.TryRemove(e.ClientId, out _);
            if (!string.IsNullOrEmpty(deviceKeyFingerprint))
                _sessionKeyFingerprints[e.ClientId] = deviceKeyFingerprint!;
            else
                _sessionKeyFingerprints.TryRemove(e.ClientId, out _);
        }
        else
        {
            // Credencial de bootstrap o modo anónimo. En anónimo no hay identidad por
            // dispositivo pero SÍ hay que poder publicar (si no, el ACL denegaba toda
            // publicación con ámbito y el entorno de desarrollo quedaba roto en silencio): se
            // trata como bootstrap, que puede publicar en nombre de varios equipos pero nunca
            // leer el canal de bajada de ninguno.
            _bootstrapClientIds[e.ClientId] = 1;
            // No hay identidad por dispositivo: se limpia cualquier resto de una conexión
            // anterior con el mismo ClientId.
            _authenticatedChipIds.TryRemove(e.ClientId, out _);
            _sessionKeyFingerprints.TryRemove(e.ClientId, out _);
            // El tenant que acota la sesión: presente para una credencial de bootstrap de un
            // tenant, AUSENTE en modo anónimo (nunca se le asigna uno — eso abriría inyección
            // sin credencial). Set-o-remove para no heredar el tenant de una sesión previa.
            if (bootstrapTenantId.HasValue)
                _bootstrapTenantIds[e.ClientId] = bootstrapTenantId.Value;
            else
                _bootstrapTenantIds.TryRemove(e.ClientId, out _);
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
                // Tenant que acota la credencial de bootstrap de esta sesión, si la hay: la
                // ingesta rechaza la trama si el dispositivo resuelto es de otro tenant.
                Guid? credentialTenantId =
                    _bootstrapTenantIds.TryGetValue(e.ClientId, out var btid) ? btid : null;

                var inbound = new InboundMessage(topic, payload, messageId, e.ClientId, authenticatedChipId, credentialTenantId);

                // Los reportes de CIERRE de campaña (success/error) van por el canal de
                // prioridad: cierran la OTA, viajan en QoS 0 (sin reintento) y no pueden
                // perderse por un pico de telemetría. Su descarte, si ocurriera, es un ERROR
                // (no un warning): es un reporte crítico que no vuelve.
                if (IsOtaClosingReport(topic))
                {
                    if (!_controlChannel.Writer.TryWrite(inbound))
                    {
                        _logger.LogError(
                            "Canal de control OTA lleno; se descartó un reporte de cierre de {ClientId} (tópico {Topic}). El equipo podría quedar marcado como 'actualizando' hasta que el vigilante lo cierre.",
                            e.ClientId, topic);
                    }
                }
                else if (!_channel.Writer.TryWrite(inbound))
                {
                    // El canal de volumen es acotado (500). Si está lleno (pico de reconexión /
                    // BD lenta), TryWrite devuelve false y la trama se descarta: se loguea para
                    // tener visibilidad del backpressure en vez de perder telemetría en silencio.
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
        _sessionKeyFingerprints.TryRemove(e.ClientId, out _);
        _bootstrapTenantIds.TryRemove(e.ClientId, out _);

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
