using Aegis.Agent.Infrastructure;
using Aegis.Agent.Models;
using Microsoft.Data.Sqlite;

namespace Aegis.Agent.Services;

public sealed class TelemetryHistoryStoreService
{
    private readonly StateDatabase _database;

    public TelemetryHistoryStoreService(StateDatabase database)
    {
        _database = database;
    }

    public async Task AppendAsync(
        TelemetryHistoryPoint point,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO telemetry_history (
                timestamp,
                cpu_load_percent,
                cpu_temperature_c,
                memory_load_percent,
                gpu_load_percent,
                gpu_temperature_c,
                receive_bytes_per_second,
                send_bytes_per_second
            )
            VALUES (
                $timestamp,
                $cpuLoad,
                $cpuTemp,
                $memoryLoad,
                $gpuLoad,
                $gpuTemp,
                $rx,
                $tx
            );
            """;

        command.Parameters.AddWithValue("$timestamp", point.Timestamp.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$cpuLoad", DbValue(point.CpuLoadPercent));
        command.Parameters.AddWithValue("$cpuTemp", DbValue(point.CpuTemperatureC));
        command.Parameters.AddWithValue("$memoryLoad", DbValue(point.MemoryLoadPercent));
        command.Parameters.AddWithValue("$gpuLoad", DbValue(point.GpuLoadPercent));
        command.Parameters.AddWithValue("$gpuTemp", DbValue(point.GpuTemperatureC));
        command.Parameters.AddWithValue("$rx", point.ReceiveBytesPerSecond);
        command.Parameters.AddWithValue("$tx", point.SendBytesPerSecond);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TelemetryHistoryPoint>> ReadAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int limit = 20_000,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100_000);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT
                timestamp,
                cpu_load_percent,
                cpu_temperature_c,
                memory_load_percent,
                gpu_load_percent,
                gpu_temperature_c,
                receive_bytes_per_second,
                send_bytes_per_second
            FROM telemetry_history
            WHERE timestamp >= $from
              AND timestamp <= $to
            ORDER BY timestamp ASC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$from", from.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$to", to.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<TelemetryHistoryPoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TelemetryHistoryPoint(
                ParseTimestamp(reader.GetString(0)),
                ReadNullableDouble(reader, 1),
                ReadNullableDouble(reader, 2),
                ReadNullableDouble(reader, 3),
                ReadNullableDouble(reader, 4),
                ReadNullableDouble(reader, 5),
                reader.GetDouble(6),
                reader.GetDouble(7)));
        }

        return result;
    }

    public async Task<long> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM telemetry_history;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static object DbValue(double? value) =>
        value.HasValue ? value.Value : DBNull.Value;

    private static double? ReadNullableDouble(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
}
