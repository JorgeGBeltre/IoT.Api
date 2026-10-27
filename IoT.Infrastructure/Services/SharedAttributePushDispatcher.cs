using System.Text.Json;
using IoT.Application.Common.Interfaces;
using IoT.Application.Common.Observability;
using IoT.Domain.Entities;
using IoT.Protocols.Contracts;
using IoT.Protocols.Routing;
using Microsoft.Extensions.Logging;

namespace IoT.Infrastructure.Services;

/// <summary>
/// Despachador de sincronización de atributos compartidos hacia el equipo (ADR-015, P1c).
/// Soporta MQTT como canal nativo estándar (ThingsBoard) y traducción a comandos GT06 (TCP binario)
/// cuando el dispositivo está conectado en el gateway TCP.
/// </summary>
public sealed class SharedAttributePushDispatcher : ISharedAttributePushDispatcher
{
    private readonly IMqttPublisher _mqttPublisher;
    private readonly ITopicRoutingService _topicRoutingService;
    private readonly IProtocolRegistry _protocolRegistry;
    private readonly ITcpCommandGateway? _tcpCommandGateway;
    private readonly ILogger<SharedAttributePushDispatcher> _logger;

    public SharedAttributePushDispatcher(
        IMqttPublisher mqttPublisher,
        ITopicRoutingService topicRoutingService,
        IProtocolRegistry protocolRegistry,
        ILogger<SharedAttributePushDispatcher> logger,
        ITcpCommandGateway? tcpCommandGateway = null)
    {
        _mqttPublisher = mqttPublisher;
        _topicRoutingService = topicRoutingService;
        _protocolRegistry = protocolRegistry;
        _logger = logger;
        _tcpCommandGateway = tcpCommandGateway;
    }

