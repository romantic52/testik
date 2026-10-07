using System.Text.Json;
using Aegis.Agent.Infrastructure;

namespace Aegis.Agent.Services;

public sealed class AlertSettingsService
{
    private const string SettingsKey = "alerts";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StateDatabase _database;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private AlertSettings? _cached;

    public AlertSettingsService(StateDatabase database)
    {
        _database = database;
        MigrateLegacySettings();
    }

    public async Task<AlertSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
            return _cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null)
                return _cached;

            await using var connection = _database.OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT value_json
                FROM settings
                WHERE key = $key
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$key", SettingsKey);

            var raw = (await command.ExecuteScalarAsync(cancellationToken))?.ToString();
            if (string.IsNullOrWhiteSpace(raw))
                return _cached = AlertSettings.Default;

            try
            {
                var value = JsonSerializer.Deserialize<AlertSettings>(raw, _json);
                return _cached = Normalize(value ?? AlertSettings.Default);
            }
            catch (JsonException)
            {
                return _cached = AlertSettings.Default;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AlertSettings> SaveAsync(
        AlertSettings settings,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(settings);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = _database.OpenConnection();
            await using var transaction = connection.BeginTransaction();

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO settings(key, value_json, updated_at)
                    VALUES ($key, $value, $updatedAt)
                    ON CONFLICT(key) DO UPDATE SET
                        value_json = excluded.value_json,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$key", SettingsKey);
                command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(normalized, _json));
                command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            _cached = normalized;
            return normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void MigrateLegacySettings()
    {
        var legacyPath = Path.Combine(_database.DataDirectory, "alert-settings.json");
        if (!File.Exists(legacyPath))
            return;

        try
        {
            using var connection = _database.OpenConnection();

            using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT 1 FROM settings WHERE key = $key LIMIT 1;";
            exists.Parameters.AddWithValue("$key", SettingsKey);

            if (exists.ExecuteScalar() is not null)
            {
                MoveLegacy(legacyPath, ".migrated");
                return;
            }

            var parsed = JsonSerializer.Deserialize<AlertSettings>(
                File.ReadAllText(legacyPath),
                _json);

            var normalized = Normalize(parsed ?? AlertSettings.Default);

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO settings(key, value_json, updated_at)
                VALUES ($key, $value, $updatedAt)
                ON CONFLICT(key) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$key", SettingsKey);
            command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(normalized, _json));
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();

            MoveLegacy(legacyPath, ".migrated");
        }
        catch (JsonException)
        {
            MoveLegacy(
                legacyPath,
                $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}");
        }
    }

    private static void MoveLegacy(string path, string suffix)
    {
        try
        {
            var destination = path + suffix;
            if (File.Exists(destination))
                destination += "-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");

            File.Move(path, destination);
        }
        catch
        {
            // State already lives in SQLite; a legacy rename failure is non-fatal.
        }
    }

    private static AlertSettings Normalize(AlertSettings value) => value with
    {
        CpuLoad = Math.Clamp(value.CpuLoad, 50, 100),
        CpuTemp = Math.Clamp(value.CpuTemp, 50, 120),
        GpuTemp = Math.Clamp(value.GpuTemp, 50, 120),
        Ram = Math.Clamp(value.Ram, 50, 100),
        ProcessCpu = Math.Clamp(value.ProcessCpu, 30, 100),
        Disk = Math.Clamp(value.Disk, 70, 100),
        SustainSeconds = Math.Clamp(value.SustainSeconds, 5, 120),
        CooldownMinutes = Math.Clamp(value.CooldownMinutes, 1, 120)
    };
}

public sealed record AlertSettings(
    bool Enabled,
    bool AutoAether,
    double CpuLoad,
    double CpuTemp,
    double GpuTemp,
    double Ram,
    double ProcessCpu,
    double Disk,
    int SustainSeconds,
    int CooldownMinutes)
{
    public static AlertSettings Default { get; } = new(
        true, true, 95, 90, 90, 95, 80, 95, 15, 10);
}
