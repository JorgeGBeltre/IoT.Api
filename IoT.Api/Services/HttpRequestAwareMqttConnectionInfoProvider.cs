using IoT.Application.Common.Interfaces;
using IoT.Infrastructure.Mqtt;

namespace IoT.Api.Services;

/// <summary>
/// Decorador HTTP-aware de <see cref="IMqttConnectionInfoProvider"/> registrado en el host Web API.
/// Cuando <c>MqttBroker:PublicHost</c> es loopback (<c>localhost</c> / <c>127.0.0.1</c>) o quedó
/// apuntando a otra subred privada RFC1918 tras un cambio de red local, resuelve dinámicamente
/// el host real por el que el cliente alcanzó la API para que el paquete de aprovisionamiento
/// entregado al ESP32/ESP8266 apunte a una IP alcanzable en la LAN.
/// </summary>
public sealed class HttpRequestAwareMqttConnectionInfoProvider : IMqttConnectionInfoProvider
{
    private readonly MqttConnectionInfoProvider _inner;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpRequestAwareMqttConnectionInfoProvider(
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
    {
        _inner = new MqttConnectionInfoProvider(configuration);
        _httpContextAccessor = httpContextAccessor;
    }

    public string PublicHost =>
        MqttConnectionInfoProvider.ResolveEffectiveHost(
            _inner.PublicHost,
            _httpContextAccessor.HttpContext?.Request.Host.Host);

    public int Port => _inner.Port;

    public bool Tls => _inner.Tls;
}

