using IoT.Domain.Enums;

namespace IoT.Protocols.Contracts;

public interface IDeviceProfile
{
    string ProfileId { get; }
    string Manufacturer { get; }
    string Model { get; }
    string DisplayName { get; }
    string PrimaryTransport { get; }
    IReadOnlyList<CapabilityType> SupportedCapabilities { get; }
    IReadOnlyList<string> TelemetryTopicPatterns { get; }
    string CommandTopicPattern { get; }
    IDeviceProtocolAdapter Adapter { get; }
    bool SupportsTransport(string transportName);
}
