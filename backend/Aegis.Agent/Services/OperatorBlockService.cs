using System.Diagnostics;
using System.Globalization;
using Aegis.Agent.Infrastructure;

namespace Aegis.Agent.Services;

/// <summary>
/// Blocking actions that run only when the operator presses a button and confirms:
/// stop a program's processes, or add a Windows Firewall rule that blocks a program
/// (removable later). Every action is written to the audit log. Nothing here runs automatically.
/// </summary>
public sealed class OperatorBlockService
{
    public const string IntentHeader = "X-AEGIS-Block-Intent";
    public const string IntentValue = "operator-confirmed";
    private const string RulePrefix = "AEGIS block ";

    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "csrss", "wininit", "winlogon", "services", "lsass", "smss", "svchost", "explorer", "dwm", "fontdrvhost", "aegis.agent"
    };

    private readonly StateDatabase _database;
    private readonly IncidentStoreService _incidents;

    public OperatorBlockService(StateDatabase database, IncidentStoreService incidents)
    {
        _database = database;
        _incidents = incidents;
    }

    public static bool IsProtected(string path) => Protected.Contains(Path.GetFileNameWithoutExtension(path))
        || path.StartsWith(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);

    /// <summary>Stops every running process started from <paramref name="path"/>. Returns how many were stopped.</summary>
    public async Task<int> StopProgramAsync(string path, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("Нужен полный путь к программе.");
        if (IsProtected(path)) throw new InvalidOperationException("Это системный процесс или сам AEGIS — его остановка нарушит работу Windows.");
        path = LongPath(path);
        var stopped = 0;
        var denied = 0;
        foreach (var process in Process.GetProcesses())
            using (process)
            {
                string? file = null;
                try { file = process.MainModule?.FileName; } catch { }
                if (file is null || !string.Equals(LongPath(file), path, StringComparison.OrdinalIgnoreCase)) continue;
                try { process.Kill(entireProcessTree: false); stopped++; }
                catch { denied++; }
            }
        if (stopped == 0 && denied > 0) throw new UnauthorizedAccessException("Нет прав остановить процесс: он запущен другим пользователем или системой. Нужна служба AEGIS.");
        await Audit("control.process.stopped", path, $"Operator stopped {stopped} process(es)", token);
        return stopped;
    }

    public async Task<FirewallRuleRecord> BlockProgramAsync(string path, string note, CancellationToken token)
    {
        if (!Environment.IsPrivilegedProcess)
            throw new UnauthorizedAccessException("Правила брандмауэра Windows может создавать только AEGIS со службой (права администратора): install-service.bat.");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new ArgumentException("Файл программы не найден.");
        if (IsProtected(path)) throw new InvalidOperationException("Системный процесс или сам AEGIS блокировать нельзя.");
        var name = RulePrefix + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " " + Path.GetFileName(path);
        foreach (var dir in new[] { "out", "in" })
            await Netsh($"advfirewall firewall add rule name=\"{name}\" dir={dir} action=block program=\"{path}\" enable=yes", token);
        await using (var connection = _database.OpenConnection())
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO firewall_rules(created_at,rule_name,scope,target,direction,note,active) VALUES($t,$n,'program',$p,'both',$note,1);";
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$note", note.Length > 300 ? note[..300] : note);
            await cmd.ExecuteNonQueryAsync(token);
        }
        await Audit("control.firewall.blocked", path, "Operator added Windows Firewall rule " + name, token);
        return (await RulesAsync(token)).First(r => r.RuleName == name);
    }

    public async Task<bool> UnblockAsync(long id, CancellationToken token)
    {
        var rule = (await RulesAsync(token)).FirstOrDefault(r => r.Id == id && r.Active);
        if (rule is null) return false;
        if (!Environment.IsPrivilegedProcess)
            throw new UnauthorizedAccessException("Снять правило брандмауэра может только AEGIS со службой (права администратора).");
        await Netsh($"advfirewall firewall delete rule name=\"{rule.RuleName}\"", token);
        await using (var connection = _database.OpenConnection())
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE firewall_rules SET active=0 WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(token);
        }
        await Audit("control.firewall.removed", rule.Target, "Operator removed Windows Firewall rule " + rule.RuleName, token);
        return true;
    }

    public async Task<IReadOnlyList<FirewallRuleRecord>> RulesAsync(CancellationToken token)
    {
        await using var connection = _database.OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id,created_at,rule_name,target,note,active FROM firewall_rules ORDER BY id DESC LIMIT 200;";
        var list = new List<FirewallRuleRecord>();
        await using var r = await cmd.ExecuteReaderAsync(token);
        while (await r.ReadAsync(token))
            list.Add(new FirewallRuleRecord(r.GetInt64(0), DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5) == 1));
        return list;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetLongPathName(string shortPath, System.Text.StringBuilder longPath, uint size);

    /// <summary>8.3 names (C:\Users\ROMANT~1\…) and long names point to the same file; compare in long form.</summary>
    private static string LongPath(string path)
    {
        if (!path.Contains('~')) return path;
        var buffer = new System.Text.StringBuilder(1024);
        var length = GetLongPathName(path, buffer, (uint)buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? buffer.ToString() : path;
    }

    private static async Task Netsh(string args, CancellationToken token)
    {
        var start = new ProcessStartInfo("netsh.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(start) ?? throw new InvalidOperationException("netsh не запустился.");
        var output = p.StandardOutput.ReadToEndAsync(token);
        var error = p.StandardError.ReadToEndAsync(token);
        await Task.WhenAll(output, error, p.WaitForExitAsync(token));
        if (p.ExitCode != 0) throw new InvalidOperationException("netsh: " + (output.Result + " " + error.Result).Trim());
    }

    private Task Audit(string type, string subject, string detail, CancellationToken token) =>
        _incidents.AppendAuditAsync(new AuditRecord(DateTimeOffset.UtcNow, type, subject, detail, "AEGIS local operator"), token);
}

public sealed record FirewallRuleRecord(long Id, DateTimeOffset CreatedAt, string RuleName, string Target, string? Note, bool Active);
