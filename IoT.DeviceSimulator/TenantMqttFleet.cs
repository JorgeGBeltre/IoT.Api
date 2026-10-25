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
///
/// Cada conexión se RECONECTA SOLA: si el broker se reinicia (p. ej. al reiniciar el API, que
/// lo aloja embebido), la conexión cae y un bucle de reintentos con backoff la vuelve a
/// levantar sin intervención. Antes el simulador conectaba una única vez al arrancar y, tras
/// una caída, se quedaba publicando al vacío ("Sin conexión … se omite") hasta reiniciarlo a
/// mano. Los clientes se crean una vez por tenant configurado y se reutilizan entre reconexiones.
/// </summary>
public sealed class TenantMqttFleet : IAsyncDisposable
{
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);
    private const double MaxReconnectSeconds = 30;

    private readonly IConfiguration _configuration;
    private readonly ILogger<TenantMqttFleet> _logger;
    private readonly MqttClientFactory _factory = new();
    private readonly ConcurrentDictionary<string, IMqttClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MqttClientOptions> _optionsByTenant = new(StringComparer.OrdinalIgnoreCase);
    // Garantiza UN solo bucle de reconexión por tenant aunque DisconnectedAsync dispare varias veces.
    private readonly ConcurrentDictionary<string, byte> _reconnecting = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();

    public TenantMqttFleet(IConfiguration configuration, ILogger<TenantMqttFleet> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Tenants con conexión ABIERTA en este instante (subconjunto de los configurados).</summary>
    public IReadOnlyCollection<string> ConnectedTenants =>
        _clients.Where(kv => kv.Value.IsConnected).Select(kv => kv.Key).ToArray();

    /// <summary>Tenants con credencial de bootstrap configurada (haya o no conexión viva ahora).</summary>
    public int ConfiguredTenantCount => _clients.Count;

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

        foreach (var cred in credentials)
        {
            if (string.IsNullOrWhiteSpace(cred.TenantCode) || string.IsNullOrWhiteSpace(cred.ApiKey))
            {
                _logger.LogWarning("Credencial de bootstrap del simulador mal configurada; se omite.");
                continue;
            }

            string code = cred.TenantCode.Trim().ToUpperInvariant();

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

            var client = _factory.CreateMqttClient();

            // Al caer la conexión (p. ej. el broker embebido se reinició con el API) se relanza
            // el bucle de reconexión EN SEGUNDO PLANO. El handler retorna de inmediato: el backoff
            // NO corre dentro del pipeline de desconexión del cliente, que quedaría bloqueado.
            client.DisconnectedAsync += e =>
            {
                if (!_shutdown.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "Conexión del simulador caída para el tenant {TenantCode} ({Reason}); reintentando reconexión.",
                        code, e.Reason);
                    _ = Task.Run(() => ReconnectWithBackoffAsync(code));
                }
                return Task.CompletedTask;
            };

            // El cliente queda registrado aunque el primer intento falle: así el tenant cuenta
            // como "configurado" y el bucle de reintentos puede levantarlo cuando el broker esté.
            _clients[code] = client;
            _optionsByTenant[code] = options;

            await TryConnectAsync(code, cancellationToken);
        }
    }

    /// <summary>
    /// Intento de conexión puntual para un tenant ya registrado. Si falla (broker aún no
    /// disponible, credencial no sembrada todavía) arranca el bucle de reintentos en segundo
    /// plano en vez de rendirse.
    /// </summary>
    private async Task TryConnectAsync(string code, CancellationToken cancellationToken)
    {
        if (!_clients.TryGetValue(code, out var client) || !_optionsByTenant.TryGetValue(code, out var options))
            return;

        try
        {
            var result = await client.ConnectAsync(options, cancellationToken);
            if (result.ResultCode == MqttClientConnectResultCode.Success)
            {
                _logger.LogInformation(
                    "Simulador conectado al broker como tenant {TenantCode} (ClientId IoT-Simulator-{TenantCode}).",
                    code, code);
                return;
            }

            _logger.LogError(
                "El simulador NO pudo autenticarse como tenant {TenantCode}: {Reason}. " +
                "¿Está sembrada su credencial de bootstrap? Reintentará en segundo plano.", code, result.ResultCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo al conectar el simulador como tenant {TenantCode}; reintentará en segundo plano.", code);
        }

        _ = Task.Run(() => ReconnectWithBackoffAsync(code));
    }

    /// <summary>
    /// Bucle de reconexión con backoff exponencial acotado (1s → 30s). Un único bucle por
    /// tenant, garantizado por <see cref="_reconnecting"/>, para que varios disparos de
    /// DisconnectedAsync no acumulen bucles. Termina al reconectar o al apagar el simulador.
    /// </summary>
    private async Task ReconnectWithBackoffAsync(string code)
    {
        if (!_reconnecting.TryAdd(code, 0))
            return; // ya hay un bucle vivo para este tenant

        try
        {
            var delay = InitialReconnectDelay;
            while (!_shutdown.IsCancellationRequested)
            {
                if (!_clients.TryGetValue(code, out var client) || !_optionsByTenant.TryGetValue(code, out var options))
                    return;
                if (client.IsConnected)
                    return;

                try
                {
                    await Task.Delay(delay, _shutdown.Token);
                    var result = await client.ConnectAsync(options, _shutdown.Token);
                    if (result.ResultCode == MqttClientConnectResultCode.Success)
                    {
                        _logger.LogInformation("Simulador RECONECTADO al broker como tenant {TenantCode}.", code);
                        return;
                    }

                    _logger.LogWarning(
                        "Reintento de conexión del simulador como tenant {TenantCode} rechazado: {Reason}.",
                        code, result.ResultCode);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex, "Reintento de conexión del simulador como tenant {TenantCode} falló; siguiente intento en {Delay}s.",
                        code, delay.TotalSeconds);
                }

                delay = TimeSpan.FromSeconds(Math.Min(MaxReconnectSeconds, delay.TotalSeconds * 2));
            }
        }
        finally
        {
            _reconnecting.TryRemove(code, out _);
        }
    }

    /// <summary>
    /// Publica en nombre de un tenant. Si no hay conexión para ese tenant (credencial no
    /// configurada o conexión caída) devuelve false sin lanzar y asegura que haya un bucle de
    /// reconexión en marcha: el simulador sigue con el resto y la conexión vuelve sola. No "cae"
    /// a otra conexión a propósito: publicar por el tenant equivocado es justo lo que la
    /// plataforma ahora rechaza.
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
            _logger.LogDebug(
                "Sin conexión para el tenant {TenantCode}; se omite la publicación en '{Topic}' (reconexión en curso).",
                code, topic);
            // Defensa: asegurar un bucle de reconexión aunque, por el motivo que sea, no haya
            // saltado DisconnectedAsync. _reconnecting evita duplicar bucles.
            if (_clients.ContainsKey(code) && !_shutdown.IsCancellationRequested)
                _ = Task.Run(() => ReconnectWithBackoffAsync(code));
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
        // Frena los bucles de reconexión antes de cerrar los clientes.
        _shutdown.Cancel();

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
        _optionsByTenant.Clear();
        _shutdown.Dispose();
    }

    /// <summary>Forma de cada entrada de <c>MqttBroker:BootstrapCredentials</c>.</summary>
    private sealed class BootstrapCredentialConfig
    {
        public string? TenantCode { get; set; }
        public string? ApiKey { get; set; }
    }
}
