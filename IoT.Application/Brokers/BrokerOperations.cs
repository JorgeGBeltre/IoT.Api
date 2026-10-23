using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using MediatR;

namespace IoT.Application.Brokers;

// =============================================================================
// DTO devuelto a la capa de API
// =============================================================================
public sealed record MqttBrokerDto(
    Guid Id,
    string Name,
    string Host,
    int Port,
    string Protocol,
    bool TlsEnabled,
    string AuthMethod,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? LastSeenAt);

internal static class BrokerMapper
{
    public static MqttBrokerDto ToDto(MqttBrokerConfig b) => new(
        b.Id, b.Name, b.Host, b.Port, b.Protocol, b.TlsEnabled, b.AuthMethod,
        b.IsActive, b.CreatedAt, b.UpdatedAt, b.LastSeenAt);
}

// =============================================================================
// Query: Lista de brokers del tenant
// =============================================================================
public sealed record GetBrokersQuery(Guid TenantId) : IRequest<Result<IReadOnlyList<MqttBrokerDto>>>;

public sealed class GetBrokersQueryHandler : IRequestHandler<GetBrokersQuery, Result<IReadOnlyList<MqttBrokerDto>>>
{
    private readonly IMqttBrokerConfigRepository _repo;

    public GetBrokersQueryHandler(IMqttBrokerConfigRepository repo) => _repo = repo;

    public async Task<Result<IReadOnlyList<MqttBrokerDto>>> Handle(GetBrokersQuery request, CancellationToken ct)
    {
        if (request.TenantId == Guid.Empty)
            return Result<IReadOnlyList<MqttBrokerDto>>.Failure(new Error("Broker.InvalidTenant", "TenantId no puede estar vacío."));

        var list = await _repo.GetByTenantAsync(request.TenantId, ct);
        var dtos = list.Select(BrokerMapper.ToDto).ToList();
        return Result<IReadOnlyList<MqttBrokerDto>>.Success(dtos);
    }
}

// =============================================================================
// Command: Crear broker
// =============================================================================
public sealed record CreateBrokerCommand(
    Guid TenantId,
    string Name,
    string Host,
    int Port,
    string Protocol,
    bool TlsEnabled,
    string AuthMethod,
    bool IsActive) : IRequest<Result<MqttBrokerDto>>;

public sealed class CreateBrokerCommandHandler : IRequestHandler<CreateBrokerCommand, Result<MqttBrokerDto>>
{
    private readonly IMqttBrokerConfigRepository _repo;
    private readonly IUnitOfWork _unitOfWork;

    public CreateBrokerCommandHandler(IMqttBrokerConfigRepository repo, IUnitOfWork unitOfWork)
    {
        _repo = repo;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<MqttBrokerDto>> Handle(CreateBrokerCommand request, CancellationToken ct)
    {
        var created = MqttBrokerConfig.Create(
            request.TenantId, request.Name, request.Host, request.Port,
            request.Protocol, request.TlsEnabled, request.AuthMethod, request.IsActive);
        if (created.IsFailure) return Result<MqttBrokerDto>.Failure(created.Error);

        await _repo.AddAsync(created.Value, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result<MqttBrokerDto>.Success(BrokerMapper.ToDto(created.Value));
    }
}

// =============================================================================
// Command: Actualizar broker
// =============================================================================
public sealed record UpdateBrokerCommand(
    Guid TenantId,
    Guid Id,
    string Name,
    string Host,
    int Port,
    string Protocol,
    bool TlsEnabled,
    string AuthMethod,
    bool IsActive) : IRequest<Result<MqttBrokerDto>>;

public sealed class UpdateBrokerCommandHandler : IRequestHandler<UpdateBrokerCommand, Result<MqttBrokerDto>>
{
    private readonly IMqttBrokerConfigRepository _repo;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateBrokerCommandHandler(IMqttBrokerConfigRepository repo, IUnitOfWork unitOfWork)
    {
        _repo = repo;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<MqttBrokerDto>> Handle(UpdateBrokerCommand request, CancellationToken ct)
    {
        var broker = await _repo.GetByIdAsync(request.TenantId, request.Id, ct);
        if (broker is null)
            return Result<MqttBrokerDto>.Failure(new Error("Broker.NotFound", "Broker no encontrado para el tenant actual."));

        var update = broker.Update(request.Name, request.Host, request.Port,
            request.Protocol, request.TlsEnabled, request.AuthMethod, request.IsActive);
        if (update.IsFailure) return Result<MqttBrokerDto>.Failure(update.Error);

        _repo.Update(broker);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result<MqttBrokerDto>.Success(BrokerMapper.ToDto(broker));
    }
}

// =============================================================================
// Command: Eliminar broker
// =============================================================================
public sealed record DeleteBrokerCommand(Guid TenantId, Guid Id) : IRequest<Result>;

public sealed class DeleteBrokerCommandHandler : IRequestHandler<DeleteBrokerCommand, Result>
{
    private readonly IMqttBrokerConfigRepository _repo;
    private readonly IUnitOfWork _unitOfWork;

    public DeleteBrokerCommandHandler(IMqttBrokerConfigRepository repo, IUnitOfWork unitOfWork)
    {
        _repo = repo;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result> Handle(DeleteBrokerCommand request, CancellationToken ct)
    {
        var broker = await _repo.GetByIdAsync(request.TenantId, request.Id, ct);
        if (broker is null)
            return Result.Failure(new Error("Broker.NotFound", "Broker no encontrado para el tenant actual."));

        _repo.Remove(broker);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
