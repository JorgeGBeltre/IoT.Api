using IoT.Protocols.Contracts;

namespace IoT.Protocols.Routing;

public interface ITopicRoutingService
{
    string BuildCommandTopic(string? profileIdOrModel, string chipId, Guid? tenantId = null);
    string BuildAttributeTopic(string? profileIdOrModel, string chipId, Guid? tenantId = null);
    IReadOnlyList<string> GetAllTelemetrySubscriptionPatterns();
    TimeSpan GetDefaultCommandExpiry(string? profileIdOrModel = null);
}

public sealed class TopicRoutingService : ITopicRoutingService
{
    private readonly IProtocolRegistry _registry;

    public TopicRoutingService(IProtocolRegistry registry)
    {
        _registry = registry;
    }

    public string BuildCommandTopic(string? profileIdOrModel, string chipId, Guid? tenantId = null)
    {
        IDeviceProfile? profile = null;

        if (!string.IsNullOrWhiteSpace(profileIdOrModel))
        {
            profile = _registry.GetProfile(profileIdOrModel) ??
                      _registry.GetAllProfiles().FirstOrDefault(p =>
                          string.Equals(p.Model, profileIdOrModel, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(p.ProfileId, profileIdOrModel, StringComparison.OrdinalIgnoreCase));
        }

        string pattern = profile?.CommandTopicPattern ?? "devices/{deviceId}/commands";

        return pattern
            .Replace("{deviceId}", chipId, StringComparison.OrdinalIgnoreCase)
            .Replace("{tenantId}", tenantId?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    public string BuildAttributeTopic(string? profileIdOrModel, string chipId, Guid? tenantId = null)
    {
        IDeviceProfile? profile = null;

        if (!string.IsNullOrWhiteSpace(profileIdOrModel))
        {
            profile = _registry.GetProfile(profileIdOrModel) ??
                      _registry.GetAllProfiles().FirstOrDefault(p =>
                          string.Equals(p.Model, profileIdOrModel, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(p.ProfileId, profileIdOrModel, StringComparison.OrdinalIgnoreCase));
        }

        string pattern = profile?.AttributeTopicPattern ?? "devices/{deviceId}/attributes";

        return pattern
            .Replace("{deviceId}", chipId, StringComparison.OrdinalIgnoreCase)
            .Replace("{tenantId}", tenantId?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> GetAllTelemetrySubscriptionPatterns()
    {
        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in _registry.GetAllProfiles())
        {
            foreach (var topicPattern in profile.TelemetryTopicPatterns)
            {
                // Convert route placeholders like {deviceId} to MQTT single-level wildcard '+'
                string mqttTopic = topicPattern
                    .Replace("{deviceId}", "+", StringComparison.OrdinalIgnoreCase)
                    .Replace("{tenantId}", "+", StringComparison.OrdinalIgnoreCase);

                patterns.Add(mqttTopic);
            }
        }

        // Ensure baseline fallback telemetry roots exist if no profiles match
        if (patterns.Count == 0)
        {
            patterns.Add("devices/+/telemetry");
            patterns.Add("telemetry/#");
        }

        return patterns.ToList();
    }

    public TimeSpan GetDefaultCommandExpiry(string? profileIdOrModel = null)
    {
        // 5-minute standard command TTL for MQTT v5 Message Expiry Interval
        return TimeSpan.FromMinutes(5);
    }
}
