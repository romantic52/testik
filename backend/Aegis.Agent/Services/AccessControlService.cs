using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Aegis.Agent.Infrastructure;

namespace Aegis.Agent.Services;

/// <summary>
/// Access control from real sources only:
///  * Windows sign-ins on this PC (Security log: 4624/4625/4634/4647/4740/4800/4801/4778/4779);
///  * events pushed by a physical access controller to POST /api/v1/access/events.
/// Brute-force (5+ failed sign-ins in 5 minutes) and account lockouts raise incidents.
/// </summary>
public sealed class AccessControlService : BackgroundService
{
    private static readonly int[] EventIds = [4624, 4625, 4634, 4647, 4740, 4800, 4801, 4778, 4779];
    private static readonly HashSet<int> InteractiveLogonTypes = [2, 7, 10, 11];
    private const string IntakeSettingKey = "access.intake";

    private readonly StateDatabase _database;
    private readonly IncidentStoreService _incidents;
    private readonly ILogger<AccessControlService> _logger;
    private long _watermark;
    private DateTimeOffset _lastBruteForceIncident = DateTimeOffset.MinValue;

    public AccessControlService(StateDatabase database, IncidentStoreService incidents, ILogger<AccessControlService> logger)
    {
        _database = database;
        _incidents = incidents;
        _logger = logger;
    }

