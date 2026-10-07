using System.Net;

namespace Aegis.Agent.Configuration;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public string ListenUrl { get; init; } = "http://127.0.0.1:8765";
    public int SampleIntervalMs { get; init; } = 1000;
    public int ProcessLimit { get; init; } = 50;
    public int HistoryMinutes { get; init; } = 15;
    public string? DataDirectory { get; init; }
    public int AetherTimeoutSeconds { get; init; } = 25;
    public int AuditMaxMegabytes { get; init; } = 25;
    public int AuditRetentionFiles { get; init; } = 5;

    public int SafeSampleIntervalMs => Math.Clamp(SampleIntervalMs, 500, 10_000);
    public int SafeProcessLimit => Math.Clamp(ProcessLimit, 10, 200);
    public int SafeHistoryMinutes => Math.Clamp(HistoryMinutes, 1, 120);
    public int SafeAetherTimeoutSeconds => Math.Clamp(AetherTimeoutSeconds, 5, 60);
    public int SafeAuditMaxMegabytes => Math.Clamp(AuditMaxMegabytes, 1, 512);
    public int SafeAuditRetentionFiles => Math.Clamp(AuditRetentionFiles, 1, 20);

    public static bool IsLoopbackListenUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;

        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(uri.Host, out var address)
            && IPAddress.IsLoopback(address);
    }
}
