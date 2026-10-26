namespace IoT.Application.Common.Interfaces;

/// <summary>
/// Servicio de presencia de dispositivos (B-08):
/// Detecta dispositivos cuyo estado figura como Online pero que no han comunicado
/// dentro del período de gracia especificado, transicionándolos a Offline y notificando
/// al sistema en tiempo real.
/// </summary>
public interface IDevicePresenceService
{
    Task<int> CheckAndMarkOfflineDevicesAsync(TimeSpan? defaultGracePeriod = null, CancellationToken cancellationToken = default);
}
