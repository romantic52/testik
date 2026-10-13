using System.Text.Json;
using Aegis.Agent.Infrastructure;
using Aegis.Agent.Services;
namespace Aegis.Agent.Tests;
public sealed class TelemetryWireTests
{
    [Fact]
    public void Live_nested_fields_match_the_http_and_browser_contract()
    {
        var now = DateTimeOffset.UtcNow;
        var adapter = new NetworkAdapterSnapshot("Ethernet", "Virtual adapter", "Ethernet", 1000, 10, 20, 1, 2);
        var network = new NetworkSnapshot(now, new[] { adapter }, 1, 2);
        var system = new SystemSnapshot(now, "qa", "Windows", 2, 12.5, null,
            new MemorySnapshot(8, 2, 6, 25), Array.Empty<GpuSnapshot>(), Array.Empty<FanSnapshot>(),
            Array.Empty<TemperatureSnapshot>(), new[] { new DiskSnapshot("C:\\", "NTFS", 40, 10, 30, 25) }, network);
        using var doc = JsonDocument.Parse(TelemetryJson.Serialize(new { type = "telemetry", system }));
        var live = doc.RootElement.GetProperty("system");
        Assert.Equal(12.5, live.GetProperty("cpuLoadPercent").GetDouble());
        Assert.Equal(8, live.GetProperty("memory").GetProperty("totalGb").GetDouble());
        Assert.Equal("Ethernet", live.GetProperty("network").GetProperty("adapters")[0].GetProperty("name").GetString());
        Assert.Equal("NTFS", live.GetProperty("disks")[0].GetProperty("fileSystem").GetString());
        Assert.False(live.TryGetProperty("CpuLoadPercent", out _));
    }
}