using System.Management;

namespace Aegis.Agent.Services;

/// <summary>
/// Physical disk health (SMART predictive failure, media type, bus) and logical
/// volume usage. Read-only; nothing here writes to disks. SMART predictive-failure
/// flags need elevation; MSFT_PhysicalDisk health works for standard users.
/// </summary>
public sealed class DiskMonitorService
{
    public DiskHealthReport GetReport()
    {
        var (physical, smartAccess) = ReadPhysicalDisks();
        var volumes = ReadVolumes();
        var removable = volumes.Where(v => v.Removable).Select(v => v.Drive).ToList();
        var failing = physical.Where(d => d.Predicted == "failing" || d.Health == "unhealthy").Select(d => d.Model).ToList();
        return new DiskHealthReport(physical, volumes, removable, failing, smartAccess);
    }

    private static (List<PhysicalDiskInfo> Disks, bool SmartAccess) ReadPhysicalDisks()
    {
        var result = new List<PhysicalDiskInfo>();
        // SMART predictive-failure flags. root\wmi is admin-only on Windows 10/11; standard users get
        // "access denied" and the flag is reported as unknown (the storage stack health below still works).
        var smartByPnp = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var smartAccess = false;
        try
        {
            using var smart = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus");
            foreach (ManagementObject item in smart.Get())
                using (item)
                    smartByPnp[StripInstanceSuffix(item["InstanceName"]?.ToString())] = (bool)(item["PredictFailure"] ?? false);
            smartAccess = true;
        }
        catch { /* access denied without elevation, or the controller does not publish SMART via WMI */ }

        // Win32_DiskDrive links the SMART instance (PNPDeviceID) to the serial number.
        var legacy = new List<(string Model, string? Serial, string? Pnp, string Interface, string? Media, double? SizeGb)>();
        try
        {
            using var drives = new ManagementObjectSearcher("SELECT Model, SerialNumber, PNPDeviceID, Size, InterfaceType, MediaType FROM Win32_DiskDrive");
            foreach (ManagementObject drive in drives.Get())
                using (drive)
                    legacy.Add((drive["Model"]?.ToString() ?? "Диск", drive["SerialNumber"]?.ToString()?.Trim(), drive["PNPDeviceID"]?.ToString(),
                        drive["InterfaceType"]?.ToString() ?? "—", drive["MediaType"]?.ToString(),
                        drive["Size"] is { } s ? Math.Round(Convert.ToDouble(s) / 1_000_000_000d, 1) : null));
        }
        catch { }

        string PredictedFor(string? serial, string? pnp)
        {
            if (!smartAccess) return "unknown";
            pnp ??= legacy.FirstOrDefault(d => Normalize(d.Serial) == Normalize(serial) && Normalize(serial).Length > 0).Pnp;
            return pnp is not null && smartByPnp.TryGetValue(pnp, out var fail) ? (fail ? "failing" : "ok") : "unknown";
        }

        try
        {
            using var disks = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_PhysicalDisk");
            foreach (ManagementObject disk in disks.Get())
                using (disk)
                {
                    var name = disk["FriendlyName"]?.ToString() ?? "Диск";
                    var serial = disk["SerialNumber"]?.ToString()?.Trim().TrimEnd('.');
                    var mediaType = Convert.ToUInt16(disk["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unknown" };
                    var bus = BusName(Convert.ToUInt16(disk["BusType"] ?? 0));
                    // HealthStatus is computed by the storage stack (includes the drive's own failure prediction).
                    var health = Convert.ToUInt16(disk["HealthStatus"] ?? 5) switch { 0 => "healthy", 1 => "warning", 2 => "unhealthy", _ => "unknown" };
                    var sizeGb = disk["Size"] is { } s ? Math.Round(Convert.ToDouble(s) / 1_000_000_000d, 1) : (double?)null;
                    result.Add(new PhysicalDiskInfo(name, string.IsNullOrEmpty(serial) ? null : serial, mediaType, bus, health, PredictedFor(serial, null), sizeGb));
                }
        }
        catch { /* storage namespace unavailable (older Windows) */ }

        if (result.Count == 0)
            foreach (var d in legacy)
                result.Add(new PhysicalDiskInfo(d.Model, d.Serial, d.Media?.Contains("SSD", StringComparison.OrdinalIgnoreCase) == true ? "SSD" : "Unknown",
                    d.Interface, "unknown", PredictedFor(d.Serial, d.Pnp), d.SizeGb));
        return (result, smartAccess);
    }

    /// <summary>SMART InstanceName is the disk PNPDeviceID plus an instance suffix such as "_0".</summary>
    public static string StripInstanceSuffix(string? instance)
    {
        var value = instance ?? "";
        var cut = value.LastIndexOf('_');
        return cut > value.LastIndexOf('\\') && cut > 0 && value[(cut + 1)..].All(char.IsDigit) ? value[..cut] : value;
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

    private static string BusName(ushort bus) => bus switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 5 => "SSA", 6 => "Fibre", 7 => "USB",
        8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual", 15 => "VHD", 16 => "Storage Spaces", 17 => "NVMe", _ => "—"
    };

    private static string Normalize(string? value) => (value ?? "").Trim().TrimEnd('.').Replace("_", "").Replace(" ", "").ToUpperInvariant();
}

public sealed record PhysicalDiskInfo(string Model, string? Serial, string MediaType, string Bus, string Health, string Predicted, double? SizeGb);
public sealed record VolumeInfo(string Drive, string Label, string FileSystem, string Type, bool Removable, double TotalGb, double FreeGb, double UsedPercent);
public sealed record DiskHealthReport(IReadOnlyList<PhysicalDiskInfo> Disks, IReadOnlyList<VolumeInfo> Volumes, IReadOnlyList<string> RemovableDrives, IReadOnlyList<string> FailingDisks, bool SmartAccess);
