using System.Globalization;
using System.Text;
using Aegis.Agent.Infrastructure;
using Aegis.Agent.Services;

namespace Aegis.Agent.Endpoints;

/// <summary>Network journal, access control, cameras, agent logs and sensor capabilities.</summary>
public static class SecurityEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/network/live", (ConnectionTrackerService tracker) =>
            Results.Ok(new { byteCounters = tracker.ByteCountersAvailable, connections = tracker.Live() }));

        api.MapGet("/network/processes", (ConnectionTrackerService tracker) =>
            Results.Ok(new { byteCounters = tracker.ByteCountersAvailable, processes = tracker.TrafficByProcess() }));

        api.MapGet("/network/journal", async (string? q, string? scope, int? hours, int? limit, ConnectionTrackerService tracker, CancellationToken token) =>
            Results.Ok(await tracker.JournalAsync(q, scope, hours ?? 24, limit ?? 500, token)));

        api.MapGet("/network/journal.csv", async (string? q, string? scope, int? hours, ConnectionTrackerService tracker, CancellationToken token) =>
        {
            var rows = await tracker.JournalAsync(q, scope, hours ?? 24, 20_000, token);
            var csv = new StringBuilder("opened_at;closed_at;process;pid;process_path;direction;scope;service;remote_host;remote_address;remote_port;local_address;local_port;state;bytes_in;bytes_out\n");
            foreach (var r in rows)
                csv.AppendJoin(';', new object?[] { r.OpenedAt.ToString("O"), r.ClosedAt?.ToString("O"), r.ProcessName, r.ProcessId, r.ProcessPath, r.Direction, r.Scope, r.Service,
                    r.RemoteHost, r.RemoteAddress, r.RemotePort, r.LocalAddress, r.LocalPort, r.State, r.BytesIn, r.BytesOut }.Select(Csv)).Append('\n');
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(),
                "text/csv; charset=utf-8", $"aegis-connections-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        });

        api.MapGet("/access/summary", async (AccessControlService access, CancellationToken token) =>
            Results.Ok(await access.SummaryAsync(token)));

        api.MapGet("/access/events", async (string? source, int? limit, AccessControlService access, CancellationToken token) =>
            Results.Ok(await access.ListAsync(source, limit ?? 200, token)));

        api.MapGet("/access/intake", async (AccessControlService access, CancellationToken token) =>
            Results.Ok(new { endpoint = "/api/v1/access/events", header = "X-AEGIS-Access-Token", token = await access.IntakeTokenAsync(token) }));

        // Local bridge of a physical access controller. Same loopback/origin rules as the rest of the API, plus a token.
        api.MapPost("/access/events", async (ControllerAccessEvent body, HttpContext context, AccessControlService access, CancellationToken token) =>
        {
            var expected = await access.IntakeTokenAsync(token);
            if (!string.Equals(context.Request.Headers["X-AEGIS-Access-Token"].ToString(), expected, StringComparison.Ordinal))
                return Results.Json(new { detail = "Неверный X-AEGIS-Access-Token" }, statusCode: StatusCodes.Status401Unauthorized);
            await access.AcceptControllerEventAsync(body, token);
            return Results.Accepted();
        });

        api.MapGet("/cameras", async (CameraService cameras, CancellationToken token) => Results.Ok(await cameras.ViewAsync(token)));

        api.MapPut("/cameras", async (List<CameraInput> body, CameraService cameras, CancellationToken token) =>
        {
            try { return Results.Ok(await cameras.SaveAsync(body, token)); }
            catch (ArgumentException error) { return Results.BadRequest(new { detail = error.Message }); }
        });

        api.MapGet("/cameras/{id}/frame", async (string id, CameraService cameras, CancellationToken token) =>
        {
            try
            {
                var (data, type) = await cameras.FrameAsync(id, token);
                return Results.File(data, type);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidDataException or ArgumentException or IOException or System.Net.Sockets.SocketException)
            {
                return Results.Json(new { detail = error is TaskCanceledException ? "Камера не ответила" : error.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        api.MapPost("/cameras/{id}/motion", async (string id, MotionReport body, CameraService cameras, CancellationToken token) =>
            Results.Ok(new { incident = await cameras.ReportMotionAsync(id, Math.Clamp(body.ChangedPercent, 0, 100), token) }));

        api.MapGet("/logs/agent", (int? lines, FileLoggerProvider logs) =>
            Results.Ok(new { file = logs.CurrentFile, lines = logs.Tail(lines ?? 400) }));

        api.MapGet("/sensors/capabilities", () => Results.Ok(SensorCapabilities.Detect()));

        MapProtection(api);
    }

    public const string ScanIntentHeader = "X-AEGIS-Scan-Intent";
    public const string ScanIntentValue = "defender-scan";

    private static void MapProtection(RouteGroupBuilder api)
    {
        api.MapGet("/disks", (DiskMonitorService disks) => Results.Ok(disks.GetReport()));

        api.MapGet("/ports", (PortsService ports) =>
            Results.Ok(ports.GetOpenPorts().Select(p => new
            {
                p.Protocol, p.Address, p.Port, p.Exposure, p.ProcessId, p.ProcessName, p.ProcessPath, p.Service,
                trust = p.ProcessPath is null ? null : SignatureInspector.Inspect(p.ProcessPath)
            })));

        api.MapGet("/devices/events", async (int? limit, ProtectionWatchService watch, CancellationToken token) =>
            Results.Ok(await watch.DeviceEventsAsync(limit ?? 200, token)));

        api.MapGet("/defender/status", (DefenderService defender) => Results.Ok(defender.GetStatus()));

        api.MapGet("/defender/detections", (int? limit, DefenderService defender) =>
            Results.Ok(defender.GetDetections(limit ?? 100)));

        // Asks Microsoft Defender to scan; AEGIS itself never deletes or quarantines anything.
        api.MapPost("/defender/scan", async (DefenderScanRequest body, HttpContext context, DefenderService defender,
            IncidentStoreService incidents, CancellationToken token) =>
        {
            if (!string.Equals(context.Request.Headers[ScanIntentHeader].ToString(), ScanIntentValue, StringComparison.Ordinal))
                return Results.BadRequest(new { detail = "Требуется подтверждение проверки оператором." });
            var target = string.IsNullOrWhiteSpace(body.Path) ? "Быстрая проверка" : body.Path.Trim();
            await incidents.AppendAuditAsync(new AuditRecord(DateTimeOffset.UtcNow, "defender.scan.started", target,
                "Operator started a Microsoft Defender scan", "AEGIS local operator"), token);
            try
            {
                var result = await defender.ScanAsync(body.Path, token);
                await incidents.AppendAuditAsync(new AuditRecord(DateTimeOffset.UtcNow,
                    result.ThreatsFound ? "defender.scan.threats" : "defender.scan.finished", target,
                    $"Defender exit code {result.ExitCode}", "Microsoft Defender"), CancellationToken.None);
                if (result.ThreatsFound)
                    await incidents.UpsertAsync(new IncidentRecord($"DEFENDER-SCAN-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
                        "Defender нашёл угрозы при проверке", "Microsoft Defender / " + Environment.MachineName, "Критический", "Авто",
                        "Проверка: " + target + ". Defender обработал найденное по своим правилам; подробности — во вкладке «Защита» и в Защитнике Windows.",
                        [$"{DateTimeOffset.Now:HH:mm:ss} — код завершения MpCmdRun {result.ExitCode}"], true, false, "protection:defender", DateTimeOffset.UtcNow),
                        CancellationToken.None);
                return Results.Ok(result);
            }
            catch (InvalidOperationException error) { return Results.Json(new { detail = error.Message }, statusCode: StatusCodes.Status409Conflict); }
            catch (Exception error) when (error is FileNotFoundException or ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Results.BadRequest(new { detail = error.Message });
            }
        });
    }

    private static string Csv(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        return text.IndexOfAny([';', '"', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }
}

public sealed record MotionReport(double ChangedPercent);
public sealed record DefenderScanRequest(string? Path);

/// <summary>Why a sensor may be missing: elevation, the PawnIO driver LibreHardwareMonitor needs for CPU/board sensors, VM.</summary>
public static class SensorCapabilities
{
    public static object Detect()
    {
        bool pawnIo;
        try { pawnIo = System.ServiceProcess.ServiceController.GetServices().Any(s => s.ServiceName.Equals("PawnIO", StringComparison.OrdinalIgnoreCase)); }
        catch { pawnIo = false; }
        var elevated = Environment.IsPrivilegedProcess;
        var service = !Environment.UserInteractive;
        var cpuTemperatureHint = !pawnIo
            ? "Для температуры CPU и датчиков материнской платы нужен драйвер PawnIO (https://pawnio.eu), который использует LibreHardwareMonitor. После установки драйвера перезапусти AEGIS."
            : !elevated
                ? "Драйвер PawnIO установлен, но AEGIS запущен без прав администратора: установи службу (install-service.bat)."
                : null;
        return new { elevated, service, pawnIoDriver = pawnIo, cpuTemperatureHint };
    }
}
