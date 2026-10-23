using IoT.Domain.Common;
using IoT.Protocols.Binary;
using IoT.Protocols.Contracts;
using IoT.Protocols.Mapping;
using IoT.Protocols.Models;
using IoT.Protocols.Strategies;

namespace IoT.Protocols.Tcp;

/// <summary>
/// Estrategia para dispositivos TCP binarios (SEEWORLD R12L-F, GT06 y similares).
/// Soporta tanto tramas binarias directas (0x78 0x78 / 0x79 0x79) como tramas intermedias normalizadas por el socket TCP.
/// </summary>
public sealed class TcpBinaryTelemetryStrategy : ITelemetryStrategy
{
    public string StrategyName => "TcpBinaryTelemetryStrategy";
    public TransportFamily Transport => TransportFamily.TcpBinary;

    public bool CanHandle(TransportMessage message)
    {
        // 1. Transporte TCP explícito
        if (!string.IsNullOrWhiteSpace(message.TransportHint) &&
            message.TransportHint.StartsWith("tcp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(message.TransportProtocol, "TCP-R12LF", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Source, "r12l-f", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. Cabecera binaria 0x78 0x78 o 0x79 0x79
        var bytes = message.RawBytes;
        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0x78 && bytes[1] == 0x78) return true;
            if (bytes[0] == 0x79 && bytes[1] == 0x79) return true;
        }

        return false;
    }

    public Result<CanonicalDeviceTelemetry> Parse(TransportMessage message)
    {
        try
        {
            var bytes = message.RawBytes;

            // Caso A: Trama binaria pura
            if (bytes.Length >= 5 &&
                ((bytes[0] == 0x78 && bytes[1] == 0x78) ||
                 (bytes[0] == 0x79 && bytes[1] == 0x79)))
            {
                if (!R12LFFrame.TryParse(bytes, out var frame, out _) || frame == null)
                {
                    return Result<CanonicalDeviceTelemetry>.Failure(
                        new Error("TcpBinaryTelemetryStrategy.InvalidFrame", "La trama TCP binaria no tiene un formato R12LF válido."));
                }

                var (pos, recordedAt) = frame.DecodeLocationPacket();
                var imei = frame.ExtractImeiFromLoginPacket();

                var canonical = new CanonicalDeviceTelemetry
                {
                    Imei = !string.IsNullOrWhiteSpace(imei) ? imei : message.DeviceIdentifier,
                    ClientId = !string.IsNullOrWhiteSpace(imei) ? imei : message.DeviceIdentifier,
                    Timestamp = recordedAt,
                    Latitude = pos?.Latitude,
                    Longitude = pos?.Longitude,
                    Speed = pos?.SpeedKph,
                    Course = pos?.Heading,
                    Satellites = pos?.Satellites,
                    IsValid = pos?.IsValid
                };

                canonical.AdditionalAttributes["protocol_number"] = $"0x{frame.ProtocolNumber:X2}";
                canonical.AdditionalAttributes["serial_number"] = frame.SerialNumber.ToString();

                return Result<CanonicalDeviceTelemetry>.Success(canonical);
            }

            // Caso B: JSON generado por el socket o puente TCP
            var canonicalFromJson = TelemetryAutoMapper.MapFromJson(message.Payload, message.DeviceIdentifier);
            return Result<CanonicalDeviceTelemetry>.Success(canonicalFromJson);
        }
        catch (Exception ex)
        {
            return Result<CanonicalDeviceTelemetry>.Failure(
                new Error("TcpBinaryTelemetryStrategy.ParseError", $"Error parseando trama TCP binaria: {ex.Message}"));
        }
    }
}
