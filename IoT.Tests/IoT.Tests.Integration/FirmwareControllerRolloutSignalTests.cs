using System.Text;
using FluentAssertions;
using IoT.Api.Controllers;
using IoT.Application.Common.Interfaces;
using IoT.Application.Firmware;
using IoT.Domain.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// MEDIUM-16: al subir firmware con DeviceId/GroupId, el controlador arrancaba la OTA y
/// DESCARTABA el Result, respondiendo siempre 202 "aceptado" aunque el arranque fallara (equipo
/// inexistente, de otro tenant, apagado o ya actualizándose). El cuerpo ahora refleja si el
/// rollout automático prendió y, si no, por qué.
/// </summary>
public sealed class FirmwareControllerRolloutSignalTests
{
    private static IFormFile FakeFile()
    {
        var bytes = Encoding.UTF8.GetBytes("\xE9bin");
        var file = new Mock<IFormFile>();
        file.SetupGet(f => f.Length).Returns(bytes.Length);
        file.SetupGet(f => f.FileName).Returns("fw.bin");
        file.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(bytes));
        return file.Object;
    }

    private static FirmwareController BuildController(IFirmwareOtaService svc)
    {
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns(Guid.NewGuid());
        return new FirmwareController(svc, tenant.Object, new Mock<ILogger<FirmwareController>>().Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static FirmwareVersionRecordDto Dto() => new(
        Guid.NewGuid(), Guid.NewGuid(), "1.0.0", "storage/x/1.0.0/a.bin", null, null,
        4, "sha", 1, 2048, true, null, DateTime.UtcNow);

    private static (bool Started, string? Error) ReadRollout(IActionResult result)
    {
        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        var value = accepted.Value!;
        bool started = (bool)value.GetType().GetProperty("rolloutStarted")!.GetValue(value)!;
        var error = (string?)value.GetType().GetProperty("rolloutError")!.GetValue(value);
        return (started, error);
    }

    [Fact]
    public async Task UploadWithDeviceId_ReportsWhenTheAutoRolloutFailed()
    {
        var svc = new Mock<IFirmwareOtaService>();
        svc.Setup(s => s.UploadFirmwareAsync(
                It.IsAny<Guid>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<FirmwareVersionRecordDto>.Success(Dto()));
        // El arranque automático FALLA (p.ej. equipo apagado).
        svc.Setup(s => s.StartDeviceOtaAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(new Error("OTA.DeviceOffline", "El dispositivo no está conectado.")));

        var controller = BuildController(svc.Object);
        var legacyParams = new ReferenceGenericParameters { DeviceId = Guid.NewGuid() };

        var result = await controller.UploadFirmware(null, legacyParams, FakeFile(), "1.0.0", null, null);

        var (started, error) = ReadRollout(result);
        started.Should().BeFalse("el arranque automático no prendió y el operador debe saberlo");
        error.Should().Contain("no está conectado");
    }

    [Fact]
    public async Task UploadWithDeviceId_ReportsSuccessWhenTheAutoRolloutStarted()
    {
        var svc = new Mock<IFirmwareOtaService>();
        svc.Setup(s => s.UploadFirmwareAsync(
                It.IsAny<Guid>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<FirmwareVersionRecordDto>.Success(Dto()));
        svc.Setup(s => s.StartDeviceOtaAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var controller = BuildController(svc.Object);
        var legacyParams = new ReferenceGenericParameters { DeviceId = Guid.NewGuid() };

        var result = await controller.UploadFirmware(null, legacyParams, FakeFile(), "1.0.0", null, null);

        var (started, error) = ReadRollout(result);
        started.Should().BeTrue();
        error.Should().BeNull();
    }
}
