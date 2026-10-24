namespace IoT.Application.Devices.Dtos;

/// <summary>
/// Credenciales y datos de conexión MQTT de un dispositivo. El texto plano de la
/// ApiKey (<see cref="MqttPasswordPlaintext"/>) sólo se rellena al generarla/rotarla;
/// en consultas posteriores es null y sólo se expone la huella.
///
/// Reparto de campos, que es el que el broker valida de verdad:
/// <list type="bullet">
/// <item><description><see cref="MqttClientId"/>: el ChipId. El broker EXIGE que el
/// ClientId sea el del equipo; con otro rechaza la conexión.</description></item>
/// <item><description><see cref="MqttUsername"/> y <see cref="MqttPasswordPlaintext"/>:
/// la ApiKey, en los dos. Los firmwares la mandan en campos distintos (Relay y GPS en el
/// usuario, el GLP donde lo configure el operador) y el broker acepta cualquiera de los
/// dos, así que la misma ficha sirve para todos.</description></item>
/// </list>
/// </summary>
public sealed record DeviceCredentialsDto(
    Guid DeviceId,
    string ChipId,
    string MqttClientId,
    string? MqttUsername,
    string? MqttPasswordPlaintext,
    string? MqttPasswordFingerprint,
    string MqttTopicPrefix,
    string BrokerHost,
    int BrokerPort,
    bool BrokerTls,
    DateTime IssuedAt,
    string? IssuedBy);
