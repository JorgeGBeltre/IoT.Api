using FluentAssertions;
using IoT.Domain.Entities;
using IoT.Domain.Enums;
using Xunit;

namespace IoT.Tests.Unit;

public class DeviceAttributeTests
{
    [Fact]
    public void Create_WithValidParameters_ShouldReturnAttribute()
    {
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        var result = DeviceAttribute.Create(tenantId, deviceId, AttributeScope.Shared, "reportIntervalSec", "60");

        result.IsSuccess.Should().BeTrue();
        var attr = result.Value;
        attr.TenantId.Should().Be(tenantId);
        attr.DeviceId.Should().Be(deviceId);
        attr.Scope.Should().Be(AttributeScope.Shared);
        attr.Key.Should().Be("reportIntervalSec");
        attr.Value.Should().Be("60");
        attr.LastUpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Create_ShouldTrimKey()
    {
        var result = DeviceAttribute.Create(Guid.NewGuid(), Guid.NewGuid(), AttributeScope.Client, "  battery  ", "42");

        result.IsSuccess.Should().BeTrue();
        result.Value.Key.Should().Be("battery");
    }

    [Fact]
    public void Create_WithNullValue_ShouldDefaultToEmptyString()
    {
        var result = DeviceAttribute.Create(Guid.NewGuid(), Guid.NewGuid(), AttributeScope.Server, "note", null!);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(string.Empty);
    }

    [Fact]
    public void Create_WithEmptyTenant_ShouldFail()
    {
        var result = DeviceAttribute.Create(Guid.Empty, Guid.NewGuid(), AttributeScope.Client, "battery", "42");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("DeviceAttribute.InvalidTenant");
    }

    [Fact]
    public void Create_WithEmptyDevice_ShouldFail()
    {
        var result = DeviceAttribute.Create(Guid.NewGuid(), Guid.Empty, AttributeScope.Client, "battery", "42");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("DeviceAttribute.InvalidDevice");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankKey_ShouldFail(string? key)
    {
        var result = DeviceAttribute.Create(Guid.NewGuid(), Guid.NewGuid(), AttributeScope.Client, key!, "42");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("DeviceAttribute.InvalidKey");
    }

    [Fact]
    public void UpdateValue_ShouldReplaceValueAndRefreshTimestamp()
    {
        var attr = DeviceAttribute.Create(Guid.NewGuid(), Guid.NewGuid(), AttributeScope.Shared, "speedLimitKph", "80").Value;
        var before = attr.LastUpdatedAt.AddMinutes(-10);
        typeof(DeviceAttribute).GetProperty(nameof(DeviceAttribute.LastUpdatedAt))!
            .SetValue(attr, before); // forzar una marca antigua para comprobar el refresco

        attr.UpdateValue("100");

        attr.Value.Should().Be("100");
        attr.LastUpdatedAt.Should().BeAfter(before);
        attr.LastUpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void UpdateValue_WithNull_ShouldSetEmptyString()
    {
        var attr = DeviceAttribute.Create(Guid.NewGuid(), Guid.NewGuid(), AttributeScope.Server, "note", "x").Value;

        attr.UpdateValue(null!);

        attr.Value.Should().Be(string.Empty);
    }

    [Fact]
    public void AttributeScope_ShouldHaveThreeExpectedMembers()
    {
        Enum.GetValues<AttributeScope>().Should().BeEquivalentTo(new[]
        {
            AttributeScope.Client, AttributeScope.Shared, AttributeScope.Server
        });
        ((int)AttributeScope.Client).Should().Be(1);
        ((int)AttributeScope.Shared).Should().Be(2);
        ((int)AttributeScope.Server).Should().Be(3);
    }
}
