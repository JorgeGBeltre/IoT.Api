using IoT.Application.Common.Interfaces;
using IoT.Application.Devices.Dtos;
using IoT.Domain.Common;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Devices.Queries;

public sealed record GetDevicesQuery(Guid TenantId, DeviceOperationalStatus? Status = null) : IRequest<Result<IReadOnlyList<DeviceDto>>>;

public sealed class GetDevicesQueryHandler : IRequestHandler<GetDevicesQuery, Result<IReadOnlyList<DeviceDto>>>
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly ICustomerRepository _customerRepository;
    private readonly IStateCacheService _stateCache;

    public GetDevicesQueryHandler(
        IDeviceRepository deviceRepository,
        ICustomerRepository customerRepository,
        IStateCacheService stateCache)
    {
        _deviceRepository = deviceRepository;
        _customerRepository = customerRepository;
        _stateCache = stateCache;
    }

    public async Task<Result<IReadOnlyList<DeviceDto>>> Handle(GetDevicesQuery request, CancellationToken cancellationToken)
    {
        var devices = await _deviceRepository.GetByTenantAsync(request.TenantId, request.Status, cancellationToken);

        // Nombres de los nodos dueños (Customer) en UNA sola consulta que RESPETA los filtros
        // (RLS por subárbol + EF por tenant, SIN IgnoreQueryFilters). Los customers devueltos son
        // exactamente el subárbol visible de la sesión, el mismo del que cuelgan los devices visibles,
        // así que el nombre se resuelve en memoria sin consultas por dispositivo ni fuga de subárbol.
        var customerNames = (await _customerRepository.GetVisibleAsync(cancellationToken))
            .ToDictionary(c => c.Id, c => c.Name);

        var list = new List<DeviceDto>(devices.Count);
        foreach (var d in devices)
        {
            var cachedPos = await _stateCache.GetLatestPositionAsync(d.TenantId, d.Id, cancellationToken);

            string? customerName = d.CustomerId is Guid cid && customerNames.TryGetValue(cid, out var name)
                ? name
                : null;

            list.Add(new DeviceDto(
                d.Id,
                d.TenantId,
                d.ChipId,
                d.MacAddress,
                d.Name,
                d.Model,
                d.Manufacturer,
                d.AdminStatus.ToString(),
                d.OperationalStatus.ToString(),
                d.LastCommunicationAt ?? d.LastTelemetryAt,
                d.CreatedAt,
                d.Capabilities.Select(c => c.CapabilityType.ToString()).ToList(),
                cachedPos,
                d.CustomerId,
                customerName));
        }

        return Result<IReadOnlyList<DeviceDto>>.Success(list);
    }
}
