using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Aegis.Agent.Services;

/// <summary>Bounded resource-pressure generator for an operator's local Windows test lab.</summary>
public sealed class LabStressService : IDisposable
{
    private const long MiB = 1024L * 1024;
    private const int BlockBytes = 8 * 1024 * 1024;
    private readonly object _sync = new();
    private readonly List<byte[]> _blocks = new();
    private readonly ILogger<LabStressService> _logger;
    private CancellationTokenSource? _cpuStop;
    private CancellationTokenSource? _memoryStop;
    private int _cpuDuty;
    private int _memoryTarget;
    private bool _disposed;

    public LabStressService(ILogger<LabStressService> logger) => _logger = logger;

    public LabStressState Read()
    {
        lock (_sync)
        {
            var memory = ReadMemory();
            return new LabStressState(
                _cpuDuty, _memoryTarget, _blocks.Sum(b => (long)b.Length) / (double)MiB,
                memory.LoadPercent, memory.TotalBytes / (double)MiB,
                memory.AvailableBytes / (double)MiB,
                ReserveBytes(memory.TotalBytes) / (double)MiB);
        }
    }

    public LabStressState Apply(LabStressRequest request)
    {
        if (request.CpuDutyPercent is < 0 or > 95 ||
            request.MemoryTargetPercent is < 0 or > 95)
            throw new ArgumentOutOfRangeException(nameof(request), "CPU/RAM targets must be within 0–95%");

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_cpuDuty != request.CpuDutyPercent)
            {
                _cpuStop?.Cancel();
                _cpuStop = null;
                _cpuDuty = request.CpuDutyPercent;
                if (_cpuDuty > 0)
                {
                    var stop = new CancellationTokenSource();
                    _cpuStop = stop;
                    var workers = Math.Min(Environment.ProcessorCount, 24);
                    for (var i = 0; i < workers; i++)
                    {
                        _ = Task.Factory.StartNew(
                            () => RunCpuWorker(request.CpuDutyPercent, stop.Token),
                            stop.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    }
                }
            }

            _memoryTarget = request.MemoryTargetPercent;
            if (_memoryTarget <= 0)
            {
                _memoryStop?.Cancel();
                _memoryStop = null;
                _blocks.Clear();
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            }
            else if (_memoryStop is null)
            {
                var stop = new CancellationTokenSource();
                _memoryStop = stop;
                _ = Task.Run(() => MaintainMemoryAsync(stop.Token), stop.Token);
            }
            _logger.LogInformation(
                "Lab stress set: CPU duty {Cpu}%, RAM total-use target {Ram}%",
                _cpuDuty, _memoryTarget);
        }
        return Read();
    }

    public LabStressState Stop() => Apply(new LabStressRequest(0, 0));

    private static void RunCpuWorker(int dutyPercent, CancellationToken token)
    {
        var busy = dutyPercent; // Milliseconds in each 100-ms period.
        var result = 1.123456;
        while (!token.IsCancellationRequested)
        {
            var start = Stopwatch.GetTimestamp();
            while (!token.IsCancellationRequested &&
                   Stopwatch.GetElapsedTime(start).TotalMilliseconds < busy)
            {
                // Real arithmetic, kept observable so JIT cannot elide it.
                result = Math.Sqrt(result * 1.000001 + 0.21);
            }
            if (double.IsNaN(result)) result = 1.123456;
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var rest = Math.Max(1, 100 - (int)elapsed);
            if (token.WaitHandle.WaitOne(rest)) break;
        }
        GC.KeepAlive(result);
    }

    private async Task MaintainMemoryAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                lock (_sync)
                {
                    var mem = ReadMemory();
                    var held = _blocks.Sum(b => (long)b.Length);
                    var total = (long)Math.Min(mem.TotalBytes, (ulong)long.MaxValue);
                    var available = (long)Math.Min(mem.AvailableBytes, (ulong)long.MaxValue);
                    var baselineUsed = Math.Max(0L, total - available - held);
                    var desiredTotalUsed = (long)(total * (_memoryTarget / 100d));
                    var desiredHeld = Math.Max(0L, desiredTotalUsed - baselineUsed);
                    var maxAdditional = Math.Max(0L, available - ReserveBytes(mem.TotalBytes));
                    var safeHeld = Math.Min(desiredHeld, held + maxAdditional);

                    if (safeHeld + BlockBytes < held && _blocks.Count > 0)
                    {
                        _blocks.RemoveAt(_blocks.Count - 1);
                    }
                    else if (safeHeld - held >= BlockBytes)
                    {
                        // Commit every page: this is real physical-memory pressure.
                        var bytes = new byte[BlockBytes];
                        for (var index = 0; index < bytes.Length; index += 4096)
                            bytes[index] = (byte)(index & 0xff);
                        _blocks.Add(bytes);
                    }
                }

                await Task.Delay(65, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (OutOfMemoryException ex)
        {
            _logger.LogWarning(ex, "Lab memory allocation hit an OOM guard; releasing stress blocks");
            lock (_sync) { _memoryTarget = 0; _blocks.Clear(); _memoryStop = null; }
            GC.Collect();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lab memory worker stopped");
        }
    }

    private static long ReserveBytes(ulong total) =>
        Math.Max(512L * MiB, (long)Math.Min(total, (ulong)long.MaxValue) / 12);

    private static MemoryInfo ReadMemory()
    {
        var buffer = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(buffer))
            return new MemoryInfo(0, 0, 0);
        return new MemoryInfo(
            buffer.TotalPhysical,
            buffer.AvailablePhysical,
            buffer.TotalPhysical == 0 ? 0
                : (1d - (double)buffer.AvailablePhysical / buffer.TotalPhysical) * 100);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _cpuStop?.Cancel();
            _memoryStop?.Cancel();
            _blocks.Clear();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus memory);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private sealed record MemoryInfo(ulong TotalBytes, ulong AvailableBytes, double LoadPercent);
}

public sealed record LabStressRequest(int CpuDutyPercent, int MemoryTargetPercent);

public sealed record LabStressState(
    int CpuDutyPercent,
    int MemoryTargetPercent,
    double HeldMemoryMb,
    double ActualMemoryLoadPercent,
    double TotalMemoryMb,
    double AvailableMemoryMb,
    double ProtectedMemoryReserveMb);
