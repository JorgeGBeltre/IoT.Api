using IoT.Application.Common.Interfaces;
using IoT.Application.Users.Dtos;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IoT.Application.Users.Commands;

/// <summary>
/// Alta de un usuario EN RUNTIME asignado a un nodo Customer del subárbol de la sesión (E8.6a), para
/// que cada nivel de la jerarquía tenga su propio login. El backend genera una contraseña TEMPORAL
/// fuerte, la guarda SÓLO como hash (BCrypt) y la devuelve en claro UNA vez
/// (<see cref="CreateUserResult.TemporaryPassword"/>); el usuario entra con ella y el flujo
/// <c>ChangeTemporaryPassword</c> lo obliga a cambiarla (<c>MustChangePassword = true</c>).
///
/// El alta queda CONFINADA al subárbol por la RLS de PostgreSQL (WITH CHECK de <c>users</c> por
/// <c>app.customer_path</c>) y, en defensa en profundidad, por la comprobación
/// <see cref="TenantContextExtensions.IsPathWithinSessionSubtree"/> en código —exactamente igual que
/// <c>CreateCustomer</c> y <c>AssignDeviceToCustomer</c>—. Un administrador acotado a un subárbol NO
/// puede crear usuarios fuera de él ni en otro tenant.
///
/// <paramref name="PerformedByUserId"/>/<paramref name="PerformedByUsername"/> identifican al actor
/// para la auditoría (los rellena el controlador desde los claims); no afectan al alcance, que lo
/// imponen la RLS y el contexto de tenant/customer de la sesión.
/// </summary>
public sealed record CreateUserCommand(
    string Username,
    string Email,
    string FullName,
    string RoleName,
    Guid? TargetCustomerId = null,
    Guid? PerformedByUserId = null,
    string? PerformedByUsername = null) : IRequest<Result<CreateUserResult>>;

public sealed class CreateUserCommandHandler : IRequestHandler<CreateUserCommand, Result<CreateUserResult>>
{
    private readonly IUserRepository _userRepository;
    private readonly ICustomerRepository _customerRepository;
    private readonly ITenantContext _tenantContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<CreateUserCommandHandler> _logger;

