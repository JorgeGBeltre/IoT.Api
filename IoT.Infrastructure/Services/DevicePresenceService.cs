using IoT.Application.Common.Interfaces;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IoT.Infrastructure.Services;

/// <summary>
/// Implementación de detección de presencia de dispositivos (B-08).
/// Escanea periódicamente todos los dispositivos en estado Online cuya última comunicación
/// supere el período de gracia configurado, actualizándolos a Offline y notificando vía SignalR.
/// </summary>
public sealed class DevicePresenceService : IDevicePresenceService
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IFleetNotificationService? _notificationService;
    private readonly ILogger<DevicePresenceService> _logger;
    private readonly IConfiguration _configuration;

    public DevicePresenceService(
        IDeviceRepository deviceRepository,
        IUnitOfWork unitOfWork,
        ILogger<DevicePresenceService> logger,
        IConfiguration configuration,
        IFleetNotificationService? notificationService = null)
    {
        _deviceRepository = deviceRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _configuration = configuration;
        _notificationService = notificationService;
    }

    public async Task<int> CheckAndMarkOfflineDevicesAsync(TimeSpan? defaultGracePeriod = null, CancellationToken cancellationToken = default)
    {
        int configuredSeconds = _configuration.GetValue("Worker:PresenceGracePeriodSeconds", 180);
        var grace = defaultGracePeriod ?? TimeSpan.FromSeconds(configuredSeconds > 10 ? configuredSeconds : 180);
        var cutoff = DateTime.UtcNow - grace;

        var staleDevices = await _deviceRepository.GetOnlineDevicesOlderThanAsync(cutoff, cancellationToken);
        if (staleDevices.Count == 0) return 0;

        _logger.LogInformation("Dispositivos sin comunicación desde antes de {Cutoff:u}: {Count}. Cambiando a Offline.", cutoff, staleDevices.Count);

        int updatedCount = 0;
        foreach (var device in staleDevices)
        {
            device.SetOperationalStatus(DeviceOperationalStatus.Offline);
            _deviceRepository.Update(device);
            updatedCount++;

            if (_notificationService != null)
            {
                try
                {
                    await _notificationService.NotifyDeviceStatusChangedAsync(
                        device.TenantId,
                        device.Id,
                        device.ChipId,
                        "Offline",
                        DateTime.UtcNow,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "No se pudo emitir notificación de presencia para dispositivo {DeviceId}.", device.Id);
                }
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Transicionados {Count} dispositivos a Offline.", updatedCount);
        return updatedCount;
    }
}
