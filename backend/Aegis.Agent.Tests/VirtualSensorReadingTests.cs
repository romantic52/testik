using Aegis.Agent.Services;
namespace Aegis.Agent.Tests;
public sealed class VirtualSensorReadingTests
{
    [Theory]
    [InlineData(true, "Cpu", "Temperature", 0f, true)]
    [InlineData(true, "Cpu", "Temperature", 52f, false)]
    [InlineData(true, "Cpu", "Load", 0f, false)]
    [InlineData(true, "GpuNvidia", "Temperature", 45f, false)]
    [InlineData(true, "GpuNvidia", "Fan", 0f, false)]
    [InlineData(false, "Cpu", "Temperature", 0f, true)]
    [InlineData(false, "Cpu", "Temperature", 47f, false)]
    [InlineData(false, "GpuNvidia", "Fan", 0f, false)]
    [InlineData(true, "Cpu", "Temperature", null, false)]
    [InlineData(false, "SuperIO", "Temperature", 0f, true)]      // Nuvoton "M2 #1" without a probe
    [InlineData(false, "SuperIO", "Temperature", 55f, false)]
    public void Virtual_cpu_stub_is_hidden_without_hiding_passthrough_or_idle_sensors(bool vm, string hardware, string sensor, float? value, bool hidden) =>
        Assert.Equal(hidden, HardwareMonitorService.IsUnreliableVirtualReading(vm, hardware, sensor, value));
}