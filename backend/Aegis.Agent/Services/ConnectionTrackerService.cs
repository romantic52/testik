using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Aegis.Agent.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Aegis.Agent.Services;

/// <summary>
/// Follows TCP connections over time: classifies them, resolves remote hosts,
/// counts bytes per connection/process when elevated and keeps a journal of
/// every non-loopback connection (opened, last seen, closed) in SQLite.
/// </summary>
public sealed class ConnectionTrackerService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(30);
    private const int RetentionDays = 7;
    private const int MaxLogRows = 200_000;

    private readonly StateDatabase _database;
    private readonly ILogger<ConnectionTrackerService> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, Tracked> _open = new();
    private readonly ConcurrentDictionary<string, (string? Host, DateTimeOffset Expires)> _dns = new();
    private readonly SemaphoreSlim _dnsSlots = new(4, 4);
    private readonly ConcurrentDictionary<int, (string? Name, string? Path)> _processes = new();
    private readonly Dictionary<string, ProcessTraffic> _traffic = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastFlush = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTick = DateTimeOffset.MinValue;

    public ConnectionTrackerService(StateDatabase database, ILogger<ConnectionTrackerService> logger)
    {
        _database = database;
        _logger = logger;
        ByteCountersAvailable = Environment.IsPrivilegedProcess;
    }

    /// <summary>TCP Extended Statistics need an elevated process (service or "Run as administrator").</summary>
    public bool ByteCountersAvailable { get; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        CloseDanglingRows();
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { Tick(); }
            catch (Exception error) { _logger.LogWarning(error, "Connection tracking tick failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = TcpTableReader.Read();
        var listening = rows.Where(r => r.State == "Listen").Select(r => r.LocalPort).ToHashSet();
        var seen = new HashSet<string>();
        var opened = new List<Tracked>();
        var closed = new List<Tracked>();

        lock (_sync)
        {
            var seconds = _lastTick == DateTimeOffset.MinValue ? 0 : (now - _lastTick).TotalSeconds;
            _lastTick = now;
            foreach (var t in _traffic.Values) { t.InRate = 0; t.OutRate = 0; }

            foreach (var row in rows)
            {
                if (row.State == "Listen") continue;
                var key = $"{row.LocalAddress}:{row.LocalPort}>{row.RemoteAddress}:{row.RemotePort}#{row.Pid}";
                seen.Add(key);
                if (!_open.TryGetValue(key, out var item))
                {
                    var (name, path) = ProcessInfo(row.Pid);
                    var direction = ConnectionClassifier.Direction(row.State, row.LocalPort, listening);
                    item = new Tracked
                    {
                        Row = row, OpenedAt = now, Process = name, ProcessPath = path,
                        Scope = ConnectionClassifier.Scope(row.RemoteAddress), Direction = direction,
                        Service = ConnectionClassifier.Service(row.LocalPort, row.RemotePort, direction)
                    };
                    if (ByteCountersAvailable && row.State == "Established")
                        item.Counting = TcpTableReader.EnableByteCounters(row);
                    _open[key] = item;
                    if (item.Scope != ConnectionClassifier.Loopback) opened.Add(item);
                    ResolveLater(row.RemoteAddress, item.Scope);
                }
                item.Row = row;
                item.LastSeen = now;
                if (!item.Counting && ByteCountersAvailable && row.State == "Established")
                    item.Counting = TcpTableReader.EnableByteCounters(row);
                if (item.Counting && TcpTableReader.ReadByteCounters(row) is { } bytes)
                {
                    var dIn = Math.Max(0, bytes.In - (item.BytesIn ?? 0));
                    var dOut = Math.Max(0, bytes.Out - (item.BytesOut ?? 0));
                    item.BytesIn = bytes.In;
                    item.BytesOut = bytes.Out;
                    if (item.Scope != ConnectionClassifier.Loopback && seconds > 0)
                    {
                        var traffic = Traffic(item.Process ?? "PID " + row.Pid);
                        traffic.TotalIn += dIn; traffic.TotalOut += dOut;
                        traffic.InRate += dIn / seconds; traffic.OutRate += dOut / seconds;
                        traffic.LastSeen = now;
                    }
                }
                if (_dns.TryGetValue(row.RemoteAddress, out var dns)) item.RemoteHost = dns.Host;
            }

            foreach (var key in _open.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                var item = _open[key];
                _open.Remove(key);
                if (item.Scope != ConnectionClassifier.Loopback) closed.Add(item);
            }
        }

        Persist(now, opened, closed);
    }

    private ProcessTraffic Traffic(string process)
    {
        if (!_traffic.TryGetValue(process, out var t)) _traffic[process] = t = new ProcessTraffic { Process = process };
        return t;
    }

    private (string? Name, string? Path) ProcessInfo(int pid)
    {
        if (pid <= 0) return (pid == 0 ? "System Idle" : null, null);
        if (_processes.TryGetValue(pid, out var cached)) return cached;
        string? name = null, path = null;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
            try { path = p.MainModule?.FileName; } catch { /* protected or other-user process */ }
        }
        catch { /* exited */ }
        if (_processes.Count > 4096) _processes.Clear();
        _processes[pid] = (name, path);
        return (name, path);
    }

    private void ResolveLater(string address, string scope)
    {
        if (scope is not (ConnectionClassifier.Internet or ConnectionClassifier.Lan or ConnectionClassifier.ProviderNat)) return;
        if (_dns.TryGetValue(address, out var entry) && entry.Expires > DateTimeOffset.UtcNow) return;
        _dns[address] = (entry.Host, DateTimeOffset.UtcNow.AddMinutes(30));
        _ = Task.Run(async () =>
        {
            if (!await _dnsSlots.WaitAsync(0)) { _dns.TryRemove(address, out _); return; }
            try
            {
                var lookup = Dns.GetHostEntryAsync(address);
                var done = await Task.WhenAny(lookup, Task.Delay(2500));
                var host = done == lookup && lookup.IsCompletedSuccessfully ? lookup.Result.HostName : null;
                if (host == address) host = null;
                _dns[address] = (host, DateTimeOffset.UtcNow.AddMinutes(host is null ? 5 : 30));
            }
            catch { _dns[address] = (null, DateTimeOffset.UtcNow.AddMinutes(5)); }
            finally { _dnsSlots.Release(); }
        });
    }

    private void Persist(DateTimeOffset now, List<Tracked> opened, List<Tracked> closed)
    {
        var flush = now - _lastFlush >= FlushEvery;
        if (opened.Count == 0 && closed.Count == 0 && !flush) return;
        List<Tracked> live;
        lock (_sync) live = flush ? _open.Values.Where(t => t.LogId > 0).ToList() : new();

        using var connection = _database.OpenConnection();
        using var tx = connection.BeginTransaction();
        foreach (var item in opened)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO connection_log(opened_at,last_seen,pid,process,process_path,local_address,local_port,
                    remote_address,remote_port,remote_host,service,scope,direction,state,bytes_in,bytes_out)
                VALUES($o,$o,$pid,$p,$pp,$la,$lp,$ra,$rp,$rh,$s,$sc,$d,$st,$bi,$bo) RETURNING id;
                """;
            Bind(cmd, item, now);
            cmd.Parameters.AddWithValue("$o", item.OpenedAt.ToString("O"));
            item.LogId = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        foreach (var item in live.Concat(closed.Where(c => c.LogId > 0)))
        {
            var isClosed = closed.Contains(item);
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE connection_log SET last_seen=$ls, closed_at=$c, state=$st, remote_host=COALESCE($rh, remote_host),
                    bytes_in=$bi, bytes_out=$bo WHERE id=$id;
                """;
            cmd.Parameters.AddWithValue("$ls", item.LastSeen.ToString("O"));
            cmd.Parameters.AddWithValue("$c", isClosed ? now.ToString("O") : DBNull.Value);
            cmd.Parameters.AddWithValue("$st", isClosed ? "Closed" : item.Row.State);
            cmd.Parameters.AddWithValue("$rh", (object?)item.RemoteHost ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bi", (object?)item.BytesIn ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bo", (object?)item.BytesOut ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", item.LogId);
            cmd.ExecuteNonQuery();
        }
        if (now - _lastPrune > TimeSpan.FromHours(1))
        {
            using var prune = connection.CreateCommand();
            prune.Transaction = tx;
            prune.CommandText = """
                DELETE FROM connection_log WHERE opened_at < $cutoff;
                DELETE FROM connection_log WHERE id <= (SELECT id FROM connection_log ORDER BY id DESC LIMIT 1 OFFSET $max);
                """;
            prune.Parameters.AddWithValue("$cutoff", now.AddDays(-RetentionDays).ToString("O"));
            prune.Parameters.AddWithValue("$max", MaxLogRows);
            prune.ExecuteNonQuery();
            _lastPrune = now;
        }
        tx.Commit();
        if (flush) _lastFlush = now;
    }

    private static void Bind(SqliteCommand cmd, Tracked item, DateTimeOffset now)
    {
        cmd.Parameters.AddWithValue("$pid", item.Row.Pid);
        cmd.Parameters.AddWithValue("$p", (object?)item.Process ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pp", (object?)item.ProcessPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$la", item.Row.LocalAddress);
        cmd.Parameters.AddWithValue("$lp", item.Row.LocalPort);
        cmd.Parameters.AddWithValue("$ra", item.Row.RemoteAddress);
        cmd.Parameters.AddWithValue("$rp", item.Row.RemotePort);
        cmd.Parameters.AddWithValue("$rh", (object?)item.RemoteHost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", (object?)item.Service ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sc", item.Scope);
        cmd.Parameters.AddWithValue("$d", item.Direction);
        cmd.Parameters.AddWithValue("$st", item.Row.State);
        cmd.Parameters.AddWithValue("$bi", (object?)item.BytesIn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bo", (object?)item.BytesOut ?? DBNull.Value);
    }

    /// <summary>Rows left open by a crash or stop are closed at their last observation.</summary>
    private void CloseDanglingRows()
    {
        try
        {
            using var connection = _database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE connection_log SET closed_at = last_seen, state = 'Closed' WHERE closed_at IS NULL;";
            cmd.ExecuteNonQuery();
        }
        catch (Exception error) { _logger.LogWarning(error, "Could not close dangling connection rows"); }
    }

    public IReadOnlyList<LiveConnection> Live()
    {
        lock (_sync)
        {
            return _open.Values
                .OrderBy(t => t.Scope == ConnectionClassifier.Loopback)
                .ThenByDescending(t => t.Row.State == "Established")
                .ThenByDescending(t => (t.BytesIn ?? 0) + (t.BytesOut ?? 0))
                .ThenBy(t => t.Process)
                .Select(t => new LiveConnection(
                    t.Row.LocalAddress, t.Row.LocalPort, t.Row.RemoteAddress, t.Row.RemotePort, t.RemoteHost,
                    t.Row.State, t.Row.Pid, t.Process, t.ProcessPath, t.Service, t.Scope, t.Direction,
                    t.OpenedAt, t.BytesIn, t.BytesOut))
                .ToList();
        }
    }

    public IReadOnlyList<ProcessTrafficSnapshot> TrafficByProcess()
    {
        lock (_sync)
        {
            var counts = _open.Values.Where(t => t.Scope != ConnectionClassifier.Loopback)
                .GroupBy(t => t.Process ?? "PID " + t.Row.Pid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (Total: g.Count(), Internet: g.Count(t => t.Scope == ConnectionClassifier.Internet)), StringComparer.OrdinalIgnoreCase);
            var names = counts.Keys.Union(_traffic.Keys, StringComparer.OrdinalIgnoreCase);
            return names.Select(n =>
                {
                    _traffic.TryGetValue(n, out var t);
                    counts.TryGetValue(n, out var c);
                    return new ProcessTrafficSnapshot(n, c.Total, c.Internet,
                        Math.Round(t?.InRate ?? 0), Math.Round(t?.OutRate ?? 0), t?.TotalIn ?? 0, t?.TotalOut ?? 0);
                })
                .OrderByDescending(p => p.ReceiveBytesPerSecond + p.SendBytesPerSecond)
                .ThenByDescending(p => p.TotalReceived + p.TotalSent)
                .ThenByDescending(p => p.Connections)
                .Take(100)
                .ToList();
        }
    }

    public async Task<IReadOnlyList<ConnectionLogEntry>> JournalAsync(
        string? search, string? scope, int hours, int limit, CancellationToken token)
    {
        await using var connection = _database.OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT opened_at,last_seen,closed_at,pid,process,process_path,local_address,local_port,remote_address,
                   remote_port,remote_host,service,scope,direction,state,bytes_in,bytes_out
            FROM connection_log
            WHERE opened_at >= $since
              AND ($scope IS NULL OR scope = $scope)
              AND ($q IS NULL OR process LIKE $q OR remote_address LIKE $q OR remote_host LIKE $q
                   OR service LIKE $q OR CAST(remote_port AS TEXT) = $raw)
            ORDER BY id DESC LIMIT $limit;
            """;
        var q = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        cmd.Parameters.AddWithValue("$since", DateTimeOffset.UtcNow.AddHours(-Math.Clamp(hours, 1, 24 * RetentionDays)).ToString("O"));
        cmd.Parameters.AddWithValue("$scope", string.IsNullOrWhiteSpace(scope) ? DBNull.Value : scope);
        cmd.Parameters.AddWithValue("$q", q is null ? DBNull.Value : "%" + q + "%");
        cmd.Parameters.AddWithValue("$raw", (object?)q ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 20_000));
        var result = new List<ConnectionLogEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            string? S(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            long? L(int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);
            result.Add(new ConnectionLogEntry(
                DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                S(2) is { } c ? DateTimeOffset.Parse(c, CultureInfo.InvariantCulture) : null,
                (int?)L(3), S(4), S(5), reader.GetString(6), reader.GetInt32(7), reader.GetString(8), reader.GetInt32(9),
                S(10), S(11), reader.GetString(12), reader.GetString(13), reader.GetString(14), L(15), L(16)));
        }
        return result;
    }

    private sealed class Tracked
    {
        public required TcpTableReader.Row Row;
        public DateTimeOffset OpenedAt;
        public DateTimeOffset LastSeen;
        public string? Process, ProcessPath, RemoteHost, Service;
        public required string Scope, Direction;
        public bool Counting;
        public long? BytesIn, BytesOut;
        public long LogId;
    }

    private sealed class ProcessTraffic
    {
        public required string Process;
        public long TotalIn, TotalOut;
        public double InRate, OutRate;
        public DateTimeOffset LastSeen;
    }
}

public sealed record LiveConnection(
    string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort, string? RemoteHost,
    string State, int ProcessId, string? ProcessName, string? ProcessPath, string? Service,
    string Scope, string Direction, DateTimeOffset FirstSeen, long? BytesIn, long? BytesOut);

public sealed record ProcessTrafficSnapshot(
    string Process, int Connections, int InternetConnections,
    double ReceiveBytesPerSecond, double SendBytesPerSecond, long TotalReceived, long TotalSent);

public sealed record ConnectionLogEntry(
    DateTimeOffset OpenedAt, DateTimeOffset LastSeen, DateTimeOffset? ClosedAt, int? ProcessId,
    string? ProcessName, string? ProcessPath, string LocalAddress, int LocalPort, string RemoteAddress,
    int RemotePort, string? RemoteHost, string? Service, string Scope, string Direction, string State,
    long? BytesIn, long? BytesOut);