    public CreateUserCommandHandler(
        IUserRepository userRepository,
        ICustomerRepository customerRepository,
        ITenantContext tenantContext,
        IPasswordHasher passwordHasher,
        IAuditLogRepository auditLogRepository,
        IUnitOfWork unitOfWork,
        ILogger<CreateUserCommandHandler> logger)
    {
        _userRepository = userRepository;
        _customerRepository = customerRepository;
        _tenantContext = tenantContext;
        _passwordHasher = passwordHasher;
        _auditLogRepository = auditLogRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<CreateUserResult>> Handle(CreateUserCommand request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.TenantId;
        if (tenantId == Guid.Empty)
        {
            return Result<CreateUserResult>.Failure(new Error("User.NoTenant", "No hay tenant resuelto en la sesión."));
        }

        // ROL: sólo roles de nivel tenant asignables (Admin/Operator/Viewer). Un rol desconocido o de
        // plataforma/sistema se rechaza: un invitador acotado no puede escalar privilegios ni otorgar un
        // rol fuera del conjunto cerrado. Se normaliza la caja para el claim de rol del JWT.
        var canonicalRole = UserRoles.Normalize(request.RoleName);
        if (canonicalRole is null)
        {
            return Result<CreateUserResult>.Failure(new Error(
                "User.InvalidRole",
                $"El rol '{request.RoleName}' no es asignable. Roles válidos: {string.Join(", ", UserRoles.Assignable)}."));
        }

        // CUSTOMER DESTINO: el indicado, o el nodo de la sesión; si la sesión no está acotada
        // (raíz / token antiguo), el raíz del tenant. Se carga ACOTADO (RLS + EF): un nodo de otro
        // subárbol o de otro tenant devuelve null → no visible. Mismo patrón que CreateCustomer.
        var targetId = request.TargetCustomerId
            ?? _tenantContext.CustomerId
            ?? await _customerRepository.GetRootCustomerIdAsync(tenantId, cancellationToken);

        if (targetId is null || targetId.Value == Guid.Empty)
        {
            return Result<CreateUserResult>.Failure(new Error(
                "User.NoCustomerNode",
                "No se pudo resolver el customer destino (el tenant no tiene nodo raíz)."));
        }

        var target = await _customerRepository.GetByIdScopedAsync(targetId.Value, cancellationToken);
        if (target is null)
        {
            return Result<CreateUserResult>.Failure(new Error(
                "User.TargetCustomerNotFound",
                "El customer destino no existe o está fuera del subárbol visible."));
        }

        // Defensa en profundidad: mismo tenant (la RLS/EF ya lo garantizan).
        if (target.TenantId != tenantId)
        {
            return Result<CreateUserResult>.Failure(new Error(
                "User.CustomerTenantMismatch",
                "El customer destino pertenece a otro tenant."));
        }

        // Defensa en profundidad (la línea dura es el WITH CHECK de la RLS de users): el nodo destino
        // DEBE caer en el subárbol de la sesión. Si está acotada (CustomerPath no vacío) y el Path del
        // destino no desciende de él, se rechaza en código antes de tocar la BD. Raíz/sistema/token
        // antiguo: sin recorte (NO DISRUPTIVO).
        if (!_tenantContext.IsPathWithinSessionSubtree(target.Path))
        {
            return Result<CreateUserResult>.Failure(new Error(
                "User.OutsideSubtree",
                "El customer destino cae fuera del subárbol de la sesión."));
        }

        // UNICIDAD TENANT-WIDE: el índice único es (TenantId, Username) —ámbito TENANT, no subárbol—. El
        // índice (TenantId, Email) NO es único, de modo que el email puede repetirse y no se valida.
        //
        // La precomprobación corre en SCOPE DE SISTEMA (patrón idéntico a GetRootCustomerIdAsync): aunque
        // GetByUsernameAsync usa IgnoreQueryFilters (apaga el filtro EF), la RLS de PostgreSQL con
        // app.customer_path fijado seguiría ocultando usuarios de subárboles HERMANOS a un admin acotado;
        // el pre-check devolvería null y el INSERT violaría el índice único (23505 → 500). Bajo
        // BeginSystemOperation la RLS no recorta por subárbol y el pre-check ve TODO el tenant, acorde al
        // índice único. El resultado se usa SÓLO para la comprobación de existencia: NUNCA se expone al
        // llamante dato alguno del usuario de otro subárbol (sin fuga). El scope normal se restaura al
        // salir del using.
        var username = request.Username?.Trim() ?? string.Empty;
        bool usernameTaken;
        using (_tenantContext.BeginSystemOperation())
        {
            var existing = await _userRepository.GetByUsernameAsync(tenantId, username, cancellationToken);
            usernameTaken = existing is not null;
        }

        if (usernameTaken)
        {
            return Result<CreateUserResult>.Failure(new Error(
                "User.UsernameTaken",
                $"Ya existe un usuario con el nombre '{username}' en el tenant."));
        }

        // CONTRASEÑA TEMPORAL: CSPRNG + complejidad garantizada; se hashea con BCrypt. El texto plano
        // sólo vive en memoria y se devuelve UNA vez. NUNCA se registra ni se persiste en claro.
        var temporaryPassword = TemporaryPasswordGenerator.Generate();
        try
        {
            // El generador ya garantiza la complejidad; esta comprobación es fail-closed por si la
            // política endurece en el futuro (jamás emitir una clave que el login no aceptaría).
            _passwordHasher.ValidateComplexity(temporaryPassword);
        }
        catch (InvalidOperationException ex)
        {
            return Result<CreateUserResult>.Failure(new Error("User.PasswordPolicy", ex.Message));
        }

        var passwordHash = _passwordHasher.Hash(temporaryPassword);

        var createResult = User.Create(
            tenantId,
            username,
            request.Email,
            passwordHash,
            request.FullName,
            canonicalRole,
            mustChangePassword: true);

        if (createResult.IsFailure)
        {
            return Result<CreateUserResult>.Failure(createResult.Error);
        }

        var user = createResult.Value;
        user.AssignToCustomer(target.Id);

        await _userRepository.AddAsync(user, cancellationToken);

        // AUDITORÍA: quién (actor), a quién (usuario creado), qué customer/rol. SIN la contraseña.
        await _auditLogRepository.AddAsync(new AuditLog(
            Guid.NewGuid(),
            tenantId,
            request.PerformedByUserId,
            string.IsNullOrWhiteSpace(request.PerformedByUsername) ? "sistema" : request.PerformedByUsername!,
            "UserCreated",
            $"Alta de usuario '{user.Username}' (id {user.Id}) con rol '{canonicalRole}' en el customer {target.Id}.",
            ipAddress: null,
            userAgent: null), cancellationToken);

        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // DEFENSA TOCTOU / CARRERA: entre el pre-check y el INSERT otra transacción pudo insertar el
            // mismo username, o dos altas concurrentes del mismo username compiten. El índice único
            // (TenantId, Username) rechaza la segunda con SQLSTATE 23505. Se traduce al fallo de negocio
            // User.UsernameTaken (el controlador lo mapea a 400) en vez de propagar un 500 genérico, de
            // modo que el resultado observable es SIEMPRE un 400 limpio. No se registra la contraseña.
            _logger.LogWarning(
                "Alta de usuario '{Username}' rechazada por colisión de unicidad (23505) detectada en el guardado.",
                username);
            return Result<CreateUserResult>.Failure(new Error(
                "User.UsernameTaken",
                $"Ya existe un usuario con el nombre '{username}' en el tenant."));
        }

        // Nunca se registra la contraseña ni el hash: sólo metadatos no sensibles.
        _logger.LogInformation(
            "Usuario {Username} (id {UserId}) creado con rol {Role} en el customer {CustomerId} por {Actor}.",
            user.Username, user.Id, canonicalRole, target.Id,
            string.IsNullOrWhiteSpace(request.PerformedByUsername) ? "sistema" : request.PerformedByUsername);

        return Result<CreateUserResult>.Success(new CreateUserResult(
            user.Id,
            user.Username,
            user.Email,
            user.FullName,
            user.RoleName,
            target.Id,
            user.MustChangePassword,
            temporaryPassword));
    }
}
