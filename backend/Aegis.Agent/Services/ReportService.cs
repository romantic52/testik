using System.Text;
using System.Text.Json;

namespace Aegis.Agent.Services;

public sealed class ReportService
{
    private readonly HardwareMonitorService _hardware;
    private readonly NetworkMonitorService _network;
    private readonly ProcessMonitorService _processes;
    private readonly IncidentStoreService _incidents;

    public ReportService(
        HardwareMonitorService hardware,
        NetworkMonitorService network,
        ProcessMonitorService processes,
        IncidentStoreService incidents)
    {
        _hardware = hardware;
        _network = network;
        _processes = processes;
        _incidents = incidents;
    }

    public async Task<object> BuildAsync(CancellationToken cancellationToken = default)
    {
        var network = _network.GetSnapshot();
        var system = _hardware.GetSystemSnapshot(network);
        var processes = _processes.GetProcesses(50);
        var incidents = await _incidents.ListAsync(cancellationToken);
        var audit = await _incidents.AuditAsync(250, cancellationToken);

        return new
        {
            generatedAt = DateTimeOffset.UtcNow,
            agent = new
            {
                machine = Environment.MachineName,
                version = typeof(ReportService).Assembly.GetName().Version?.ToString() ?? "dev",
                dataDirectory = _incidents.DataDirectory
            },
            system,
            processes,
            incidents,
            audit
        };
    }

    public async Task<string> BuildJsonAsync(CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await BuildAsync(cancellationToken), new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        });

    public async Task<string> BuildCsvAsync(CancellationToken cancellationToken = default)
    {
        var incidents = await _incidents.ListAsync(cancellationToken);
        var builder = new StringBuilder();
        builder.AppendLine("id,created_at,severity,status,source,title,description");

        foreach (var item in incidents)
        {
            builder.Append(Csv(item.Id)).Append(',')
                .Append(Csv(item.CreatedAt.ToString("O"))).Append(',')
                .Append(Csv(item.Severity)).Append(',')
                .Append(Csv(item.Status)).Append(',')
                .Append(Csv(item.Source)).Append(',')
                .Append(Csv(item.Title)).Append(',')
                .Append(Csv(item.Description))
                .AppendLine();
        }

        return builder.ToString();
    }

    private static string Csv(string? value)
    {
        var text = value ?? "";
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
