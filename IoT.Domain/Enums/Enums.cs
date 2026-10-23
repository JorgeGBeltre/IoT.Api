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
