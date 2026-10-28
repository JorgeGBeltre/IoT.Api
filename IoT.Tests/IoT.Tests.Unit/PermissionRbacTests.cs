using FluentAssertions;
using IoT.Domain.Common;
using IoT.Domain.Entities;
using Xunit;

namespace IoT.Tests.Unit;

/// <summary>
/// Pruebas unitarias para el modelo de RBAC fino (ADR-013, Decisión D3):
/// Catálogo de permisos estándar, mapeos por rol por defecto y comportamiento de las entidades de seguridad.
/// </summary>
public sealed class PermissionRbacTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void StandardPermissions_Catalog_Has14StandardPermissions()
    {
        StandardPermissions.All.Should().HaveCount(14);
        StandardPermissions.All.Select(p => p.Code).Should().Contain(new[]
        {
            StandardPermissions.DevicesRead,
            StandardPermissions.DevicesCreate,
            StandardPermissions.DevicesUpdate,
            StandardPermissions.DevicesDelete,
            StandardPermissions.DevicesCommand,
            StandardPermissions.DevicesManage,
            StandardPermissions.TelemetryRead,
            StandardPermissions.TelemetryExport,
            StandardPermissions.GpsRead,
            StandardPermissions.ReportsRead,
            StandardPermissions.FirmwareManage,
            StandardPermissions.CustomersManage,
            StandardPermissions.UsersManage,
            StandardPermissions.SettingsManage
        });
    }

    [Fact]
    public void StandardPermissions_GetDefaultPermissionsForRole_AdminHasAllPermissions()
    {
        var adminPerms = StandardPermissions.GetDefaultPermissionsForRole("Admin");
        adminPerms.Should().HaveCount(14);
        adminPerms.Should().Contain(StandardPermissions.FirmwareManage);
        adminPerms.Should().Contain(StandardPermissions.CustomersManage);
        adminPerms.Should().Contain(StandardPermissions.SettingsManage);
    }

    [Fact]
    public void StandardPermissions_GetDefaultPermissionsForRole_OperatorHasOperationalPermissions()
    {
        var opPerms = StandardPermissions.GetDefaultPermissionsForRole("Operator");
        opPerms.Should().Contain(StandardPermissions.DevicesRead);
        opPerms.Should().Contain(StandardPermissions.DevicesCommand);
        opPerms.Should().Contain(StandardPermissions.TelemetryRead);
        opPerms.Should().Contain(StandardPermissions.TelemetryExport);
        opPerms.Should().Contain(StandardPermissions.GpsRead);
        opPerms.Should().Contain(StandardPermissions.ReportsRead);
        opPerms.Should().NotContain(StandardPermissions.FirmwareManage);
        opPerms.Should().NotContain(StandardPermissions.CustomersManage);
        opPerms.Should().NotContain(StandardPermissions.UsersManage);
        opPerms.Should().NotContain(StandardPermissions.SettingsManage);
    }

    [Fact]
    public void StandardPermissions_GetDefaultPermissionsForRole_ViewerHasReadOnlyPermissions()
    {
        var viewerPerms = StandardPermissions.GetDefaultPermissionsForRole("Viewer");
        viewerPerms.Should().Contain(StandardPermissions.DevicesRead);
        viewerPerms.Should().Contain(StandardPermissions.TelemetryRead);
        viewerPerms.Should().Contain(StandardPermissions.GpsRead);
        viewerPerms.Should().Contain(StandardPermissions.ReportsRead);
        viewerPerms.Should().NotContain(StandardPermissions.DevicesCommand);
        viewerPerms.Should().NotContain(StandardPermissions.DevicesCreate);
        viewerPerms.Should().NotContain(StandardPermissions.FirmwareManage);
    }

    [Fact]
    public void StandardPermissions_GetDefaultPermissionsForRole_UnknownRole_FallsBackToDevicesRead()
    {
        var perms = StandardPermissions.GetDefaultPermissionsForRole("UnknownCustomRole");
        perms.Should().ContainSingle().Which.Should().Be(StandardPermissions.DevicesRead);
    }

    [Fact]
    public void User_AssignRole_UpdatesRoleIdRoleNameAndTimestamp()
    {
        var user = User.Create(TenantId, "test_user", "test@domain.com", "hash", "Test User", "Viewer").Value;
        user.RoleId.Should().BeNull();
        user.RoleName.Should().Be("Viewer");

        var role = Role.Create(TenantId, "Operator", "Operador de flota").Value;
        user.AssignRole(role);

        user.RoleId.Should().Be(role.Id);
        user.RoleName.Should().Be("Operator");
        user.Role.Should().Be(role);
        user.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public void Role_Create_WithValidArgs_Succeeds()
    {
        var result = Role.Create(TenantId, "CustomRole", "Descripcion", isSystemRole: false);
        result.IsSuccess.Should().BeTrue();
        result.Value.TenantId.Should().Be(TenantId);
        result.Value.Name.Should().Be("CustomRole");
        result.Value.Description.Should().Be("Descripcion");
        result.Value.IsSystemRole.Should().BeFalse();
    }

    [Fact]
    public void Role_Create_WithEmptyTenantOrName_Fails()
    {
        Role.Create(Guid.Empty, "Role").IsFailure.Should().BeTrue();
        Role.Create(TenantId, "").IsFailure.Should().BeTrue();
        Role.Create(TenantId, "   ").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Permission_Create_WithValidAndInvalidArgs_BehavesExpectedly()
    {
        var success = Permission.Create("custom.perm", "Custom Perm", "CustomModule", "Desc");
        success.IsSuccess.Should().BeTrue();
        success.Value.Code.Should().Be("custom.perm");
        success.Value.Name.Should().Be("Custom Perm");
        success.Value.Module.Should().Be("CustomModule");

        Permission.Create("", "Name", "Module").IsFailure.Should().BeTrue();
        Permission.Create("   ", "Name", "Module").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void RolePermission_And_UserPermission_ConstructProperly()
    {
        var roleId = Guid.NewGuid();
        var permId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var rp = new RolePermission(Guid.NewGuid(), TenantId, roleId, permId);
        rp.TenantId.Should().Be(TenantId);
        rp.RoleId.Should().Be(roleId);
        rp.PermissionId.Should().Be(permId);

        var up = new UserPermission(Guid.NewGuid(), TenantId, userId, permId, isGranted: false);
        up.TenantId.Should().Be(TenantId);
        up.UserId.Should().Be(userId);
        up.PermissionId.Should().Be(permId);
        up.IsGranted.Should().BeFalse();
    }
}
