using IoT.Application.Devices.Dtos;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Devices.Queries;

/// <summary>
/// Lee los atributos de un dispositivo (P1b) AGRUPADOS por ámbito. El dispositivo se carga RESPETANDO
/// los filtros (RLS + filtro EF por subárbol): si no es visible para la sesión (otro tenant u otro
/// subárbol) se devuelve <c>Device.NotFound</c>, nunca se filtra la existencia del equipo. La RLS de
/// <c>device_attributes</c> acota además las filas devueltas por el repositorio.
/// </summary>
public sealed record GetDeviceAttributesQuery(Guid TenantId, Guid DeviceId) : IRequest<Result<DeviceAttributesDto>>;

public sealed class GetDeviceAttributesQueryHandler
    : IRequestHandler<GetDeviceAttributesQuery, Result<DeviceAttributesDto>>
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IDeviceAttributeRepository _attributeRepository;

    public GetDeviceAttributesQueryHandler(
        IDeviceRepository deviceRepository,
        IDeviceAttributeRepository attributeRepository)
    {
        _deviceRepository = deviceRepository;
        _attributeRepository = attributeRepository;
    }

    public async Task<Result<DeviceAttributesDto>> Handle(
        GetDeviceAttributesQuery request, CancellationToken cancellationToken)
    {
        // Carga SCOPED (RLS + EF): un dispositivo fuera del subárbol/tenant de la sesión no es visible.
        var device = await _deviceRepository.GetByIdAsync(request.DeviceId, cancellationToken);
        if (device is null || device.TenantId != request.TenantId)
        {
            return Result<DeviceAttributesDto>.Failure(
                new Error("Device.NotFound", $"El dispositivo '{request.DeviceId}' no existe o está fuera del subárbol visible."));
        }

        var attributes = await _attributeRepository.ListByDeviceAsync(request.DeviceId, cancellationToken);

        var dto = new DeviceAttributesDto(
            request.DeviceId,
            ToItems(attributes, AttributeScope.Client),
            ToItems(attributes, AttributeScope.Shared),
            ToItems(attributes, AttributeScope.Server));

        return Result<DeviceAttributesDto>.Success(dto);
    }

    private static readonly HashSet<string> SensitiveServerKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "device_key",
        "bind_token",
        "ota_hmac_key"
    };

    private static IReadOnlyList<DeviceAttributeItemDto> ToItems(
        IReadOnlyList<DeviceAttribute> source, AttributeScope scope) =>
        source
            .Where(a => a.Scope == scope && !(scope == AttributeScope.Server && SensitiveServerKeys.Contains(a.Key)))
            .OrderBy(a => a.Key, StringComparer.Ordinal)
            .Select(a => new DeviceAttributeItemDto(a.Key, a.Value, a.LastUpdatedAt))
            .ToList();
}
