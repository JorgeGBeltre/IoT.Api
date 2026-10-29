using IoT.Api.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IoT.Api.Controllers;

/// <summary>
/// Puntos de conexión que el OPERADOR debe configurar en cada dispositivo: host y puerto
/// por familia de protocolo TCP, más el del broker MQTT.
///
/// Existe porque era la causa raíz de un fallo real: el backend no publicaba en ninguna
/// parte los puertos de <c>TcpGateway:Listeners</c>, así que el formulario de alta del
/// frontend los traía cableados a mano y los SEIS estaban equivocados —y dos de ellos
/// apuntaban al listener de otra familia (un Queclink al handler de iStartek, un Coban al
/// de Jointech)—. Cualquier equipo dado de alta siguiendo la UI nunca llegaba a conectar.
/// Con este endpoint la UI lee los puertos reales y deja de inventarlos.
///
/// AUTORIZACIÓN: expone INFRAESTRUCTURA de conexión (host público + puertos de los listeners),
/// la misma categoría que <see cref="BrokersController"/>, así que comparte su gate
/// <see cref="AuthorizationPolicies.BrokerManage"/>: sólo admin/superadmin (configurable con
/// <c>Authorization:BrokerManageRoles</c>) pueden leerlo. Antes el <c>[Authorize]</c> desnudo
/// dejaba que cualquier usuario autenticado del tenant leyera host:port. El único consumidor en la
/// UI es la página de Integraciones (ya restringida a admin); el alta de dispositivos NO usa este
/// endpoint (deriva el host del navegador y el puerto del catálogo).
/// </summary>
[Authorize(Policy = AuthorizationPolicies.BrokerManage)]
[ApiController]
[Route("api/v1/gateways")]
public sealed class GatewaysController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public GatewaysController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public sealed record TcpListenerDto(string Protocol, int Port);

    public sealed record MqttEndpointDto(string Host, int Port, bool Tls, bool RequiresApiKey);

    public sealed record GatewayEndpointsDto(
        string Host,
        bool TcpEnabled,
        IReadOnlyList<TcpListenerDto> TcpListeners,
        MqttEndpointDto Mqtt);

    /// <summary>
    /// Host y puertos reales a los que apuntar los dispositivos. Se leen de la MISMA
    /// configuración que usa el gateway al abrir los listeners, para que no puedan divergir.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetEndpoints()
    {
        bool tcpEnabled = !bool.TryParse(_configuration["TcpGateway:Enabled"], out var enabled) || enabled;

        var listeners = _configuration.GetSection("TcpGateway:Listeners").GetChildren()
            .Select(section => new
            {
                Protocol = section["Protocol"],
                Port = int.TryParse(section["Port"], out var port) ? port : 0
            })
            .Where(l => !string.IsNullOrWhiteSpace(l.Protocol) && l.Port > 0)
            .Select(l => new TcpListenerDto(l.Protocol!, l.Port))
            .ToList();

        // Si no se declara ningún listener, el gateway abre sólo GT06 en TcpGateway:Port.
        if (listeners.Count == 0 && tcpEnabled)
        {
            int fallbackPort = int.TryParse(_configuration["TcpGateway:Port"], out var p) ? p : 5023;
            listeners.Add(new TcpListenerDto("GT06", fallbackPort));
        }

        // Host público por el que los dispositivos alcanzan la plataforma. Es el mismo que
        // se muestra en las credenciales MQTT del dispositivo.
        string host = _configuration["MqttBroker:PublicHost"] ?? Request.Host.Host;

        var mqtt = new MqttEndpointDto(
            Host: host,
            Port: int.TryParse(_configuration["MqttBroker:Port"], out var mqttPort) ? mqttPort : 1883,
            Tls: bool.TryParse(_configuration["MqttBroker:Tls"], out var tls) && tls,
            RequiresApiKey: !(bool.TryParse(_configuration["MqttBroker:AllowAnonymous"], out var anon) && anon));

        return Ok(new GatewayEndpointsDto(host, tcpEnabled, listeners, mqtt));
    }
}
