using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IoT.Api.Controllers;

/// <summary>
/// Health y diagnóstico operativo real. Lee del <see cref="HealthCheckService"/>
/// que se registra en Program.cs (checks de PostgreSQL y Redis).
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/diagnostics")]
public sealed class DiagnosticsController : ControllerBase
{
    private readonly HealthCheckService _healthCheckService;

    public DiagnosticsController(HealthCheckService healthCheckService)
    {
        _healthCheckService = healthCheckService;
    }

    public sealed record HealthEntryDto(
        string Name,
        string Status,
        double DurationMs,
        string? Description,
        IDictionary<string, object>? Data);

    public sealed record DiagnosticsReportDto(
        string OverallStatus,
        double TotalDurationMs,
        DateTime CheckedAt,
        IReadOnlyList<HealthEntryDto> Checks);

    [HttpGet("health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetHealth(CancellationToken ct)
    {
        var report = await _healthCheckService.CheckHealthAsync(ct);

        var checks = report.Entries.Select(kvp => new HealthEntryDto(
            kvp.Key,
            kvp.Value.Status.ToString(),
            kvp.Value.Duration.TotalMilliseconds,
            kvp.Value.Description,
            kvp.Value.Data?.ToDictionary(d => d.Key, d => d.Value)
        )).ToList();

        var dto = new DiagnosticsReportDto(
            report.Status.ToString(),
            report.TotalDuration.TotalMilliseconds,
            DateTime.UtcNow,
            checks);

        var statusCode = report.Status switch
        {
            HealthStatus.Healthy => StatusCodes.Status200OK,
            HealthStatus.Degraded => StatusCodes.Status200OK,
            HealthStatus.Unhealthy => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status200OK
        };

        return StatusCode(statusCode, dto);
    }
}
