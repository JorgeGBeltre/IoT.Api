using IoT.Protocols.Contracts;

namespace IoT.Protocols.Routing;

public interface ITopicRoutingService
{
    string BuildCommandTopic(string? profileIdOrModel, string chipId, Guid? tenantId = null);

    /// <summary>
    /// Codifica el payload de un comando para el perfil dado, o null si el perfil usa el envelope
    /// genérico de la plataforma. Resuelve el perfil igual que <see cref="BuildCommandTopic"/>.
    /// </summary>
    string? BuildCommandPayload(string? profileIdOrModel, string commandName, string? payloadJson = null);

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
        IDeviceProfile? profile = ResolveProfile(profileIdOrModel);

        string pattern = profile?.CommandTopicPattern ?? "devices/{deviceId}/commands";

        return pattern
            .Replace("{deviceId}", chipId, StringComparison.OrdinalIgnoreCase)
            .Replace("{tenantId}", tenantId?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    public string? BuildCommandPayload(string? profileIdOrModel, string commandName, string? payloadJson = null)
    {
        if (string.IsNullOrWhiteSpace(profileIdOrModel) || string.IsNullOrWhiteSpace(commandName))
            return null;

        var profile = ResolveProfile(profileIdOrModel);

        return profile?.EncodeCommandPayload(commandName, payloadJson);
    }

    public string BuildAttributeTopic(string? profileIdOrModel, string chipId, Guid? tenantId = null)
    {
        IDeviceProfile? profile = ResolveProfile(profileIdOrModel);

        string pattern = profile?.AttributeTopicPattern ?? "devices/{deviceId}/attributes";

        return pattern
            .Replace("{deviceId}", chipId, StringComparison.OrdinalIgnoreCase)
            .Replace("{tenantId}", tenantId?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private IDeviceProfile? ResolveProfile(string? profileIdOrModel)
    {
        if (string.IsNullOrWhiteSpace(profileIdOrModel))
        {
            return null;
        }

        string trimmed = profileIdOrModel.Trim();
        var exact = _registry.GetProfile(trimmed) ??
                    _registry.GetAllProfiles().FirstOrDefault(p =>
                        string.Equals(p.Model, trimmed, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.ProfileId, trimmed, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
        {
            return exact;
        }

        string lower = trimmed.ToLowerInvariant();
        if (lower.Contains("relay", StringComparison.Ordinal) ||
            lower.Contains("switch", StringComparison.Ordinal) ||
            lower.Contains("interruptor", StringComparison.Ordinal) ||
            lower.Contains("psc-", StringComparison.Ordinal) ||
            lower.Contains("vesis", StringComparison.Ordinal) ||
            lower.Contains("minir", StringComparison.Ordinal))
        {
            return _registry.GetProfile("relay-switch");
        }

        if (lower.Contains("validator", StringComparison.Ordinal) ||
            lower.Contains("fligo", StringComparison.Ordinal) ||
            lower.Contains("toour", StringComparison.Ordinal))
        {
            return _registry.GetProfile("ticket-validator");
        }

        if (lower.Contains("glp", StringComparison.Ordinal) ||
            lower.Contains("pressure", StringComparison.Ordinal))
        {
            return _registry.GetProfile("glp-pressure-sensor");
        }

        return null;
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
        var profile = ResolveProfile(profileIdOrModel);
        if (profile is not null && string.Equals(profile.ProfileId, "relay-switch", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromSeconds(15);
        }

        // 5-minute standard command TTL for MQTT v5 Message Expiry Interval
        return TimeSpan.FromMinutes(5);
    }
}
