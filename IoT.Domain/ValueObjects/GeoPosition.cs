using IoT.Domain.Common;

namespace IoT.Domain.ValueObjects;

public sealed record GeoPosition
{
    public double Latitude { get; }
    public double Longitude { get; }
    public float? SpeedKph { get; }
    public float? Heading { get; }
    public float? AltitudeM { get; }
    public short? Satellites { get; }
    public bool IsValid { get; }

    private GeoPosition(
        double latitude,
        double longitude,
        float? speedKph,
        float? heading,
        float? altitudeM,
        short? satellites,
        bool isValid)
    {
        Latitude = latitude;
        Longitude = longitude;
        SpeedKph = speedKph;
        Heading = heading;
        AltitudeM = altitudeM;
        Satellites = satellites;
        IsValid = isValid;
    }

    public static Result<GeoPosition> Create(
        double latitude,
        double longitude,
        float? speedKph = null,
        float? heading = null,
        float? altitudeM = null,
        short? satellites = null,
        bool isValid = true)
    {
        if (latitude < -90.0 || latitude > 90.0)
            return Result<GeoPosition>.Failure(new Error("GeoPosition.InvalidLatitude", "Latitude must be between -90.0 and 90.0 degrees."));

        if (longitude < -180.0 || longitude > 180.0)
            return Result<GeoPosition>.Failure(new Error("GeoPosition.InvalidLongitude", "Longitude must be between -180.0 and 180.0 degrees."));

        if (speedKph.HasValue && speedKph.Value < 0.0f)
            return Result<GeoPosition>.Failure(new Error("GeoPosition.InvalidSpeed", "Speed cannot be negative."));

        if (heading.HasValue && (heading.Value < 0.0f || heading.Value > 360.0f))
            return Result<GeoPosition>.Failure(new Error("GeoPosition.InvalidHeading", "Heading must be between 0.0 and 360.0 degrees."));

        return new GeoPosition(latitude, longitude, speedKph, heading, altitudeM, satellites, isValid);
    }

    public double DistanceToKm(GeoPosition other)
    {
        const double earthRadiusKm = 6371.0;
        double dLat = DegreesToRadians(other.Latitude - Latitude);
        double dLon = DegreesToRadians(other.Longitude - Longitude);

        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(DegreesToRadians(Latitude)) * Math.Cos(DegreesToRadians(other.Latitude)) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadiusKm * c;
    }

    private static double DegreesToRadians(double deg) => deg * (Math.PI / 180.0);
}
