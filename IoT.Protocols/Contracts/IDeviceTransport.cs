using IoT.Domain.Common;

namespace IoT.Protocols.Contracts;

public interface IDeviceTransport
{
    string TransportName { get; }
    bool IsConnected { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);

    Task<Result> PublishCommandAsync(
        string targetTopicOrRoute,
        string payload,
        DeviceTransportOptions? options = null,
        CancellationToken cancellationToken = default);

    Func<TransportMessage, Task<Result>>? OnMessageReceived { get; set; }
}

public sealed record DeviceTransportOptions(
    int Qos = 1,
    bool Retain = false,
    TimeSpan? MessageExpiry = null,
    string? CorrelationId = null,
    Dictionary<string, string>? UserProperties = null);
