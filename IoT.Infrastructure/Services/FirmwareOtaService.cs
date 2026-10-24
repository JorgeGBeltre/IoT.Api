using System.Text.Json;
using IoT.Application.Common.Interfaces;
using IoT.Application.Firmware;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Protocols.Ota;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IoT.Infrastructure.Services;

/// <summary>
/// Servicio integral de gestión y despliegue de actualizaciones OTA para dispositivos MQTT.
/// Portado y adaptado de References/Mqtt-Broker (FirmwareServices).
/// </summary>
public sealed class FirmwareOtaService : IFirmwareOtaService
{
    private readonly IFirmwareVersionRecordRepository _firmwareRepository;
    private readonly IDeviceRepository _deviceRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMqttPublisher _mqttPublisher;
    private readonly IFleetNotificationService _notificationService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAuditLogRepository? _auditLogRepository;
    private readonly ILogger<FirmwareOtaService> _logger;

    public FirmwareOtaService(
        IFirmwareVersionRecordRepository firmwareRepository,
        IDeviceRepository deviceRepository,
        IUnitOfWork unitOfWork,
        IMqttPublisher mqttPublisher,
        IFleetNotificationService notificationService,
        IServiceScopeFactory scopeFactory,
        ILogger<FirmwareOtaService> logger,
        IAuditLogRepository? auditLogRepository = null)
    {
        _firmwareRepository = firmwareRepository;
        _deviceRepository = deviceRepository;
        _unitOfWork = unitOfWork;
        _mqttPublisher = mqttPublisher;
        _notificationService = notificationService;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _auditLogRepository = auditLogRepository;
    }

    public async Task<Result<FirmwareVersionRecordDto>> UploadFirmwareAsync(
        Guid tenantId,
        Stream fileStream,
        string fileName,
        string version,
        string? targetModel = null,
        string? featureDescription = null,
        string? updatedFromIp = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(version))
            return Result<FirmwareVersionRecordDto>.Failure(new Error("Firmware.InvalidVersion", "Firmware version is required."));

        // La versión forma parte de la ruta en disco: se valida contra lista blanca ANTES
        // de usarla para cualquier cosa, incluida la clave de búsqueda del registro.
        if (!FirmwareStorage.TryNormalizeVersion(version, out string normalizedVersion))
        {
            return Result<FirmwareVersionRecordDto>.Failure(new Error(
                "Firmware.InvalidVersion",
                "La versión sólo admite letras, números, punto, guion y guion bajo (máx. 50), y no puede contener separadores de ruta."));
        }

        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        var allowedExtensions = new[] { ".bin", ".hex", ".dfu" };

        if (!allowedExtensions.Contains(extension))
            return Result<FirmwareVersionRecordDto>.Failure(
                new Error("Firmware.InvalidExtension", $"Allowed firmware extensions are: {string.Join(", ", allowedExtensions)}."));

        using var memoryStream = new MemoryStream();
        await fileStream.CopyToAsync(memoryStream, cancellationToken);
        byte[] bytes = memoryStream.ToArray();

        if (bytes.Length == 0)
            return Result<FirmwareVersionRecordDto>.Failure(new Error("Firmware.EmptyFile", "Firmware file cannot be empty."));

        // Validate image structure if .bin
        if (extension == ".bin")
        {
            string? validationError = OtaChunker.ValidateImage(bytes);
            if (validationError != null)
            {
                _logger.LogWarning("Validación de imagen OTA arrojó advertencia: {Warning}", validationError);
                // Si es menor al mínimo absoluto de ESP32 se rechaza para evitar dañar flash
                if (bytes.Length < OtaChunker.MinimumFirmwareBytes)
                    return Result<FirmwareVersionRecordDto>.Failure(new Error("Firmware.InvalidImage", validationError));
            }
        }

        string sha256 = OtaChunker.Sha256Hex(bytes);
        int totalChunks = OtaChunker.CountChunks(bytes.Length, OtaChunker.DefaultChunkSizeBytes);

