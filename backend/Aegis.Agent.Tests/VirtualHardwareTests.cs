using Aegis.Agent.Services;

namespace Aegis.Agent.Tests;

public sealed class VirtualHardwareTests
{
    [Theory]
    [InlineData("Virtual Machine")]
    [InlineData("VMware Virtual Platform")]
    [InlineData("VirtualBox")]
    [InlineData("Standard PC (Q35 + ICH9, QEMU)")]
    public void Guest_models_do_not_claim_physical_temperature_sensors(string model) =>
        Assert.True(HardwareMonitorService.IsVirtualMachineModel(model));

    [Theory]
    [InlineData(null)]
    [InlineData("System Product Name")]
    [InlineData("ThinkPad T14 Gen 5")]
    public void Physical_or_unknown_models_keep_sensor_support(string? model) =>
        Assert.False(HardwareMonitorService.IsVirtualMachineModel(model));
}
