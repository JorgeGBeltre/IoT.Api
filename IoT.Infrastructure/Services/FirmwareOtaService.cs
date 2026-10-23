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

        string normalizedVersion = version.Trim().TrimStart('v', 'V');
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
        string relativeDir = Path.Combine("storage", "firmware", tenantId.ToString(), normalizedVersion);
        string absoluteDir = Path.Combine(AppContext.BaseDirectory, relativeDir);
        Directory.CreateDirectory(absoluteDir);

        string relativeFilePath = Path.Combine(relativeDir, $"firmware_{normalizedVersion}{extension}");
        string absoluteFilePath = Path.Combine(AppContext.BaseDirectory, relativeFilePath);
        await File.WriteAllBytesAsync(absoluteFilePath, bytes, cancellationToken);

        // Buscar registro existente para actualizar o crear nuevo
        var existing = await _firmwareRepository.GetByVersionAsync(tenantId, normalizedVersion, cancellationToken);
        FirmwareVersionRecord record;

        if (existing != null)
        {
            existing.SetActive(true);
            _firmwareRepository.Update(existing);
            record = existing;
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

        string normalizedVersion = version.Trim().TrimStart('v', 'V');
        var firmware = await _firmwareRepository.GetByVersionAsync(tenantId, normalizedVersion, cancellationToken);
        if (firmware == null)
            return Result.Failure(new Error("Firmware.NotFound", $"Versión de firmware '{normalizedVersion}' no encontrada."));

        string fullPath = Path.Combine(AppContext.BaseDirectory, firmware.Src);
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
        }
        else if (!string.IsNullOrWhiteSpace(targetVersion))
        {
            targetRecord = await _firmwareRepository.GetByVersionAsync(tenantId, targetVersion, cancellationToken);
        }
        else
        {
            var records = await _firmwareRepository.GetRecordsAsync(tenantId, take: 5, cancellationToken: cancellationToken);
            targetRecord = records.FirstOrDefault(r => r.IsActive);
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

        if (action == "error")
        {
            string errorMessage = "Dispositivo reportó error en actualización OTA.";
            if (!string.IsNullOrWhiteSpace(payloadJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(payloadJson);
                    if (doc.RootElement.TryGetProperty("error", out var e1))
                        errorMessage = e1.GetString() ?? errorMessage;
                    else if (doc.RootElement.TryGetProperty("details", out var dObj) &&
                             dObj.TryGetProperty("errorMessage", out var e2))
                        errorMessage = e2.GetString() ?? errorMessage;
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
            string targetVer = device.OtaTargetVersion ?? device.FirmwareVersion ?? "1.0.0";
            if (!string.IsNullOrWhiteSpace(payloadJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(payloadJson);
                    if (doc.RootElement.TryGetProperty("version", out var v))
                        targetVer = v.GetString() ?? targetVer;
                }
                catch { }
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

            string fullPath = Path.Combine(AppContext.BaseDirectory, firmware.Src);
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
            string legacyEventTopic = $"event/{device.ChipId}";

            for (int partIndex = startChunk; partIndex <= totalParts; partIndex++)
            {
                if (ct.IsCancellationRequested) break;

                int offset = (partIndex - 1) * chunkSize;
                int bytesToSend = Math.Min(chunkSize, firmwareBytes.Length - offset);

                string payload = OtaChunker.BuildChunkPayload(
                    firmwareVersion: firmware.FirmwareVersion,
                    partIndex: partIndex,
                    totalParts: totalParts,
                    chunk: firmwareBytes.AsSpan(offset, bytesToSend),
                    sha256Hex: sha256,
                    timestampUtc: DateTime.UtcNow);

                // Publicar con QoS 1 a ota/{chipId}
                await publisher.PublishAsync(otaTopic, payload, qos: 1, retain: false, cancellationToken: ct);
                // Publicar también a event/{chipId} para clientes legacy (paridad con References/Mqtt-Broker)
                await publisher.PublishAsync(legacyEventTopic, payload, qos: 1, retain: false, cancellationToken: ct);

                device.UpdateOtaProgress(partIndex, totalParts);
                deviceRepo.Update(device);
                await uow.SaveChangesAsync(ct);

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

                // Retardo de 250ms (portado de FirmwareServices.cs) para no saturar memoria flash del microcontrolador
                await Task.Delay(250, ct);
            }

            if (device.OtaLastChunkSent >= totalParts)
            {
                _logger.LogInformation("Todos los chunks OTA fueron transmitidos a {ChipId} ({Version})",
                    device.ChipId, firmware.FirmwareVersion);
            }
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
