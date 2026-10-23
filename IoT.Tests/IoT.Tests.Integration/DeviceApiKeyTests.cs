using FluentAssertions;
using IoT.Domain.Entities;
using IoT.Infrastructure.Security;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// Credencial MQTT por dispositivo: generación/verificación de ApiKey y
/// asignación en el agregado Device.
/// </summary>
public sealed class DeviceApiKeyTests
{
    [Fact]
    public void Generate_ProducesVerifiableKeyWithFingerprint()
    {
        var svc = new DeviceApiKeyService();

        var (plain, hash, fingerprint) = svc.Generate();

        plain.Should().StartWith("iotk_");
        hash.Should().HaveLength(64); // SHA-256 hex
        fingerprint.Should().HaveLength(8);
        hash.Should().StartWith(fingerprint); // la huella son los primeros 8 hex del hash
        svc.Verify(plain, hash).Should().BeTrue();
    }

    [Fact]
    public void Verify_RejectsWrongKey()
    {
        var svc = new DeviceApiKeyService();
        var (_, hash, _) = svc.Generate();

        svc.Verify("iotk_clave-incorrecta", hash).Should().BeFalse();
        svc.Verify("", hash).Should().BeFalse();
    }

    [Fact]
    public void Generate_ProducesDistinctKeysEachTime()
    {
        var svc = new DeviceApiKeyService();

        var (p1, h1, _) = svc.Generate();
        var (p2, h2, _) = svc.Generate();

        p1.Should().NotBe(p2);
        h1.Should().NotBe(h2);
    }

    [Fact]
    public void Device_SetMqttApiKey_StoresFingerprintIssuerAndTimestamp()
    {
        var device = Device.Create(Guid.NewGuid(), "ESP-DEV-01", "AA:BB:CC:DD:EE:FF", "Unidad 01", "Generic", "Generic").Value;
        var svc = new DeviceApiKeyService();
        var (_, hash, fingerprint) = svc.Generate();

        device.HasMqttApiKey.Should().BeFalse();

        var result = device.SetMqttApiKey(hash, fingerprint, "admin@example.do");

        result.IsSuccess.Should().BeTrue();
        device.HasMqttApiKey.Should().BeTrue();
        device.MqttApiKeyHash.Should().Be(hash);
        device.MqttApiKeyFingerprint.Should().Be(fingerprint);
        device.MqttApiKeyIssuedBy.Should().Be("admin@example.do");
        device.MqttApiKeyIssuedAt.Should().NotBeNull();
    }

    [Fact]
    public void Device_SetMqttApiKey_RejectsEmptyHash()
    {
        var device = Device.Create(Guid.NewGuid(), "ESP-DEV-02", "AA:BB:CC:DD:EE:01", "Unidad 02", "Generic", "Generic").Value;

        device.SetMqttApiKey("", "abcd1234", null).IsFailure.Should().BeTrue();
    }
}
