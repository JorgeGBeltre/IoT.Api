using System.Collections.Concurrent;

namespace IoT.Infrastructure.Services;

/// <summary>
/// Garantiza COMO MUCHO UNA bomba de transmisión OTA viva por dispositivo en este proceso.
///
/// La reclamación atómica de la campaña (DeviceRepository.TryStartOtaCampaignAsync) ya impide
/// que dos peticiones concurrentes lancen dos bombas a la vez. Queda un caso residual: si una
/// bomba se quedó colgada y su campaña caducó (más de la ventana de rescate sin avanzar), una
/// nueva reclamación legítima arranca una segunda bomba mientras la primera sigue viva. Este
/// registro cierra ese hueco: al arrancar una campaña nueva para un dispositivo, CANCELA la
/// anterior. Es singleton (el límite es del PROCESO; el servicio OTA es scoped).
///
/// El token entregado está ligado al apagado ordenado, así que detener el servicio también
/// cancela todas las bombas.
/// </summary>
public sealed class OtaCampaignRegistry : IDisposable
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();
    private bool _disposed;

    /// <summary>
    /// Reclama la bomba para un dispositivo. Cancela la bomba anterior de ese dispositivo (si
    /// la hay) y devuelve el token con el que debe correr la nueva. El token se cancela al
    /// apagar el servicio (<paramref name="shutdownToken"/>) o cuando otra campaña posterior
    /// del mismo dispositivo la sustituya.
    /// </summary>
    public CancellationToken StartCampaign(Guid deviceId, CancellationToken shutdownToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);

        // Sustituye la entrada y cancela la anterior. AddOrUpdate hace la sustitución atómica;
        // la anterior se cancela y libera fuera del factory para no hacerlo bajo el lock.
        CancellationTokenSource? previous = null;
        _active.AddOrUpdate(
            deviceId,
            _ => cts,
            (_, existing) => { previous = existing; return cts; });

        if (previous is not null)
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
            previous.Dispose();
        }

        // Si el registro ya se está apagando, no dejes una campaña viva colgada.
        if (_disposed)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        return cts.Token;
    }

    /// <summary>
    /// Retira la campaña de un dispositivo al terminar su bomba, SÓLO si sigue siendo la suya
    /// (identificada por su token): así no pisa una campaña más nueva que ya la sustituyó.
    /// </summary>
    public void EndCampaign(Guid deviceId, CancellationToken token)
    {
        if (_active.TryGetValue(deviceId, out var cts) && cts.Token == token)
        {
            if (_active.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(deviceId, cts)))
                cts.Dispose();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var kv in _active)
        {
            try { kv.Value.Cancel(); } catch (ObjectDisposedException) { }
            kv.Value.Dispose();
        }
        _active.Clear();
    }
}
