using Aegis.Agent.Services;

namespace Aegis.Agent.Tests;

public sealed class OperatorShutdownServiceTests
{
    [Theory]
    [InlineData("ВЫКЛЮЧИТЬ", "local-shutdown-confirmed", true)]
    [InlineData("ВЫКЛЮЧИТЬ", null, false)]
    [InlineData("", "local-shutdown-confirmed", false)]
    [InlineData("выключить", "local-shutdown-confirmed", false)]
    [InlineData("ВЫКЛЮЧИТЬ", "unconfirmed", false)]
    public void Requires_explicit_exact_confirmation(
        string phrase, string? intent, bool expected)
    {
        Assert.Equal(expected, OperatorShutdownService.IsAuthorized(phrase, intent));
    }
}
