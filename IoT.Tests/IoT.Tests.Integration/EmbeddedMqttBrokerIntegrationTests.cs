using FluentAssertions;
using IoT.Application.Common.Interfaces;
using IoT.Infrastructure.Mqtt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MQTTnet;
using MQTTnet.Formatter;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// MEDIUM-11: NINGÚN test cubría EmbeddedMqttBroker. El rechazo del ClientId reservado y la
/// exigencia de credencial vivían en OnValidatingConnection y sólo había un test que comparaba
/// una constante (su nombre afirmaba cubrir el CONNECT, pero el cuerpo no tocaba el broker:
/// pasaba aunque se quitara el rechazo). Aquí se arranca el broker REAL en un puerto libre y se
/// conecta un cliente MQTT de verdad.
/// </summary>
public sealed class EmbeddedMqttBrokerIntegrationTests
{
    private static int FreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static EmbeddedMqttBroker CreateBroker(int port)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MqttBroker:Port"] = port.ToString(),
            ["MqttBroker:AllowAnonymous"] = "false",
            // Desactiva el bucle de re-validación periódica: no hace falta para estos tests.
            ["MqttBroker:AuthRecheckSeconds"] = "0"
        }).Build();

        // El ServiceScopeFactory no se llega a usar en los caminos probados (el rechazo ocurre
        // antes de crear el ámbito), pero se pasa uno real por si acaso.
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var clientMap = new Mock<IMqttClientMap>();
        return new EmbeddedMqttBroker(config, scopeFactory, clientMap.Object, NullLogger<EmbeddedMqttBroker>.Instance);
    }

    private static async Task WaitUntilListeningAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new System.Net.Sockets.TcpClient();
                await probe.ConnectAsync(System.Net.IPAddress.Loopback, port);
                return;
            }
            catch
            {
                await Task.Delay(50);
            }
        }
        throw new TimeoutException($"El broker no quedó escuchando en el puerto {port} a tiempo.");
    }

    private static async Task<MqttClientConnectResultCode> TryConnectAsync(int port, string clientId, string? apiKey)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer("localhost", port)
            .WithClientId(clientId)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithTimeout(TimeSpan.FromSeconds(5));
        if (!string.IsNullOrEmpty(apiKey))
            builder.WithCredentials(apiKey, string.Empty);

        try
        {
            var result = await client.ConnectAsync(builder.Build(), CancellationToken.None);
            return result.ResultCode;
        }
        catch (Exception ex)
        {
            // Un CONNACK distinto de Success se traduce en una MqttConnectingFailedException,
            // que lleva el código real en su propiedad ResultCode. Se lee por reflexión para
            // no acoplar el test al namespace exacto de la excepción (cambió entre versiones).
            var prop = ex.GetType().GetProperty("ResultCode");
            if (prop?.GetValue(ex) is MqttClientConnectResultCode rc)
                return rc;
            throw;
        }
        finally
        {
            try { await client.DisconnectAsync(); } catch { }
            client.Dispose();
        }
    }

    [Theory]
    [InlineData("internal-server")]
    [InlineData("INTERNAL-SERVER")] // el rechazo es insensible a mayúsculas
    public async Task ConnectingWithTheReservedServerClientIdIsRejected(string clientId)
    {
        int port = FreeTcpPort();
        var broker = CreateBroker(port);
        await ((IHostedService)broker).StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilListeningAsync(port, TimeSpan.FromSeconds(10));

            // Aunque presente una ApiKey, el ClientId reservado se rechaza ANTES de mirar
            // credenciales: nadie externo puede hacerse pasar por el cliente interno del
            // servidor (que es a quien el ACL concede publicar firmware a cualquier equipo).
            var code = await TryConnectAsync(port, clientId, apiKey: "cualquier-cosa");
            code.Should().Be(MqttClientConnectResultCode.ClientIdentifierNotValid);
        }
        finally
        {
            await ((IHostedService)broker).StopAsync(CancellationToken.None);
            broker.Dispose();
        }
    }

    [Fact]
    public async Task ConnectingWithoutCredentialsIsRejectedWhenAnonymousIsDisabled()
    {
        int port = FreeTcpPort();
        var broker = CreateBroker(port);
        await ((IHostedService)broker).StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilListeningAsync(port, TimeSpan.FromSeconds(10));

            // Sin ApiKey y con AllowAnonymous=false, el broker rechaza por NotAuthorized
            // (antes de crear ningún ámbito ni tocar la base).
            var code = await TryConnectAsync(port, "un-equipo-cualquiera", apiKey: null);
            code.Should().Be(MqttClientConnectResultCode.NotAuthorized);
        }
        finally
        {
            await ((IHostedService)broker).StopAsync(CancellationToken.None);
            broker.Dispose();
        }
    }
}
