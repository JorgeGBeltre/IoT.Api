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
    LivePositionSnapshot? LatestPosition,
    // Nodo dueño en la jerarquía Customer (ADR-013). Nullable: espeja Device.CustomerId
    // (nullable en el dominio durante la transición E8.1) y el contrato del frontend
    // (customerId?/customerName? en DeviceDetailPage). Null = "Sin asignar".
    Guid? CustomerId,
    string? CustomerName,
    IReadOnlyDictionary<string, double>? LatestMetrics = null,
    string? FirmwareVersion = null);
