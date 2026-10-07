using System.Text.Json;
using System.Text.RegularExpressions;
using Aegis.Agent.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Aegis.Agent.Services;

public sealed class IncidentStoreService
{
    private static readonly Regex IncidentIdPattern =
        new(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,95}$", RegexOptions.Compiled);

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly StateDatabase _database;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public IncidentStoreService(StateDatabase database)
    {
        _database = database;
        MigrateLegacyState();
    }

    public string DataDirectory => _database.DataDirectory;
    public string DatabasePath => _database.DatabasePath;
    public long Revision => _database.Revision;

    public async Task<IReadOnlyList<IncidentRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id, title, source, severity, status, description,
                events_json, auto, aether_sent, rule_key, created_at
            FROM incidents
            ORDER BY datetime(created_at) DESC, rowid DESC;
            """;

        var result = new List<IncidentRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadIncident(reader));

        return result;
    }

    public async Task<IncidentRecord> UpsertAsync(
        IncidentRecord incident,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(incident);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = _database.OpenConnection();
            await using var transaction = connection.BeginTransaction();

            var exists = await ExistsAsync(connection, transaction, normalized.Id, cancellationToken);
            var now = DateTimeOffset.UtcNow;

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO incidents (
                        id, title, source, severity, status, description,
                        events_json, auto, aether_sent, rule_key, created_at, updated_at
                    )
                    VALUES (
                        $id, $title, $source, $severity, $status, $description,
                        $events, $auto, $aetherSent, $ruleKey, $createdAt, $updatedAt
                    )
                    ON CONFLICT(id) DO UPDATE SET
                        title = excluded.title,
                        source = excluded.source,
                        severity = excluded.severity,
                        status = excluded.status,
                        description = excluded.description,
                        events_json = excluded.events_json,
                        auto = excluded.auto,
                        aether_sent = excluded.aether_sent,
                        rule_key = excluded.rule_key,
                        created_at = excluded.created_at,
                        updated_at = excluded.updated_at;
                    """;

                BindIncident(command, normalized, now);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditAsync(
                connection,
                transaction,
                new AuditRecord(
                    now,
                    exists ? "incident.updated" : "incident.created",
                    normalized.Id,
                    normalized.Title,
                    normalized.Source),
                cancellationToken);

            await _database.IncrementRevisionAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return normalized;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<bool> DeleteAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        id = (id ?? "").Trim();
        if (!IncidentIdPattern.IsMatch(id))
            return false;

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = _database.OpenConnection();
            await using var transaction = connection.BeginTransaction();

            string? title = null;
            await using (var lookup = connection.CreateCommand())
            {
                lookup.Transaction = transaction;
                lookup.CommandText = "SELECT title FROM incidents WHERE id = $id LIMIT 1;";
                lookup.Parameters.AddWithValue("$id", id);
                title = (await lookup.ExecuteScalarAsync(cancellationToken))?.ToString();
            }

