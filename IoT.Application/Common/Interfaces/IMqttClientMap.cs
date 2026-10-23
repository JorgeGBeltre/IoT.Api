namespace IoT.Application.Common.Interfaces;

/// <summary>
/// Mapa de correlación entre el ChipId lógico del dispositivo y el ClientId de su
/// conexión MQTT activa. Portado del broker de referencia (RedisClientMapping):
/// permite el push dirigido server→device y el tracking de presencia.
/// </summary>
public interface IMqttClientMap
{
    Task SetOrUpdateAsync(string chipId, string clientId, TimeSpan? expiry = null, CancellationToken ct = default);
    Task<string?> GetClientIdAsync(string chipId, CancellationToken ct = default);
    Task<string?> GetChipIdByClientIdAsync(string clientId, CancellationToken ct = default);

    /// <summary>
    /// Da de baja el mapeo de <paramref name="chipId"/>. Si se indica
    /// <paramref name="expectedClientId"/>, el borrado es CONDICIONAL: sólo ocurre si el mapeo
    /// sigue apuntando a ese clientId. Esto evita que la desconexión tardía de una sesión
    /// antigua (p.ej. takeover al reconectar con el mismo ClientId) borre el mapeo de la
    /// conexión nueva y viva. Devuelve true si realmente se borró.
    /// </summary>
    Task<bool> RemoveAsync(string chipId, string? expectedClientId = null, CancellationToken ct = default);
}
