using IoT.Domain.Entities;
using IoT.Domain.Enums;

namespace IoT.Infrastructure.Mqtt;

/// <summary>
/// Decide si una sesión MQTT ya abierta debe cerrarse. Está fuera del broker a propósito:
/// dentro hacía falta un servidor en marcha para ejercitarla, y la regla —qué cambio en el
/// registro invalida una sesión viva— es justo lo que hay que poder probar caso por caso.
/// </summary>
public static class MqttSessionRevocation
{
    /// <summary>
    /// Devuelve el motivo por el que la sesión debe cerrarse, o <c>null</c> si puede seguir.
    /// </summary>
    /// <param name="device">
    /// El dispositivo tal como está AHORA en el registro, o null si ya no existe.
    /// </param>
    /// <param name="sessionKeyFingerprint">
    /// Huella de la ApiKey con la que se abrió la sesión. Null cuando no se conoce (sesión
    /// anterior a este control): entonces no se usa como motivo, porque no poder comparar no
    /// es prueba de que la clave haya cambiado.
    /// </param>
    public static string? ReasonToRevoke(Device? device, string? sessionKeyFingerprint)
    {
        if (device is null)
            return "el dispositivo ya no está registrado";

        if (device.AdminStatus != DeviceAdminStatus.Active)
            return $"el dispositivo está en estado {device.AdminStatus}";

        // Quitarle la credencial a un equipo tiene que echarlo, igual que rotarla.
        if (!device.HasMqttApiKey)
            return "el dispositivo ya no tiene ApiKey MQTT";

        if (!string.IsNullOrEmpty(sessionKeyFingerprint) &&
            !string.IsNullOrEmpty(device.MqttApiKeyFingerprint) &&
            !string.Equals(sessionKeyFingerprint, device.MqttApiKeyFingerprint, StringComparison.Ordinal))
        {
            return "la ApiKey se rotó después de abrir la sesión";
        }

        return null;
    }
}
