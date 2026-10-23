using System.Globalization;
using IoT.Domain.Common;
using IoT.Protocols.Contracts;
using IoT.Protocols.Models;
using IoT.Protocols.Strategies;

namespace IoT.Protocols.Http;

/// <summary>
/// Estrategia para dispositivos HTTP que transmiten tramas delimitadas (CSV, NMEA simplificado, tramas separadas por comas/punto y coma).
/// Ejemplo: "IMEI,TIMESTAMP,LAT,LON,SPEED,COURSE,SATELLITES,VOLTAGE"
/// </summary>
public sealed class HttpCustomFrameTelemetryStrategy : ITelemetryStrategy
{
    public string StrategyName => "HttpCustomFrameTelemetryStrategy";
    public TransportFamily Transport => TransportFamily.Http;

    public bool CanHandle(TransportMessage message)
    {
        var isHttp = !string.IsNullOrWhiteSpace(message.TransportHint) &&
                     message.TransportHint.StartsWith("http", StringComparison.OrdinalIgnoreCase);

        var payloadStr = message.Payload?.Trim() ?? string.Empty;
        var isDelimited = (payloadStr.Contains(',') || payloadStr.Contains(';')) &&
                          !payloadStr.StartsWith('{') &&
                          !payloadStr.Contains('=');

        return isHttp && isDelimited;
    }

    public Result<CanonicalDeviceTelemetry> Parse(TransportMessage message)
    {
        try
        {
            var payloadStr = message.Payload?.Trim() ?? string.Empty;
            var delimiter = payloadStr.Contains(';') ? ';' : ',';
            var parts = payloadStr.Split(delimiter, StringSplitOptions.TrimEntries);

            var canonical = new CanonicalDeviceTelemetry();

            // Convención estándar de tramas de rastreo:
            // [0]=ID/IMEI, [1]=Timestamp, [2]=Lat, [3]=Lon, [4]=Speed, [5]=Course, [6]=Sats, [7]=Voltaje
            if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                canonical.Imei = parts[0];
                canonical.ClientId = parts[0];
            }

            if (parts.Length > 1 && DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                canonical.Timestamp = dt;
            }

            if (parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var lat))
            {
                canonical.Latitude = lat;
            }

            if (parts.Length > 3 && double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var lon))
            {
                canonical.Longitude = lon;
            }

            if (parts.Length > 4 && float.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var spd))
            {
                canonical.Speed = spd;
            }

            if (parts.Length > 5 && float.TryParse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var crs))
            {
                canonical.Course = crs;
            }

            if (parts.Length > 6 && int.TryParse(parts[6], out var sats))
            {
                canonical.Satellites = sats;
            }

            if (parts.Length > 7 && double.TryParse(parts[7], NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            {
                canonical.Voltage = v;
            }

            if (string.IsNullOrWhiteSpace(canonical.ClientId) && !string.IsNullOrWhiteSpace(message.DeviceIdentifier))
            {
                canonical.ClientId = message.DeviceIdentifier;
            }

            return Result<CanonicalDeviceTelemetry>.Success(canonical);
        }
        catch (Exception ex)
        {
            return Result<CanonicalDeviceTelemetry>.Failure(
                new Error("HttpCustomFrameTelemetryStrategy.ParseError", $"Error parseando trama delimitada HTTP: {ex.Message}"));
        }
    }
}
