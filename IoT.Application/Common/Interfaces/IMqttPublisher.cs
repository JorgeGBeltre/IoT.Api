namespace IoT.Application.Common.Interfaces;

public sealed record MqttPublishOptions
{
    public int Qos { get; init; } = 1;
    public bool Retain { get; init; } = false;
    public uint? MessageExpiryInterval { get; init; }
    public string? ContentType { get; init; } = "application/json";
    public string? ResponseTopic { get; init; }
    public byte[]? CorrelationData { get; init; }
    public Dictionary<string, string>? UserProperties { get; init; }
}

public interface IMqttPublisher
{
    Task PublishAsync(
        string topic,
        string payload,
        int qos = 1,
        bool retain = false,
        CancellationToken cancellationToken = default);

    Task PublishAsync(
        string topic,
        string payload,
        MqttPublishOptions options,
        CancellationToken cancellationToken = default);
}
