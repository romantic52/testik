using Aegis.Agent.Configuration;

namespace Aegis.Agent.Tests;

public sealed class AgentOptionsTests
{
    [Fact]
    public void Runtime_limits_are_clamped_to_safe_ranges()
    {
        var options = new AgentOptions
        {
            SampleIntervalMs = 10,
            ProcessLimit = 5000,
            HistoryMinutes = 999,
            AetherTimeoutSeconds = 1
        };

        Assert.Equal(500, options.SafeSampleIntervalMs);
        Assert.Equal(200, options.SafeProcessLimit);
        Assert.Equal(120, options.SafeHistoryMinutes);
        Assert.Equal(5, options.SafeAetherTimeoutSeconds);
    }
}
