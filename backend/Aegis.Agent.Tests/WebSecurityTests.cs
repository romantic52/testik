using Aegis.Agent.Infrastructure;

namespace Aegis.Agent.Tests;

public sealed class WebSecurityTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8765", 8765, true)]
    [InlineData("http://localhost:8765", 8765, true)]
    [InlineData("http://[::1]:8765", 8765, true)]
    [InlineData("http://[::1]:8765/", 8765, true)]
    [InlineData("http://[::1]:1234", 8765, false)]
    [InlineData("http://127.0.0.2:8765", 8765, false)]
    [InlineData("https://localhost:8765", 8765, false)]
    [InlineData("http://localhost:8765/other", 8765, false)]
    [InlineData("http://localhost:8765/?key=x", 8765, false)]
    [InlineData("http://user@localhost:8765", 8765, false)]
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
    [Fact]
    public void Https_loopback_origin_requires_https_server()
    {
        Assert.True(AegisWebExtensions.IsAllowedBrowserOrigin(
            "https://localhost:8765", 8765, expectedHttps: true));
        Assert.False(AegisWebExtensions.IsAllowedBrowserOrigin(
            "http://localhost:8765", 8765, expectedHttps: true));
    }
}
