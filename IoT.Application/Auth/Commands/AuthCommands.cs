using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using MediatR;
using Microsoft.Extensions.Logging;

namespace IoT.Application.Auth.Commands;

public sealed record LoginCommand(
    string UsernameOrEmail,
    string Password,
    Guid? TenantId = null,
    string? IpAddress = null,
    string? UserAgent = null) : IRequest<Result<AuthTokenResult>>;

public sealed class LoginCommandHandler : IRequestHandler<LoginCommand, Result<AuthTokenResult>>
{
    private readonly IUserRepository _userRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<LoginCommandHandler> _logger;
    private readonly ITenantContext? _tenantContext;
    private readonly ICustomerRepository? _customerRepository;
    private readonly IPermissionService? _permissionService;

    public LoginCommandHandler(
        IUserRepository userRepository,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator jwtTokenGenerator,
        IAuditLogRepository auditLogRepository,
        IUnitOfWork unitOfWork,
        ILogger<LoginCommandHandler> logger,
        ITenantContext? tenantContext = null,
        ICustomerRepository? customerRepository = null,
        IPermissionService? permissionService = null)
    {
        _userRepository = userRepository;
        _passwordHasher = passwordHasher;
        _jwtTokenGenerator = jwtTokenGenerator;
        _auditLogRepository = auditLogRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _tenantContext = tenantContext;
        _customerRepository = customerRepository;
        _permissionService = permissionService;
    }

    public async Task<Result<AuthTokenResult>> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        // Login anónimo: sin tenant ambiental. Operación de sistema para que bajo
        // Platform:Rls:Enforce el lookup (global o por tenant con IgnoreQueryFilters) y la
        // auditoría no sean bloqueados por el interceptor RLS (ADR-003 D6).
        if (_tenantContext != null) _tenantContext.IsSystemOperation = true;

        User? user = null;
        if (request.TenantId.HasValue && request.TenantId.Value != Guid.Empty)
        {
            user = await _userRepository.GetByUsernameAsync(request.TenantId.Value, request.UsernameOrEmail, cancellationToken)
                ?? await _userRepository.GetByEmailAsync(request.TenantId.Value, request.UsernameOrEmail, cancellationToken);
        }
        else
        {
            user = await _userRepository.FindByUsernameOrEmailGlobalAsync(request.UsernameOrEmail, cancellationToken);
        }

        if (user is null || !user.IsActive)
        {
            _logger.LogWarning("Failed login attempt for unknown/inactive user: {Identifier}", request.UsernameOrEmail);
            return Result<AuthTokenResult>.Failure(new Error("Auth.InvalidCredentials", "Credenciales incorrectas o usuario inactivo."));
        }

        if (user.IsLockedOut)
        {
            _logger.LogWarning("Account locked out for user: {Username}", user.Username);
            return Result<AuthTokenResult>.Failure(new Error("Auth.LockedOut", "La cuenta está bloqueada temporalmente por intentos fallidos. Intente más tarde."));
        }

