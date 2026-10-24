using FluentAssertions;
using IoT.Infrastructure.Services;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// Contención del almacén de firmware. La versión la escribe quien sube el archivo y se usa
/// para construir una ruta en disco: sin validar, un operador autenticado podía crear
/// directorios y escribir un archivo con contenido elegido FUERA del almacén, pisar el
/// firmware de otro tenant, y —como la ruta guardada se lee luego para publicar los trozos—
/// hacer que un dispositivo recibiera bytes de un archivo arbitrario del servidor.
/// </summary>
public class FirmwareStoragePathTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenant = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string Sha = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    [Theory]
    // Travesía con separadores de ambos sistemas.
    [InlineData("../../../etc/passwd")]
    [InlineData(@"..\..\..\Windows\Temp\x")]
    [InlineData("1.0.0/../../../../x")]
    [InlineData("..")]
    [InlineData("../")]
    // Rutas con raíz: Path.Combine DESCARTA la base y escribe donde diga el atacante.
    [InlineData("/etc/cron.d/x")]
    [InlineData(@"C:\Windows\Temp\x")]
    [InlineData(@"\\servidor\compartido\x")]
    // Separadores sueltos y caracteres que no pintan nada en un nombre de versión.
    [InlineData("1.0/0")]
    [InlineData(@"1.0\0")]
    [InlineData("a:b")]
    [InlineData("a\0b")]
    // Vacíos y versiones que quedan vacías al quitar la 'v'.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    public void RejectsAVersionThatCouldEscapeTheStore(string version)
    {
        FirmwareStorage.TryNormalizeVersion(version, out _).Should().BeFalse($"'{version}' no debe aceptarse como versión");

        FirmwareStorage.TryBuildRelativeFilePath(Tenant, version, Sha, ".bin", out _, out _)
            .Should().BeFalse($"'{version}' no debe producir ninguna ruta de escritura");
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("v1.0.4", "1.0.4")]
    [InlineData("V2.1.0-rc.3", "2.1.0-rc.3")]
    [InlineData("1_0_0", "1_0_0")]
    [InlineData("build.12345", "build.12345")]
    public void AcceptsRealisticVersions(string version, string expected)
    {
        FirmwareStorage.TryNormalizeVersion(version, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Fact]
    public void KeepsTheWrittenFileInsideTheTenantStore()
    {
        FirmwareStorage.TryBuildRelativeFilePath(Tenant, "1.0.4", Sha, ".bin", out var relative, out var absolute)
            .Should().BeTrue();

        absolute.Should().StartWith(FirmwareStorage.TenantRoot(Tenant));
        relative.Should().NotContain("..");
        Path.IsPathRooted(relative).Should().BeFalse();
    }

    [Fact]
    public void NamesTheFileFromTheHash_SoTheClientStringIsNeverInTheFileName()
    {
        FirmwareStorage.TryBuildRelativeFilePath(Tenant, "1.0.4", Sha, ".bin", out _, out var absolute)
            .Should().BeTrue();

        string fileName = Path.GetFileName(absolute);
        fileName.Should().Be($"firmware_{Sha[..16]}.bin");
        fileName.Should().NotContain("1.0.4");
    }

    [Fact]
    public void AStoredPathFromAnotherTenantIsRefusedOnRead()
    {
        // Lo que impide que un registro manipulado (o escrito antes de esta validación)
        // haga que un tenant lea y publique el firmware de otro.
        FirmwareStorage.TryBuildRelativeFilePath(OtherTenant, "1.0.4", Sha, ".bin", out var otherRelative, out _)
            .Should().BeTrue();

        FirmwareStorage.TryResolveWithinTenant(OtherTenant, otherRelative, out _).Should().BeTrue();
        FirmwareStorage.TryResolveWithinTenant(Tenant, otherRelative, out _)
            .Should().BeFalse("la ruta pertenece al almacén de otro tenant");
    }

    [Theory]
    [InlineData("storage/firmware/11111111-1111-1111-1111-111111111111/../../../../appsettings.json")]
    [InlineData("storage/firmware/22222222-2222-2222-2222-222222222222/1.0.0/firmware_x.bin")]
    [InlineData("../appsettings.json")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("/etc/passwd")]
    public void RefusesToResolveAStoredPathOutsideTheTenantStore(string storedPath)
    {
        FirmwareStorage.TryResolveWithinTenant(Tenant, storedPath, out var resolved).Should().BeFalse();
        resolved.Should().BeEmpty();
    }

    [Fact]
    public void TheTenantRootIsNotConfusedWithASiblingThatSharesItsPrefix()
    {
        // Un almacén cuyo nombre empieza igual que otro no debe colar: se compara con el
        // separador incluido, no por prefijo de cadena a secas.
        string root = FirmwareStorage.TenantRoot(Tenant);
        root.Should().EndWith(Tenant.ToString());

        string sibling = Path.Combine(
            FirmwareStorage.RootFolder, FirmwareStorage.FirmwareFolder,
            Tenant + "-evil", "1.0.0", "firmware_x.bin");

        FirmwareStorage.TryResolveWithinTenant(Tenant, sibling, out _).Should().BeFalse();
    }
}
