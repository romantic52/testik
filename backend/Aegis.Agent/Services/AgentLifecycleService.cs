namespace Aegis.Agent.Services;

public sealed class AgentLifecycleService : IHostedService
{
    private readonly IncidentStoreService _incidents;

    public AgentLifecycleService(IncidentStoreService incidents)
    {
        _incidents = incidents;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _incidents.AppendAuditAsync(new AuditRecord(
            DateTimeOffset.UtcNow,
            "agent.started",
            Environment.MachineName,
            typeof(AgentLifecycleService).Assembly.GetName().Version?.ToString() ?? "dev",
            "Aegis.Agent"), cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        _incidents.AppendAuditAsync(new AuditRecord(
            DateTimeOffset.UtcNow,
            "agent.stopped",
            Environment.MachineName,
            "Graceful shutdown",
            "Aegis.Agent"), CancellationToken.None);
}
