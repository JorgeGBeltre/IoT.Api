using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using IoT.Protocols;
using IoT.Protocols.Contracts;
using MediatR;
using Microsoft.Extensions.Logging;

namespace IoT.Application.Ingestion.Commands;

/// <summary>
/// Ingesta de telemetría. <paramref name="TransportProtocol"/> es opcional: cuando se
/// indica (p.ej. "TCP-QUECLINK" desde el gateway multi-protocolo), el decode se enruta por
/// ese hint vía CanHandle del adaptador, en vez de depender del patrón de tópico.
/// </summary>
public sealed record IngestTelemetryCommand(string Topic, string Payload, string Source = "real", string? TransportProtocol = null) : IRequest<Result>;

public sealed class IngestTelemetryCommandHandler : IRequestHandler<IngestTelemetryCommand, Result>
{
    private readonly ICompositeProtocolAdapter _protocolAdapter;
    private readonly IDeviceRepository _deviceRepository;
    private readonly ITenantRepository _tenantRepository;
    private readonly IStateCacheService _stateCache;
    private readonly ITelemetryStore _telemetryStore;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<IngestTelemetryCommandHandler> _logger;
    private readonly IFleetNotificationService? _notificationService;
    private readonly ITenantContext? _tenantContext;
    private readonly IAuditLogRepository? _auditLogRepository;

    public IngestTelemetryCommandHandler(
        ICompositeProtocolAdapter protocolAdapter,
        IDeviceRepository deviceRepository,
        ITenantRepository tenantRepository,
        IStateCacheService stateCache,
        ITelemetryStore telemetryStore,
        IUnitOfWork unitOfWork,
        ILogger<IngestTelemetryCommandHandler> logger,
        IFleetNotificationService? notificationService = null,
        ITenantContext? tenantContext = null,
        IAuditLogRepository? auditLogRepository = null)
    {
        _protocolAdapter = protocolAdapter;
        _deviceRepository = deviceRepository;
        _tenantRepository = tenantRepository;
        _stateCache = stateCache;
        _telemetryStore = telemetryStore;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _notificationService = notificationService;
        _tenantContext = tenantContext;
        _auditLogRepository = auditLogRepository;
    }

