using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Enums;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Devices.Commands;

/// <summary>
/// Fija (upsert) uno o varios atributos de un dispositivo en un ámbito (P1b). Reglas de ADR-014:
/// <list type="bullet">
///   <item><see cref="AttributeScope.Client"/> lo reporta el EQUIPO: el operador NO lo escribe, así que
///     cualquier intento se RECHAZA con <c>Attributes.ClientReadOnly</c> (la API lo mapea a 400).</item>
///   <item><see cref="AttributeScope.Shared"/> y <see cref="AttributeScope.Server"/> los fija el
///     operador; aquí sólo se ALMACENAN (el push de <c>Shared</c> al equipo es diferido por protocolo
///     y queda fuera de P1b).</item>
/// </list>
/// El dispositivo se carga SCOPED (RLS + filtro EF por subárbol): uno fuera del subárbol/tenant de la
/// sesión no es visible y se trata como <c>Device.NotFound</c>. El aislamiento por subárbol al ESCRIBIR
/// lo refuerza la RLS (<c>WITH CHECK</c>) de <c>device_attributes</c>; el <c>TenantId</c> de la fila se
/// deriva del DEVICE resuelto, nunca de entrada del cliente.
/// </summary>
public sealed record SetDeviceAttributesCommand(
    Guid DeviceId,
    AttributeScope Scope,
    IReadOnlyDictionary<string, string?> Attributes) : IRequest<Result>;

public sealed class SetDeviceAttributesCommandHandler : IRequestHandler<SetDeviceAttributesCommand, Result>
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IDeviceAttributeRepository _attributeRepository;
    private readonly ITenantContext _tenantContext;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISharedAttributePushDispatcher? _pushDispatcher;

    public SetDeviceAttributesCommandHandler(
        IDeviceRepository deviceRepository,
        IDeviceAttributeRepository attributeRepository,
        ITenantContext tenantContext,
        IUnitOfWork unitOfWork,
        ISharedAttributePushDispatcher? pushDispatcher = null)
    {
        _deviceRepository = deviceRepository;
        _attributeRepository = attributeRepository;
        _tenantContext = tenantContext;
        _unitOfWork = unitOfWork;
        _pushDispatcher = pushDispatcher;
    }

    public async Task<Result> Handle(SetDeviceAttributesCommand request, CancellationToken cancellationToken)
    {
        // Los atributos CLIENT son de SÓLO-LECTURA para el operador: los reporta el equipo (ADR-014).
        if (request.Scope == AttributeScope.Client)
        {
            return Result.Failure(new Error(
                "Attributes.ClientReadOnly",
                "Los atributos de ámbito 'Client' los reporta el dispositivo y no pueden escribirse desde la API."));
        }

        if (request.Attributes is null || request.Attributes.Count == 0)
        {
            return Result.Failure(new Error(
                "Attributes.NoAttributes", "Debe indicarse al menos un atributo (par clave-valor)."));
        }

        if (request.Attributes.Keys.Any(string.IsNullOrWhiteSpace))
        {
            return Result.Failure(new Error(
                "Attributes.InvalidKey", "Las claves de atributo no pueden estar vacías."));
        }

        // Carga SCOPED (RLS + EF por subárbol): un dispositivo fuera del subárbol/tenant de la sesión
        // no es visible -> NotFound (mismo trato que un id inexistente, sin filtrar su existencia).
        var device = await _deviceRepository.GetByIdAsync(request.DeviceId, cancellationToken);
        if (device is null || device.TenantId != _tenantContext.TenantId)
        {
            return Result.Failure(new Error(
                "Device.NotFound", $"El dispositivo '{request.DeviceId}' no existe o está fuera del subárbol visible."));
        }

        foreach (var (key, value) in request.Attributes)
        {
            // El TenantId lo SELLA el device resuelto (server-side), nunca entrada del cliente; la RLS
            // (WITH CHECK) rechaza en la BD cualquier fila cuyo device caiga fuera del subárbol/tenant.
            await _attributeRepository.UpsertAsync(
                device.TenantId, device.Id, request.Scope, key, value ?? string.Empty, cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Envío (push) de atributos SHARED al dispositivo físico (ADR-015, P1c):
        // La BD es la fuente de verdad del estado deseado y YA está confirmada.
        // El despacho de red es best-effort y aislado: si el equipo está desconectado o el
        // transporte falla, la persistencia en Postgres NO se pierde ni revierte.
        if (request.Scope == AttributeScope.Shared && _pushDispatcher != null)
        {
            try
            {
                await _pushDispatcher.PushSharedAttributesAsync(device, request.Attributes, cancellationToken);
            }
            catch (Exception)
            {
                // Fallback silencioso a nivel de command handler: el dispatcher registra logs y métricas.
            }
        }

        return Result.Success();
    }
}
