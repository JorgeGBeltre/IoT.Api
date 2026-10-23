using System.Text.Json;
using FluentAssertions;
using IoT.Infrastructure.Persistence;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// La columna telemetry_records.RawPayload es <c>jsonb</c>. Postgres rechaza con
/// <c>22P02: invalid input syntax for type json</c> todo lo que no sea un documento JSON
/// válido, y muchos dispositivos no envían JSON: la telemetría se decodificaba bien y se
/// perdía al guardarla. Estos tests fijan la invariante: lo que se escriba en esa columna
/// SIEMPRE debe ser JSON parseable.
/// </summary>
public class RawPayloadPersistenceTests
{
    [Theory]
    // Estado del relé en texto plano (Relay_Wifi_Esp32/TelemetryService.cpp:42-45).
    [InlineData("on")]
    [InlineData("off")]
    // Trama ASCII de un tracker Coban/TK103.
    [InlineData("imei:353451044508750,tracker,0809231929,,F,112909.397,A,2234.4669,N,11354.3287,E,0.11,0;")]
    // Latido Coban: el IMEI desnudo.
    [InlineData("359586015829802")]
    // Volcado hexadecimal de una trama binaria GT06.
    [InlineData("78780D01086471700328820200018CDD0D0A")]
    // Trama Queclink @Track.
    [InlineData("+RESP:GTFRI,060100,135790246811220,,0,0,1,1,4.3,92,70.0,121.354335,31.222073,20090214013254,0460,0000,18d8,6141,00,2000.0,,,,100,210100,,,,20090214093254,11F0$")]
    // JSON mal formado: tampoco debe tumbar el INSERT.
    [InlineData("{\"roto\": ")]
    public void RawPayload_IsAlwaysValidJson_WhateverTheDeviceSent(string deviceFrame)
    {
        var normalized = JsonbText.Normalize(deviceFrame);

        normalized.Should().NotBeNull();
        // La aserción clave: Postgres debe poder parsearlo como jsonb.
        Action parse = () => JsonDocument.Parse(normalized!).Dispose();
        parse.Should().NotThrow($"'{deviceFrame}' debe quedar como JSON válido en la columna jsonb");
    }

    [Fact]
    public void RawPayload_KeepsTheOriginalDocument_WhenTheDeviceDoesSendJson()
    {
        const string json = """{"Device":{"ChipId":"A1"},"Details":{"pressure_bar":7.4}}""";

        JsonbText.Normalize(json).Should().Be(json, "una trama JSON se guarda tal cual, consultable con operadores jsonb");
    }

    [Fact]
    public void RawPayload_PreservesThePlainTextContentInsideTheJsonString()
    {
        const string frame = "imei:353451044508750,tracker,0809231929;";

        var normalized = JsonbText.Normalize(frame);

        using var doc = JsonDocument.Parse(normalized!);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.String);
        doc.RootElement.GetString().Should().Be(frame, "no se pierde ni un byte de la trama original");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RawPayload_IsNullWhenThereIsNothingToStore(string? empty)
    {
        JsonbText.Normalize(empty).Should().BeNull();
    }
}
