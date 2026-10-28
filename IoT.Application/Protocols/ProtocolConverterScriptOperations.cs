using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Protocols.Dynamic;
using MediatR;

namespace IoT.Application.Protocols;

// =============================================================================
// DTO
// =============================================================================
public sealed record ProtocolConverterScriptDto(
    string ProfileKey,
    bool IsEnabled,
    string UplinkScript,
    string? DownlinkScript,
    DateTime UpdatedAt);

// =============================================================================
// Query: lista los convertidores del tenant actual
// =============================================================================
public sealed record ListConverterScriptsQuery(Guid TenantId) : IRequest<Result<IReadOnlyList<ProtocolConverterScriptDto>>>;

public sealed class ListConverterScriptsQueryHandler
    : IRequestHandler<ListConverterScriptsQuery, Result<IReadOnlyList<ProtocolConverterScriptDto>>>
{
    private readonly IProtocolConverterScriptRepository _repo;

    public ListConverterScriptsQueryHandler(IProtocolConverterScriptRepository repo) => _repo = repo;

    public async Task<Result<IReadOnlyList<ProtocolConverterScriptDto>>> Handle(ListConverterScriptsQuery request, CancellationToken ct)
    {
        if (request.TenantId == Guid.Empty)
            return Result<IReadOnlyList<ProtocolConverterScriptDto>>.Failure(new Error("ConverterScript.InvalidTenant", "TenantId no puede estar vacío."));

        var items = await _repo.GetByTenantAsync(request.TenantId, ct);
        var dtos = items.Select(ToDto).ToList();
        return Result<IReadOnlyList<ProtocolConverterScriptDto>>.Success(dtos);
    }

    internal static ProtocolConverterScriptDto ToDto(ProtocolConverterScript s) =>
        new(s.ProfileKey, s.IsEnabled, s.UplinkScript, s.DownlinkScript, s.UpdatedAt);
}

// =============================================================================
// Query: lee un convertidor concreto por clave de perfil
// =============================================================================
public sealed record GetConverterScriptQuery(Guid TenantId, string ProfileKey) : IRequest<Result<ProtocolConverterScriptDto>>;

public sealed class GetConverterScriptQueryHandler
    : IRequestHandler<GetConverterScriptQuery, Result<ProtocolConverterScriptDto>>
{
    private readonly IProtocolConverterScriptRepository _repo;

    public GetConverterScriptQueryHandler(IProtocolConverterScriptRepository repo) => _repo = repo;

    public async Task<Result<ProtocolConverterScriptDto>> Handle(GetConverterScriptQuery request, CancellationToken ct)
    {
        if (request.TenantId == Guid.Empty)
            return Result<ProtocolConverterScriptDto>.Failure(new Error("ConverterScript.InvalidTenant", "TenantId no puede estar vacío."));

        var key = ProtocolConverterScript.NormalizeKey(request.ProfileKey);
        var existing = await _repo.GetByTenantAndProfileKeyAsync(request.TenantId, key, ct);
        if (existing is null)
            return Result<ProtocolConverterScriptDto>.Failure(new Error("ConverterScript.NotFound", $"No hay convertidor para el perfil '{key}'."));

        return Result<ProtocolConverterScriptDto>.Success(ListConverterScriptsQueryHandler.ToDto(existing));
    }
}

// =============================================================================
// Command: carga (crea o actualiza) el convertidor de un perfil del tenant.
// VALIDA el script en el sandbox Jint ANTES de persistir; rechaza JS inválido o que exceda el sandbox.
// =============================================================================
public sealed record UpsertConverterScriptCommand(
    Guid TenantId,
    string ProfileKey,
    string UplinkScript,
    string? DownlinkScript,
    bool IsEnabled,
    string? SamplePayload = null) : IRequest<Result<ProtocolConverterScriptDto>>;

public sealed class UpsertConverterScriptCommandHandler
    : IRequestHandler<UpsertConverterScriptCommand, Result<ProtocolConverterScriptDto>>
{
    private readonly IProtocolConverterScriptRepository _repo;
    private readonly IUnitOfWork _unitOfWork;

    public UpsertConverterScriptCommandHandler(IProtocolConverterScriptRepository repo, IUnitOfWork unitOfWork)
    {
        _repo = repo;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<ProtocolConverterScriptDto>> Handle(UpsertConverterScriptCommand request, CancellationToken ct)
    {
        if (request.TenantId == Guid.Empty)
            return Result<ProtocolConverterScriptDto>.Failure(new Error("ConverterScript.InvalidTenant", "TenantId no puede estar vacío."));

        // 1. Validación en sandbox SIN persistir: JS inválido o que exceda el sandbox => fallo (400).
        var uplinkValidation = ScriptConverterValidator.ValidateUplink(request.UplinkScript, request.SamplePayload);
        if (uplinkValidation.IsFailure)
            return Result<ProtocolConverterScriptDto>.Failure(uplinkValidation.Error);

        if (!string.IsNullOrWhiteSpace(request.DownlinkScript))
        {
            var downlinkValidation = ScriptConverterValidator.ValidateDownlink(request.DownlinkScript);
            if (downlinkValidation.IsFailure)
                return Result<ProtocolConverterScriptDto>.Failure(downlinkValidation.Error);
        }

        // 2. Upsert acotado al tenant de la sesión (sin acceso cross-tenant: RLS + filtro EF).
        var key = ProtocolConverterScript.NormalizeKey(request.ProfileKey);
        var existing = await _repo.GetByTenantAndProfileKeyAsync(request.TenantId, key, ct);
        if (existing is null)
        {
            var created = ProtocolConverterScript.Create(
                request.TenantId, request.ProfileKey, request.UplinkScript, request.DownlinkScript, request.IsEnabled);
            if (created.IsFailure) return Result<ProtocolConverterScriptDto>.Failure(created.Error);
            await _repo.AddAsync(created.Value, ct);
            existing = created.Value;
        }
        else
        {
            var updated = existing.Update(request.UplinkScript, request.DownlinkScript, request.IsEnabled);
            if (updated.IsFailure) return Result<ProtocolConverterScriptDto>.Failure(updated.Error);
            _repo.Update(existing);
        }

        await _unitOfWork.SaveChangesAsync(ct);
        return Result<ProtocolConverterScriptDto>.Success(ListConverterScriptsQueryHandler.ToDto(existing));
    }
}
