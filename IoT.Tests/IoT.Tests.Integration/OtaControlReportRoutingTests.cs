using FluentAssertions;
using IoT.Infrastructure.Mqtt;
using Xunit;

namespace IoT.Tests.Integration;

/// <summary>
/// MEDIUM-15: los reportes que CIERRAN una campaña (ota/&lt;id&gt;/success|error) van por un
/// canal de prioridad aparte, porque cierran la OTA, viajan en QoS 0 (sin reintento) y no deben
/// perderse cuando el canal de telemetría de volumen se llena. El resto de tráfico (progreso,
/// estado, telemetría) va por el canal normal: perder uno sólo omite una actualización de UI.
///
/// Esto fija qué tópicos entran al canal de prioridad —la parte sutil del arreglo—: ampliar o
/// estrechar ese conjunto por error reabriría la pérdida del reporte de cierre o saturaría la
/// prioridad con tráfico de volumen.
/// </summary>
public sealed class OtaControlReportRoutingTests
{
    [Theory]
    [InlineData("ota/A1B2C3D4E5F6/success")]
    [InlineData("ota/A1B2C3D4E5F6/error")]
    [InlineData("OTA/A1B2C3D4E5F6/SUCCESS")] // insensible a mayúsculas
    public void ClosingReportsGoToThePriorityChannel(string topic)
    {
        EmbeddedMqttBroker.IsOtaClosingReport(topic).Should().BeTrue();
    }

    [Theory]
    [InlineData("ota/A1B2C3D4E5F6/progress")]  // sólo actualiza la UI: no es de cierre
    [InlineData("ota/A1B2C3D4E5F6/state")]
    [InlineData("ota/A1B2C3D4E5F6")]           // sin sufijo
    [InlineData("telemetry")]
    [InlineData("devices/A1B2C3D4E5F6/telemetry")]
    [InlineData("glp/telemetria/GLP-001")]
    [InlineData("success")]                    // "success" suelto no es un reporte OTA
    [InlineData("event/A1B2C3D4E5F6/success")] // no es del prefijo ota/
    public void EverythingElseGoesToTheVolumeChannel(string topic)
    {
        EmbeddedMqttBroker.IsOtaClosingReport(topic).Should().BeFalse();
    }
}
