using Aegis.Agent.Models;
using Aegis.Agent.Services;

namespace Aegis.Agent.Tests;

public sealed class TelemetryHistoryQueryTests
{
    [Fact]
    public void Query_is_bounded_and_time_sorted()
    {
        var from = DateTimeOffset.UtcNow.AddMinutes(-10);
        var to = from.AddMinutes(10);
        var points = Enumerable.Range(0, 600)
            .Select(i => Point(from.AddSeconds(i), i % 100, i % 80))
            .ToArray();

        var result = TelemetryHistoryQuery.MergeAndDownsample(
            points, Array.Empty<TelemetryHistoryPoint>(), from, to, 60);

        Assert.InRange(result.Count, 1, 60);
        Assert.True(result.Zip(result.Skip(1), (a, b) => a.Timestamp <= b.Timestamp).All(value => value));
        Assert.All(result, point => Assert.InRange(point.CpuLoadPercent!.Value, 0, 100));
    }

    [Fact]
    public void Query_prioritizes_fresh_memory_over_persisted_overlap()
    {
        var from = DateTimeOffset.UtcNow.AddMinutes(-2);
        var older = Point(from.AddSeconds(5), 10, 10);
        var duplicateFromStorage = Point(from.AddSeconds(60), 90, 90);
        var live = Point(from.AddSeconds(60), 35, 40);
        var end = Point(from.AddSeconds(90), 50, 60);

        var result = TelemetryHistoryQuery.MergeAndDownsample(
            new[] { older, duplicateFromStorage },
            new[] { live, end },
            from,
            from.AddMinutes(2),
            100);

        Assert.Equal(3, result.Count);
        Assert.Equal(10, result[0].CpuLoadPercent);
        Assert.Equal(35, result[1].CpuLoadPercent);
        Assert.Equal(50, result[2].CpuLoadPercent);
    }

    [Fact]
    public void Query_keeps_missing_sensor_values_as_null()
    {
        var from = DateTimeOffset.UtcNow.AddMinutes(-10);
        var points = Enumerable.Range(0, 100)
            .Select(i => new TelemetryHistoryPoint(
                from.AddSeconds(i), null, null, i, null, null, 0, 0))
            .ToArray();

        var result = TelemetryHistoryQuery.MergeAndDownsample(
            points, Array.Empty<TelemetryHistoryPoint>(),
            from, from.AddMinutes(2), 30);

        Assert.NotEmpty(result);
        Assert.All(result, point =>
        {
            Assert.Null(point.CpuLoadPercent);
            Assert.Null(point.CpuTemperatureC);
            Assert.Null(point.GpuLoadPercent);
            Assert.Null(point.GpuTemperatureC);
        });
    }

    private static TelemetryHistoryPoint Point(
        DateTimeOffset time, double cpu, double memory) =>
        new(time, cpu, 60, memory, 45, 50, 400, 200);
}
