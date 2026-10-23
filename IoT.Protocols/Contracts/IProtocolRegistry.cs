namespace IoT.Protocols.Contracts;

public interface IProtocolRegistry
{
    void RegisterProfile(IDeviceProfile profile);
    IDeviceProfile? FindProfileForTopic(string topic);
    IDeviceProfile? GetProfile(string profileId);
    IReadOnlyList<IDeviceProfile> GetAllProfiles();
}
