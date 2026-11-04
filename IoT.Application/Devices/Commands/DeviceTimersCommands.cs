using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Devices.Commands;

public sealed record DeviceTimerActionDto(
    [property: JsonPropertyName("switch")] string Switch);

public sealed record DeviceTimerItemDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("channel")] int Channel = 1,
    [property: JsonPropertyName("hour")] int? Hour = null,
    [property: JsonPropertyName("minute")] int? Minute = null,
    [property: JsonPropertyName("weekdays")] int? Weekdays = null,
    [property: JsonPropertyName("at")] long? At = null,
    [property: JsonPropertyName("startAt")] long? StartAt = null,
    [property: JsonPropertyName("periodSec")] int? PeriodSec = null,
    [property: JsonPropertyName("onDurationSec")] int? OnDurationSec = null,
    [property: JsonPropertyName("do")] DeviceTimerActionDto? Do = null,
    [property: JsonPropertyName("startDo")] DeviceTimerActionDto? StartDo = null,
    [property: JsonPropertyName("endDo")] DeviceTimerActionDto? EndDo = null);

public sealed record DeviceTimersResultDto(
    Guid DeviceId,
    IReadOnlyList<DeviceTimerItemDto> Timers,
    bool DeliveredViaMqtt,
    DateTime UpdatedAtUtc);

public sealed record GetDeviceTimersQuery(
    Guid TenantId,
    Guid DeviceId) : IRequest<Result<DeviceTimersResultDto>>;

public sealed record UpsertDeviceTimersCommand(
    Guid TenantId,
    Guid DeviceId,
    IReadOnlyList<DeviceTimerItemDto>? Timers,
    Guid? PerformedByUserId = null,
    string? PerformedByUsername = null) : IRequest<Result<DeviceTimersResultDto>>;

internal static class DeviceTimersJsonSettings
{
    public const string SharedAttributeKey = "timers_json";
    public const int MaxTimersPerDevice = 8;

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed class GetDeviceTimersQueryHandler : IRequestHandler<GetDeviceTimersQuery, Result<DeviceTimersResultDto>>
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IDeviceAttributeRepository _attributeRepository;

    public GetDeviceTimersQueryHandler(
        IDeviceRepository deviceRepository,
        IDeviceAttributeRepository attributeRepository)
    {
        _deviceRepository = deviceRepository;
        _attributeRepository = attributeRepository;
    }

    public async Task<Result<DeviceTimersResultDto>> Handle(GetDeviceTimersQuery request, CancellationToken cancellationToken)
    {
        var device = await _deviceRepository.GetByIdAsync(request.DeviceId, cancellationToken);
        if (device is null || device.TenantId != request.TenantId)
        {
            return Result<DeviceTimersResultDto>.Failure(
                new Error("Device.NotFound", "No se encontró el dispositivo solicitado."));
        }

        var sharedAttrs = await _attributeRepository.GetByDeviceAndScopeAsync(
            device.Id, AttributeScope.Shared, cancellationToken);
        var timersAttr = sharedAttrs.FirstOrDefault(a =>
            string.Equals(a.Key, DeviceTimersJsonSettings.SharedAttributeKey, StringComparison.OrdinalIgnoreCase));

        IReadOnlyList<DeviceTimerItemDto> timers = Array.Empty<DeviceTimerItemDto>();
        DateTime updatedAt = device.CreatedAt;

        if (timersAttr is not null && !string.IsNullOrWhiteSpace(timersAttr.Value))
        {
            updatedAt = timersAttr.LastUpdatedAt;
            try
            {
                var parsed = JsonSerializer.Deserialize<List<DeviceTimerItemDto>>(
                    timersAttr.Value, DeviceTimersJsonSettings.SerializerOptions);
                if (parsed is not null)
                {
                    timers = parsed;
                }
            }
            catch (JsonException)
            {
                timers = Array.Empty<DeviceTimerItemDto>();
            }
        }

        return Result<DeviceTimersResultDto>.Success(
            new DeviceTimersResultDto(device.Id, timers, false, updatedAt));
    }
}