        // Guardar archivo en disco
        // Ruta de guardado SIEMPRE por el resolver: valida la versión contra una lista
        // blanca, genera el nombre de archivo a partir del hash (la cadena del cliente no
        // participa en el nombre) y comprueba que la ruta final queda dentro del almacén de
        // ESTE tenant. Sin esto, una versión como "..\..\x" o "C:\x" escribía fuera.
        if (!FirmwareStorage.TryBuildRelativeFilePath(
                tenantId, normalizedVersion, sha256, extension,
                out string relativeFilePath, out string absoluteFilePath))
        {
            _logger.LogWarning(
                "Subida de firmware rechazada para el tenant {TenantId}: la versión '{Version}' no es válida como ruta.",
                tenantId, version);
            return Result<FirmwareVersionRecordDto>.Failure(new Error(
                "Firmware.InvalidVersion",
                "La versión sólo admite letras, números, punto, guion y guion bajo (máx. 50), y no puede contener separadores de ruta."));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(absoluteFilePath)!);
        await File.WriteAllBytesAsync(absoluteFilePath, bytes, cancellationToken);

        // Buscar registro existente para actualizar o crear nuevo
        var existing = await _firmwareRepository.GetByVersionAsync(tenantId, normalizedVersion, cancellationToken);
        FirmwareVersionRecord record;

        if (existing != null)
        {
            // Re-subida de una versión existente: hay que APUNTAR AL BINARIO NUEVO. Antes
            // sólo se re-activaba el registro, así que la flota seguía recibiendo el archivo
            // viejo mientras la API respondía con la versión nueva, y el recién subido
            // quedaba huérfano en disco.
            string? previousSrc = existing.ReplaceBinary(
                src: relativeFilePath,
                fileSizeBytes: bytes.Length,
                sha256: sha256,
                totalChunks: totalChunks,
                chunkSizeBytes: OtaChunker.DefaultChunkSizeBytes,
                targetModel: targetModel,
                featureDescription: featureDescription,
                updatedFromIp: updatedFromIp);

            _firmwareRepository.Update(existing);
            record = existing;

            // El binario anterior ya no lo referencia nadie: se elimina para no acumular
            // imágenes muertas en el almacén. Se vuelve a validar la contención por si la
            // fila venía de antes de esa comprobación.
            if (previousSrc is not null &&
                FirmwareStorage.TryResolveWithinTenant(tenantId, previousSrc, out var previousAbsolute) &&
                File.Exists(previousAbsolute))
            {
                try
                {
                    File.Delete(previousAbsolute);
                    _logger.LogInformation(
                        "Firmware {Version} del tenant {TenantId} reemplazado; se eliminó el binario anterior.",
                        normalizedVersion, tenantId);
                }
                catch (IOException ex)
                {
                    // No es motivo para fallar la subida: el registro ya apunta al nuevo.
                    _logger.LogWarning(ex,
                        "No se pudo eliminar el binario anterior de {Version}: {Src}", normalizedVersion, previousSrc);
                }
            }
        }
        else
        {
            var createResult = FirmwareVersionRecord.Create(
                tenantId: tenantId,
                firmwareVersion: normalizedVersion,
                src: relativeFilePath,
                fileSizeBytes: bytes.Length,
                sha256: sha256,
                totalChunks: totalChunks,
                chunkSizeBytes: OtaChunker.DefaultChunkSizeBytes,
                targetModel: targetModel,
                featureDescription: featureDescription,
                updatedFromIp: updatedFromIp);

            if (createResult.IsFailure)
                return Result<FirmwareVersionRecordDto>.Failure(createResult.Error);

            record = createResult.Value;
            await _firmwareRepository.AddAsync(record, cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Firmware {Version} subido exitosamente para tenant {TenantId}. Tamaño: {Bytes} bytes, Chunks: {Chunks}",
            normalizedVersion, tenantId, bytes.Length, totalChunks);

        return Result<FirmwareVersionRecordDto>.Success(MapToDto(record));
    }