    public async Task<Result> Handle(IngestTelemetryCommand request, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Common.Observability.IoTMetrics.TelemetryReceivedCounter.Add(
            1,
            new KeyValuePair<string, object?>("topic", request.Topic),
            new KeyValuePair<string, object?>("source", request.Source));

        try
        {
            // 1. Decode payload via Protocol Adapter. Con hint de transporte (gateway TCP
            //    multi-protocolo) se enruta por TransportProtocol; si no, por tópico/payload.
            var decodeResult = string.IsNullOrEmpty(request.TransportProtocol)
                ? _protocolAdapter.Decode(request.Topic, request.Payload)
                : _protocolAdapter.Decode(TransportMessage.Create(
                    topicOrRoute: request.Topic,
                    payload: request.Payload,
                    transportProtocol: request.TransportProtocol,
                    source: request.Source));
            if (decodeResult.IsFailure)
            {
                _logger.LogWarning("Failed to decode telemetry on topic '{Topic}': {Error}", request.Topic, decodeResult.Error.Message);
                return decodeResult;
            }

            var ev = decodeResult.Value;

            // 2. Cryptographic MessageId & Deduplication check (E2-03)
            string messageId = ev.MessageId ?? ComputeCryptographicMessageId(ev.DeviceIdentifier, ev.RecordedAt, ev.RawPayload);
            bool isDuplicate = await _stateCache.IsDuplicateMessageAsync(ev.DeviceIdentifier, ev.RecordedAt, messageId, TimeSpan.FromMinutes(30), cancellationToken);
            if (isDuplicate)
            {
                Common.Observability.IoTMetrics.TelemetryDeduplicatedCounter.Add(1);
                _logger.LogDebug("Duplicate message discarded for device {DeviceIdentifier} with MessageId {MessageId} at {RecordedAt}",
                    ev.DeviceIdentifier, messageId, ev.RecordedAt);
                return Result.Success();
            }

        // 3. Resolve device
        var device = await _deviceRepository.GetByChipIdGlobalAsync(ev.DeviceIdentifier, cancellationToken);
        if (device is null)
        {
            _logger.LogWarning("Telemetry received from unprovisioned device '{DeviceIdentifier}' on topic '{Topic}'.",
                ev.DeviceIdentifier, request.Topic);

            // E2-05: Audit unknown device discovery without creating silent fake tenants
            if (_auditLogRepository != null)
            {
                var auditLog = new AuditLog(
                    id: Guid.NewGuid(),
                    tenantId: Guid.Empty,
                    userId: null,
                    username: "System.Ingestion",
                    action: "DeviceDiscovered",
                    details: $"Unprovisioned device '{ev.DeviceIdentifier}' detected on topic '{request.Topic}'. Telemetry rejected.",
                    ipAddress: null,
                    userAgent: "IoT.Worker");

                await _auditLogRepository.AddAsync(auditLog, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return Result.Failure(new Error("Ingestion.DeviceNotProvisioned",
                $"Device with ChipId '{ev.DeviceIdentifier}' is not provisioned in the system. Telemetry rejected."));
        }

        // E1-04: Bind the resolved device's tenant to the ambient tenant context
        if (_tenantContext != null)
        {
            _tenantContext.TenantId = device.TenantId;
        }

        if (device.AdminStatus != DeviceAdminStatus.Active)
        {
            _logger.LogWarning("Device {ChipId} is suspended or revoked. Rejecting telemetry.", device.ChipId);
            return Result.Failure(new Error("Ingestion.DeviceNotActive", $"Device '{device.ChipId}' is not active."));
        }

        // E1-05: Guard clause against clock skew attacks (> 5 min future) and stale timestamps (> 7 days)
        var nowUtc = DateTime.UtcNow;
        if (ev.RecordedAt > nowUtc.AddMinutes(5))
        {
            _logger.LogWarning("Rejecting telemetry for device {ChipId}: recorded time {RecordedAt} is too far in the future (> 5 min skew).",
                device.ChipId, ev.RecordedAt);
            return Result.Failure(new Error("Ingestion.ClockSkewTooFarInFuture", "RecordedAt is too far in the future."));
        }

        bool isStaleHistorical = ev.RecordedAt < nowUtc.AddDays(-7);
        if (isStaleHistorical)
        {
            _logger.LogInformation("Telemetry for device {ChipId} has a stale timestamp {RecordedAt} (> 7 days old). Persisting historical record without updating live cache.",
                device.ChipId, ev.RecordedAt);
        }

        // 4. Update live position snapshot in Redis with out-of-order protection (skip if stale historical)
        if (ev.Position != null)
        {
            if (!isStaleHistorical)
            {
                bool updatedInCache = await _stateCache.TryUpdateLatestPositionAsync(
                    device.TenantId,
                    device.Id,
                    device.ChipId,
                    ev.Position,
                    ev.RecordedAt,
                    cancellationToken);
            }

            // 5. Store position in PostgreSQL historical table with MessageId
            await _telemetryStore.StorePositionAsync(
                device.TenantId,
                device.Id,
                ev.RecordedAt,
                ev.ReceivedAt,
                ev.Position,
                version: 1,
                source: request.Source,
                messageId: messageId,
                cancellationToken: cancellationToken);
        }

        // 6. Store numeric metrics in JSONB historical table with MessageId
        if (ev.NumericMetrics.Count > 0)
        {
            await _telemetryStore.StoreTelemetryMetricsAsync(
                device.TenantId,
                device.Id,
                ev.RecordedAt,
                ev.ReceivedAt,
                ev.NumericMetrics,
                ev.RawPayload,
                source: request.Source,
                messageId: messageId,
                cancellationToken: cancellationToken);
        }

        // 7. Update device last seen
        device.RecordTelemetry(ev.RecordedAt);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // 8. Real-time fleet notifications (SignalR stream)
        if (_notificationService != null)
        {
            try
            {
                if (ev.Position != null)
                {
                    await _notificationService.NotifyPositionUpdatedAsync(
                        device.TenantId,
                        device.Id,
                        device.ChipId,
                        device.Name,
                        ev.Position.Latitude,
                        ev.Position.Longitude,
                        ev.Position.SpeedKph,
                        ev.Position.Heading,
                        ev.RecordedAt,
                        cancellationToken);
                }

                if (ev.NumericMetrics.Count > 0)
                {
                    await _notificationService.NotifyTelemetryReceivedAsync(
                        device.TenantId,
                        device.Id,
                        device.ChipId,
                        ev.NumericMetrics,
                        ev.RecordedAt,
                        cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast notification for device {ChipId}", device.ChipId);
            }
        }

        _logger.LogInformation("Telemetry processed successfully for device {ChipId} on topic '{Topic}'", device.ChipId, request.Topic);
        return Result.Success();
        }
        finally
        {
            Common.Observability.IoTMetrics.TelemetryIngestionDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private static string ComputeCryptographicMessageId(string deviceId, DateTime recordedAt, string? rawPayload)
    {
        string raw = $"{deviceId}:{recordedAt.Ticks}:{rawPayload ?? string.Empty}";
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
