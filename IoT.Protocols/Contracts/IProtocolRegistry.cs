namespace IoT.Protocols.Contracts;

public interface IProtocolRegistry
{
    void RegisterProfile(IDeviceProfile profile);
    IDeviceProfile? FindProfileForTopic(string topic);
    IDeviceProfile? GetProfile(string profileId);
    IReadOnlyList<IDeviceProfile> GetAllProfiles();

    // Connector & Converter management (TB-1 / ThingsBoard Integration architecture)
    void RegisterConnector(IProtocolConnector connector);
    IProtocolConnector? GetConnector(string connectorName);
    IReadOnlyList<IProtocolConnector> GetAllConnectors();
    IProtocolConnector? FindConnector(TransportMessage message);
}
