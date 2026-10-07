using Aegis.Agent.Configuration;

namespace Aegis.Agent.Tests;

public sealed class AgentOptionsTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8765", true)]
    [InlineData("http://localhost:8765", true)]
    [InlineData("http://[::1]:8765", true)]
    [InlineData("http://0.0.0.0:8765", false)]
    [InlineData("http://192.168.1.20:8765", false)]
    [InlineData("https://example.com", false)]
    [InlineData("ftp://127.0.0.1:21", false)]
    public void Listen_url_must_remain_loopback_only(string url, bool expected)
    {
        Assert.Equal(expected, AgentOptions.IsLoopbackListenUrl(url));
    }

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
