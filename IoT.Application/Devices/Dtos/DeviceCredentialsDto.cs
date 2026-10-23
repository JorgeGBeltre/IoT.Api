namespace IoT.Application.Devices.Dtos;

/// <summary>
/// Credenciales y datos de conexión MQTT de un dispositivo. El texto plano de la
/// ApiKey (<see cref="MqttPasswordPlaintext"/>) sólo se rellena al generarla/rotarla;
/// en consultas posteriores es null y sólo se expone la huella.
/// </summary>
public sealed record DeviceCredentialsDto(
    Guid DeviceId,
    string ChipId,
    string MqttUsername,
    string? MqttPasswordPlaintext,
    string? MqttPasswordFingerprint,
    string MqttTopicPrefix,
    string BrokerHost,
    int BrokerPort,
    bool BrokerTls,
    DateTime IssuedAt,
    string? IssuedBy);
