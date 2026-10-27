namespace IoT.Domain.Enums;

public enum DeviceAdminStatus
{
    Active = 1,
    Suspended = 2,
    Revoked = 3
}

public enum DeviceOperationalStatus
{
    Offline = 0,
    Online = 1,
    Stale = 2,
    Error = 3
}

public enum CapabilityType
{
    Gps = 1,
    Switch = 2,
    LevelTelemetry = 3,
    Pressure = 4,
    Temperature = 5,
    NfcTicket = 6,
    QrReader = 7,
    Ota = 8,
    Health = 9
}

public enum CommandStatus
{
    Pending = 1,
    Sent = 2,
    Confirmed = 3,
    Failed = 4,
    TimedOut = 5,
    Delivered = 6
}

public enum NetworkType
{
    Unknown = 0,
    Wifi = 1,
    Cellular = 2,
    Ethernet = 3,
    Ble = 4
}

/// <summary>
/// Ámbito de un atributo de dispositivo (modelo de atributos de ThingsBoard, fase P1a). Define
/// QUIÉN es la fuente de verdad del valor y en qué dirección fluye:
/// <list type="bullet">
///   <item><see cref="Client"/>: lo reporta el EQUIPO (batería, rssi, versión de firmware).</item>
///   <item><see cref="Shared"/>: lo fija el OPERADOR y se sincroniza AL equipo (intervalo de
///     reporte, límite de velocidad, estado deseado de un relé). El push al dispositivo depende del
///     downlink de cada protocolo y es un seguimiento (no entra en P1a).</item>
///   <item><see cref="Server"/>: metadato de negocio SÓLO-servidor que el equipo nunca ve (fecha de
///     instalación, póliza, notas).</item>
/// </list>
/// </summary>
public enum AttributeScope
{
    Client = 1,
    Shared = 2,
    Server = 3
}
