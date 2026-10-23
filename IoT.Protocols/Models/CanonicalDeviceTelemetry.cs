using IoT.Domain.ValueObjects;
using IoT.Protocols.Contracts;

namespace IoT.Protocols.Models;

/// <summary>
/// Modelo canónico unificado de telemetría IoT.
/// Declara todos los parámetros y métricas posibles que pueden enviar los dispositivos
/// (GPS, accidentes/inerciales, eléctricos, ambientales, industriales, relés y control de acceso),
/// correspondiente a Types.h y protocolos MQTT/HTTP/TCP.
/// </summary>
public sealed class CanonicalDeviceTelemetry
{
    // ==========================================
    // 1. Device Info (Types.h: DeviceInfo)
    // ==========================================
    public string? ChipId { get; set; }
    public string? ChipType { get; set; }
    public string? MacAddress { get; set; }
    public string? ClientId { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? Imei { get; set; }
    public string? Imsi { get; set; }
    public string? Iccid { get; set; }

    // ==========================================
    // 2. GPS & Posicionamiento (Types.h: GPSData)
    // ==========================================
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public float? Altitude { get; set; }
    public float? Speed { get; set; }
    public float? Course { get; set; }
    public int? Satellites { get; set; }
    public bool? IsValid { get; set; }
    public DateTime? Timestamp { get; set; }

    // ==========================================
    // 3. Accidentes & Cinemática (Types.h: AccidentData)
    // ==========================================
    public bool? ImpactDetected { get; set; }
    public bool? RolloverDetected { get; set; }
    public float? ImpactMagnitude { get; set; }
    public float? RollAngle { get; set; }
    public float? PitchAngle { get; set; }
    public ulong? DetectionTime { get; set; }
    public float? AccX { get; set; }
    public float? AccY { get; set; }
    public float? AccZ { get; set; }

    // ==========================================
    // 4. Parámetros Eléctricos & Batería
    // ==========================================
    public double? Voltage { get; set; }
    public double? Battery { get; set; }
    public double? Current { get; set; }
    public double? Power { get; set; }
    public double? Energy { get; set; }
    public double? Frequency { get; set; }

    // ==========================================
    // 5. Sensores Ambientales & Niveles
    // ==========================================
    public double? Temperature { get; set; }
    public double? Pressure { get; set; }
    public double? Humidity { get; set; }
    public double? GasLevel { get; set; }
    public double? Volume { get; set; }
    public double? FlowRate { get; set; }
    public int? Rssi { get; set; }

    // ==========================================
    // 6. Estado de Actuadores, Relés & Acceso
    // ==========================================
    public bool? RelayState { get; set; }
    public bool? SwitchState { get; set; }
    public bool? AccessGranted { get; set; }
    public bool? DoorOpen { get; set; }
    public string? CardId { get; set; }
    public string? PinCode { get; set; }

    // ==========================================
    // 7. Flota, Diagnósticos & Alarmas
    // ==========================================
    public double? Odometer { get; set; }
    public double? EngineHours { get; set; }
    public double? FuelLevel { get; set; }
    public bool? Ignition { get; set; }
    public bool? Emergency { get; set; }
    public string? AlarmCode { get; set; }

    // ==========================================
    // 8. Métricas y Atributos Adicionales (Extensible)
    // ==========================================
    public Dictionary<string, double> AdditionalMetrics { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> AdditionalAttributes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Convierte la telemetría canónica a un NormalizedTelemetryEvent para el pipeline del dominio.
    /// </summary>
    public NormalizedTelemetryEvent ToNormalizedEvent(string? fallbackDeviceId = null, string? messageId = null, string? rawPayload = null)
    {
        var deviceId = !string.IsNullOrWhiteSpace(ClientId) ? ClientId :
                       !string.IsNullOrWhiteSpace(ChipId) ? ChipId :
                       !string.IsNullOrWhiteSpace(Imei) ? Imei :
                       !string.IsNullOrWhiteSpace(MacAddress) ? MacAddress :
                       fallbackDeviceId ?? "unknown-device";

        var recordedAt = Timestamp ?? DateTime.UtcNow;

        GeoPosition? position = null;
        if (Latitude.HasValue && Longitude.HasValue)
        {
            var posResult = GeoPosition.Create(
                Latitude.Value,
                Longitude.Value,
                Altitude ?? 0,
                Speed ?? 0,
                Course ?? 0);

            if (posResult.IsSuccess)
            {
                position = posResult.Value;
            }
        }

        var numericMetrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Copiar métricas adicionales previas
        foreach (var (k, v) in AdditionalMetrics)
        {
            numericMetrics[k] = v;
        }

        foreach (var (k, v) in AdditionalAttributes)
        {
            attributes[k] = v;
        }

        // Poblar métricas numéricas canónicas si están definidas
        if (Voltage.HasValue) numericMetrics["voltage"] = Voltage.Value;
        if (Battery.HasValue) numericMetrics["battery"] = Battery.Value;
        if (Current.HasValue) numericMetrics["current"] = Current.Value;
        if (Power.HasValue) numericMetrics["power"] = Power.Value;
        if (Energy.HasValue) numericMetrics["energy"] = Energy.Value;
        if (Frequency.HasValue) numericMetrics["frequency"] = Frequency.Value;

        if (Speed.HasValue) numericMetrics["speed"] = Speed.Value;
        if (Altitude.HasValue) numericMetrics["altitude"] = Altitude.Value;
        if (Course.HasValue) numericMetrics["course"] = Course.Value;
        if (Satellites.HasValue) numericMetrics["satellites"] = Satellites.Value;

        if (Temperature.HasValue)
        {
            numericMetrics["temperature"] = Temperature.Value;
            if (!numericMetrics.ContainsKey("temp_celsius")) numericMetrics["temp_celsius"] = Temperature.Value;
        }

        if (Pressure.HasValue)
        {
            numericMetrics["pressure"] = Pressure.Value;
            if (!numericMetrics.ContainsKey("pressure_bar")) numericMetrics["pressure_bar"] = Pressure.Value;
        }

        if (Humidity.HasValue) numericMetrics["humidity"] = Humidity.Value;
        if (GasLevel.HasValue)
        {
            numericMetrics["gas_level"] = GasLevel.Value;
            if (!numericMetrics.ContainsKey("fill_percentage")) numericMetrics["fill_percentage"] = GasLevel.Value;
        }

        if (Volume.HasValue)
        {
            numericMetrics["volume"] = Volume.Value;
            if (!numericMetrics.ContainsKey("volume_liters")) numericMetrics["volume_liters"] = Volume.Value;
        }

        if (FlowRate.HasValue) numericMetrics["flow_rate"] = FlowRate.Value;
        if (Rssi.HasValue)
        {
            numericMetrics["rssi"] = Rssi.Value;
            if (!numericMetrics.ContainsKey("rssi_dbm")) numericMetrics["rssi_dbm"] = Rssi.Value;
        }

        if (ImpactMagnitude.HasValue) numericMetrics["impact_magnitude"] = ImpactMagnitude.Value;
        if (RollAngle.HasValue) numericMetrics["roll_angle"] = RollAngle.Value;
        if (PitchAngle.HasValue) numericMetrics["pitch_angle"] = PitchAngle.Value;
        if (DetectionTime.HasValue) numericMetrics["detection_time"] = DetectionTime.Value;
        if (AccX.HasValue) numericMetrics["acc_x"] = AccX.Value;
        if (AccY.HasValue) numericMetrics["acc_y"] = AccY.Value;
        if (AccZ.HasValue) numericMetrics["acc_z"] = AccZ.Value;

        if (RelayState.HasValue)
        {
            numericMetrics["relay_state"] = RelayState.Value ? 1.0 : 0.0;
            if (!numericMetrics.ContainsKey("relay_state_binary")) numericMetrics["relay_state_binary"] = RelayState.Value ? 1.0 : 0.0;
        }
        if (SwitchState.HasValue) numericMetrics["switch_state"] = SwitchState.Value ? 1.0 : 0.0;
        if (Odometer.HasValue) numericMetrics["odometer"] = Odometer.Value;
        if (EngineHours.HasValue) numericMetrics["engine_hours"] = EngineHours.Value;
        if (FuelLevel.HasValue) numericMetrics["fuel_level"] = FuelLevel.Value;

        // Poblar atributos canónicos si están definidos
        if (!string.IsNullOrWhiteSpace(ChipId)) attributes["chip_id"] = ChipId;
        if (!string.IsNullOrWhiteSpace(ChipType)) attributes["chip_type"] = ChipType;
        if (!string.IsNullOrWhiteSpace(MacAddress)) attributes["mac_address"] = MacAddress;
        if (!string.IsNullOrWhiteSpace(ClientId)) attributes["client_id"] = ClientId;
        if (!string.IsNullOrWhiteSpace(FirmwareVersion)) attributes["firmware_version"] = FirmwareVersion;
        if (!string.IsNullOrWhiteSpace(Imei)) attributes["imei"] = Imei;
        if (!string.IsNullOrWhiteSpace(CardId)) attributes["card_id"] = CardId;
        if (!string.IsNullOrWhiteSpace(AlarmCode)) attributes["alarm_code"] = AlarmCode;

        if (IsValid.HasValue) attributes["is_valid"] = IsValid.Value.ToString();
        if (ImpactDetected.HasValue) attributes["impact_detected"] = ImpactDetected.Value.ToString();
        if (RolloverDetected.HasValue) attributes["rollover_detected"] = RolloverDetected.Value.ToString();
        if (AccessGranted.HasValue) attributes["access_granted"] = AccessGranted.Value.ToString();
        if (DoorOpen.HasValue) attributes["door_open"] = DoorOpen.Value.ToString();
        if (Ignition.HasValue) attributes["ignition"] = Ignition.Value.ToString();
        if (Emergency.HasValue) attributes["emergency"] = Emergency.Value.ToString();

        return new NormalizedTelemetryEvent
        {
            DeviceIdentifier = deviceId,
            RecordedAt = recordedAt,
            ReceivedAt = DateTime.UtcNow,
            Position = position,
            NumericMetrics = numericMetrics,
            Attributes = attributes,
            MessageId = messageId,
            RawPayload = rawPayload
        };
    }
}
