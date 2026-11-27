using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using IoT.Application.Common.Interfaces;
using IoT.Application.Ingestion.Commands;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Persistence;
using IoT.Protocols.Tcp;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IoT.Infrastructure.Tcp;

/// <summary>
/// Gateway TCP multi-protocolo (ADR-007). Un listener por protocolo (puerto por protocolo,
/// decisión del dueño), cada uno con su <see cref="ITcpProtocolHandler"/>. El manejo de
/// conexión es genérico: límite de conexiones, timeout de inactividad, compuerta de adopción
/// (sólo IMEI registrado y activo se adopta y recibe ACK), y ruteo del decode por el hint de
/// transporte del handler. Implementa ITcpCommandGateway para el downlink de comandos.
/// </summary>
public sealed class MultiProtocolTcpGatewayService : BackgroundService, ITcpCommandGateway
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IReadOnlyDictionary<string, ITcpProtocolHandler> _handlers;
    private readonly ILogger<MultiProtocolTcpGatewayService> _logger;
    private readonly ITelemetryQueuePublisher? _telemetryQueuePublisher;

    private readonly ConcurrentDictionary<string, TcpDeviceSession> _activeSessions = new();
    private readonly List<TcpListener> _listeners = new();
    private SemaphoreSlim? _connectionLimiter;
    private int _idleTimeoutSeconds = 300;
    private volatile bool _stopping;

    // Re-validación periódica del estado del dispositivo (revocación/suspensión en caliente).
    private static readonly TimeSpan AuthRecheck = TimeSpan.FromSeconds(60);

    public MultiProtocolTcpGatewayService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IEnumerable<ITcpProtocolHandler> handlers,
        ILogger<MultiProtocolTcpGatewayService> logger,
        ITelemetryQueuePublisher? telemetryQueuePublisher = null)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _handlers = handlers.ToDictionary(h => h.Family, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
        _telemetryQueuePublisher = telemetryQueuePublisher;
    }

    public bool IsConnected(string imei) =>
        _activeSessions.TryGetValue(imei, out var s) && s.Client.Connected;

    public async Task<bool> SendCommandAsync(string imei, string commandText, CancellationToken cancellationToken = default)
    {
        if (!_activeSessions.TryGetValue(imei, out var session) || !session.Client.Connected)
        {
            _logger.LogWarning("No se puede enviar comando TCP: dispositivo IMEI {Imei} no conectado.", imei);
            return false;
        }

        try
        {
            await session.WriteLock.WaitAsync(cancellationToken);
            try
            {
                ushort serial = ++session.ServerSerialCounter;
                byte[]? frame = session.Handler.BuildCommand(commandText, serial);
                if (frame == null)
                {
                    _logger.LogWarning("La familia {Family} no soporta comandos salientes.", session.Handler.Family);
                    return false;
                }
                await session.Stream.WriteAsync(frame, cancellationToken);
                await session.Stream.FlushAsync(cancellationToken);
            }
            finally
            {
                session.WriteLock.Release();
            }

            _logger.LogInformation("Comando enviado a IMEI {Imei} ({Family}): '{Command}'", imei, session.Handler.Family, commandText);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo al enviar comando TCP a IMEI {Imei}", imei);
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue<bool>("TcpGateway:Enabled", true))
        {
            _logger.LogInformation("Gateway TCP multi-protocolo deshabilitado por configuración.");
            return;
        }

        int maxConnections = _configuration.GetValue<int>("TcpGateway:MaxConnections", 5000);
        _idleTimeoutSeconds = _configuration.GetValue<int>("TcpGateway:IdleTimeoutSeconds", 300);
        _connectionLimiter = new SemaphoreSlim(maxConnections, maxConnections);

        var listeners = _configuration.GetSection("TcpGateway:Listeners").Get<List<ListenerConfig>>();
        if (listeners == null || listeners.Count == 0)
        {
            // Compatibilidad: si no se configuran listeners, uno GT06 en TcpGateway:Port (5023).
            listeners = new List<ListenerConfig>
            {
                new() { Protocol = "GT06", Port = _configuration.GetValue<int>("TcpGateway:Port", 5023) }
            };
        }

        var tasks = new List<Task>();
        foreach (var cfg in listeners)
        {
            if (string.IsNullOrWhiteSpace(cfg.Protocol) || cfg.Port <= 0) continue;
            if (!_handlers.TryGetValue(cfg.Protocol, out var handler))
            {
                _logger.LogWarning("No hay handler TCP para el protocolo '{Protocol}'; listener omitido.", cfg.Protocol);
                continue;
            }
            tasks.Add(AcceptLoopAsync(handler, cfg.Port, stoppingToken));
        }

        if (tasks.Count == 0)
        {
            _logger.LogWarning("Gateway TCP multi-protocolo sin listeners válidos.");
            return;
        }

        await Task.WhenAll(tasks);
    }

    private async Task AcceptLoopAsync(ITcpProtocolHandler handler, int port, CancellationToken stoppingToken)
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            lock (_listeners) _listeners.Add(listener);
            _logger.LogInformation("Listener TCP {Family} escuchando en puerto {Port}.", handler.Family, port);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "No se pudo abrir el listener {Family} en puerto {Port}.", handler.Family, port);
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _connectionLimiter!.WaitAsync(stoppingToken);
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stoppingToken);
                }
                catch
                {
                    _connectionLimiter.Release();
                    throw;
                }
                _ = HandleConnectionAsync(client, handler, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Listener TCP {Family} apagándose.", handler.Family);
        }
        catch (Exception) when (_stopping || stoppingToken.IsCancellationRequested)
        {
            // Apagado ordenado: listener.Stop() desbloquea AcceptTcpClientAsync con
            // ObjectDisposedException. No es un error fatal.
            _logger.LogInformation("Listener TCP {Family} detenido durante el apagado.", handler.Family);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Error fatal en el listener TCP {Family}.", handler.Family);
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, ITcpProtocolHandler handler, CancellationToken stoppingToken)
    {
        // try/finally externo: garantiza liberar el permiso del semáforo y cerrar el socket
        // AUNQUE GetStream()/RemoteEndPoint lancen antes de entrar al bucle (RST tras accept).
        string endpoint = "Unknown";
        try
        {
            endpoint = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
            _logger.LogInformation("Nueva conexión TCP {Family} desde {Endpoint}", handler.Family, endpoint);

            using var session = new TcpDeviceSession
            {
                Client = client,
                Stream = client.GetStream(),
                Handler = handler,
                ConnectedAtUtc = DateTime.UtcNow,
                LastActivityUtc = DateTime.UtcNow
            };

            byte[] readBuffer = new byte[4096];
            var frameBuffer = new List<byte>(8192);

            try
            {
                while (!stoppingToken.IsCancellationRequested && client.Connected)
                {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                idleCts.CancelAfter(TimeSpan.FromSeconds(_idleTimeoutSeconds));

                int bytesRead;
                try
                {
                    bytesRead = await session.Stream.ReadAsync(readBuffer, idleCts.Token);
                }
                catch (OperationCanceledException) when (idleCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Conexión TCP {Family} inactiva ({Endpoint}, IMEI {Imei}); cerrando.", handler.Family, endpoint, session.Imei);
                    break;
                }

                if (bytesRead == 0)
                {
                    _logger.LogInformation("Conexión TCP {Family} cerrada por el equipo ({Endpoint}, IMEI {Imei}).", handler.Family, endpoint, session.Imei);
                    break;
                }

                session.LastActivityUtc = DateTime.UtcNow;
                frameBuffer.AddRange(readBuffer.AsSpan(0, bytesRead).ToArray());

                while (handler.TryReadFrame(frameBuffer, string.IsNullOrEmpty(session.Imei) ? null : session.Imei, out var outcome) && outcome != null)
                {
                    await ProcessOutcomeAsync(outcome, session, handler, stoppingToken);
                    if (session.Rejected) break;
                }

                if (session.Rejected)
                {
                    _logger.LogInformation("Cerrando conexión TCP {Family} rechazada desde {Endpoint}.", handler.Family, endpoint);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error TCP {Family} con {Endpoint} (IMEI {Imei}).", handler.Family, endpoint, session.Imei);
        }
            finally
            {
                if (!string.IsNullOrEmpty(session.Imei))
                {
                    _activeSessions.TryRemove(new KeyValuePair<string, TcpDeviceSession>(session.Imei, session));
                    _logger.LogInformation("Sesión TCP desregistrada para IMEI {Imei}", session.Imei);
                }
            }
        }
        catch (Exception ex)
        {
            // Fallo temprano (p.ej. GetStream/RemoteEndPoint ante un RST inmediato tras accept).
            _logger.LogWarning(ex, "Fallo temprano en conexión TCP {Family} desde {Endpoint}.", handler.Family, endpoint);
        }
        finally
        {
            // SIEMPRE: cerrar socket y liberar el permiso del semáforo (evita fuga de cupos).
            try { client.Close(); } catch { /* ignore */ }
            _connectionLimiter?.Release();
        }
    }

    private async Task ProcessOutcomeAsync(TcpFrameOutcome outcome, TcpDeviceSession session, ITcpProtocolHandler handler, CancellationToken cancellationToken)
    {
        // 1. Identidad + compuerta de adopción.
        if (outcome.IdentifiesSession && !string.IsNullOrWhiteSpace(outcome.SessionImei))
        {
            if (string.IsNullOrEmpty(session.Imei))
            {
                // Resolución global del dispositivo (sin tenant): sesión de sistema para que
                // bajo Platform:Rls:Enforce la lectura cross-tenant no sea bloqueada (ADR-003 D6).
                using var scope = _scopeFactory.CreateSystemScope();
                var deviceRepo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
                var device = await deviceRepo.GetByChipIdGlobalAsync(outcome.SessionImei, cancellationToken);
                if (device is null || device.AdminStatus != DeviceAdminStatus.Active)
                {
                    session.Rejected = true;
                    _logger.LogWarning("Conexión TCP {Family} rechazada (IMEI {Imei}): no registrado o inactivo. Sin ACK.", handler.Family, outcome.SessionImei);
                    return;
                }
                if (device.HasMqttApiKey)
                {
                    session.Rejected = true;
                    _logger.LogWarning("Conexión TCP {Family} rechazada (IMEI {Imei}): el dispositivo requiere transporte MQTT autenticado por ApiKey.", handler.Family, outcome.SessionImei);
                    return;
                }
                session.Imei = outcome.SessionImei;
                session.TenantId = device.TenantId;
                session.DeviceId = device.Id;
                session.LastAuthCheckUtc = DateTime.UtcNow;
                // Reconexión: cerrar una sesión previa del mismo IMEI (socket medio-abierto)
                // para evitar doble ingesta y ambigüedad en el ruteo de comandos.
                if (_activeSessions.TryGetValue(session.Imei, out var previous) && !ReferenceEquals(previous, session))
                {
                    previous.Rejected = true;
                    try { previous.Client.Close(); } catch { /* ignore */ }
                }
                _activeSessions[session.Imei] = session;
                _logger.LogInformation("Dispositivo autenticado por TCP {Family}: IMEI {Imei} (tenant {TenantId}).", handler.Family, session.Imei, device.TenantId);
            }
            else if (!string.Equals(session.Imei, outcome.SessionImei, StringComparison.Ordinal))
            {
                session.Rejected = true;
                _logger.LogWarning("Segundo IMEI distinto en la misma conexión TCP {Family} ({New} != {Existing}); rechazado.", handler.Family, outcome.SessionImei, session.Imei);
                return;
            }
        }

        // 2. Toda trama que no identifique requiere una sesión ya adoptada.
        if (string.IsNullOrEmpty(session.Imei))
        {
            session.Rejected = true;
            _logger.LogWarning("Trama TCP {Family} sin sesión autenticada desde conexión no adoptada; rechazada.", handler.Family);
            return;
        }

        // 2b. Re-validación periódica del estado: una revocación/suspensión en caliente debe
        //     dejar de recibir ACK y comandos (no esperar al idle timeout).
        if (DateTime.UtcNow - session.LastAuthCheckUtc > AuthRecheck)
        {
            using var scope = _scopeFactory.CreateSystemScope();
            var deviceRepo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
            var current = await deviceRepo.GetByChipIdGlobalAsync(session.Imei, cancellationToken);
            if (current is null || current.AdminStatus != DeviceAdminStatus.Active)
            {
                session.Rejected = true;
                _logger.LogWarning("Sesión TCP {Family} IMEI {Imei} revocada/suspendida en caliente; cerrando sin ACK.", handler.Family, session.Imei);
                return;
            }
            session.LastAuthCheckUtc = DateTime.UtcNow;
        }

        // 3. ACK/handshake (sólo para sesiones ya adoptadas).
        if (outcome.Ack != null && outcome.Ack.Length > 0)
        {
            await session.WriteLock.WaitAsync(cancellationToken);
            try
            {
                await session.Stream.WriteAsync(outcome.Ack, cancellationToken);
                await session.Stream.FlushAsync(cancellationToken);
            }
            finally
            {
                session.WriteLock.Release();
            }
        }

        // 4. Ingesta vía pipeline (ruteo por hint de transporte cuando aplica). Un fallo de
        //    INFRAESTRUCTURA (Redis/BD) no debe tumbar la conexión del dispositivo; se loguea
        //    y se continúa con la siguiente trama.
        if (outcome.Ingest && !string.IsNullOrWhiteSpace(outcome.IngestPayload))
        {
            try
            {
                // ADR-016: Encolado particionado secuencial por origen (IMEI)
                if (_telemetryQueuePublisher is not null)
                {
                    await _telemetryQueuePublisher.EnqueueAsync(new TelemetryQueueItem(
                        outcome.IngestTopic,
                        outcome.IngestPayload,
                        Source: $"tcp-{handler.Family.ToLowerInvariant()}",
                        TransportProtocol: string.IsNullOrEmpty(outcome.TransportProtocol) ? null : outcome.TransportProtocol,
                        TransportDeviceIdentifier: session.Imei),
                        cancellationToken);
                }
                else
                {
                    using var scope = _scopeFactory.CreateScope();
                    var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                    var result = await mediator.Send(new IngestTelemetryCommand(
                        outcome.IngestTopic,
                        outcome.IngestPayload,
                        Source: $"tcp-{handler.Family.ToLowerInvariant()}",
                        TransportProtocol: string.IsNullOrEmpty(outcome.TransportProtocol) ? null : outcome.TransportProtocol),
                        cancellationToken);

                    if (result.IsFailure)
                    {
                        _logger.LogWarning("Ingesta TCP {Family} fallida para IMEI {Imei}: {Error}", handler.Family, session.Imei, result.Error.Message);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error de infraestructura ingiriendo TCP {Family} IMEI {Imei}; la conexión se mantiene.", handler.Family, session.Imei);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        lock (_listeners)
        {
            foreach (var l in _listeners)
            {
                try { l.Stop(); } catch { /* ignore */ }
            }
        }
        await base.StopAsync(cancellationToken);
    }

    private sealed class ListenerConfig
    {
        public string Protocol { get; set; } = string.Empty;
        public int Port { get; set; }
    }

    private sealed class TcpDeviceSession : IDisposable
    {
        public TcpClient Client { get; init; } = null!;
        public NetworkStream Stream { get; init; } = null!;
        public ITcpProtocolHandler Handler { get; init; } = null!;
        public string Imei { get; set; } = string.Empty;
        public Guid TenantId { get; set; }
        public Guid DeviceId { get; set; }
        public bool Rejected { get; set; }
        public DateTime ConnectedAtUtc { get; init; }
        public DateTime LastActivityUtc { get; set; }
        public DateTime LastAuthCheckUtc { get; set; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public ushort ServerSerialCounter;

        public void Dispose()
        {
            WriteLock.Dispose();
            Stream.Dispose();
            Client.Dispose();
        }
    }
}
