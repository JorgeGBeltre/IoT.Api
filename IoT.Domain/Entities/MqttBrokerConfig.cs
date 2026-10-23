using IoT.Domain.Common;

namespace IoT.Domain.Entities;

/// <summary>
/// Endpoint MQTT configurado por un tenant. La plataforma puede operar contra uno
/// o varios brokers (p.ej. un broker interno Mosquitto para dispositivos propios +
/// un broker externo para integraciones). Perteneciente al tenant propietario.
/// </summary>
public sealed class MqttBrokerConfig : AggregateRoot<Guid>, ITenantScoped
{
    public Guid TenantId { get; private set; }
    public string Name { get; private set; }
    public string Host { get; private set; }
    public int Port { get; private set; }
    public string Protocol { get; private set; }
    public bool TlsEnabled { get; private set; }
    public string AuthMethod { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? LastSeenAt { get; private set; }

    public const string ProtocolV31 = "mqtt-v3.1";
    public const string ProtocolV311 = "mqtt-v3.1.1";
    public const string ProtocolV5 = "mqtt-v5";

    private static readonly HashSet<string> AllowedProtocols = new(StringComparer.OrdinalIgnoreCase)
    {
        ProtocolV31, ProtocolV311, ProtocolV5
    };

    private static readonly HashSet<string> AllowedAuthMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "username-password", "x509-mutual-tls", "api-key-header"
    };

    private MqttBrokerConfig(
        Guid id,
        Guid tenantId,
        string name,
        string host,
        int port,
        string protocol,
        bool tlsEnabled,
        string authMethod,
        bool isActive) : base(id)
    {
        TenantId = tenantId;
        Name = name;
        Host = host;
        Port = port;
        Protocol = protocol;
        TlsEnabled = tlsEnabled;
        AuthMethod = authMethod;
        IsActive = isActive;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public static Result<MqttBrokerConfig> Create(
        Guid tenantId,
        string name,
        string host,
        int port,
        string protocol,
        bool tlsEnabled,
        string authMethod,
        bool isActive = true)
    {
        if (tenantId == Guid.Empty)
            return Result<MqttBrokerConfig>.Failure(new Error("Broker.InvalidTenant", "TenantId no puede estar vacío."));
        if (string.IsNullOrWhiteSpace(name))
            return Result<MqttBrokerConfig>.Failure(new Error("Broker.InvalidName", "El nombre del broker es obligatorio."));
        if (string.IsNullOrWhiteSpace(host))
            return Result<MqttBrokerConfig>.Failure(new Error("Broker.InvalidHost", "El host del broker es obligatorio."));
        if (port <= 0 || port > 65535)
            return Result<MqttBrokerConfig>.Failure(new Error("Broker.InvalidPort", "El puerto debe estar entre 1 y 65535."));
        if (!AllowedProtocols.Contains(protocol))
            return Result<MqttBrokerConfig>.Failure(new Error("Broker.InvalidProtocol", $"Protocolo '{protocol}' no soportado."));
        if (!AllowedAuthMethods.Contains(authMethod))
            return Result<MqttBrokerConfig>.Failure(new Error("Broker.InvalidAuth", $"Método de autenticación '{authMethod}' no soportado."));

        return new MqttBrokerConfig(
            Guid.NewGuid(),
            tenantId,
            name.Trim(),
            host.Trim(),
            port,
            protocol.ToLowerInvariant(),
            tlsEnabled,
            authMethod.ToLowerInvariant(),
            isActive);
    }

    public Result Update(string name, string host, int port, string protocol, bool tlsEnabled, string authMethod, bool isActive)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure(new Error("Broker.InvalidName", "El nombre del broker es obligatorio."));
        if (string.IsNullOrWhiteSpace(host))
            return Result.Failure(new Error("Broker.InvalidHost", "El host del broker es obligatorio."));
        if (port <= 0 || port > 65535)
            return Result.Failure(new Error("Broker.InvalidPort", "El puerto debe estar entre 1 y 65535."));
        if (!AllowedProtocols.Contains(protocol))
            return Result.Failure(new Error("Broker.InvalidProtocol", $"Protocolo '{protocol}' no soportado."));
        if (!AllowedAuthMethods.Contains(authMethod))
            return Result.Failure(new Error("Broker.InvalidAuth", $"Método de autenticación '{authMethod}' no soportado."));

        Name = name.Trim();
        Host = host.Trim();
        Port = port;
        Protocol = protocol.ToLowerInvariant();
        TlsEnabled = tlsEnabled;
        AuthMethod = authMethod.ToLowerInvariant();
        IsActive = isActive;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    public void MarkSeen() => LastSeenAt = DateTime.UtcNow;
    public void Deactivate() { IsActive = false; UpdatedAt = DateTime.UtcNow; }
    public void Activate() { IsActive = true; UpdatedAt = DateTime.UtcNow; }
}
