using IoT.Application.Common.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IoT.Worker;

/// <summary>
/// Worker de presencia de dispositivos (B-08):
/// Detecta dispositivos cuyo estado figura como Online pero que no han comunicado
/// dentro del período de gracia (por defecto 180 segundos). Los transiciona a Offline
/// de manera periódica y segura en la base de datos y difunde la actualización en tiempo real.
/// </summary>
public sealed class DevicePresenceWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DevicePresenceWorker> _logger;

    public DevicePresenceWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<DevicePresenceWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DevicePresenceWorker iniciado.");

        int intervalSeconds = _configuration.GetValue("Worker:PresenceCheckIntervalSeconds", 30);
        int graceSeconds = _configuration.GetValue("Worker:PresenceGracePeriodSeconds", 180);
        var interval = TimeSpan.FromSeconds(intervalSeconds < 5 ? 5 : intervalSeconds);
        var grace = TimeSpan.FromSeconds(graceSeconds < 10 ? 10 : graceSeconds);

        // Retardo inicial breve para permitir que la base de datos termine su inicialización
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using (var scope = _scopeFactory.CreateScope())
                {
                    var presenceService = scope.ServiceProvider.GetRequiredService<IDevicePresenceService>();
                    int markedOffline = await presenceService.CheckAndMarkOfflineDevicesAsync(grace, stoppingToken);
                    if (markedOffline > 0)
                    {
                        _logger.LogInformation("DevicePresenceWorker: Se transicionaron {Count} dispositivos a Offline.", markedOffline);
                    }
                }

                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en el ciclo de verificación de presencia de dispositivos.");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }
}
