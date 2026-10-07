namespace Aegis.Agent.Services;

public sealed class WindowsEventAlertService : BackgroundService
{
    private static readonly string[] Logs = ["System", "Application"];

    private readonly WindowsEventLogService _events;
    private readonly IncidentStoreService _incidents;
    private readonly ILogger<WindowsEventAlertService> _logger;
    private readonly Dictionary<string, long> _watermarks =
        new(StringComparer.OrdinalIgnoreCase);

    public WindowsEventAlertService(
        WindowsEventLogService events,
        IncidentStoreService incidents,
        ILogger<WindowsEventAlertService> logger)
    {
        _events = events;
        _incidents = incidents;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SeedWatermarks();

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                foreach (var logName in Logs)
                    await PollLogAsync(logName, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                _logger.LogWarning(error, "Windows Event Log correlation failed");
            }
        }
    }

    private void SeedWatermarks()
    {
        foreach (var logName in Logs)
        {
            var newest = _events
                .GetRecent(logName, 20)
                .Where(item => item.RecordId is not null)
                .Select(item => item.RecordId!.Value)
                .DefaultIfEmpty(0)
                .Max();

            _watermarks[logName] = newest;
        }
    }

    private async Task PollLogAsync(
        string logName,
        CancellationToken cancellationToken)
    {
        var watermark = _watermarks.GetValueOrDefault(logName);

        var fresh = _events
            .GetRecent(logName, 100)
            .Where(item => item.RecordId is long recordId && recordId > watermark)
            .OrderBy(item => item.RecordId)
            .ToList();

        if (fresh.Count == 0)
            return;

        foreach (var item in fresh)
        {
            if (item.RecordId is not long recordId)
                continue;

            watermark = Math.Max(watermark, recordId);

            // Event Log levels: 1 Critical, 2 Error, 3 Warning.
            if (item.Level is not (1 or 2))
                continue;

            if ((item.Provider ?? "").Contains(
                    "AEGIS",
                    StringComparison.OrdinalIgnoreCase))
                continue;

            var timestamp = item.TimeCreated ?? DateTimeOffset.UtcNow;
            var severity = item.Level == 1 ? "Критический" : "Высокий";
            var provider = string.IsNullOrWhiteSpace(item.Provider)
                ? "Unknown provider"
                : item.Provider.Trim();
            var message = string.IsNullOrWhiteSpace(item.Message)
                ? "Windows Event Log не предоставил описание события."
                : item.Message.Trim();

            var incident = new IncidentRecord(
                $"WIN-EVT-{logName.ToUpperInvariant()}-{recordId}",
                $"Windows {logName}: Event {item.EventId}",
                $"{provider} / {logName}",
                severity,
                "Авто",
                message,
                new[]
                {
                    $"{timestamp.LocalDateTime:HH:mm:ss} — {item.LevelName ?? severity}",
                    $"Event ID {item.EventId} • Record {recordId}"
                },
                true,
                false,
                $"windows-event:{logName}:{recordId}",
                timestamp.ToUniversalTime());

            await _incidents.UpsertAsync(incident, cancellationToken);

            _logger.LogWarning(
                "Windows Event Log created incident {IncidentId} from {LogName}/{EventId}",
                incident.Id,
                logName,
                item.EventId);
        }

        _watermarks[logName] = watermark;
    }
}
