using System.Diagnostics;

namespace Aegis.Agent.Services;

public sealed record ProcessDetailsSnapshot(
    int Pid,
    string Name,
    string? Path,
    DateTimeOffset? StartedAt,
    double MemoryMb,
    double PrivateMemoryMb,
    int? Threads,
    int? Handles,
    string? Priority,
    bool? Responding);

public static class ProcessDetailsReader
{
    public static ProcessDetailsSnapshot? Read(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new ProcessDetailsSnapshot(
                process.Id,
                process.ProcessName,
                Try(() => process.MainModule?.FileName),
                Try(() => new DateTimeOffset(process.StartTime)),
                Math.Round(process.WorkingSet64 / 1024d / 1024d, 1),
                Math.Round(process.PrivateMemorySize64 / 1024d / 1024d, 1),
                Try(() => (int?)process.Threads.Count),
                Try(() => (int?)process.HandleCount),
                Try(() => process.PriorityClass.ToString()),
                Try(() => (bool?)process.Responding));
        }
        catch
        {
            return null;
        }
    }

    private static T? Try<T>(Func<T> read)
    {
        try { return read(); }
        catch { return default; }
    }
}