    public async Task<Result> StartDeviceOtaAsync(
        Guid tenantId,
        Guid deviceId,
        string version,
        CancellationToken cancellationToken = default)
    {
        var device = await _deviceRepository.GetByIdAsync(deviceId, cancellationToken);
        if (device == null || device.TenantId != tenantId)
            return Result.Failure(new Error("Device.NotFound", "Dispositivo no encontrado."));

        if (!FirmwareStorage.TryNormalizeVersion(version, out string normalizedVersion))
            return Result.Failure(new Error("Firmware.InvalidVersion", "Versión de firmware inválida."));

        var firmware = await _firmwareRepository.GetByVersionAsync(tenantId, normalizedVersion, cancellationToken);
        if (firmware == null)
            return Result.Failure(new Error("Firmware.NotFound", $"Versión de firmware '{normalizedVersion}' no encontrada."));

        // La ruta viene de base de datos: se vuelve a comprobar que sigue dentro del almacén
        // de este tenant antes de abrirla. Una fila escrita antes de esta validación (o por
        // otra vía) podría apuntar a cualquier archivo del servidor y acabar publicada a los
        // dispositivos.
        if (!FirmwareStorage.TryResolveWithinTenant(tenantId, firmware.Src, out string fullPath))
        {
            _logger.LogError(
                "La ruta almacenada del firmware {Version} del tenant {TenantId} queda fuera del almacén y se ha rechazado: {Src}",
                normalizedVersion, tenantId, firmware.Src);
            return Result.Failure(new Error("Firmware.InvalidPath", "La ruta del archivo de firmware no es válida."));
        }

        if (!File.Exists(fullPath))
            return Result.Failure(new Error("Firmware.FileNotFound", "El archivo de firmware físico no existe en el servidor."));

        if (device.OtaInProgress)
        {
            // Si tiene más de 15 minutos en progreso sin actualizarse, permitir reinicio
            if (device.OtaUpdatedAt.HasValue && DateTime.UtcNow - device.OtaUpdatedAt.Value < TimeSpan.FromMinutes(15))
            {
                return Result.Failure(new Error("OTA.AlreadyInProgress", "Ya hay una actualización OTA en curso para este dispositivo."));
            }
        }

        device.StartOta(normalizedVersion, firmware.TotalChunks);
        _deviceRepository.Update(device);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Notificar inicio en tiempo real
        await _notificationService.NotifyOtaProgressAsync(
            tenantId: device.TenantId,
            deviceId: device.Id,
            chipId: device.ChipId,
            version: normalizedVersion,
            currentChunk: 0,
            totalChunks: firmware.TotalChunks,
            percentage: 0,
            status: "Starting",
            cancellationToken: cancellationToken);

        // Lanzar transmisión en background
        _ = Task.Run(() => DispatchOtaTransmissionAsync(tenantId, device.Id, firmware.Id, CancellationToken.None));

        return Result.Success();
    }

    public async Task<Result> StartGroupOtaAsync(
        Guid tenantId,
        Guid groupId,
        string version,
        CancellationToken cancellationToken = default)
    {
        var devices = await _deviceRepository.GetByGroupIdAsync(tenantId, groupId, cancellationToken);
        if (devices.Count == 0)
            return Result.Failure(new Error("Group.Empty", "El grupo no contiene dispositivos para actualizar."));

        int startedCount = 0;
        foreach (var device in devices)
        {
            var res = await StartDeviceOtaAsync(tenantId, device.Id, version, cancellationToken);
            if (res.IsSuccess) startedCount++;
        }

        _logger.LogInformation("Actualización OTA grupal iniciada para {Count} de {Total} dispositivos en grupo {GroupId}",
            startedCount, devices.Count, groupId);

        return Result.Success();
    }

