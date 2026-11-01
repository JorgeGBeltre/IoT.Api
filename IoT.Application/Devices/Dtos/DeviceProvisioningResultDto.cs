namespace IoT.Application.Devices.Dtos;

/// <summary>
/// Resultado del aprovisionamiento en un solo paso (emparejamiento). <see cref="Created"/> es
/// true si el equipo se registró ahora, y false si ya existía en el tenant y se re-emparejó
/// (se rotó su ApiKey). <see cref="Credentials"/> es el paquete de conexión que la app inyecta
/// en el equipo por BLE/SoftAP; su ApiKey en texto plano se expone UNA sola vez, aquí.
/// </summary>
public sealed record DeviceProvisioningResultDto(bool Created, DeviceCredentialsDto Credentials);
