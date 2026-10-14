using System.Text.Json;
using Aegis.Agent.Infrastructure;
using Aegis.Agent.Services;

namespace Aegis.Agent.Tests;

public sealed class ProtectionTests
{
    [Theory]
    [InlineData("127.0.0.1", "local")]
    [InlineData("::1", "local")]
    [InlineData("::ffff:127.0.0.1", "local")]
    [InlineData("0.0.0.0", "network")]
    [InlineData("::", "network")]
    [InlineData("192.168.1.10", "network")]
    [InlineData("not-an-ip", "network")]
    public void Port_exposure(string address, string exposure) =>
        Assert.Equal(exposure, PortsService.Exposure(address));

    [Theory]
    [InlineData(@"C:\Users\user\AppData\Local\Temp\x.exe", true)]
    [InlineData(@"C:\Users\user\Downloads\setup.exe", true)]
    [InlineData(@"C:\Users\user\AppData\Roaming\app\a.exe", true)]
    [InlineData(@"C:\Windows\Temp\b.exe", true)]
    [InlineData(@"C:\Users\Public\c.exe", true)]
    [InlineData(@"C:\Program Files\App\app.exe", false)]
    [InlineData(@"C:\Windows\System32\svchost.exe", false)]
    public void User_writable_locations(string path, bool expected) =>
        Assert.Equal(expected, SignatureInspector.IsUserWritable(path));

    [Fact]
    public void Signature_of_missing_and_unsigned_files()
    {
        Assert.Equal("no-path", SignatureInspector.Inspect(null).Status);
        Assert.Equal("missing", SignatureInspector.Inspect(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")).Status);

        var file = Path.Combine(Path.GetTempPath(), "aegis-sig-" + Guid.NewGuid() + ".exe");
        File.WriteAllBytes(file, [0x4D, 0x5A, 0, 0]);
        try
        {
            var trust = SignatureInspector.Inspect(file);
            Assert.True(trust.Exists);
            Assert.False(trust.Signed);
            Assert.Equal(SignatureInspector.IsUserWritable(file) ? "unsigned-user-writable" : "unsigned", trust.Status);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(@"SCSI\Disk&Ven_NVMe&Prod_X\4&1a2b&0&000000_0", @"SCSI\Disk&Ven_NVMe&Prod_X\4&1a2b&0&000000")]
    [InlineData(@"IDE\DiskST1000\5&abc&0&0.0.0_12", @"IDE\DiskST1000\5&abc&0&0.0.0")]
    [InlineData(@"USB\VID_1\ABC_DEF", @"USB\VID_1\ABC_DEF")]
    [InlineData(null, "")]
    public void Smart_instance_maps_to_pnp_id(string? instance, string pnp) =>
        Assert.Equal(pnp, DiskMonitorService.StripInstanceSuffix(instance));

    [Fact]
    public void Disk_report_wire_format()
    {
        var report = new DiskHealthReport(
            [new PhysicalDiskInfo("SSD 1", "S1", "SSD", "NVMe", "healthy", "unknown", 1024)],
            [new VolumeInfo("C:", "System", "NTFS", "Fixed", false, 931.5, 100.2, 89.2)],
            [], [], false);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report, TelemetryJson.Options));
        var root = json.RootElement;
        Assert.False(root.GetProperty("smartAccess").GetBoolean());
        var disk = root.GetProperty("disks")[0];
        foreach (var name in new[] { "model", "serial", "mediaType", "bus", "health", "predicted", "sizeGb" })
            Assert.True(disk.TryGetProperty(name, out _), name);
        var volume = root.GetProperty("volumes")[0];
        foreach (var name in new[] { "drive", "label", "fileSystem", "removable", "totalGb", "freeGb", "usedPercent" })
            Assert.True(volume.TryGetProperty(name, out _), name);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --tray", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Windows\system32\svchost.exe -k netsvcs -p", @"C:\Windows\system32\svchost.exe")]
    [InlineData(@"C:\Tools\run.cmd /quiet", @"C:\Tools\run.cmd")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Executable_from_command_line(string? command, string? expected) =>
        Assert.Equal(expected, ControlModeService.ExecutableOf(command));

    [Fact]
    public void Control_risk_levels()
    {
        Assert.Equal("low", ControlModeService.Risk(new FileTrust(true, true, "Microsoft", false, "signed")));
        Assert.Equal("medium", ControlModeService.Risk(new FileTrust(true, false, null, false, "unsigned")));
        Assert.Equal("high", ControlModeService.Risk(new FileTrust(true, false, null, true, "unsigned-user-writable")));
        Assert.Equal("medium", ControlModeService.Risk(null));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\lsass.exe", true)]
    [InlineData(@"C:\Windows\explorer.exe", true)]
    [InlineData(@"C:\Users\u\Downloads\tool.exe", false)]
    public void System_processes_cannot_be_stopped_or_blocked(string path, bool expected) =>
        Assert.Equal(expected, OperatorBlockService.IsProtected(path));

    [Fact]
    public void Port_key_ignores_pid_and_address()
    {
        var a = new OpenPort("TCP", "0.0.0.0", 5000, "network", 100, "Python", null, null);
        var b = a with { ProcessId = 200, Address = "::" };
        Assert.Equal(ProtectionWatchService.PortKey(a), ProtectionWatchService.PortKey(b));
        Assert.NotEqual(ProtectionWatchService.PortKey(a), ProtectionWatchService.PortKey(a with { Port = 5001 }));
    }
}
