using System.Net;
using Aegis.Agent.Configuration;
using Aegis.Agent.Endpoints;
using Aegis.Agent.Infrastructure;
using Aegis.Agent.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "AEGIS Agent";
});

var startupOptions = builder.Configuration
    .GetSection(AgentOptions.SectionName)
    .Get<AgentOptions>() ?? new AgentOptions();

builder.WebHost.UseUrls(startupOptions.ListenUrl);

builder.Services
    .AddOptions<AgentOptions>()
    .Bind(builder.Configuration.GetSection(AgentOptions.SectionName))
    .Validate(options => AgentOptions.IsLoopbackListenUrl(options.ListenUrl), "Agent:ListenUrl must be an HTTP(S) loopback URL")
    .Validate(options => options.SampleIntervalMs >= 500, "Agent:SampleIntervalMs must be at least 500")
    .ValidateOnStart();

builder.Services.AddSingleton<HardwareMonitorService>();
builder.Services.AddSingleton<ProcessMonitorService>();
builder.Services.AddSingleton<NetworkMonitorService>();
builder.Services.AddSingleton<WindowsEventLogService>();
builder.Services.AddSingleton<WindowsServiceMonitorService>();
builder.Services.AddSingleton<AgentDiagnosticsService>();
builder.Services.AddSingleton<IncidentStoreService>();
builder.Services.AddSingleton<AlertSettingsService>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddSingleton<TelemetrySamplerService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<TelemetrySamplerService>());
builder.Services.AddHostedService<AlertEngineService>();
builder.Services.AddHostedService<AgentLifecycleService>();

builder.Services
    .AddHttpClient<AetherRelayProxyService>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All
    });

var app = builder.Build();

app.UseAegisSecurity();
app.UseWebSockets();
app.UseAegisFrontend();
app.MapAegisEndpoints();

app.Run();
