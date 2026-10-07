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
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyHeader()
              .AllowAnyMethod());
});

var app = builder.Build();

app.UseCors();
app.UseWebSockets();

var repoRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", ".."));
var indexPath = Path.Combine(repoRoot, "index.html");

if (File.Exists(indexPath))
{
    var provider = new PhysicalFileProvider(repoRoot);
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
