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
///
/// <paramref name="TransportDeviceIdentifier"/> es la identidad YA AUTENTICADA por el
/// transporte: el ChipId que el broker MQTT verificó contra el hash de la ApiKey del
/// dispositivo al aceptar la conexión (o el IMEI que el gateway TCP validó contra el
/// registro). Sólo debe venir informada cuando la credencial es POR DISPOSITIVO; con una
/// ApiKey global de bootstrap (simulador) el publicador puede hablar por varios equipos y
/// no hay identidad única que propagar. Cuando viene, manda sobre la que se deduzca del
/// tópico o del payload, que no están autenticados: sin esto, una trama con el tópico fijo
/// (telemetry/accidents, ota/&lt;dev&gt;/progress, device_switch1_state…) se atribuía a un
/// dispositivo inventado a partir del último segmento del tópico y se rechazaba por "no
/// aprovisionado", perdiendo la trama aunque la plataforma SÍ conocía al emisor.
/// </summary>
public sealed record IngestTelemetryCommand(
    string Topic,
    string Payload,
    string Source = "real",
    string? TransportProtocol = null,
    string? TransportDeviceIdentifier = null,
    string? TransportMessageId = null) : IRequest<Result>;

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

            // 1b. Identidad: la autenticada por el transporte manda sobre la deducida del
            //     tópico/payload (que no está autenticada). Ver nota en el record.
            if (!string.IsNullOrWhiteSpace(request.TransportDeviceIdentifier))
            {
                if (string.IsNullOrWhiteSpace(ev.DeviceIdentifier) ||
                    IsPlaceholderIdentifier(ev.DeviceIdentifier))
                {
                    _logger.LogDebug(
                        "Identidad no deducible del tópico '{Topic}'; se usa la autenticada por el transporte: {ChipId}",
                        request.Topic, request.TransportDeviceIdentifier);
                }
                else if (!string.Equals(ev.DeviceIdentifier, request.TransportDeviceIdentifier, StringComparison.OrdinalIgnoreCase))
                {
                    // El payload declara un equipo distinto al que se autenticó. Es un
                    // intento de suplantación entre tenants o un firmware mal configurado:
                    // se registra y se impone la identidad autenticada.
                    _logger.LogWarning(
                        "La trama en '{Topic}' declara el dispositivo '{Declared}' pero la conexión se autenticó como '{Authenticated}'. Se usa la autenticada.",
                        request.Topic, ev.DeviceIdentifier, request.TransportDeviceIdentifier);
                }

                ev.DeviceIdentifier = request.TransportDeviceIdentifier!;
            }

            if (string.IsNullOrWhiteSpace(ev.DeviceIdentifier) || IsPlaceholderIdentifier(ev.DeviceIdentifier))
            {
                // Sin identidad fiable no se puede resolver tenant ni dispositivo. Se rechaza
                // SIN auditar como "dispositivo descubierto": antes se fabricaba un ChipId
                // con el último segmento del tópico y cada trama creaba una fila de AuditLog
                // para un equipo fantasma ('progress', 'status', 'accidents'…).
                _logger.LogWarning(
                    "Trama descartada en '{Topic}': no se pudo determinar la identidad del dispositivo.",
                    request.Topic);
                return Result.Failure(new Error("Ingestion.UnidentifiedDevice",
                    $"Could not determine the device identity for topic '{request.Topic}'. Telemetry rejected."));
            }

            // 1c. Saneado de secretos ANTES de hashear y persistir. Algunos firmwares
            //     mandan credenciales en la trama (Fligo incluye la contraseña WiFi en
            //     claro) y tanto los atributos como el RawPayload se guardan en base y se
            //     devuelven en el histórico.
            IoT.Protocols.Security.TelemetryRedaction.RedactAttributes(ev.Attributes);
            ev.RawPayload = IoT.Protocols.Security.TelemetryRedaction.RedactPayload(ev.RawPayload);

            // 2. Cryptographic MessageId & Deduplication check (E2-03)
            if (string.IsNullOrWhiteSpace(ev.MessageId) && !string.IsNullOrWhiteSpace(request.TransportMessageId))
            {
                ev.MessageId = request.TransportMessageId;
            }

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

    /// <summary>
    /// Identificadores que NO son un dispositivo: marcadores de relleno que los adaptadores
    /// usan cuando no logran extraer la identidad. Tratarlos como ChipId creaba equipos
    /// fantasma y una fila de auditoría por trama.
    /// </summary>
    private static bool IsPlaceholderIdentifier(string identifier) =>
        identifier.Equals("unknown-device", StringComparison.OrdinalIgnoreCase) ||
        identifier.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
        identifier.Equals("unidentified", StringComparison.OrdinalIgnoreCase);

    private static string ComputeCryptographicMessageId(string deviceId, DateTime recordedAt, string? rawPayload)
    {
        string raw = $"{deviceId}:{recordedAt.Ticks}:{rawPayload ?? string.Empty}";
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
