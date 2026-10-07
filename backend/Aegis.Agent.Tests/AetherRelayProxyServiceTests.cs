using Aegis.Agent.Configuration;
using Aegis.Agent.Services;
using Microsoft.Extensions.Options;

namespace Aegis.Agent.Tests;

public sealed class AetherRelayProxyServiceTests
{
    private static AetherRelayProxyService CreateService() =>
        new(new HttpClient(), Options.Create(new AgentOptions()));

    [Fact]
    public async Task Remote_plain_http_is_rejected_before_network_access()
    {
        var service = CreateService();
        var request = new AetherRelayRequest(
            "http://example.com",
            "POST",
            "/users/login",
            null,
            null);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ForwardAsync(request, CancellationToken.None));

        Assert.Contains("HTTPS", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Absolute_relay_path_is_rejected()
    {
        var service = CreateService();
        var request = new AetherRelayRequest(
            "https://example.com",
            "GET",
            "https://internal.example/api",
            null,
            null);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ForwardAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task Non_allowlisted_endpoint_is_rejected()
    {
        var service = CreateService();
        var request = new AetherRelayRequest(
            "https://example.com",
            "GET",
            "/admin/export",
            null,
            null);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ForwardAsync(request, CancellationToken.None));

        Assert.Contains("not allowed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("file:///C:/Windows/System32")]
    [InlineData("javascript:alert(1)")]
    public async Task Unsupported_server_schemes_are_rejected(string serverUrl)
    {
        var service = CreateService();
        var request = new AetherRelayRequest(
            serverUrl,
            "POST",
            "/users/login",
            null,
            null);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ForwardAsync(request, CancellationToken.None));
    }
}