    public async Task<SharedAttributePushResult> PushSharedAttributesAsync(
        Device device,
        IReadOnlyDictionary<string, string?> attributes,
        CancellationToken cancellationToken = default)
    {
        if (device is null)
        {
            throw new ArgumentNullException(nameof(device));
        }

        if (attributes is null || attributes.Count == 0)
        {
            return new SharedAttributePushResult(false, "none", device.ChipId, "No attributes provided");
        }

        var profile = _protocolRegistry.GetProfile(device.Model) ??
                      _protocolRegistry.GetAllProfiles().FirstOrDefault(p =>
                          string.Equals(p.Model, device.Model, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(p.ProfileId, device.Model, StringComparison.OrdinalIgnoreCase));

        bool isGt06 = profile != null &&
                      (string.Equals(profile.TcpFamily, "GT06", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(profile.PrimaryTransport, "TCP-R12LF", StringComparison.OrdinalIgnoreCase));

        // 1. Canal TCP binario para familia GT06 (ADR-015 D4)
        if (isGt06)
        {
            if (_tcpCommandGateway != null && _tcpCommandGateway.IsConnected(device.ChipId))
            {
                var translatedCommands = TranslateToGt06Commands(attributes);
                if (translatedCommands.Count > 0)
                {
                    bool allSent = true;
                    foreach (var cmd in translatedCommands)
                    {
                        try
                        {
                            bool sent = await _tcpCommandGateway.SendCommandAsync(device.ChipId, cmd, cancellationToken);
                            if (!sent)
                            {
                                allSent = false;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Fallo al enviar comando GT06 '{Command}' a IMEI {ChipId}", cmd, device.ChipId);
                            allSent = false;
                        }
                    }

                    if (allSent)
                    {
                        IoTMetrics.AttributesPushedCounter.Add(1,
                            new KeyValuePair<string, object?>("transport", "tcp"),
                            new KeyValuePair<string, object?>("status", "success"));

                        _logger.LogInformation("Atributos shared sincronizados vía TCP GT06 a IMEI {ChipId} ({Count} comandos enviados)",
                            device.ChipId, translatedCommands.Count);

                        return new SharedAttributePushResult(true, "tcp", device.ChipId);
                    }
                }
            }
            // Dispositivo GT06 desconectado del socket TCP
            IoTMetrics.AttributesPushedCounter.Add(1,
                new KeyValuePair<string, object?>("transport", "tcp"),
                new KeyValuePair<string, object?>("status", "offline"));

            _logger.LogInformation("Dispositivo GT06 IMEI {ChipId} no conectado en socket TCP. Estado deseado persistido en BD para posterior sincronización.",
                device.ChipId);

            return new SharedAttributePushResult(false, "tcp", device.ChipId, "DeviceOfflineTcp");
        }

        // 2. Canal MQTT nativo (ThingsBoard standard, ADR-015 D3)
        string topic = _topicRoutingService.BuildAttributeTopic(device.Model, device.ChipId, device.TenantId);
        string payloadJson = JsonSerializer.Serialize(attributes);

        var publishOptions = new MqttPublishOptions
        {
            Qos = 1,
            Retain = false,
            MessageExpiryInterval = 300,
            ContentType = "application/json",
            UserProperties = new Dictionary<string, string>
            {
                ["tenant_id"] = device.TenantId.ToString(),
                ["chip_id"] = device.ChipId,
                ["scope"] = "shared"
            }
        };

        try
        {
            await _mqttPublisher.PublishAsync(topic, payloadJson, publishOptions, cancellationToken);

            IoTMetrics.AttributesPushedCounter.Add(1,
                new KeyValuePair<string, object?>("transport", "mqtt"),
                new KeyValuePair<string, object?>("status", "success"));

            _logger.LogInformation("Atributos shared publicados con éxito a {Topic} para equipo {ChipId} ({Count} pares)",
                topic, device.ChipId, attributes.Count);

            return new SharedAttributePushResult(true, "mqtt", topic);
        }
        catch (Exception ex)
        {
            IoTMetrics.AttributesPushedCounter.Add(1,
                new KeyValuePair<string, object?>("transport", "mqtt"),
                new KeyValuePair<string, object?>("status", "failed"));

            _logger.LogError(ex, "Error al publicar atributos shared a {Topic} para equipo {ChipId}",
                topic, device.ChipId);

            return new SharedAttributePushResult(false, "mqtt", topic, ex.Message);
        }
    }

    /// <summary>
    /// Traduce pares clave-valor conocidos a comandos de configuración nativos de la norma GT06 (ADR-015 D4).
    /// </summary>
    private static List<string> TranslateToGt06Commands(IReadOnlyDictionary<string, string?> attributes)
    {
        var commands = new List<string>();

        foreach (var (key, value) in attributes)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var normalizedKey = key.Trim().ToLowerInvariant();

            switch (normalizedKey)
            {
                case "upload_interval":
                case "interval":
                case "timer":
                    if (int.TryParse(value, out int seconds) && seconds > 0)
                    {
                        commands.Add($"TIMER,{seconds}#");
                    }
                    break;

                case "heartbeat_interval":
                case "hbt":
                    if (int.TryParse(value, out int minutes) && minutes > 0)
                    {
                        commands.Add($"HBT,{minutes}#");
                    }
                    break;

                case "relay":
                case "relay_cut":
                case "engine_stop":
                    if (IsTruthy(value))
                    {
                        commands.Add("DYD,000000#");
                    }
                    else
                    {
                        commands.Add("HFYD,000000#");
                    }
                    break;

                case "relay_restore":
                case "engine_resume":
                    if (IsTruthy(value))
                    {
                        commands.Add("HFYD,000000#");
                    }
                    break;

                case "reset":
                case "reboot":
                    if (IsTruthy(value))
                    {
                        commands.Add("RESET#");
                    }
                    break;

                case "raw_command":
                case "command":
                    commands.Add(value.EndsWith('#') ? value : $"{value}#");
                    break;
            }
        }

        return commands;
    }

    private static bool IsTruthy(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("yes", StringComparison.OrdinalIgnoreCase);
}
