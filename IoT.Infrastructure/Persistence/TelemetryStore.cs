using System.Text.Json;
using IoT.Application.Common.Interfaces;
using IoT.Domain.ValueObjects;
using IoT.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace IoT.Infrastructure.Persistence;

public sealed class TelemetryStore : ITelemetryStore
{
    private readonly ApplicationDbContext _context;

    public TelemetryStore(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task StorePositionAsync(
        Guid tenantId,
        Guid deviceId,
        DateTime recordedAt,
        DateTime receivedAt,
        GeoPosition position,
        long version,
        string source = "real",
        string? messageId = null,
        CancellationToken cancellationToken = default)
    {
        var geom = new Point(position.Longitude, position.Latitude) { SRID = 4326 };

        var entity = new PositionEntity
        {
            TenantId = tenantId,
            DeviceId = deviceId,
            RecordedAt = recordedAt,
            ReceivedAt = receivedAt,
            Location = geom,
            Latitude = position.Latitude,
            Longitude = position.Longitude,
            SpeedKph = position.SpeedKph,
            Heading = position.Heading,
            AltitudeM = position.AltitudeM,
            Satellites = position.Satellites,
            IsValid = position.IsValid,
            Version = version,
            Source = source,
            MessageId = messageId
        };

        await _context.Positions.AddAsync(entity, cancellationToken);
    }

    public async Task StoreTelemetryMetricsAsync(
        Guid tenantId,
        Guid deviceId,
        DateTime recordedAt,
        DateTime receivedAt,
        Dictionary<string, double> metrics,
        string? rawPayload,
        string source = "real",
        string? messageId = null,
        CancellationToken cancellationToken = default)
    {
        var entity = new TelemetryRecordEntity
        {
            TenantId = tenantId,
            DeviceId = deviceId,
            RecordedAt = recordedAt,
            ReceivedAt = receivedAt,
            MetricsJson = JsonSerializer.Serialize(metrics),
            // La columna es jsonb: una trama que no sea JSON (texto plano del relé, ASCII
            // de los trackers, hex de los binarios) hacía fallar el INSERT y se perdía la
            // telemetría ya decodificada. Ver JsonbText.
            RawPayload = JsonbText.Normalize(rawPayload),
            Source = source,
            MessageId = messageId
        };

        await _context.TelemetryRecords.AddAsync(entity, cancellationToken);
    }


    public async Task<IReadOnlyList<HistoricalPositionDto>> GetPositionsAsync(
        Guid tenantId,
        Guid deviceId,
        DateTime fromUtc,
        DateTime toUtc,
        int limit = 1000,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Positions
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.DeviceId == deviceId && p.RecordedAt >= fromUtc && p.RecordedAt <= toUtc);

        if (!string.IsNullOrWhiteSpace(source))
        {
            query = query.Where(p => p.Source == source);
        }

        var entities = await query
            .OrderBy(p => p.RecordedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return entities.Select(e => new HistoricalPositionDto(
            e.RecordedAt,
            e.Latitude,
            e.Longitude,
            e.SpeedKph,
            e.Heading,
            e.AltitudeM,
            e.Satellites,
            e.IsValid,
            e.Source,
            e.MessageId
        )).ToList();
    }

    public async Task<IReadOnlyList<HistoricalTelemetryDto>> GetTelemetryAsync(
        Guid tenantId,
        Guid deviceId,
        DateTime fromUtc,
        DateTime toUtc,
        int limit = 1000,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        var query = _context.TelemetryRecords
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.DeviceId == deviceId && t.RecordedAt >= fromUtc && t.RecordedAt <= toUtc);

        if (!string.IsNullOrWhiteSpace(source))
        {
            query = query.Where(t => t.Source == source);
        }

        var entities = await query
            .OrderBy(t => t.RecordedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var list = new List<HistoricalTelemetryDto>(entities.Count);
        foreach (var e in entities)
        {
            var dict = new Dictionary<string, double>();
            if (!string.IsNullOrWhiteSpace(e.MetricsJson))
            {
                try
                {
                    dict = JsonSerializer.Deserialize<Dictionary<string, double>>(e.MetricsJson) ?? new();
                }
                catch
                {
                    // Ignore malformed individual JSON
                }
            }
            list.Add(new HistoricalTelemetryDto(e.RecordedAt, dict, e.RawPayload, e.Source, e.MessageId));
        }

        return list;
    }

    public async Task<int> PurgePositionsOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.Positions
                .Where(p => p.RecordedAt < cutoffUtc)
                .ExecuteDeleteAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Fallback for InMemory provider in test environments
            var expired = await _context.Positions
                .Where(p => p.RecordedAt < cutoffUtc)
                .ToListAsync(cancellationToken);

            if (expired.Count > 0)
            {
                _context.Positions.RemoveRange(expired);
                return await _context.SaveChangesAsync(cancellationToken);
            }

            return 0;
        }
    }

    public async Task<int> PurgeTelemetryRecordsOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.TelemetryRecords
                .Where(t => t.RecordedAt < cutoffUtc)
                .ExecuteDeleteAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Fallback for InMemory provider in test environments
            var expired = await _context.TelemetryRecords
                .Where(t => t.RecordedAt < cutoffUtc)
                .ToListAsync(cancellationToken);

            if (expired.Count > 0)
            {
                _context.TelemetryRecords.RemoveRange(expired);
                return await _context.SaveChangesAsync(cancellationToken);
            }

            return 0;
        }
    }
}
