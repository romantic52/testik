using System.Diagnostics;
using System.Management;

namespace Aegis.Agent.Services;

/// <summary>
/// Microsoft Defender bridge. AEGIS does not ship its own signature engine — it
/// reads Defender's protection status and threat history and asks Defender (via
/// MpCmdRun.exe) to scan a file/folder or run a quick scan. A custom AV engine
/// would be a false sense of security; Defender is the real one on this PC.
/// </summary>
public sealed class DefenderService
{
    private static readonly string Scope = @"root\Microsoft\Windows\Defender";
    private readonly SemaphoreSlim _scan = new(1, 1);

    public DefenderStatus GetStatus()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM MSFT_MpComputerStatus");
            foreach (ManagementObject item in searcher.Get())
                using (item)
                {
                    bool B(string n) => item[n] is bool b && b;
                    string? S(string n) => item[n]?.ToString();
                    DateTimeOffset? D(string n) => item[n] is string s && ManagementDateTimeConverter.ToDateTime(s) is { } dt ? new DateTimeOffset(dt) : null;
                    return new DefenderStatus(true, null,
                        B("AntivirusEnabled"), B("RealTimeProtectionEnabled"), B("IsTamperProtected"),
                        S("AntivirusSignatureVersion"), D("AntivirusSignatureLastUpdated"),
                        S("AMProductVersion"),
                        D("QuickScanEndTime"), D("FullScanEndTime"));
                }
            return new DefenderStatus(false, "Defender не вернул статус (возможно, заменён сторонним антивирусом).", false, false, false, null, null, null, null, null);
        }
        catch (ManagementException error)
        {
            return new DefenderStatus(false, "Defender недоступен: " + error.Message, false, false, false, null, null, null, null, null);
        }
    }

    public IReadOnlyList<DefenderDetection> GetDetections(int limit)
    {
        var result = new List<DefenderDetection>();
        var names = ThreatNames();
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM MSFT_MpThreatDetection");
            foreach (ManagementObject item in searcher.Get())
                using (item)
                {
                    string? S(string n) => item[n]?.ToString();
                    DateTimeOffset? D(string n) => item[n] is string s && ManagementDateTimeConverter.ToDateTime(s) is { } dt ? new DateTimeOffset(dt) : null;
                    var resources = item["Resources"] as string[];
                    result.Add(new DefenderDetection(
                        S("ThreatID"), D("InitialDetectionTime") ?? DateTimeOffset.MinValue,
                        S("ThreatID") is { } id && names.TryGetValue(id, out var name) ? name : ThreatName(S("ThreatID")),ActionName((uint?)Convert.ToUInt32(item["CleaningActionID"] ?? 0u)),
                        (uint?)Convert.ToUInt32(item["ThreatStatusID"] ?? 0u) == 2, resources is { Length: > 0 } ? string.Join("; ", resources) : null));
                }
        }
        catch { /* threat history can require elevation */ }
        return result.OrderByDescending(d => d.DetectedAt).Take(Math.Clamp(limit, 1, 500)).ToList();
    }

    public bool Available => OperatingSystem.IsWindows() && File.Exists(MpCmdRunPath());

    public async Task<DefenderScanResult> ScanAsync(string? path, CancellationToken token)
    {
        if (!await _scan.WaitAsync(0, token))
            throw new InvalidOperationException("Проверка Defender уже выполняется.");
        try
        {
            var exe = MpCmdRunPath();
            if (!File.Exists(exe)) throw new InvalidOperationException("MpCmdRun.exe не найден: Defender отключён или заменён.");
            string args;
            if (string.IsNullOrWhiteSpace(path))
                args = "-Scan -ScanType 1"; // quick scan
            else
            {
                var full = Path.GetFullPath(path);
                if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException("Путь не найден: " + full);
                args = $"-Scan -ScanType 3 -File \"{full}\"";
            }
            var start = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить MpCmdRun.exe.");
            using var reg = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(token));
            var output = (stdout.Result + "\n" + stderr.Result).Trim();
            // MpCmdRun exit code 2 = threats found and handled, 0 = clean.
            return new DefenderScanResult(process.ExitCode == 0, process.ExitCode == 2, process.ExitCode, string.IsNullOrWhiteSpace(path) ? "Быстрая проверка" : path!, output.Length > 4000 ? output[^4000..] : output);
        }
        finally { _scan.Release(); }
    }

    private static string MpCmdRunPath()
    {
        var platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        if (File.Exists(platform)) return platform;
        // Platform-versioned path (modern Defender).
        var root = Environment.ExpandEnvironmentVariables(@"%ProgramData%\Microsoft\Windows Defender\Platform");
        if (Directory.Exists(root))
        {
            var newest = new DirectoryInfo(root).GetDirectories().OrderByDescending(d => d.Name).FirstOrDefault();
            var candidate = newest is null ? null : Path.Combine(newest.FullName, "MpCmdRun.exe");
            if (candidate is not null && File.Exists(candidate)) return candidate;
        }
        return platform;
    }

    /// <summary>ThreatID → readable name ("Trojan:Win32/…") from MSFT_MpThreat.</summary>
    private static Dictionary<string, string> ThreatNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT ThreatID, ThreatName FROM MSFT_MpThreat");
            foreach (ManagementObject item in searcher.Get())
                using (item)
                    if (item["ThreatID"]?.ToString() is { } id && item["ThreatName"]?.ToString() is { Length: > 0 } name)
                        names[id] = name;
        }
        catch { /* can require elevation */ }
        return names;
    }

    private static string ThreatName(string? id) => string.IsNullOrWhiteSpace(id) ? "Обнаружение" : "Угроза " + id;
    private static string ActionName(uint? id) => id switch
    {
        2 => "в карантине", 3 => "удалено", 6 => "разрешено", 8 => "действие пользователя", 9 => "без действия", 10 => "заблокировано", _ => "обработано"
    };
}

public sealed record DefenderStatus(bool Available, string? Error, bool AntivirusEnabled, bool RealTimeProtection, bool TamperProtection,
    string? SignatureVersion, DateTimeOffset? SignatureUpdated, string? ProductVersion, DateTimeOffset? LastQuickScan, DateTimeOffset? LastFullScan);
public sealed record DefenderDetection(string? ThreatId, DateTimeOffset DetectedAt, string Name, string Action, bool Active, string? Resource);
public sealed record DefenderScanResult(bool Clean, bool ThreatsFound, int ExitCode, string Target, string Output);
