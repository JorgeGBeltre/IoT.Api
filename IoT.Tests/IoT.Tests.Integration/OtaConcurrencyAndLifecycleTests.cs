using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Entities;
using IoT.Domain.Repositories;
using IoT.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// Concurrencia del despliegue OTA y ciclo de vida de una campaña cuando algo interrumpe el
/// proceso. Antes: un rollout de grupo lanzaba una bomba por dispositivo sin ninguna cota,
/// cada una con la imagen COMPLETA en memoria, y un reinicio del servicio dejaba al equipo
/// marcado como "actualizando" para siempre.
/// </summary>
public class OtaConcurrencyAndLifecycleTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    // -----------------------------------------------------------------
    // Limitador de transmisiones simultáneas
    // -----------------------------------------------------------------

    [Fact]
    public void TheThrottleHasASaneDefault()
    {
        using var throttle = new OtaTransmissionThrottle();

        throttle.MaxConcurrent.Should().Be(OtaTransmissionThrottle.DefaultMaxConcurrent);
        throttle.Available.Should().Be(throttle.MaxConcurrent);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("12", 12)]
    [InlineData("500", 50)]   // tope de cordura: más de 50 bombas no mejora nada
    public void TheThrottleIsConfigurableAndCapped(string configured, int expected)
    {
        using var throttle = new OtaTransmissionThrottle(Config(("Firmware:MaxConcurrentRollouts", configured)));

        throttle.MaxConcurrent.Should().Be(expected);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("no-es-un-numero")]
    public void ABrokenThrottleValueFallsBackInsteadOfRemovingTheLimit(string configured)
    {
        // Un 0 interpretado literalmente crearía un semáforo que no deja pasar a nadie.
        using var throttle = new OtaTransmissionThrottle(Config(("Firmware:MaxConcurrentRollouts", configured)));

        throttle.MaxConcurrent.Should().Be(OtaTransmissionThrottle.DefaultMaxConcurrent);
    }

    [Fact]
    public async Task TheThrottleBlocksBeyondItsCapacityAndFreesOnDispose()
    {
        using var throttle = new OtaTransmissionThrottle(Config(("Firmware:MaxConcurrentRollouts", "2")));

        var first = await throttle.AcquireAsync(CancellationToken.None);
        var second = await throttle.AcquireAsync(CancellationToken.None);
        throttle.Available.Should().Be(0);

        // El tercero NO debe pasar mientras los dos primeros tengan su hueco.
        var third = throttle.AcquireAsync(CancellationToken.None);
        third.IsCompleted.Should().BeFalse("la tercera transmisión tiene que esperar");

        first.Dispose();
        var acquired = await third;   // ahora sí
        acquired.Should().NotBeNull();

        second.Dispose();
        acquired.Dispose();
        throttle.Available.Should().Be(2, "todos los huecos vuelven al liberarse");
    }

    [Fact]
    public async Task WaitingForASlotIsInterruptedByShutdown()
    {
        using var throttle = new OtaTransmissionThrottle(Config(("Firmware:MaxConcurrentRollouts", "1")));
        using var occupied = await throttle.AcquireAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var waiting = throttle.AcquireAsync(cts.Token);

        cts.Cancel();

        // Es lo que permite que un apagado ordenado no deje tareas colgadas esperando hueco.
        await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<OperationCanceledException>();
        throttle.Available.Should().Be(0, "cancelar la espera no consume el hueco de nadie");
    }

    [Fact]
    public async Task ReleasingASlotTwiceDoesNotInflateTheCapacity()
    {
        using var throttle = new OtaTransmissionThrottle(Config(("Firmware:MaxConcurrentRollouts", "1")));
        var slot = await throttle.AcquireAsync(CancellationToken.None);

        slot.Dispose();
        slot.Dispose();   // doble liberación: no debe crear huecos de la nada

        throttle.Available.Should().Be(1);
    }

    // -----------------------------------------------------------------
    // Reconciliación de arranque
    // -----------------------------------------------------------------

    private static (OtaStartupReconciliationService Service, List<Device> Devices, Mock<IUnitOfWork> Uow) BuildReconciliation(
        params Device[] devices)
    {
        var list = devices.ToList();

        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetDevicesWithOtaInProgressAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(list.Where(d => d.OtaInProgress).ToList());

        var uow = new Mock<IUnitOfWork>();

        var services = new ServiceCollection();
        services.AddSingleton(deviceRepo.Object);
        services.AddSingleton(uow.Object);
        var provider = services.BuildServiceProvider();

        var service = new OtaStartupReconciliationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new Mock<ILogger<OtaStartupReconciliationService>>().Object);

        return (service, list, uow);
    }

    private static Device DeviceInOta(string chipId, string version)
    {
        var device = Device.Create(Guid.NewGuid(), chipId, "11:22:33:44:55:66", chipId, "ESP32", "Espressif", "1.0.0").Value;
        device.StartOta(version, 10);
        return device;
    }

    [Fact]
    public async Task OnStartupAnInterruptedCampaignIsClosedSoItCanBeRelaunched()
    {
        // Sin esto el equipo quedaba con OtaInProgress=true para siempre: la UI lo mostraba
        // eternamente "actualizando" y, peor, el guard de StartDeviceOtaAsync rechazaba
        // lanzarle una campaña nueva, dejándolo inservible para OTA.
        var device = DeviceInOta("CHIP_INTERRUMPIDO", "2.0.0");
        var (service, _, uow) = BuildReconciliation(device);

        await service.StartAsync(CancellationToken.None);

        device.OtaInProgress.Should().BeFalse();
        device.OtaCompleted.Should().BeFalse();
        device.OtaErrorMessage.Should().Contain("reinició");
        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnStartupWithNothingInterruptedNothingIsWritten()
    {
        var (service, _, uow) = BuildReconciliation();

        await service.StartAsync(CancellationToken.None);

        uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AFailureWhileReconcilingDoesNotPreventTheServiceFromStarting()
    {
        // Sin reconciliación la plataforma funciona; impedir el arranque por esto sería peor.
        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetDevicesWithOtaInProgressAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("base de datos no disponible"));

        var services = new ServiceCollection();
        services.AddSingleton(deviceRepo.Object);
        services.AddSingleton(new Mock<IUnitOfWork>().Object);
        var provider = services.BuildServiceProvider();

        var service = new OtaStartupReconciliationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new Mock<ILogger<OtaStartupReconciliationService>>().Object);

        await FluentActions.Awaiting(() => service.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync();
    }
}
