using Aegis.Agent.Configuration;
using Aegis.Agent.Infrastructure;
using Microsoft.Extensions.Options;

namespace Aegis.Agent.Services;

public sealed class StateMaintenanceService : BackgroundService
{
    private readonly StateDatabase _database;
    private readonly AgentOptions _options;
    private readonly ILogger<StateMaintenanceService> _logger;

    public StateMaintenanceService(
        StateDatabase database,
        IOptions<AgentOptions> options,
        ILogger<StateMaintenanceService> logger)
    {
        _database = database;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunSafeAsync(stoppingToken);

        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(_options.SafeStateMaintenanceMinutes));

        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunSafeAsync(stoppingToken);
    }

    public async Task<StateMaintenanceResult> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow
            .AddDays(-_options.SafeAuditRetentionDays)
            .ToString("O");
        var telemetryCutoff = DateTimeOffset.UtcNow
            .AddHours(-_options.SafeTelemetryRetentionHours)
            .ToString("O");

        await using var connection = _database.OpenConnection();
        await using var transaction =
            (Microsoft.Data.Sqlite.SqliteTransaction)connection.BeginTransaction();

        int removedByAge;
        await using (var byAge = connection.CreateCommand())
        {
            byAge.Transaction = transaction;
            byAge.CommandText = "DELETE FROM audit WHERE timestamp < $cutoff;";
            byAge.Parameters.AddWithValue("$cutoff", cutoff);
            removedByAge = await byAge.ExecuteNonQueryAsync(cancellationToken);
        }

        int removedByCount;
        await using (var byCount = connection.CreateCommand())
        {
            byCount.Transaction = transaction;
            byCount.CommandText = """
                DELETE FROM audit
                WHERE id NOT IN (
                    SELECT id
                    FROM audit
                    ORDER BY id DESC
                    LIMIT $maxRows
                );
                """;
            byCount.Parameters.AddWithValue("$maxRows", _options.SafeAuditMaxRows);
            removedByCount = await byCount.ExecuteNonQueryAsync(cancellationToken);
        }

        int telemetryRemovedByAge;
        await using (var telemetryByAge = connection.CreateCommand())
        {
            telemetryByAge.Transaction = transaction;
            telemetryByAge.CommandText = "DELETE FROM telemetry_history WHERE timestamp < $cutoff;";
            telemetryByAge.Parameters.AddWithValue("$cutoff", telemetryCutoff);
            telemetryRemovedByAge = await telemetryByAge.ExecuteNonQueryAsync(cancellationToken);
        }

        int telemetryRemovedByCount;
        await using (var telemetryByCount = connection.CreateCommand())
        {
            telemetryByCount.Transaction = transaction;
            telemetryByCount.CommandText = """
                DELETE FROM telemetry_history
                WHERE id NOT IN (
                    SELECT id
                    FROM telemetry_history
                    ORDER BY id DESC
                    LIMIT $maxRows
                );
                """;
            telemetryByCount.Parameters.AddWithValue("$maxRows", _options.SafeTelemetryMaxRows);
            telemetryRemovedByCount = await telemetryByCount.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        await using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            await checkpoint.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM audit;";
        var remaining = Convert.ToInt64(
            await count.ExecuteScalarAsync(cancellationToken));

        await using var telemetryCount = connection.CreateCommand();
        telemetryCount.CommandText = "SELECT COUNT(*) FROM telemetry_history;";
        var remainingTelemetry = Convert.ToInt64(
            await telemetryCount.ExecuteScalarAsync(cancellationToken));

        return new StateMaintenanceResult(
            DateTimeOffset.UtcNow,
            removedByAge,
            removedByCount,
            remaining,
            _options.SafeAuditRetentionDays,
            _options.SafeAuditMaxRows,
            telemetryRemovedByAge,
            telemetryRemovedByCount,
            remainingTelemetry,
            _options.SafeTelemetryRetentionHours,
            _options.SafeTelemetryMaxRows);
    }

    private async Task RunSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunOnceAsync(cancellationToken);

            if (result.RemovedTotal > 0)
            {
                _logger.LogInformation(
                    "AEGIS SQLite maintenance removed {AuditRemoved} audit rows and {TelemetryRemoved} telemetry rows; {AuditRemaining} audit / {TelemetryRemaining} telemetry remain",
                    result.RemovedTotal,
                    result.TelemetryRemovedTotal,
                    result.RemainingAuditRows,
                    result.RemainingTelemetryRows);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "AEGIS SQLite maintenance failed");
        }
    }
}

public sealed record StateMaintenanceResult(
    DateTimeOffset Timestamp,
    int RemovedByAge,
    int RemovedByCount,
    long RemainingAuditRows,
    int RetentionDays,
    int MaxRows,
    int TelemetryRemovedByAge,
    int TelemetryRemovedByCount,
    long RemainingTelemetryRows,
    int TelemetryRetentionHours,
    int TelemetryMaxRows)
{
    public int RemovedTotal => RemovedByAge + RemovedByCount;
    public int TelemetryRemovedTotal => TelemetryRemovedByAge + TelemetryRemovedByCount;
}
