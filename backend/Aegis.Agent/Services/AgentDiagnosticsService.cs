using System.Diagnostics;
using Aegis.Agent.Configuration;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace Aegis.Agent.Services;

public sealed class AgentDiagnosticsService
{
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly AgentOptions _options;
    private readonly TelemetrySamplerService _telemetry;
    private readonly IncidentStoreService _incidents;

    public AgentDiagnosticsService(
        IOptions<AgentOptions> options,
        TelemetrySamplerService telemetry,
        IncidentStoreService incidents)
    {
        _options = options.Value;
        _telemetry = telemetry;
        _incidents = incidents;
    }

    public AgentDiagnosticsSnapshot Capture()
    {
        using var process = Process.GetCurrentProcess();
        var now = DateTimeOffset.UtcNow;
        var latest = _telemetry.Latest;
        var gc = GC.GetGCMemoryInfo();

        return new AgentDiagnosticsSnapshot(
            now,
            _startedAt,
            Math.Max(0, (now - _startedAt).TotalSeconds),
            Environment.ProcessId,
            Environment.MachineName,
            typeof(AgentDiagnosticsService).Assembly.GetName().Version?.ToString() ?? "dev",
            Environment.Version.ToString(),
            Environment.Is64BitProcess,
            process.Threads.Count,
            process.HandleCount,
            Math.Round(process.WorkingSet64 / 1024d / 1024d, 1),
            Math.Round(process.PrivateMemorySize64 / 1024d / 1024d, 1),
            Math.Round(GC.GetTotalMemory(false) / 1024d / 1024d, 1),
            Math.Round(gc.HeapSizeBytes / 1024d / 1024d, 1),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            latest?.Timestamp,
            latest is null ? null : Math.Max(0, (now - latest.Timestamp).TotalSeconds),
            _telemetry.GetHistory(TimeSpan.FromMinutes(_options.SafeHistoryMinutes)).Count,
            _incidents.Revision,
            _incidents.DataDirectory,
            _options.SafeSampleIntervalMs,
            _options.SafeProcessLimit,
            _options.SafeHistoryMinutes,
            WindowsServiceHelpers.IsWindowsService());
    }
}

public sealed record AgentDiagnosticsSnapshot(
    DateTimeOffset Timestamp,
    DateTimeOffset StartedAt,
    double UptimeSeconds,
    int ProcessId,
    string MachineName,
    string Version,
    string RuntimeVersion,
    bool Is64BitProcess,
    int ThreadCount,
    int HandleCount,
    double WorkingSetMb,
    double PrivateMemoryMb,
    double ManagedMemoryMb,
    double GcHeapSizeMb,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    DateTimeOffset? LastSampleAt,
    double? LastSampleAgeSeconds,
    int HistoryPoints,
    long IncidentRevision,
    string DataDirectory,
    int SampleIntervalMs,
    int ProcessLimit,
    int HistoryMinutes,
    bool RunningAsWindowsService);
