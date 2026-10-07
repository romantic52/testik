using System.Text;
using System.Text.Json;
using Aegis.Agent.Services;

namespace Aegis.Agent.Services;

public sealed class ReportService
{
    private readonly TelemetrySamplerService _telemetry;
    private readonly IncidentStoreService _incidents;
    private readonly AlertSettingsService _alerts;
    private readonly NetworkMonitorService _network;
    private readonly WindowsEventLogService _windowsEvents;
    private readonly WindowsServiceMonitorService _windowsServices;

    public ReportService(
        TelemetrySamplerService telemetry,
        IncidentStoreService incidents,
        AlertSettingsService alerts,
        NetworkMonitorService network,
        WindowsEventLogService windowsEvents,
        WindowsServiceMonitorService windowsServices)
    {
        _telemetry = telemetry;
        _incidents = incidents;
        _alerts = alerts;
        _network = network;
        _windowsEvents = windowsEvents;
        _windowsServices = windowsServices;
    }

    public async Task<object> BuildAsync(CancellationToken cancellationToken = default)
    {
        var frame = _telemetry.Latest
            ?? throw new InvalidOperationException("Telemetry is still warming up");

        var incidents = await _incidents.ListAsync(cancellationToken);
        var audit = await _incidents.AuditAsync(250, cancellationToken);
        var alertSettings = await _alerts.GetAsync(cancellationToken);
        var history = _telemetry.GetHistory(TimeSpan.FromMinutes(15));
        var tcpConnections = _network.GetTcpConnections(250);
        var udpListeners = _network.GetUdpListeners(250);
        var systemEvents = _windowsEvents.GetRecent("System", 100);
        var applicationEvents = _windowsEvents.GetRecent("Application", 100);
        var windowsServices = _windowsServices.GetServices(1_000);

        return new
        {
            generatedAt = DateTimeOffset.UtcNow,
            agent = new
            {
                machine = Environment.MachineName,
                version = typeof(ReportService).Assembly.GetName().Version?.ToString() ?? "dev",
                dataDirectory = _incidents.DataDirectory,
                lastSampleAt = frame.Timestamp
            },
            system = frame.System,
            processes = frame.Processes,
            history,
            networkConnections = new
            {
                tcp = tcpConnections,
                udp = udpListeners
            },
            windowsEvents = new
            {
                system = systemEvents,
                application = applicationEvents
            },
            windowsServices,
            alertSettings,
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
        return string.Concat('"', text.Replace("\"", "\"\""), '"');
    }
}
