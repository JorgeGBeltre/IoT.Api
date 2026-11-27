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
    private readonly ITenantContext? _tenantContext;

    public TelemetryRetentionService(
        ITelemetryStore telemetryStore,
        IDeviceCommandRepository commandRepository,
        ILogger<TelemetryRetentionService> logger,
        ApplicationDbContext? context = null,
        ITenantContext? tenantContext = null)
    {
        _telemetryStore = telemetryStore;
        _commandRepository = commandRepository;
        _logger = logger;
        _context = context;
        _tenantContext = tenantContext;
    }

    public async Task<RetentionPurgeResult> PurgeExpiredDataAsync(
        RetentionPolicyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // La purga es inherentemente cross-tenant (recorre TenantSettings de todos los tenants
        // y purga datos globales con IgnoreQueryFilters): opera como sesión de sistema para que
        // bajo Platform:Rls:Enforce el interceptor no bloquee esas consultas sin tenant (ADR-003 D6).
        if (_tenantContext != null)
        {
            _tenantContext.IsSystemOperation = true;
        }

        var opts = options ?? new RetentionPolicyOptions();
        var now = DateTime.UtcNow;

        var positionsCutoff = now.Subtract(opts.PositionsTtl);
        var telemetryCutoff = now.Subtract(opts.TelemetryRecordsTtl);
        var commandsCutoff = now.Subtract(opts.ClosedCommandsTtl);
        var tokensCutoff = now.Subtract(opts.SecurityTokensTtl);

        _logger.LogInformation("Starting telemetry and data retention purge. Positions cutoff: {PosCutoff}, Telemetry cutoff: {TelCutoff}",
            positionsCutoff, telemetryCutoff);

        int deletedPositions = 0;
        int deletedTelemetry = 0;

        // 1. Purge per-tenant respecting custom RetentionDays so tenants with longer retention (e.g., 180-365d)
        //    are never truncated by the default global TTL.
        bool purgedPerTenant = false;
        if (_context != null)
        {
            try
            {
                var tenantSettingsByTenant = await _context.TenantSettings
                    .IgnoreQueryFilters()
                    .Where(ts => ts.RetentionDays > 0)
                    .ToDictionaryAsync(ts => ts.TenantId, ts => ts.RetentionDays, cancellationToken);

                var allTenantIds = await _context.Tenants
                    .IgnoreQueryFilters()
                    .Select(t => t.Id)
                    .ToListAsync(cancellationToken);

                foreach (var tid in tenantSettingsByTenant.Keys)
                {
                    if (!allTenantIds.Contains(tid))
                    {
                        allTenantIds.Add(tid);
                    }
                }

                foreach (var tenantId in allTenantIds)
                {
                    DateTime posCut = positionsCutoff;
                    DateTime telCut = telemetryCutoff;
                    if (tenantSettingsByTenant.TryGetValue(tenantId, out int customDays) && customDays > 0)
                    {
                        var customCutoff = now.AddDays(-customDays);
                        posCut = customCutoff;
                        telCut = customCutoff;
                    }

                    deletedPositions += await _telemetryStore.PurgePositionsOlderThanAsync(posCut, tenantId, cancellationToken);
                    deletedTelemetry += await _telemetryStore.PurgeTelemetryRecordsOlderThanAsync(telCut, tenantId, cancellationToken);
                }

                purgedPerTenant = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply per-tenant retention settings; falling back to global retention TTL.");
            }
        }

        // 2. Global fallback purge only when DbContext is absent (e.g., isolated store unit tests) or per-tenant enumeration failed
        if (!purgedPerTenant)
        {
            deletedPositions += await _telemetryStore.PurgePositionsOlderThanAsync(positionsCutoff, cancellationToken);
            deletedTelemetry += await _telemetryStore.PurgeTelemetryRecordsOlderThanAsync(telemetryCutoff, cancellationToken);
        }

        // 3. Purge closed DeviceCommands older than TTL
        int deletedCommands = await _commandRepository.PurgeClosedCommandsOlderThanAsync(commandsCutoff, cancellationToken);

        // 4. Purge expired OtpCodes and PasswordResetTokens if DB context is available
        int deletedTokens = 0;
        if (_context != null)
        {
            var expiredOtps = await _context.OtpCodes
                .IgnoreQueryFilters()
                .Where(o => o.ExpirationUtc < tokensCutoff || o.IsUsed)
                .ToListAsync(cancellationToken);

            var expiredResetTokens = await _context.PasswordResetTokens
                .IgnoreQueryFilters()
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
