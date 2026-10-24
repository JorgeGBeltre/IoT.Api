using FluentAssertions;
using IoT.Domain.Entities;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// Ciclo de vida del registro de firmware. Dos de los hallazgos más graves de la revisión
/// adversarial vivían aquí: re-subir una versión no cambiaba a qué binario apuntaba el
/// registro, y el "rollback" volvía a desplegar la MISMA versión que acababa de fallar.
/// </summary>
public class FirmwareRecordLifecycleTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static FirmwareVersionRecord NewRecord(string version, string src, string sha, int chunks = 2) =>
        FirmwareVersionRecord.Create(
            tenantId: Tenant,
            firmwareVersion: version,
            src: src,
            fileSizeBytes: 4096,
            sha256: sha,
            totalChunks: chunks).Value;

    [Fact]
    public void ReplacingTheBinaryRepointsTheRecordAndNamesThePreviousFile()
    {
        // ESTE era el fallo: re-subir una versión existente sólo la re-activaba, así que el
        // registro seguía apuntando al archivo VIEJO con su sha256 y su número de trozos. La
        // flota recibía el binario antiguo mientras la API respondía con la versión nueva, y
        // el archivo recién subido quedaba huérfano en disco.
        var record = NewRecord("1.0.0", "storage/firmware/t/1.0.0/firmware_aaaa.bin", "aaaa", chunks: 2);

        string? previous = record.ReplaceBinary(
            src: "storage/firmware/t/1.0.0/firmware_bbbb.bin",
            fileSizeBytes: 8192,
            sha256: "bbbb",
            totalChunks: 4,
            chunkSizeBytes: 2048);

        record.Src.Should().Be("storage/firmware/t/1.0.0/firmware_bbbb.bin");
        record.Sha256.Should().Be("bbbb");
        record.FileSizeBytes.Should().Be(8192);
        record.TotalChunks.Should().Be(4);
        record.FirmwareVersion.Should().Be("1.0.0", "la versión no cambia, sólo su contenido");
        previous.Should().Be("storage/firmware/t/1.0.0/firmware_aaaa.bin",
            "hay que devolver la ruta anterior para poder borrar el binario que ya no referencia nadie");
    }

    [Fact]
    public void ReplacingWithTheSameFileReportsNoPreviousToDelete()
    {
        // Mismo contenido => mismo nombre derivado del hash. No hay nada que borrar, y
        // borrarlo sería borrar el archivo que acabamos de dejar en su sitio.
        const string src = "storage/firmware/t/1.0.0/firmware_aaaa.bin";
        var record = NewRecord("1.0.0", src, "aaaa");

        record.ReplaceBinary(src, 4096, "aaaa", 2, 2048).Should().BeNull();
        record.Src.Should().Be(src);
    }

    [Fact]
    public void ReplacingTheBinaryReactivatesAndUndeletes()
    {
        var record = NewRecord("1.0.0", "a.bin", "aaaa");
        record.MarkAsDeleted();
        record.IsDeleted.Should().BeTrue();
        record.IsActive.Should().BeFalse();

        record.ReplaceBinary("b.bin", 4096, "bbbb", 2, 2048);

        record.IsDeleted.Should().BeFalse("volver a subir la versión la resucita");
        record.IsActive.Should().BeTrue();
    }

    [Fact]
    public void IsActiveIsNotAMarkerOfWhatIsDeployed()
    {
        // Fijado a propósito: Create deja IsActive=true y nada lo apaga, así que IsActive NO
        // sirve para saber qué versión está desplegada. El rollback usaba
        // `records.FirstOrDefault(r => r.IsActive)` sobre una lista ordenada por fecha
        // descendente y por eso re-desplegaba la versión que acababa de fallar.
        var vieja = NewRecord("1.0.0", "a.bin", "aaaa");
        var nueva = NewRecord("1.1.0", "b.bin", "bbbb");

        vieja.IsActive.Should().BeTrue();
        nueva.IsActive.Should().BeTrue();

        // Seleccionar "la anterior" exige EXCLUIR la versión actual del equipo, no mirar IsActive.
        var ordenadas = new[] { nueva, vieja }; // CreatedAt descendente, como devuelve el repo
        const string instalada = "1.1.0";

        var destino = ordenadas.FirstOrDefault(r =>
            !string.Equals(r.FirmwareVersion, instalada, StringComparison.OrdinalIgnoreCase));

        destino.Should().NotBeNull();
        destino!.FirmwareVersion.Should().Be("1.0.0", "el rollback debe ir a la anterior, no a la que falló");
        ordenadas.FirstOrDefault(r => r.IsActive)!.FirmwareVersion
            .Should().Be("1.1.0", "lo que hacía antes: devolvía la que acababa de romperse");
    }
}
