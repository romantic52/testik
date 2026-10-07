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

        return new StateMaintenanceResult(
            DateTimeOffset.UtcNow,
            removedByAge,
            removedByCount,
            remaining,
            _options.SafeAuditRetentionDays,
            _options.SafeAuditMaxRows);
    }

    private async Task RunSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunOnceAsync(cancellationToken);

            if (result.RemovedTotal > 0)
            {
                _logger.LogInformation(
                    "AEGIS SQLite maintenance removed {Removed} audit rows; {Remaining} remain",
                    result.RemovedTotal,
                    result.RemainingAuditRows);
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
    int MaxRows)
{
    public int RemovedTotal => RemovedByAge + RemovedByCount;
}
