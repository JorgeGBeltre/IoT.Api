using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IoT.Worker;

/// <summary>
/// Latido de liveness para el proceso worker (que no expone HTTP). Escribe el epoch UNIX
/// actual (segundos) en un archivo cada intervalo; el HEALTHCHECK del contenedor compara
/// ese valor con la hora actual. Si el bucle del host se cuelga, el archivo deja de
/// refrescarse y Docker marca el contenedor como unhealthy.
/// Config: Worker:HeartbeatFile (ruta), Worker:HeartbeatIntervalSeconds (por defecto 30).
/// </summary>
public sealed class HeartbeatWorker : BackgroundService
{
    private readonly string _heartbeatFile;
    private readonly TimeSpan _interval;
    private readonly ILogger<HeartbeatWorker> _logger;

    public HeartbeatWorker(IConfiguration configuration, ILogger<HeartbeatWorker> logger)
    {
        _logger = logger;
        _heartbeatFile = configuration["Worker:HeartbeatFile"] is { Length: > 0 } path
            ? path
            : Path.Combine(Path.GetTempPath(), "worker-heartbeat");
        int seconds = configuration.GetValue("Worker:HeartbeatIntervalSeconds", 30);
        _interval = TimeSpan.FromSeconds(seconds < 5 ? 5 : seconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HeartbeatWorker activo. Archivo={File}, intervalo={Interval}s.",
            _heartbeatFile, _interval.TotalSeconds);

        // Escribir uno al arrancar para que el start_period no marque unhealthy de inmediato.
        await WriteHeartbeatAsync(stoppingToken);

        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await WriteHeartbeatAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // apagado ordenado
        }
    }

    private async Task WriteHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            long epochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await File.WriteAllTextAsync(_heartbeatFile, epochSeconds.ToString(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "No se pudo escribir el latido en {File}.", _heartbeatFile);
        }
    }
}