    public string WindowsLogStatus { get; private set; } = "starting";
    public string? WindowsLogError { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First pass: last 24 hours, so the section is not empty after a restart.
        _watermark = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                var fresh = ReadSecurityLog(_watermark == 0 ? DateTime.UtcNow.AddHours(-24) : null);
                WindowsLogStatus = "online";
                WindowsLogError = null;
                if (fresh.Count > 0)
                {
                    await StoreAsync(fresh, stoppingToken);
                    _watermark = Math.Max(_watermark, fresh.Max(e => e.RecordId));
                    await RaiseIncidentsAsync(fresh, stoppingToken);
                }
                if (_watermark == 0) _watermark = NewestRecordId();
            }
            catch (UnauthorizedAccessException)
            {
                WindowsLogStatus = "no_permission";
                WindowsLogError = "Журнал безопасности Windows читается только с правами администратора: установи AEGIS службой (install-service.bat) или запусти от администратора.";
            }
            catch (EventLogException error)
            {
                WindowsLogStatus = "unavailable";
                WindowsLogError = error.Message;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                WindowsLogStatus = "error";
                WindowsLogError = error.Message;
                _logger.LogWarning(error, "Access control polling failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private static long NewestRecordId()
    {
        using var reader = new EventLogReader(new EventLogQuery("Security", PathType.LogName) { ReverseDirection = true });
        using var newest = reader.ReadEvent();
        return Math.Max(1, newest?.RecordId ?? 1);
    }

    private List<WindowsAccessEvent> ReadSecurityLog(DateTime? since)
    {
        var ids = string.Join(" or ", EventIds.Select(id => "EventID=" + id));
        var filter = since is { } s
            ? $"*[System[({ids}) and TimeCreated[@SystemTime>='{s:yyyy-MM-ddTHH:mm:ss.fffZ}']]]"
            : $"*[System[({ids}) and EventRecordID>{_watermark}]]";
        var query = new EventLogQuery("Security", PathType.LogName, filter) { ReverseDirection = false, TolerateQueryErrors = true };
        using var reader = new EventLogReader(query);
        var result = new List<WindowsAccessEvent>();
        while (result.Count < 2000 && reader.ReadEvent() is { } record)
        {
            using (record)
            {
                var parsed = Parse(record);
                if (parsed is not null) result.Add(parsed);
                else if (record.RecordId is long id) _watermark = Math.Max(_watermark, id);
            }
        }
        return result;
    }

    private static WindowsAccessEvent? Parse(EventRecord record)
    {
        string P(int index) => record.Properties.Count > index ? record.Properties[index].Value?.ToString() ?? "" : "";
        var id = record.Id;
        var time = record.TimeCreated is DateTime t ? new DateTimeOffset(t.ToUniversalTime(), TimeSpan.Zero) : DateTimeOffset.UtcNow;
        var recordId = record.RecordId ?? 0;
        string user, domain, kind, outcome, detail, origin = "";
        switch (id)
        {
            case 4624:
                user = P(5); domain = P(6);
                if (!int.TryParse(P(8), out var type) || !InteractiveLogonTypes.Contains(type)) return null;
                kind = "logon"; outcome = "success";
                origin = Origin(P(18), P(11));
                detail = $"Вход ({LogonType(type)})" + (P(17) is { Length: > 0 } proc ? $", процесс {Path.GetFileName(proc)}" : "");
                break;
            case 4625:
                user = P(5); domain = P(6);
                int.TryParse(P(10), out var failType);
                kind = "logon"; outcome = "failure";
                origin = Origin(P(19), P(13));
                detail = $"Неудачный вход ({LogonType(failType)}): {FailureReason(P(9), P(7))}";
                break;
            case 4634 or 4647:
                user = P(1); domain = P(2);
                if (id == 4634 && (!int.TryParse(P(4), out var offType) || !InteractiveLogonTypes.Contains(offType))) return null;
                kind = "logoff"; outcome = "success"; detail = "Выход из системы";
                break;
            case 4740:
                user = P(0); domain = P(5);
                kind = "lockout"; outcome = "failure"; origin = P(1); detail = "Учётная запись заблокирована после неудачных входов";
                break;
            case 4800 or 4801:
                user = P(1); domain = P(2);
                kind = id == 4800 ? "lock" : "unlock"; outcome = "success";
                detail = id == 4800 ? "Рабочая станция заблокирована" : "Рабочая станция разблокирована";
                break;
            case 4778 or 4779:
                user = P(0); domain = P(1);
                kind = id == 4778 ? "rdp_reconnect" : "rdp_disconnect"; outcome = "success";
                origin = Origin(P(5), P(4)); detail = id == 4778 ? "Удалённый сеанс подключён" : "Удалённый сеанс отключён";
                break;
            default:
                return null;
        }
        if (IsServiceAccount(user)) return null;
        return new WindowsAccessEvent(recordId, time, id, kind, outcome, string.IsNullOrEmpty(domain) ? user : domain + "\\" + user, detail, origin);
    }

    private static bool IsServiceAccount(string user) =>
        string.IsNullOrWhiteSpace(user) || user == "-" || user.EndsWith('$')
        || user.StartsWith("DWM-", StringComparison.OrdinalIgnoreCase) || user.StartsWith("UMFD-", StringComparison.OrdinalIgnoreCase)
        || user.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) || user.Equals("LOCAL SERVICE", StringComparison.OrdinalIgnoreCase)
        || user.Equals("NETWORK SERVICE", StringComparison.OrdinalIgnoreCase) || user.Equals("ANONYMOUS LOGON", StringComparison.OrdinalIgnoreCase);

    private static string Origin(string ip, string workstation)
    {
        ip = ip is "-" or "::1" or "127.0.0.1" ? "" : ip;
        workstation = workstation == "-" ? "" : workstation;
        return string.Join(" / ", new[] { workstation, ip }.Where(s => s.Length > 0));
    }

    public static string LogonType(int type) => type switch
    {
        2 => "за компьютером", 3 => "по сети", 4 => "пакетный", 5 => "служба", 7 => "разблокировка",
        8 => "по сети, открытый пароль", 9 => "новые учётные данные", 10 => "удалённый рабочий стол",
        11 => "кэшированный", _ => "тип " + type
    };

    public static string FailureReason(string subStatus, string status)
    {
        var code = (subStatus is { Length: > 0 } && subStatus != "0x0" && subStatus != "0" ? subStatus : status).ToLowerInvariant();
        // The event log hands NTSTATUS over as a number (often signed: -1073741718 = 0xC000006A).
        if (long.TryParse(code, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var numeric))
            code = "0x" + unchecked((uint)numeric).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        return code switch
        {
            "0xc000006a" => "неверный пароль",
            "0xc0000064" => "нет такой учётной записи",
            "0xc0000234" => "учётная запись заблокирована",
            "0xc0000072" => "учётная запись отключена",
            "0xc000006f" => "вход вне разрешённого времени",
            "0xc0000070" => "вход с этого компьютера запрещён",
            "0xc0000071" => "срок действия пароля истёк",
            "0xc0000193" => "срок действия учётной записи истёк",
            "0xc0000133" => "рассинхронизация времени",
            "0xc000015b" => "этот тип входа не разрешён",
            _ => "код " + code
        };
    }

    private async Task StoreAsync(IEnumerable<WindowsAccessEvent> items, CancellationToken token)
    {
        await using var connection = _database.OpenConnection();
        await using var tx = connection.BeginTransaction();
        foreach (var e in items)
            await InsertAsync(connection, (Microsoft.Data.Sqlite.SqliteTransaction)tx, e.Timestamp, "windows", e.Kind, e.Outcome, e.Subject, e.Detail, e.Origin, "win-sec:" + e.RecordId, token);
        await tx.CommitAsync(token);
    }

    private static async Task InsertAsync(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction? tx,
        DateTimeOffset time, string source, string kind, string outcome, string? subject, string? detail, string? origin, string key, CancellationToken token)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO access_events(timestamp,source,kind,outcome,subject,detail,origin,record_key)
            VALUES($t,$s,$k,$o,$u,$d,$g,$r) ON CONFLICT(record_key) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$t", time.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$s", source);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$o", outcome);
        cmd.Parameters.AddWithValue("$u", (object?)subject ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", (object?)detail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$g", (object?)origin ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$r", key);
        await cmd.ExecuteNonQueryAsync(token);
    }

