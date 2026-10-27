using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Application.Users.Commands;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace IoT.Tests.IoT.Tests.Unit;

/// <summary>
/// Validación del alta de usuario en runtime (E8.6a): customer destino por defecto = nodo del
/// invitador, rechazo fuera del subárbol, unicidad de username, validación de rol, contraseña temporal
/// con MustChangePassword, auditoría y que el secreto NO se persiste ni se filtra a la auditoría.
/// </summary>
public sealed class CreateUserCommandTests
{
    private static readonly Guid T1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid T2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static CreateUserCommandHandler BuildHandler(
        Mock<IUserRepository> users,
        Mock<ICustomerRepository> customers,
        Mock<ITenantContext> tenant,
        Mock<IPasswordHasher> hasher,
        Mock<IAuditLogRepository> audit,
        Mock<IUnitOfWork> uow) =>
        new(users.Object, customers.Object, tenant.Object, hasher.Object, audit.Object, uow.Object,
            NullLogger<CreateUserCommandHandler>.Instance);

    [Fact]
    public async Task Handle_WhenTargetNull_UsesSessionNode_NotRoot_AndGeneratesTempPassword()
    {
        var node = Customer.Create(T1, "Nodo del invitador", parent: null, code: "NODE").Value;

        var users = new Mock<IUserRepository>();
        users.Setup(r => r.GetByUsernameAsync(T1, "nuevo", It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        var customers = new Mock<ICustomerRepository>();
        customers.Setup(r => r.GetByIdScopedAsync(node.Id, It.IsAny<CancellationToken>())).ReturnsAsync(node);
        var hasher = new Mock<IPasswordHasher>();
        string? hashedPlaintext = null;
        hasher.Setup(h => h.Hash(It.IsAny<string>()))
            .Callback<string>(p => hashedPlaintext = p)
            .Returns("BCRYPT-HASH");
        var audit = new Mock<IAuditLogRepository>();
        AuditLog? captured = null;
        audit.Setup(a => a.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .Callback<AuditLog, CancellationToken>((l, _) => captured = l);
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(T1);
        tenant.SetupGet(t => t.CustomerId).Returns(node.Id);     // sesión acotada a su nodo
        tenant.SetupGet(t => t.CustomerPath).Returns(node.Path);

        User? addedUser = null;
        users.Setup(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Callback<User, CancellationToken>((u, _) => addedUser = u);

        var handler = BuildHandler(users, customers, tenant, hasher, audit, uow);
        var result = await handler.Handle(
            new CreateUserCommand("nuevo", "nuevo@t1.com", "Nuevo Usuario", "Operator"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CustomerId.Should().Be(node.Id, "el customer destino por defecto es el nodo del invitador");
        result.Value.MustChangePassword.Should().BeTrue();
        result.Value.RoleName.Should().Be("Operator");
        result.Value.TemporaryPassword.Should().NotBeNullOrWhiteSpace();
        result.Value.TemporaryPassword.Should().NotBe("BCRYPT-HASH", "se devuelve el texto plano, no el hash");

        // El hash se calculó sobre la MISMA contraseña temporal devuelta (y se guarda el hash, no el plano).
        hashedPlaintext.Should().Be(result.Value.TemporaryPassword);
        addedUser!.PasswordHash.Should().Be("BCRYPT-HASH");
        addedUser.MustChangePassword.Should().BeTrue();
        addedUser.CustomerId.Should().Be(node.Id);

        customers.Verify(r => r.GetRootCustomerIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never,
            "con nodo de sesión no debe resolver el raíz");
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);

        // El secreto NUNCA aparece en la auditoría.
        captured.Should().NotBeNull();
        captured!.Action.Should().Be("UserCreated");
        captured.Details.Should().NotContain(result.Value.TemporaryPassword);
        captured.Details.Should().NotContain("BCRYPT-HASH");
    }

    [Fact]
    public async Task Handle_WhenSessionNotBounded_ResolvesRoot()
    {
        var root = Customer.Create(T1, "Raiz", parent: null, code: "ROOT").Value;

        var users = new Mock<IUserRepository>();
        users.Setup(r => r.GetByUsernameAsync(T1, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        var customers = new Mock<ICustomerRepository>();
        customers.Setup(r => r.GetRootCustomerIdAsync(T1, It.IsAny<CancellationToken>())).ReturnsAsync(root.Id);
        customers.Setup(r => r.GetByIdScopedAsync(root.Id, It.IsAny<CancellationToken>())).ReturnsAsync(root);
        var hasher = new Mock<IPasswordHasher>();
        hasher.Setup(h => h.Hash(It.IsAny<string>())).Returns("H");
        var audit = new Mock<IAuditLogRepository>();
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(T1);
        tenant.SetupGet(t => t.CustomerId).Returns((Guid?)null);  // raíz / token antiguo
        tenant.SetupGet(t => t.CustomerPath).Returns((string?)null);

        var handler = BuildHandler(users, customers, tenant, hasher, audit, uow);
        var result = await handler.Handle(
            new CreateUserCommand("admin2", "admin2@t1.com", "Admin Dos", "Admin"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CustomerId.Should().Be(root.Id);
        customers.Verify(r => r.GetRootCustomerIdAsync(T1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenTargetOutsideSubtree_FailsOutsideSubtree()
    {
        // Customer destino VÁLIDO (mismo tenant) pero la sesión está acotada a OTRO subárbol.
        var target = Customer.Create(T1, "Fuera", parent: null, code: "OUT").Value;

        var users = new Mock<IUserRepository>();
        var customers = new Mock<ICustomerRepository>();
        customers.Setup(r => r.GetByIdScopedAsync(target.Id, It.IsAny<CancellationToken>())).ReturnsAsync(target);
        var hasher = new Mock<IPasswordHasher>();
        var audit = new Mock<IAuditLogRepository>();
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(T1);
        tenant.SetupGet(t => t.CustomerPath).Returns("cffffffffffffffffffffffffffffffff"); // subárbol distinto

        var handler = BuildHandler(users, customers, tenant, hasher, audit, uow);
        var result = await handler.Handle(
            new CreateUserCommand("x", "x@t1.com", "X", "Viewer", target.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("User.OutsideSubtree");
        users.Verify(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenTargetInAnotherTenant_FailsTenantMismatch()
    {
        var foreign = Customer.Create(T2, "Ajeno", parent: null, code: "F").Value;

        var users = new Mock<IUserRepository>();
        var customers = new Mock<ICustomerRepository>();
        customers.Setup(r => r.GetByIdScopedAsync(foreign.Id, It.IsAny<CancellationToken>())).ReturnsAsync(foreign);
        var hasher = new Mock<IPasswordHasher>();
        var audit = new Mock<IAuditLogRepository>();
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(T1);

        var handler = BuildHandler(users, customers, tenant, hasher, audit, uow);
        var result = await handler.Handle(
            new CreateUserCommand("x", "x@t1.com", "X", "Viewer", foreign.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("User.CustomerTenantMismatch");
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenUsernameTaken_FailsUsernameTaken()
    {
        var node = Customer.Create(T1, "Nodo", parent: null, code: "N").Value;
        var existing = User.Create(T1, "dup", "dup@t1.com", "h", "Dup").Value;

        var users = new Mock<IUserRepository>();
        users.Setup(r => r.GetByUsernameAsync(T1, "dup", It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        var customers = new Mock<ICustomerRepository>();
        customers.Setup(r => r.GetByIdScopedAsync(node.Id, It.IsAny<CancellationToken>())).ReturnsAsync(node);
        var hasher = new Mock<IPasswordHasher>();
        var audit = new Mock<IAuditLogRepository>();
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(T1);
        tenant.SetupGet(t => t.CustomerId).Returns(node.Id);
        tenant.SetupGet(t => t.CustomerPath).Returns(node.Path);

        var handler = BuildHandler(users, customers, tenant, hasher, audit, uow);
        var result = await handler.Handle(
            new CreateUserCommand("dup", "otro@t1.com", "Otro", "Operator"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("User.UsernameTaken");
        users.Verify(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
        hasher.Verify(h => h.Hash(It.IsAny<string>()), Times.Never, "no se genera clave para un alta rechazada");
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("Superadmin")]
    [InlineData("Platform Super Admin")]
    [InlineData("root")]
    [InlineData("")]
    public async Task Handle_WhenRoleNotAssignable_FailsInvalidRole(string role)
    {
        var users = new Mock<IUserRepository>();
        var customers = new Mock<ICustomerRepository>();
        var hasher = new Mock<IPasswordHasher>();
        var audit = new Mock<IAuditLogRepository>();
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(T1);

        var handler = BuildHandler(users, customers, tenant, hasher, audit, uow);
        var result = await handler.Handle(
            new CreateUserCommand("x", "x@t1.com", "X", role),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("User.InvalidRole");
        customers.Verify(r => r.GetByIdScopedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("admin", "Admin")]
    [InlineData("OPERATOR", "Operator")]
    [InlineData("viewer", "Viewer")]
    public async Task Handle_NormalizesRoleCasing(string input, string canonical)
    {
        var node = Customer.Create(T1, "Nodo", parent: null, code: "N").Value;

        var users = new Mock<IUserRepository>();
        users.Setup(r => r.GetByUsernameAsync(T1, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        var customers = new Mock<ICustomerRepository>();
        customers.Setup(r => r.GetByIdScopedAsync(node.Id, It.IsAny<CancellationToken>())).ReturnsAsync(node);
        var hasher = new Mock<IPasswordHasher>();
        hasher.Setup(h => h.Hash(It.IsAny<string>())).Returns("H");
        var audit = new Mock<IAuditLogRepository>();
        var uow = new Mock<IUnitOfWork>();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(T1);
        tenant.SetupGet(t => t.CustomerId).Returns(node.Id);
        tenant.SetupGet(t => t.CustomerPath).Returns(node.Path);

        var handler = BuildHandler(users, customers, tenant, hasher, audit, uow);
        var result = await handler.Handle(
            new CreateUserCommand("u", "u@t1.com", "U", input),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RoleName.Should().Be(canonical);
    }
}
