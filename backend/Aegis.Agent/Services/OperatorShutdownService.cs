using System.Diagnostics;

namespace Aegis.Agent.Services;

/// <summary>
/// Explicit operator-initiated shutdown. Delay is maintained by the local agent,
/// not by shutdown.exe /t (which implicitly forces applications to close for t > 0).
/// </summary>
public sealed class OperatorShutdownService
{
    public const int DelaySeconds = 45;
    public const string Confirmation = "ВЫКЛЮЧИТЬ";
    public const string IntentHeader = "X-AEGIS-Operator-Intent";
    public const string IntentValue = "local-shutdown-confirmed";

    private readonly object _sync = new();
    private readonly ILogger<OperatorShutdownService> _logger;
    private DateTimeOffset? _scheduledAt;
    private CancellationTokenSource? _pending;

    public OperatorShutdownService(ILogger<OperatorShutdownService> logger) => _logger = logger;

    public static bool IsAuthorized(string? confirmation, string? intent) =>
        string.Equals(confirmation?.Trim(), Confirmation, StringComparison.Ordinal)
        && string.Equals(intent, IntentValue, StringComparison.Ordinal);

    public ShutdownCommandResult Schedule()
    {
        lock (_sync)
        {
            if (!OperatingSystem.IsWindows())
                return new(false, "Реальное выключение поддерживается только в Windows.", 0);

            if (_pending is not null)
                return new(false, "Выключение уже запланировано.", 0);

            var cts = new CancellationTokenSource();
            _pending = cts;
            _scheduledAt = DateTimeOffset.UtcNow;
            _ = RunDelayedShutdownAsync(cts);
            _logger.LogWarning("Operator confirmed graceful shutdown in {DelaySeconds} seconds", DelaySeconds);

            return new(true, "Выключение Windows будет запрошено через 45 секунд.", DelaySeconds);
        }
    }

    public ShutdownCommandResult Cancel()
    {
        lock (_sync)
        {
            if (_pending is null)
                return new(false, "Нет ожидающего выключения или запрос уже передан Windows.", 0);

            var pending = _pending;
            _pending = null;
            _scheduledAt = null;
            pending.Cancel();
            _logger.LogInformation("Operator cancelled pending Windows shutdown");
            return new(true, "Ожидающее выключение отменено.", 0);
        }
    }

    private async Task RunDelayedShutdownAsync(CancellationTokenSource pending)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(DelaySeconds), pending.Token);
            lock (_sync)
            {
                if (!ReferenceEquals(_pending, pending))
                    return;
                _pending = null;
                _scheduledAt = null;
            }

            // Zero seconds avoids the implicit /f of positive shutdown.exe /t values.
            // Open apps may prompt for unsaved changes and block the shutdown.
            if (RunWindowsShutdown("/s", "/t", "0"))
                _logger.LogWarning("Graceful Windows shutdown was requested");
            else
                _logger.LogError("Windows refused the graceful shutdown request");
        }
        catch (OperationCanceledException)
        {
            // Cancelled explicitly in the UI.
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Delayed Windows shutdown failed");
        }
        finally
        {
            pending.Dispose();
        }
    }

    private static bool RunWindowsShutdown(params string[] args)
    {
        try
        {
            var file = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
            var start = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            return process is not null && process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
public sealed record ShutdownCommandResult(bool Success, string Message, int DelaySeconds);
public sealed record OperatorShutdownRequest(string? Confirmation);
