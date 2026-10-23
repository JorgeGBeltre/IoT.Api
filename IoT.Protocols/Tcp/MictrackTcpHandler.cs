namespace IoT.Protocols.Tcp;

/// <summary>
/// Dialecto Mictrack (M558G-V2) de la familia GT06.
///
/// Su especificación renumera dos tramas SIN cambiar su contenido: la posición es 0x22 en
/// vez de 0x12 y la alarma 0x26 en vez de 0x16
/// (References/Protocolos/M558G-V2_GPS Tracker_Communication_Protocol_v1.8.2.pdf, tabla 4.3).
/// El cuerpo que sigue al número de protocolo es byte a byte el del GT06 —DateTime(6),
/// satBytes(1), lat(4), lon(4), speed(1), course/status(2), MCC/MNC/LAC/CellID— comprobado
/// contra los ejemplos hexadecimales del propio documento (5.2.2 y 5.3.1.21), así que basta
/// traducir el número y reutilizar el decodificador.
///
/// POR QUÉ NO SE HACE COMO ALIAS GLOBAL 0x22→0x12: el X3Tech NT20 también define un 0x22,
/// pero con OTRO layout (antepone Location Source Type y Terminal ID antes de los datos GPS,
/// NT20 Protocol rev3.21 sección 5.5.1). Un alias global haría que un NT20 en modo "new
/// frame" se decodificase con los offsets equivocados y publicase posiciones falsas, que es
/// peor que descartar la trama. Por eso el dialecto va atado a su propio listener/puerto.
///
/// AVISO sobre lo que el equipo emite de verdad: el documento se contradice. Las tablas
/// declaran 0x22/0x26, pero el apéndice B, con capturas de tráfico REAL del equipo, muestra
/// 0x12 para posición y 0x16 para alarma con su ACK. Por eso este handler acepta AMBOS
/// juegos: los canónicos siguen funcionando por herencia y los del dialecto se traducen.
/// </summary>
public sealed class MictrackTcpHandler : Gt06TcpHandler
{
    /// <summary>Posición en el dialecto Mictrack (equivale a 0x12 del GT06).</summary>
    public const byte ProtocolLocationMictrack = 0x22;

    /// <summary>Alarma en el dialecto Mictrack (equivale a 0x16 del GT06).</summary>
    public const byte ProtocolAlarmMictrack = 0x26;

    public override string Family => "Mictrack";

    protected override byte NormalizeProtocol(byte protocolNumber) => protocolNumber switch
    {
        ProtocolLocationMictrack => Binary.R12LFFrame.ProtocolLocation,
        ProtocolAlarmMictrack => Binary.R12LFFrame.ProtocolAlarm,
        _ => protocolNumber
    };
}