        bool isPasswordValid = _passwordHasher.Verify(request.Password, user.PasswordHash);
        if (!isPasswordValid)
        {
            user.RecordFailedLogin();
            await _auditLogRepository.AddAsync(new AuditLog(
                Guid.NewGuid(), user.TenantId, user.Id, user.Username, "LoginFailed", "Contraseña incorrecta.", request.IpAddress, request.UserAgent), cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            _logger.LogWarning("Invalid password for user: {Username}", user.Username);
            return Result<AuthTokenResult>.Failure(new Error("Auth.InvalidCredentials", "Credenciales incorrectas o usuario inactivo."));
        }

        user.RecordSuccessfulLogin();
        await _auditLogRepository.AddAsync(new AuditLog(
            Guid.NewGuid(), user.TenantId, user.Id, user.Username, "LoginSuccess", "Inicio de sesión exitoso.", request.IpAddress, request.UserAgent), cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Jerarquía multinivel (ADR-013): resolver el Path del nodo del usuario EN EL SERVIDOR para
        // emitirlo como claim firmado. Corre en el scope de sistema del login (IgnoreQueryFilters).
        // Si el usuario no tiene nodo (o no hay repositorio en un test), no se emiten claims de
        // customer y el token no restringe el subárbol (NO DISRUPTIVO).
        string? customerPath = null;
        if (_customerRepository is not null && user.CustomerId.HasValue && user.CustomerId.Value != Guid.Empty)
        {
            var customer = await _customerRepository.GetByIdAsync(user.CustomerId.Value, cancellationToken);
            customerPath = customer?.Path;
        }

        var permissions = _permissionService != null
            ? await _permissionService.GetEffectivePermissionsAsync(user.TenantId, user.Id, user.RoleName, user.RoleId, cancellationToken)
            : StandardPermissions.GetDefaultPermissionsForRole(user.RoleName);

        var tokenResult = _jwtTokenGenerator.GenerateToken(user, user.TenantId, user.CustomerId, customerPath, permissions);
        _logger.LogInformation("User {Username} logged in successfully for Tenant {TenantId} with {PermCount} permissions", user.Username, user.TenantId, permissions.Count);

        return Result<AuthTokenResult>.Success(tokenResult);
    }
}

public sealed record ForgotPasswordCommand(
    string Email,
    Guid TenantId,
    string? IpAddress = null,
    string? UserAgent = null) : IRequest<Result<string>>;

public sealed class ForgotPasswordCommandHandler : IRequestHandler<ForgotPasswordCommand, Result<string>>
{
    private readonly IUserRepository _userRepository;
    private readonly IOtpRepository _otpRepository;
    private readonly IEmailService _emailService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<ForgotPasswordCommandHandler> _logger;
    private readonly ITenantContext? _tenantContext;

    public ForgotPasswordCommandHandler(
        IUserRepository userRepository,
        IOtpRepository otpRepository,
        IEmailService emailService,
        IUnitOfWork unitOfWork,
        ILogger<ForgotPasswordCommandHandler> logger,
        ITenantContext? tenantContext = null)
    {
        _userRepository = userRepository;
        _otpRepository = otpRepository;
        _emailService = emailService;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _tenantContext = tenantContext;
    }

    public async Task<Result<string>> Handle(ForgotPasswordCommand request, CancellationToken cancellationToken)
    {
        // Recuperación anónima: sin tenant ambiental. Operación de sistema (ADR-003 D6).
        if (_tenantContext != null) _tenantContext.IsSystemOperation = true;

        var user = await _userRepository.GetByEmailAsync(request.TenantId, request.Email, cancellationToken);
        if (user is null || !user.IsActive)
        {
            // Security best practice: Do not disclose if email exists
            return Result<string>.Success("Si el correo está registrado, se ha enviado un código de recuperación OTP.");
        }

        var (otpEntity, plainCode) = OtpCode.Generate(request.TenantId, user.Email, user.Id, OtpPurpose.PasswordReset, expirationMinutes: 15);
        await _otpRepository.AddAsync(otpEntity, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await _emailService.SendPasswordResetOtpEmailAsync(user.Email, user.Username, plainCode, cancellationToken);
        _logger.LogInformation("Password reset OTP generated and dispatched via MailKit for {Email}", user.Email);

        return Result<string>.Success("Código de recuperación OTP enviado exitosamente al correo.");
    }
}

public sealed record VerifyOtpCommand(
    string Email,
    string OtpCode,
    Guid TenantId) : IRequest<Result<string>>;

public sealed class VerifyOtpCommandHandler : IRequestHandler<VerifyOtpCommand, Result<string>>
{
    private readonly IOtpRepository _otpRepository;
    private readonly IUserRepository _userRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext? _tenantContext;

    public VerifyOtpCommandHandler(IOtpRepository otpRepository, IUserRepository userRepository, IUnitOfWork unitOfWork, ITenantContext? tenantContext = null)
    {
        _otpRepository = otpRepository;
        _userRepository = userRepository;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
    }

