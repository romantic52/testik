using Aegis.Agent.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Aegis.Agent.Infrastructure;

public sealed class StateDatabase
{
    private readonly string _connectionString;
    private long _revision;

    public StateDatabase(IOptions<AgentOptions> options)
    {
        var configured = options.Value.DataDirectory;
        DataDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AEGIS")
            : Environment.ExpandEnvironmentVariables(configured);

        DataDirectory = Path.GetFullPath(DataDirectory);
        Directory.CreateDirectory(DataDirectory);

        DatabasePath = Path.Combine(DataDirectory, "aegis.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        InitializeSchema();
        _revision = LoadRevision();
    }

    public string DataDirectory { get; }
    public string DatabasePath { get; }
    public long Revision => Interlocked.Read(ref _revision);

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();

        return connection;
    }

    public async Task<long> StageRevisionIncrementAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE metadata
            SET value = CAST(value AS INTEGER) + 1
            WHERE key = 'revision'
            RETURNING value;
            """;

        var raw = (await command.ExecuteScalarAsync(cancellationToken))?.ToString();
        if (!long.TryParse(
                raw,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var next))
        {
            throw new InvalidOperationException("SQLite incident revision could not be advanced");
        }

        return next;
    }

    public void PublishCommittedRevision(long revision)
    {
        Interlocked.Exchange(ref _revision, revision);
    }

    public async Task<StateDatabaseStatus> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version' LIMIT 1;";
            var schema = (await command.ExecuteScalarAsync(cancellationToken))?.ToString() ?? "unknown";

            await using var writeProbe = connection.CreateCommand();
            writeProbe.CommandText = """
                INSERT INTO metadata(key, value)
                VALUES ('last_probe_at', $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            writeProbe.Parameters.AddWithValue("$value", DateTimeOffset.UtcNow.ToString("O"));
            await writeProbe.ExecuteNonQueryAsync(cancellationToken);

            return new StateDatabaseStatus(
                true,
                schema,
                Path.GetFileName(DatabasePath),
                Revision,
                null);
        }
        catch (Exception error)
        {
            return new StateDatabaseStatus(
                false,
                "unknown",
                Path.GetFileName(DatabasePath),
                Revision,
                error.GetType().Name);
        }
    }

    private void InitializeSchema()
    {
        using var connection = OpenConnection();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            pragma.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS incidents (
                id TEXT PRIMARY KEY COLLATE NOCASE,
                title TEXT NOT NULL,
                source TEXT NOT NULL,
                severity TEXT NOT NULL,
                status TEXT NOT NULL,
                description TEXT NOT NULL,
                events_json TEXT NOT NULL,
                auto INTEGER NOT NULL DEFAULT 0,
                aether_sent INTEGER NOT NULL DEFAULT 0,
                rule_key TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_incidents_created_at
                ON incidents(created_at DESC);

            CREATE INDEX IF NOT EXISTS ix_incidents_status
                ON incidents(status);

            CREATE INDEX IF NOT EXISTS ix_incidents_rule_key
                ON incidents(rule_key);

            CREATE TABLE IF NOT EXISTS audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                type TEXT NOT NULL,
                subject TEXT NULL,
                detail TEXT NULL,
                source TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_audit_timestamp
                ON audit(timestamp DESC);

            CREATE INDEX IF NOT EXISTS ix_audit_type
                ON audit(type);

            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS telemetry_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                cpu_load_percent REAL NULL,
                cpu_temperature_c REAL NULL,
                memory_load_percent REAL NULL,
                gpu_load_percent REAL NULL,
                gpu_temperature_c REAL NULL,
                receive_bytes_per_second REAL NOT NULL,
                send_bytes_per_second REAL NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_telemetry_history_timestamp
                ON telemetry_history(timestamp ASC);

            CREATE TABLE IF NOT EXISTS metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            INSERT INTO metadata(key, value)
            VALUES ('schema_version', '2')
            ON CONFLICT(key) DO UPDATE SET value = '2';

            INSERT INTO metadata(key, value)
            VALUES ('revision', '0')
            ON CONFLICT(key) DO NOTHING;
            """;
        command.ExecuteNonQuery();
    }

    private long LoadRevision()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = 'revision' LIMIT 1;";
        var raw = command.ExecuteScalar()?.ToString();

        return long.TryParse(raw, out var revision) ? revision : 0L;
    }
}


public sealed record StateDatabaseStatus(
    bool Ready,
    string SchemaVersion,
    string DatabaseFile,
    long Revision,
    string? Error);
