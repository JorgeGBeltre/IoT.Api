using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.TenantSettings;

// =============================================================================
// DTO
// =============================================================================
public sealed record TenantSettingsDto(
    int RetentionDays,
    bool AllowOfflineQueue,
    string MapProvider,
    string WebhookUrl);

// =============================================================================
// Query: Lee configuración del tenant actual. Si no existe, crea default.
// =============================================================================
public sealed record GetTenantSettingsQuery(Guid TenantId) : IRequest<Result<TenantSettingsDto>>;

public sealed class GetTenantSettingsQueryHandler : IRequestHandler<GetTenantSettingsQuery, Result<TenantSettingsDto>>
{
    private readonly ITenantSettingsRepository _repo;
    private readonly IUnitOfWork _unitOfWork;

    public GetTenantSettingsQueryHandler(ITenantSettingsRepository repo, IUnitOfWork unitOfWork)
    {
        _repo = repo;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<TenantSettingsDto>> Handle(GetTenantSettingsQuery request, CancellationToken ct)
    {
        if (request.TenantId == Guid.Empty)
            return Result<TenantSettingsDto>.Failure(new Error("TenantSettings.InvalidTenant", "TenantId no puede estar vacío."));

        var existing = await _repo.GetByTenantAsync(request.TenantId, ct);
        if (existing is null)
        {
            var created = Domain.Entities.TenantSettings.CreateDefault(request.TenantId);
            if (created.IsFailure) return Result<TenantSettingsDto>.Failure(created.Error);
            await _repo.AddAsync(created.Value, ct);
            await _unitOfWork.SaveChangesAsync(ct);
            existing = created.Value;
        }

        return Result<TenantSettingsDto>.Success(new TenantSettingsDto(
            existing.RetentionDays, existing.AllowOfflineQueue, existing.MapProvider, existing.WebhookUrl));
    }
}

// =============================================================================
// Command: Actualiza configuración (crea default si no existe)
// =============================================================================
public sealed record UpdateTenantSettingsCommand(
    Guid TenantId,
    int RetentionDays,
    bool AllowOfflineQueue,
    string MapProvider,
    string WebhookUrl) : IRequest<Result<TenantSettingsDto>>;

public sealed class UpdateTenantSettingsCommandHandler : IRequestHandler<UpdateTenantSettingsCommand, Result<TenantSettingsDto>>
{
    private readonly ITenantSettingsRepository _repo;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateTenantSettingsCommandHandler(ITenantSettingsRepository repo, IUnitOfWork unitOfWork)
    {
        _repo = repo;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<TenantSettingsDto>> Handle(UpdateTenantSettingsCommand request, CancellationToken ct)
    {
        if (request.TenantId == Guid.Empty)
            return Result<TenantSettingsDto>.Failure(new Error("TenantSettings.InvalidTenant", "TenantId no puede estar vacío."));

        var existing = await _repo.GetByTenantAsync(request.TenantId, ct);
        if (existing is null)
        {
            var createRes = Domain.Entities.TenantSettings.CreateDefault(request.TenantId);
            if (createRes.IsFailure) return Result<TenantSettingsDto>.Failure(createRes.Error);
            existing = createRes.Value;
            await _repo.AddAsync(existing, ct);
        }

        var upd = existing.Update(request.RetentionDays, request.AllowOfflineQueue,
            request.MapProvider, request.WebhookUrl ?? string.Empty);
        if (upd.IsFailure) return Result<TenantSettingsDto>.Failure(upd.Error);

        _repo.Update(existing);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result<TenantSettingsDto>.Success(new TenantSettingsDto(
            existing.RetentionDays, existing.AllowOfflineQueue, existing.MapProvider, existing.WebhookUrl));
    }
}
