using System.Diagnostics;

namespace Aegis.Agent.Services;

public sealed class ProcessMonitorService
{
    private readonly object _sync = new();
    private readonly Dictionary<int, ProcessSample> _previous = new();

    public IReadOnlyList<ProcessSnapshot> GetProcesses(int limit)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            var result = new List<ProcessSnapshot>();
            var alive = new HashSet<int>();

            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        alive.Add(process.Id);

                        var cpuTime = process.TotalProcessorTime;
                        var cpuPercent = 0d;

                        if (_previous.TryGetValue(process.Id, out var previous))
                        {
                            var wallMs = (now - previous.Timestamp).TotalMilliseconds;
                            var cpuMs = (cpuTime - previous.TotalProcessorTime).TotalMilliseconds;

                            if (wallMs > 0 && cpuMs >= 0)
                            {
                                cpuPercent = cpuMs / wallMs / Environment.ProcessorCount * 100d;
                                cpuPercent = Math.Clamp(cpuPercent, 0d, 100d);
                            }
                        }

                        _previous[process.Id] = new ProcessSample(cpuTime, now);

                        result.Add(new ProcessSnapshot(
                            process.Id,
                            process.ProcessName,
                            Math.Round(cpuPercent, 1),
                            Math.Round(process.WorkingSet64 / 1024d / 1024d, 1),
                            SafeThreads(process),
                            SafePath(process)));
                    }
                    catch
                    {
                        // System/protected processes can deny one or more properties.
                    }
                }
            }

            foreach (var pid in _previous.Keys.Where(pid => !alive.Contains(pid)).ToArray())
                _previous.Remove(pid);

            return result
                .OrderByDescending(p => p.CpuPercent)
                .ThenByDescending(p => p.MemoryMb)
                .Take(limit)
                .ToList();
        }
    }

    private static int? SafeThreads(Process process)
    {
        try { return process.Threads.Count; }
        catch { return null; }
    }

    private static string? SafePath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch { return null; }
    }

    private sealed record ProcessSample(TimeSpan TotalProcessorTime, DateTimeOffset Timestamp);
}

public sealed record ProcessSnapshot(
    int Pid,
    string Name,
    double CpuPercent,
    double MemoryMb,
    int? Threads,
    string? Path);