            if (title is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM incidents WHERE id = $id;";
                delete.Parameters.AddWithValue("$id", id);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditAsync(
                connection,
                transaction,
                new AuditRecord(
                    DateTimeOffset.UtcNow,
                    "incident.deleted",
                    id,
                    title,
                    null),
                cancellationToken);

            await _database.IncrementRevisionAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task AppendAuditAsync(
        AuditRecord record,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = _database.OpenConnection();
            await using var transaction = connection.BeginTransaction();

            await InsertAuditAsync(
                connection,
                transaction,
                record with
                {
                    Timestamp = record.Timestamp == default
                        ? DateTimeOffset.UtcNow
                        : record.Timestamp
                },
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<AuditRecord>> AuditAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 5000);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT timestamp, type, subject, detail, source
            FROM audit
            ORDER BY id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<AuditRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AuditRecord(
                ParseTimestamp(reader.GetString(0)),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return result;
    }

    private IncidentRecord Normalize(IncidentRecord incident)
    {
        var id = (incident.Id ?? "").Trim();
        if (!IncidentIdPattern.IsMatch(id))
            throw new ArgumentException("Incident id contains unsupported characters or is too long");

        return incident with
        {
            Id = id,
            Title = RequiredText(incident.Title, 160, "Incident title is required"),
            Source = LimitText(incident.Source, 160),
            Severity = string.IsNullOrWhiteSpace(incident.Severity)
                ? "Средний"
                : LimitText(incident.Severity, 32),
            Status = string.IsNullOrWhiteSpace(incident.Status)
                ? "Новый"
                : LimitText(incident.Status, 32),
            Description = LimitText(incident.Description, 4_000),
            CreatedAt = incident.CreatedAt == default
                ? DateTimeOffset.UtcNow
                : incident.CreatedAt.ToUniversalTime(),
            Events = (incident.Events ?? Array.Empty<string>())
                .Take(200)
                .Select(value => LimitText(value, 1_000))
                .ToArray(),
            RuleKey = string.IsNullOrWhiteSpace(incident.RuleKey)
                ? null
                : LimitText(incident.RuleKey, 160)
        };
    }

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM incidents WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private void BindIncident(
        SqliteCommand command,
        IncidentRecord incident,
        DateTimeOffset updatedAt)
    {
        command.Parameters.AddWithValue("$id", incident.Id);
        command.Parameters.AddWithValue("$title", incident.Title ?? "");
        command.Parameters.AddWithValue("$source", incident.Source ?? "");
        command.Parameters.AddWithValue("$severity", incident.Severity ?? "Средний");
        command.Parameters.AddWithValue("$status", incident.Status ?? "Новый");
        command.Parameters.AddWithValue("$description", incident.Description ?? "");
        command.Parameters.AddWithValue(
            "$events",
            JsonSerializer.Serialize(incident.Events ?? Array.Empty<string>(), _json));
        command.Parameters.AddWithValue("$auto", incident.Auto ? 1 : 0);
        command.Parameters.AddWithValue("$aetherSent", incident.AetherSent ? 1 : 0);
        command.Parameters.AddWithValue("$ruleKey", (object?)incident.RuleKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", incident.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", updatedAt.ToUniversalTime().ToString("O"));
    }

    private IncidentRecord ReadIncident(SqliteDataReader reader)
    {
        IReadOnlyList<string> events;
        try
        {
            events = JsonSerializer.Deserialize<string[]>(reader.GetString(6), _json)
                ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            events = Array.Empty<string>();
        }

        return new IncidentRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            events,
            reader.GetInt64(7) != 0,
            reader.GetInt64(8) != 0,
            reader.IsDBNull(9) ? null : reader.GetString(9),
            ParseTimestamp(reader.GetString(10)));
    }

    private static async Task InsertAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuditRecord record,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit(timestamp, type, subject, detail, source)
            VALUES ($timestamp, $type, $subject, $detail, $source);
            """;
        command.Parameters.AddWithValue("$timestamp", record.Timestamp.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$type", LimitText(record.Type, 120));
        command.Parameters.AddWithValue("$subject", DbValue(record.Subject));
        command.Parameters.AddWithValue("$detail", DbValue(record.Detail));
        command.Parameters.AddWithValue("$source", DbValue(record.Source));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void MigrateLegacyState()
    {
        var incidentPath = Path.Combine(DataDirectory, "incidents.json");
        var auditPaths = Directory.Exists(DataDirectory)
            ? Directory.GetFiles(DataDirectory, "audit*.jsonl")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();

        using var connection = _database.OpenConnection();

        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM incidents;";
        var incidentCount = Convert.ToInt64(countCommand.ExecuteScalar());

        if (incidentCount == 0 && File.Exists(incidentPath))
            MigrateIncidents(connection, incidentPath);

        using var auditCountCommand = connection.CreateCommand();
        auditCountCommand.CommandText = "SELECT COUNT(*) FROM audit;";
        var auditCount = Convert.ToInt64(auditCountCommand.ExecuteScalar());

        if (auditCount == 0 && auditPaths.Length > 0)
            MigrateAudit(connection, auditPaths);
    }

    private void MigrateIncidents(SqliteConnection connection, string path)
    {
        try
        {
            var legacy = JsonSerializer.Deserialize<List<IncidentRecord>>(
                File.ReadAllText(path),
                _json) ?? new List<IncidentRecord>();

            using var transaction = connection.BeginTransaction();
            foreach (var raw in legacy)
            {
                var incident = Normalize(raw);
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR REPLACE INTO incidents (
                        id, title, source, severity, status, description,
                        events_json, auto, aether_sent, rule_key, created_at, updated_at
                    )
                    VALUES (
                        $id, $title, $source, $severity, $status, $description,
                        $events, $auto, $aetherSent, $ruleKey, $createdAt, $updatedAt
                    );
                    """;
                BindIncident(command, incident, DateTimeOffset.UtcNow);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            MoveLegacyFile(path, ".migrated");
        }
        catch (JsonException)
        {
            MoveLegacyFile(path, $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}");
        }
    }

    private void MigrateAudit(SqliteConnection connection, IEnumerable<string> paths)
    {
        using var transaction = connection.BeginTransaction();

        foreach (var path in paths.OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    var record = JsonSerializer.Deserialize<AuditRecord>(line, _json);
                    if (record is null)
                        continue;

                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO audit(timestamp, type, subject, detail, source)
                        VALUES ($timestamp, $type, $subject, $detail, $source);
                        """;
                    command.Parameters.AddWithValue("$timestamp", record.Timestamp.ToUniversalTime().ToString("O"));
                    command.Parameters.AddWithValue("$type", LimitText(record.Type, 120));
                    command.Parameters.AddWithValue("$subject", DbValue(record.Subject));
                    command.Parameters.AddWithValue("$detail", DbValue(record.Detail));
                    command.Parameters.AddWithValue("$source", DbValue(record.Source));
                    command.ExecuteNonQuery();
                }
                catch (JsonException)
                {
                    // Skip malformed legacy lines but continue importing valid audit history.
                }
            }
        }

        transaction.Commit();

        foreach (var path in paths)
            MoveLegacyFile(path, ".migrated");
    }

    private static void MoveLegacyFile(string path, string suffix)
    {
        try
        {
            if (!File.Exists(path))
                return;

            var destination = path + suffix;
            if (File.Exists(destination))
                destination += "-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");

            File.Move(path, destination);
        }
        catch
        {
            // Migration success must not be undone just because a legacy file cannot be renamed.
        }
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;

    private static string RequiredText(string? value, int maxLength, string error)
    {
        var normalized = LimitText(value, maxLength);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException(error);

        return normalized;
    }

    private static string LimitText(string? value, int maxLength)
    {
        var normalized = (value ?? "").Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static object DbValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
}

public sealed record IncidentRecord(
    string Id,
    string? Title,
    string? Source,
    string? Severity,
    string? Status,
    string? Description,
    IReadOnlyList<string>? Events,
    bool Auto,
    bool AetherSent,
    string? RuleKey,
    DateTimeOffset CreatedAt);

public sealed record AuditRecord(
    DateTimeOffset Timestamp,
    string Type,
    string? Subject,
    string? Detail,
    string? Source);
