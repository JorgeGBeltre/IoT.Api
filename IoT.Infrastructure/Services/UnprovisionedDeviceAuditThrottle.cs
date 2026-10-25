using System.Collections.Concurrent;
using IoT.Application.Common.Interfaces;
using Microsoft.Extensions.Configuration;

namespace IoT.Infrastructure.Services;

/// <summary>
/// Implementación en memoria de <see cref="IUnprovisionedDeviceAuditThrottle"/>. Singleton: el
/// cortafuegos es del PROCESO. Acotada en tamaño para que un atacante que enumere identificadores
/// no haga crecer el diccionario sin límite: al pasar del tope se vacía (un reinicio del conteo
/// es aceptable para una señal de auditoría best-effort).
/// </summary>
public sealed class UnprovisionedDeviceAuditThrottle : IUnprovisionedDeviceAuditThrottle
{
    private readonly ConcurrentDictionary<string, DateTime> _lastAudited =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _window;
    private readonly int _maxEntries;

    public UnprovisionedDeviceAuditThrottle(IConfiguration? configuration = null)
    {
        _window = int.TryParse(configuration?["Ingestion:UnprovisionedAuditWindowMinutes"], out var m) && m > 0
            ? TimeSpan.FromMinutes(m)
            : TimeSpan.FromMinutes(60);
        _maxEntries = int.TryParse(configuration?["Ingestion:UnprovisionedAuditMaxTracked"], out var n) && n > 0
            ? n
            : 10_000;
    }

    // Para poder probarlo sin esperar una hora real.
    public UnprovisionedDeviceAuditThrottle(TimeSpan window, int maxEntries)
    {
        _window = window;
        _maxEntries = maxEntries > 0 ? maxEntries : 10_000;
    }

    public bool ShouldAudit(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return false;

        var now = DateTime.UtcNow;

        // Tope de seguridad: no crecer sin límite ante una enumeración de identificadores.
        if (_lastAudited.Count >= _maxEntries && !_lastAudited.ContainsKey(identifier))
            _lastAudited.Clear();

        bool shouldAudit = true;
        _lastAudited.AddOrUpdate(
            identifier,
            _ => now,
            (_, last) =>
            {
                if (now - last < _window)
                {
                    shouldAudit = false;
                    return last; // dentro de la ventana: no se audita ni se renueva el instante
                }
                return now;
            });

        return shouldAudit;
    }
}
