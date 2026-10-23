namespace IoT.Protocols.Mapping;

/// <summary>
/// DTOs que mapean directamente las estructuras nativas de microcontroladores (Types.h).
/// </summary>
public sealed class DeviceInfoDto
{
    public string? ChipId { get; set; }
    public string? ChipType { get; set; }
    public string? MacAddress { get; set; }
    public string? ClientID { get; set; }
    public string? FirmwareVersion { get; set; }
}

public sealed class GpsDataDto
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public float Altitude { get; set; }
    public float Speed { get; set; }
    public float Course { get; set; }
    public int Satellites { get; set; }
    public bool IsValid { get; set; }
    public string? Timestamp { get; set; }
}

public sealed class AccidentDataDto
{
    public bool Impact_detected { get; set; }
    public bool Rollover_detected { get; set; }
    public float Impact_magnitude { get; set; }
    public float Roll_angle { get; set; }
    public float Pitch_angle { get; set; }
    public ulong Detection_time { get; set; }
}

/// <summary>
/// DTO representativo del envelope MQTT genérico multi-dispositivo.
/// </summary>
public sealed class MqttDeviceMessageDto
{
    public DeviceInfoDto? Device { get; set; }
    public string? Timestamp { get; set; }
    public Dictionary<string, object>? Details { get; set; }
}
