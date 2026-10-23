using FluentAssertions;
using IoT.Application.Auth.Commands;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

public class SecurityAndMultiTenantIntegrationTests
{
    private readonly Mock<IUserRepository> _userRepoMock = new();
    private readonly Mock<IOtpRepository> _otpRepoMock = new();
    private readonly Mock<IAuditLogRepository> _auditRepoMock = new();
    private readonly Mock<IPasswordHasher> _hasherMock = new();
    private readonly Mock<IJwtTokenGenerator> _jwtMock = new();
    private readonly Mock<IEmailService> _emailMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<ILogger<LoginCommandHandler>> _loginLoggerMock = new();
    private readonly Mock<ILogger<ForgotPasswordCommandHandler>> _forgotLoggerMock = new();

    [Fact]
    public async Task Login_WithValidCredentials_ShouldReturnJwtTokenAndRecordAudit()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var user = User.Create(tenantId, "operator_carlos", "carlos@caribe-log.com", "hashed_pwd_123", "Carlos Santana", "Operator").Value;

        _userRepoMock.Setup(r => r.GetByUsernameAsync(tenantId, "operator_carlos", It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        _hasherMock.Setup(h => h.Verify("SecretPassword123!", "hashed_pwd_123"))
            .Returns(true);

        _jwtMock.Setup(j => j.GenerateToken(user, tenantId))
            .Returns(new AuthTokenResult("sample_jwt_token_xyz", tenantId, user.Id, user.Username, user.Email, user.RoleName, false, DateTime.UtcNow.AddDays(7)));

        var handler = new LoginCommandHandler(
            _userRepoMock.Object,
            _hasherMock.Object,
            _jwtMock.Object,
            _auditRepoMock.Object,
            _unitOfWorkMock.Object,
            _loginLoggerMock.Object);

        var command = new LoginCommand("operator_carlos", "SecretPassword123!", tenantId, "190.166.44.10", "Mozilla/5.0");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Token.Should().Be("sample_jwt_token_xyz");
        result.Value.Username.Should().Be("operator_carlos");

        _auditRepoMock.Verify(a => a.AddAsync(It.Is<AuditLog>(l => l.Action == "LoginSuccess" && l.TenantId == tenantId), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Login_CrossTenantAttempt_ShouldBeRejected()
    {
        // Tenant A user attempts to login specifying Tenant B
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        _userRepoMock.Setup(r => r.GetByUsernameAsync(tenantB, "operator_carlos", It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null); // User does not exist in Tenant B

        var handler = new LoginCommandHandler(
            _userRepoMock.Object,
            _hasherMock.Object,
            _jwtMock.Object,
            _auditRepoMock.Object,
            _unitOfWorkMock.Object,
            _loginLoggerMock.Object);

        var command = new LoginCommand("operator_carlos", "SecretPassword123!", tenantB);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Auth.InvalidCredentials");
        _jwtMock.Verify(j => j.GenerateToken(It.IsAny<User>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task ForgotPassword_ShouldGenerateOtpAndSendEmailViaMailKit()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var user = User.Create(tenantId, "admin_jorge", "jorge@telemetry.io", "hashed_pwd", "Jorge Beltre", "Admin").Value;

        _userRepoMock.Setup(r => r.GetByEmailAsync(tenantId, "jorge@telemetry.io", It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        _emailMock.Setup(e => e.SendPasswordResetOtpEmailAsync("jorge@telemetry.io", "admin_jorge", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var handler = new ForgotPasswordCommandHandler(
            _userRepoMock.Object,
            _otpRepoMock.Object,
            _emailMock.Object,
            _unitOfWorkMock.Object,
            _forgotLoggerMock.Object);

        var command = new ForgotPasswordCommand("jorge@telemetry.io", tenantId);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _otpRepoMock.Verify(o => o.AddAsync(It.Is<OtpCode>(otp => otp.Email == "jorge@telemetry.io" && otp.TenantId == tenantId), It.IsAny<CancellationToken>()), Times.Once);
        _emailMock.Verify(e => e.SendPasswordResetOtpEmailAsync("jorge@telemetry.io", "admin_jorge", It.Is<string>(code => code.Length == 6), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ChangeTemporaryPassword_WithValidNewPassword_ShouldUpdateHashAndClearFlag()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var user = User.Create(tenantId, "new_driver", "driver@caribe-log.com", "temp_hash_01", "Driver One", "Operator", mustChangePassword: true).Value;
        user.MustChangePassword.Should().BeTrue();

        _userRepoMock.Setup(r => r.GetByUsernameAsync(tenantId, "new_driver", It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        _hasherMock.Setup(h => h.Verify("TempPass123!", "temp_hash_01"))
            .Returns(true);

        _hasherMock.Setup(h => h.Hash("NewStrongPass2026!"))
            .Returns("new_bcrypt_hash_xyz");

        _jwtMock.Setup(j => j.GenerateToken(user, tenantId))
            .Returns(new AuthTokenResult("new_jwt_token", tenantId, user.Id, user.Username, user.Email, user.RoleName, false, DateTime.UtcNow.AddDays(7)));

        var handler = new ChangeTemporaryPasswordCommandHandler(
            _userRepoMock.Object,
            _hasherMock.Object,
            _jwtMock.Object,
            _auditRepoMock.Object,
            _unitOfWorkMock.Object);

        var command = new ChangeTemporaryPasswordCommand("new_driver", "TempPass123!", "NewStrongPass2026!", tenantId);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        user.MustChangePassword.Should().BeFalse();
        user.PasswordHash.Should().Be("new_bcrypt_hash_xyz");
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
