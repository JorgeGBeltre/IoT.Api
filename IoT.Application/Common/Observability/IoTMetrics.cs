using System.Diagnostics.Metrics;

namespace IoT.Application.Common.Observability;

/// <summary>
/// Centralized enterprise telemetry metrics instrumented via System.Diagnostics.Metrics (OpenTelemetry compatible).
/// </summary>
public static class IoTMetrics
{
    public const string MeterName = "IoT.Platform";
    private static readonly Meter Meter = new(MeterName, "1.0.0");

    // Ingestion Counters & Latency
    public static readonly Counter<long> TelemetryReceivedCounter = Meter.CreateCounter<long>(
        "iot_telemetry_messages_received_total",
        description: "Total telemetry messages received across all transport families");

    public static readonly Counter<long> TelemetryDeduplicatedCounter = Meter.CreateCounter<long>(
        "iot_telemetry_messages_deduplicated_total",
        description: "Total telemetry messages discarded by the SHA-256 deduplication filter");

    public static readonly Histogram<double> TelemetryIngestionDuration = Meter.CreateHistogram<double>(
        "iot_telemetry_ingestion_duration_ms",
        unit: "ms",
        description: "Latency of telemetry ingestion processing in milliseconds");

    // Command Lifecycle Counters & Latency
    public static readonly Counter<long> CommandsDispatchedCounter = Meter.CreateCounter<long>(
        "iot_device_commands_dispatched_total",
        description: "Total device commands dispatched via MQTT publisher");

    public static readonly Counter<long> CommandsConfirmedCounter = Meter.CreateCounter<long>(
        "iot_device_commands_confirmed_total",
        description: "Total device commands confirmed via semantic ACK");

    public static readonly Histogram<double> CommandLifecycleDuration = Meter.CreateHistogram<double>(
        "iot_command_lifecycle_duration_ms",
        unit: "ms",
        description: "Total duration from command dispatch to device confirmation in milliseconds");
}
