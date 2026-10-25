using IoT.Api.Common;
using IoT.Infrastructure.Services;
using IoT.Application.Common.Interfaces;
using IoT.Application.Firmware;
using IoT.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IoT.Api.Controllers;

[Authorize]
[RequireTenantDataPlane]
[ApiController]
[Route("api/v1/[controller]")]
public sealed class FirmwareController : ControllerBase
{
    private readonly IFirmwareOtaService _firmwareOtaService;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<FirmwareController> _logger;

    public FirmwareController(
        IFirmwareOtaService firmwareOtaService,
        ITenantContext tenantContext,
        ILogger<FirmwareController> logger)
    {
        _firmwareOtaService = firmwareOtaService;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    /// <summary>
    /// Lista los registros de versiones de firmware disponibles para el tenant actual.
    /// </summary>
    [HttpGet("records")]
    [HttpGet("/api/MqttBroker/Firmware/GetFirmwareVersionRecords")]
    [ProducesResponseType(typeof(IReadOnlyList<FirmwareVersionRecordDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFirmwareVersionRecords(
        [FromQuery] string? targetModel,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await _firmwareOtaService.GetFirmwareRecordsAsync(
            _tenantContext.TenantId, targetModel, skip, take, cancellationToken);

        if (result.IsFailure)
            return BadRequest(new { error = result.Error.Message });

        return Ok(result.Value);
    }

    /// <summary>
    /// Sube una nueva versión de firmware (.bin, .hex, .dfu) y opcionalmente dispara la actualización OTA.
    /// </summary>
    // Escribe firmware en hardware real: irreversible, exige rol explícito.
    [Authorize(Policy = AuthorizationPolicies.FirmwareManage)]
    // Tope DURO del cuerpo de la peticion y del multipart. Sin esto el unico limite era el
    // default de Kestrel y el client_max_body_size del nginx del FRONTEND, que una llamada
    // directa al puerto de la API esquiva. El tope efectivo se puede BAJAR por configuracion
    // con Firmware:MaxUploadBytes; subirlo no sirve porque este atributo rechaza antes.
    [RequestSizeLimit(FirmwareStorage.MaxUploadBytesHardLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = FirmwareStorage.MaxUploadBytesHardLimit)]
    [HttpPost("upload")]
    [HttpPost("/api/MqttBroker/Firmware/UpdateFirmwareVersion")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(FirmwareVersionRecordDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadFirmware(
        [FromForm] ReferenceUpdateFirmwareDto? legacyRequest,
        [FromQuery] ReferenceGenericParameters? legacyParams,
        IFormFile? file,
        [FromForm] string? firmwareVersion,
        [FromForm] string? targetModel,
        [FromForm] string? featureDescription,
        CancellationToken cancellationToken = default)
    {
        // Soporte unificado para DTO moderno y contrato legacy de References/Mqtt-Broker
        var actualFile = file ?? legacyRequest?.NewFirmwareFile;
        var version = !string.IsNullOrWhiteSpace(firmwareVersion) ? firmwareVersion : legacyRequest?.FirmwareVersion;
        var model = !string.IsNullOrWhiteSpace(targetModel) ? targetModel : legacyRequest?.Dst;
        var description = !string.IsNullOrWhiteSpace(featureDescription) ? featureDescription : legacyRequest?.Feature;
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? legacyRequest?.UpdatedFromIp;

        if (actualFile == null || actualFile.Length == 0)
            return BadRequest(new { error = "El archivo de firmware es requerido." });

        if (string.IsNullOrWhiteSpace(version))
            return BadRequest(new { error = "La versión del firmware es requerida." });

        using var stream = actualFile.OpenReadStream();
        var uploadResult = await _firmwareOtaService.UploadFirmwareAsync(
            tenantId: _tenantContext.TenantId,
            fileStream: stream,
            fileName: actualFile.FileName,
            version: version,
            targetModel: model,
            featureDescription: description,
            updatedFromIp: ip,
            actor: User.Identity?.Name,
            cancellationToken: cancellationToken);

        if (uploadResult.IsFailure)
            return BadRequest(new { error = uploadResult.Error.Message });

        // Si se indicó un DeviceId o GroupId, arrancar OTA en background automáticamente. El
        // Result del arranque ya NO se descarta: antes, aunque el dispositivo no existiera,
        // fuera de otro tenant o ya tuviera una campaña en curso, el operador recibía 202 y
        // creía que la flota se estaba actualizando (MEDIUM-16).
        Result? rollout = null;
        if (legacyParams?.DeviceId.HasValue == true && legacyParams.DeviceId.Value != Guid.Empty)
        {
            rollout = await _firmwareOtaService.StartDeviceOtaAsync(_tenantContext.TenantId, legacyParams.DeviceId.Value, version, User.Identity?.Name, cancellationToken);
        }
        else if (legacyParams?.GroupId.HasValue == true && legacyParams.GroupId.Value != Guid.Empty)
        {
            rollout = await _firmwareOtaService.StartGroupOtaAsync(_tenantContext.TenantId, legacyParams.GroupId.Value, version, User.Identity?.Name, cancellationToken);
        }

        // La subida sí tuvo éxito (el binario quedó almacenado), de ahí el 202; pero si se pidió
        // un arranque automático, el cuerpo dice honestamente si prendió y por qué no.
        if (rollout is not null)
        {
            return Accepted(new
            {
                firmware = uploadResult.Value,
                rolloutStarted = rollout.IsSuccess,
                rolloutError = rollout.IsFailure ? rollout.Error.Message : null
            });
        }

        return Accepted(uploadResult.Value);
    }

    /// <summary>
    /// Inicia la actualización OTA de firmware en un dispositivo específico.
    /// </summary>
    // Escribe firmware en hardware real: irreversible, exige rol explícito.
    [Authorize(Policy = AuthorizationPolicies.FirmwareManage)]
    [HttpPost("devices/{deviceId:guid}/start-ota")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> StartDeviceOta(
        Guid deviceId,
        [FromBody] StartDeviceOtaRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request?.FirmwareVersion))
            return BadRequest(new { error = "FirmwareVersion es obligatoria." });

        var result = await _firmwareOtaService.StartDeviceOtaAsync(_tenantContext.TenantId, deviceId, request.FirmwareVersion, User.Identity?.Name, cancellationToken);

        if (result.IsFailure)
            return BadRequest(new { error = result.Error.Message });

        return Ok(new { message = $"Actualización OTA iniciada para versión {request.FirmwareVersion}." });
    }

    /// <summary>
    /// Inicia la actualización OTA de firmware para todos los dispositivos de un grupo.
    /// </summary>
    // Escribe firmware en hardware real: irreversible, exige rol explícito.
    [Authorize(Policy = AuthorizationPolicies.FirmwareManage)]
    [HttpPost("groups/{groupId:guid}/start-ota")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> StartGroupOta(
        Guid groupId,
        [FromBody] StartGroupOtaRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request?.FirmwareVersion))
            return BadRequest(new { error = "FirmwareVersion es obligatoria." });

        var result = await _firmwareOtaService.StartGroupOtaAsync(_tenantContext.TenantId, groupId, request.FirmwareVersion, User.Identity?.Name, cancellationToken);

        if (result.IsFailure)
            return BadRequest(new { error = result.Error.Message });

        return Ok(new { message = $"Actualización OTA iniciada para el grupo con versión {request.FirmwareVersion}." });
    }

    /// <summary>
    /// Ejecuta el rollback de firmware para un dispositivo a una versión previa o especificada.
    /// </summary>
    // Escribe firmware en hardware real: irreversible, exige rol explícito.
    [Authorize(Policy = AuthorizationPolicies.FirmwareManage)]
    [HttpPost("devices/{deviceId:guid}/rollback")]
    [HttpPost("/api/MqttBroker/Firmware/RollbackFirmwareVersion")]
    // El HttpGet legacy de esta ruta se eliminó: es una operación que FLASHEA equipos, así que
    // un GET la dejaba en el log de acceso y el historial del navegador y la hacía reintentable
    // como si fuese idempotente (MEDIUM-16). El frontend ya la invoca por POST.
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RollbackFirmware(
        Guid? deviceId,
        [FromQuery] ReferenceGenericParameters? legacyParams,
        [FromBody] RollbackFirmwareRequest? request,
        CancellationToken cancellationToken = default)
    {
        var targetDeviceId = deviceId ?? legacyParams?.DeviceId;
        if (!targetDeviceId.HasValue || targetDeviceId.Value == Guid.Empty)
            return BadRequest(new { error = "DeviceId es obligatorio para el rollback." });

        var recordId = request?.FirmwareRecordId ?? legacyParams?.FirmwareRecordId;
        var version = request?.TargetVersion ?? legacyParams?.FirmwareVersion;

        var result = await _firmwareOtaService.RollbackDeviceOtaAsync(_tenantContext.TenantId, targetDeviceId.Value, recordId, version, User.Identity?.Name, cancellationToken);

        if (result.IsFailure)
            return BadRequest(new { error = result.Error.Message });

        return Ok(new { message = "Rollback de firmware iniciado exitosamente." });
    }

    /// <summary>
    /// Borra una versión de firmware: marca el registro y desenlaza el binario del disco.
    /// </summary>
    // Toca el almacén y el inventario de firmware: exige rol de gestión.
    [Authorize(Policy = AuthorizationPolicies.FirmwareManage)]
    [HttpDelete("versions/{version}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteFirmwareVersion(
        string version,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(version))
            return BadRequest(new { error = "La versión es obligatoria." });

        var result = await _firmwareOtaService.DeleteFirmwareVersionAsync(
            _tenantContext.TenantId, version, User.Identity?.Name, cancellationToken);

        if (result.IsFailure)
            return BadRequest(new { error = result.Error.Message });

        return Ok(new { message = $"Versión de firmware '{version}' borrada." });
    }

    /// <summary>
    /// Cancela la actualización OTA en curso de un dispositivo (lo saca de "actualizando").
    /// </summary>
    // Cambia el estado de una campaña en hardware real: exige rol de gestión.
    [Authorize(Policy = AuthorizationPolicies.FirmwareManage)]
    [HttpPost("devices/{deviceId:guid}/cancel-ota")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CancelDeviceOta(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        var result = await _firmwareOtaService.CancelDeviceOtaAsync(
            _tenantContext.TenantId, deviceId, User.Identity?.Name, cancellationToken);

        if (result.IsFailure)
            return BadRequest(new { error = result.Error.Message });

        return Ok(new { message = "Actualización cancelada." });
    }

    /// <summary>
    /// Obtiene el estado actual de la actualización OTA de un dispositivo.
    /// </summary>
    [HttpGet("devices/{deviceId:guid}/status")]
    [ProducesResponseType(typeof(DeviceOtaStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetDeviceOtaStatus(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        var result = await _firmwareOtaService.GetDeviceOtaStatusAsync(
            _tenantContext.TenantId, deviceId, cancellationToken);

        if (result.IsFailure)
            return BadRequest(new { error = result.Error.Message });

        return Ok(result.Value);
    }
}

public sealed class ReferenceUpdateFirmwareDto
{
    public IFormFile? NewFirmwareFile { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? Feature { get; set; }
    public string? Dst { get; set; }
    public string? UpdatedFromIp { get; set; }
}
