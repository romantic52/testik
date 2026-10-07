using Aegis.Agent.Configuration;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aegis.Agent.Services;

public sealed class IncidentStoreService
{
    private static readonly Regex IncidentIdPattern =
        new(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,95}$", RegexOptions.Compiled);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _dataDirectory;
    private readonly string _incidentFile;
    private readonly string _auditFile;
    private readonly long _auditMaxBytes;
    private readonly int _auditRetentionFiles;
    private long _revision;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly JsonSerializerOptions _jsonLine = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public IncidentStoreService(IOptions<AgentOptions> options)
    {
        var configured = options.Value.DataDirectory;
        _dataDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AEGIS")
            : Environment.ExpandEnvironmentVariables(configured);

        _dataDirectory = Path.GetFullPath(_dataDirectory);
        Directory.CreateDirectory(_dataDirectory);
        _incidentFile = Path.Combine(_dataDirectory, "incidents.json");
        _auditFile = Path.Combine(_dataDirectory, "audit.jsonl");
        _auditMaxBytes = options.Value.SafeAuditMaxMegabytes * 1024L * 1024L;
        _auditRetentionFiles = options.Value.SafeAuditRetentionFiles;
    }

    public async Task<IReadOnlyList<IncidentRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var incidents = await ReadUnsafeAsync(cancellationToken);
            return incidents
                .OrderByDescending(i => i.CreatedAt)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IncidentRecord> UpsertAsync(IncidentRecord incident, CancellationToken cancellationToken = default)
    {
        var id = (incident.Id ?? "").Trim();
        if (!IncidentIdPattern.IsMatch(id))
            throw new ArgumentException("Incident id contains unsupported characters or is too long");

        var normalized = incident with
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
            CreatedAt = incident.CreatedAt == default ? DateTimeOffset.UtcNow : incident.CreatedAt,
            Events = (incident.Events ?? Array.Empty<string>())
                .Take(200)
                .Select(value => LimitText(value, 1_000))
                .ToArray(),
            RuleKey = string.IsNullOrWhiteSpace(incident.RuleKey)
                ? null
                : LimitText(incident.RuleKey, 160)
        };

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var incidents = await ReadUnsafeAsync(cancellationToken);
            var index = incidents.FindIndex(i => i.Id.Equals(normalized.Id, StringComparison.OrdinalIgnoreCase));
            var action = index >= 0 ? "incident.updated" : "incident.created";

            if (index >= 0) incidents[index] = normalized;
            else incidents.Add(normalized);

            await WriteUnsafeAsync(incidents, cancellationToken);
            Interlocked.Increment(ref _revision);
            await AppendAuditUnsafeAsync(new AuditRecord(
                DateTimeOffset.UtcNow,
                action,
                normalized.Id,
                normalized.Title,
                normalized.Source), cancellationToken);

            return normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var incidents = await ReadUnsafeAsync(cancellationToken);
            var removed = incidents.RemoveAll(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (!removed) return false;

            await WriteUnsafeAsync(incidents, cancellationToken);
            Interlocked.Increment(ref _revision);
            await AppendAuditUnsafeAsync(new AuditRecord(
                DateTimeOffset.UtcNow,
                "incident.deleted",
                id,
                null,
                null), cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendAuditAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await AppendAuditUnsafeAsync(record with
            {
                Timestamp = record.Timestamp == default ? DateTimeOffset.UtcNow : record.Timestamp
            }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AuditRecord>> AuditAsync(int limit = 200, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 1000);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var files = new List<string>();
            if (File.Exists(_auditFile))
                files.Add(_auditFile);

            for (var index = 1; index <= _auditRetentionFiles; index++)
            {
                var rotated = RotatedAuditPath(index);
                if (File.Exists(rotated))
                    files.Add(rotated);
            }

            if (files.Count == 0)
                return Array.Empty<AuditRecord>();

            var result = new List<AuditRecord>(limit);
            foreach (var file in files)
            {
                var lines = await File.ReadAllLinesAsync(file, cancellationToken);
                for (var i = lines.Length - 1; i >= 0 && result.Count < limit; i--)
                {
                    try
                    {
                        var item = JsonSerializer.Deserialize<AuditRecord>(lines[i], _jsonLine);
                        if (item is not null) result.Add(item);
                    }
                    catch
                    {
                        // One malformed historical line must not break the audit view.
                    }
                }

                if (result.Count >= limit)
                    break;
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public string DataDirectory => _dataDirectory;
    public long Revision => Interlocked.Read(ref _revision);

    private async Task<List<IncidentRecord>> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_incidentFile))
            return new List<IncidentRecord>();

        try
        {
            await using var stream = File.OpenRead(_incidentFile);
            return await JsonSerializer.DeserializeAsync<List<IncidentRecord>>(stream, _json, cancellationToken)
                ?? new List<IncidentRecord>();
        }
        catch (JsonException)
        {
            PreserveCorruptIncidentFile();
            return new List<IncidentRecord>();
        }
    }

    private void PreserveCorruptIncidentFile()
    {
        try
        {
            if (!File.Exists(_incidentFile))
                return;

            var backup = Path.Combine(
                _dataDirectory,
                $"incidents.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.json");
            File.Move(_incidentFile, backup, false);
        }
        catch
        {
            // A corrupt file should never prevent Agent startup.
        }
    }

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

    private async Task WriteUnsafeAsync(List<IncidentRecord> incidents, CancellationToken cancellationToken)
    {
        var temp = _incidentFile + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, incidents, _json, cancellationToken);
        }

        File.Move(temp, _incidentFile, true);
    }

    private async Task AppendAuditUnsafeAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(record, _jsonLine) + Environment.NewLine;
        RotateAuditIfNeededUnsafe(Encoding.UTF8.GetByteCount(line));
        await File.AppendAllTextAsync(_auditFile, line, cancellationToken);
    }

    private void RotateAuditIfNeededUnsafe(int incomingBytes)
    {
        if (!File.Exists(_auditFile))
            return;

        var size = new FileInfo(_auditFile).Length;
        if (size + incomingBytes <= _auditMaxBytes)
            return;

        for (var index = _auditRetentionFiles; index >= 2; index--)
        {
            var source = RotatedAuditPath(index - 1);
            var destination = RotatedAuditPath(index);

            if (File.Exists(source))
                File.Move(source, destination, overwrite: true);
        }

        File.Move(_auditFile, RotatedAuditPath(1), overwrite: true);
    }

    private string RotatedAuditPath(int index) =>
        Path.Combine(_dataDirectory, $"audit.{index}.jsonl");
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
