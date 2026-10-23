namespace IoT.Application.Common.Interfaces;

/// <summary>
/// Expone los datos de conexión públicos del broker MQTT embebido para mostrarlos
/// al operador al aprovisionar un dispositivo. La implementación vive en Infrastructure
/// (lee la configuración), manteniendo Application libre de dependencias de hosting.
/// </summary>
public interface IMqttConnectionInfoProvider
{
    /// <summary>Host o dominio público por el que los dispositivos alcanzan el broker.</summary>
    string PublicHost { get; }

    /// <summary>Puerto TCP del broker MQTT embebido.</summary>
    int Port { get; }

    /// <summary>Indica si se requiere TLS (mqtts).</summary>
    bool Tls { get; }
}
