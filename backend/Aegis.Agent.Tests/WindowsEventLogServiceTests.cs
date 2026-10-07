using Aegis.Agent.Services;

namespace Aegis.Agent.Tests;

public sealed class WindowsEventLogServiceTests
{
    [Fact]
    public void Only_allowlisted_logs_can_be_requested()
    {
        var service = new WindowsEventLogService();

        Assert.Throws<ArgumentException>(() =>
            service.GetRecent("Security", 10));
    }
}
