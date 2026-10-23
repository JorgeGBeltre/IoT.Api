namespace IoT.Application.Common.Interfaces;

public sealed record RetentionPolicyOptions
{
    public TimeSpan PositionsTtl { get; init; } = TimeSpan.FromDays(90);
    public TimeSpan TelemetryRecordsTtl { get; init; } = TimeSpan.FromDays(90);
    public TimeSpan ClosedCommandsTtl { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan SecurityTokensTtl { get; init; } = TimeSpan.FromDays(1);
}

public sealed record RetentionPurgeResult(
    int DeletedPositions,
    int DeletedTelemetryRecords,
    int DeletedClosedCommands,
    int DeletedSecurityTokens,
    DateTime PurgedAt);

public interface ITelemetryRetentionService
{
    Task<RetentionPurgeResult> PurgeExpiredDataAsync(RetentionPolicyOptions? options = null, CancellationToken cancellationToken = default);
}
