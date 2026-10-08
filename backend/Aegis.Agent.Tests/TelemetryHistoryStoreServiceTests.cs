using Aegis.Agent.Configuration;
using Aegis.Agent.Infrastructure;
using Aegis.Agent.Models;
using Aegis.Agent.Services;
using Microsoft.Extensions.Options;

namespace Aegis.Agent.Tests;

public sealed class TelemetryHistoryStoreServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aegis-telemetry-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Store_round_trips_points_in_time_order()
    {
        var options = Options.Create(new AgentOptions { DataDirectory = _directory });
        var database = new StateDatabase(options);
        var store = new TelemetryHistoryStoreService(database);

        var now = DateTimeOffset.UtcNow;
        await store.AppendAsync(Point(now.AddSeconds(-10), 25, 40));
        await store.AppendAsync(Point(now.AddSeconds(-5), 50, 60));

        var points = await store.ReadAsync(now.AddMinutes(-1), now.AddMinutes(1));

        Assert.Equal(2, points.Count);
        Assert.Equal(25, points[0].CpuLoadPercent);
        Assert.Equal(50, points[1].CpuLoadPercent);
        Assert.True(points[0].Timestamp < points[1].Timestamp);
        Assert.Equal(2, await store.CountAsync());
    }

    [Fact]
    public async Task Store_filters_points_outside_requested_range()
    {
        var options = Options.Create(new AgentOptions { DataDirectory = _directory });
        var database = new StateDatabase(options);
        var store = new TelemetryHistoryStoreService(database);

        var now = DateTimeOffset.UtcNow;
        await store.AppendAsync(Point(now.AddHours(-2), 10, 20));
        await store.AppendAsync(Point(now.AddMinutes(-2), 70, 80));

        var points = await store.ReadAsync(now.AddMinutes(-10), now.AddMinutes(1));

        var point = Assert.Single(points);
        Assert.Equal(70, point.CpuLoadPercent);
    }

    private static TelemetryHistoryPoint Point(
        DateTimeOffset timestamp,
        double cpu,
        double memory) =>
        new(
            timestamp,
            cpu,
            55,
            memory,
            35,
            60,
            1024,
            512);

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }
}
