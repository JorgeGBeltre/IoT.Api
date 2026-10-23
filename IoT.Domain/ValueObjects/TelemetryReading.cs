using IoT.Domain.Common;

namespace IoT.Domain.ValueObjects;

public sealed record TelemetryReading
{
    public string MetricName { get; }
    public double Value { get; }
    public string Unit { get; }
    public DateTime Timestamp { get; }
    public string? Quality { get; }

    private TelemetryReading(string metricName, double value, string unit, DateTime timestamp, string? quality)
    {
        MetricName = metricName;
        Value = value;
        Unit = unit;
        Timestamp = timestamp;
        Quality = quality;
    }

    public static Result<TelemetryReading> Create(string metricName, double value, string unit, DateTime timestamp, string? quality = "Good")
    {
        if (string.IsNullOrWhiteSpace(metricName))
            return Result<TelemetryReading>.Failure(new Error("TelemetryReading.InvalidMetricName", "Metric name is required."));

        if (double.IsNaN(value) || double.IsInfinity(value))
            return Result<TelemetryReading>.Failure(new Error("TelemetryReading.InvalidValue", "Value must be a valid real number."));

        return new TelemetryReading(metricName.Trim(), value, unit?.Trim() ?? string.Empty, timestamp, quality);
    }
}