    private async Task RaiseIncidentsAsync(List<WindowsAccessEvent> fresh, CancellationToken token)
    {
        foreach (var lockout in fresh.Where(e => e.Kind == "lockout"))
            await _incidents.UpsertAsync(new IncidentRecord(
                $"ACCESS-LOCK-{lockout.RecordId}", "Блокировка учётной записи " + lockout.Subject, "Windows Security / " + Environment.MachineName,
                "Высокий", "Авто", lockout.Detail + (lockout.Origin.Length > 0 ? ". Источник: " + lockout.Origin : ""),
                [$"{lockout.Timestamp.LocalDateTime:HH:mm:ss} — событие 4740"], true, false, "access:lockout:" + lockout.RecordId, lockout.Timestamp), token);

        var now = DateTimeOffset.UtcNow;
        var failures = await CountAsync("failure", "logon", now.AddMinutes(-5), token);
        if (failures >= 5 && now - _lastBruteForceIncident > TimeSpan.FromMinutes(10))
        {
            _lastBruteForceIncident = now;
            var recent = fresh.Where(e => e.Outcome == "failure" && e.Kind == "logon").TakeLast(5).ToList();
            await _incidents.UpsertAsync(new IncidentRecord(
                $"ACCESS-BRUTE-{now:yyyyMMddHHmm}", "Подбор пароля: " + failures + " неудачных входов за 5 минут", "Windows Security / " + Environment.MachineName,
                "Критический", "Авто",
                "Учётные записи: " + string.Join(", ", recent.Select(e => e.Subject).Distinct()) +
                (recent.Any(e => e.Origin.Length > 0) ? ". Источники: " + string.Join(", ", recent.Select(e => e.Origin).Where(o => o.Length > 0).Distinct()) : ""),
                recent.Select(e => $"{e.Timestamp.LocalDateTime:HH:mm:ss} — {e.Subject}: {e.Detail}").ToArray(),
                true, false, "access:bruteforce", now), token);
            _logger.LogWarning("Brute-force sign-in pattern: {Count} failures in 5 minutes", failures);
        }
    }

