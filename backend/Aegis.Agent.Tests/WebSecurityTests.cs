using Aegis.Agent.Infrastructure;

namespace Aegis.Agent.Tests;

public sealed class WebSecurityTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8765", 8765, true)]
    [InlineData("http://localhost:8765", 8765, true)]
    [InlineData("http://[::1]:8765", 8765, true)]
    [InlineData("http://localhost:3000", 8765, false)]
    [InlineData("https://example.com:8765", 8765, false)]
    [InlineData("file:///C:/index.html", 8765, false)]
    [InlineData("not-a-url", 8765, false)]
    public void Browser_origin_must_be_loopback_and_same_port(
        string origin,
        int port,
        bool expected)
    {
        Assert.Equal(
            expected,
            AegisWebExtensions.IsAllowedBrowserOrigin(origin, port));
    }
}
