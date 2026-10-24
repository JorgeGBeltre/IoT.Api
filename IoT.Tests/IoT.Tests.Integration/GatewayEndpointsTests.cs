using FluentAssertions;
using IoT.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// El endpoint que publica a dónde debe apuntar el operador cada dispositivo, verificado
/// contra el appsettings.json REAL del API (no una configuración de laboratorio), que es la
/// misma que usa el gateway para abrir los listeners.
/// </summary>
public class GatewayEndpointsTests
{
    private static string FindApiDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "IoT.Api");
            if (File.Exists(Path.Combine(candidate, "appsettings.json"))) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("No se encontró IoT.Api/appsettings.json");
    }

    private static GatewaysController BuildController()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(FindApiDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        return new GatewaysController(configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static GatewaysController.GatewayEndpointsDto Endpoints()
    {
        var result = BuildController().GetEndpoints().Should().BeOfType<OkObjectResult>().Subject;
        return result.Value.Should().BeOfType<GatewaysController.GatewayEndpointsDto>().Subject;
    }

    [Fact]
    public void PublishesEveryTcpListenerWithItsRealPort()
    {
        var endpoints = Endpoints();

        endpoints.TcpEnabled.Should().BeTrue();
        var byProtocol = endpoints.TcpListeners.ToDictionary(l => l.Protocol, l => l.Port, StringComparer.OrdinalIgnoreCase);

        // Puertos reales de los listeners. El formulario de alta del frontend los traía
        // cableados y TODOS estaban mal: GT06 7018, Queclink 5001 (que es el listener de
        // iStartek), Coban 5002 (que es el de Jointech), iStartek 5003, Jointech 5004 y
        // Mictrack 7018. Ningún equipo dado de alta así llegaba a conectar.
        byProtocol.Should().Contain("GT06", 5023);
        byProtocol.Should().Contain("Mictrack", 5024);
        byProtocol.Should().Contain("Queclink", 5027);
        byProtocol.Should().Contain("Coban", 5013);
        byProtocol.Should().Contain("iStartek", 5001);
        byProtocol.Should().Contain("Jointech", 5002);
    }

    [Fact]
    public void PublishesTheMqttEndpointSoDevicesAreNotConfiguredByGuesswork()
    {
        var endpoints = Endpoints();

        endpoints.Mqtt.Port.Should().Be(1883);
        endpoints.Mqtt.RequiresApiKey.Should().BeTrue("el modelo de adopción exige ApiKey por dispositivo");
        endpoints.Host.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void EveryPortIsUniqueAndPlausible()
    {
        var endpoints = Endpoints();

        endpoints.TcpListeners.Should().OnlyHaveUniqueItems();
        endpoints.TcpListeners.Select(l => l.Port).Should().OnlyHaveUniqueItems(
            "dos familias en el mismo puerto significa que una decodifica tramas ajenas");
        endpoints.TcpListeners.Should().OnlyContain(l => l.Port > 0 && l.Port <= 65535);
    }

    [Fact]
    public void FallsBackToTheSingleGt06ListenerWhenNoneAreDeclared()
    {
        // Sin sección Listeners el gateway abre sólo GT06 en TcpGateway:Port; el endpoint
        // debe decir lo mismo para que la UI no se quede sin puerto que mostrar.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TcpGateway:Enabled"] = "true",
                ["TcpGateway:Port"] = "5023",
                ["MqttBroker:Port"] = "1883",
                ["MqttBroker:PublicHost"] = "gps.example.com"
            })
            .Build();

        var controller = new GatewaysController(configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var dto = (controller.GetEndpoints() as OkObjectResult)!.Value as GatewaysController.GatewayEndpointsDto;

        dto!.TcpListeners.Should().HaveCount(1);
        dto.TcpListeners[0].Protocol.Should().Be("GT06");
        dto.TcpListeners[0].Port.Should().Be(5023);
        dto.Host.Should().Be("gps.example.com");
    }
}
