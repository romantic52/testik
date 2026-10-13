using System.Management;

namespace Aegis.Agent.Services;

/// <summary>
/// Physical disk health (SMART predictive failure, media type, bus) and logical
/// volume usage. Read-only; nothing here writes to disks. SMART predictive-failure
/// status is exposed to standard users by the storage driver.
/// </summary>
public sealed class DiskMonitorService
{
    public DiskHealthReport GetReport()
    {
        var physical = ReadPhysicalDisks();
        var volumes = ReadVolumes();
        var removable = volumes.Where(v => v.Removable).Select(v => v.Drive).ToList();
        var failing = physical.Where(d => d.Predicted == "failing").Select(d => d.Model).ToList();
        return new DiskHealthReport(physical, volumes, removable, failing);
    }

    private static List<PhysicalDiskInfo> ReadPhysicalDisks()
    {
        var result = new List<PhysicalDiskInfo>();
        // SMART predictive failure flags per physical drive.
        var predicted = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var smart = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus");
            foreach (ManagementObject item in smart.Get())
                using (item)
                    predicted[Normalize(item["InstanceName"]?.ToString())] = (bool)(item["PredictFailure"] ?? false);
        }
        catch { /* not all controllers publish SMART via WMI */ }

        try
        {
            using var disks = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_PhysicalDisk");
            foreach (ManagementObject disk in disks.Get())
                using (disk)
                {
                    var name = disk["FriendlyName"]?.ToString() ?? "Диск";
                    var serial = disk["SerialNumber"]?.ToString()?.Trim();
                    var mediaType = (ushort?)disk["MediaType"] switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unknown" };
                    var bus = BusName((ushort?)disk["BusType"]);
                    var healthRaw = (ushort?)disk["HealthStatus"];
                    var health = healthRaw switch { 0 => "healthy", 1 => "warning", 2 => "unhealthy", _ => "unknown" };
                    var sizeGb = disk["Size"] is { } s ? Math.Round(Convert.ToDouble(s) / 1_000_000_000d, 1) : (double?)null;
                    var predictFail = predicted.TryGetValue(Normalize(serial), out var pf) && pf;
                    result.Add(new PhysicalDiskInfo(name, serial, mediaType, bus, health,
                        predictFail ? "failing" : predicted.Count > 0 ? "ok" : "unknown", sizeGb));
                }
        }
        catch { /* storage namespace unavailable (older Windows) */ }

        if (result.Count == 0)
        {
            // Fallback: legacy Win32_DiskDrive without health detail.
            try
            {
                using var drives = new ManagementObjectSearcher("SELECT Model, SerialNumber, Size, InterfaceType, MediaType FROM Win32_DiskDrive");
                foreach (ManagementObject drive in drives.Get())
                    using (drive)
                        result.Add(new PhysicalDiskInfo(
                            drive["Model"]?.ToString() ?? "Диск", drive["SerialNumber"]?.ToString()?.Trim(),
                            drive["MediaType"]?.ToString()?.Contains("SSD", StringComparison.OrdinalIgnoreCase) == true ? "SSD" : "Unknown",
                            drive["InterfaceType"]?.ToString() ?? "—", "unknown",
                            predicted.TryGetValue(Normalize(drive["SerialNumber"]?.ToString()), out var pf) && pf ? "failing" : "unknown",
                            drive["Size"] is { } s ? Math.Round(Convert.ToDouble(s) / 1_000_000_000d, 1) : null));
            }
            catch { }
        }
        return result;
    }

    private static List<VolumeInfo> ReadVolumes()
    {
        var result = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                var total = drive.TotalSize;
                var free = drive.AvailableFreeSpace;
                var used = total - free;
                result.Add(new VolumeInfo(
                    drive.Name.TrimEnd('\\'), drive.VolumeLabel, drive.DriveFormat,
                    drive.DriveType.ToString(), drive.DriveType == DriveType.Removable || drive.DriveType == DriveType.CDRom,
                    Math.Round(total / 1_073_741_824d, 1), Math.Round(free / 1_073_741_824d, 1),
                    total > 0 ? Math.Round(used * 100d / total, 1) : 0));
            }
            catch { /* drive became unavailable between enumeration and query */ }
        }
        return result;
    }

    private static string BusName(ushort? bus) => bus switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 5 => "SSA", 6 => "Fibre", 7 => "USB",
        8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 17 => "NVMe", _ => "—"
    };

    private static string Normalize(string? value) => (value ?? "").Trim().Replace("_", "").Replace(" ", "").ToUpperInvariant();
}

public sealed record PhysicalDiskInfo(string Model, string? Serial, string MediaType, string Bus, string Health, string Predicted, double? SizeGb);
public sealed record VolumeInfo(string Drive, string Label, string FileSystem, string Type, bool Removable, double TotalGb, double FreeGb, double UsedPercent);
public sealed record DiskHealthReport(IReadOnlyList<PhysicalDiskInfo> Disks, IReadOnlyList<VolumeInfo> Volumes, IReadOnlyList<string> RemovableDrives, IReadOnlyList<string> FailingDisks);
