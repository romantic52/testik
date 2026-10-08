using Aegis.Agent.Models;

namespace Aegis.Agent.Services;

/// <summary>
/// Keeps a stable, bounded time-series response while preserving missing sensor values.
/// This is a chart aggregation, not an alerting input: rules always see full live samples.
/// </summary>
public static class TelemetryHistoryQuery
{
    public static IReadOnlyList<TelemetryHistoryPoint> MergeAndDownsample(
        IEnumerable<TelemetryHistoryPoint> persisted,
        IReadOnlyList<TelemetryHistoryPoint> live,
        DateTimeOffset from,
        DateTimeOffset to,
        int maxPoints)
    {
        maxPoints = Math.Clamp(maxPoints, 30, 2_000);
        var firstLive = live.Count > 0 ? live.Min(p => p.Timestamp) : (DateTimeOffset?)null;
        var merged = persisted
            .Where(p => !firstLive.HasValue || p.Timestamp < firstLive.Value)
            .Concat(live)
            .Where(p => p.Timestamp >= from && p.Timestamp <= to)
            .OrderBy(p => p.Timestamp)
            .ToArray();

        if (merged.Length <= maxPoints)
            return merged;

        var windowMs = Math.Max(1L, (long)Math.Ceiling((to - from).TotalMilliseconds));
        var bucketMs = Math.Max(1L, (long)Math.Ceiling(windowMs / (double)maxPoints));

        return merged
            .GroupBy(point => Math.Min(
                maxPoints - 1,
                Math.Max(0L, (long)(point.Timestamp - from).TotalMilliseconds / bucketMs)))
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var points = group.ToArray();
                return new TelemetryHistoryPoint(
                    points[^1].Timestamp,
                    Average(points.Select(p => p.CpuLoadPercent)),
                    Average(points.Select(p => p.CpuTemperatureC)),
                    Average(points.Select(p => p.MemoryLoadPercent)),
                    Average(points.Select(p => p.GpuLoadPercent)),
                    Average(points.Select(p => p.GpuTemperatureC)),
                    points.Average(p => p.ReceiveBytesPerSecond),
                    points.Average(p => p.SendBytesPerSecond));
            })
            .ToArray();
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var available = values.Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        return available.Length > 0 ? available.Average() : null;
    }
}
