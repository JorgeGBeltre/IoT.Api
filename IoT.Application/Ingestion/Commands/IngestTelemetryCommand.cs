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
/// <remarks>
/// <paramref name="CredentialTenantId"/>: el tenant al que está ACOTADA la credencial del
/// transporte, cuando la hay. Sólo lo informa el broker MQTT para una credencial de
/// bootstrap (multi-equipo) resuelta contra el tenant dueño; una credencial POR DISPOSITIVO
/// no lo necesita, porque <paramref name="TransportDeviceIdentifier"/> ya fija la identidad
/// exacta. Cuando viene informado, la ingesta RECHAZA toda trama cuyo dispositivo resuelto
/// pertenezca a otro tenant (cierra MEDIUM-2: con la antigua clave global, sin tenant, se
/// inyectaba telemetría atribuida a equipos de cualquier tenant). null = sin acotar por
/// credencial (HTTP, webhook, gateways TCP, worker), que tienen su propia identidad y no
/// deben verse afectados por esta comprobación.
/// </remarks>
public sealed record IngestTelemetryCommand(
    string Topic,
    string Payload,
    string Source = "real",
    string? TransportProtocol = null,
    string? TransportDeviceIdentifier = null,
    string? TransportMessageId = null,
    Guid? CredentialTenantId = null) : IRequest<Result>;

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
    private readonly IFirmwareOtaService? _firmwareOtaService;
    private readonly IDeviceCommandRepository? _commandRepository;
    private readonly IUnprovisionedDeviceAuditThrottle? _unprovisionedAuditThrottle;

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
        IAuditLogRepository? auditLogRepository = null,
        IFirmwareOtaService? firmwareOtaService = null,
        IDeviceCommandRepository? commandRepository = null,
        IUnprovisionedDeviceAuditThrottle? unprovisionedAuditThrottle = null)
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
        _firmwareOtaService = firmwareOtaService;
        _commandRepository = commandRepository;
        _unprovisionedAuditThrottle = unprovisionedAuditThrottle;
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

        // 3. Resolve device.
        // La resolución global por ChipId y la auditoría de equipos no aprovisionados corren SIN
        // tenant (el dispositivo aún no está mapeado a ninguno). Se marca operación de sistema
        // para que, bajo Platform:Rls:Enforce, el interceptor permita esa lectura/escritura
        // cross-tenant en vez de bloquear la conexión (ADR-003 D6). Se desactiva en cuanto se
        // fija el tenant del dispositivo, unas líneas más abajo.
        if (_tenantContext != null)
        {
            _tenantContext.IsSystemOperation = true;
        }

        var device = await _deviceRepository.GetByChipIdGlobalAsync(ev.DeviceIdentifier, cancellationToken);
        if (device is null)
        {
            _logger.LogWarning("Telemetry received from unprovisioned device '{DeviceIdentifier}' on topic '{Topic}'.",
                ev.DeviceIdentifier, request.Topic);

            // E2-05: Audit unknown device discovery without creating silent fake tenants.
            // LOW-3: una fila por trama era amplificación de escritura (una conexión sin
            // identidad de transporte podía llenar la tabla y presionar PostgreSQL). Se
            // audita como mucho una vez por identificador y por ventana; si no hay throttle
            // (tests antiguos), se conserva el comportamiento de auditar.
            bool shouldAudit = _unprovisionedAuditThrottle?.ShouldAudit(ev.DeviceIdentifier) ?? true;
            if (_auditLogRepository != null && shouldAudit)
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

        // MEDIUM-2: si la trama llegó con una credencial ACOTADA A UN TENANT (credencial de
        // bootstrap MQTT), el dispositivo resuelto TIENE que ser de ese tenant. Va AQUÍ, antes
        // de fijar el contexto de tenant unas líneas más abajo: si se comprobara después, se
        // compararía el tenant de la víctima contra sí mismo y pasaría siempre. No se escribe
        // NADA (ni auditoría ni SaveChanges) para no reabrir la amplificación de escritura, y
        // el mensaje externo es el MISMO que el de "no aprovisionado" para no dar un oráculo de
        // qué dispositivos existen en otros tenants; el código interno y el log sí distinguen.
        if (request.CredentialTenantId.HasValue && device.TenantId != request.CredentialTenantId.Value)
        {
            _logger.LogWarning(
                "Trama RECHAZADA por tenant cruzado: credencial del tenant {CredentialTenant} intentó publicar como el dispositivo {ChipId} del tenant {DeviceTenant}.",
                request.CredentialTenantId.Value, device.ChipId, device.TenantId);

            return Result.Failure(new Error("Ingestion.DeviceNotProvisioned",
                $"Device with ChipId '{ev.DeviceIdentifier}' is not provisioned in the system. Telemetry rejected."));
        }

        // E1-04: Bind the resolved device's tenant to the ambient tenant context.
        // Desde aquí las escrituras (telemetría, posiciones, comandos) quedan RLS-scoped al
        // tenant del dispositivo: se desactiva la operación de sistema abierta para la resolución.
        if (_tenantContext != null)
        {
            _tenantContext.TenantId = device.TenantId;
            _tenantContext.IsSystemOperation = false;
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

        // 9. Procesamiento de reportes y callbacks OTA emitidos por dispositivos MQTT.
        //    Un reporte OTA CAMBIA el estado de la campaña (la marca completada o fallida),
        //    así que sólo se acepta de una conexión cuya identidad esté AUTENTICADA por el
        //    transporte (ApiKey por dispositivo) y que coincida con el equipo resuelto. Con
        //    una credencial global de bootstrap cualquiera podría cerrar la OTA de otro.
        //    Se pasa el payload YA REDACTADO, no el original.
        if (_firmwareOtaService != null &&
            ev.Attributes.TryGetValue("packet_type", out var pktType) &&
            pktType == "ota_event" &&
            ev.Attributes.TryGetValue("ota_action", out var otaAction))
        {
            bool identityAuthenticated =
                !string.IsNullOrWhiteSpace(request.TransportDeviceIdentifier) &&
                string.Equals(request.TransportDeviceIdentifier, device.ChipId, StringComparison.OrdinalIgnoreCase);

            if (!identityAuthenticated)
            {
                _logger.LogWarning(
                    "Reporte OTA de '{ChipId}' descartado: la conexión no está autenticada por dispositivo.",
                    device.ChipId);
            }
            else
            {
                try
                {
                    await _firmwareOtaService.HandleDeviceOtaReportAsync(
                        chipId: device.ChipId,
                        otaAction: otaAction,
                        payloadJson: ev.RawPayload,
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error procesando reporte OTA de {ChipId}", device.ChipId);
                }
            }
        }

        // 10. Correlación de respuesta a comandos de terminal (GT06/Mictrack 0x15 o similar)
        if (_commandRepository != null &&
            ev.Attributes.TryGetValue("command_response", out var cmdResponse) &&
            !string.IsNullOrWhiteSpace(cmdResponse))
        {
            try
            {
                var pendingCommands = await _commandRepository.GetPendingCommandsByDeviceAsync(device.Id, cancellationToken);
                var latestPending = pendingCommands.FirstOrDefault(c => c.Status == CommandStatus.Sent || c.Status == CommandStatus.Pending);
                if (latestPending != null)
                {
                    latestPending.Confirm(cmdResponse);
                    _commandRepository.Update(latestPending);
                    await _unitOfWork.SaveChangesAsync(cancellationToken);

                    if (_notificationService != null)
                    {
                        await _notificationService.NotifyCommandStatusUpdatedAsync(
                            device.TenantId,
                            device.Id,
                            latestPending.Id,
                            latestPending.Status.ToString(),
                            cmdResponse,
                            DateTime.UtcNow,
                            cancellationToken);
                    }
                    _logger.LogInformation("Comando {CommandId} de {ChipId} confirmado por respuesta de terminal: {Resp}",
                        latestPending.Id, device.ChipId, cmdResponse);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error correlacionando respuesta de comando para {ChipId}", device.ChipId);
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
        identifier.Equals("unidentified", StringComparison.OrdinalIgnoreCase) ||
        // Rellenos que fabrican los adaptadores cuando el payload no trae identidad: sin
        // reconocerlos aquí, una trama sin identidad de transporte (credencial de bootstrap o
        // modo anónimo) recorría todo el pipeline hasta resolver un equipo inexistente y
        // escribir una fila de AuditLog por trama (LOW-3). Reconocidos, se descartan temprano.
        identifier.Equals("MQTTV5-DEVICE", StringComparison.OrdinalIgnoreCase) ||
        identifier.Equals("TICKET-VALIDATOR-UNKNOWN", StringComparison.OrdinalIgnoreCase) ||
        identifier.Equals("UNKNOWN-OTA-DEVICE", StringComparison.OrdinalIgnoreCase);

    private static string ComputeCryptographicMessageId(string deviceId, DateTime recordedAt, string? rawPayload)
    {
        string raw = $"{deviceId}:{recordedAt.Ticks}:{rawPayload ?? string.Empty}";
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
