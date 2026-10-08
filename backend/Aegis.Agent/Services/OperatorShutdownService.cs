using System.Diagnostics;

namespace Aegis.Agent.Services;

/// <summary>
/// An explicit, operator-initiated Windows shutdown. This is not a BSOD or crash.
/// The OS gets a grace period to close applications; "/f" is deliberately not used.
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

    public OperatorShutdownService(ILogger<OperatorShutdownService> logger)
    {
        _logger = logger;
    }

    public static bool IsAuthorized(string? confirmation, string? intent) =>
        string.Equals(confirmation?.Trim(), Confirmation, StringComparison.Ordinal)
        && string.Equals(intent, IntentValue, StringComparison.Ordinal);

    public ShutdownCommandResult Schedule()
    {
        lock (_sync)
        {
            if (!OperatingSystem.IsWindows())
                return new(false, "Реальное выключение поддерживается только в Windows.", 0);

            if (_scheduledAt.HasValue &&
                DateTimeOffset.UtcNow < _scheduledAt.Value.AddSeconds(DelaySeconds))
                return new(false, "Выключение уже запланировано.", 0);

            var result = RunWindowsShutdown("/s", "/t", DelaySeconds.ToString(),
                "/c", "AEGIS: подтверждённое выключение оператором");
            if (!result)
                return new(false, "Windows отказала в планировании выключения.", 0);

            _scheduledAt = DateTimeOffset.UtcNow;
            _logger.LogWarning(
                "Operator confirmed Windows shutdown in {DelaySeconds} seconds",
                DelaySeconds);
            return new(true, "Windows выключится через 45 секунд.", DelaySeconds);
        }
    }

    public ShutdownCommandResult Cancel()
    {
        lock (_sync)
        {
            if (!_scheduledAt.HasValue)
                return new(false, "В AEGIS нет запланированного выключения.", 0);

            if (!RunWindowsShutdown("/a"))
                return new(false, "Windows не смогла отменить выключение.", 0);

            _scheduledAt = null;
            _logger.LogInformation("Operator cancelled the scheduled Windows shutdown");
            return new(true, "Запланированное выключение отменено.", 0);
        }
    }

    private static bool RunWindowsShutdown(params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo("shutdown.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process is null) return false;
            return process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public sealed record ShutdownCommandResult(
    bool Success, string Message, int DelaySeconds);

public sealed record OperatorShutdownRequest(string? Confirmation);
