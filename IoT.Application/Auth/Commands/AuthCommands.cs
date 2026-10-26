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

    public LoginCommandHandler(
        IUserRepository userRepository,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator jwtTokenGenerator,
        IAuditLogRepository auditLogRepository,
        IUnitOfWork unitOfWork,
        ILogger<LoginCommandHandler> logger,
        ITenantContext? tenantContext = null)
    {
        _userRepository = userRepository;
        _passwordHasher = passwordHasher;
        _jwtTokenGenerator = jwtTokenGenerator;
        _auditLogRepository = auditLogRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _tenantContext = tenantContext;
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

        var tokenResult = _jwtTokenGenerator.GenerateToken(user, user.TenantId);
        _logger.LogInformation("User {Username} logged in successfully for Tenant {TenantId}", user.Username, user.TenantId);

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

    public ChangeTemporaryPasswordCommandHandler(
        IUserRepository userRepository,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator jwtTokenGenerator,
        IAuditLogRepository auditLogRepository,
        IUnitOfWork unitOfWork,
        ITenantContext? tenantContext = null)
    {
        _userRepository = userRepository;
        _passwordHasher = passwordHasher;
        _jwtTokenGenerator = jwtTokenGenerator;
        _auditLogRepository = auditLogRepository;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
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

        var token = _jwtTokenGenerator.GenerateToken(user, user.TenantId);
        return Result<AuthTokenResult>.Success(token);
    }
}
