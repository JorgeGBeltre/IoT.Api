using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using IoT.Protocols.Ota;
using Xunit;

namespace IoT.Tests.Unit;

/// <summary>
/// Contrato de cable de la actualización OTA por MQTT, fijado contra la librería REAL del
/// dispositivo (References/Dispositivos/MQTTOTAv5). El firmware no se toca, así que estos
/// tests son la especificación que el backend debe cumplir, no una preferencia nuestra.
/// </summary>
public class OtaChunkerTests
{
    private static byte[] FakeFirmware(int sizeBytes)
    {
        var image = new byte[sizeBytes];
        // Una imagen ESP32 válida empieza por el magic 0xE9; el resto es relleno determinista.
        image[0] = OtaChunker.EspImageMagic;
        for (int i = 1; i < sizeBytes; i++) image[i] = (byte)(i % 251);
        return image;
    }

    [Fact]
    public void PublishesToTheTopicTheDeviceIsActuallySubscribedTo()
    {
        // La librería se suscribe SÓLO a "ota/<clave>", con coincidencia exacta y sin
        // comodines. El broker de referencia publicaba en "event/{chipId}", al que el
        // manejador OTA no está suscrito: su OTA nunca llegaba al dispositivo.
        OtaChunker.TopicFor("A1B2C3D4E5F6").Should().Be("ota/A1B2C3D4E5F6");
        OtaChunker.TopicFor("A1B2C3D4E5F6").Should().NotStartWith("event/");

        OtaChunker.ProgressTopicFor("GLP-001").Should().Be("ota/GLP-001/progress");
        OtaChunker.ErrorTopicFor("GLP-001").Should().Be("ota/GLP-001/error");
        OtaChunker.SuccessTopicFor("GLP-001").Should().Be("ota/GLP-001/success");
        OtaChunker.StateTopicFor("GLP-001").Should().Be("ota/GLP-001/state");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2048, 1)]
    [InlineData(2049, 2)]
    [InlineData(4096, 2)]
    [InlineData(1048576, 512)]
    public void CountsChunksInclusively(long sizeBytes, int expectedChunks)
    {
        OtaChunker.CountChunks(sizeBytes).Should().Be(expectedChunks);
    }

    [Fact]
    public void BuildsTheEnvelopeWithTheKeysTheParserPrefers()
    {
        var firmware = FakeFirmware(5000);
        string sha = OtaChunker.Sha256Hex(firmware);
        var stamp = new DateTime(2026, 10, 23, 17, 0, 0, DateTimeKind.Utc);

        string payload = OtaChunker.BuildChunkPayload(
            "1.0.4", partIndex: 1, totalParts: 3,
            chunk: firmware.AsSpan(0, 2048), sha256Hex: sha, timestampUtc: stamp);

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        // EventType: si está presente debe valer EXACTAMENTE esto o el mensaje se descarta.
        root.GetProperty("EventType").GetString().Should().Be("UpdateFirmwareDevice");

        var details = root.GetProperty("Details");
        // Primeras claves de cada lista de preferencia del parser (MQTTOTAv5.cpp:304-328).
        details.GetProperty("firmware_version").GetString().Should().Be("1.0.4");
        details.GetProperty("part_index").GetInt32().Should().Be(1);
        details.GetProperty("total_parts").GetInt32().Should().Be(3);
        details.GetProperty("base64").GetString().Should().NotBeNullOrEmpty();
        details.GetProperty("sha256").GetString().Should().Be(sha);
    }

    [Fact]
    public void ChunksFirstAndBase64Afterwards_SoEachPartDecodesOnItsOwn()
    {
        var firmware = FakeFirmware(5000);
        string sha = OtaChunker.Sha256Hex(firmware);
        int total = OtaChunker.CountChunks(firmware.Length);

        var reassembled = new List<byte>();
        for (int part = 1; part <= total; part++)
        {
            int offset = (part - 1) * OtaChunker.DefaultChunkSizeBytes;
            int length = Math.Min(OtaChunker.DefaultChunkSizeBytes, firmware.Length - offset);

            string payload = OtaChunker.BuildChunkPayload(
                "1.0.4", part, total, firmware.AsSpan(offset, length), sha, DateTime.UnixEpoch);

            using var doc = JsonDocument.Parse(payload);
            string base64 = doc.RootElement.GetProperty("Details").GetProperty("base64").GetString()!;

            // Base64 estándar RFC 4648: base64url (- y _) provoca "Base64 decode error".
            base64.Should().NotContain("-").And.NotContain("_");

            reassembled.AddRange(Convert.FromBase64String(base64));
        }

        reassembled.Should().Equal(firmware, "el dispositivo concatena los bytes decodificados en orden");
        Convert.ToHexString(SHA256.HashData(reassembled.ToArray())).ToLowerInvariant()
            .Should().Be(sha, "el sha256 del contrato es el de la imagen COMPLETA, no el de un trozo");
    }

    [Fact]
    public void TheSha256IsTheSameInEveryChunk_BecauseTheDeviceStoresItFromTheFirstOne()
    {
        var firmware = FakeFirmware(5000);
        string sha = OtaChunker.Sha256Hex(firmware);
        int total = OtaChunker.CountChunks(firmware.Length);

        var seen = new HashSet<string>();
        for (int part = 1; part <= total; part++)
        {
            int offset = (part - 1) * OtaChunker.DefaultChunkSizeBytes;
            int length = Math.Min(OtaChunker.DefaultChunkSizeBytes, firmware.Length - offset);
            string payload = OtaChunker.BuildChunkPayload("1.0.4", part, total, firmware.AsSpan(offset, length), sha, DateTime.UnixEpoch);
            using var doc = JsonDocument.Parse(payload);
            seen.Add(doc.RootElement.GetProperty("Details").GetProperty("sha256").GetString()!);
        }

        seen.Should().HaveCount(1);
        // Hex minúscula de 64 caracteres, como lo compara el equipo.
        sha.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void AFullChunkMessageFitsInTheDeviceJsonPool()
    {
        // ESTE es el límite que de verdad manda: la librería parsea con
        // StaticJsonDocument<4096> (MQTTOTAv5.h:40) y, si no cabe, DESCARTA el mensaje en
        // silencio (sólo log por serie, sin publicar error). Se mide el sobre real.
        var firmware = FakeFirmware(1048576);          // 1 MB: tamaño de imagen realista
        string sha = OtaChunker.Sha256Hex(firmware);
        int total = OtaChunker.CountChunks(firmware.Length);

        string payload = OtaChunker.BuildChunkPayload(
            "1.0.4-build.12345", partIndex: total, totalParts: total,
            chunk: firmware.AsSpan(0, OtaChunker.DefaultChunkSizeBytes), sha256Hex: sha,
            timestampUtc: DateTime.UtcNow);

        int bytes = Encoding.UTF8.GetByteCount(payload);

        // 2048 B binarios -> 2732 caracteres base64. Con el sobre completo debe quedar
        // margen suficiente por debajo de 4096 para el overhead de slots de ArduinoJson.
        bytes.Should().BeLessThan(3500,
            $"el mensaje completo ocupa {bytes} B y el pool del dispositivo es de 4096 B");
        payload.Should().Contain("\"part_index\":" + total);
    }

    [Fact]
    public void RejectsAnImageTheDeviceWouldThrowAway()
    {
        // Mejor no empezar un rollout que dejar al equipo a medias: sin reanudación, un
        // fallo a mitad obliga a volver al part_index 1.
        OtaChunker.ValidateImage(new byte[500]).Should().Contain("Firmware too small");

        var noMagic = FakeFirmware(5000);
        noMagic[0] = 0x00;
        OtaChunker.ValidateImage(noMagic).Should().Contain("Invalid firmware image header");

        OtaChunker.ValidateImage(FakeFirmware(5000)).Should().BeNull();
    }

    [Fact]
    public void RefusesToBuildAMessageTheDeviceWouldSilentlyDiscard()
    {
        var chunk = new byte[16];

        // Sin firmware_version el equipo descarta el mensaje sin publicar nada.
        Action noVersion = () => OtaChunker.BuildChunkPayload("", 1, 1, chunk, "abc", DateTime.UnixEpoch);
        noVersion.Should().Throw<ArgumentException>();

        // Índices 1-based: un 0 arrancaría la rama intermedia con el contador equivocado.
        Action zeroIndex = () => OtaChunker.BuildChunkPayload("1.0.0", 0, 1, chunk, "abc", DateTime.UnixEpoch);
        zeroIndex.Should().Throw<ArgumentOutOfRangeException>();

        Action pastTotal = () => OtaChunker.BuildChunkPayload("1.0.0", 5, 3, chunk, "abc", DateTime.UnixEpoch);
        pastTotal.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ASinglePartImageIsMarkedAsBothFirstAndLast()
    {
        // part_index == 1 arranca la OTA y part_index == total_parts la cierra: con una
        // sola parte ambas condiciones coinciden en el mismo mensaje.
        var firmware = FakeFirmware(1500);
        string payload = OtaChunker.BuildChunkPayload(
            "1.0.4", 1, 1, firmware, OtaChunker.Sha256Hex(firmware), DateTime.UnixEpoch);

        using var doc = JsonDocument.Parse(payload);
        var details = doc.RootElement.GetProperty("Details");
        details.GetProperty("part_index").GetInt32().Should().Be(1);
        details.GetProperty("total_parts").GetInt32().Should().Be(1);
    }
}
