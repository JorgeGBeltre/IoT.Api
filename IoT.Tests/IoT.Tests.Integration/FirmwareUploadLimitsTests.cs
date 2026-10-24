using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Services;
using IoT.Protocols.Ota;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// Cotas de tamaño de la subida de firmware y limpieza del almacén.
///
/// Antes NO había ningún límite en la aplicación: el único tope era el `client_max_body_size`
/// de 16m del nginx del FRONTEND, que una llamada directa al puerto de la API —que el compose
/// publica al host— esquiva por completo. Y el fichero se copiaba DOS veces completo en
/// memoria (`CopyToAsync(memoryStream)` + `ToArray()`, la segunda en el Large Object Heap).
/// </summary>
public class FirmwareUploadLimitsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    // ---------------------------------------------------------------------
    // Resolución del tope
    // ---------------------------------------------------------------------

    [Fact]
    public void WithoutConfigurationTheDefaultCapApplies()
    {
        FirmwareStorage.ResolveMaxUploadBytes(null).Should().Be(FirmwareStorage.DefaultMaxUploadBytes);
        FirmwareStorage.ResolveMaxUploadBytes(Config()).Should().Be(FirmwareStorage.DefaultMaxUploadBytes);
    }

    [Fact]
    public void TheCapCanBeLoweredByConfiguration()
    {
        var configuration = Config(("Firmware:MaxUploadBytes", "2097152")); // 2 MiB

        FirmwareStorage.ResolveMaxUploadBytes(configuration).Should().Be(2 * 1024 * 1024);
    }

    [Fact]
    public void TheCapCannotBeRaisedAboveTheHardLimit()
    {
        // Subirlo por configuración no sirve: el atributo [RequestSizeLimit] del endpoint
        // rechaza antes, así que permitirlo aquí sólo daría una falsa sensación de margen.
        var configuration = Config(("Firmware:MaxUploadBytes", "1073741824")); // 1 GiB

        FirmwareStorage.ResolveMaxUploadBytes(configuration)
            .Should().Be(FirmwareStorage.MaxUploadBytesHardLimit);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("no-es-un-numero")]
    [InlineData("")]
    public void ABrokenOrZeroValueFallsBackInsteadOfRemovingTheLimit(string configured)
    {
        // Un 0 interpretado literalmente dejaría la subida sin tope.
        var configuration = Config(("Firmware:MaxUploadBytes", configured));

        FirmwareStorage.ResolveMaxUploadBytes(configuration)
            .Should().Be(FirmwareStorage.DefaultMaxUploadBytes);
    }

    // ---------------------------------------------------------------------
    // Validación de la imagen sin tenerla entera en memoria
    // ---------------------------------------------------------------------

    [Fact]
    public void TheImageIsValidatedFromItsHeaderAndTotalSize()
    {
        var header = new byte[OtaChunker.MinimumFirstChunkBytes];
        header[0] = OtaChunker.EspImageMagic;

        OtaChunker.ValidateImage(header, 1048576).Should().BeNull();
        OtaChunker.ValidateImage(header, 500).Should().Contain("Firmware too small");

        header[0] = 0x00;
        OtaChunker.ValidateImage(header, 1048576).Should().Contain("Invalid firmware image header");

        OtaChunker.ValidateImage(ReadOnlySpan<byte>.Empty, 1048576)
            .Should().Contain("cabecera", "sin cabecera no se puede validar la imagen");
    }

    // ---------------------------------------------------------------------
    // Temporal de entrada
    // ---------------------------------------------------------------------

    [Fact]
    public void TheIncomingTempFileLivesInsideTheTenantStore()
    {
        var tenant = Guid.NewGuid();

        string first = FirmwareStorage.CreateIncomingTempPath(tenant);
        string second = FirmwareStorage.CreateIncomingTempPath(tenant);

        // Mismo volumen que el destino => el movimiento al nombre final es atómico.
        first.Should().StartWith(FirmwareStorage.TenantRoot(tenant));
        first.Should().NotBe(second, "dos subidas simultáneas no pueden pisarse");
        Directory.Exists(Path.GetDirectoryName(first)).Should().BeTrue();

        Directory.Delete(Path.GetDirectoryName(first)!, recursive: true);
    }

    // ---------------------------------------------------------------------
    // Rechazo real de una subida demasiado grande
    // ---------------------------------------------------------------------

    private static FirmwareOtaService BuildService(IConfiguration configuration)
    {
        return new FirmwareOtaService(
            new Mock<IFirmwareVersionRecordRepository>().Object,
            new Mock<IDeviceRepository>().Object,
            new Mock<IUnitOfWork>().Object,
            new Mock<IMqttPublisher>().Object,
            new Mock<IFleetNotificationService>().Object,
            new Mock<IServiceScopeFactory>().Object,
            new Mock<ILogger<FirmwareOtaService>>().Object,
            auditLogRepository: null,
            configuration: configuration);
    }

    [Fact]
    public async Task AnOversizedUploadIsRejectedAndLeavesNothingBehind()
    {
        var tenant = Guid.NewGuid();
        var configuration = Config(("Firmware:MaxUploadBytes", "4096")); // 4 KiB
        var service = BuildService(configuration);

        // 64 KiB: supera el tope. El corte ocurre MIENTRAS se lee, no después de
        // materializarlo, así que un fichero enorme no llega a ocupar memoria ni disco.
        var oversized = new byte[65536];
        oversized[0] = OtaChunker.EspImageMagic;

        var result = await service.UploadFirmwareAsync(
            tenantId: tenant,
            fileStream: new MemoryStream(oversized),
            fileName: "firmware.bin",
            version: "1.0.0");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Firmware.TooLarge");

        // Ni el temporal ni el destino deben quedarse ocupando el almacén.
        string tenantRoot = FirmwareStorage.TenantRoot(tenant);
        if (Directory.Exists(tenantRoot))
        {
            Directory.GetFiles(tenantRoot, "*", SearchOption.AllDirectories)
                .Should().BeEmpty("una subida rechazada no debe dejar restos en disco");
            Directory.Delete(tenantRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AnEmptyUploadIsRejectedAndLeavesNothingBehind()
    {
        var tenant = Guid.NewGuid();
        var service = BuildService(Config());

        var result = await service.UploadFirmwareAsync(
            tenantId: tenant,
            fileStream: new MemoryStream(Array.Empty<byte>()),
            fileName: "firmware.bin",
            version: "1.0.0");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Firmware.EmptyFile");

        string tenantRoot = FirmwareStorage.TenantRoot(tenant);
        if (Directory.Exists(tenantRoot))
        {
            Directory.GetFiles(tenantRoot, "*", SearchOption.AllDirectories).Should().BeEmpty();
            Directory.Delete(tenantRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AnImageBelowTheDeviceMinimumIsRejectedAndLeavesNothingBehind()
    {
        var tenant = Guid.NewGuid();
        var service = BuildService(Config());

        // 512 bytes: el dispositivo publicaría "Firmware too small".
        var tooSmall = new byte[512];
        tooSmall[0] = OtaChunker.EspImageMagic;

        var result = await service.UploadFirmwareAsync(
            tenantId: tenant,
            fileStream: new MemoryStream(tooSmall),
            fileName: "firmware.bin",
            version: "1.0.0");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Firmware.InvalidImage");

        string tenantRoot = FirmwareStorage.TenantRoot(tenant);
        if (Directory.Exists(tenantRoot))
        {
            Directory.GetFiles(tenantRoot, "*", SearchOption.AllDirectories).Should().BeEmpty();
            Directory.Delete(tenantRoot, recursive: true);
        }
    }
}
