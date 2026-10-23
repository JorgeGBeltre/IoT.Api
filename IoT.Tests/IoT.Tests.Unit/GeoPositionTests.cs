using FluentAssertions;
using IoT.Domain.ValueObjects;
using Xunit;

namespace IoT.Tests.Unit;

public class GeoPositionTests
{
    [Fact]
    public void Create_WithValidCoordinates_ShouldReturnSuccess()
    {
        // Act
        var result = GeoPosition.Create(18.4861, -69.9312, 60.5f, 180.0f, 15.0f, 8, true);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Latitude.Should().Be(18.4861);
        result.Value.Longitude.Should().Be(-69.9312);
        result.Value.SpeedKph.Should().Be(60.5f);
        result.Value.Heading.Should().Be(180.0f);
        result.Value.Satellites.Should().Be(8);
        result.Value.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(-91.0, 0.0)]
    [InlineData(91.0, 0.0)]
    [InlineData(0.0, -181.0)]
    [InlineData(0.0, 181.0)]
    public void Create_WithOutOfRangeCoordinates_ShouldReturnFailure(double lat, double lon)
    {
        // Act
        var result = GeoPosition.Create(lat, lon);

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_WithNegativeSpeed_ShouldReturnFailure()
    {
        // Act
        var result = GeoPosition.Create(18.0, -69.0, speedKph: -5.0f);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("GeoPosition.InvalidSpeed");
    }

    [Fact]
    public void DistanceToKm_BetweenKnownCities_ShouldCalculateCorrectDistance()
    {
        // Santo Domingo: (18.4861, -69.9312)
        // Santiago de los Caballeros: (19.4517, -70.6970)
        // Expected distance: ~130 - 145 km
        var stoDgo = GeoPosition.Create(18.4861, -69.9312).Value;
        var santiago = GeoPosition.Create(19.4517, -70.6970).Value;

        // Act
        double distanceKm = stoDgo.DistanceToKm(santiago);

        // Assert
        distanceKm.Should().BeInRange(130.0, 145.0);
    }
}
