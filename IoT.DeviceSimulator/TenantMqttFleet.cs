using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace IoT.DeviceSimulator;

/// <summary>
/// Conjunto de conexiones MQTT del simulador, UNA POR TENANT. Antes el simulador abría una
/// sola conexión con una ApiKey global y publicaba telemetría de equipos de VARIOS tenants
/// por ella; esa clave global, acotada a ningún tenant, era justo el agujero de MEDIUM-2.
/// Ahora cada conexión usa la credencial de BOOTSTRAP de su tenant (que el broker resuelve
/// contra la entidad Tenant y acota a ese tenant): una conexión no puede publicar como
/// equipos de otro tenant, porque la ingesta rechaza toda trama cruzada.
/// </summary>
public sealed class TenantMqttFleet : IAsyncDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<TenantMqttFleet> _logger;
    private readonly ConcurrentDictionary<string, IMqttClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _apiKeyByTenant = new(StringComparer.OrdinalIgnoreCase);

    public TenantMqttFleet(IConfiguration configuration, ILogger<TenantMqttFleet> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Tenants para los que hay credencial configurada y conexión abierta.</summary>
    public IReadOnlyCollection<string> ConnectedTenants => _clients.Keys.ToArray();

    public async Task ConnectAllAsync(CancellationToken cancellationToken)
    {
        string host = _configuration["Mqtt:Host"] ?? "localhost";
        int port = int.TryParse(_configuration["Mqtt:Port"], out var p) ? p : 1883;

        var credentials = _configuration
            .GetSection("MqttBroker:BootstrapCredentials")
            .Get<List<BootstrapCredentialConfig>>() ?? new List<BootstrapCredentialConfig>();

        if (credentials.Count == 0)
        {
            _logger.LogWarning(
                "El simulador no tiene credenciales de bootstrap configuradas (MqttBroker:BootstrapCredentials); " +
                "no publicará nada. El broker exige una credencial por tenant.");
            return;
        }

        var factory = new MqttClientFactory();

        foreach (var cred in credentials)
        {
            if (string.IsNullOrWhiteSpace(cred.TenantCode) || string.IsNullOrWhiteSpace(cred.ApiKey))
            {
                _logger.LogWarning("Credencial de bootstrap del simulador mal configurada; se omite.");
                continue;
            }

            string code = cred.TenantCode.Trim().ToUpperInvariant();
            _apiKeyByTenant[code] = cred.ApiKey;

            var client = factory.CreateMqttClient();
            // ClientId DISTINTO por tenant: el broker mapea el tenant de la sesión por ClientId
            // y dos conexiones con el mismo nombre se expulsarían mutuamente por takeover.
            string clientId = $"IoT-Simulator-{code}";

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(host, port)
                .WithProtocolVersion(MqttProtocolVersion.V500)
                .WithClientId(clientId)
                // La ApiKey de bootstrap va como UserProperty (MQTT 5) y como Username
                // (respaldo 3.1.1). El broker la acepta en cualquiera de los dos campos.
                .WithUserProperty("ApiKey", cred.ApiKey)
                .WithCredentials(cred.ApiKey, string.Empty)
                .WithCleanSession(true)
                .Build();

            try
            {
                var result = await client.ConnectAsync(options, cancellationToken);
                if (result.ResultCode == MqttClientConnectResultCode.Success)
                {
                    _clients[code] = client;
                    _logger.LogInformation("Simulador conectado al broker como tenant {TenantCode} (ClientId {ClientId}).", code, clientId);
                }
                else
                {
                    _logger.LogError(
                        "El simulador NO pudo autenticarse como tenant {TenantCode}: {Reason}. " +
                        "¿Está sembrada su credencial de bootstrap?", code, result.ResultCode);
                    client.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo al conectar el simulador como tenant {TenantCode}.", code);
                client.Dispose();
            }
        }
    }

    /// <summary>
    /// Publica en nombre de un tenant. Si no hay conexión para ese tenant (credencial no
    /// configurada o conexión caída) devuelve false sin lanzar: el simulador sigue con el
    /// resto. No "cae" a otra conexión a propósito: publicar por el tenant equivocado es
    /// justo lo que la plataforma ahora rechaza.
    /// </summary>
    public async Task<bool> PublishAsync(
        string tenantCode,
        string topic,
        string payload,
        IReadOnlyDictionary<string, string>? userProperties,
        CancellationToken cancellationToken)
    {
        string code = tenantCode.Trim().ToUpperInvariant();
        if (!_clients.TryGetValue(code, out var client) || !client.IsConnected)
        {
            _logger.LogWarning(
                "Sin conexión para el tenant {TenantCode}; se omite la publicación en '{Topic}'.", code, topic);
            return false;
        }

        var builder = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

        if (userProperties is not null)
            foreach (var (k, v) in userProperties)
                builder.WithUserProperty(k, v);

        try
        {
            var result = await client.PublishAsync(builder.Build(), cancellationToken);
            // NoMatchingSubscribers es ÉXITO en MQTT 5: el broker aceptó la trama pero no hay
            // suscriptores. Es lo normal aquí, porque el servidor la ingiere por el interceptor
            // de publicación, no suscribiéndose. Sólo lo demás es un rechazo real.
            if (result.ReasonCode is not (MqttClientPublishReasonCode.Success
                or MqttClientPublishReasonCode.NoMatchingSubscribers))
            {
                _logger.LogWarning(
                    "El broker rechazó la publicación del tenant {TenantCode} en '{Topic}': {Reason}.",
                    code, topic, result.ReasonCode);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallo publicando como tenant {TenantCode} en '{Topic}'.", code, topic);
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            try
            {
                if (client.IsConnected)
                    await client.DisconnectAsync();
            }
            catch
            {
                // apagado: nada que hacer.
            }
            client.Dispose();
        }
        _clients.Clear();
    }

    /// <summary>Forma de cada entrada de <c>MqttBroker:BootstrapCredentials</c>.</summary>
    private sealed class BootstrapCredentialConfig
    {
        public string? TenantCode { get; set; }
        public string? ApiKey { get; set; }
    }
}
