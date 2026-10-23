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
    Task RemoveAsync(string chipId, CancellationToken ct = default);
}
