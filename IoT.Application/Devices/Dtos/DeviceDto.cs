using IoT.Application.Common.Interfaces;

namespace IoT.Application.Devices.Dtos;

public sealed record DeviceDto(
    Guid Id,
    Guid TenantId,
    string ChipId,
    string MacAddress,
    string Name,
    string Model,
    string Manufacturer,
    string AdminStatus,
    string OperationalStatus,
    DateTime? LastSeenAt,
    DateTime CreatedAt,
    IReadOnlyList<string> Capabilities,
    LivePositionSnapshot? LatestPosition);
