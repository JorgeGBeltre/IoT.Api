using IoT.Domain.Enums;
using IoT.Protocols.Contracts;

namespace IoT.Protocols.Profiles;

/// <summary>
/// Perfil declarativo para rastreadores satelitales GPS / IoT de diversos fabricantes
/// (SEEWORLD, Queclink, Concox, Coban, iStartek, Jointech, Benway, X3Tech, Mictrack).
/// Asocia el fabricante, modelo, transporte primario y tópicos con su respectivo adaptador.
/// </summary>
public sealed class GenericTrackerProfile : IDeviceProfile
{
    public string ProfileId { get; }
    public string Manufacturer { get; }
    public string Model { get; }
    public string DisplayName { get; }
    public string PrimaryTransport { get; }
    public IReadOnlyList<CapabilityType> SupportedCapabilities { get; }
    public IReadOnlyList<string> TelemetryTopicPatterns { get; }
    public string CommandTopicPattern { get; }
    public IDeviceProtocolAdapter Adapter { get; }

    public GenericTrackerProfile(
        string profileId,
        string manufacturer,
        string model,
        string primaryTransport,
        IDeviceProtocolAdapter adapter,
        IReadOnlyList<CapabilityType>? capabilities = null,
        IReadOnlyList<string>? topicPatterns = null)
    {
        ProfileId = profileId;
        Manufacturer = manufacturer;
        Model = model;
        // Nombre estrictamente formateado como <Fabricante> <Modelo>
        DisplayName = $"{manufacturer} {model}";
        PrimaryTransport = primaryTransport;
        Adapter = adapter;

        SupportedCapabilities = capabilities ??
        [
            CapabilityType.Gps,
            CapabilityType.Health
        ];

        string lowerModel = model.ToLowerInvariant().Replace(" ", "-");
        string lowerMfg = manufacturer.ToLowerInvariant().Replace(" ", "-");

        TelemetryTopicPatterns = topicPatterns ??
        [
            $"tcp/{lowerModel}/{{deviceId}}",
            $"{lowerMfg}/{lowerModel}/{{deviceId}}/telemetry",
            $"{lowerMfg}/{{deviceId}}/telemetry",
            $"devices/{{deviceId}}/{lowerModel}",
            $"devices/{{deviceId}}/gps"
        ];

        CommandTopicPattern = $"devices/{{deviceId}}/commands";
    }

    public bool SupportsTransport(string transportName)
    {
        if (string.Equals(transportName, PrimaryTransport, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(transportName, "TCP", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(transportName, "UDP", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(transportName, "MQTT", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return PrimaryTransport.Contains(transportName, StringComparison.OrdinalIgnoreCase);
    }
}
