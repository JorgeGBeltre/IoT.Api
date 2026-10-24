namespace IoT.Application.Common.Interfaces;

/// <summary>
/// Cierra la sesión MQTT de un dispositivo.
///
/// Hace falta porque el broker sólo comprobaba las credenciales en el CONNECT: una vez
/// dentro, la sesión y sus suscripciones sobrevivían indefinidamente a que el equipo fuese
/// suspendido o a que se le rotara la ApiKey. El gateway TCP sí re-validaba cada 60 segundos
/// (<c>MultiProtocolTcpGatewayService.AuthRecheck</c>); el de MQTT no hacía nada, así que
/// revocar una credencial no revocaba el acceso hasta que el equipo decidiera reconectar.
/// </summary>
public interface IMqttSessionRevoker
{
    /// <summary>
    /// Desconecta la sesión del dispositivo indicado y limpia su mapeo. Devuelve true si
    /// había una sesión que cerrar. No lanza: revocar es una acción de saneamiento y no debe
    /// tumbar la operación que la pidió (p.ej. rotar la credencial).
    /// </summary>
    Task<bool> RevokeSessionAsync(string chipId, string reason, CancellationToken cancellationToken = default);
}
