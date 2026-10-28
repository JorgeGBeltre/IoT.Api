using IoT.Domain.Enums;
using IoT.Protocols.Contracts;
using IoT.Protocols.Dynamic;

namespace IoT.Protocols.Profiles;

/// <summary>
/// Perfil de dispositivo extensible definido en runtime mediante scripts JavaScript sandboxed (Jint).
/// Permite añadir o customizar compatibilidad con dispositivos de terceros sin recompilar el backend.
/// </summary>
public sealed class ScriptableDeviceProfile : IDeviceProfile
{
    public string ProfileId { get; }
    public string Manufacturer { get; }
    public string Model { get; }
    public string DisplayName => $"{Manufacturer} {Model} (Dynamic Jint)";
    public string PrimaryTransport { get; }
    public string? TcpFamily => null;
    public IReadOnlyList<CapabilityType> SupportedCapabilities { get; }
    public IReadOnlyList<string> TelemetryTopicPatterns { get; }
    public string CommandTopicPattern { get; }
    public IDeviceProtocolAdapter Adapter { get; }

    public ScriptableDeviceProfile(
        string profileId,
        string manufacturer,
        string model,
        string primaryTransport,
        IReadOnlyList<string> telemetryTopicPatterns,
        string commandTopicPattern,
        string uplinkScript,
        string? downlinkScript = null,
        IReadOnlyList<CapabilityType>? capabilities = null)
    {
        ProfileId = profileId ?? throw new ArgumentNullException(nameof(profileId));
        Manufacturer = manufacturer ?? throw new ArgumentNullException(nameof(manufacturer));
        Model = model ?? throw new ArgumentNullException(nameof(model));
        PrimaryTransport = primaryTransport ?? throw new ArgumentNullException(nameof(primaryTransport));
        TelemetryTopicPatterns = telemetryTopicPatterns ?? throw new ArgumentNullException(nameof(telemetryTopicPatterns));
        CommandTopicPattern = commandTopicPattern ?? throw new ArgumentNullException(nameof(commandTopicPattern));
        SupportedCapabilities = capabilities ?? [CapabilityType.LevelTelemetry];
        Adapter = new ScriptableProtocolAdapter(profileId, uplinkScript, downlinkScript);
    }

    public bool SupportsTransport(string transportName)
    {
        return string.Equals(PrimaryTransport, transportName, StringComparison.OrdinalIgnoreCase);
    }
}
