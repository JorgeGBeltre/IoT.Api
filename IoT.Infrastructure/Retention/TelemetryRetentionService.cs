using IoT.Application.Common.Interfaces;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IoT.Infrastructure.Retention;

public sealed class TelemetryRetentionService : ITelemetryRetentionService
{
    private readonly ITelemetryStore _telemetryStore;
    private readonly IDeviceCommandRepository _commandRepository;
    private readonly ApplicationDbContext? _context;
    private readonly ILogger<TelemetryRetentionService> _logger;

    public TelemetryRetentionService(
        ITelemetryStore telemetryStore,
        IDeviceCommandRepository commandRepository,
        ILogger<TelemetryRetentionService> logger,
        ApplicationDbContext? context = null)
    {
        _telemetryStore = telemetryStore;
        _commandRepository = commandRepository;
        _logger = logger;
        _context = context;
    }

    public async Task<RetentionPurgeResult> PurgeExpiredDataAsync(
        RetentionPolicyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetentionPolicyOptions();
        var now = DateTime.UtcNow;

        var positionsCutoff = now.Subtract(opts.PositionsTtl);
        var telemetryCutoff = now.Subtract(opts.TelemetryRecordsTtl);
        var commandsCutoff = now.Subtract(opts.ClosedCommandsTtl);
        var tokensCutoff = now.Subtract(opts.SecurityTokensTtl);

        _logger.LogInformation("Starting telemetry and data retention purge. Positions cutoff: {PosCutoff}, Telemetry cutoff: {TelCutoff}",
            positionsCutoff, telemetryCutoff);

        // 1. Purge Positions older than TTL
        int deletedPositions = await _telemetryStore.PurgePositionsOlderThanAsync(positionsCutoff, cancellationToken);

        // 2. Purge Telemetry Records older than TTL
        int deletedTelemetry = await _telemetryStore.PurgeTelemetryRecordsOlderThanAsync(telemetryCutoff, cancellationToken);

        // 3. Purge closed DeviceCommands older than TTL
        int deletedCommands = await _commandRepository.PurgeClosedCommandsOlderThanAsync(commandsCutoff, cancellationToken);

        // 4. Purge expired OtpCodes and PasswordResetTokens if DB context is available
        int deletedTokens = 0;
        if (_context != null)
        {
            var expiredOtps = await _context.OtpCodes
                .Where(o => o.ExpirationUtc < tokensCutoff || o.IsUsed)
                .ToListAsync(cancellationToken);

            var expiredResetTokens = await _context.PasswordResetTokens
                .Where(r => r.ExpirationUtc < tokensCutoff || r.IsUsed)
                .ToListAsync(cancellationToken);

            deletedTokens = expiredOtps.Count + expiredResetTokens.Count;
            if (expiredOtps.Count > 0) _context.OtpCodes.RemoveRange(expiredOtps);
            if (expiredResetTokens.Count > 0) _context.PasswordResetTokens.RemoveRange(expiredResetTokens);

            await _context.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation("Retention purge complete: {DeletedPos} positions, {DeletedTel} telemetry records, {DeletedCmd} closed commands, {DeletedTok} expired tokens removed.",
            deletedPositions, deletedTelemetry, deletedCommands, deletedTokens);

        return new RetentionPurgeResult(
            deletedPositions,
            deletedTelemetry,
            deletedCommands,
            deletedTokens,
            now);
    }
}
