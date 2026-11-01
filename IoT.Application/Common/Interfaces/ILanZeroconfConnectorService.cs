using IoT.Domain.Common;

namespace IoT.Application.Common.Interfaces;

/// <summary>
/// Resultado de interrogar un dispositivo ESP32/ESP8266 en la red local (SoftAP :80/device
/// o LAN Zeroconf :8081/zeroconf/info).
/// </summary>
public sealed record LanDeviceProbeResultDto(
    bool Reachable,
    string Host,
    int Port,
    string Mode,
    string? ChipId = null,
    string? MacAddress = null,
    string? Model = null,
    string? FirmwareVersion = null,
    string? DeviceKey = null,
    string? CurrentSsid = null,
    string? SwitchState = null,
    string? StartupMode = null,
    bool? PulseEnabled = null,
    int? PulseWidthMs = null,
    bool? SledOnline = null,
    int? RssiDbm = null,
    string? RawResponseJson = null,
    string? ErrorMessage = null);

/// <summary>
/// Resultado de ejecutar un comando o inyección de aprovisionamiento contra el servidor HTTP
/// local del dispositivo (:80/ap o :8081/zeroconf/*).
/// </summary>
public sealed record LanCommandExecutionResultDto(
    bool Success,
    int HttpStatusCode,
    int DeviceErrorCode,
    bool Encrypted,
    string? DecryptedDataJson = null,
    string? RawResponseJson = null,
    string? ErrorMessage = null);

/// <summary>
/// Conector de red local (patrón <c>thingsboard-gateway</c> REST/LAN Connector) para descubrir,
/// adoptar y controlar dispositivos ESP32/ESP8266 en modo SoftAP (<c>:80/device</c>, <c>:80/ap</c>)
/// o modo LAN Zeroconf (<c>:8081/zeroconf/*</c> en texto plano o cifrado AES-128-CBC con <c>MD5(deviceKey)</c>).
/// </summary>
public interface ILanZeroconfConnectorService
{
    /// <summary>
    /// Valida que el host o IP destino pertenezca a un rango de red local privada (RFC1918 / SoftAP
    /// 192.168.x.x, 10.x.x.x, 172.16-31.x.x o mDNS <c>.local</c>), bloqueando vectores SSRF hacia
    /// loopback, link-local de metadatos cloud (169.254.169.254) o hosts públicos de internet.
    /// </summary>
    Result ValidateLanTargetHost(string host, int port);

    /// <summary>
    /// Interroga un dispositivo en modo SoftAP (<c>GET /device</c>) o LAN Zeroconf (<c>POST /zeroconf/info</c>)
    /// y extrae su identidad (<c>ChipId</c>, <c>MacAddress</c>, <c>Model</c>, <c>FirmwareVersion</c>, <c>DeviceKey</c>).
    /// </summary>
    Task<Result<LanDeviceProbeResultDto>> ProbeDeviceAsync(
        string host,
        int port,
        string mode,
        string? deviceKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Envía un comando o paquete de aprovisionamiento al dispositivo por HTTP local
    /// (<c>POST /ap</c> en SoftAP o <c>POST /zeroconf/&lt;endpoint&gt;</c> en LAN :8081),
    /// aplicando cifrado AES-128-CBC cuando <paramref name="encrypt"/> es <c>true</c>.
    /// </summary>
    Task<Result<LanCommandExecutionResultDto>> SendLanCommandAsync(
        string host,
        int port,
        string endpoint,
        string innerDataJson,
        string? deviceId = null,
        string? deviceKey = null,
        bool encrypt = false,
        CancellationToken cancellationToken = default);
}

