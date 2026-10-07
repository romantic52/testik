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

    public async Task<long> IncrementRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var next = Interlocked.Increment(ref _revision);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO metadata(key, value)
            VALUES ('revision', $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$value", next.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return next;
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

            CREATE TABLE IF NOT EXISTS metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            INSERT INTO metadata(key, value)
            VALUES ('schema_version', '1')
            ON CONFLICT(key) DO NOTHING;

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
