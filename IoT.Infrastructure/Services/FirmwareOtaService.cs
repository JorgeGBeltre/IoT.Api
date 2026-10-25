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

    /// <summary>
    /// Opcional a propósito: así los tests que ya construían el servicio siguen compilando.
    /// Si es null se usa el tope de subida por defecto.
    /// </summary>
    private readonly Microsoft.Extensions.Configuration.IConfiguration? _configuration;

    /// <summary>
    /// Limita las transmisiones simultaneas del PROCESO. Opcional para no romper los tests
    /// que ya construian el servicio; si falta, no hay cota (comportamiento anterior).
    /// </summary>
    private readonly OtaTransmissionThrottle? _throttle;

    /// <summary>
    /// Permite que un apagado ordenado detenga las bombas en curso. La bomba se lanzaba con
    /// CancellationToken.None, asi que su comprobacion de cancelacion era codigo muerto y
    /// seguia publicando trozos contra un broker que ya se estaba parando.
    /// </summary>
    private readonly Microsoft.Extensions.Hosting.IHostApplicationLifetime? _appLifetime;

    /// <summary>
    /// Registro de campañas vivas: garantiza como mucho UNA bomba por dispositivo. Opcional
    /// para no romper los tests que ya construían el servicio; si falta, la reclamación
    /// atómica sigue impidiendo el doble arranque concurrente, pero no se cancela una bomba
    /// vieja colgada al rearrancar tras la ventana de rescate.
    /// </summary>
    private readonly OtaCampaignRegistry? _campaignRegistry;

    /// <summary>
    /// Mapa de presencia ChipId↔ClientId (Redis). Opcional para no romper los tests que ya
    /// construían el servicio; si falta, no se comprueba presencia (comportamiento anterior).
    /// </summary>
    private readonly IMqttClientMap? _clientMap;
    private readonly ILogger<FirmwareOtaService> _logger;

    /// <summary>Ventana de rescate: una campaña sin avanzar más de esto se considera muerta.</summary>
    private static readonly TimeSpan StaleCampaignAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Caducidad de cada trozo OTA en el broker, en segundos. Sin ella, si la sesión del
    /// equipo persiste (GLP y Relay usan cleanStart=false), los trozos QoS 1 se encolaban y se
    /// reinyectaban en ráfaga al reconectar —trozos de una campaña ya olvidada—. Con caducidad,
    /// ningún trozo sobrevive a su campaña. Configurable con <c>Firmware:ChunkExpirySeconds</c>.
    /// </summary>
    private uint ChunkExpirySeconds =>
        uint.TryParse(_configuration?["Firmware:ChunkExpirySeconds"], out var s) && s > 0 ? s : 420u;

    public FirmwareOtaService(
        IFirmwareVersionRecordRepository firmwareRepository,
        IDeviceRepository deviceRepository,
        IUnitOfWork unitOfWork,
        IMqttPublisher mqttPublisher,
        IFleetNotificationService notificationService,
        IServiceScopeFactory scopeFactory,
        ILogger<FirmwareOtaService> logger,
        IAuditLogRepository? auditLogRepository = null,
        Microsoft.Extensions.Configuration.IConfiguration? configuration = null,
        OtaTransmissionThrottle? throttle = null,
        Microsoft.Extensions.Hosting.IHostApplicationLifetime? appLifetime = null,
        OtaCampaignRegistry? campaignRegistry = null,
        IMqttClientMap? clientMap = null)
    {
        _firmwareRepository = firmwareRepository;
        _deviceRepository = deviceRepository;
        _unitOfWork = unitOfWork;
        _mqttPublisher = mqttPublisher;
        _notificationService = notificationService;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _auditLogRepository = auditLogRepository;
        _configuration = configuration;
        _throttle = throttle;
        _appLifetime = appLifetime;
        _campaignRegistry = campaignRegistry;
        _clientMap = clientMap;
    }

    public async Task<Result<FirmwareVersionRecordDto>> UploadFirmwareAsync(
        Guid tenantId,
        Stream fileStream,
        string fileName,
        string version,
        string? targetModel = null,
        string? featureDescription = null,
        string? updatedFromIp = null,
        string? actor = null,
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

        // Cotas de los campos de texto del formulario: los escribe el usuario y acaban en
        // columnas acotadas, donde una cadena larga provocaba un 22001 DESPUÉS de haber
        // escrito ya el binario en disco.
        targetModel = Truncate(targetModel, MaxTargetModelLength);
        featureDescription = Truncate(featureDescription, MaxFeatureDescriptionLength);

        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        var allowedExtensions = new[] { ".bin", ".hex", ".dfu" };

        if (!allowedExtensions.Contains(extension))
            return Result<FirmwareVersionRecordDto>.Failure(
                new Error("Firmware.InvalidExtension", $"Allowed firmware extensions are: {string.Join(", ", allowedExtensions)}."));

        // Tope de tamaño ANTES de materializar nada. El fichero NO se carga en memoria: se
        // escribe en streaming a un temporal dentro del almacén del tenant mientras se calcula
        // el SHA-256, y se corta en cuanto se pasa del límite. Antes se hacía
        // `CopyToAsync(memoryStream)` + `ToArray()`, es decir, DOS copias completas en memoria
        // (la segunda en el Large Object Heap) sin ningún límite de la aplicación.
        long maxBytes = FirmwareStorage.ResolveMaxUploadBytes(_configuration);

        string tempPath = FirmwareStorage.CreateIncomingTempPath(tenantId);
        long totalBytes = 0;
        var header = new byte[OtaChunker.MinimumFirstChunkBytes];
        int headerFilled = 0;
        string sha256;

        try
        {
            using (var hasher = System.Security.Cryptography.IncrementalHash.CreateHash(
                       System.Security.Cryptography.HashAlgorithmName.SHA256))
            using (var output = new FileStream(
                       tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                       bufferSize: 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await fileStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    totalBytes += read;
                    if (totalBytes > maxBytes)
                    {
                        _logger.LogWarning(
                            "Subida de firmware del tenant {TenantId} abortada: supera el límite de {Max} bytes.",
                            tenantId, maxBytes);
                        return Result<FirmwareVersionRecordDto>.Failure(new Error(
                            "Firmware.TooLarge",
                            $"La imagen supera el tamaño máximo admitido ({maxBytes / (1024 * 1024)} MiB)."));
                    }

                    if (headerFilled < header.Length)
                    {
                        int copy = Math.Min(read, header.Length - headerFilled);
                        Array.Copy(buffer, 0, header, headerFilled, copy);
                        headerFilled += copy;
                    }

                    hasher.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                sha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            if (totalBytes == 0)
                return Result<FirmwareVersionRecordDto>.Failure(new Error("Firmware.EmptyFile", "Firmware file cannot be empty."));

            // Validación de la estructura de la imagen con la cabecera leída y el tamaño
            // total, sin necesitar el binario completo en memoria.
            if (extension == ".bin")
            {
                string? validationError = OtaChunker.ValidateImage(header.AsSpan(0, headerFilled), totalBytes);
                if (validationError != null)
                {
                    _logger.LogWarning("Validación de imagen OTA arrojó advertencia: {Warning}", validationError);
                    if (totalBytes < OtaChunker.MinimumFirmwareBytes)
                        return Result<FirmwareVersionRecordDto>.Failure(new Error("Firmware.InvalidImage", validationError));
                }
            }

            int totalChunks = OtaChunker.CountChunks(totalBytes, OtaChunker.DefaultChunkSizeBytes);

            // Ruta final SIEMPRE por el resolver: valida la versión contra lista blanca,
            // genera el nombre a partir del hash (la cadena del cliente no participa) y
            // comprueba que queda dentro del almacén de ESTE tenant.
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
            File.Move(tempPath, absoluteFilePath, overwrite: true);

            return await PersistFirmwareRecordAsync(
                tenantId, normalizedVersion, relativeFilePath, absoluteFilePath,
                totalBytes, sha256, totalChunks, targetModel, featureDescription, updatedFromIp,
                actor, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Disco lleno o almacén no escribible (p.ej. permisos del volumen): sin esto el
            // IOException salía del servicio como excepción no controlada y el controlador
            // devolvía un 500 opaco. Ahora es un fallo con mensaje para el operador.
            _logger.LogError(ex,
                "No se pudo escribir el binario de firmware del tenant {TenantId} en el almacén.", tenantId);
            return Result<FirmwareVersionRecordDto>.Failure(new Error(
                "Firmware.StorageUnavailable",
                "No se pudo guardar el firmware en el servidor (almacén lleno o no escribible). Inténtalo más tarde."));
        }
        finally
        {
            // Si se abortó por tamaño, por validación o por un error, el temporal no debe
            // quedarse ocupando el almacén.
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (IOException) { /* el almacén se limpia aparte; no tapar el error original */ }
        }
    }

    /// <summary>
    /// Crea o actualiza el registro de la versión y devuelve su DTO. Si falla el guardado, el
    /// binario que se acaba de dejar en su sitio se elimina: antes quedaba huérfano en disco
    /// porque el archivo se escribía ANTES del SaveChanges.
    /// </summary>
    private async Task<Result<FirmwareVersionRecordDto>> PersistFirmwareRecordAsync(
        Guid tenantId,
        string normalizedVersion,
        string relativeFilePath,
        string absoluteFilePath,
        long fileSizeBytes,
        string sha256,
        int totalChunks,
        string? targetModel,
        string? featureDescription,
        string? updatedFromIp,
        string? actor,
        CancellationToken cancellationToken)
    {
        try
        {

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
                fileSizeBytes: fileSizeBytes,
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
                fileSizeBytes: fileSizeBytes,
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
                normalizedVersion, tenantId, fileSizeBytes, totalChunks);

            await AuditAsync(tenantId, actor, "FirmwareUploaded",
                $"Versión {normalizedVersion} subida ({fileSizeBytes} bytes, sha256 {sha256}, {totalChunks} trozos).",
                updatedFromIp, cancellationToken);

            return Result<FirmwareVersionRecordDto>.Success(MapToDto(record));
        }
        catch (Exception)
        {
            // El binario ya está en su sitio pero el registro no se pudo guardar: sin fila
            // que lo referencie es un huérfano, así que se elimina.
            try
            {
                if (File.Exists(absoluteFilePath)) File.Delete(absoluteFilePath);
            }
            catch (IOException) { /* no tapar la excepción original */ }

            throw;
        }
    }

    public async Task<Result> StartDeviceOtaAsync(
        Guid tenantId,
        Guid deviceId,
        string version,
        string? actor = null,
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

        // Presencia: no tiene sentido emitir 512 trozos a un equipo que no está conectado.
        // Antes se lanzaba igual contra un tracker apagado, se logueaba "todos los trozos
        // transmitidos" y la campaña quedaba OtaInProgress=true sin nadie al otro lado. Si el
        // mapa dice que NO está conectado, se rechaza con un error claro para que el operador
        // reintente cuando vuelva. Es una comprobación de saneamiento, NO de autorización: si
        // el mapa de Redis falla, se DEGRADA abriendo (se arranca igual) en vez de bloquear
        // todo el OTA por la salud de Redis; el vigilante y la caducidad de los trozos cubren
        // el caso de un equipo que se caiga después. Configurable: Firmware:RequireDeviceOnline.
        bool requireOnline = !bool.TryParse(_configuration?["Firmware:RequireDeviceOnline"], out var ro) || ro;
        if (requireOnline && _clientMap is not null)
        {
            try
            {
                var clientId = await _clientMap.GetClientIdAsync(device.ChipId, cancellationToken);
                if (string.IsNullOrEmpty(clientId))
                {
                    return Result.Failure(new Error("OTA.DeviceOffline",
                        "El dispositivo no está conectado al broker. Lanza la actualización cuando esté en línea."));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "No se pudo comprobar la presencia de {ChipId} (Redis); se arranca la OTA igualmente y el vigilante cubrirá un equipo caído.",
                    device.ChipId);
            }
        }

        // Toma de la campaña ATÓMICA: una escritura condicional en la base, no un
        // leer-luego-escribir. Si otra petición concurrente (doble clic, reintento, o el
        // start-ota que dispara el upload) ya la tomó, ésta no afecta ninguna fila y se
        // rechaza SIN lanzar una segunda bomba sobre el mismo tópico (MEDIUM-6/LOW-1/LOW-4).
        // El guard de los 15 minutos es ahora la CONDICIÓN del propio UPDATE.
        bool claimed = await _deviceRepository.TryStartOtaCampaignAsync(
            tenantId, device.Id, normalizedVersion, firmware.TotalChunks, StaleCampaignAfter, cancellationToken);
        if (!claimed)
        {
            return Result.Failure(new Error("OTA.AlreadyInProgress", "Ya hay una actualización OTA en curso para este dispositivo."));
        }

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

        await AuditAsync(tenantId, actor, "FirmwareRolloutStarted",
            $"Actualización de {device.ChipId} a la versión {normalizedVersion} " +
            $"({firmware.TotalChunks} trozos, sha256 {firmware.Sha256}).",
            null, cancellationToken);

        // Lanzar transmisión en background.
        // Token del apagado ordenado, no CancellationToken.None: asi un Ctrl+C o un
        // "docker compose stop" detienen la transmision en vez de dejarla publicando contra
        // un broker que ya se esta cerrando. El registro de campañas lo envuelve para
        // garantizar como mucho UNA bomba por dispositivo: si quedara una vieja colgada de una
        // campaña caducada, arrancar ésta la cancela.
        var shutdownToken = _appLifetime?.ApplicationStopping ?? CancellationToken.None;
        var backgroundToken = _campaignRegistry?.StartCampaign(device.Id, shutdownToken) ?? shutdownToken;
        var deviceId2 = device.Id;
        _ = Task.Run(() => DispatchOtaTransmissionAsync(tenantId, deviceId2, firmware.Id, backgroundToken), backgroundToken);

        return Result.Success();
    }

    public async Task<Result> StartGroupOtaAsync(
        Guid tenantId,
        Guid groupId,
        string version,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        var devices = await _deviceRepository.GetByGroupIdAsync(tenantId, groupId, cancellationToken);
        if (devices.Count == 0)
            return Result.Failure(new Error("Group.Empty", "El grupo no contiene dispositivos para actualizar."));

        // Deduplicar por Id: un dispositivo que esté repetido (o que pertenezca al grupo por
        // dos vías) no debe recibir dos arranques en la misma llamada.
        var uniqueDeviceIds = devices.Select(d => d.Id).Distinct().ToArray();

        int startedCount = 0;
        foreach (var deviceId in uniqueDeviceIds)
        {
            var res = await StartDeviceOtaAsync(tenantId, deviceId, version, actor, cancellationToken);
            if (res.IsSuccess) startedCount++;
        }

        await AuditAsync(tenantId, actor, "FirmwareGroupRolloutStarted",
            $"Actualización del grupo {groupId} a la versión {version}: {startedCount} de {uniqueDeviceIds.Length} dispositivos iniciados.",
            null, cancellationToken);

        _logger.LogInformation("Actualización OTA grupal iniciada para {Count} de {Total} dispositivos en grupo {GroupId}",
            startedCount, uniqueDeviceIds.Length, groupId);

        // Antes devolvía Success incondicional: un grupo donde TODOS estaban ocupados (o
        // fallaron) respondía 200 "OTA iniciada" con cero arrancados. Si no arrancó ninguno,
        // es un fallo y el operador debe verlo.
        if (startedCount == 0)
        {
            return Result.Failure(new Error("Group.NoneStarted",
                $"Ningún dispositivo del grupo pudo iniciar la actualización (0 de {uniqueDeviceIds.Length}): " +
                "puede que ya tuvieran una campaña en curso."));
        }

        return Result.Success();
    }

    public async Task<Result> DeleteFirmwareVersionAsync(
        Guid tenantId,
        string version,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        if (!FirmwareStorage.TryNormalizeVersion(version, out string normalizedVersion))
            return Result.Failure(new Error("Firmware.InvalidVersion", "Versión de firmware inválida."));

        var record = await _firmwareRepository.GetByVersionAsync(tenantId, normalizedVersion, cancellationToken);
        if (record is null)
            return Result.Failure(new Error("Firmware.NotFound", $"Versión de firmware '{normalizedVersion}' no encontrada."));

        // No se le quita el binario a una campaña viva: si algún equipo del tenant está
        // actualizándose a ESTA versión, la bomba lo lee del disco trozo a trozo y borrarlo la
        // dejaría a medias. El operador debe esperar o cancelar esas campañas primero.
        var inProgress = await _deviceRepository.GetDevicesWithOtaInProgressAsync(cancellationToken);
        bool inUse = inProgress.Any(d =>
            d.TenantId == tenantId &&
            string.Equals(d.OtaTargetVersion, normalizedVersion, StringComparison.OrdinalIgnoreCase));
        if (inUse)
        {
            return Result.Failure(new Error("Firmware.VersionInUse",
                "No se puede borrar: hay una actualización en curso a esta versión. Espera a que termine o cancélala."));
        }

        // Desenlaza el binario del disco, SIEMPRE resolviendo dentro del almacén del tenant
        // (una ruta guardada fuera de él no se toca). Best-effort: si el fichero ya no está o
        // no se puede borrar, se sigue marcando el registro —el objetivo es no dejar huérfanos
        // ni filas que apunten a binarios inexistentes—, y queda en el log.
        if (FirmwareStorage.TryResolveWithinTenant(tenantId, record.Src, out string fullPath))
        {
            try
            {
                if (File.Exists(fullPath)) File.Delete(fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex,
                    "No se pudo borrar el binario de firmware {Version} del tenant {TenantId} en {Path}; el registro se marca igualmente.",
                    normalizedVersion, tenantId, fullPath);
            }
        }
        else
        {
            _logger.LogWarning(
                "La ruta del firmware {Version} del tenant {TenantId} queda fuera del almacén; se marca el registro sin tocar disco: {Src}",
                normalizedVersion, tenantId, record.Src);
        }

        _firmwareRepository.Remove(record); // marca IsDeleted
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await AuditAsync(tenantId, actor, "FirmwareVersionDeleted",
            $"Versión de firmware '{normalizedVersion}' borrada (sha256 {record.Sha256}).",
            null, cancellationToken);

        _logger.LogInformation("Versión de firmware {Version} del tenant {TenantId} borrada por {Actor}.",
            normalizedVersion, tenantId, actor ?? "(desconocido)");

        return Result.Success();
    }

    public async Task<Result> CancelDeviceOtaAsync(
        Guid tenantId,
        Guid deviceId,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        var device = await _deviceRepository.GetByIdAsync(deviceId, cancellationToken);
        if (device is null || device.TenantId != tenantId)
            return Result.Failure(new Error("Device.NotFound", "Dispositivo no encontrado."));

        // Idempotente: cancelar algo que ya no corre no es un error.
        if (!device.OtaInProgress)
            return Result.Success();

        string targetVersion = device.OtaTargetVersion ?? "(desconocida)";
        // FailOta (no ResetOta) para dejar rastro de que fue una cancelación del operador. La
        // bomba en vuelo lo ve en su relectura por trozo (!OtaInProgress) y abandona.
        device.FailOta("Actualización cancelada por el operador.");
        _deviceRepository.Update(device);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await AuditAsync(tenantId, actor, "FirmwareRolloutCancelled",
            $"Actualización de {device.ChipId} a la versión {targetVersion} cancelada por el operador.",
            null, cancellationToken);

        await _notificationService.NotifyOtaProgressAsync(
            tenantId: device.TenantId,
            deviceId: device.Id,
            chipId: device.ChipId,
            version: targetVersion,
            currentChunk: device.OtaLastChunkSent,
            totalChunks: device.OtaTotalChunks,
            percentage: 0,
            status: "Failed",
            errorMessage: "Cancelada por el operador.",
            cancellationToken: cancellationToken);

        return Result.Success();
    }

    public async Task<Result> RollbackDeviceOtaAsync(
        Guid tenantId,
        Guid deviceId,
        Guid? firmwareRecordId = null,
        string? targetVersion = null,
        string? actor = null,
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

        await AuditAsync(tenantId, actor, "FirmwareRollbackRequested",
            $"Reversión del dispositivo {deviceId} a la versión {targetRecord.FirmwareVersion}.",
            null, cancellationToken);

        return await StartDeviceOtaAsync(tenantId, deviceId, targetRecord.FirmwareVersion, actor, cancellationToken);
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

            // PRUEBA OBLIGATORIA de lo que se instaló. Antes la comprobación era vacua si el
            // reporte OMITÍA el campo "version": `reportedVer` quedaba null, la rama de
            // desajuste se saltaba y la campaña se cerraba como COMPLETADA sin que el equipo
            // hubiera demostrado nada. La librería real siempre incluye la versión
            // (MQTTOTAv5: {"device","version","success","bytes","ts"}), así que exigirla no
            // rompe ningún equipo legítimo y cierra el hueco para uno malicioso.
            if (reportedVer is null)
            {
                const string sinVersion =
                    "El dispositivo reportó éxito sin indicar la versión instalada: no se puede dar la campaña por completada.";
                _logger.LogError("OTA de {ChipId} marcada fallida: {Detalle}", chipId, sinVersion);

                device.FailOta(sinVersion);
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
                    errorMessage: sinVersion,
                    cancellationToken: cancellationToken);
                return;
            }

            if (device.OtaTargetVersion != null &&
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

            // Señal de vida: refresca la marca de actividad para que el vigilante no cierre
            // como "sin respuesta" una actualización que el equipo está procesando (un flasheo
            // de 16 MB tarda minutos y va reportando progreso).
            device.TouchOtaActivity();
            _deviceRepository.Update(device);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

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
        else
        {
            // Cualquier otro reporte durante una campaña en curso (ack de trozo, estado
            // intermedio, "status"...) NO cambia el estado contable, pero SÍ es señal de vida:
            // antes caía en un hueco sin rama else y se descartaba en silencio, de modo que un
            // equipo que sólo mandaba acks podía acabar cerrado por el vigilante pese a estar
            // vivo. Se refresca la actividad y se deja rastro en depuración.
            _logger.LogDebug(
                "Reporte OTA '{Action}' de {ChipId} tratado como señal de vida (no cambia el estado de la campaña).",
                action, chipId);
            device.TouchOtaActivity();
            _deviceRepository.Update(device);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// SHA-256 (hex minúscula) del fichero, leído en streaming con el mismo formato que usa la
    /// subida (<c>Convert.ToHexString(...).ToLowerInvariant()</c>), para poder compararlo con
    /// <c>FirmwareVersionRecord.Sha256</c>.
    /// </summary>
    private static async Task<string> ComputeFileSha256Async(string fullPath, int bufferSize, CancellationToken ct)
    {
        using var hasher = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        await using var fs = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: bufferSize > 0 ? bufferSize : 81920, useAsync: true);
        var buffer = new byte[bufferSize > 0 ? bufferSize : 81920];
        int read;
        while ((read = await fs.ReadAsync(buffer, ct)) > 0)
            hasher.AppendData(buffer, 0, read);
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task DispatchOtaTransmissionAsync(Guid tenantId, Guid deviceId, Guid firmwareRecordId, CancellationToken ct)
    {
        // Hueco del limitador de concurrencia ANTES de crear el ambito y de tocar la base:
        // un rollout de grupo crea una tarea por dispositivo, y si cada una abriera su
        // DbContext y leyera el registro antes de esperar, un grupo grande tendria N
        // contextos vivos sin hacer nada. Sin el limitador, ademas, eran N bombas publicando
        // a la vez contra el mismo broker.
        IDisposable? throttleSlot = null;
        if (_throttle is not null)
        {
            try
            {
                throttleSlot = await _throttle.AcquireAsync(ct);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation(
                    "Transmision OTA del dispositivo {DeviceId} cancelada mientras esperaba hueco (apagado del servicio).",
                    deviceId);
                return;
            }
        }

        using var _throttleSlotHolder = throttleSlot;
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

            // La imagen NO se carga en memoria: antes era un File.ReadAllBytesAsync por
            // dispositivo, asi que un rollout de grupo de 100 equipos con una imagen de 1 MB
            // eran 100 copias completas simultaneas (en el Large Object Heap). Ahora cada
            // bomba lee del disco solo el trozo que va a publicar, reutilizando un unico
            // buffer del tamano del chunk.
            int chunkSize = firmware.ChunkSizeBytes > 0 ? firmware.ChunkSizeBytes : OtaChunker.DefaultChunkSizeBytes;
            long fileLength = new FileInfo(fullPath).Length;

            // LOW-5: re-verificar INTEGRIDAD antes de emitir. El sha256 y el tamaño se
            // calcularon al subir; si el fichero en disco cambió desde entonces (restauración
            // de backup, copia manual en el volumen), publicar los bytes NUEVOS con el hash
            // VIEJO haría que el equipo descargue la imagen entera, calcule el SHA, no coincida
            // y aborte —minutos y megabytes a la basura—. Recalcular aquí (una lectura
            // secuencial, insignificante frente a los minutos de transmisión con su retardo por
            // trozo) corta eso en el servidor. NOTA: el sha256 es INTEGRIDAD, no autenticación
            // (viaja en el mismo mensaje que los datos); el origen verificable exigiría firmar
            // la imagen (HMAC/ECDSA del firmware, hoy sin usar).
            if (fileLength != firmware.FileSizeBytes)
            {
                _logger.LogError(
                    "Transmisión OTA abortada: el tamaño del fichero de {Version} ({Actual} B) no coincide con el del registro ({Expected} B).",
                    firmware.FirmwareVersion, fileLength, firmware.FileSizeBytes);
                device.FailOta(Truncate("La imagen en disco no coincide con el registro (tamaño). Vuelve a subirla.", MaxErrorLength)!);
                deviceRepo.Update(device);
                await uow.SaveChangesAsync(ct);
                return;
            }

            string actualSha = await ComputeFileSha256Async(fullPath, chunkSize, ct);
            if (!string.Equals(actualSha, firmware.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "Transmisión OTA abortada: el sha256 del fichero de {Version} no coincide con el del registro.",
                    firmware.FirmwareVersion);
                device.FailOta(Truncate("La imagen en disco no coincide con el registro (sha256). Vuelve a subirla.", MaxErrorLength)!);
                deviceRepo.Update(device);
                await uow.SaveChangesAsync(ct);
                return;
            }

            int totalParts = OtaChunker.CountChunks(fileLength, chunkSize);
            string sha256 = firmware.Sha256;

            await using var image = new FileStream(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: chunkSize, useAsync: true);
            var chunkBuffer = new byte[chunkSize];

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

                long offset = (long)(partIndex - 1) * chunkSize;
                image.Position = offset;
                int bytesToSend = await image.ReadAsync(chunkBuffer.AsMemory(0, (int)Math.Min(chunkSize, fileLength - offset)), ct);
                if (bytesToSend <= 0)
                {
                    _logger.LogError(
                        "Transmision OTA de {ChipId} abortada: no se pudieron leer los bytes del trozo {Part}/{Total}.",
                        device.ChipId, partIndex, totalParts);
                    return;
                }

                string payload = OtaChunker.BuildChunkPayload(
                    firmwareVersion: firmware.FirmwareVersion,
                    partIndex: partIndex,
                    totalParts: totalParts,
                    chunk: chunkBuffer.AsSpan(0, bytesToSend),
                    sha256Hex: sha256,
                    timestampUtc: DateTime.UtcNow);

                // Publicar con QoS 1 a ota/{chipId}, el tópico al que el firmware está suscrito,
                // CON caducidad: si la sesión del equipo persiste, un trozo que no se entregue
                // no debe sobrevivir a la campaña y reinyectarse en ráfaga al reconectar.
                await publisher.PublishAsync(otaTopic, payload,
                    new MqttPublishOptions { Qos = 1, Retain = false, MessageExpiryInterval = ChunkExpirySeconds },
                    ct);

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
                    // Truncado: el mensaje de la excepción es de longitud arbitraria y la
                    // columna no lo es. Sin esto Postgres devolvía 22001, el error se
                    // tragaba en el catch vacío y el equipo quedaba bloqueado en "actualizando".
                    device.FailOta(Truncate($"Error de transmisión: {ex.Message}", MaxErrorLength)!);
                    deviceRepo.Update(device);
                    await uow.SaveChangesAsync(ct);
                }
            }
            catch { }
        }
        finally
        {
            // La bomba terminó (completa, abandonada o con error): se retira del registro para
            // no dejar su CancellationTokenSource colgado. EndCampaign sólo la quita si sigue
            // siendo la suya, así que no pisa una campaña posterior que ya la sustituyó.
            _campaignRegistry?.EndCampaign(deviceId, ct);
        }
    }
    /// <summary>
    /// Deja rastro de QUIÉN hizo qué sobre el firmware. El repositorio de auditoría estaba
    /// inyectado en este servicio y NO se usaba en ninguna línea: flashear un equipo —una
    /// operación irreversible sobre hardware ajeno— no dejaba ningún registro de autoría.
    ///
    /// Nunca propaga una excepción: un fallo al auditar no debe tumbar la operación que se
    /// acaba de hacer, pero sí queda en el log.
    /// </summary>
    private async Task AuditAsync(
        Guid tenantId,
        string? actor,
        string action,
        string details,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        if (_auditLogRepository is null) return;

        try
        {
            var entry = new AuditLog(
                id: Guid.NewGuid(),
                tenantId: tenantId,
                userId: null,
                username: string.IsNullOrWhiteSpace(actor) ? "(desconocido)" : actor!,
                action: action,
                details: Truncate(details, MaxAuditDetailsLength) ?? string.Empty,
                ipAddress: ipAddress,
                userAgent: "IoT.Api/Firmware");

            await _auditLogRepository.AddAsync(entry, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "No se pudo registrar la auditoría de '{Action}' para el tenant {TenantId}", action, tenantId);
        }
    }

    /// <summary>Cota del detalle de auditoría; la columna no es ilimitada.</summary>
    private const int MaxAuditDetailsLength = 1000;

    /// <summary>Cotas de las cadenas que escribe el DISPOSITIVO y que se persisten.</summary>
    private const int MaxVersionLength = 64;
    private const int MaxErrorLength = 500;
    private const int MaxTargetModelLength = 100;
    private const int MaxFeatureDescriptionLength = 500;

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