    private async Task<int> CountAsync(string outcome, string kind, DateTimeOffset since, CancellationToken token)
    {
        await using var connection = _database.OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM access_events WHERE outcome=$o AND kind=$k AND timestamp >= $s;";
        cmd.Parameters.AddWithValue("$o", outcome);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$s", since.ToString("O"));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<AccessEventRecord>> ListAsync(string? source, int limit, CancellationToken token)
    {
        await using var connection = _database.OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT timestamp,source,kind,outcome,subject,detail,origin FROM access_events
            WHERE ($s IS NULL OR source=$s) ORDER BY timestamp DESC, id DESC LIMIT $l;
            """;
        cmd.Parameters.AddWithValue("$s", string.IsNullOrWhiteSpace(source) ? DBNull.Value : source);
        cmd.Parameters.AddWithValue("$l", Math.Clamp(limit, 1, 2000));
        var list = new List<AccessEventRecord>();
        await using var r = await cmd.ExecuteReaderAsync(token);
        while (await r.ReadAsync(token))
            list.Add(new AccessEventRecord(DateTimeOffset.Parse(r.GetString(0), CultureInfo.InvariantCulture), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
        return list;
    }

    public async Task<AccessSummary> SummaryAsync(CancellationToken token)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        await using var connection = _database.OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
              sum(kind='logon' AND outcome='success'), sum(kind='logon' AND outcome='failure'),
              sum(kind='lockout'), sum(source='controller'), max(CASE WHEN source='controller' THEN timestamp END)
            FROM access_events WHERE timestamp >= $s;
            """;
        cmd.Parameters.AddWithValue("$s", since.ToString("O"));
        await using var r = await cmd.ExecuteReaderAsync(token);
        await r.ReadAsync(token);
        int I(int i) => r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture);
        var lastController = r.IsDBNull(4) ? (DateTimeOffset?)null : DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture);
        return new AccessSummary(WindowsLogStatus, WindowsLogError, I(0), I(1), I(2), I(3), lastController);
    }

    /// <summary>Token a local access-controller bridge must send in X-AEGIS-Access-Token.</summary>
    public async Task<string> IntakeTokenAsync(CancellationToken token)
    {
        await using var connection = _database.OpenConnection();
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT value_json FROM settings WHERE key=$k;";
        read.Parameters.AddWithValue("$k", IntakeSettingKey);
        if (await read.ExecuteScalarAsync(token) is string json && JsonSerializer.Deserialize<string>(json) is { Length: >= 32 } existing)
            return existing;
        var created = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        await using var write = connection.CreateCommand();
        write.CommandText = "INSERT INTO settings(key,value_json,updated_at) VALUES($k,$v,$t) ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json, updated_at=excluded.updated_at;";
        write.Parameters.AddWithValue("$k", IntakeSettingKey);
        write.Parameters.AddWithValue("$v", JsonSerializer.Serialize(created));
        write.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        await write.ExecuteNonQueryAsync(token);
        return created;
    }

    public async Task<bool> AcceptControllerEventAsync(ControllerAccessEvent e, CancellationToken token)
    {
        var outcome = e.Granted ? "success" : "failure";
        var time = e.Timestamp ?? DateTimeOffset.UtcNow;
        var key = "ctl:" + (string.IsNullOrWhiteSpace(e.EventId) ? Guid.NewGuid().ToString("N") : e.EventId.Trim());
        await using var connection = _database.OpenConnection();
        await InsertAsync(connection, null, time, "controller", "door", outcome,
            Trim(e.Person, 120), Trim((e.Granted ? "Проход разрешён" : "Проход запрещён") + (string.IsNullOrWhiteSpace(e.Reason) ? "" : ": " + e.Reason), 300),
            Trim(e.Door, 120), Trim(key, 160)!, token);
        if (!e.Granted && await CountControllerDenialsAsync(e.Door, token) >= 3)
            await _incidents.UpsertAsync(new IncidentRecord(
                $"ACCESS-DOOR-{DateTimeOffset.UtcNow:yyyyMMddHHmm}", "Повторные отказы в проходе: " + (e.Door ?? "точка доступа"),
                "СКУД / " + (e.Door ?? "контроллер"), "Высокий", "Авто",
                "Три и более отказа в проходе за 5 минут. Последний: " + (e.Person ?? "неизвестно") + (string.IsNullOrWhiteSpace(e.Reason) ? "" : " (" + e.Reason + ")"),
                [$"{time.LocalDateTime:HH:mm:ss} — отказ: {e.Person}"], true, false, "access:door:" + e.Door, DateTimeOffset.UtcNow), token);
        return true;
    }

    private async Task<int> CountControllerDenialsAsync(string? door, CancellationToken token)
    {
        await using var connection = _database.OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM access_events WHERE source='controller' AND outcome='failure' AND ifnull(origin,'')=ifnull($d,'') AND timestamp >= $s;";
        cmd.Parameters.AddWithValue("$d", (object?)door ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }

    private static string? Trim(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length > max ? value.Trim()[..max] : value.Trim();

    private sealed record WindowsAccessEvent(long RecordId, DateTimeOffset Timestamp, int EventId, string Kind, string Outcome, string Subject, string Detail, string Origin);
}

public sealed record AccessEventRecord(DateTimeOffset Timestamp, string Source, string Kind, string Outcome, string? Subject, string? Detail, string? Origin);

public sealed record AccessSummary(string WindowsLog, string? WindowsLogError, int SuccessfulLogons24h, int FailedLogons24h, int Lockouts24h, int ControllerEvents24h, DateTimeOffset? LastControllerEvent);

public sealed record ControllerAccessEvent(string? EventId, DateTimeOffset? Timestamp, string? Door, string? Person, bool Granted, string? Reason);
