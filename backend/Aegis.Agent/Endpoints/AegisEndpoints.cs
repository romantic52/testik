using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Aegis.Agent.Configuration;
using Aegis.Agent.Infrastructure;
using Aegis.Agent.Models;
using Aegis.Agent.Services;
using Microsoft.Extensions.Options;

namespace Aegis.Agent.Endpoints;

public static class AegisEndpoints
{
    public static WebApplication MapAegisEndpoints(this WebApplication app)
    {
        MapApiSurface(app.MapGroup("/api"), "legacy");
        MapApiSurface(app.MapGroup("/api/v1"), "v1");
        app.MapTelemetryWebSocket();

        return app;
    }

    private static void MapApiSurface(RouteGroupBuilder api, string apiVersion)
    {
        api.MapGet("/version", () => Results.Ok(new
        {
            api = apiVersion,
            service = "AEGIS Agent",
            version = typeof(AegisEndpoints).Assembly.GetName().Version?.ToString() ?? "dev"
        }));

        api.MapGet("/health", (
            TelemetrySamplerService telemetry,
            IncidentStoreService incidents,
            IOptions<AgentOptions> options) =>
        {
            var latest = telemetry.Latest;
            var value = options.Value;
            var now = DateTimeOffset.UtcNow;
            var staleAfter = TimeSpan.FromMilliseconds(
                Math.Max(10_000, value.SafeSampleIntervalMs * 3));

            var statusName = latest is null
                ? "warming_up"
                : now - latest.Timestamp > staleAfter
                    ? "degraded"
                    : "ok";

            var status = new AgentStatus(
                statusName,
                "AEGIS Agent",
                typeof(AegisEndpoints).Assembly.GetName().Version?.ToString() ?? "dev",
                Environment.MachineName,
                now,
                latest?.Timestamp,
                value.SafeSampleIntervalMs,
                telemetry.GetHistory(TimeSpan.FromMinutes(value.SafeHistoryMinutes)).Count,
                incidents.DataDirectory);

            return Results.Ok(status);
        });

        api.MapGet("/health/live", () =>
            Results.Ok(new
            {
                status = "ok",
                timestamp = DateTimeOffset.UtcNow
            }));

        api.MapGet("/health/ready", async (
            TelemetrySamplerService telemetry,
            StateDatabase database,
            IOptions<AgentOptions> options,
            CancellationToken cancellationToken) =>
        {
            var databaseStatus = await database.ProbeAsync(cancellationToken);
            var latest = telemetry.Latest;

            if (latest is null)
            {
                return Results.Json(
                    new
                    {
                        status = "warming_up",
                        database = databaseStatus
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var staleAfter = TimeSpan.FromMilliseconds(
                Math.Max(10_000, options.Value.SafeSampleIntervalMs * 3));
            var age = DateTimeOffset.UtcNow - latest.Timestamp;
            var ready = age <= staleAfter && databaseStatus.Ready;

            return ready
                ? Results.Ok(new
                {
                    status = "ready",
                    lastSampleAt = latest.Timestamp,
                    sampleAgeSeconds = age.TotalSeconds,
                    database = databaseStatus
                })
                : Results.Json(
                    new
                    {
                        status = databaseStatus.Ready ? "stale" : "storage_unavailable",
                        lastSampleAt = latest.Timestamp,
                        sampleAgeSeconds = age.TotalSeconds,
                        database = databaseStatus
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        api.MapGet("/diagnostics", (
            AgentDiagnosticsService diagnostics,
            StateDatabase database) =>
        {
            var current = diagnostics.Capture();
            return Results.Ok(new
            {
                agent = current,
                persistence = new
                {
                    provider = "sqlite",
                    database = Path.GetFileName(database.DatabasePath),
                    revision = database.Revision
                }
            });
        });

        api.MapGet("/system", (TelemetrySamplerService telemetry) =>
            LatestOrUnavailable(telemetry, frame => frame.System));

        api.MapGet("/hardware", (TelemetrySamplerService telemetry) =>
            LatestOrUnavailable(telemetry, frame => frame.Sensors));

        api.MapGet("/network", (TelemetrySamplerService telemetry) =>
            LatestOrUnavailable(telemetry, frame => frame.System.Network));

        api.MapGet("/network/connections", (
            int? limit,
            NetworkMonitorService network) =>
            Results.Ok(network.GetTcpConnections(limit ?? 200)));

        api.MapGet("/network/udp", (
            int? limit,
            NetworkMonitorService network) =>
            Results.Ok(network.GetUdpListeners(limit ?? 200)));

        api.MapGet("/windows/services", (
            int? limit,
            WindowsServiceMonitorService services) =>
            Results.Ok(services.GetServices(limit ?? 500)));

        api.MapGet("/windows/events", (
            string? log,
            int? limit,
            WindowsEventLogService events) =>
        {
            try
            {
                return Results.Ok(events.GetRecent(log ?? "System", limit ?? 100));
            }
            catch (ArgumentException error)
            {
                return Results.BadRequest(new { detail = error.Message });
            }
        });

        api.MapGet("/processes", (
            int? limit,
            TelemetrySamplerService telemetry) =>
            LatestOrUnavailable(
                telemetry,
                frame => frame.Processes
                    .Take(Math.Clamp(limit ?? 50, 1, 200))
                    .ToList()));

        api.MapGet("/processes/{pid:int}", (int pid) =>
        {
            var details = ProcessDetailsReader.Read(pid);
            return details is null
                ? Results.NotFound()
                : Results.Ok(details);
        });

        api.MapGet("/history", async (
            int? seconds,
            int? maxPoints,
            TelemetrySamplerService telemetry,
            TelemetryHistoryStoreService historyStore,
            IOptions<AgentOptions> options,
            CancellationToken cancellationToken) =>
        {
            var maxSeconds = options.Value.SafeTelemetryRetentionHours * 60 * 60;
            var windowSeconds = Math.Clamp(
                seconds ?? options.Value.SafeHistoryMinutes * 60, 10, maxSeconds);
            var now = DateTimeOffset.UtcNow;
            var from = now.AddSeconds(-windowSeconds);

            var persisted = await historyStore.ReadAsync(
                from, now,
                Math.Min(options.Value.SafeTelemetryMaxRows, 100_000),
                cancellationToken);

            var live = telemetry.GetHistory(TimeSpan.FromSeconds(windowSeconds));
            return Results.Ok(TelemetryHistoryQuery.MergeAndDownsample(
                persisted, live, from, now, maxPoints ?? 600));
        });

        api.MapGet("/connectors", (TelemetrySamplerService telemetry) =>
        {
            var frame = telemetry.Latest;
            return Results.Ok(new
            {
                monitoring = new
                {
                    status = frame is null ? "warming_up" : "online",
                    real = true
                },
                hardware = new
                {
                    status = frame?.Sensors.Count > 0 ? "online" : "unavailable",
                    real = true
                },
                processes = new
                {
                    status = frame is null ? "warming_up" : "online",
                    real = true
                },
                network = new
                {
                    status = frame?.System.Network.Adapters.Count > 0
                        ? "online"
                        : "unavailable",
                    real = true
                },
                aether = new
                {
                    status = "client_managed",
                    real = true
                },
                accessControl = new
                {
                    status = "not_configured",
                    real = false
                },
                cameras = new
                {
                    status = "not_configured",
                    real = false
                }
            });
        });

        MapIncidentEndpoints(api);
        MapAlertSettingsEndpoints(api);
        MapReportEndpoints(api);
        MapAetherEndpoints(api);
        MapOperatorShutdownEndpoints(api);
    }

    private static void MapIncidentEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/incidents", async (
            IncidentStoreService incidents,
            CancellationToken cancellationToken) =>
            Results.Ok(await incidents.ListAsync(cancellationToken)));

        api.MapPut("/incidents/{id}", async (
            string id,
            IncidentRecord incident,
            IncidentStoreService incidents,
            CancellationToken cancellationToken) =>
        {
            if (!id.Equals(incident.Id, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { detail = "Incident id mismatch" });

            try
            {
                return Results.Ok(
                    await incidents.UpsertAsync(incident, cancellationToken));
            }
            catch (ArgumentException error)
            {
                return Results.BadRequest(new { detail = error.Message });
            }
        });

        api.MapDelete("/incidents/{id}", async (
            string id,
            IncidentStoreService incidents,
            CancellationToken cancellationToken) =>
        {
            return await incidents.DeleteAsync(id, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        });

        api.MapGet("/audit", async (
            int? limit,
            IncidentStoreService incidents,
            CancellationToken cancellationToken) =>
            Results.Ok(
                await incidents.AuditAsync(limit ?? 200, cancellationToken)));

        api.MapPost("/audit", async (
            AuditRecord record,
            IncidentStoreService incidents,
            CancellationToken cancellationToken) =>
        {
            await incidents.AppendAuditAsync(record, cancellationToken);
            return Results.Accepted();
        });
    }

    private static void MapAlertSettingsEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/settings/alerts", async (
            AlertSettingsService settings,
            CancellationToken cancellationToken) =>
            Results.Ok(await settings.GetAsync(cancellationToken)));

        api.MapPut("/settings/alerts", async (
            AlertSettings value,
            AlertSettingsService settings,
            IncidentStoreService incidents,
            CancellationToken cancellationToken) =>
        {
            var saved = await settings.SaveAsync(value, cancellationToken);

            await incidents.AppendAuditAsync(
                new AuditRecord(
                    DateTimeOffset.UtcNow,
                    "settings.alerts.updated",
                    "alert-rules",
                    "Alert thresholds updated",
                    "AEGIS API"),
                cancellationToken);

            return Results.Ok(saved);
        });
    }

    private static void MapReportEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/reports/current.json", async (
            ReportService reports,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var json = await reports.BuildJsonAsync(cancellationToken);
                return Results.Text(json, "application/json", Encoding.UTF8);
            }
            catch (InvalidOperationException error)
            {
                return Results.Json(
                    new { detail = error.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        api.MapGet("/reports/incidents.csv", async (
            ReportService reports,
            CancellationToken cancellationToken) =>
        {
            var csv = await reports.BuildCsvAsync(cancellationToken);
            return Results.Text(
                csv,
                "text/csv; charset=utf-8",
                Encoding.UTF8);
        });

        api.MapGet("/reports/bundle.zip", async (
            ReportService reports,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var bundle = await reports.BuildSupportBundleAsync(cancellationToken);
                return Results.File(
                    bundle,
                    "application/zip",
                    $"aegis-support-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip");
            }
            catch (InvalidOperationException error)
            {
                return Results.Json(
                    new { detail = error.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }

    private static void MapAetherEndpoints(RouteGroupBuilder api)
    {
        api.MapPost("/aether/relay", async (
            AetherRelayRequest request,
            AetherRelayProxyService proxy,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await proxy.ForwardAsync(
                    request,
                    cancellationToken);

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
            catch (TaskCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                return Results.Json(
                    new { detail = "AETHER relay request timed out" },
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
        });
    }

    private static void MapOperatorShutdownEndpoints(RouteGroupBuilder api)
    {
        // The web UI uses a typed confirmation and a custom header. Refuse power
        // operations from non-interactive Windows Service processes.
        api.MapPost("/system/shutdown", async (
            OperatorShutdownRequest request,
            HttpContext context,
            OperatorShutdownService shutdown,
            IncidentStoreService incidents,
            CancellationToken cancellationToken) =>
        {
            if (!Environment.UserInteractive)
                return Results.Json(
                    new { detail = "Реальное выключение разрешено только в интерактивном режиме AEGIS." },
                    statusCode: StatusCodes.Status403Forbidden);

            if (!OperatorShutdownService.IsAuthorized(
                request.Confirmation,
                context.Request.Headers[OperatorShutdownService.IntentHeader].ToString()))
                return Results.BadRequest(new
                {
                    detail = "Для выключения Windows нужно ввести ВЫКЛЮЧИТЬ и подтвердить действие."
                });

            var result = shutdown.Schedule();
            if (!result.Success)
                return Results.Json(new { detail = result.Message },
                    statusCode: StatusCodes.Status409Conflict);

            await incidents.AppendAuditAsync(
                new AuditRecord(
                    DateTimeOffset.UtcNow,
                    "system.shutdown.scheduled",
                    Environment.MachineName,
                    "Operator scheduled Windows shutdown with a 45-second grace period.",
                    "AEGIS local operator"),
                cancellationToken);

            return Results.Ok(result);
        });

        api.MapPost("/system/shutdown/cancel", async (
            HttpContext context,
            OperatorShutdownService shutdown,
            IncidentStoreService incidents,
            CancellationToken cancellationToken) =>
        {
            if (!Environment.UserInteractive)
                return Results.Json(
                    new { detail = "Отмена через AEGIS доступна только в интерактивном режиме." },
                    statusCode: StatusCodes.Status403Forbidden);

            if (!string.Equals(
                context.Request.Headers[OperatorShutdownService.IntentHeader].ToString(),
                OperatorShutdownService.IntentValue,
                StringComparison.Ordinal))
                return Results.BadRequest(new { detail = "Не подтверждён локальный оператор." });

            var result = shutdown.Cancel();
            if (!result.Success)
                return Results.Json(new { detail = result.Message },
                    statusCode: StatusCodes.Status409Conflict);

            await incidents.AppendAuditAsync(
                new AuditRecord(
                    DateTimeOffset.UtcNow,
                    "system.shutdown.cancelled",
                    Environment.MachineName,
                    "Operator cancelled the scheduled Windows shutdown.",
                    "AEGIS local operator"),
                cancellationToken);

            return Results.Ok(result);
        });
    }

    private static void MapTelemetryWebSocket(this WebApplication app)
    {
        app.Map("/ws/monitor", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode =
                    StatusCodes.Status400BadRequest;
                return;
            }

            var telemetry = context.RequestServices
                .GetRequiredService<TelemetrySamplerService>();
            var incidents = context.RequestServices
                .GetRequiredService<IncidentStoreService>();
            var options = context.RequestServices
                .GetRequiredService<IOptions<AgentOptions>>()
                .Value;

            using var socket =
                await context.WebSockets.AcceptWebSocketAsync();

            DateTimeOffset? lastTimestamp = null;

            while (socket.State == WebSocketState.Open
                   && !context.RequestAborted.IsCancellationRequested)
            {
                var frame = telemetry.Latest;
                if (frame is not null && frame.Timestamp != lastTimestamp)
                {
                    lastTimestamp = frame.Timestamp;

                    var payload = new
                    {
                        type = "telemetry",
                        system = frame.System,
                        processes = frame.Processes,
                        incidentRevision = incidents.Revision
                    };

                    var json = JsonSerializer.Serialize(payload);
                    var bytes = Encoding.UTF8.GetBytes(json);

                    await socket.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        context.RequestAborted);
                }

                try
                {
                    await Task.Delay(
                        Math.Max(250, options.SafeSampleIntervalMs / 2),
                        context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
    }

    private static IResult LatestOrUnavailable<T>(
        TelemetrySamplerService telemetry,
        Func<TelemetryFrame, T> select)
    {
        var frame = telemetry.Latest;
        return frame is null
            ? Results.Json(
                new { detail = "Telemetry is warming up" },
                statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Ok(select(frame));
    }
}
