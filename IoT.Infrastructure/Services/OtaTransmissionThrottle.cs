using Microsoft.Extensions.Configuration;

namespace IoT.Infrastructure.Services;

/// <summary>
/// Limita cuántas transmisiones OTA corren a la vez en el proceso.
///
/// Sin esto, un rollout de grupo lanzaba una tarea en background POR DISPOSITIVO sin
/// ninguna cota: 100 equipos eran 100 bombas simultáneas publicando trozos contra el mismo
/// broker, cada una con su propio recorrido del fichero y su propia presión sobre el canal
/// acotado de ingesta. Un grupo grande podía desplazar la telemetría del resto de la flota.
///
/// Es un singleton porque el servicio de firmware es scoped: el límite tiene que ser del
/// PROCESO, no de la petición que lo originó.
/// </summary>
public sealed class OtaTransmissionThrottle : IDisposable
{
    /// <summary>Transmisiones simultáneas por defecto.</summary>
    public const int DefaultMaxConcurrent = 5;

    private readonly SemaphoreSlim _gate;

    public int MaxConcurrent { get; }

    public OtaTransmissionThrottle(IConfiguration? configuration = null)
    {
        int configured = DefaultMaxConcurrent;

        if (configuration is not null &&
            int.TryParse(configuration["Firmware:MaxConcurrentRollouts"], out var fromConfig) &&
            fromConfig > 0)
        {
            // Tope de cordura: más de 50 bombas a la vez no mejora nada y asfixia al broker.
            configured = Math.Min(fromConfig, 50);
        }

        MaxConcurrent = configured;
        _gate = new SemaphoreSlim(configured, configured);
    }

    /// <summary>Transmisiones esperando o en curso que no han liberado su hueco.</summary>
    public int Available => _gate.CurrentCount;

    /// <summary>
    /// Espera un hueco libre. Devuelve un objeto que al liberarse devuelve el hueco, así que
    /// se usa con <c>using</c>. Si se cancela mientras espera, lanza y NO consume hueco.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        return new Slot(_gate);
    }

    public void Dispose() => _gate.Dispose();

    private sealed class Slot : IDisposable
    {
        private readonly SemaphoreSlim _gate;
        private bool _released;

        public Slot(SemaphoreSlim gate) => _gate = gate;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            _gate.Release();
        }
    }
}
