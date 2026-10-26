using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using IoT.Application.Common.Interfaces;
using IoT.Application.Ingestion.Commands;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Persistence;
using IoT.Protocols.Binary;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IoT.Infrastructure.Tcp;

/// <summary>
/// High-performance TCP Binary Gateway service for SEEWORLD R12L-F, GT06, and Concox GPS trackers (ADR-007).
/// Manages persistent device sockets, decodes binary frames, enforces synchronous 5s ACKs, and dispatches telemetry.
/// </summary>
public sealed class R12LFTcpGatewayService : BackgroundService, ITcpCommandGateway
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<R12LFTcpGatewayService> _logger;

    private readonly ConcurrentDictionary<string, TcpDeviceSession> _activeSessions = new();
    private TcpListener? _tcpListener;
    private SemaphoreSlim? _connectionLimiter;
    private int _idleTimeoutSeconds = 300;

    public R12LFTcpGatewayService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<R12LFTcpGatewayService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsConnected(string imei)
    {
        return _activeSessions.TryGetValue(imei, out var session) && session.Client.Connected;
    }

    public async Task<bool> SendCommandAsync(string imei, string commandText, CancellationToken cancellationToken = default)
    {
        if (!_activeSessions.TryGetValue(imei, out var session) || !session.Client.Connected)
        {
            _logger.LogWarning("Cannot send TCP command: device with IMEI {Imei} is not connected.", imei);
            return false;
        }

        try
        {
            await session.WriteLock.WaitAsync(cancellationToken);
            try
            {
                // El serial se incrementa y se usa DENTRO del lock: dos comandos
                // concurrentes al mismo IMEI no deben compartir número de serie.
                ushort serial = ++session.ServerSerialCounter;
                byte[] commandFrame = BuildServerCommandFrame(commandText, serial);
                await session.Stream.WriteAsync(commandFrame, cancellationToken);
                await session.Stream.FlushAsync(cancellationToken);
            }
            finally
            {
                session.WriteLock.Release();
            }

            _logger.LogInformation("Binary server command (0x80) sent successfully to IMEI {Imei}: '{Command}'", imei, commandText);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send binary command to IMEI {Imei}", imei);
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool isEnabled = _configuration.GetValue<bool>("TcpGateway:Enabled", true);
        int port = _configuration.GetValue<int>("TcpGateway:Port", 5023);
        int maxConnections = _configuration.GetValue<int>("TcpGateway:MaxConnections", 5000);
        _idleTimeoutSeconds = _configuration.GetValue<int>("TcpGateway:IdleTimeoutSeconds", 300);
        _connectionLimiter = new SemaphoreSlim(maxConnections, maxConnections);

        if (!isEnabled)
        {
            _logger.LogInformation("R12L-F TCP Binary Gateway is disabled by configuration.");
            return;
        }

        try
        {
            _tcpListener = new TcpListener(IPAddress.Any, port);
            _tcpListener.Start();
            _logger.LogInformation(
                "R12L-F TCP Binary Gateway listening on port {Port} (TCP). MaxConnections={Max}, IdleTimeout={Idle}s",
                port, maxConnections, _idleTimeoutSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                // Backpressure: no aceptar más conexiones de las permitidas (evita
                // agotamiento de recursos por sockets que nunca se autentican).
                await _connectionLimiter.WaitAsync(stoppingToken);
                TcpClient client;
                try
                {
                    client = await _tcpListener.AcceptTcpClientAsync(stoppingToken);
                }
                catch
                {
                    _connectionLimiter.Release();
                    throw;
                }
                _ = HandleClientConnectionAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("R12L-F TCP Binary Gateway is shutting down.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Fatal error in R12L-F TCP Binary Gateway listener.");
        }
        finally
        {
            _tcpListener?.Stop();
        }
    }

    private async Task HandleClientConnectionAsync(TcpClient client, CancellationToken stoppingToken)
    {
        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
        _logger.LogInformation("New incoming TCP client connection from {Endpoint}", endpoint);

        using var session = new TcpDeviceSession
        {
            Client = client,
            Stream = client.GetStream(),
            ConnectedAtUtc = DateTime.UtcNow,
            LastActivityUtc = DateTime.UtcNow
        };

        byte[] readBuffer = new byte[4096];
        var frameBuffer = new List<byte>(8192);

        try
        {
            while (!stoppingToken.IsCancellationRequested && client.Connected)
            {
                // Timeout de inactividad por conexión: un socket que no envía nada (p.ej.
                // nunca hace login, o queda half-open) se cierra en vez de bloquear el
                // Task para siempre y dejar la sesión colgada.
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                idleCts.CancelAfter(TimeSpan.FromSeconds(_idleTimeoutSeconds));

                int bytesRead;
                try
                {
                    bytesRead = await session.Stream.ReadAsync(readBuffer, idleCts.Token);
                }
                catch (OperationCanceledException) when (idleCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("TCP connection idle-timeout {Endpoint} (IMEI: {Imei}); closing.", endpoint, session.Imei);
                    break;
                }

                if (bytesRead == 0)
                {
                    _logger.LogInformation("TCP connection closed by remote device {Endpoint} (IMEI: {Imei})", endpoint, session.Imei);
                    break;
                }

                session.LastActivityUtc = DateTime.UtcNow;
                frameBuffer.AddRange(readBuffer.AsSpan(0, bytesRead).ToArray());

                // Process all complete frames currently in accumulator
                while (TryExtractNextFrame(frameBuffer, out var frame) && frame != null)
                {
                    await ProcessFrameAsync(frame, session, stoppingToken);
                    if (session.Rejected) break; // no seguir drenando tramas de una conexión rechazada
                }

                // Login de un IMEI no registrado/inactivo: cerrar la conexión.
                if (session.Rejected)
                {
                    _logger.LogInformation("Cerrando conexión TCP rechazada desde {Endpoint}.", endpoint);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TCP error occurred during communication with {Endpoint} (IMEI: {Imei})", endpoint, session.Imei);
        }
        finally
        {
            if (!string.IsNullOrEmpty(session.Imei))
            {
                // Quitar sólo si la sesión registrada bajo este IMEI es ESTA misma
                // (evita borrar una sesión ajena que haya reemplazado a la nuestra).
                _activeSessions.TryRemove(new KeyValuePair<string, TcpDeviceSession>(session.Imei, session));
                _logger.LogInformation("Device session deregistered for IMEI {Imei}", session.Imei);
            }
            client.Close();
            _connectionLimiter?.Release();
        }
    }

    private async Task ProcessFrameAsync(R12LFFrame frame, TcpDeviceSession session, CancellationToken cancellationToken)
    {
        // Sólo el login puede ocurrir sin sesión adoptada. Cualquier otra trama de una
        // conexión no autenticada o ya rechazada NO se procesa ni se ACKea: el equipo no
        // provisionado no debe recibir respuesta alguna (modelo de adopción).
        if (frame.ProtocolNumber != R12LFFrame.ProtocolLogin &&
            (session.Rejected || string.IsNullOrEmpty(session.Imei)))
        {
            session.Rejected = true;
            return;
        }

        switch (frame.ProtocolNumber)
        {
            case R12LFFrame.ProtocolLogin:
                string imei = frame.ExtractImeiFromLoginPacket();
                if (string.IsNullOrWhiteSpace(imei))
                {
                    session.Rejected = true;
                    _logger.LogWarning("Login TCP sin IMEI válido; conexión rechazada.");
                    break;
                }

                // Un socket sólo se autentica UNA vez. Un segundo login con IMEI distinto
                // es abuso o trama corrupta: rechazar (evita dejar claves huérfanas en
                // _activeSessions, que el finally sólo limpia por el IMEI actual).
                if (!string.IsNullOrEmpty(session.Imei) && !string.Equals(session.Imei, imei, StringComparison.Ordinal))
                {
                    session.Rejected = true;
                    _logger.LogWarning("Segundo login TCP con IMEI distinto ({NewImei} != {ExistingImei}); rechazado.", imei, session.Imei);
                    break;
                }

                // Compuerta de adopción: el IMEI (== ChipId) debe estar REGISTRADO y Activo.
                // Espeja EmbeddedMqttBroker.OnValidatingConnection: un equipo no provisionado
                // NO se adopta (ni sesión ni ACK); la plataforma no debe "verlo".
                // Resolución global del dispositivo (sin tenant): sesión de sistema para que
                // bajo Platform:Rls:Enforce la lectura cross-tenant no sea bloqueada (ADR-003 D6).
                using (var scope = _scopeFactory.CreateSystemScope())
                {
                    var deviceRepo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
                    var device = await deviceRepo.GetByChipIdGlobalAsync(imei, cancellationToken);
                    if (device is null || device.AdminStatus != DeviceAdminStatus.Active)
                    {
                        session.Rejected = true;
                        _logger.LogWarning(
                            "Conexión TCP rechazada (IMEI {Imei}): dispositivo no registrado o inactivo. No se envía ACK.",
                            imei);
                        break; // el bucle de lectura cerrará el socket sin ACK
                    }

                    session.Imei = imei;
                    session.TenantId = device.TenantId;
                    session.DeviceId = device.Id;
                    _activeSessions[imei] = session;
                    _logger.LogInformation(
                        "Dispositivo autenticado por TCP: IMEI {Imei} (tenant {TenantId})", imei, device.TenantId);
                }

                // ACK de login (requerido en 5s) SÓLO para dispositivos registrados.
                byte[] loginAck = R12LFFrame.BuildAck(R12LFFrame.ProtocolLogin, frame.SerialNumber);
                await session.WriteLock.WaitAsync(cancellationToken);
                try
                {
                    await session.Stream.WriteAsync(loginAck, cancellationToken);
                    await session.Stream.FlushAsync(cancellationToken);
                }
                finally
                {
                    session.WriteLock.Release();
                }
                break;

            case R12LFFrame.ProtocolStatus:
                // Heartbeat status ACK
                byte[] statusAck = R12LFFrame.BuildAck(R12LFFrame.ProtocolStatus, frame.SerialNumber);
                await session.WriteLock.WaitAsync(cancellationToken);
                try
                {
                    await session.Stream.WriteAsync(statusAck, cancellationToken);
                    await session.Stream.FlushAsync(cancellationToken);
                }
                finally
                {
                    session.WriteLock.Release();
                }
                break;

            case R12LFFrame.ProtocolLocation:
            case R12LFFrame.ProtocolAlarm:
                bool isAlarm = frame.ProtocolNumber == R12LFFrame.ProtocolAlarm;

                // El paquete de alarma (0x16) EXIGE respuesta del servidor con el mismo
                // número de protocolo (spec 5.4.2). Se responde antes de ingerir para no
                // exceder la ventana de 5s aunque la ingesta tarde.
                if (isAlarm)
                {
                    byte[] alarmAck = R12LFFrame.BuildAck(R12LFFrame.ProtocolAlarm, frame.SerialNumber);
                    await session.WriteLock.WaitAsync(cancellationToken);
                    try
                    {
                        await session.Stream.WriteAsync(alarmAck, cancellationToken);
                        await session.Stream.FlushAsync(cancellationToken);
                    }
                    finally
                    {
                        session.WriteLock.Release();
                    }
                }

                var (position, recordedAt) = frame.DecodeLocationPacket();
                if (position != null && !string.IsNullOrWhiteSpace(session.Imei))
                {
                    // Construct canonical JSON payload for unified ingestion pipeline
                    var payloadObj = new
                    {
                        imei = session.Imei,
                        latitude = position.Latitude,
                        longitude = position.Longitude,
                        speed = position.SpeedKph,
                        course = position.Heading,
                        satellites = position.Satellites,
                        isValid = position.IsValid,
                        timestamp = recordedAt.ToString("o"),
                        source = "r12l-f",
                        is_alarm = isAlarm,
                        protocol_number = $"0x{frame.ProtocolNumber:X2}",
                        serial_number = frame.SerialNumber
                    };

                    string jsonPayload = JsonSerializer.Serialize(payloadObj);
                    string topic = $"devices/{session.Imei}/telemetry";

                    using var scope = _scopeFactory.CreateScope();
                    var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                    var result = await mediator.Send(new IngestTelemetryCommand(topic, jsonPayload, Source: "r12l-f"), cancellationToken);
                    if (result.IsFailure)
                    {
                        _logger.LogWarning("Failed to ingest TCP location telemetry for IMEI {Imei}: {Error}", session.Imei, result.Error.Message);
                    }
                    else
                    {
                        _logger.LogTrace("Ingested TCP {Kind} telemetry for IMEI {Imei} at {Timestamp}", isAlarm ? "alarm" : "location", session.Imei, recordedAt);
                    }
                }
                break;

            default:
                _logger.LogDebug("Received unhandled protocol frame 0x{Protocol:X2} from IMEI {Imei}", frame.ProtocolNumber, session.Imei);
                break;
        }
    }

    private static bool TryExtractNextFrame(List<byte> buffer, out R12LFFrame? frame)
    {
        frame = null;
        if (buffer.Count < 10) return false;

        byte[] raw = buffer.ToArray();
        ReadOnlySpan<byte> span = raw;
        if (!R12LFFrame.TryParse(span, out frame, out int consumedBytes))
        {
            if (consumedBytes > 0 && consumedBytes <= buffer.Count)
            {
                buffer.RemoveRange(0, consumedBytes);
            }
            return false;
        }

        // En éxito, TryParse devuelve consumedBytes = ruido inicial + la trama completa
        // (contrato cubierto por los tests). Consumir exactamente eso; recalcular con
        // PacketLength duplicaba el tamaño de la trama y descartaba paquetes consecutivos.
        int bytesToRemove = Math.Min(consumedBytes, buffer.Count);
        buffer.RemoveRange(0, bytesToRemove);
        return true;
    }

    private static byte[] BuildServerCommandFrame(string commandText, ushort serialNumber)
    {
        byte[] contentBytes = Encoding.ASCII.GetBytes(commandText);

        // Server Command 0x80 structure:
        // Length = 1 (server flag len) + 4 (server flag) + CommandLen + 2 (language: 0x0002 English) + 2 (serial) + 2 (crc) = 11 + CommandLen
        byte packetLength = (byte)(11 + contentBytes.Length);

        Span<byte> toHash = stackalloc byte[1 + 1 + 1 + 4 + contentBytes.Length + 2 + 2];
        toHash[0] = packetLength;
        toHash[1] = R12LFFrame.ProtocolServerCommand;
        toHash[2] = 0x04; // Server flag length
        toHash[3] = 0x00; // Server flag (4 bytes)
        toHash[4] = 0x00;
        toHash[5] = 0x00;
        toHash[6] = 0x01;

        contentBytes.CopyTo(toHash.Slice(7, contentBytes.Length));

        int offset = 7 + contentBytes.Length;
        toHash[offset] = 0x00; // Language: 0x0002 = English
        toHash[offset + 1] = 0x02;

        BinaryPrimitives.WriteUInt16BigEndian(toHash.Slice(offset + 2, 2), serialNumber);

        ushort crc = R12LFCrc16.Compute(toHash);

        byte[] frame = new byte[5 + packetLength];
        frame[0] = 0x78;
        frame[1] = 0x78;
        frame[2] = packetLength;
        frame[3] = R12LFFrame.ProtocolServerCommand;
        frame[4] = 0x04;
        frame[5] = 0x00;
        frame[6] = 0x00;
        frame[7] = 0x00;
        frame[8] = 0x01;

        Array.Copy(contentBytes, 0, frame, 9, contentBytes.Length);

        int frameOffset = 9 + contentBytes.Length;
        frame[frameOffset] = 0x00;
        frame[frameOffset + 1] = 0x02;

        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(frameOffset + 2, 2), serialNumber);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(frameOffset + 4, 2), crc);

        frame[frameOffset + 6] = 0x0D;
        frame[frameOffset + 7] = 0x0A;

        return frame;
    }

    private sealed class TcpDeviceSession : IDisposable
    {
        public TcpClient Client { get; init; } = null!;
        public NetworkStream Stream { get; init; } = null!;
        public string Imei { get; set; } = string.Empty;
        public Guid TenantId { get; set; }
        public Guid DeviceId { get; set; }
        public bool Rejected { get; set; }
        public DateTime ConnectedAtUtc { get; init; }
        public DateTime LastActivityUtc { get; set; }
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
