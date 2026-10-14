using Aegis.Agent.Services;

namespace Aegis.Agent.Tests;

public sealed class FanStallTests
{
    private static SystemSnapshot Snapshot(FanSnapshot[] fans, TemperatureSnapshot[] temps) =>
        new(DateTimeOffset.UtcNow, "PC", "Windows", 12, 10, null, null!, [], fans, temps, [], null!);

    [Theory]
    [InlineData("Critical Temperature", true)]
    [InlineData("Warning Temperature", true)]
    [InlineData("GPU Core", false)]
    [InlineData("Composite Temperature", false)]
    public void Nvme_limits_are_not_readings(string name, bool threshold) =>
        Assert.Equal(threshold, HardwareMonitorService.IsThreshold(name));

    [Fact]
    public void Cool_gpu_with_zero_rpm_fans_is_not_a_stall()
    {
        // Real case from the user's PC: GPU 49 °C with fans in zero-RPM mode, NVMe temperatures.
        var (hot, _, rpm) = AlertEngineService.FanStall(Snapshot(
            [new("RTX 3070", "GPU Fan 1", 0), new("RTX 3070", "GPU Fan 2", 0)],
            [new("RTX 3070", "GpuNvidia", "GPU Core", 49), new("NVMe", "Storage", "Composite Temperature", 41)]));
        Assert.False(hot >= 85 && rpm <= 100);
    }

    [Fact]
    public void Hot_gpu_with_stopped_fans_is_a_stall()
    {
        var (hot, device, rpm) = AlertEngineService.FanStall(Snapshot(
            [new("RTX 3070", "GPU Fan 1", 0), new("Board", "Fan #1", 900)],
            [new("RTX 3070", "GpuNvidia", "GPU Core", 92), new("Ryzen", "Cpu", "Core (Tctl/Tdie)", 60)]));
        Assert.Equal("RTX 3070", device);
        Assert.True(hot >= 85 && rpm <= 100);
    }

    [Fact]
    public void Hot_cpu_uses_board_fans_not_gpu_fans()
    {
        var (hot, device, rpm) = AlertEngineService.FanStall(Snapshot(
            [new("RTX 3070", "GPU Fan 1", 0), new("Board", "Fan #1", 1200)],
            [new("RTX 3070", "GpuNvidia", "GPU Core", 45), new("Ryzen", "Cpu", "Core (Tctl/Tdie)", 90)]));
        Assert.Equal("Ryzen", device);
        Assert.Equal(1200, rpm);
        Assert.True(hot >= 85);
    }

    [Fact]
    public void Hot_storage_is_not_a_fan_problem()
    {
        var (_, _, rpm) = AlertEngineService.FanStall(Snapshot(
            [new("RTX 3070", "GPU Fan 1", 0)],
            [new("NVMe", "Storage", "Composite Temperature", 88)]));
        Assert.True(double.IsNaN(rpm));
    }
}
