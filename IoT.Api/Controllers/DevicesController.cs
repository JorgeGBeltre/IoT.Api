using System.Text.Json;
using IoT.Api.Common;
using IoT.Application.Common.Interfaces;
using IoT.Application.Common.Security;
using IoT.Application.Devices.Commands;
using IoT.Application.Devices.Queries;
using IoT.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Microsoft.AspNetCore.Authorization;

namespace IoT.Api.Controllers;

[Authorize]
[RequireTenantDataPlane]
[ApiController]
[Route("api/v1/[controller]")]
public sealed class DevicesController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ITenantContext _tenantContext;
    private readonly ILanZeroconfConnectorService? _lanConnector;

    public DevicesController(
        IMediator mediator,
        ITenantContext tenantContext,
        ILanZeroconfConnectorService? lanConnector = null)
    {
        _mediator = mediator;
        _tenantContext = tenantContext;
        _lanConnector = lanConnector;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDevices([FromQuery] DeviceOperationalStatus? status, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetDevicesQuery(_tenantContext.TenantId, status), cancellationToken);
        if (result.IsFailure)
        {
            return BadRequest(new { error = result.Error.Message });
        }

        return Ok(result.Value);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDeviceById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetDeviceByIdQuery(_tenantContext.TenantId, id), cancellationToken);
        if (result.IsFailure)
        {
            return NotFound(new { error = result.Error.Message });
        }

        return Ok(result.Value);
    }

    public sealed record RegisterDeviceRequest(
        string ChipId,
        string MacAddress,
        string Name,
        string Model,
        string Manufacturer,
        IReadOnlyList<CapabilityType>? Capabilities);

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.DeviceManage)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceRequest request, CancellationToken cancellationToken)
    {
        var command = new CreateDeviceCommand(
            _tenantContext.TenantId,
            request.ChipId,
            request.MacAddress,
            request.Name,
            request.Model,
            request.Manufacturer,
            request.Capabilities);

        var result = await _mediator.Send(command, cancellationToken);
        if (result.IsFailure)
        {
            return BadRequest(new { error = result.Error.Message });
        }

        return CreatedAtAction(nameof(GetDeviceById), new { id = result.Value }, new { id = result.Value });
    }

    public sealed record ProvisionDeviceRequest(
        string ChipId,
        string? MacAddress = null,
        string? Name = null,
        string? Model = null,
        string? Manufacturer = null,
        IReadOnlyList<CapabilityType>? Capabilities = null,
        string? FirmwareVersion = null,
        string? PairingMode = null,
        string? DeviceKey = null,
        string? BindToken = null,
        string? WifiSsid = null,
        string? WifiPassword = null,
        Dictionary<string, string?>? DeviceParameters = null);

    /// <summary>
    /// Aprovisionamiento multi-modo en un solo paso (BLE GATT Schema A/B, Compatible SoftAP HTTP,
    /// Red Local LAN :8081 /zeroconf/*, Cable USB Web Serial AT+ y Reclamación). Registra —o
    /// reutiliza— el dispositivo por su ChipId en el tenant del usuario, acuña su ApiKey MQTT,
    /// persiste los atributos iniciales (<c>device_key</c>, <c>bind_token</c>, configuración
    /// compartida) y devuelve el PAQUETE de conexión y tramas listas por transporte.
    /// </summary>
    [HttpPost("provision")]
    [Authorize(Policy = AuthorizationPolicies.DeviceManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ProvisionDevice([FromBody] ProvisionDeviceRequest request, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new ProvisionDeviceCommand(
                _tenantContext.TenantId,
                request.ChipId,
                request.MacAddress,
                request.Name,
                request.Model,
                request.Manufacturer,
                request.Capabilities,
                User.Identity?.Name,
                request.FirmwareVersion,
                request.PairingMode,
                request.DeviceKey,
                request.BindToken,
                request.WifiSsid,
                request.WifiPassword,
                request.DeviceParameters),
            cancellationToken);

        if (result.IsFailure)
        {
            return BadRequest(new { error = result.Error.Message });
        }

        var provisioning = result.Value;
        return provisioning.Created
            ? CreatedAtAction(nameof(GetDeviceById), new { id = provisioning.Credentials.DeviceId }, provisioning)
            : Ok(provisioning);
    }

    public sealed record ClaimDeviceRequest(
        string ChipId,
        string ClaimSecret,
        string? Name = null,
        string? Model = null,
        string? Manufacturer = null,
        string? MacAddress = null,
        Guid? TargetCustomerId = null);

    /// <summary>
    /// Reclamación / adopción de un dispositivo por su <c>ChipId</c> y secreto de reclamación
    /// (<c>bind_token</c> o <c>device_key</c>), estilo ThingsBoard Device Claiming.
    /// </summary>
    [HttpPost("claim")]
    [Authorize(Policy = AuthorizationPolicies.DeviceManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ClaimDevice([FromBody] ClaimDeviceRequest request, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new ClaimDeviceCommand(
                _tenantContext.TenantId,
                request.ChipId,
                request.ClaimSecret,
                request.Name,
                request.Model,
                request.Manufacturer,
                request.MacAddress,
                request.TargetCustomerId,
                User.Identity?.Name),
            cancellationToken);

        if (result.IsFailure)
        {
            return BadRequest(new { error = result.Error.Message });
        }

        var claimed = result.Value;
        return claimed.Created
            ? CreatedAtAction(nameof(GetDeviceById), new { id = claimed.Credentials.DeviceId }, claimed)
            : Ok(claimed);
    }

    public sealed record LanProbeRequest(
        string Host,
        int Port = 8081,
        string Mode = "lan_zeroconf",
        string? DeviceKey = null);

    /// <summary>
    /// Sondea un dispositivo ESP32/ESP8266 en la red local (SoftAP <c>:80/device</c> o LAN Zeroconf
    /// <c>:8081/zeroconf/info</c>) cuando el backend comparte red con el equipo.
    /// </summary>
    [HttpPost("lan-probe")]
    [Authorize(Policy = AuthorizationPolicies.DeviceManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ProbeLanDevice([FromBody] LanProbeRequest request, CancellationToken cancellationToken)
    {
        if (_lanConnector is null)
        {
            return BadRequest(new { error = "El conector LAN Zeroconf no está habilitado." });
        }

        var result = await _lanConnector.ProbeDeviceAsync(
            request.Host,
            request.Port,
            request.Mode,
            request.DeviceKey,
            cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error.Message });
    }

    public sealed record LanProvisionRequest(
        string Host,
        int Port = 8081,
        string Mode = "lan_zeroconf",
        bool Encrypt = false,
        string? ChipId = null,
        string? MacAddress = null,
        string? Name = null,
        string? Model = null,
        string? Manufacturer = null,
        IReadOnlyList<CapabilityType>? Capabilities = null,
        string? FirmwareVersion = null,
        string? DeviceKey = null,
        string? BindToken = null,
        string? WifiSsid = null,
        string? WifiPassword = null,
        Dictionary<string, string?>? DeviceParameters = null);

    /// <summary>
    /// Adopción completa por Red Local (SoftAP <c>:80/ap</c> o LAN Zeroconf <c>:8081/zeroconf/config</c>):
    /// 1) Si no se indicó <c>ChipId</c>, intenta descubrirlo sondeando el equipo en <paramref name="request"/>.Host.
    /// 2) Registra o re-empareja el dispositivo en la plataforma emitiendo su ApiKey MQTT y <c>DeviceKey</c>.
    /// 3) Intenta inyectar el paquete en el equipo local y devuelve tanto el paquete aprovisionado como el resultado del envío.
    /// </summary>
    [HttpPost("lan-provision")]
    [Authorize(Policy = AuthorizationPolicies.DeviceManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ProvisionLanDevice([FromBody] LanProvisionRequest request, CancellationToken cancellationToken)
    {
        string? chipId = request.ChipId?.Trim();
        string? mac = request.MacAddress;
        string? model = request.Model;
        string? fw = request.FirmwareVersion;
        string? devKey = request.DeviceKey;

        if (_lanConnector is not null && !string.IsNullOrWhiteSpace(request.Host))
        {
            var hostCheck = _lanConnector.ValidateLanTargetHost(request.Host, request.Port);
            if (hostCheck.IsFailure)
            {
                return BadRequest(new { error = hostCheck.Error.Message });
            }

            if (string.IsNullOrWhiteSpace(chipId))
            {
                var probeRes = await _lanConnector.ProbeDeviceAsync(
                    request.Host, request.Port, request.Mode, request.DeviceKey, cancellationToken);
                if (probeRes.IsSuccess && probeRes.Value.Reachable)
                {
                    chipId = probeRes.Value.ChipId;
                    mac ??= probeRes.Value.MacAddress;
                    model ??= probeRes.Value.Model;
                    fw ??= probeRes.Value.FirmwareVersion;
                    devKey ??= probeRes.Value.DeviceKey;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(chipId))
        {
            return BadRequest(new { error = "No se pudo obtener el ChipId del dispositivo. Indíquelo manualmente o verifique la conexión al equipo local." });
        }

        var provRes = await _mediator.Send(
            new ProvisionDeviceCommand(
                _tenantContext.TenantId,
                chipId,
                mac,
                request.Name,
                model,
                request.Manufacturer,
                request.Capabilities,
                User.Identity?.Name,
                fw,
                request.Mode,
                devKey,
                request.BindToken,
                request.WifiSsid,
                request.WifiPassword,
                request.DeviceParameters),
            cancellationToken);

        if (provRes.IsFailure)
        {
            return BadRequest(new { error = provRes.Error.Message });
        }

        var provisioning = provRes.Value;
        LanCommandExecutionResultDto? lanPush = null;

        if (_lanConnector is not null && !string.IsNullOrWhiteSpace(request.Host) && provisioning.Bundles is not null)
        {
            bool isSoftAp = string.Equals(request.Mode, "softap", StringComparison.OrdinalIgnoreCase);
            string endpoint = isSoftAp ? "ap" : "config";
            string innerDataJson;

            if (isSoftAp)
            {
                innerDataJson = provisioning.Bundles.SoftApPayloadJson;
            }
            else
            {
                LanZeroconfCrypto.TryExtractDataJson(
                    provisioning.Bundles.LanZeroconfConfigPlainJson,
                    provisioning.DeviceKey,
                    out innerDataJson,
                    out _);
            }

            var sendRes = await _lanConnector.SendLanCommandAsync(
                request.Host,
                request.Port,
                endpoint,
                innerDataJson,
                provisioning.Credentials.ChipId,
                provisioning.DeviceKey,
                encrypt: !isSoftAp && request.Encrypt,
                cancellationToken);

            if (sendRes.IsSuccess)
            {
                lanPush = sendRes.Value;
            }
        }

        return Ok(new
        {
            provisioning,
            lanPush
        });
    }

    public sealed record LanDeviceCommandRequest(
        string Host,
        int Port = 8081,
        string Endpoint = "switch",
        JsonElement? Data = null,
        bool Encrypt = false,
        string? DeviceKey = null);

    /// <summary>
    /// Ejecuta un comando directo contra la API HTTP LAN local (<c>:8081/zeroconf/*</c>) del
    /// dispositivo (<c>switch</c>, <c>switches</c>, <c>startup</c>, <c>pulse</c>, <c>sled</c>,
    /// <c>timers</c>, <c>info</c>, <c>signal_strength</c>, <c>ota_unlock</c>) con soporte tanto
    /// en texto plano como cifrado <c>AES-128-CBC</c> (<c>Key = MD5(deviceKey)</c>).
    /// </summary>
    [HttpPost("{id:guid}/lan-command")]
    [Authorize(Policy = AuthorizationPolicies.DeviceManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SendLanDeviceCommand(
        Guid id,
        [FromBody] LanDeviceCommandRequest request,
        CancellationToken cancellationToken)
    {
        if (_lanConnector is null)
        {
            return BadRequest(new { error = "El conector LAN Zeroconf no está habilitado." });
        }

        var devResult = await _mediator.Send(new GetDeviceByIdQuery(_tenantContext.TenantId, id), cancellationToken);
        if (devResult.IsFailure)
        {
            return NotFound(new { error = devResult.Error.Message });
        }

        string? resolvedKey = request.DeviceKey;
        if (string.IsNullOrWhiteSpace(resolvedKey))
        {
            var attrsResult = await _mediator.Send(new GetDeviceAttributesQuery(_tenantContext.TenantId, id), cancellationToken);
            if (attrsResult.IsSuccess)
            {
                resolvedKey = attrsResult.Value.Server
                    .FirstOrDefault(a => string.Equals(a.Key, "device_key", StringComparison.OrdinalIgnoreCase))?.Value;
            }
        }

        string innerJson = request.Data.HasValue && request.Data.Value.ValueKind != JsonValueKind.Undefined
            ? request.Data.Value.GetRawText()
            : "{}";

        string? prebuiltEncryptedEnvelope = null;
        if (!string.IsNullOrWhiteSpace(resolvedKey))
        {
            prebuiltEncryptedEnvelope = LanZeroconfCrypto.BuildEncryptedEnvelopeJson(
                innerJson,
                resolvedKey,
                devResult.Value.ChipId);
        }

        var execResult = await _lanConnector.SendLanCommandAsync(
            request.Host,
            request.Port,
            request.Endpoint,
            innerJson,
            devResult.Value.ChipId,
            resolvedKey,
            request.Encrypt,
            cancellationToken);

        if (execResult.IsFailure)
        {
            return BadRequest(new { error = execResult.Error.Message });
        }

        return Ok(new
        {
            execution = execResult.Value,
            encryptedEnvelopeJson = prebuiltEncryptedEnvelope
        });
    }

    /// <summary>Credenciales MQTT del dispositivo (datos de conexión + huella de la ApiKey, sin el texto plano).</summary>
    [HttpGet("{id:guid}/credentials")]
    [Authorize(Policy = AuthorizationPolicies.DeviceManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDeviceCredentials(Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetDeviceCredentialsQuery(_tenantContext.TenantId, id), cancellationToken);
        if (result.IsFailure)
        {
            return NotFound(new { error = result.Error.Message });
        }

        return Ok(result.Value);
    }

    public sealed record AssignDeviceToCustomerRequest(Guid TargetCustomerId);

    /// <summary>
    /// Reasigna el nodo dueño (Customer) del dispositivo dentro de la jerarquía (ADR-013 E8.4b).
    /// Exige el rol <see cref="AuthorizationPolicies.CustomerAdmin"/> por ser una operación de la
    /// jerarquía; el recorte por subárbol (device origen y customer destino) lo aplican la RLS y el
    /// filtro de EF, no la política de rol.
    /// </summary>
    [HttpPost("{id:guid}/customer")]
    [Authorize(Policy = AuthorizationPolicies.CustomerAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignToCustomer(Guid id, [FromBody] AssignDeviceToCustomerRequest request, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new AssignDeviceToCustomerCommand(id, request.TargetCustomerId),
            cancellationToken);
        if (result.IsFailure)
        {
            return result.Error.Code is "Device.NotFound" or "Device.CustomerNotFound"
                ? NotFound(new { error = result.Error.Message })
                : BadRequest(new { error = result.Error.Message });
        }

        return NoContent();
    }

    /// <summary>
    /// Atributos del dispositivo (ADR-014, P1b) agrupados por ámbito: Client (reportados por el equipo,
    /// sólo-lectura), Shared (estado deseado que fija el operador; push diferido por protocolo) y Server
    /// (metadato de negocio). Lectura: basta sesión válida (igual que el resto de GET de device); el
    /// recorte por subárbol/tenant lo aplican la RLS + el filtro EF, no la política de rol.
    /// </summary>
    [HttpGet("{id:guid}/attributes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDeviceAttributes(Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetDeviceAttributesQuery(_tenantContext.TenantId, id), cancellationToken);
        if (result.IsFailure)
        {
            return NotFound(new { error = result.Error.Message });
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Fija (upsert) atributos de un ámbito del dispositivo. <paramref name="scope"/> va en la ruta
    /// (<c>shared</c> o <c>server</c>); <c>client</c> se RECHAZA con 400 porque esos valores los reporta
    /// el equipo. Requiere <see cref="AuthorizationPolicies.DeviceManage"/>, coherente con el resto de
    /// mutaciones de device (registro, credenciales); el recorte por subárbol lo imponen la carga scoped
    /// del device y la RLS (<c>WITH CHECK</c>) de <c>device_attributes</c>.
    /// </summary>
    [HttpPut("{id:guid}/attributes/{scope}")]
    [Authorize(Policy = AuthorizationPolicies.DeviceManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDeviceAttributes(
        Guid id, string scope, [FromBody] Dictionary<string, string?> attributes, CancellationToken cancellationToken)
    {
        if (!TryParseScope(scope, out var parsedScope))
        {
            return BadRequest(new { error = $"Ámbito de atributo inválido: '{scope}'. Use 'shared' o 'server'." });
        }

        var result = await _mediator.Send(
            new SetDeviceAttributesCommand(id, parsedScope, attributes ?? new Dictionary<string, string?>()),
            cancellationToken);

        return result.IsSuccess ? NoContent() : MapAttributeFailure(result.Error);
    }

    /// <summary>
    /// Borra un atributo fijado por el operador (<c>shared</c>/<c>server</c>). <c>client</c> se rechaza
    /// con 400. Mismas garantías de alcance que el PUT.
    /// </summary>
    [HttpDelete("{id:guid}/attributes/{scope}/{key}")]
    [Authorize(Policy = AuthorizationPolicies.DeviceManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteDeviceAttribute(
        Guid id, string scope, string key, CancellationToken cancellationToken)
    {
        if (!TryParseScope(scope, out var parsedScope))
        {
            return BadRequest(new { error = $"Ámbito de atributo inválido: '{scope}'. Use 'shared' o 'server'." });
        }

        var result = await _mediator.Send(
            new DeleteDeviceAttributeCommand(id, parsedScope, key), cancellationToken);

        return result.IsSuccess ? NoContent() : MapAttributeFailure(result.Error);
    }

    private static bool TryParseScope(string scope, out AttributeScope parsed) =>
        Enum.TryParse(scope, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);

    private IActionResult MapAttributeFailure(IoT.Domain.Common.Error error) => error.Code switch
    {
        "Device.NotFound" or "Attributes.NotFound" => NotFound(new { error = error.Message }),
        _ => BadRequest(new { error = error.Message }),
    };

    /// <summary>Genera/rota la ApiKey MQTT del dispositivo. Devuelve el texto plano UNA sola vez.</summary>
    [HttpPost("{id:guid}/credentials/rotate")]
    [Authorize(Policy = AuthorizationPolicies.DeviceManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RotateDeviceCredentials(Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new RotateDeviceApiKeyCommand(_tenantContext.TenantId, id, User.Identity?.Name),
            cancellationToken);
        if (result.IsFailure)
        {
            return NotFound(new { error = result.Error.Message });
        }

        return Ok(result.Value);
    }

    public sealed record ImportDeviceApiKeyRequest(string ApiKey);

    /// <summary>
    /// Importa una ApiKey MQTT YA conocida (gestionada fuera de la plataforma) como credencial
    /// per-device del dispositivo, en vez de acuñar una aleatoria. Pensado para flotas cuyo
    /// firmware trae una clave de fábrica compartida: se registra cada ChipId con esa clave y el
    /// equipo autentica por la vía per-device sin tocar el firmware. Devuelve sólo la huella
    /// (nunca el texto plano). Misma política que la rotación (<see
    /// cref="AuthorizationPolicies.DeviceManage"/>).
    /// </summary>
    [HttpPost("{id:guid}/credentials/import")]
    [Authorize(Policy = AuthorizationPolicies.DeviceManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ImportDeviceCredentials(Guid id, [FromBody] ImportDeviceApiKeyRequest request, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new ImportDeviceApiKeyCommand(_tenantContext.TenantId, id, request.ApiKey, User.Identity?.Name),
            cancellationToken);
        if (result.IsFailure)
        {
            return result.Error.Code == "Device.NotFound"
                ? NotFound(new { error = result.Error.Message })
                : BadRequest(new { error = result.Error.Message });
        }

        return Ok(new { fingerprint = result.Value });
    }
}
