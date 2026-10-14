using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text.Json;
using Aegis.Agent.Infrastructure;
using Microsoft.Win32;

namespace Aegis.Agent.Services;

/// <summary>
/// Control mode: when the operator turns it on, AEGIS remembers what is on this PC
/// (programs that run, services, autostart entries, ports open to the network) as the
/// baseline. Anything new afterwards lands in a review queue: the operator allows it
/// (→ baseline) or marks it as a threat (→ incident). The mode itself blocks nothing.
/// </summary>
public sealed class ControlModeService : BackgroundService
{
    private const string EnabledKey = "control.enabled";
    private readonly StateDatabase _database;
    private readonly IncidentStoreService _incidents;
    private readonly PortsService _ports;
    private readonly ILogger<ControlModeService> _logger;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public ControlModeService(StateDatabase database, IncidentStoreService incidents, PortsService ports, ILogger<ControlModeService> logger)
    {
        _database = database;
        _incidents = incidents;
        _ports = ports;
        _logger = logger;
    }

    public DateTimeOffset? LastScan { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try { if (await IsEnabledAsync(stoppingToken)) await ScanAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { _logger.LogWarning(error, "Control mode scan failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // ---------- Inventory ----------

    public IReadOnlyList<ControlItem> Inventory()
    {
        var items = new List<ControlItem>();
        Try(() => items.AddRange(Processes()));
        Try(() => items.AddRange(Services()));
        Try(() => items.AddRange(Autostart()));
        Try(() => items.AddRange(_ports.GetOpenPorts()
            .Where(p => p.Protocol == "TCP" && p.Exposure == "network")
            .GroupBy(ProtectionWatchService.PortKey)
            .Select(g => g.First())
            .Select(p => new ControlItem("port", ProtectionWatchService.PortKey(p), $"TCP {p.Port} · {p.ProcessName ?? "PID " + p.ProcessId}",
                p.ProcessPath ?? p.Address, p.ProcessPath))));
        return items.GroupBy(i => (i.Kind, i.Key)).Select(g => g.First()).ToList();
    }

    private void Try(Action action)
    {
        try { action(); } catch (Exception error) { _logger.LogDebug(error, "Control inventory source failed"); }
    }

    private static IEnumerable<ControlItem> Processes()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
            using (process)
            {
                string? path = null;
                try { path = process.MainModule?.FileName; } catch { /* protected/system process: not readable without elevation */ }
                if (path is null || !seen.Add(path)) continue;
                yield return new ControlItem("process", path.ToLowerInvariant(), Path.GetFileName(path), path, path);
            }
    }

    private static IEnumerable<ControlItem> Services()
    {
        using var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName, PathName, StartMode FROM Win32_Service");
        var list = new List<ControlItem>();
        foreach (ManagementObject service in searcher.Get())
            using (service)
            {
                var name = service["Name"]?.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                var command = service["PathName"]?.ToString() ?? "";
                list.Add(new ControlItem("service", name.ToLowerInvariant(), (service["DisplayName"]?.ToString() ?? name) + " (" + name + ")",
                    $"{service["StartMode"]}: {command}", ExecutableOf(command)));
            }
        return list;
    }

    private static IEnumerable<ControlItem> Autostart()
    {
        var list = new List<ControlItem>();
        void Read(RegistryKey root, string hive, string subKey)
        {
            using var key = root.OpenSubKey(subKey);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                var command = key.GetValue(name)?.ToString() ?? "";
                list.Add(new ControlItem("autostart", $@"{hive}\{subKey}\{name}".ToLowerInvariant(), name, $@"{hive}\…\{Path.GetFileName(subKey)}: {command}", ExecutableOf(command)));
            }
        }
        foreach (var sub in new[] { @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\RunOnce" })
        {
            Read(Registry.CurrentUser, "HKCU", sub);
            Read(Registry.LocalMachine, "HKLM", sub);
        }
        Read(Registry.LocalMachine, "HKLM", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run");
        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) })
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                list.Add(new ControlItem("autostart", file.ToLowerInvariant(), Path.GetFileName(file), "Папка автозагрузки: " + file, file));
        }
        return list;
    }

    /// <summary>Executable from a command line: "C:\a b\x.exe" -arg → C:\a b\x.exe; C:\x.exe -k → C:\x.exe.</summary>
    public static string? ExecutableOf(string? command)
    {
        var text = Environment.ExpandEnvironmentVariables((command ?? "").Trim());
        if (text.Length == 0) return null;
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? text[..(exe + 4)] : text.Split(' ')[0];
    }

    /// <summary>low = signed; medium = unsigned; high = unsigned in a user-writable folder (Temp, Downloads, AppData).</summary>
    public static string Risk(FileTrust? trust) => trust is null || !trust.Exists ? "medium"
        : trust.Signed ? "low" : trust.UserWritable ? "high" : "medium";

    private static string Reason(FileTrust? trust) => trust is null || !trust.Exists ? "Файл программы не прочитан"
        : trust.Signed ? "Подписан: " + (trust.Publisher ?? "издатель неизвестен")
        : trust.UserWritable ? "Без цифровой подписи, лежит в папке пользователя (Temp/Downloads/AppData)" : "Без цифровой подписи";

    // ---------- Scan / baseline ----------

    public async Task<int> ScanAsync(CancellationToken token)
    {
        await _scanLock.WaitAsync(token);
        try
        {
            var items = Inventory();
            var baseline = await BaselineKeysAsync(token);
            var now = DateTimeOffset.UtcNow.ToString("O");
            var added = 0;
            await using var connection = _database.OpenConnection();
            await using var tx = connection.BeginTransaction();
            foreach (var item in items.Where(i => !baseline.Contains(i.Kind + "|" + i.Key)))
            {
                var trust = item.Executable is null ? null : SignatureInspector.Inspect(item.Executable);
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO control_pending(kind,item_key,label,detail,risk,reason,first_seen,last_seen,status)
                    VALUES($k,$i,$l,$d,$r,$why,$t,$t,'pending')
                    ON CONFLICT(kind,item_key) DO UPDATE SET last_seen=excluded.last_seen;
                    """;
                cmd.Parameters.AddWithValue("$k", item.Kind);
                cmd.Parameters.AddWithValue("$i", item.Key);
                cmd.Parameters.AddWithValue("$l", Trim(item.Label, 200));
                cmd.Parameters.AddWithValue("$d", (object?)Trim(item.Detail, 600) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$r", Risk(trust));
                cmd.Parameters.AddWithValue("$why", Reason(trust));
                cmd.Parameters.AddWithValue("$t", now);
                added += await cmd.ExecuteNonQueryAsync(token);
            }
            await tx.CommitAsync(token);
            LastScan = DateTimeOffset.UtcNow;
            return added;
        }
        finally { _scanLock.Release(); }
    }

    public async Task<int> SetEnabledAsync(bool enabled, CancellationToken token)
    {
        await using (var connection = _database.OpenConnection())
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO settings(key,value_json,updated_at) VALUES($k,$v,$t) ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json, updated_at=excluded.updated_at;";
            cmd.Parameters.AddWithValue("$k", EnabledKey);
            cmd.Parameters.AddWithValue("$v", JsonSerializer.Serialize(enabled));
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(token);
        }
        if (enabled && (await BaselineKeysAsync(token)).Count == 0)
            return await CaptureBaselineAsync("initial", token);
        return 0;
    }

    /// <summary>Everything present right now becomes the allowed baseline; the queue is cleared.</summary>
    public async Task<int> CaptureBaselineAsync(string approver, CancellationToken token)
    {
        await _scanLock.WaitAsync(token);
        try
        {
            var items = Inventory();
            var now = DateTimeOffset.UtcNow.ToString("O");
            await using var connection = _database.OpenConnection();
            await using var tx = connection.BeginTransaction();
            foreach (var item in items)
                await InsertBaselineAsync(connection, tx, item.Kind, item.Key, item.Label, item.Detail, approver, now, token);
            await using var clear = connection.CreateCommand();
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM control_pending WHERE status='pending';";
            await clear.ExecuteNonQueryAsync(token);
            await tx.CommitAsync(token);
            return items.Count;
        }
        finally { _scanLock.Release(); }
    }

    private static async Task InsertBaselineAsync(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction? tx,
        string kind, string key, string label, string? detail, string approver, string at, CancellationToken token)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO control_baseline(kind,item_key,label,detail,approved_at,approver) VALUES($k,$i,$l,$d,$t,$a)
            ON CONFLICT(kind,item_key) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$i", key);
        cmd.Parameters.AddWithValue("$l", Trim(label, 200));
        cmd.Parameters.AddWithValue("$d", (object?)Trim(detail, 600) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", at);
        cmd.Parameters.AddWithValue("$a", approver);
        await cmd.ExecuteNonQueryAsync(token);
    }

    // ---------- Operator decisions ----------

    public async Task<ControlPendingRecord?> DecideAsync(long id, string decision, CancellationToken token)
    {
        var item = (await PendingAsync("all", 5000, token)).FirstOrDefault(p => p.Id == id);
        if (item is null) return null;
        var now = DateTimeOffset.UtcNow;
        await using var connection = _database.OpenConnection();
        if (decision == "allow")
            await InsertBaselineAsync(connection, null, item.Kind, item.Key, item.Label, item.Detail, "operator", now.ToString("O"), token);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE control_pending SET status=$s WHERE id=$id;";
            cmd.Parameters.AddWithValue("$s", decision == "allow" ? "allowed" : "threat");
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(token);
        }
        if (decision == "threat")
            await _incidents.UpsertAsync(new IncidentRecord($"CONTROL-{id}", "Отмечено как угроза: " + item.Label, "Режим контроля / " + Environment.MachineName,
                item.Risk == "high" ? "Критический" : "Высокий", "Новый",
                $"{KindName(item.Kind)}: {item.Detail ?? item.Label}. {item.Reason}. Режим контроля ничего не блокирует сам — " +
                "остановить процесс или закрыть доступ в сеть можно кнопками во вкладке «Контроль» (с подтверждением).",
                [$"{item.FirstSeen.LocalDateTime:dd.MM HH:mm:ss} — впервые замечено", $"{now.LocalDateTime:HH:mm:ss} — оператор отметил как угрозу"],
                false, false, "control:" + item.Kind, now), token);
        return item with { Status = decision == "allow" ? "allowed" : "threat" };
    }

    public static string KindName(string kind) => kind switch
    {
        "process" => "Программа", "service" => "Служба", "autostart" => "Автозагрузка", "port" => "Порт в сеть", _ => kind
    };

    public async Task<IReadOnlyList<ControlPendingRecord>> PendingAsync(string? status, int limit, CancellationToken token)
    {
        await using var connection = _database.OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id,kind,item_key,label,detail,risk,reason,first_seen,last_seen,status FROM control_pending
            WHERE ($s='all' OR status=$s)
            ORDER BY CASE risk WHEN 'high' THEN 0 WHEN 'medium' THEN 1 ELSE 2 END, last_seen DESC LIMIT $l;
            """;
        cmd.Parameters.AddWithValue("$s", string.IsNullOrWhiteSpace(status) ? "pending" : status);
        cmd.Parameters.AddWithValue("$l", Math.Clamp(limit, 1, 5000));
        var list = new List<ControlPendingRecord>();
        await using var r = await cmd.ExecuteReaderAsync(token);
        while (await r.ReadAsync(token))
            list.Add(new ControlPendingRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture), DateTimeOffset.Parse(r.GetString(8), CultureInfo.InvariantCulture), r.GetString(9)));
        return list;
    }

    public async Task<ControlSummary> SummaryAsync(CancellationToken token)
    {
        await using var connection = _database.OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT count(*) FROM control_baseline),
                   (SELECT count(*) FROM control_pending WHERE status='pending'),
                   (SELECT count(*) FROM control_pending WHERE status='pending' AND risk='high'),
                   (SELECT count(*) FROM control_pending WHERE status='threat'),
                   (SELECT min(approved_at) FROM control_baseline);
            """;
        await using var r = await cmd.ExecuteReaderAsync(token);
        await r.ReadAsync(token);
        return new ControlSummary(await IsEnabledAsync(token), r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3),
            r.IsDBNull(4) ? null : DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture), LastScan, Environment.IsPrivilegedProcess);
    }

    public async Task<bool> IsEnabledAsync(CancellationToken token)
    {
        await using var connection = _database.OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM settings WHERE key=$k;";
        cmd.Parameters.AddWithValue("$k", EnabledKey);
        return await cmd.ExecuteScalarAsync(token) is string json && JsonSerializer.Deserialize<bool>(json);
    }

    private async Task<HashSet<string>> BaselineKeysAsync(CancellationToken token)
    {
        await using var connection = _database.OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT kind, item_key FROM control_baseline;";
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync(token);
        while (await r.ReadAsync(token)) set.Add(r.GetString(0) + "|" + r.GetString(1));
        return set;
    }

    private static string? Trim(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}

public sealed record ControlItem(string Kind, string Key, string Label, string? Detail, string? Executable);
public sealed record ControlPendingRecord(long Id, string Kind, string Key, string Label, string? Detail, string Risk, string? Reason,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen, string Status);
public sealed record ControlSummary(bool Enabled, int Baseline, int Pending, int PendingHigh, int Threats, DateTimeOffset? BaselineSince,
    DateTimeOffset? LastScan, bool Elevated);