public sealed class UpsertDeviceTimersCommandHandler : IRequestHandler<UpsertDeviceTimersCommand, Result<DeviceTimersResultDto>>
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IDeviceAttributeRepository _attributeRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMqttPublisher? _mqttPublisher;
    private readonly IMqttClientMap? _mqttClientMap;
    private readonly IAuditLogRepository? _auditLogRepository;
    private readonly IFleetNotificationService? _notificationService;

    public UpsertDeviceTimersCommandHandler(
        IDeviceRepository deviceRepository,
        IDeviceAttributeRepository attributeRepository,
        IUnitOfWork unitOfWork,
        IMqttPublisher? mqttPublisher = null,
        IMqttClientMap? mqttClientMap = null,
        IAuditLogRepository? auditLogRepository = null,
        IFleetNotificationService? notificationService = null)
    {
        _deviceRepository = deviceRepository;
        _attributeRepository = attributeRepository;
        _unitOfWork = unitOfWork;
        _mqttPublisher = mqttPublisher;
        _mqttClientMap = mqttClientMap;
        _auditLogRepository = auditLogRepository;
        _notificationService = notificationService;
    }

    public async Task<Result<DeviceTimersResultDto>> Handle(UpsertDeviceTimersCommand request, CancellationToken cancellationToken)
    {
        var device = await _deviceRepository.GetByIdAsync(request.DeviceId, cancellationToken);
        if (device is null || device.TenantId != request.TenantId)
        {
            return Result<DeviceTimersResultDto>.Failure(
                new Error("Device.NotFound", "No se encontró el dispositivo solicitado."));
        }

        var incoming = request.Timers ?? Array.Empty<DeviceTimerItemDto>();
        if (incoming.Count > DeviceTimersJsonSettings.MaxTimersPerDevice)
        {
            return Result<DeviceTimersResultDto>.Failure(
                new Error(
                    "Device.TimersLimitExceeded",
                    $"Cada dispositivo admite un máximo de {DeviceTimersJsonSettings.MaxTimersPerDevice} temporizadores configurados."));
        }

        var normalized = new List<DeviceTimerItemDto>(incoming.Count);
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        for (int index = 0; index < incoming.Count; index++)
        {
            var raw = incoming[index];
            var validationRes = ValidateAndNormalize(raw, index + 1, nowEpoch, usedIds);
            if (validationRes.IsFailure)
            {
                return Result<DeviceTimersResultDto>.Failure(validationRes.Error);
            }
            normalized.Add(validationRes.Value);
        }

        string serializedTimers = JsonSerializer.Serialize(normalized, DeviceTimersJsonSettings.SerializerOptions);
        var attr = await _attributeRepository.UpsertAsync(
            device.TenantId,
            device.Id,
            AttributeScope.Shared,
            DeviceTimersJsonSettings.SharedAttributeKey,
            serializedTimers,
            cancellationToken);

        if (_auditLogRepository is not null)
        {
            await _auditLogRepository.AddAsync(
                new AuditLog(
                    Guid.NewGuid(),
                    device.TenantId,
                    request.PerformedByUserId,
                    string.IsNullOrWhiteSpace(request.PerformedByUsername) ? "usuario" : request.PerformedByUsername!,
                    "DeviceTimersUpdated",
                    $"Configuración de temporizadores actualizada para '{device.Name}' ({device.ChipId}): {normalized.Count} entradas.",
                    ipAddress: null,
                    userAgent: null),
                cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        bool deliveredViaMqtt = false;
        if (_mqttPublisher is not null)
        {
            string? activeClientId = _mqttClientMap is not null
                ? await _mqttClientMap.GetClientIdAsync(device.ChipId, cancellationToken)
                : null;

            if (!string.IsNullOrWhiteSpace(activeClientId) ||
                device.OperationalStatus == DeviceOperationalStatus.Online)
            {
                string topic = $"devices/{device.ChipId}/timers/command";
                string mqttPayload = JsonSerializer.Serialize(
                    new
                    {
                        action = "set_timers",
                        timers = normalized
                    },
                    DeviceTimersJsonSettings.SerializerOptions);

                await _mqttPublisher.PublishAsync(
                    topic,
                    mqttPayload,
                    new MqttPublishOptions
                    {
                        Qos = 1,
                        Retain = false,
                        MessageExpiryInterval = 300,
                        ContentType = "application/json",
                        CorrelationData = Encoding.UTF8.GetBytes(device.Id.ToString("N")),
                        UserProperties = new Dictionary<string, string>
                        {
                            ["action"] = "set_timers",
                            ["timer_count"] = normalized.Count.ToString()
                        }
                    },
                    cancellationToken);

                deliveredViaMqtt = true;
            }
        }

        if (_notificationService is not null)
        {
            try
            {
                await _notificationService.NotifyDeviceAlertAsync(
                    device.TenantId,
                    device.Id,
                    device.ChipId,
                    "TIMERS_UPDATED",
                    serializedTimers,
                    DateTime.UtcNow,
                    cancellationToken);
            }
            catch
            {
                // Best-effort realtime notification; persistence and MQTT sync already completed.
            }
        }

        return Result<DeviceTimersResultDto>.Success(
            new DeviceTimersResultDto(device.Id, normalized, deliveredViaMqtt, attr.LastUpdatedAt));
    }

    private static Result<DeviceTimerItemDto> ValidateAndNormalize(
        DeviceTimerItemDto item,
        int ordinal,
        long nowEpoch,
        HashSet<string> usedIds)
    {
        string id = string.IsNullOrWhiteSpace(item.Id) ? $"tmr_{ordinal}" : item.Id.Trim();
        if (id.Length > 23)
        {
            id = id[..23];
        }
        while (!usedIds.Add(id))
        {
            id = $"{id[..Math.Min(18, id.Length)]}_{ordinal}";
        }

        if (item.Channel is < 0 or > 16)
        {
            return Result<DeviceTimerItemDto>.Failure(
                new Error("Device.InvalidTimer", "El canal del temporizador debe estar entre 0 (todos) y 16."));
        }

        string type = (item.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (type == "schedule") type = "repeat";
        if (type == "loop") type = "duration";

        if (type == "repeat")
        {
            int hour = item.Hour ?? -1;
            int minute = item.Minute ?? -1;
            int weekdays = item.Weekdays ?? 0x7F;
            if (hour is < 0 or > 23 || minute is < 0 or > 59)
            {
                return Result<DeviceTimerItemDto>.Failure(
                    new Error("Device.InvalidTimer", "La hora programada debe indicar hora (0..23) y minuto (0..59) válidos."));
            }
            if (weekdays is < 0 or > 0x7F)
            {
                return Result<DeviceTimerItemDto>.Failure(
                    new Error("Device.InvalidTimer", "La máscara de días de la semana debe estar entre 0 y 127."));
            }

            string sw = NormalizeSwitchToken(item.Do?.Switch);
            return Result<DeviceTimerItemDto>.Success(
                new DeviceTimerItemDto(
                    Id: id,
                    Enabled: item.Enabled,
                    Type: "repeat",
                    Channel: item.Channel,
                    Hour: hour,
                    Minute: minute,
                    Weekdays: weekdays,
                    Do: new DeviceTimerActionDto(sw)));
        }

        if (type == "once")
        {
            long at = item.At ?? 0;
            if (at <= 1_000_000_000L)
            {
                return Result<DeviceTimerItemDto>.Failure(
                    new Error("Device.InvalidTimer", "El temporizador de cuenta regresiva requiere un instante UTC válido ('at')."));
            }

            string sw = NormalizeSwitchToken(item.Do?.Switch);
            return Result<DeviceTimerItemDto>.Success(
                new DeviceTimerItemDto(
                    Id: id,
                    Enabled: item.Enabled,
                    Type: "once",
                    Channel: item.Channel,
                    At: at,
                    Do: new DeviceTimerActionDto(sw)));
        }

        if (type == "duration")
        {
            int periodSec = item.PeriodSec ?? 0;
            int onDurationSec = item.OnDurationSec ?? 0;
            if (periodSec is < 2 or > 604800 || onDurationSec < 1 || onDurationSec >= periodSec)
            {
                return Result<DeviceTimerItemDto>.Failure(
                    new Error("Device.InvalidTimer", "El temporizador de ciclo requiere duraciones de encendido y apagado mayores o iguales a 1 segundo."));
            }

            long startAt = item.StartAt is > 1_000_000_000L ? item.StartAt.Value : nowEpoch;
            string startSw = NormalizeSwitchToken(item.StartDo?.Switch, defaultOn: true);
            string endSw = NormalizeSwitchToken(item.EndDo?.Switch, defaultOn: startSw != "on");
            if (startSw == endSw)
            {
                endSw = startSw == "on" ? "off" : "on";
            }

            return Result<DeviceTimerItemDto>.Success(
                new DeviceTimerItemDto(
                    Id: id,
                    Enabled: item.Enabled,
                    Type: "duration",
                    Channel: item.Channel,
                    StartAt: startAt,
                    PeriodSec: periodSec,
                    OnDurationSec: onDurationSec,
                    StartDo: new DeviceTimerActionDto(startSw),
                    EndDo: new DeviceTimerActionDto(endSw)));
        }

        return Result<DeviceTimerItemDto>.Failure(
            new Error("Device.InvalidTimer", $"Tipo de temporizador no soportado: '{item.Type}'. Use 'repeat', 'once' o 'duration'."));
    }

    private static string NormalizeSwitchToken(string? raw, bool defaultOn = false)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultOn ? "on" : "off";
        }
        string t = raw.Trim().ToLowerInvariant();
        return t is "on" or "1" or "true" ? "on" : "off";
    }
}
