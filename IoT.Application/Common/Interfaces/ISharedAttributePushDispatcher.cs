using IoT.Domain.Entities;

namespace IoT.Application.Common.Interfaces;

/// <summary>
/// Resultado del intento de envío (push) de atributos compartidos al equipo.
/// </summary>
public sealed record SharedAttributePushResult(
    bool Dispatched,
    string Transport,
    string? Target = null,
    string? Error = null);

/// <summary>
/// Despacha la sincronización de atributos compartidos (estado deseado, ADR-015 P1c)
/// hacia el dispositivo físico según su protocolo y canal de transporte.
/// </summary>
public interface ISharedAttributePushDispatcher
{
    Task<SharedAttributePushResult> PushSharedAttributesAsync(
        Device device,
        IReadOnlyDictionary<string, string?> attributes,
        CancellationToken cancellationToken = default);
}
