using System.Diagnostics.Eventing.Reader;

namespace Aegis.Agent.Services;

public sealed class WindowsEventLogService
{
    private static readonly HashSet<string> AllowedLogs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "System",
            "Application"
        };

    public IReadOnlyList<WindowsEventSnapshot> GetRecent(
        string logName = "System",
        int limit = 100)
    {
        if (!AllowedLogs.Contains(logName))
            throw new ArgumentException("Only System and Application event logs are supported");

        limit = Math.Clamp(limit, 1, 500);
        var result = new List<WindowsEventSnapshot>(limit);

        try
        {
            var query = new EventLogQuery(
                logName,
                PathType.LogName,
                "*[System[(Level=1 or Level=2 or Level=3)]]")
            {
                ReverseDirection = true,
                TolerateQueryErrors = true
            };

            using var reader = new EventLogReader(query);

            while (result.Count < limit)
            {
                using var record = reader.ReadEvent();
                if (record is null)
                    break;

                result.Add(new WindowsEventSnapshot(
                    record.RecordId,
                    record.Id,
                    record.Level,
                    Safe(() => record.LevelDisplayName),
                    record.ProviderName,
                    record.TimeCreated is DateTime time
                        ? new DateTimeOffset(time)
                        : null,
                    Safe(() => record.FormatDescription()),
                    record.MachineName,
                    logName));
            }
        }
        catch (EventLogException)
        {
            return Array.Empty<WindowsEventSnapshot>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<WindowsEventSnapshot>();
        }

        return result;
    }

    private static string? Safe(Func<string?> read)
    {
        try { return read(); }
        catch { return null; }
    }
}

public sealed record WindowsEventSnapshot(
    long? RecordId,
    int EventId,
    byte? Level,
    string? LevelName,
    string? Provider,
    DateTimeOffset? TimeCreated,
    string? Message,
    string? MachineName,
    string LogName);
