using System.Text.Json;
using IoT.Application.Common.Interfaces;
using IoT.Application.Ingestion.Commands;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Http;
using IoT.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IoT.Api.Controllers;

/// <summary>
/// Ingesta HTTP de dispositivos. Modelo de adopción (confirmado por el dueño): NO usa
/// credencial/ApiKey como MQTT; el dispositivo se configura para apuntar al servidor y la
/// plataforma sólo acepta tramas de dispositivos REGISTRADOS (por IMEI/ChipId). Un equipo
/// no registrado es rechazado por el pipeline (Ingestion.DeviceNotProvisioned). Por eso los
/// endpoints de dispositivo son [AllowAnonymous] (no exigen JWT), pero la identidad se valida
/// contra el provisioning. (Spoofing por ChipId es inherente a los trackers HTTP "tontos" sin
/// credencial; los dispositivos que SÍ tienen ApiKey per-device deben presentarla y se verifica
/// en todos los endpoints, incluido /ingest.)
/// </summary>
[AllowAnonymous]
[EnableRateLimiting("DeviceIngest")]
[ApiController]
[Route("api/v1/telemetry")]
public sealed class HttpIngestionController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly HttpDeviceTransport _httpTransport;
    private readonly IDeviceRepository _deviceRepository;
    private readonly IConfiguration _configuration;
    private readonly IDeviceApiKeyService? _apiKeyService;
    private readonly ILogger<HttpIngestionController> _logger;

    public HttpIngestionController(
        IMediator mediator,
        HttpDeviceTransport httpTransport,
        IDeviceRepository deviceRepository,
        IConfiguration configuration,
        ILogger<HttpIngestionController> logger,
        IDeviceApiKeyService? apiKeyService = null)
    {
        _mediator = mediator;
        _httpTransport = httpTransport;
        _deviceRepository = deviceRepository;
        _configuration = configuration;
        _logger = logger;
        _apiKeyService = apiKeyService;
    }

    [HttpPost("ingest")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> IngestTelemetry(
        [FromQuery] string? topic,
        [FromBody] JsonElement payload,
        CancellationToken cancellationToken)
    {
        string topicOrRoute = string.IsNullOrWhiteSpace(topic) ? "telemetry" : topic;
        string rawPayload = payload.GetRawText();

        var authFailure = await ValidateHttpDeviceCredentialsAsync(topicOrRoute, payload, cancellationToken);
        if (authFailure is not null)
        {
            return authFailure;
        }

        var command = new IngestTelemetryCommand(topicOrRoute, rawPayload, Source: "http");
        var result = await _mediator.Send(command, cancellationToken);

        if (result.IsFailure)
        {
            _logger.LogWarning("HTTP telemetry ingestion failed for route '{Route}': {Error}", topicOrRoute, result.Error.Message);
            return BadRequest(new { error = result.Error.Message, code = result.Error.Code });
        }

        return Ok(new { status = "Ingested", route = topicOrRoute, timestamp = DateTime.UtcNow });
    }

    [HttpPost("webhook/{source}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> IngestWebhook(
        string source,
        [FromQuery] string? route,
        [FromBody] JsonElement payload,
        CancellationToken cancellationToken)
    {
        string topicOrRoute = !string.IsNullOrWhiteSpace(route) ? route : $"webhook/{source}";
        string rawPayload = payload.GetRawText();

        var authFailure = await ValidateHttpDeviceCredentialsAsync(topicOrRoute, payload, cancellationToken);
        if (authFailure is not null)
        {
            return authFailure;
        }

        var command = new IngestTelemetryCommand(topicOrRoute, rawPayload, Source: $"webhook:{source}");
        var result = await _mediator.Send(command, cancellationToken);

        if (result.IsFailure)
        {
            _logger.LogWarning("Webhook telemetry ingestion failed for source '{Source}': {Error}", source, result.Error.Message);
            return BadRequest(new { error = result.Error.Message, code = result.Error.Code });
        }

        return Ok(new { status = "Accepted", source, route = topicOrRoute });
    }

    private async Task<IActionResult?> ValidateHttpDeviceCredentialsAsync(
        string topicOrRoute,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (_apiKeyService is null)
        {
            return null;
        }

        bool requireKey = _configuration.GetValue<bool>("HttpIngestion:RequireDeviceKey", false);
        string? providedKey = Request.Headers["X-Device-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(providedKey) && Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var authStr = authHeader.ToString();
            if (authStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                providedKey = authStr.Substring(7).Trim();
            }
        }

        string? chipId = ExtractCanonicalChipId(topicOrRoute, payload);
        if (!string.IsNullOrWhiteSpace(chipId))
        {
            var device = await _deviceRepository.GetByChipIdGlobalAsync(chipId, cancellationToken);
            if (device != null && device.HasMqttApiKey)
            {
                if (string.IsNullOrWhiteSpace(providedKey) || !_apiKeyService.Verify(providedKey, device.MqttApiKeyHash!))
                {
                    _logger.LogWarning("Ingesta HTTP rechazada para '{ChipId}': ApiKey no autorizada o ausente.", chipId);
                    return Unauthorized(new { error = "Invalid or missing device API key." });
                }
            }
            else if (requireKey)
            {
                return Unauthorized(new { error = "Device API key is required." });
            }
        }
        else if (requireKey)
        {
            return Unauthorized(new { error = "Device identity and API key are required." });
        }

        return null;
    }

    private static string? ExtractCanonicalChipId(string topicOrRoute, JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in payload.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String &&
                    (prop.Name.Equals("chipId", StringComparison.OrdinalIgnoreCase) ||
                     prop.Name.Equals("chip_id", StringComparison.OrdinalIgnoreCase) ||
                     prop.Name.Equals("imei", StringComparison.OrdinalIgnoreCase) ||
                     prop.Name.Equals("deviceId", StringComparison.OrdinalIgnoreCase) ||
                     prop.Name.Equals("device_id", StringComparison.OrdinalIgnoreCase)))
                {
                    var val = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
                }

                if (prop.Value.ValueKind == JsonValueKind.Object &&
                    prop.Name.Equals("device", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var nested in prop.Value.EnumerateObject())
                    {
                        if (nested.Value.ValueKind == JsonValueKind.String &&
                            (nested.Name.Equals("chipId", StringComparison.OrdinalIgnoreCase) ||
                             nested.Name.Equals("imei", StringComparison.OrdinalIgnoreCase) ||
                             nested.Name.Equals("id", StringComparison.OrdinalIgnoreCase)))
                        {
                            var val = nested.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
                        }
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(topicOrRoute))
        {
            var segments = topicOrRoute.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (segments[i].Equals("devices", StringComparison.OrdinalIgnoreCase) ||
                    segments[i].Equals("telemetry", StringComparison.OrdinalIgnoreCase) ||
                    segments[i].Equals("event", StringComparison.OrdinalIgnoreCase) ||
                    segments[i].Equals("events", StringComparison.OrdinalIgnoreCase) ||
                    segments[i].Equals("ota", StringComparison.OrdinalIgnoreCase) ||
                    segments[i].Equals("commands", StringComparison.OrdinalIgnoreCase))
                {
                    var candidate = segments[i + 1];
                    if (!candidate.Equals("telemetry", StringComparison.OrdinalIgnoreCase) &&
                        !candidate.Equals("ingest", StringComparison.OrdinalIgnoreCase) &&
                        !candidate.Equals("webhook", StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    [HttpGet("/api/v1/devices/{chipId}/poll-commands")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PollCommands(string chipId, CancellationToken cancellationToken)
    {
        // Sólo un dispositivo REGISTRADO y Activo puede drenar su cola de comandos
        // (modelo de adopción). Un ChipId no provisionado no debe ver nada: 404 para
        // no revelar la existencia ni servir comandos ajenos.
        var device = await _deviceRepository.GetByChipIdGlobalAsync(chipId, cancellationToken);
        if (device is null || device.AdminStatus != DeviceAdminStatus.Active)
        {
            _logger.LogWarning("poll-commands rechazado: ChipId '{ChipId}' no registrado o inactivo.", chipId);
            return NotFound();
        }

        // Si el dispositivo tiene ApiKey configurada, o la política global exige DeviceKey, validamos la credencial
        bool requireKey = _configuration.GetValue<bool>("HttpIngestion:RequireDeviceKey", false) || device.HasMqttApiKey;
        if (requireKey && _apiKeyService != null)
        {
            string? providedKey = Request.Headers["X-Device-Key"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(providedKey) && Request.Headers.TryGetValue("Authorization", out var authHeader))
            {
                var authStr = authHeader.ToString();
                if (authStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    providedKey = authStr.Substring(7).Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(providedKey) || string.IsNullOrEmpty(device.MqttApiKeyHash) || !_apiKeyService.Verify(providedKey, device.MqttApiKeyHash))
            {
                _logger.LogWarning("poll-commands no autorizado para '{ChipId}': ApiKey inválida o ausente.", chipId);
                return Unauthorized(new { error = "Valid device key required for polling commands." });
            }
        }

        var command = _httpTransport.PollNextCommand(chipId);
        if (command == null)
        {
            return NoContent();
        }

        return Ok(new
        {
            command_id = command.CommandId,
            payload = command.Payload,
            created_at = command.CreatedAt,
            expires_at = command.ExpiresAt
        });
    }

    /// <summary>
    /// Consulta los atributos de configuración (Shared y Client) desde el propio dispositivo por HTTP (ADR-015, P1c).
    /// Los atributos de ámbito Server son privados de la plataforma y NUNCA se exponen al dispositivo físico.
    /// </summary>
    [HttpGet("/api/v1/devices/{chipId}/attributes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDeviceAttributes(
        string chipId,
        [FromQuery] string? scope,
        [FromServices] IServiceScopeFactory scopeFactory,
        CancellationToken cancellationToken)
    {
        var device = await _deviceRepository.GetByChipIdGlobalAsync(chipId, cancellationToken);
        if (device is null || device.AdminStatus != DeviceAdminStatus.Active)
        {
            _logger.LogWarning("Consulta de atributos HTTP rechazada: ChipId '{ChipId}' no registrado o inactivo.", chipId);
            return NotFound();
        }

        bool requireKey = _configuration.GetValue<bool>("HttpIngestion:RequireDeviceKey", false) || device.HasMqttApiKey;
        if (requireKey && _apiKeyService != null)
        {
            string? providedKey = Request.Headers["X-Device-Key"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(providedKey) && Request.Headers.TryGetValue("Authorization", out var authHeader))
            {
                var authStr = authHeader.ToString();
                if (authStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    providedKey = authStr.Substring(7).Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(providedKey) || string.IsNullOrEmpty(device.MqttApiKeyHash) || !_apiKeyService.Verify(providedKey, device.MqttApiKeyHash))
            {
                _logger.LogWarning("Consulta de atributos HTTP no autorizada para '{ChipId}': ApiKey inválida o ausente.", chipId);
                return Unauthorized(new { error = "Valid device key required for querying attributes." });
            }
        }

        AttributeScope? requestedScope = null;
        if (!string.IsNullOrWhiteSpace(scope))
        {
            if (scope.Equals("shared", StringComparison.OrdinalIgnoreCase))
            {
                requestedScope = AttributeScope.Shared;
            }
            else if (scope.Equals("client", StringComparison.OrdinalIgnoreCase))
            {
                requestedScope = AttributeScope.Client;
            }
            else
            {
                return BadRequest(new { error = $"Ámbito inválido para dispositivo: '{scope}'. Use 'shared' o 'client'." });
            }
        }

        using var tenantScope = scopeFactory.CreateTenantScope(device.TenantId);
        var attributeRepo = tenantScope.ServiceProvider.GetRequiredService<IDeviceAttributeRepository>();

        if (requestedScope.HasValue)
        {
            var list = await attributeRepo.GetByDeviceAndScopeAsync(device.Id, requestedScope.Value, cancellationToken);
            var dict = list.ToDictionary(a => a.Key, a => a.Value);
            return Ok(dict);
        }
        else
        {
            var list = await attributeRepo.ListByDeviceAsync(device.Id, cancellationToken);
            var response = new Dictionary<string, Dictionary<string, string>>
            {
                ["shared"] = list.Where(a => a.Scope == AttributeScope.Shared).ToDictionary(a => a.Key, a => a.Value),
                ["client"] = list.Where(a => a.Scope == AttributeScope.Client).ToDictionary(a => a.Key, a => a.Value)
            };
            return Ok(response);
        }
    }
}
