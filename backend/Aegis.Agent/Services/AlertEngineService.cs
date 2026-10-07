using Aegis.Agent.Models;

namespace Aegis.Agent.Services;

public sealed class AlertEngineService : BackgroundService
{
    private readonly TelemetrySamplerService _telemetry;
    private readonly IncidentStoreService _incidents;
    private readonly AlertSettingsService _settings;
    private readonly ILogger<AlertEngineService> _logger;
    private readonly Dictionary<string, DateTimeOffset> _started = new();
    private readonly Dictionary<string, DateTimeOffset> _lastTriggered = new();

    public AlertEngineService(
        TelemetrySamplerService telemetry,
        IncidentStoreService incidents,
        AlertSettingsService settings,
        ILogger<AlertEngineService> logger)
    {
        _telemetry = telemetry;
        _incidents = incidents;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var settings = await _settings.GetAsync(stoppingToken);
                if (!settings.Enabled)
                {
                    _started.Clear();
                    continue;
                }

                var frame = _telemetry.Latest;
                if (frame is null)
                    continue;

                await EvaluateFrameAsync(frame, settings, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                _logger.LogWarning(error, "Alert rule evaluation failed");
            }
        }
    }

    private async Task EvaluateFrameAsync(
        TelemetryFrame frame,
        AlertSettings settings,
        CancellationToken cancellationToken)
    {
        var system = frame.System;
        var machine = system.MachineName;
        var sustain = TimeSpan.FromSeconds(settings.SustainSeconds);

        await EvaluateAsync(
            "telemetry-stale",
            DateTimeOffset.UtcNow - frame.Timestamp > TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(5),
            settings,
            () => Build(
                "Потеря актуальной телеметрии",
                machine,
                "Высокий",
                "AEGIS Agent работает, но telemetry snapshot не обновлялся более 15 секунд.",
                "telemetry-stale"),
            cancellationToken);

        await EvaluateAsync(
            "cpu-load",
            system.CpuLoadPercent is double cpu && cpu >= settings.CpuLoad,
            sustain,
            settings,
            () => Build(
                "Критическая нагрузка CPU",
                machine,
                system.CpuLoadPercent >= 99 ? "Критический" : "Высокий",
                $"CPU удерживается на {system.CpuLoadPercent:F1}%, порог {settings.CpuLoad:F0}%.",
                "cpu-load"),
            cancellationToken);

        await EvaluateAsync(
            "cpu-temp",
            system.CpuTemperatureC is double cpuTemp && cpuTemp >= settings.CpuTemp,
            Min(sustain, TimeSpan.FromSeconds(10)),
            settings,
            () => Build(
                "Перегрев CPU",
                machine,
                "Критический",
                $"Температура CPU {system.CpuTemperatureC:F1} °C, порог {settings.CpuTemp:F0} °C.",
                "cpu-temp"),
            cancellationToken);

        await EvaluateAsync(
            "ram-load",
            system.Memory.LoadPercent is double ram && ram >= settings.Ram,
            sustain,
            settings,
            () => Build(
                "Критическая загрузка памяти",
                machine,
                "Высокий",
                $"Использование RAM {system.Memory.LoadPercent:F1}%, порог {settings.Ram:F0}%.",
                "ram-load"),
            cancellationToken);

        foreach (var gpu in system.Gpus)
        {
            var key = "gpu-temp:" + gpu.Name;
            await EvaluateAsync(
                key,
                gpu.TemperatureC is double temp && temp >= settings.GpuTemp,
                Min(sustain, TimeSpan.FromSeconds(10)),
                settings,
                () => Build(
                    "Перегрев GPU",
                    $"{gpu.Name} / {machine}",
                    "Критический",
                    $"Температура GPU {gpu.TemperatureC:F1} °C, порог {settings.GpuTemp:F0} °C.",
                    key),
                cancellationToken);
        }

        foreach (var disk in system.Disks)
        {
            var key = "disk:" + disk.Name;
            await EvaluateAsync(
                key,
                disk.UsedPercent >= settings.Disk,
                TimeSpan.FromSeconds(5),
                settings,
                () => Build(
                    "Заканчивается место на диске",
                    $"{disk.Name} / {machine}",
                    disk.UsedPercent >= 99 ? "Критический" : "Высокий",
                    $"Диск заполнен на {disk.UsedPercent:F1}%, свободно {disk.FreeGb:F1} GB.",
                    key),
                cancellationToken);
        }

        var hot = system.Temperatures
            .Where(t => t.Celsius is not null)
            .Select(t => (double)t.Celsius!.Value)
            .DefaultIfEmpty(0)
            .Max();

        var maxRpm = system.Fans
            .Where(f => f.Rpm is not null)
            .Select(f => (double)f.Rpm!.Value)
            .DefaultIfEmpty(double.NaN)
            .Max();

        await EvaluateAsync(
            "fan-stall",
            hot >= 85 && !double.IsNaN(maxRpm) && maxRpm <= 100,
            TimeSpan.FromSeconds(10),
            settings,
            () => Build(
                "Возможная остановка охлаждения",
                machine,
                "Критический",
                $"Температура достигла {hot:F1} °C, доступные fan sensors показывают не более {maxRpm:F0} RPM.",
                "fan-stall"),
            cancellationToken);

        var hotProcess = frame.Processes.FirstOrDefault(p => p.CpuPercent >= settings.ProcessCpu);
        var processKeys = _started.Keys.Where(k => k.StartsWith("process-cpu:", StringComparison.Ordinal)).ToList();

        if (hotProcess is not null)
        {
            var key = "process-cpu:" + hotProcess.Name;
            await EvaluateAsync(
                key,
                true,
                sustain,
                settings,
                () => Build(
                    "Аномальная нагрузка процесса",
                    $"{hotProcess.Name} (PID {hotProcess.Pid})",
                    hotProcess.CpuPercent >= 95 ? "Высокий" : "Средний",
                    $"Процесс удерживает {hotProcess.CpuPercent:F1}% CPU и {hotProcess.MemoryMb:F1} MB RAM. Это ресурсная аномалия, не автоматический вывод о вредоносности.",
                    key),
                cancellationToken);

            processKeys.Remove(key);
        }

        foreach (var staleKey in processKeys)
            _started.Remove(staleKey);
    }

    private async Task EvaluateAsync(
        string key,
        bool condition,
        TimeSpan sustain,
        AlertSettings settings,
        Func<IncidentRecord> build,
        CancellationToken cancellationToken)
    {
        if (!condition)
        {
            _started.Remove(key);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!_started.TryGetValue(key, out var started))
        {
            _started[key] = now;
            return;
        }

        if (now - started < sustain)
            return;

        var cooldown = TimeSpan.FromMinutes(settings.CooldownMinutes);
        if (_lastTriggered.TryGetValue(key, out var last) && now - last < cooldown)
            return;

        var incident = build();
        await _incidents.UpsertAsync(incident, cancellationToken);
        _lastTriggered[key] = now;
        _started[key] = now;
        _logger.LogWarning("AEGIS rule {RuleKey} created incident {IncidentId}", key, incident.Id);
    }

    private static IncidentRecord Build(
        string title,
        string source,
        string severity,
        string description,
        string ruleKey)
    {
        var now = DateTimeOffset.UtcNow;
        return new IncidentRecord(
            $"AUTO-{now:yyyyMMddHHmmssfff}-{Random.Shared.Next(100, 999)}",
            title,
            source,
            severity,
            "Авто",
            description,
            new[] { $"{DateTime.Now:HH:mm:ss} — backend rule {ruleKey} сработало" },
            true,
            false,
            ruleKey,
            now);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) =>
        left <= right ? left : right;
}
