using FluentAssertions;
using IoT.Infrastructure.Persistence;
using Xunit;

namespace IoT.Tests.IoT.Tests.Unit;

/// <summary>
/// Guarda de arranque de RLS (E8.4c): en PRODUCCIÓN exige aislamiento forzado (Enforce=true y rol de
/// runtime NOBYPASSRLS/NOSUPERUSER) o se falla (fail-fast). Fuera de producción nunca se falla aunque
/// el aislamiento no esté completo (dev/test pueden usar el rol admin). Se prueba la lógica pura.
/// </summary>
public sealed class RlsStartupGuardTests
{
    [Fact]
    public void Evaluate_Production_EnforceFalse_ShouldFail()
    {
        var eval = RlsStartupGuard.Evaluate(
            isProduction: true, enforce: false, runtimeRoleIsSuperuser: false, runtimeRoleBypassesRls: false);

        eval.ShouldFail.Should().BeTrue("en producción el modo estricto es obligatorio");
        eval.IsSatisfied.Should().BeFalse();
        eval.Message.Should().Contain("Platform:Rls:Enforce");
    }

    [Fact]
    public void Evaluate_Production_RoleBypassesRls_ShouldFail()
    {
        var eval = RlsStartupGuard.Evaluate(
            isProduction: true, enforce: true, runtimeRoleIsSuperuser: false, runtimeRoleBypassesRls: true);

        eval.ShouldFail.Should().BeTrue("un rol con BYPASSRLS omite por completo la RLS");
        eval.IsSatisfied.Should().BeFalse();
        eval.Message.Should().Contain("BYPASSRLS");
    }

    [Fact]
    public void Evaluate_Production_RoleIsSuperuser_ShouldFail()
    {
        var eval = RlsStartupGuard.Evaluate(
            isProduction: true, enforce: true, runtimeRoleIsSuperuser: true, runtimeRoleBypassesRls: false);

        eval.ShouldFail.Should().BeTrue("un rol superusuario omite por completo la RLS");
        eval.IsSatisfied.Should().BeFalse();
        eval.Message.Should().Contain("SUPERUSER");
    }

    [Fact]
    public void Evaluate_Development_EvenWithSuperuser_DoesNotFail()
    {
        var eval = RlsStartupGuard.Evaluate(
            isProduction: false, enforce: false, runtimeRoleIsSuperuser: true, runtimeRoleBypassesRls: true);

        eval.ShouldFail.Should().BeFalse("fuera de producción el arranque no se bloquea (dev/test con rol admin)");
        eval.IsSatisfied.Should().BeFalse("pero el aislamiento no está completo → se registra aviso");
    }

    [Fact]
    public void Evaluate_Production_EnforceTrue_RoleNoBypass_DoesNotFail()
    {
        var eval = RlsStartupGuard.Evaluate(
            isProduction: true, enforce: true, runtimeRoleIsSuperuser: false, runtimeRoleBypassesRls: false);

        eval.ShouldFail.Should().BeFalse("Enforce=true + rol NOBYPASSRLS/NOSUPERUSER = aislamiento garantizado");
        eval.IsSatisfied.Should().BeTrue();
    }
}
