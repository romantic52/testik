using System.Globalization;
using System.Text.Json;
using Aegis.Agent.Infrastructure;

namespace Aegis.Agent.Services;

/// <summary>
/// Background checks behind the "Защита" tab. Read-only, blocks nothing:
///  * volumes every 15 s → device_events journal (flash drives, external disks plugged/unplugged);
///  * disks every 5 min → incident when Windows reports a disk as failing (SMART / storage health);
///  * listening TCP ports every 60 s → incident when a new port becomes reachable from the network.
/// The first port scan after installation only remembers what is already open.
/// </summary>
public sealed class ProtectionWatchService : BackgroundService
{
    private const string KnownPortsKey = "protection.known_ports";

    private readonly StateDatabase _database;
    private readonly IncidentStoreService _incidents;
    private readonly DiskMonitorService _disks;
    private readonly PortsService _ports;
    private readonly ILogger<ProtectionWatchService> _logger;
    private Dictionary<string, VolumeSnapshot>? _volumes;

    public ProtectionWatchService(StateDatabase database, IncidentStoreService incidents, DiskMonitorService disks, PortsService ports, ILogger<ProtectionWatchService> logger)
    {
        _database = database;
        _incidents = incidents;
        _disks = disks;
        _ports = ports;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        var tick = 0L;
        do
        {
            await Guard("volumes", () => PollVolumesAsync(stoppingToken), stoppingToken);
            if (tick % 4 == 0) await Guard("ports", () => PollPortsAsync(stoppingToken), stoppingToken);
            if (tick % 20 == 0) await Guard("disks", () => PollDisksAsync(stoppingToken), stoppingToken);
            tick++;
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task Guard(string what, Func<Task> action, CancellationToken token)
    {
        try { await action(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { _logger.LogWarning(error, "Protection check '{Check}' failed", what); }
    }

    // ---------- Removable / external devices ----------

    private async Task PollVolumesAsync(CancellationToken token)
    {
        var now = ReadVolumes();
        if (_volumes is null) { _volumes = now; return; }  // baseline: drives present at start are not "connected"
        var changes = new List<(string Action, VolumeSnapshot Volume)>();
        foreach (var (key, v) in now) if (!_volumes.ContainsKey(key)) changes.Add(("connected", v));
        foreach (var (key, v) in _volumes) if (!now.ContainsKey(key)) changes.Add(("disconnected", v));
        _volumes = now;
        if (changes.Count == 0) return;

        await using var connection = _database.OpenConnection();
        var stamp = DateTimeOffset.UtcNow;
        foreach (var (action, v) in changes)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO device_events(timestamp,action,kind,label,detail,record_key) VALUES($t,$a,$k,$l,$d,$r) ON CONFLICT(record_key) DO NOTHING;";
            cmd.Parameters.AddWithValue("$t", stamp.ToString("O"));
            cmd.Parameters.AddWithValue("$a", action);
            cmd.Parameters.AddWithValue("$k", v.Kind);
            cmd.Parameters.AddWithValue("$l", v.Label);
            cmd.Parameters.AddWithValue("$d", v.Detail);
            cmd.Parameters.AddWithValue("$r", $"{action}:{v.Key}:{stamp.UtcTicks}");
            await cmd.ExecuteNonQueryAsync(token);
        }
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");
        await using var prune = connection.CreateCommand();
        prune.CommandText = "DELETE FROM device_events WHERE timestamp < $c;";
        prune.Parameters.AddWithValue("$c", cutoff);
        await prune.ExecuteNonQueryAsync(token);
    }

    private static Dictionary<string, VolumeSnapshot> ReadVolumes()
    {
        var result = new Dictionary<string, VolumeSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Removable or DriveType.Fixed or DriveType.CDRom) || !drive.IsReady) continue;
                var letter = drive.Name.TrimEnd('\\');
                var sizeGb = Math.Round(drive.TotalSize / 1_073_741_824d, 1);
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? letter : $"{letter} «{drive.VolumeLabel}»";
                var kind = drive.DriveType switch { DriveType.Removable => "removable", DriveType.CDRom => "optical", _ => "fixed" };
                var key = $"{letter}|{drive.VolumeLabel}|{drive.DriveFormat}|{drive.TotalSize}";
                result[key] = new VolumeSnapshot(key, kind, label,
                    string.Create(CultureInfo.InvariantCulture, $"{drive.DriveFormat}, {sizeGb:0.#} ГБ"));
            }
            catch { /* drive vanished during enumeration */ }
        }
        return result;
    }

    public async Task<IReadOnlyList<DeviceEventRecord>> DeviceEventsAsync(int limit, CancellationToken token)
    {
        await using var connection = _database.OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT timestamp,action,kind,label,detail FROM device_events ORDER BY timestamp DESC, id DESC LIMIT $l;";
        cmd.Parameters.AddWithValue("$l", Math.Clamp(limit, 1, 1000));
        var list = new List<DeviceEventRecord>();
        await using var r = await cmd.ExecuteReaderAsync(token);
        while (await r.ReadAsync(token))
            list.Add(new DeviceEventRecord(DateTimeOffset.Parse(r.GetString(0), CultureInfo.InvariantCulture), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }

    // ---------- Disk health ----------

    private async Task PollDisksAsync(CancellationToken token)
    {
        var report = _disks.GetReport();
        foreach (var disk in report.Disks.Where(d => d.Predicted == "failing" || d.Health == "unhealthy"))
        {
            var id = "DISK-HEALTH-" + IdPart(disk.Serial ?? disk.Model);
            if (await ExistsAsync(id, token)) continue;
            await _incidents.UpsertAsync(new IncidentRecord(id, "Диск может выйти из строя: " + disk.Model, "Диски / " + Environment.MachineName,
                "Критический", "Авто",
                (disk.Predicted == "failing" ? "SMART сообщает о прогнозируемом отказе. " : "Windows помечает диск как неисправный. ") +
                "Сделай резервную копию данных с этого диска и замени его.",
                [$"{DateTimeOffset.Now:HH:mm:ss} — здоровье: {disk.Health}, SMART: {disk.Predicted}, серийный номер: {disk.Serial ?? "—"}"],
                true, false, "protection:disk:" + IdPart(disk.Serial ?? disk.Model), DateTimeOffset.UtcNow), token);
        }
    }

    // ---------- Network-exposed ports ----------

    private async Task PollPortsAsync(CancellationToken token)
    {
        var exposed = _ports.GetOpenPorts().Where(p => p.Protocol == "TCP" && p.Exposure == "network").ToList();
        var current = exposed.GroupBy(PortKey).ToDictionary(g => g.Key, g => g.First());
        var known = await LoadKnownPortsAsync(token);
        if (known is null)
        {
            await SaveKnownPortsAsync(current.Keys, token);   // first run: remember, do not alarm
            return;
        }
        var fresh = current.Where(kv => !known.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        if (fresh.Count == 0) return;
        known.UnionWith(fresh.Select(PortKey));
        await SaveKnownPortsAsync(known, token);

        var stamp = DateTimeOffset.UtcNow;
        var lines = fresh.Take(20).Select(p => $"{DateTimeOffset.Now:HH:mm:ss} — TCP {p.Address}:{p.Port} · {p.ProcessName ?? "PID " + p.ProcessId}" +
            (p.ProcessPath is null ? "" : " · " + p.ProcessPath)).ToList();
        var first = fresh[0];
        await _incidents.UpsertAsync(new IncidentRecord(
            $"PORT-NEW-{stamp:yyyyMMddHHmmss}",
            fresh.Count == 1
                ? $"Новый порт открыт в сеть: TCP {first.Port} ({first.ProcessName ?? "PID " + first.ProcessId})"
                : $"Новые порты открыты в сеть: {fresh.Count}",
            "Порты / " + Environment.MachineName, "Средний", "Авто",
            "Программа начала принимать подключения из сети. Если это не ожидалось — проверь программу и правила брандмауэра Windows. " +
            "Доступность извне зависит ещё и от брандмауэра.",
            lines, true, false, "protection:port", stamp), token);
    }

    public static string PortKey(OpenPort p) => $"{p.Protocol}:{p.Port}:{(p.ProcessName ?? "pid").ToLowerInvariant()}";

    private async Task<HashSet<string>?> LoadKnownPortsAsync(CancellationToken token)
    {
        await using var connection = _database.OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM settings WHERE key=$k;";
        cmd.Parameters.AddWithValue("$k", KnownPortsKey);
        return await cmd.ExecuteScalarAsync(token) is string json && JsonSerializer.Deserialize<List<string>>(json) is { } list
            ? new HashSet<string>(list, StringComparer.OrdinalIgnoreCase) : null;
    }

    private async Task SaveKnownPortsAsync(IEnumerable<string> keys, CancellationToken token)
    {
        await using var connection = _database.OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key,value_json,updated_at) VALUES($k,$v,$t) ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json, updated_at=excluded.updated_at;";
        cmd.Parameters.AddWithValue("$k", KnownPortsKey);
        cmd.Parameters.AddWithValue("$v", JsonSerializer.Serialize(keys.Take(5000).ToList()));
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(token);
    }

    private async Task<bool> ExistsAsync(string id, CancellationToken token) =>
        (await _incidents.ListAsync(token)).Any(i => i.Id == id);

    private static string IdPart(string value) =>
        new string(value.Where(char.IsLetterOrDigit).Take(40).ToArray()).ToUpperInvariant();

    private sealed record VolumeSnapshot(string Key, string Kind, string Label, string Detail);
}

public sealed record DeviceEventRecord(DateTimeOffset Timestamp, string Action, string Kind, string Label, string? Detail);
