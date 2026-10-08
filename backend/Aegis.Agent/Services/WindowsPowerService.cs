using System.Diagnostics;
using System.Security.Cryptography;

namespace Aegis.Agent.Services;

/// <summary>
/// Explicit, cancellable, local Windows shutdown. Never forces applications closed.
/// A fresh one-time approval and an exact confirmation phrase are required.
/// </summary>
public sealed class WindowsPowerService
{
    private readonly object _gate = new();
    private readonly ILogger<WindowsPowerService> _logger;
    private string? _approvalToken;
    private DateTimeOffset _approvalExpires;
    private string? _shutdownReceipt;
    private DateTimeOffset? _scheduledAt;
    public const int DelaySeconds = 60;
    public const string ConfirmationPhrase = "ВЫКЛЮЧИТЬ";

    public WindowsPowerService(ILogger<WindowsPowerService> logger)
    {
        _logger = logger;
    }

    public object Capabilities() => new
    {
        supported = OperatingSystem.IsWindows(),
        mode = "explicit-confirmation",
        shutdownDelaySeconds = DelaySeconds,
        canCancel = true,
        warning = "Windows will shut down after confirmation. Save all open work."
    };

    public PowerApproval Prepare()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows only");

        lock (_gate)
        {
            _approvalToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _approvalExpires = DateTimeOffset.UtcNow.AddSeconds(90);
            return new PowerApproval(_approvalToken, _approvalExpires, DelaySeconds);
        }
    }

    public PowerResult Schedule(PowerShutdownRequest request)
    {
        if (!OperatingSystem.IsWindows()) return PowerResult.Failure("Only available on Windows");
        lock (_gate)
        {
            if (_approvalToken is null || DateTimeOffset.UtcNow > _approvalExpires
                || !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(_approvalToken),
                    System.Text.Encoding.UTF8.GetBytes(request.Token ?? "")))
                return PowerResult.Failure("Confirmation expired. Start again.");

            // Consume approval even if the operator typed the wrong phrase.
            _approvalToken = null;
            if (!string.Equals(request.Confirmation, ConfirmationPhrase, StringComparison.Ordinal)
                || request.AcknowledgedDataLoss != true)
                return PowerResult.Failure("Explicit confirmation and data-loss acknowledgement required.");

            if (_scheduledAt is not null)
                return PowerResult.Failure("A Windows shutdown is already scheduled.");

            var result = ExecuteShutdown("/s", "/t", DelaySeconds.ToString(),
                "/c", "AEGIS: confirmed Windows shutdown. Save your work.");
            if (!result.Success) return result;
            _shutdownReceipt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            _scheduledAt = DateTimeOffset.UtcNow;
            _logger.LogWarning("Operator scheduled Windows shutdown with {Delay}s cancellation window", DelaySeconds);
            return new PowerResult(true, "Shutdown scheduled", _shutdownReceipt, DelaySeconds);
        }
    }

    public PowerResult Cancel(PowerCancelRequest request)
    {
        if (!OperatingSystem.IsWindows()) return PowerResult.Failure("Only available on Windows");
        lock (_gate)
        {
            if (string.IsNullOrEmpty(_shutdownReceipt)
                || !string.Equals(_shutdownReceipt, request.Receipt, StringComparison.Ordinal))
                return PowerResult.Failure("No matching shutdown request");

            var result = ExecuteShutdown("/a");
            if (!result.Success) return result;
            _shutdownReceipt = null;
            _scheduledAt = null;
            _logger.LogInformation("Operator cancelled pending Windows shutdown");
            return new PowerResult(true, "Shutdown cancelled", null, null);
        }
    }

    private static PowerResult ExecuteShutdown(params string[] arguments)
    {
        try
        {
            var executable = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null || !process.WaitForExit(5000) || process.ExitCode != 0)
                return PowerResult.Failure("Windows rejected the shutdown command");
            return new PowerResult(true, "ok", null, null);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return PowerResult.Failure("Cannot request Windows shutdown: " + e.Message);
        }
    }
}
public sealed record PowerApproval(string Token, DateTimeOffset ExpiresAt, int DelaySeconds);
public sealed record PowerShutdownRequest(string? Token, string? Confirmation, bool AcknowledgedDataLoss);
public sealed record PowerCancelRequest(string? Receipt);
public sealed record PowerResult(bool Success, string Message, string? Receipt, int? DelaySeconds)
{
    public static PowerResult Failure(string message) => new(false, message, null, null);
}
