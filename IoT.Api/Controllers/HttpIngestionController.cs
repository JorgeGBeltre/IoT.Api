using System.Text.Json;
using IoT.Application.Ingestion.Commands;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Http;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace IoT.Api.Controllers;

/// <summary>
/// Ingesta HTTP de dispositivos. Modelo de adopción (confirmado por el dueño): NO usa
/// credencial/ApiKey como MQTT; el dispositivo se configura para apuntar al servidor y la
/// plataforma sólo acepta tramas de dispositivos REGISTRADOS (por IMEI/ChipId). Un equipo
/// no registrado es rechazado por el pipeline (Ingestion.DeviceNotProvisioned). Por eso los
/// endpoints de dispositivo son [AllowAnonymous] (no exigen JWT), pero la identidad se valida
/// contra el provisioning. (Spoofing por ChipId es inherente a estos trackers HTTP "tontos";
/// si se requiere, se puede añadir después un token por dispositivo.)
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
    private readonly ILogger<HttpIngestionController> _logger;

    public HttpIngestionController(
        IMediator mediator,
        HttpDeviceTransport httpTransport,
        IDeviceRepository deviceRepository,
        ILogger<HttpIngestionController> logger)
    {
        _mediator = mediator;
        _httpTransport = httpTransport;
        _deviceRepository = deviceRepository;
        _logger = logger;
    }

    [HttpPost("ingest")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> IngestTelemetry(
        [FromQuery] string? topic,
        [FromBody] JsonElement payload,
        CancellationToken cancellationToken)
    {
        string topicOrRoute = string.IsNullOrWhiteSpace(topic) ? "telemetry" : topic;
        string rawPayload = payload.GetRawText();

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
    public async Task<IActionResult> IngestWebhook(
        string source,
        [FromQuery] string? route,
        [FromBody] JsonElement payload,
        CancellationToken cancellationToken)
    {
        string topicOrRoute = !string.IsNullOrWhiteSpace(route) ? route : $"webhook/{source}";
        string rawPayload = payload.GetRawText();

        var command = new IngestTelemetryCommand(topicOrRoute, rawPayload, Source: $"webhook:{source}");
        var result = await _mediator.Send(command, cancellationToken);

        if (result.IsFailure)
        {
            _logger.LogWarning("Webhook telemetry ingestion failed for source '{Source}': {Error}", source, result.Error.Message);
            return BadRequest(new { error = result.Error.Message, code = result.Error.Code });
        }

        return Ok(new { status = "Accepted", source, route = topicOrRoute });
    }

    [HttpGet("/api/v1/devices/{chipId}/poll-commands")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
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
}