    public async Task<Result> RollbackDeviceOtaAsync(
        Guid tenantId,
        Guid deviceId,
        Guid? firmwareRecordId = null,
        string? targetVersion = null,
        CancellationToken cancellationToken = default)
    {
        FirmwareVersionRecord? targetRecord = null;

        if (firmwareRecordId.HasValue && firmwareRecordId.Value != Guid.Empty)
        {
            targetRecord = await _firmwareRepository.GetByIdAsync(firmwareRecordId.Value, cancellationToken);

            // La búsqueda por Id NO está acotada por tenant: hay que comprobarlo aquí. Sin
            // esto, pasando el Id de un registro de OTRO tenant se revelaba su existencia y
            // su cadena de versión (queda además en el log de abajo).
            if (targetRecord != null && targetRecord.TenantId != tenantId)
            {
                _logger.LogWarning(
                    "Rollback rechazado: el registro de firmware {RecordId} pertenece a otro tenant que {TenantId}.",
                    firmwareRecordId.Value, tenantId);
                targetRecord = null;
            }
        }
        else if (!string.IsNullOrWhiteSpace(targetVersion))
        {
            targetRecord = await _firmwareRepository.GetByVersionAsync(tenantId, targetVersion, cancellationToken);
        }
        else
        {
            // "Rollback" sin versión explícita = volver a la ANTERIOR, no a la actual.
            // Antes se tomaba el primer registro con IsActive, pero IsActive queda en true en
            // TODOS los registros (Create lo pone a true, FirmwareVersionRecord.cs:48, y nada
            // lo apaga: SetActive(false) no se llama en ninguna parte del repo) y la lista
            // viene ordenada por CreatedAt DESCENDENTE, así que el "rollback" re-desplegaba
            // exactamente la versión que acababa de fallar.
            var rollbackDevice = await _deviceRepository.GetByIdAsync(deviceId, cancellationToken);
            if (rollbackDevice is null || rollbackDevice.TenantId != tenantId)
                return Result.Failure(new Error("Device.NotFound", "Dispositivo no encontrado."));

            string? current = rollbackDevice.OtaTargetVersion ?? rollbackDevice.FirmwareVersion;
            string currentNormalized = (current ?? string.Empty).Trim().TrimStart('v', 'V');

            var records = await _firmwareRepository.GetRecordsAsync(
                tenantId, take: 20, cancellationToken: cancellationToken);

            targetRecord = records.FirstOrDefault(r =>
                currentNormalized.Length == 0 ||
                !string.Equals(
                    r.FirmwareVersion.Trim().TrimStart('v', 'V'),
                    currentNormalized,
                    StringComparison.OrdinalIgnoreCase));

            if (targetRecord is null)
            {
                return Result.Failure(new Error("Rollback.NoPreviousVersion",
                    $"No hay ninguna versión distinta de '{current}' registrada para este tenant. Indica la versión destino de forma explícita."));
            }

            _logger.LogInformation(
                "Rollback de {ChipId}: de '{Current}' a la anterior disponible '{Target}'.",
                rollbackDevice.ChipId, current ?? "(desconocida)", targetRecord.FirmwareVersion);
        }

        if (targetRecord == null)
            return Result.Failure(new Error("Rollback.TargetNotFound", "No se encontró una versión previa de firmware para el rollback."));

        _logger.LogInformation("Ejecutando rollback de firmware para dispositivo {DeviceId} a versión {Version}",
            deviceId, targetRecord.FirmwareVersion);

        return await StartDeviceOtaAsync(tenantId, deviceId, targetRecord.FirmwareVersion, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<FirmwareVersionRecordDto>>> GetFirmwareRecordsAsync(
        Guid tenantId,
        string? targetModel = null,
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        var records = await _firmwareRepository.GetRecordsAsync(tenantId, targetModel, skip, take, cancellationToken);
        var dtos = records.Select(MapToDto).ToList();
        return Result<IReadOnlyList<FirmwareVersionRecordDto>>.Success(dtos);
    }

    public async Task<Result<DeviceOtaStatusDto>> GetDeviceOtaStatusAsync(
        Guid tenantId,
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        var device = await _deviceRepository.GetByIdAsync(deviceId, cancellationToken);
        if (device == null || device.TenantId != tenantId)
            return Result<DeviceOtaStatusDto>.Failure(new Error("Device.NotFound", "Dispositivo no encontrado."));

        int pct = device.OtaTotalChunks > 0
            ? Math.Clamp((int)((device.OtaLastChunkSent * 100.0) / device.OtaTotalChunks), 0, 100)
            : 0;

        var dto = new DeviceOtaStatusDto(
            DeviceId: device.Id,
            ChipId: device.ChipId,
            DeviceName: device.Name,
            CurrentFirmwareVersion: device.FirmwareVersion,
            OtaInProgress: device.OtaInProgress,
            OtaTargetVersion: device.OtaTargetVersion,
            OtaLastChunkSent: device.OtaLastChunkSent,
            OtaTotalChunks: device.OtaTotalChunks,
            Percentage: pct,
            OtaCompleted: device.OtaCompleted,
            OtaErrorMessage: device.OtaErrorMessage,
            OtaUpdatedAt: device.OtaUpdatedAt);

        return Result<DeviceOtaStatusDto>.Success(dto);
    }

    public async Task HandleDeviceOtaReportAsync(
        string chipId,
        string otaAction,
        string? payloadJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(chipId)) return;

        var device = await _deviceRepository.GetByChipIdGlobalAsync(chipId, cancellationToken);
        if (device == null)
        {
            _logger.LogWarning("Reporte OTA recibido de dispositivo no encontrado: {ChipId}", chipId);
            return;
        }

        string action = otaAction.ToLowerInvariant();
        _logger.LogInformation("Procesando reporte OTA de {ChipId}: acción '{Action}'", chipId, action);

        // Un reporte sólo puede mover el estado de una campaña que esté EN CURSO. Sin esta
        // comprobación, un equipo podía marcar como completada o fallida una OTA que nadie
        // había lanzado, y dejar registrada una versión de firmware que nunca se le envió.
        if (!device.OtaInProgress)
        {
            _logger.LogWarning(
                "Reporte OTA '{Action}' de {ChipId} ignorado: el dispositivo no tiene ninguna actualización en curso.",
                action, chipId);
            return;
        }

        if (action == "error")
        {
            string errorMessage = "Dispositivo reportó error en actualización OTA.";
            if (!string.IsNullOrWhiteSpace(payloadJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(payloadJson);
                    // Cotas de longitud: el texto lo escribe el dispositivo y acaba
                    // persistido y difundido por SignalR.
                    if (doc.RootElement.TryGetProperty("error", out var e1))
                        errorMessage = Truncate(e1.GetString(), MaxErrorLength) ?? errorMessage;
                    else if (doc.RootElement.TryGetProperty("details", out var dObj) &&
                             dObj.TryGetProperty("errorMessage", out var e2))
                        errorMessage = Truncate(e2.GetString(), MaxErrorLength) ?? errorMessage;
                }
                catch { }
            }

            device.FailOta(errorMessage);
            _deviceRepository.Update(device);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await _notificationService.NotifyOtaProgressAsync(
                tenantId: device.TenantId,
                deviceId: device.Id,
                chipId: device.ChipId,
                version: device.OtaTargetVersion ?? "unknown",
                currentChunk: device.OtaLastChunkSent,
                totalChunks: device.OtaTotalChunks,
                percentage: device.OtaTotalChunks > 0 ? (device.OtaLastChunkSent * 100) / device.OtaTotalChunks : 0,
                status: "Failed",
                errorMessage: errorMessage,
                cancellationToken: cancellationToken);
        }
        else if (action == "success")
        {
            // La versión la decide la PLATAFORMA, no el dispositivo: se registra la que se
            // le envió (OtaTargetVersion). La que reporta el equipo sólo se usa para
            // comprobar que coincide; si no coincide, flasheó algo distinto de lo que se le
            // mandó y la campaña se marca fallida en vez de dar por buena su cadena.
            string targetVer = device.OtaTargetVersion ?? device.FirmwareVersion ?? "1.0.0";
            string? reportedVer = null;

            if (!string.IsNullOrWhiteSpace(payloadJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(payloadJson);
                    if (doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        // Cota de longitud: la cadena acaba persistida y difundida por SignalR.
                        reportedVer = Truncate(v.GetString(), MaxVersionLength);
                    }
                }
                catch (JsonException) { }
            }

            if (reportedVer != null &&
                device.OtaTargetVersion != null &&
                !VersionsMatch(reportedVer, device.OtaTargetVersion))
            {
                string mismatch =
                    $"El dispositivo reportó la versión '{reportedVer}' y se le envió '{device.OtaTargetVersion}'.";
                _logger.LogError("OTA de {ChipId} marcada fallida por versión no coincidente. {Detalle}", chipId, mismatch);

                device.FailOta(mismatch);
                _deviceRepository.Update(device);
                await _unitOfWork.SaveChangesAsync(cancellationToken);

                await _notificationService.NotifyOtaProgressAsync(
                    tenantId: device.TenantId,
                    deviceId: device.Id,
                    chipId: device.ChipId,
                    version: device.OtaTargetVersion ?? "unknown",
                    currentChunk: device.OtaLastChunkSent,
                    totalChunks: device.OtaTotalChunks,
                    percentage: 0,
                    status: "Failed",
                    errorMessage: mismatch,
                    cancellationToken: cancellationToken);
                return;
            }

            device.CompleteOta(targetVer);
            _deviceRepository.Update(device);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await _notificationService.NotifyOtaProgressAsync(
                tenantId: device.TenantId,
                deviceId: device.Id,
                chipId: device.ChipId,
                version: targetVer,
                currentChunk: device.OtaTotalChunks,
                totalChunks: device.OtaTotalChunks,
                percentage: 100,
                status: "Completed",
                cancellationToken: cancellationToken);
        }
        else if (action == "progress")
        {
            int reportedPct = 0;
            if (!string.IsNullOrWhiteSpace(payloadJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(payloadJson);
                    if (doc.RootElement.TryGetProperty("progress", out var p))
                        reportedPct = (int)p.GetDouble();
                }
                catch { }
            }

            await _notificationService.NotifyOtaProgressAsync(
                tenantId: device.TenantId,
                deviceId: device.Id,
                chipId: device.ChipId,
                version: device.OtaTargetVersion ?? "unknown",
                currentChunk: device.OtaLastChunkSent,
                totalChunks: device.OtaTotalChunks,
                percentage: reportedPct,
                status: "InProgress",
                cancellationToken: cancellationToken);
        }
    }

    private async Task DispatchOtaTransmissionAsync(Guid tenantId, Guid deviceId, Guid firmwareRecordId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var deviceRepo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
        var firmwareRepo = scope.ServiceProvider.GetRequiredService<IFirmwareVersionRecordRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var publisher = scope.ServiceProvider.GetRequiredService<IMqttPublisher>();
        var notif = scope.ServiceProvider.GetRequiredService<IFleetNotificationService>();

        try
        {
            var device = await deviceRepo.GetByIdAsync(deviceId, ct);
            var firmware = await firmwareRepo.GetByIdAsync(firmwareRecordId, ct);
            if (device == null || firmware == null) return;

            // Misma contención en la bomba de envío: es la que lee los bytes que acaban
            // publicados en el dispositivo.
            if (!FirmwareStorage.TryResolveWithinTenant(tenantId, firmware.Src, out string fullPath))
            {
                _logger.LogError(
                    "Transmisión OTA abortada: la ruta almacenada {Src} queda fuera del almacén del tenant {TenantId}.",
                    firmware.Src, tenantId);
                device.FailOta("La ruta del archivo de firmware no es válida.");
                deviceRepo.Update(device);
                await uow.SaveChangesAsync(ct);
                return;
            }

            if (!File.Exists(fullPath))
            {
                device.FailOta("Archivo físico de firmware no encontrado en servidor.");
                deviceRepo.Update(device);
                await uow.SaveChangesAsync(ct);
                return;
            }

            byte[] firmwareBytes = await File.ReadAllBytesAsync(fullPath, ct);
            int chunkSize = firmware.ChunkSizeBytes > 0 ? firmware.ChunkSizeBytes : OtaChunker.DefaultChunkSizeBytes;
            int totalParts = OtaChunker.CountChunks(firmwareBytes.Length, chunkSize);
            string sha256 = firmware.Sha256;

            int startChunk = Math.Max(1, device.OtaLastChunkSent + 1);
            _logger.LogInformation(
                "Iniciando transmisión de chunks OTA para {ChipId}. Total: {Total}, arrancando en: {Start}",
                device.ChipId, totalParts, startChunk);

            string otaTopic = OtaChunker.TopicFor(device.ChipId);
            // Ya NO se publica una copia en "event/<chipId>": la librería del dispositivo sólo
            // está suscrita a "ota/<chipId>" (MQTTOTAv5, coincidencia exacta), así que esa copia
            // no la leía nadie y doblaba el tráfico de cada trozo.

            for (int partIndex = startChunk; partIndex <= totalParts; partIndex++)
            {
                if (ct.IsCancellationRequested) break;

                // Estado REAL en base de datos antes de cada trozo. Los reportes del
                // dispositivo (un "error", o una cancelación del operador) llegan por OTRO
                // ámbito y escriben sobre su propia instancia; esta bomba mantenía la suya y
                // al persistir el progreso REESCRIBÍA la entidad completa con su snapshot
                // viejo, BORRANDO el fallo recién registrado y siguiendo a la vez enviando
                // trozos a un equipo que ya había abortado.
                Device? current;
                using (var checkScope = _scopeFactory.CreateScope())
                {
                    var checkRepo = checkScope.ServiceProvider.GetRequiredService<IDeviceRepository>();
                    current = await checkRepo.GetByIdAsync(deviceId, ct);
                }

                if (current is null || !current.OtaInProgress ||
                    !string.Equals(current.OtaTargetVersion, firmware.FirmwareVersion, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Transmisión OTA detenida en el trozo {Part}/{Total}: la campaña de {ChipId} ya no está activa (en curso={InProgress}, versión objetivo='{Target}', error='{Error}').",
                        partIndex, totalParts, device.ChipId,
                        current?.OtaInProgress, current?.OtaTargetVersion, current?.OtaErrorMessage);
                    return;
                }

                int offset = (partIndex - 1) * chunkSize;
                int bytesToSend = Math.Min(chunkSize, firmwareBytes.Length - offset);

                string payload = OtaChunker.BuildChunkPayload(
                    firmwareVersion: firmware.FirmwareVersion,
                    partIndex: partIndex,
                    totalParts: totalParts,
                    chunk: firmwareBytes.AsSpan(offset, bytesToSend),
                    sha256Hex: sha256,
                    timestampUtc: DateTime.UtcNow);

                // Publicar con QoS 1 a ota/{chipId}, el tópico al que el firmware está suscrito.
                await publisher.PublishAsync(otaTopic, payload, qos: 1, retain: false, cancellationToken: ct);

                // El progreso se aplica sobre la instancia RECIÉN LEÍDA y se guarda en su
                // propio ámbito, para no arrastrar un snapshot viejo.
                using (var saveScope = _scopeFactory.CreateScope())
                {
                    var saveRepo = saveScope.ServiceProvider.GetRequiredService<IDeviceRepository>();
                    var saveUow = saveScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    var toUpdate = await saveRepo.GetByIdAsync(deviceId, ct);
                    if (toUpdate is null || !toUpdate.OtaInProgress) return;

                    toUpdate.UpdateOtaProgress(partIndex, totalParts);
                    saveRepo.Update(toUpdate);
                    await saveUow.SaveChangesAsync(ct);
                }

                int pct = (partIndex * 100) / totalParts;
                await notif.NotifyOtaProgressAsync(
                    tenantId: device.TenantId,
                    deviceId: device.Id,
                    chipId: device.ChipId,
                    version: firmware.FirmwareVersion,
                    currentChunk: partIndex,
                    totalChunks: totalParts,
                    percentage: pct,
                    status: "InProgress",
                    cancellationToken: ct);

                // Retardo de 250ms (portado de FirmwareServices.cs) para no saturar la
                // memoria flash del microcontrolador.
                await Task.Delay(250, ct);
            }

            // Se han EMITIDO todos los trozos. Eso NO significa que la actualización haya
            // funcionado: la campaña sólo se cierra cuando el propio equipo publica su
            // reporte de éxito en ota/<chipId>/success. El estado sigue "en curso" a propósito.
            _logger.LogInformation(
                "Emitidos los {Total} trozos de {Version} a {ChipId}. Pendiente de que el equipo confirme.",
                totalParts, firmware.FirmwareVersion, device.ChipId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error durante la transmisión de chunks OTA para dispositivo {DeviceId}", deviceId);
            try
            {
                var device = await deviceRepo.GetByIdAsync(deviceId, ct);
                if (device != null)
                {
                    device.FailOta($"Error de transmisión: {ex.Message}");
                    deviceRepo.Update(device);
                    await uow.SaveChangesAsync(ct);
                }
            }
            catch { }
        }
    }
    /// <summary>Cotas de las cadenas que escribe el DISPOSITIVO y que se persisten.</summary>
    private const int MaxVersionLength = 64;
    private const int MaxErrorLength = 500;

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        value = value.Trim();
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    /// <summary>
    /// Compara versiones tolerando la 'v' inicial y la caja, que es lo único que varía
    /// legítimamente entre lo que se envía y lo que el equipo reporta.
    /// </summary>
    private static bool VersionsMatch(string reported, string expected) =>
        string.Equals(
            reported.Trim().TrimStart('v', 'V'),
            expected.Trim().TrimStart('v', 'V'),
            StringComparison.OrdinalIgnoreCase);

    private static FirmwareVersionRecordDto MapToDto(FirmwareVersionRecord r) =>
        new(
            Id: r.Id,
            TenantId: r.TenantId,
            FirmwareVersion: r.FirmwareVersion,
            Src: r.Src,
            TargetModel: r.TargetModel,
            FeatureDescription: r.FeatureDescription,
            FileSizeBytes: r.FileSizeBytes,
            Sha256: r.Sha256,
            TotalChunks: r.TotalChunks,
            ChunkSizeBytes: r.ChunkSizeBytes,
            IsActive: r.IsActive,
            UpdatedFromIp: r.UpdatedFromIp,
            CreatedAt: r.CreatedAt);
}
