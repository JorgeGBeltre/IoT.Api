using IoT.Application.Common.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IoT.Worker;

public sealed class DataRetentionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DataRetentionWorker> _logger;

    public DataRetentionWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<DataRetentionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DataRetentionWorker started.");

        int intervalHours = int.TryParse(_configuration["Retention:IntervalHours"], out var h) && h > 0 ? h : 24;
        var interval = TimeSpan.FromHours(intervalHours);

        // Initial delay so we don't slow down cold startup
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Running scheduled telemetry & data retention cleanup...");

                using (var scope = _scopeFactory.CreateScope())
                {
                    var retentionService = scope.ServiceProvider.GetRequiredService<ITelemetryRetentionService>();
                    var options = new RetentionPolicyOptions
                    {
                        PositionsTtl = TimeSpan.FromDays(int.TryParse(_configuration["Retention:PositionsDays"], out var pd) ? pd : 90),
                        TelemetryRecordsTtl = TimeSpan.FromDays(int.TryParse(_configuration["Retention:TelemetryDays"], out var td) ? td : 90),
                        ClosedCommandsTtl = TimeSpan.FromDays(int.TryParse(_configuration["Retention:ClosedCommandsDays"], out var cd) ? cd : 30),
                        SecurityTokensTtl = TimeSpan.FromHours(int.TryParse(_configuration["Retention:SecurityTokensHours"], out var sh) ? sh : 24)
                    };

                    var result = await retentionService.PurgeExpiredDataAsync(options, stoppingToken);
                    _logger.LogInformation("Scheduled retention cleanup finished. Purged {Positions} positions, {Records} telemetry records, {Commands} closed commands.",
                        result.DeletedPositions, result.DeletedTelemetryRecords, result.DeletedClosedCommands);
                }

                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error executing scheduled data retention cleanup. Retrying in 1 hour.");
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
    }
}