    public async Task<Result<string>> Handle(VerifyOtpCommand request, CancellationToken cancellationToken)
    {
        // Verificación anónima de OTP: sin tenant ambiental. Operación de sistema (ADR-003 D6).
        if (_tenantContext != null) _tenantContext.IsSystemOperation = true;

        var activeOtp = await _otpRepository.GetActiveOtpAsync(request.TenantId, request.Email, OtpPurpose.PasswordReset, cancellationToken);
        if (activeOtp is null)
        {
            return Result<string>.Failure(new Error("Otp.NotFound", "No hay un código OTP activo o ya ha expirado."));
        }

        bool isValid = activeOtp.Verify(request.OtpCode);
        if (!isValid)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Result<string>.Failure(new Error("Otp.Invalid", "Código OTP incorrecto o expirado."));
        }

        var (resetTokenEntity, rawToken) = PasswordResetToken.Create(request.TenantId, activeOtp.UserId ?? Guid.Empty, expirationMinutes: 30);
        await _otpRepository.AddResetTokenAsync(resetTokenEntity, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<string>.Success(rawToken);
    }
}

public sealed record ResetPasswordCommand(
    string Email,
    string ResetToken,
    string NewPassword,
    Guid TenantId,
    string? IpAddress = null,
    string? UserAgent = null) : IRequest<Result<string>>;

public sealed class ResetPasswordCommandHandler : IRequestHandler<ResetPasswordCommand, Result<string>>
{
    private readonly IUserRepository _userRepository;
    private readonly IOtpRepository _otpRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IEmailService _emailService;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext? _tenantContext;

    public ResetPasswordCommandHandler(
        IUserRepository userRepository,
        IOtpRepository otpRepository,
        IPasswordHasher passwordHasher,
        IEmailService emailService,
        IAuditLogRepository auditLogRepository,
        IUnitOfWork unitOfWork,
        ITenantContext? tenantContext = null)
    {
        _userRepository = userRepository;
        _otpRepository = otpRepository;
        _passwordHasher = passwordHasher;
        _emailService = emailService;
        _auditLogRepository = auditLogRepository;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
    }

    public async Task<Result<string>> Handle(ResetPasswordCommand request, CancellationToken cancellationToken)
    {
        // Reset anónimo con token: sin tenant ambiental. Operación de sistema (ADR-003 D6).
        if (_tenantContext != null) _tenantContext.IsSystemOperation = true;

        var user = await _userRepository.GetByEmailAsync(request.TenantId, request.Email, cancellationToken);
        if (user is null)
            return Result<string>.Failure(new Error("User.NotFound", "Usuario no encontrado."));

        var activeToken = await _otpRepository.GetActiveResetTokenAsync(request.TenantId, user.Id, cancellationToken);
        if (activeToken is null || !activeToken.Verify(request.ResetToken))
        {
            return Result<string>.Failure(new Error("ResetToken.Invalid", "Token de restablecimiento inválido o expirado."));
        }

        try
        {
            _passwordHasher.ValidateComplexity(request.NewPassword);
        }
        catch (InvalidOperationException ex)
        {
            return Result<string>.Failure(new Error("Password.PolicyViolation", ex.Message));
        }

        string newHash = _passwordHasher.Hash(request.NewPassword);
        user.UpdatePassword(newHash);

        await _auditLogRepository.AddAsync(new AuditLog(
            Guid.NewGuid(), user.TenantId, user.Id, user.Username, "PasswordReset", "Contraseña restablecida exitosamente.", request.IpAddress, request.UserAgent), cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await _emailService.SendPasswordChangedNotificationEmailAsync(user.Email, user.Username, request.UserAgent, request.IpAddress, cancellationToken);

        return Result<string>.Success("Contraseña restablecida exitosamente. Ahora puede iniciar sesión con su nueva clave.");
    }
}

public sealed record ChangeTemporaryPasswordCommand(
    string Username,
    string CurrentPassword,
    string NewPassword,
    Guid TenantId,
    string? IpAddress = null,
    string? UserAgent = null) : IRequest<Result<AuthTokenResult>>;

public sealed class ChangeTemporaryPasswordCommandHandler : IRequestHandler<ChangeTemporaryPasswordCommand, Result<AuthTokenResult>>
{
    private readonly IUserRepository _userRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext? _tenantContext;
    private readonly ICustomerRepository? _customerRepository;
    private readonly IPermissionService? _permissionService;

