using IoT.Protocols.Adapters;
using IoT.Protocols.Contracts;
using IoT.Protocols.Routing;

namespace IoT.Protocols.Profiles;

public sealed class ProtocolRegistry : IProtocolRegistry
{
    private readonly Dictionary<string, IDeviceProfile> _profilesById = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDeviceProfile> _profiles = [];
    private readonly Dictionary<string, IProtocolConnector> _connectorsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IProtocolConnector> _connectors = [];

    public ProtocolRegistry()
    {
    }

    public ProtocolRegistry(IEnumerable<IDeviceProfile> initialProfiles, IEnumerable<IProtocolConnector>? initialConnectors = null)
    {
        foreach (var profile in initialProfiles)
        {
            RegisterProfile(profile);
        }

        if (initialConnectors != null)
        {
            foreach (var connector in initialConnectors)
            {
                RegisterConnector(connector);
            }
        }
    }

    public static ProtocolRegistry CreateDefault()
    {
        var registry = new ProtocolRegistry(
        [
            // Perfiles nativos iniciales
            new GlpPressureProfile(),
            new GpsTrackerProfile(),
            new RelaySwitchProfile(),
            new TicketValidatorProfile(),
            new MqttV5JsonProfile(),
            new AccessControlProfile(),
            new R12LFDeviceProfile(),

            // SEEWORLD
            new GenericTrackerProfile("seeworld-r12l-f", "SEEWORLD", "R12L-F", "TCP (GT06)", new Gt06ProtocolAdapter("SEEWORLD", "R12L-F"), "GT06"),
            new GenericTrackerProfile("seeworld-r11", "SEEWORLD", "R11", "TCP (GT06)", new Gt06ProtocolAdapter("SEEWORLD", "R11"), "GT06"),

            // Queclink
            new GenericTrackerProfile("queclink-gv50cg", "Queclink", "GV50CG", "TCP/UDP (@Track)", new QueclinkTrackProtocolAdapter("GV50CG"), "Queclink"),
            new GenericTrackerProfile("queclink-gv55", "Queclink", "GV55", "TCP/UDP (@Track)", new QueclinkTrackProtocolAdapter("GV55"), "Queclink"),
            new GenericTrackerProfile("queclink-gv55w", "Queclink", "GV55W", "TCP/UDP (@Track)", new QueclinkTrackProtocolAdapter("GV55W"), "Queclink"),
            new GenericTrackerProfile("queclink-gv300", "Queclink", "GV300", "TCP/UDP (@Track)", new QueclinkTrackProtocolAdapter("GV300"), "Queclink"),
            new GenericTrackerProfile("queclink-gv300w", "Queclink", "GV300W", "TCP/UDP (@Track)", new QueclinkTrackProtocolAdapter("GV300W"), "Queclink"),
            new GenericTrackerProfile("queclink-gl300", "Queclink", "GL300", "SMS/@Track", new QueclinkTrackProtocolAdapter("GL300"), null),
            new GenericTrackerProfile("queclink-garmin-fmi", "Queclink", "Garmin-FMI", "TCP/UDP (@Track)", new QueclinkTrackProtocolAdapter("Garmin-FMI"), "Queclink"),

            // Concox
            new GenericTrackerProfile("concox-gt06n", "Concox", "GT06N", "TCP (GT06)", new Gt06ProtocolAdapter("Concox", "GT06N"), "GT06"),
            new GenericTrackerProfile("concox-tr06", "Concox", "TR06", "TCP (GT06)", new Gt06ProtocolAdapter("Concox", "TR06"), "GT06"),

            // Coban
            new GenericTrackerProfile("coban-tk103", "Coban", "TK103", "TCP (Coban)", new CobanProtocolAdapter("TK103"), "Coban"),
            new GenericTrackerProfile("coban-tk303g", "Coban", "TK303G", "TCP (Coban)", new CobanProtocolAdapter("TK303G"), "Coban"),

            // iStartek
            new GenericTrackerProfile("istartek-vt100", "iStartek", "VT100", "TCP/UDP (iStartek)", new IStartekProtocolAdapter("VT100"), "iStartek"),
            new GenericTrackerProfile("istartek-vt100-l", "iStartek", "VT100-L", "TCP/UDP (iStartek)", new IStartekProtocolAdapter("VT100-L"), "iStartek"),
            new GenericTrackerProfile("istartek-vt200", "iStartek", "VT200", "TCP/UDP (iStartek)", new IStartekProtocolAdapter("VT200"), "iStartek"),
            new GenericTrackerProfile("istartek-vt200-l", "iStartek", "VT200-L", "TCP/UDP (iStartek)", new IStartekProtocolAdapter("VT200-L"), "iStartek"),
            new GenericTrackerProfile("istartek-vt900", "iStartek", "VT900", "TCP/UDP (iStartek)", new IStartekProtocolAdapter("VT900"), "iStartek"),
            new GenericTrackerProfile("istartek-pt60", "iStartek", "PT60", "TCP/UDP (iStartek)", new IStartekProtocolAdapter("PT60"), "iStartek"),

            // Jointech
            new GenericTrackerProfile("jointech-jt701d", "Jointech", "JT701D", "TCP (JT701)", new JointechProtocolAdapter("JT701D"), "Jointech"),

            // Benway
            new GenericTrackerProfile("benway-bw02", "Benway", "BW02", "TCP (Benway)", new Gt06ProtocolAdapter("Benway", "BW02"), "GT06"),
            new GenericTrackerProfile("benway-bw08", "Benway", "BW08", "TCP (Benway)", new Gt06ProtocolAdapter("Benway", "BW08"), "GT06"),
            new GenericTrackerProfile("benway-bw09", "Benway", "BW09", "TCP (Benway)", new Gt06ProtocolAdapter("Benway", "BW09"), "GT06"),
            new GenericTrackerProfile("benway-et300", "Benway", "ET300", "TCP (Benway)", new Gt06ProtocolAdapter("Benway", "ET300"), "GT06"),

            // X3Tech
            new GenericTrackerProfile("x3tech-nt20", "X3Tech", "NT20", "TCP (NT20)", new Gt06ProtocolAdapter("X3Tech", "NT20"), "GT06"),

            // Mictrack
            new GenericTrackerProfile("mictrack-m558g-v2", "Mictrack", "M558G-V2", "TCP (GT06)", new Gt06ProtocolAdapter("Mictrack", "M558G-V2"), "Mictrack")
        ]);

        // Registrar Conectores nativos iniciales (TB-1)
        registry.RegisterConnector(new ProtocolConnector("MQTT-DEFAULT", "MQTT", new MqttV5JsonAdapter()));
        registry.RegisterConnector(new ProtocolConnector("TCP-GT06", "TCP", new Gt06ProtocolAdapter("SEEWORLD", "R12L-F")));
        registry.RegisterConnector(new ProtocolConnector("TCP-QUECLINK", "TCP", new QueclinkTrackProtocolAdapter("GV300")));
        registry.RegisterConnector(new ProtocolConnector("TCP-COBAN", "TCP", new CobanProtocolAdapter("TK103")));
        registry.RegisterConnector(new ProtocolConnector("TCP-ISTARTEK", "TCP", new IStartekProtocolAdapter("VT900")));
        registry.RegisterConnector(new ProtocolConnector("TCP-JOINTECH", "TCP", new JointechProtocolAdapter("JT701D")));
        registry.RegisterConnector(new ProtocolConnector("HTTP-WEBHOOK", "HTTP", new StrategyBasedProtocolAdapter()));

        return registry;
    }

