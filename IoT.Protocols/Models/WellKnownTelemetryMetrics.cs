namespace IoT.Protocols.Models;

/// <summary>
/// Catálogo centralizado de nombres de métricas y alias reconocidos en la plataforma IoT.
/// </summary>
public static class WellKnownTelemetryMetrics
{
    // Métricas GPS & Cinemática
    public const string Latitude = "latitude";
    public const string Longitude = "longitude";
    public const string Altitude = "altitude";
    public const string Speed = "speed";
    public const string Course = "course";
    public const string Satellites = "satellites";
    public const string IsValid = "is_valid";

    // Accidentes & Giroscopio
    public const string ImpactDetected = "impact_detected";
    public const string RolloverDetected = "rollover_detected";
    public const string ImpactMagnitude = "impact_magnitude";
    public const string RollAngle = "roll_angle";
    public const string PitchAngle = "pitch_angle";
    public const string DetectionTime = "detection_time";

    // Eléctrico & Energía
    public const string Voltage = "voltage";
    public const string Battery = "battery";
    public const string Current = "current";
    public const string Power = "power";
    public const string Energy = "energy";
    public const string Frequency = "frequency";

    // Sensores
    public const string Temperature = "temperature";
    public const string Pressure = "pressure";
    public const string Humidity = "humidity";
    public const string GasLevel = "gas_level";
    public const string Volume = "volume";
    public const string FlowRate = "flow_rate";
    public const string Rssi = "rssi";

    // Actuadores & Control
    public const string RelayState = "relay_state";
    public const string SwitchState = "switch_state";
    public const string AccessGranted = "access_granted";

    // Flota
    public const string Odometer = "odometer";
    public const string EngineHours = "engine_hours";
    public const string FuelLevel = "fuel_level";
    public const string Ignition = "ignition";
    public const string Emergency = "emergency";

    // Dispositivo
    public const string ChipId = "chip_id";
    public const string ChipType = "chip_type";
    public const string MacAddress = "mac_address";
    public const string ClientId = "client_id";
    public const string FirmwareVersion = "firmware_version";
}