    public ChangeTemporaryPasswordCommandHandler(
        IUserRepository userRepository,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator jwtTokenGenerator,
        IAuditLogRepository auditLogRepository,
        IUnitOfWork unitOfWork,
        ITenantContext? tenantContext = null,
        ICustomerRepository? customerRepository = null,
        IPermissionService? permissionService = null)
    {
        _userRepository = userRepository;
        _passwordHasher = passwordHasher;
        _jwtTokenGenerator = jwtTokenGenerator;
        _auditLogRepository = auditLogRepository;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
        _customerRepository = customerRepository;
        _permissionService = permissionService;
    }

    public async Task<Result<AuthTokenResult>> Handle(ChangeTemporaryPasswordCommand request, CancellationToken cancellationToken)
    {
        // Cambio de clave temporal (pre-sesión): sin tenant ambiental. Operación de sistema (ADR-003 D6).
        if (_tenantContext != null) _tenantContext.IsSystemOperation = true;

        User? user = request.TenantId != Guid.Empty
            ? await _userRepository.GetByUsernameAsync(request.TenantId, request.Username, cancellationToken)
            : await _userRepository.FindByUsernameOrEmailGlobalAsync(request.Username, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Result<AuthTokenResult>.Failure(new Error("Auth.InvalidCredentials", "Credenciales incorrectas o usuario inactivo."));
        }

        if (user.IsLockedOut)
        {
            return Result<AuthTokenResult>.Failure(new Error("Auth.LockedOut", "La cuenta está bloqueada temporalmente por intentos fallidos. Intente más tarde."));
        }

        if (!user.MustChangePassword)
        {
            return Result<AuthTokenResult>.Failure(new Error("Auth.InvalidOperation", "El usuario no requiere cambio de contraseña temporal."));
        }

        if (!_passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
        {
            user.RecordFailedLogin();
            await _auditLogRepository.AddAsync(new AuditLog(
                Guid.NewGuid(), user.TenantId, user.Id, user.Username, "ChangeTemporaryPasswordFailed", "Contraseña temporal incorrecta.", request.IpAddress, request.UserAgent), cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result<AuthTokenResult>.Failure(new Error("Auth.InvalidCredentials", "La contraseña temporal actual no es válida."));
        }

        try
        {
            _passwordHasher.ValidateComplexity(request.NewPassword);
        }
        catch (InvalidOperationException ex)
        {
            return Result<AuthTokenResult>.Failure(new Error("Password.PolicyViolation", ex.Message));
        }

        string newHash = _passwordHasher.Hash(request.NewPassword);
        user.UpdatePassword(newHash);

        await _auditLogRepository.AddAsync(new AuditLog(
            Guid.NewGuid(), user.TenantId, user.Id, user.Username, "ChangeTemporaryPassword", "Contraseña temporal cambiada exitosamente.", request.IpAddress, request.UserAgent), cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Igual que en el login: el token reemitido tras el cambio de clave temporal lleva el nodo
        // del usuario (ADR-013), para que la sesión quede acotada a su subárbol desde el principio.
        string? customerPath = null;
        if (_customerRepository is not null && user.CustomerId.HasValue && user.CustomerId.Value != Guid.Empty)
        {
            var customer = await _customerRepository.GetByIdAsync(user.CustomerId.Value, cancellationToken);
            customerPath = customer?.Path;
        }

        // Igual que en el login: el token reemitido tras el cambio de clave temporal lleva los
        // permisos efectivos del usuario (RBAC fino, ADR-013 D3). Antes se usaba la sobrecarga sin
        // permisos, dejando la sesión post-cambio con autorización sólo-por-rol hasta el próximo
        // login (H1/A de la auditoría RBAC). Fail-closed: sin IPermissionService (p. ej. en tests)
        // se cae a los permisos por defecto del rol.
        var permissions = _permissionService != null
            ? await _permissionService.GetEffectivePermissionsAsync(user.TenantId, user.Id, user.RoleName, user.RoleId, cancellationToken)
            : StandardPermissions.GetDefaultPermissionsForRole(user.RoleName);

        var token = _jwtTokenGenerator.GenerateToken(user, user.TenantId, user.CustomerId, customerPath, permissions);
        return Result<AuthTokenResult>.Success(token);
    }
}

public sealed record RefreshTokenCommand(
    string RefreshToken,
    string? IpAddress = null,
    string? UserAgent = null) : IRequest<Result<AuthTokenResult>>;

public sealed class RefreshTokenCommandHandler : IRequestHandler<RefreshTokenCommand, Result<AuthTokenResult>>
{
    private readonly IUserRepository _userRepository;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;
    private readonly ILogger<RefreshTokenCommandHandler> _logger;
    private readonly ITenantContext? _tenantContext;
    private readonly ITenantRepository? _tenantRepository;
    private readonly ICustomerRepository? _customerRepository;
    private readonly IPermissionService? _permissionService;

    public RefreshTokenCommandHandler(
        IUserRepository userRepository,
        IJwtTokenGenerator jwtTokenGenerator,
        ILogger<RefreshTokenCommandHandler> logger,
        ITenantContext? tenantContext = null,
        ITenantRepository? tenantRepository = null,
        ICustomerRepository? customerRepository = null,
        IPermissionService? permissionService = null)
    {
        _userRepository = userRepository;
        _jwtTokenGenerator = jwtTokenGenerator;
        _logger = logger;
        _tenantContext = tenantContext;
        _tenantRepository = tenantRepository;
        _customerRepository = customerRepository;
        _permissionService = permissionService;
    }

    public async Task<Result<AuthTokenResult>> Handle(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Result<AuthTokenResult>.Failure(new Error("Auth.InvalidRefreshToken", "El token de renovación es obligatorio."));
        }

        var claims = _jwtTokenGenerator.ValidateRefreshToken(request.RefreshToken);
        if (claims is null)
        {
            return Result<AuthTokenResult>.Failure(new Error("Auth.InvalidRefreshToken", "La sesión ha expirado o el token de renovación no es válido."));
        }

        if (_tenantContext != null)
        {
            _tenantContext.IsSystemOperation = true;
        }

        if (_tenantRepository is not null)
        {
            var tenant = await _tenantRepository.GetByIdAsync(claims.TenantId, cancellationToken);
            if (tenant is null || !tenant.IsActive)
            {
                return Result<AuthTokenResult>.Failure(new Error("Auth.TenantInactive", "La organización asociada a la cuenta no está activa."));
            }
        }

        var user = await _userRepository.GetByIdSystemAsync(claims.TenantId, claims.UserId, cancellationToken)
            ?? await _userRepository.GetByIdAsync(claims.UserId, cancellationToken);

        if (user is null || user.TenantId != claims.TenantId || !user.IsActive)
        {
            _logger.LogWarning("Refresh token rejected for inactive or missing user {UserId}", claims.UserId);
            return Result<AuthTokenResult>.Failure(new Error("Auth.InvalidRefreshToken", "La cuenta no está activa o la sesión fue revocada."));
        }

        if (user.IsLockedOut)
        {
            return Result<AuthTokenResult>.Failure(new Error("Auth.LockedOut", "La cuenta está bloqueada temporalmente."));
        }

        var expectedBytes = Encoding.UTF8.GetBytes(user.SecurityStamp ?? string.Empty);
        var providedBytes = Encoding.UTF8.GetBytes(claims.SecurityStamp ?? string.Empty);
        if (expectedBytes.Length == 0 ||
            expectedBytes.Length != providedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
        {
            _logger.LogWarning("Refresh token rejected due to rotated SecurityStamp for user {UserId}", user.Id);
            return Result<AuthTokenResult>.Failure(new Error("Auth.RefreshRevoked", "La sesión fue revocada por un cambio de seguridad. Inicie sesión nuevamente."));
        }

        string? customerPath = null;
        if (_customerRepository is not null && user.CustomerId.HasValue && user.CustomerId.Value != Guid.Empty)
        {
            var customer = await _customerRepository.GetByIdAsync(user.CustomerId.Value, cancellationToken);
            customerPath = customer?.Path;
        }

        var permissions = _permissionService != null
            ? await _permissionService.GetEffectivePermissionsAsync(user.TenantId, user.Id, user.RoleName, user.RoleId, cancellationToken)
            : StandardPermissions.GetDefaultPermissionsForRole(user.RoleName);

        var rotated = _jwtTokenGenerator.GenerateToken(user, user.TenantId, user.CustomerId, customerPath, permissions);
        return Result<AuthTokenResult>.Success(rotated);
    }
}

public sealed record RegisterAccountCommand(
    string FullName,
    string Email,
    string Password,
    string? OrganizationName = null,
    string? Username = null,
    string? IpAddress = null,
    string? UserAgent = null) : IRequest<Result<AuthTokenResult>>;

public sealed class RegisterAccountCommandHandler : IRequestHandler<RegisterAccountCommand, Result<AuthTokenResult>>
{
    private readonly ITenantRepository _tenantRepository;
    private readonly ICustomerRepository _customerRepository;
    private readonly IUserRepository _userRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<RegisterAccountCommandHandler> _logger;
    private readonly ITenantContext? _tenantContext;
    private readonly IPermissionService? _permissionService;

    public RegisterAccountCommandHandler(
        ITenantRepository tenantRepository,
        ICustomerRepository customerRepository,
        IUserRepository userRepository,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator jwtTokenGenerator,
        IAuditLogRepository auditLogRepository,
        IUnitOfWork unitOfWork,
        ILogger<RegisterAccountCommandHandler> logger,
        ITenantContext? tenantContext = null,
        IPermissionService? permissionService = null)
    {
        _tenantRepository = tenantRepository;
        _customerRepository = customerRepository;
        _userRepository = userRepository;
        _passwordHasher = passwordHasher;
        _jwtTokenGenerator = jwtTokenGenerator;
        _auditLogRepository = auditLogRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _tenantContext = tenantContext;
        _permissionService = permissionService;
    }

    public async Task<Result<AuthTokenResult>> Handle(RegisterAccountCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FullName))
        {
            return Result<AuthTokenResult>.Failure(new Error("Auth.InvalidName", "El nombre completo es obligatorio."));
        }

        var normalizedEmail = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedEmail) || !Regex.IsMatch(normalizedEmail, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
        {
            return Result<AuthTokenResult>.Failure(new Error("Auth.InvalidEmail", "Ingrese un correo electrónico válido."));
        }

        try
        {
            _passwordHasher.ValidateComplexity(request.Password);
        }
        catch (InvalidOperationException ex)
        {
            return Result<AuthTokenResult>.Failure(new Error("Password.PolicyViolation", ex.Message));
        }

        if (_tenantContext != null)
        {
            _tenantContext.IsSystemOperation = true;
        }

        var existingByEmail = await _userRepository.FindByUsernameOrEmailGlobalAsync(normalizedEmail, cancellationToken);
        if (existingByEmail is not null)
        {
            return Result<AuthTokenResult>.Failure(new Error("Auth.EmailAlreadyRegistered", "Ya existe una cuenta asociada a este correo electrónico."));
        }

        var desiredUsername = !string.IsNullOrWhiteSpace(request.Username)
            ? request.Username.Trim()
            : normalizedEmail;

        if (!string.Equals(desiredUsername, normalizedEmail, StringComparison.OrdinalIgnoreCase))
        {
            var existingByUsername = await _userRepository.FindByUsernameOrEmailGlobalAsync(desiredUsername, cancellationToken);
            if (existingByUsername is not null)
            {
                return Result<AuthTokenResult>.Failure(new Error("Auth.UsernameTaken", "El nombre de usuario elegido ya está en uso."));
            }
        }

        var orgName = !string.IsNullOrWhiteSpace(request.OrganizationName)
            ? request.OrganizationName.Trim()
            : $"Hogar de {request.FullName.Trim()}";

        var slug = Regex.Replace(orgName.ToUpperInvariant(), @"[^A-Z0-9]+", "-").Trim('-');
        if (slug.Length > 10) slug = slug[..10].TrimEnd('-');
        if (string.IsNullOrEmpty(slug)) slug = "ESPACIO";

        string tenantCode = $"{slug}-{RandomNumberGenerator.GetHexString(6)}";
        if (await _tenantRepository.GetByCodeAsync(tenantCode, cancellationToken) is not null)
        {
            tenantCode = $"{slug}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        }

        var tenantResult = Tenant.Create(orgName, tenantCode);
        if (tenantResult.IsFailure)
        {
            return Result<AuthTokenResult>.Failure(tenantResult.Error);
        }

        var tenant = tenantResult.Value;
        var rootCustomerResult = Customer.Create(tenant.Id, orgName, parent: null, code: "ROOT");
        if (rootCustomerResult.IsFailure)
        {
            return Result<AuthTokenResult>.Failure(rootCustomerResult.Error);
        }

        var rootCustomer = rootCustomerResult.Value;
        var passwordHash = _passwordHasher.Hash(request.Password);
        var userResult = User.Create(
            tenant.Id,
            desiredUsername,
            normalizedEmail,
            passwordHash,
            request.FullName.Trim(),
            roleName: "Admin",
            mustChangePassword: false);

        if (userResult.IsFailure)
        {
            return Result<AuthTokenResult>.Failure(userResult.Error);
        }

        var user = userResult.Value;
        user.AssignToCustomer(rootCustomer.Id);

        await _tenantRepository.AddAsync(tenant, cancellationToken);
        await _customerRepository.AddAsync(rootCustomer, cancellationToken);
        await _userRepository.AddAsync(user, cancellationToken);
        await _auditLogRepository.AddAsync(new AuditLog(
            Guid.NewGuid(),
            tenant.Id,
            user.Id,
            user.Username,
            "AccountRegistered",
            $"Registro de cuenta y espacio '{orgName}'.",
            request.IpAddress,
            request.UserAgent), cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var permissions = _permissionService != null
            ? await _permissionService.GetEffectivePermissionsAsync(tenant.Id, user.Id, user.RoleName, user.RoleId, cancellationToken)
            : StandardPermissions.GetDefaultPermissionsForRole(user.RoleName);

        var tokenResult = _jwtTokenGenerator.GenerateToken(user, tenant.Id, rootCustomer.Id, rootCustomer.Path, permissions);
        _logger.LogInformation("Registered new account {UserId} under isolated Tenant {TenantId}", user.Id, tenant.Id);

        return Result<AuthTokenResult>.Success(tokenResult);
    }
}

public sealed record LogoutCommand(
    Guid UserId,
    Guid TenantId,
    string? IpAddress = null,
    string? UserAgent = null) : IRequest<Result>;

public sealed class LogoutCommandHandler : IRequestHandler<LogoutCommand, Result>
{
    private readonly IUserRepository _userRepository;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext? _tenantContext;

    public LogoutCommandHandler(
        IUserRepository userRepository,
        IAuditLogRepository auditLogRepository,
        IUnitOfWork unitOfWork,
        ITenantContext? tenantContext = null)
    {
        _userRepository = userRepository;
        _auditLogRepository = auditLogRepository;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
    }

    public async Task<Result> Handle(LogoutCommand request, CancellationToken cancellationToken)
    {
        if (request.UserId == Guid.Empty || request.TenantId == Guid.Empty)
        {
            return Result.Success();
        }

        if (_tenantContext != null)
        {
            _tenantContext.IsSystemOperation = true;
        }

        var user = await _userRepository.GetByIdSystemAsync(request.TenantId, request.UserId, cancellationToken)
            ?? await _userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is not null && user.TenantId == request.TenantId)
        {
            user.RotateSecurityStamp();
            _userRepository.Update(user);
            await _auditLogRepository.AddAsync(new AuditLog(
                Guid.NewGuid(),
                user.TenantId,
                user.Id,
                user.Username,
                "Logout",
                "Cierre de sesión y revocación de tokens de renovación.",
                request.IpAddress,
                request.UserAgent), cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }
}

public sealed record ChangePasswordCommand(
    Guid UserId,
    Guid TenantId,
    string CurrentPassword,
    string NewPassword,
    string? IpAddress = null,
    string? UserAgent = null) : IRequest<Result>;

public sealed class ChangePasswordCommandHandler : IRequestHandler<ChangePasswordCommand, Result>
{
    private readonly IUserRepository _userRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext? _tenantContext;

    public ChangePasswordCommandHandler(
        IUserRepository userRepository,
        IPasswordHasher passwordHasher,
        IAuditLogRepository auditLogRepository,
        IUnitOfWork unitOfWork,
        ITenantContext? tenantContext = null)
    {
        _userRepository = userRepository;
        _passwordHasher = passwordHasher;
        _auditLogRepository = auditLogRepository;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
    }

    public async Task<Result> Handle(ChangePasswordCommand request, CancellationToken cancellationToken)
    {
        if (request.UserId == Guid.Empty || request.TenantId == Guid.Empty)
        {
            return Result.Failure(new Error("Auth.Unauthorized", "Sesión inválida."));
        }

        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return Result.Failure(new Error("Auth.InvalidInput", "Debe proporcionar la contraseña actual y la nueva contraseña."));
        }

        using var _ = IoT.Application.Common.Interfaces.TenantContextExtensions.BeginSystemOperationIfPresent(_tenantContext);
        var user = await _userRepository.GetByIdSystemAsync(request.TenantId, request.UserId, cancellationToken)
            ?? await _userRepository.GetByIdAsync(request.UserId, cancellationToken);

        if (user is null || !user.IsActive || user.TenantId != request.TenantId)
        {
            return Result.Failure(new Error("Auth.InvalidCredentials", "Usuario no encontrado o inactivo."));
        }

        if (user.IsLockedOut)
        {
            return Result.Failure(new Error("Auth.LockedOut", "La cuenta está bloqueada temporalmente por intentos fallidos."));
        }

        if (!_passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
        {
            user.RecordFailedLogin();
            _userRepository.Update(user);
            await _auditLogRepository.AddAsync(new AuditLog(
                Guid.NewGuid(),
                user.TenantId,
                user.Id,
                user.Username,
                "ChangePasswordFailed",
                "Contraseña actual incorrecta durante cambio voluntario.",
                request.IpAddress,
                request.UserAgent), cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Failure(new Error("Auth.InvalidCredentials", "La contraseña actual no es válida."));
        }

        try
        {
            _passwordHasher.ValidateComplexity(request.NewPassword);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(new Error("Password.PolicyViolation", ex.Message));
        }

        string newHash = _passwordHasher.Hash(request.NewPassword);
        user.UpdatePassword(newHash);
        user.RotateSecurityStamp();
        _userRepository.Update(user);

        await _auditLogRepository.AddAsync(new AuditLog(
            Guid.NewGuid(),
            user.TenantId,
            user.Id,
            user.Username,
            "ChangePassword",
            "Contraseña actualizada exitosamente por el usuario.",
            request.IpAddress,
            request.UserAgent), cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
