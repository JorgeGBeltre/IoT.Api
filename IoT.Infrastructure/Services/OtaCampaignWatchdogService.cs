using IoT.Application.Common.Interfaces;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IoT.Infrastructure.Services;

/// <summary>
/// Vigilante de campañas OTA: el "reloj de campaña" que faltaba. Cierra como FALLIDAS las
/// actualizaciones que llevan demasiado tiempo sin avanzar.
///
/// Sin esto, una campaña podía quedar <c>OtaInProgress = true</c> PARA SIEMPRE por dos vías:
/// (a) se lanzó contra un equipo que nunca respondió, o (b) el equipo completó la OTA pero su
/// reporte de éxito —que viaja en QoS 0— se perdió en el cable. En ambos casos el inventario
/// mentía ("actualizando" eterno) y, peor, el guard de arranque rechazaba relanzar la campaña.
/// La reconciliación de arranque (<see cref="OtaStartupReconciliationService"/>) cubre el
/// reinicio del proceso; este vigilante cubre el proceso EN MARCHA.
///
/// La bomba refresca la marca de actividad en cada trozo y el equipo la refresca con cada
/// reporte de progreso/ack (<c>Device.TouchOtaActivity</c>), así que una campaña viva —aunque
/// sea lenta— nunca se cierra: sólo caen las que llevan más de la ventana sin señales.
/// </summary>
public sealed class OtaCampaignWatchdogService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OtaCampaignWatchdogService> _logger;

    public OtaCampaignWatchdogService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<OtaCampaignWatchdogService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// ¿Una campaña lleva demasiado tiempo sin señales? Extraído para poder probarlo sin base
    /// de datos ni servicio en marcha. Sin marca de tiempo no se juzga (devuelve false): la
    /// reconciliación de arranque la habría cerrado si el proceso hubiera reiniciado.
    /// </summary>
    public static bool IsCampaignStale(DateTime? otaUpdatedAt, DateTime nowUtc, TimeSpan timeout)
        => otaUpdatedAt.HasValue && (nowUtc - otaUpdatedAt.Value) >= timeout;

    private TimeSpan CampaignTimeout =>
        int.TryParse(_configuration["Firmware:CampaignTimeoutMinutes"], out var m) && m > 0
            ? TimeSpan.FromMinutes(m)
            : TimeSpan.FromMinutes(15);

    private TimeSpan ScanInterval =>
        int.TryParse(_configuration["Firmware:WatchdogIntervalSeconds"], out var s) && s > 0
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Sin el vigilante la plataforma funciona; sólo quedarían campañas colgadas. Así que
        // un fallo de un ciclo no debe tumbar el servicio: se loguea y se reintenta al siguiente.
        try
        {
            using var timer = new PeriodicTimer(ScanInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // apagado ordenado
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "El vigilante de campañas OTA se detuvo por un error; las campañas colgadas dejarán de cerrarse hasta reiniciar.");
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            // El vigilante corre sin tenant ambiental y cruza tenants: sesión de sistema para
            // que bajo Platform:Rls:Enforce no se bloquee la lectura/cierre (ADR-003 D6).
            using var scope = _scopeFactory.CreateSystemScope();
            var deviceRepository = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var audit = scope.ServiceProvider.GetService<IAuditLogRepository>();

            // GetDevicesWithOtaInProgressAsync usa IgnoreQueryFilters: el vigilante corre sin
            // tenant ambiental, y con el filtro global la consulta volvería vacía EN SILENCIO
            // (misma trampa que mordió a la reconciliación de arranque).
            var inProgress = await deviceRepository.GetDevicesWithOtaInProgressAsync(ct);
            if (inProgress.Count == 0) return;

            var now = DateTime.UtcNow;
            var timeout = CampaignTimeout;
            int closed = 0;

            foreach (var device in inProgress)
            {
                // Sin marca de tiempo no se puede juzgar la antigüedad: se deja (la
                // reconciliación de arranque la habría cerrado si el proceso hubiera reiniciado).
                if (!device.OtaUpdatedAt.HasValue) continue;

                if (!IsCampaignStale(device.OtaUpdatedAt, now, timeout)) continue;
                var idle = now - device.OtaUpdatedAt!.Value;

                _logger.LogWarning(
                    "Campaña OTA de {ChipId} a {Version} cerrada por el vigilante: {Idle:n0} min sin señales del equipo (umbral {Timeout:n0} min).",
                    device.ChipId, device.OtaTargetVersion ?? "(desconocida)", idle.TotalMinutes, timeout.TotalMinutes);

                device.FailOta(
                    "La actualización se cerró por tiempo de espera: el dispositivo no confirmó ni dio señales dentro del plazo. Relánzala.");
                deviceRepository.Update(device);
                closed++;

                if (audit is not null)
                {
                    try
                    {
                        await audit.AddAsync(new Domain.Entities.AuditLog(
                            id: Guid.NewGuid(),
                            tenantId: device.TenantId,
                            userId: null,
                            username: "System.OtaWatchdog",
                            action: "FirmwareRolloutTimedOut",
                            details: $"Campaña a '{device.OtaTargetVersion}' de {device.ChipId} cerrada tras {idle.TotalMinutes:n0} min sin respuesta.",
                            ipAddress: null,
                            userAgent: "IoT.Api"), ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "No se pudo auditar el cierre por tiempo de la campaña de {ChipId}.", device.ChipId);
                    }
                }
            }

            if (closed > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
                _logger.LogInformation("Vigilante OTA: {Count} campaña(s) cerrada(s) por tiempo de espera.", closed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo en un ciclo del vigilante de campañas OTA; se reintentará en el siguiente.");
        }
    }
}
