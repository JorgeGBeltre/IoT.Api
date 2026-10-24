using IoT.Domain.Enums;

namespace IoT.Protocols.Contracts;

public interface IDeviceProfile
{
    string ProfileId { get; }
    string Manufacturer { get; }
    string Model { get; }
    string DisplayName { get; }
    string PrimaryTransport { get; }

    /// <summary>
    /// Familia del handler TCP que decodifica a este dispositivo, con el MISMO nombre que
    /// declara <c>ITcpProtocolHandler.Family</c> y que se usa como clave en
    /// <c>TcpGateway:Listeners</c> (GT06, Mictrack, Queclink, Coban, iStartek, Jointech).
    /// Es null para los dispositivos que no entran por el gateway TCP (MQTT nativo, HTTP)
    /// o para los que no tienen listener (p.ej. el Queclink GL300, que reporta por SMS).
    ///
    /// <c>PrimaryTransport</c> es sólo una etiqueta para mostrar y NO vale para esto: dice
    /// cosas como "TCP (Benway)" o "TCP (NT20)", familias que no existen como handler. El
    /// frontend deducía el puerto de esas etiquetas y los seis puertos estaban mal.
    /// </summary>
    string? TcpFamily { get; }

    IReadOnlyList<CapabilityType> SupportedCapabilities { get; }
    IReadOnlyList<string> TelemetryTopicPatterns { get; }
    string CommandTopicPattern { get; }
    IDeviceProtocolAdapter Adapter { get; }
    bool SupportsTransport(string transportName);
}
