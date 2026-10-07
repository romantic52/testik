using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace Aegis.Agent.Services;

public sealed class HardwareMonitorService : IDisposable
{
    private readonly object _sync = new();
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();

    public HardwareMonitorService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsNetworkEnabled = true,
            IsStorageEnabled = true
        };

        _computer.Open();
    }

    public IReadOnlyList<SensorReading> GetSensors()
    {
        lock (_sync)
        {
            _computer.Accept(_visitor);
            var result = new List<SensorReading>();

            foreach (var hardware in _computer.Hardware)
                CollectHardware(hardware, result);

            return result;
        }
    }

    public HardwareCapture Capture(NetworkSnapshot network)
    {
        var sensors = GetSensors();

        var cpuSensors = sensors
            .Where(s => s.HardwareType.Equals("Cpu", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var cpuLoad = Pick(cpuSensors, "Load", "CPU Total", "Total");
        var cpuTemperature = Pick(cpuSensors, "Temperature", "CPU Package", "Package", "Core Max");

        var gpuGroups = sensors
            .Where(s => s.HardwareType.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => new { s.Device, s.HardwareType })
            .Select(group => new GpuSnapshot(
                group.Key.Device,
                group.Key.HardwareType,
                Pick(group.ToList(), "Load", "GPU Core", "Core"),
                Pick(group.ToList(), "Temperature", "GPU Core", "Core", "Hot Spot"),
                Pick(group.ToList(), "Fan", "Fan")))
            .ToList();

        var fans = sensors
            .Where(s => s.SensorType.Equals("Fan", StringComparison.OrdinalIgnoreCase) && s.Value is not null)
            .Select(s => new FanSnapshot(s.Device, s.Name, s.Value))
            .OrderByDescending(f => f.Rpm)
            .ToList();

        var temperatures = sensors
            .Where(s => s.SensorType.Equals("Temperature", StringComparison.OrdinalIgnoreCase) && s.Value is not null)
            .Select(s => new TemperatureSnapshot(s.Device, s.HardwareType, s.Name, s.Value))
            .OrderByDescending(t => t.Celsius)
            .ToList();

        var system = new SystemSnapshot(
            DateTimeOffset.UtcNow,
            Environment.MachineName,
            Environment.OSVersion.VersionString,
            Environment.ProcessorCount,
            cpuLoad,
            cpuTemperature,
            ReadMemory(),
            gpuGroups,
            fans,
            temperatures,
            ReadDisks(),
            network);

        return new HardwareCapture(system, sensors);
    }

    public SystemSnapshot GetSystemSnapshot(NetworkSnapshot network) => Capture(network).System;

    private static double? Pick(
        IReadOnlyList<SensorReading> sensors,
        string sensorType,
        params string[] preferredNames)
    {
        var candidates = sensors
            .Where(s => s.SensorType.Equals(sensorType, StringComparison.OrdinalIgnoreCase) && s.Value is not null)
            .ToList();

        foreach (var preferred in preferredNames)
        {
            var match = candidates.FirstOrDefault(s =>
                s.Name.Contains(preferred, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
                return Math.Round(match.Value!.Value, 1);
        }

        return candidates.Count > 0 ? Math.Round(candidates[0].Value!.Value, 1) : null;
    }

    private static void CollectHardware(IHardware hardware, List<SensorReading> result)
    {
        foreach (var sensor in hardware.Sensors)
        {
            result.Add(new SensorReading(
                hardware.Name,
                hardware.HardwareType.ToString(),
                sensor.Name,
                sensor.SensorType.ToString(),
                sensor.Value,
                sensor.Min,
                sensor.Max));
        }

        foreach (var subHardware in hardware.SubHardware)
            CollectHardware(subHardware, result);
    }

    private static MemorySnapshot ReadMemory()
    {
        var status = new MemoryStatusEx();

        if (!GlobalMemoryStatusEx(status))
            return new MemorySnapshot(null, null, null, null);

        var total = status.TotalPhysical / 1024d / 1024d / 1024d;
        var available = status.AvailablePhysical / 1024d / 1024d / 1024d;
        var used = Math.Max(0, total - available);
        var load = total > 0 ? used / total * 100d : 0;

        return new MemorySnapshot(
            Math.Round(total, 2),
            Math.Round(used, 2),
            Math.Round(available, 2),
            Math.Round(load, 1));
    }

    private static IReadOnlyList<DiskSnapshot> ReadDisks()
    {
        var result = new List<DiskSnapshot>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                    continue;

                var total = drive.TotalSize / 1024d / 1024d / 1024d;
                var free = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;

                result.Add(new DiskSnapshot(
                    drive.Name,
                    drive.DriveFormat,
                    Math.Round(total, 1),
                    Math.Round(total - free, 1),
                    Math.Round(free, 1),
                    total > 0 ? Math.Round((total - free) / total * 100d, 1) : 0));
            }
            catch
            {
                // Removable or restricted drives can disappear while being enumerated.
            }
        }

        return result;
    }

    public void Dispose()
    {
        lock (_sync)
            _computer.Close();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();

            foreach (var subHardware in hardware.SubHardware)
                subHardware.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}

public sealed record SensorReading(
    string Device,
    string HardwareType,
    string Name,
    string SensorType,
    float? Value,
    float? Min,
    float? Max);

public sealed record MemorySnapshot(
    double? TotalGb,
    double? UsedGb,
    double? AvailableGb,
    double? LoadPercent);

public sealed record GpuSnapshot(
    string Name,
    string Type,
    double? LoadPercent,
    double? TemperatureC,
    double? FanRpm);

public sealed record FanSnapshot(string Device, string Name, float? Rpm);

public sealed record TemperatureSnapshot(
    string Device,
    string HardwareType,
    string Name,
    float? Celsius);

public sealed record DiskSnapshot(
    string Name,
    string FileSystem,
    double TotalGb,
    double UsedGb,
    double FreeGb,
    double UsedPercent);

public sealed record SystemSnapshot(
    DateTimeOffset Timestamp,
    string MachineName,
    string Os,
    int LogicalProcessors,
    double? CpuLoadPercent,
    double? CpuTemperatureC,
    MemorySnapshot Memory,
    IReadOnlyList<GpuSnapshot> Gpus,
    IReadOnlyList<FanSnapshot> Fans,
    IReadOnlyList<TemperatureSnapshot> Temperatures,
    IReadOnlyList<DiskSnapshot> Disks,
    NetworkSnapshot Network);


public sealed record HardwareCapture(
    SystemSnapshot System,
    IReadOnlyList<SensorReading> Sensors);
