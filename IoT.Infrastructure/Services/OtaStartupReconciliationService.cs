using IoT.Application.Common.Interfaces;
using IoT.Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IoT.Infrastructure.Services;

/// <summary>
/// Al arrancar el proceso, cierra las campañas OTA que se quedaron a medias.
///
/// La transmisión de trozos vive en una tarea en background de ESTE proceso. Si el servicio
/// se reinicia a mitad de un rollout —un despliegue, un OOM-kill, un `docker compose up`—,
/// esa tarea muere con él, pero el dispositivo queda con <c>OtaInProgress = true</c> en base
/// de datos PARA SIEMPRE: no hay aborto, no hay reanudación y no había ningún vigilante.
/// Consecuencias: el equipo aparece eternamente "actualizando" en la UI y, peor, el guard de
/// <c>StartDeviceOtaAsync</c> rechaza lanzarle una campaña nueva porque cree que ya tiene una
/// en curso, así que el equipo queda inservible para OTA hasta tocar la base a mano.
///
/// Se marcan como FALLIDAS en vez de intentar reanudarlas: el dispositivo descarta los trozos
/// que lleguen fuera de secuencia y aborta su propia actualización, así que lo honesto es
/// decirle al operador que hay que relanzarla.
/// </summary>
public sealed class OtaStartupReconciliationService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OtaStartupReconciliationService> _logger;

    public OtaStartupReconciliationService(
        IServiceScopeFactory scopeFactory,
        ILogger<OtaStartupReconciliationService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var deviceRepository = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var stuck = await deviceRepository.GetDevicesWithOtaInProgressAsync(cancellationToken);
            if (stuck.Count == 0) return;

            foreach (var device in stuck)
            {
                _logger.LogWarning(
                    "El dispositivo {ChipId} quedó con una actualización a {Version} en curso tras un reinicio del servicio; se marca fallida para que pueda relanzarse.",
                    device.ChipId, device.OtaTargetVersion ?? "(desconocida)");

                device.FailOta(
                    "El servicio se reinició durante la actualización. La transmisión se interrumpió y hay que relanzarla.");
                deviceRepository.Update(device);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Reconciliación de arranque: {Count} campaña(s) OTA interrumpida(s) cerradas.", stuck.Count);
        }
        catch (Exception ex)
        {
            // Nunca impedir el arranque del servicio por esto: sin la reconciliación la
            // plataforma funciona, sólo quedan equipos marcados como "actualizando".
            _logger.LogError(ex, "No se pudo reconciliar el estado OTA al arrancar.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
