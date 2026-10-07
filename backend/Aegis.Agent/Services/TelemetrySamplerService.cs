using Aegis.Agent.Configuration;
using Aegis.Agent.Models;
using Microsoft.Extensions.Options;

namespace Aegis.Agent.Services;

public sealed class TelemetrySamplerService : BackgroundService
{
    private readonly HardwareMonitorService _hardware;
    private readonly ProcessMonitorService _processes;
    private readonly NetworkMonitorService _network;
    private readonly AgentOptions _options;
    private readonly ILogger<TelemetrySamplerService> _logger;
    private readonly object _sync = new();
    private readonly Queue<TelemetryHistoryPoint> _history = new();

    private TelemetryFrame? _latest;

    public TelemetrySamplerService(
        HardwareMonitorService hardware,
        ProcessMonitorService processes,
        NetworkMonitorService network,
        IOptions<AgentOptions> options,
        ILogger<TelemetrySamplerService> logger)
    {
        _hardware = hardware;
        _processes = processes;
        _network = network;
        _options = options.Value;
        _logger = logger;
    }

    public TelemetryFrame? Latest
    {
        get
        {
            lock (_sync)
                return _latest;
        }
    }

    public IReadOnlyList<TelemetryHistoryPoint> GetHistory(TimeSpan window)
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        lock (_sync)
            return _history.Where(p => p.Timestamp >= cutoff).ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SampleSafeAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.SafeSampleIntervalMs));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await SampleSafeAsync(stoppingToken);
    }

    private Task SampleSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var network = _network.GetSnapshot();
            var hardware = _hardware.Capture(network);
            var processes = _processes.GetProcesses(_options.SafeProcessLimit);
            var frame = new TelemetryFrame(
                hardware.System.Timestamp,
                hardware.System,
                processes,
                hardware.Sensors);

            var gpu = frame.System.Gpus.FirstOrDefault();
            var point = new TelemetryHistoryPoint(
                frame.Timestamp,
                frame.System.CpuLoadPercent,
                frame.System.CpuTemperatureC,
                frame.System.Memory.LoadPercent,
                gpu?.LoadPercent,
                gpu?.TemperatureC,
                frame.System.Network.TotalReceiveBytesPerSecond,
                frame.System.Network.TotalSendBytesPerSecond);

            lock (_sync)
            {
                _latest = frame;
                _history.Enqueue(point);

                var cutoff = DateTimeOffset.UtcNow.AddMinutes(-_options.SafeHistoryMinutes);
                while (_history.Count > 0 && _history.Peek().Timestamp < cutoff)
                    _history.Dequeue();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Telemetry sampling failed");
        }

        return Task.CompletedTask;
    }
}
