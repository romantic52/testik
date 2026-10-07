using System.Text;
using System.Text.Json;
using System.IO.Compression;
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
    private readonly AgentDiagnosticsService _diagnostics;

    public ReportService(
        TelemetrySamplerService telemetry,
        IncidentStoreService incidents,
        AlertSettingsService alerts,
        NetworkMonitorService network,
        WindowsEventLogService windowsEvents,
        WindowsServiceMonitorService windowsServices,
        AgentDiagnosticsService diagnostics)
    {
        _telemetry = telemetry;
        _incidents = incidents;
        _alerts = alerts;
        _network = network;
        _windowsEvents = windowsEvents;
        _windowsServices = windowsServices;
        _diagnostics = diagnostics;
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
        var diagnostics = _diagnostics.Capture();

        return new
        {
            generatedAt = DateTimeOffset.UtcNow,
            diagnostics,
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

    public async Task<byte[]> BuildSupportBundleAsync(CancellationToken cancellationToken = default)
    {
        var json = await BuildJsonAsync(cancellationToken);
        var csv = await BuildCsvAsync(cancellationToken);

        await using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteEntryAsync(archive, "report.json", json, cancellationToken);
            await WriteEntryAsync(archive, "incidents.csv", csv, cancellationToken);
            await WriteEntryAsync(
                archive,
                "README.txt",
                "AEGIS support bundle\r\n" +
                $"Generated: {DateTimeOffset.UtcNow:O}\r\n" +
                "Contains local monitoring/report data only. AETHER password and Ratchet state are not included.\r\n",
                cancellationToken);
        }

        return output.ToArray();
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string name,
        string content,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteAsync(content);
        await writer.FlushAsync();
    }

    private static string Csv(string? value)
    {
        var text = value ?? "";
        return string.Concat('"', text.Replace("\"", "\"\""), '"');
    }
}
