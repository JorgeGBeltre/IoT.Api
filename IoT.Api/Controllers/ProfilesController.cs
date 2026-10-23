using IoT.Protocols.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IoT.Api.Controllers;

/// <summary>
/// Exposición de los perfiles de protocolo registrados en el <see cref="IProtocolRegistry"/>.
/// Los perfiles describen qué dispositivos entiende la plataforma: fabricante, modelo,
/// transporte primario, capacidades, patrones de tópico de telemetría y comandos.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/profiles")]
public sealed class ProfilesController : ControllerBase
{
    private readonly IProtocolRegistry _registry;

    public ProfilesController(IProtocolRegistry registry)
    {
        _registry = registry;
    }

    public sealed record ProtocolProfileDto(
        string ProfileId,
        string Manufacturer,
        string Model,
        string DisplayName,
        string PrimaryTransport,
        IReadOnlyList<string> SupportedCapabilities,
        IReadOnlyList<string> TelemetryTopicPatterns,
        string CommandTopicPattern,
        string AdapterTypeName);

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetAll()
    {
        var profiles = _registry.GetAllProfiles();
        var payload = profiles
            .Select(p => new ProtocolProfileDto(
                p.ProfileId,
                p.Manufacturer,
                p.Model,
                p.DisplayName,
                p.PrimaryTransport,
                p.SupportedCapabilities.Select(c => c.ToString()).ToList(),
                p.TelemetryTopicPatterns,
                p.CommandTopicPattern,
                p.Adapter.GetType().Name))
            .ToList();

        return Ok(payload);
    }

    [HttpGet("{profileId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetById(string profileId)
    {
        var profile = _registry.GetProfile(profileId);
        if (profile is null) return NotFound();

        var dto = new ProtocolProfileDto(
            profile.ProfileId,
            profile.Manufacturer,
            profile.Model,
            profile.DisplayName,
            profile.PrimaryTransport,
            profile.SupportedCapabilities.Select(c => c.ToString()).ToList(),
            profile.TelemetryTopicPatterns,
            profile.CommandTopicPattern,
            profile.Adapter.GetType().Name);

        return Ok(dto);
    }
}
