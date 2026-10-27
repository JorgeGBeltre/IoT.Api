using IoT.Domain.Entities;

namespace IoT.Application.Customers.Dtos;

/// <summary>
/// Proyección de un nodo <see cref="Customer"/> de la jerarquía multinivel (ADR-013). Lista plana:
/// el frontend arma el árbol a partir de <see cref="ParentCustomerId"/>. No expone <c>Path</c> ni el
/// resto de detalles internos del materializado ltree.
/// </summary>
public sealed record CustomerDto(
    Guid Id,
    Guid? ParentCustomerId,
    string Name,
    string? Code,
    int Level,
    bool IsActive)
{
    public static CustomerDto From(Customer c) =>
        new(c.Id, c.ParentCustomerId, c.Name, c.Code, c.Level, c.IsActive);
}
