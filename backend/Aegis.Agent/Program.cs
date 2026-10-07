using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Aegis.Agent.Services;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://127.0.0.1:8765");
builder.Services.AddSingleton<HardwareMonitorService>();
builder.Services.AddSingleton<ProcessMonitorService>();
builder.Services.AddSingleton<NetworkMonitorService>();
builder.Services.AddSingleton<AetherRelayProxyService>();
builder.Services.AddSingleton<IncidentStoreService>();
builder.Services.AddSingleton<ReportService>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins("http://127.0.0.1:8765", "http://localhost:8765")
              .AllowAnyHeader()
              .AllowAnyMethod());
});

var app = builder.Build();

app.UseCors();
app.UseWebSockets();

var publishedWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var sourceWebRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", ".."));
var webRoot = File.Exists(Path.Combine(publishedWebRoot, "index.html"))
    ? publishedWebRoot
    : sourceWebRoot;
var indexPath = Path.Combine(webRoot, "index.html");

if (File.Exists(indexPath))
{
    var provider = new PhysicalFileProvider(webRoot);
    var defaultFiles = new DefaultFilesOptions { FileProvider = provider };
    defaultFiles.DefaultFileNames.Clear();
    defaultFiles.DefaultFileNames.Add("index.html");

    app.UseDefaultFiles(defaultFiles);
    app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
}

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    service = "AEGIS Agent",
    machine = Environment.MachineName,
    timestamp = DateTimeOffset.UtcNow
}));

app.MapGet("/api/system", (HardwareMonitorService hardware, NetworkMonitorService network) =>
    Results.Ok(hardware.GetSystemSnapshot(network.GetSnapshot())));

app.MapGet("/api/hardware", (HardwareMonitorService hardware) =>
    Results.Ok(hardware.GetSensors()));

app.MapGet("/api/processes", (int? limit, ProcessMonitorService processes) =>
    Results.Ok(processes.GetProcesses(Math.Clamp(limit ?? 50, 1, 200))));

app.MapGet("/api/network", (NetworkMonitorService network) =>
    Results.Ok(network.GetSnapshot()));

app.MapGet("/api/processes/{pid:int}", (int pid) =>
{
    var details = ProcessDetailsReader.Read(pid);
    return details is null ? Results.NotFound() : Results.Ok(details);
});

app.MapGet("/api/incidents", async (IncidentStoreService incidents, CancellationToken cancellationToken) =>
    Results.Ok(await incidents.ListAsync(cancellationToken)));

app.MapPut("/api/incidents/{id}", async (
    string id,
    IncidentRecord incident,
    IncidentStoreService incidents,
    CancellationToken cancellationToken) =>
{
    if (!id.Equals(incident.Id, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { detail = "Incident id mismatch" });

    return Results.Ok(await incidents.UpsertAsync(incident, cancellationToken));
});

app.MapDelete("/api/incidents/{id}", async (
    string id,
    IncidentStoreService incidents,
    CancellationToken cancellationToken) =>
{
    return await incidents.DeleteAsync(id, cancellationToken)
        ? Results.NoContent()
        : Results.NotFound();
});

app.MapGet("/api/audit", async (
    int? limit,
    IncidentStoreService incidents,
    CancellationToken cancellationToken) =>
    Results.Ok(await incidents.AuditAsync(limit ?? 200, cancellationToken)));

app.MapPost("/api/audit", async (
    AuditRecord record,
    IncidentStoreService incidents,
    CancellationToken cancellationToken) =>
{
    await incidents.AppendAuditAsync(record, cancellationToken);
    return Results.Accepted();
});

app.MapGet("/api/reports/current.json", async (
    ReportService reports,
    CancellationToken cancellationToken) =>
{
    var json = await reports.BuildJsonAsync(cancellationToken);
    return Results.Text(json, "application/json", Encoding.UTF8);
});

app.MapGet("/api/reports/incidents.csv", async (
    ReportService reports,
    CancellationToken cancellationToken) =>
{
    var csv = await reports.BuildCsvAsync(cancellationToken);
    return Results.Text(csv, "text/csv; charset=utf-8", Encoding.UTF8);
});

app.MapPost("/api/aether/relay", async (
    AetherRelayRequest request,
    AetherRelayProxyService proxy,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await proxy.ForwardAsync(request, cancellationToken);
        return Results.Content(
            result.Content,
            result.ContentType,
            Encoding.UTF8,
            result.StatusCode);
    }
    catch (ArgumentException error)
    {
        return Results.BadRequest(new { detail = error.Message });
    }
    catch (HttpRequestException)
    {
        return Results.Json(
            new { detail = "AETHER relay is unreachable" },
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Json(
            new { detail = "AETHER relay request timed out" },
            statusCode: StatusCodes.Status504GatewayTimeout);
    }
});

app.Map("/ws/monitor", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var hardware = context.RequestServices.GetRequiredService<HardwareMonitorService>();
    var processes = context.RequestServices.GetRequiredService<ProcessMonitorService>();
    var network = context.RequestServices.GetRequiredService<NetworkMonitorService>();

    using var socket = await context.WebSockets.AcceptWebSocketAsync();

    while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
    {
        var networkSnapshot = network.GetSnapshot();
        var payload = new
        {
            type = "telemetry",
            system = hardware.GetSystemSnapshot(networkSnapshot),
            processes = processes.GetProcesses(20)
        };

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        await socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            context.RequestAborted);

        try
        {
            await Task.Delay(1000, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
});

app.Run();
