using FluentAssertions;
using IoT.Application.Common.TelemetryQueue;
using Xunit;

namespace IoT.Tests.Unit;

public sealed class OriginatorPartitionResolverTests
{
    [Fact]
    public void ComputeFnv1a32_IsDeterministic_AcrossInvocations()
    {
        string input = "ESP32-CHIP-998877";
        uint hash1 = OriginatorPartitionResolver.ComputeFnv1a32(input);
        uint hash2 = OriginatorPartitionResolver.ComputeFnv1a32(input);

        hash1.Should().Be(hash2);
        hash1.Should().NotBe(0u);
    }

    [Fact]
    public void GetPartitionIndex_WithSinglePartition_AlwaysReturnsZero()
    {
        int p1 = OriginatorPartitionResolver.GetPartitionIndex("dev-1", 1);
        int p2 = OriginatorPartitionResolver.GetPartitionIndex("dev-2", 1);
        int p3 = OriginatorPartitionResolver.GetPartitionIndex("dev-3", 0);

        p1.Should().Be(0);
        p2.Should().Be(0);
        p3.Should().Be(0);
    }

    [Fact]
    public void GetPartitionIndex_WithMultiplePartitions_IsStrictlyBoundedAndDeterministic()
    {
        int partitionCount = 8;
        string deviceA = "TRACKER-GT06-868120000000001";
        string deviceB = "GLP-TANK-SENSOR-002";

        int pA1 = OriginatorPartitionResolver.GetPartitionIndex(deviceA, partitionCount);
        int pA2 = OriginatorPartitionResolver.GetPartitionIndex(deviceA, partitionCount);
        int pB = OriginatorPartitionResolver.GetPartitionIndex(deviceB, partitionCount);

        pA1.Should().Be(pA2);
        pA1.Should().BeInRange(0, partitionCount - 1);
        pB.Should().BeInRange(0, partitionCount - 1);
    }

    [Fact]
    public void GetPartitionIndex_DistributesAcrossPartitions()
    {
        int partitionCount = 8;
        var partitionsSeen = new HashSet<int>();

        for (int i = 0; i < 200; i++)
        {
            string devId = $"DEVICE-KEY-{i:D4}";
            int partition = OriginatorPartitionResolver.GetPartitionIndex(devId, partitionCount);
            partitionsSeen.Add(partition);
        }

        // 200 claves diversas deben abarcar todas o casi todas las 8 particiones
        partitionsSeen.Count.Should().Be(partitionCount);
    }

    [Theory]
    [InlineData("devices/ESP32-RELAY-01/telemetry", null, "ESP32-RELAY-01")]
    [InlineData("devices/TRACKER-102/gps", null, "TRACKER-102")]
    [InlineData("glp/telemetria/TANK-PRESSURE-09", null, "TANK-PRESSURE-09")]
    [InlineData("relay/telemetria/PUMP-SWITCH-03", null, "PUMP-SWITCH-03")]
    [InlineData("relays/devices/VALVE-44/action", null, "VALVE-44")]
    [InlineData("telemetry/WEATHER-STATION-01", null, "WEATHER-STATION-01")]
    [InlineData("custom/unusual/topic", "AUTHENTICATED-CHIP-99", "AUTHENTICATED-CHIP-99")]
    [InlineData("devices/IGNORED-CHIP/telemetry", "OVERRIDE-IMEI-868120", "OVERRIDE-IMEI-868120")]
    public void ResolveOriginatorKey_ExtractsCorrectKey_FromTopicOrTransportId(
        string topic,
        string? transportId,
        string expectedKey)
    {
        string resolved = OriginatorPartitionResolver.ResolveOriginatorKey(topic, transportId);
        resolved.Should().Be(expectedKey);
    }
}
