namespace IoT.Application.Devices.Dtos;

/// <summary>
/// Un atributo de dispositivo tal como se expone en el detalle (P1b). <see cref="Value"/> es la cadena
/// libre que guarda la entidad (columna <c>text</c>, ADR-014 D2) y <see cref="LastUpdatedAt"/> la marca
/// del último cambio, para que el frontend muestre la frescura del valor reportado/fijado.
/// </summary>
public sealed record DeviceAttributeItemDto(string Key, string Value, DateTime LastUpdatedAt);

/// <summary>
/// Atributos de un dispositivo AGRUPADOS por ámbito (modelo de atributos de ThingsBoard, ADR-014):
/// <list type="bullet">
///   <item><c>Client</c>: lo reporta el EQUIPO (sólo-lectura para el operador).</item>
///   <item><c>Shared</c>: estado deseado que fija el operador (el push al equipo es DIFERIDO por
///     protocolo; en P1b sólo se ALMACENA).</item>
///   <item><c>Server</c>: metadato de negocio que fija el operador y el equipo nunca ve.</item>
/// </list>
/// </summary>
public sealed record DeviceAttributesDto(
    Guid DeviceId,
    IReadOnlyList<DeviceAttributeItemDto> Client,
    IReadOnlyList<DeviceAttributeItemDto> Shared,
    IReadOnlyList<DeviceAttributeItemDto> Server);