    public void RegisterProfile(IDeviceProfile profile)
    {
        _profilesById[profile.ProfileId] = profile;
        if (!_profiles.Contains(profile))
        {
            _profiles.Add(profile);
        }
    }

    public IDeviceProfile? GetProfile(string profileId)
    {
        return _profilesById.TryGetValue(profileId, out var profile) ? profile : null;
    }

    public IReadOnlyList<IDeviceProfile> GetAllProfiles()
    {
        return _profiles.AsReadOnly();
    }

    public void RegisterConnector(IProtocolConnector connector)
    {
        _connectorsByName[connector.ConnectorName] = connector;
        if (!_connectors.Contains(connector))
        {
            _connectors.Add(connector);
        }
    }

    public IProtocolConnector? GetConnector(string connectorName)
    {
        return _connectorsByName.TryGetValue(connectorName, out var connector) ? connector : null;
    }

    public IReadOnlyList<IProtocolConnector> GetAllConnectors()
    {
        return _connectors.AsReadOnly();
    }

    public IProtocolConnector? FindConnector(TransportMessage message)
    {
        // 1. Por cabecera explícita 'connector'
        if (message.Headers.TryGetValue("connector", out var connName) && !string.IsNullOrWhiteSpace(connName))
        {
            if (_connectorsByName.TryGetValue(connName, out var byName))
                return byName;
        }

        // 2. Por cabecera explícita 'converter'
        if (message.Headers.TryGetValue("converter", out var convId) && !string.IsNullOrWhiteSpace(convId))
        {
            foreach (var conn in _connectors)
            {
                if (string.Equals(conn.UplinkConverter.ConverterId, convId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(conn.ConnectorName, convId, StringComparison.OrdinalIgnoreCase))
                {
                    return conn;
                }
            }
        }

        // 3. Por concordancia de transporte y CanHandle del convertidor
        if (!string.IsNullOrWhiteSpace(message.TransportProtocol))
        {
            foreach (var conn in _connectors)
            {
                if (string.Equals(conn.TransportType, message.TransportProtocol, StringComparison.OrdinalIgnoreCase) &&
                    conn.UplinkConverter.CanHandle(message))
                {
                    return conn;
                }
            }
        }

        return null;
    }

    public IDeviceProfile? FindProfileForTopic(string topic)
    {
        foreach (var profile in _profiles)
        {
            foreach (var pattern in profile.TelemetryTopicPatterns)
            {
                // Normalize {placeholder} to MQTT single level wildcard '+'
                string mqttPattern = NormalizePatternToMqtt(pattern);
                if (TopicPatternMatcher.MatchesMqttTopic(mqttPattern, topic))
                {
                    return profile;
                }
            }
        }

        return null;
    }

    private static string NormalizePatternToMqtt(string pattern)
    {
        if (!pattern.Contains('{')) return pattern;

        var segments = pattern.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].StartsWith('{') && segments[i].EndsWith('}'))
            {
                segments[i] = "+";
            }
        }
        return string.Join('/', segments);
    }
}
