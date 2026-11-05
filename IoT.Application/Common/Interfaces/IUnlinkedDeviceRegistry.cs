namespace IoT.Application.Common.Interfaces;

/// <summary>
/// Tombstone temporal de desvinculación de cuenta para dispositivos que se encontraban
/// sin Wi-Fi o apagados en el instante en que el usuario los eliminó desde la app o la web.
/// Permite que el broker MQTT embebido responda <c>CONNACK 0x87 (NotAuthorized)</c> con
/// <c>ACCOUNT_UNLINKED:&lt;UnlinkProof&gt;</c> cuando el equipo recupere conexión, de modo
/// que el firmware verifique criptográficamente la orden y entre en Quick Pairing de fábrica.
/// </summary>
public sealed record UnlinkedDeviceTombstone(
    string ChipId,
    Guid TenantId,
    string? MqttApiKeyHash,
    string UnlinkProof,
    DateTime UnlinkedAt);

public interface IUnlinkedDeviceRegistry
{
    Task RecordUnlinkedAsync(UnlinkedDeviceTombstone tombstone, CancellationToken cancellationToken = default);
    Task<UnlinkedDeviceTombstone?> GetAsync(string chipId, CancellationToken cancellationToken = default);
    Task ClearAsync(string chipId, CancellationToken cancellationToken = default);
}

