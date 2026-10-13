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
    [InlineData(false, "Cpu", "Temperature", 0f, false)]
    [InlineData(true, "Cpu", "Temperature", null, false)]
    public void Virtual_cpu_stub_is_hidden_without_hiding_passthrough_or_idle_sensors(bool vm, string hardware, string sensor, float? value, bool hidden) =>
        Assert.Equal(hidden, HardwareMonitorService.IsUnreliableVirtualReading(vm, hardware, sensor, value));
}