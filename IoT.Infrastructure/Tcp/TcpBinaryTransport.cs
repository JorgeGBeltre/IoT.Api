using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using IoT.Domain.Common;
using IoT.Protocols.Binary;
using IoT.Protocols.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IoT.Infrastructure.Tcp;

public sealed class TcpBinaryTransport : IDeviceTransport, IAsyncDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<TcpBinaryTransport> _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenerLoopTask;

    // Active client sessions: IMEI -> ClientSession
    private readonly ConcurrentDictionary<string, ClientSession> _sessionsByImei = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, ClientSession> _sessionsById = new();

    public string TransportName => "TCP-R12LF";
    public bool IsConnected => _listener != null && _listener.Server.IsBound;
    public Func<TransportMessage, Task<Result>>? OnMessageReceived { get; set; }

    public TcpBinaryTransport(IConfiguration configuration, ILogger<TcpBinaryTransport> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        int port = int.TryParse(_configuration["Tcp:Port"] ?? _configuration["TCP_PORT"], out var p) ? p : 5023;
        bool enabled = bool.TryParse(_configuration["Tcp:Enabled"], out var e) ? e : true;

        if (!enabled)
        {
            _logger.LogInformation("TcpBinaryTransport is disabled in configuration.");
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();

        _logger.LogInformation("TcpBinaryTransport (R12L-F) listening on TCP port {Port}...", port);
        _listenerLoopTask = Task.Run(() => AcceptClientsLoopAsync(_cts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Stopping TcpBinaryTransport...");
        _cts?.Cancel();

        try
        {
            _listener?.Stop();
        }
        catch { }

        foreach (var session in _sessionsById.Values)
        {
            session.Dispose();
        }
        _sessionsById.Clear();
        _sessionsByImei.Clear();

        if (_listenerLoopTask != null)
        {
            try
            {
                await _listenerLoopTask;
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        _logger.LogInformation("TcpBinaryTransport stopped.");
    }

    public async Task<Result> PublishCommandAsync(
        string targetTopicOrRoute,
        string payload,
        DeviceTransportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string imei = ExtractImeiFromRoute(targetTopicOrRoute);

        if (!_sessionsByImei.TryGetValue(imei, out var session) || !session.IsConnected)
        {
            return Result.Failure(new Error("TcpTransport.DeviceOffline",
                $"Device with IMEI '{imei}' is not connected via TCP session."));
        }

        try
        {
            byte[] commandBytes = Encoding.UTF8.GetBytes(payload);
            await session.SendRawBytesAsync(commandBytes, cancellationToken);
            _logger.LogInformation("Dispatched command to TCP device {Imei}", imei);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send TCP command to device {Imei}", imei);
            return Result.Failure(new Error("TcpTransport.SendFailed", ex.Message));
        }
    }

    private async Task AcceptClientsLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                var session = new ClientSession(client, _logger);
                _sessionsById[session.SessionId] = session;

                _ = Task.Run(() => HandleClientAsync(session, stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Error accepting TCP client connection.");
                }
            }
        }
    }

    private async Task HandleClientAsync(ClientSession session, CancellationToken stoppingToken)
    {
        _logger.LogInformation("New TCP client connected from {RemoteEndPoint} (Session: {SessionId})",
            session.RemoteEndPoint, session.SessionId);

        var stream = session.Client.GetStream();
        byte[] readBuffer = new byte[2048];
        var incomingStream = new MemoryStream();

        try
        {
            while (!stoppingToken.IsCancellationRequested && session.IsConnected)
            {
                int bytesRead = await stream.ReadAsync(readBuffer, stoppingToken);
                if (bytesRead == 0)
                {
                    break; // Remote disconnected cleanly
                }

                session.Touch();
                incomingStream.Write(readBuffer, 0, bytesRead);

                // Attempt to parse frames from the accumulated stream
                byte[] accumulated = incomingStream.ToArray();
                var (frames, consumed) = ParseFrames(accumulated);

                foreach (var frame in frames)
                {
                    await ProcessParsedFrameAsync(session, frame, stoppingToken);
                }

                // Compact unparsed residual bytes
                if (consumed > 0)
                {
                    int remaining = accumulated.Length - consumed;
                    incomingStream.SetLength(0);
                    if (remaining > 0)
                    {
                        incomingStream.Write(accumulated, consumed, remaining);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug("TCP Client session {SessionId} disconnected: {Message}", session.SessionId, ex.Message);
        }
        finally
        {
            _sessionsById.TryRemove(session.SessionId, out _);
            if (!string.IsNullOrEmpty(session.Imei))
            {
                _sessionsByImei.TryRemove(session.Imei, out _);
            }
            session.Dispose();
            _logger.LogInformation("TCP Client session {SessionId} closed.", session.SessionId);
        }
    }

    private async Task ProcessParsedFrameAsync(ClientSession session, R12LFFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.ProtocolNumber)
        {
            case R12LFFrame.ProtocolLogin:
                string imei = frame.ExtractImeiFromLoginPacket();
                if (!string.IsNullOrWhiteSpace(imei))
                {
                    session.Imei = imei;
                    _sessionsByImei[imei] = session;
                    _logger.LogInformation("TCP Login received from IMEI: {Imei} (Serial: {Serial})", imei, frame.SerialNumber);
                }

                // Immediately send ACK within 5 seconds requirement
                byte[] loginAck = R12LFFrame.BuildAck(R12LFFrame.ProtocolLogin, frame.SerialNumber);
                await session.SendRawBytesAsync(loginAck, cancellationToken);
                break;

            case R12LFFrame.ProtocolStatus:
                _logger.LogDebug("TCP Heartbeat received from IMEI: {Imei} (Serial: {Serial})", session.Imei, frame.SerialNumber);

                // Immediately send Heartbeat ACK
                byte[] statusAck = R12LFFrame.BuildAck(R12LFFrame.ProtocolStatus, frame.SerialNumber);
                await session.SendRawBytesAsync(statusAck, cancellationToken);
                break;

            case R12LFFrame.ProtocolLocation:
            case R12LFFrame.ProtocolAlarm:
                var (pos, recordedAt) = frame.DecodeLocationPacket();
                if (pos != null && !string.IsNullOrWhiteSpace(session.Imei) && OnMessageReceived != null)
                {
                    var payloadObj = new
                    {
                        Device = new { ChipId = session.Imei },
                        Timestamp = recordedAt.ToString("O"),
                        Details = new
                        {
                            accident = new
                            {
                                gps_data = new
                                {
                                    latitude = pos.Latitude,
                                    longitude = pos.Longitude,
                                    speed = pos.SpeedKph,
                                    heading = pos.Heading,
                                    is_valid = pos.IsValid
                                }
                            }
                        }
                    };

                    string json = JsonSerializer.Serialize(payloadObj);
                    var message = TransportMessage.Create(
                        topicOrRoute: $"devices/{session.Imei}/gps",
                        payload: json,
                        messageId: $"r12l-{frame.SerialNumber}-{recordedAt.Ticks}",
                        transportProtocol: "TCP-R12LF",
                        source: "r12l-f",
                        receivedAt: DateTime.UtcNow);

                    await OnMessageReceived.Invoke(message);
                }
                break;
        }
    }

    private static string ExtractImeiFromRoute(string route)
    {
        if (route.Contains('/'))
        {
            var parts = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Equals("devices", StringComparison.OrdinalIgnoreCase))
            {
                return parts[1];
            }
            return parts[^1];
        }
        return route;
    }

    private static (List<R12LFFrame> Frames, int ConsumedBytes) ParseFrames(byte[] buffer)
    {
        var frames = new List<R12LFFrame>();
        int offset = 0;

        while (offset < buffer.Length)
        {
            var slice = buffer.AsSpan(offset);
            if (R12LFFrame.TryParse(slice, out var frame, out int consumed))
            {
                offset += consumed;
                if (frame != null)
                {
                    frames.Add(frame);
                }
            }
            else
            {
                offset += consumed;
                break;
            }
        }

        return (frames, offset);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _cts?.Dispose();
    }

    private sealed class ClientSession : IDisposable
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public TcpClient Client { get; }
        public string? Imei { get; set; }
        public DateTime LastActiveUtc { get; private set; } = DateTime.UtcNow;
        public EndPoint? RemoteEndPoint => Client.Client.RemoteEndPoint;
        public bool IsConnected => Client.Connected;

        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ILogger _logger;

        public ClientSession(TcpClient client, ILogger logger)
        {
            Client = client;
            _logger = logger;
        }

        public void Touch() => LastActiveUtc = DateTime.UtcNow;

        public async Task SendRawBytesAsync(byte[] bytes, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await Client.GetStream().WriteAsync(bytes, cancellationToken);
                await Client.GetStream().FlushAsync(cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            try
            {
                Client.Close();
                Client.Dispose();
            }
            catch { }
            _sendLock.Dispose();
        }
    }
}
