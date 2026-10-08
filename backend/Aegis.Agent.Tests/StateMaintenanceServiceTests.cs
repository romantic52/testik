using Aegis.Agent.Configuration;
using Aegis.Agent.Infrastructure;
using Aegis.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aegis.Agent.Tests;

public sealed class StateMaintenanceServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aegis-maintenance-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Maintenance_removes_old_audit_rows_without_touching_incidents()
    {
        var options = Options.Create(new AgentOptions
        {
            DataDirectory = _directory,
            AuditRetentionDays = 30,
            AuditMaxRows = 100
        });

        var database = new StateDatabase(options);
        var incidents = new IncidentStoreService(database);

        await incidents.UpsertAsync(new IncidentRecord(
            "INC-MAINT-1",
            "Keep incident",
            "unit-test",
            "Средний",
            "Новый",
            "",
            Array.Empty<string>(),
            false,
            false,
            null,
            DateTimeOffset.UtcNow));

        await incidents.AppendAuditAsync(new AuditRecord(
            DateTimeOffset.UtcNow.AddDays(-90),
            "old.audit",
            "old",
            "should be pruned",
            "unit-test"));

        var maintenance = new StateMaintenanceService(
            database,
            options,
            NullLogger<StateMaintenanceService>.Instance);

        var result = await maintenance.RunOnceAsync();

        Assert.True(result.RemovedByAge >= 1);
        Assert.DoesNotContain(
            await incidents.AuditAsync(500),
            item => item.Type == "old.audit");
        Assert.Single(await incidents.ListAsync());
    }

    [Fact]
    public async Task Maintenance_caps_audit_row_count()
    {
        var options = Options.Create(new AgentOptions
        {
            DataDirectory = _directory,
            AuditRetentionDays = 3650,
            AuditMaxRows = 100
        });

        var database = new StateDatabase(options);
        var incidents = new IncidentStoreService(database);

        for (var index = 0; index < 125; index++)
        {
            await incidents.AppendAuditAsync(new AuditRecord(
                DateTimeOffset.UtcNow.AddSeconds(index),
                "count.test",
                index.ToString(),
                null,
                "unit-test"));
        }

        var maintenance = new StateMaintenanceService(
            database,
            options,
            NullLogger<StateMaintenanceService>.Instance);

        var result = await maintenance.RunOnceAsync();

        Assert.Equal(100, result.RemainingAuditRows);
        Assert.Equal(25, result.RemovedByCount);
    }


    [Fact]
    public async Task Maintenance_prunes_telemetry_by_retention()
    {
        var options = Options.Create(new AgentOptions
        {
            DataDirectory = _directory,
            AuditRetentionDays = 3650,
            AuditMaxRows = 1000,
            TelemetryRetentionHours = 1,
            TelemetryMaxRows = 1000
        });

        var database = new StateDatabase(options);
        var store = new TelemetryHistoryStoreService(database);

        await store.AppendAsync(new Aegis.Agent.Models.TelemetryHistoryPoint(
            DateTimeOffset.UtcNow.AddHours(-3),
            10, 50, 20, 30, 60, 100, 50));

        await store.AppendAsync(new Aegis.Agent.Models.TelemetryHistoryPoint(
            DateTimeOffset.UtcNow.AddMinutes(-10),
            40, 60, 50, 70, 65, 200, 100));

        var maintenance = new StateMaintenanceService(
            database,
            options,
            NullLogger<StateMaintenanceService>.Instance);

        var result = await maintenance.RunOnceAsync();

        Assert.Equal(1, result.TelemetryRemovedByAge);
        Assert.Equal(1, result.RemainingTelemetryRows);
        Assert.Single(await store.ReadAsync(
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddMinutes(1)));
    }

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
