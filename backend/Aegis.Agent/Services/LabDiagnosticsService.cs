using System.Diagnostics;
using System.Security.Cryptography;

namespace Aegis.Agent.Services;

/// <summary>
/// Bounded local disk read/write test. Never accepts a user-supplied path,
/// never touches existing user files, and uses a delete-on-close temp file.
/// </summary>
public sealed class LabDiagnosticsService
{
    private readonly SemaphoreSlim _exclusive = new(1, 1);
    private readonly ILogger<LabDiagnosticsService> _logger;
    private DateTimeOffset _lastStart = DateTimeOffset.MinValue;

    public LabDiagnosticsService(ILogger<LabDiagnosticsService> logger) => _logger = logger;

    public static bool ValidSize(int sizeMiB) => sizeMiB is >= 8 and <= 32;

    public async Task<DiskLabResult> CheckDiskAsync(int sizeMiB, CancellationToken token)
    {
        if (!ValidSize(sizeMiB))
            throw new ArgumentOutOfRangeException(nameof(sizeMiB), "Use 8 to 32 MiB.");
        if (!await _exclusive.WaitAsync(0, token))
            throw new InvalidOperationException("Another disk check is running.");

        try
        {
            if (DateTimeOffset.UtcNow - _lastStart < TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("Wait at least 5 seconds between disk checks.");
            _lastStart = DateTimeOffset.UtcNow;

            const int blockSize = 1024 * 1024;
            var block = RandomNumberGenerator.GetBytes(blockSize);
            var result = new byte[blockSize];
            var tempRoot = Path.Combine(Path.GetTempPath(), "AEGIS-lab");
            Directory.CreateDirectory(tempRoot);
            var name = Path.Combine(tempRoot, "bench-" + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                await using var file = new FileStream(
                    name, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 64 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
                var writeTime = Stopwatch.StartNew();
                for (var i = 0; i < sizeMiB; i++)
                {
                    token.ThrowIfCancellationRequested();
                    await file.WriteAsync(block, token);
                }
                await file.FlushAsync(token);
                // Persist buffers to the OS as far as its storage stack permits.
                file.Flush(flushToDisk: true);
                writeTime.Stop();

                file.Position = 0;
                var readTime = Stopwatch.StartNew();
                for (var i = 0; i < sizeMiB; i++)
                {
                    token.ThrowIfCancellationRequested();
                    await file.ReadExactlyAsync(result, token);
                    if (!result.AsSpan().SequenceEqual(block))
                        throw new IOException("The test file did not match the expected content.");
                }
                readTime.Stop();
                return new DiskLabResult(
                    sizeMiB,
                    Math.Round(sizeMiB / Math.Max(writeTime.Elapsed.TotalSeconds, 0.001), 2),
                    Math.Round(sizeMiB / Math.Max(readTime.Elapsed.TotalSeconds, 0.001), 2),
                    Math.Round(writeTime.Elapsed.TotalMilliseconds, 1),
                    Math.Round(readTime.Elapsed.TotalMilliseconds, 1),
                    true);
            }
            finally
            {
                try { File.Delete(name); }
                catch (Exception error) { _logger.LogWarning(error, "Could not delete lab temp file"); }
            }
        }
        finally { _exclusive.Release(); }
    }
}

public sealed record DiskLabRequest(int SizeMiB);

public sealed record DiskLabResult(
    int SizeMiB, double WriteMiBps, double ReadMiBps,
    double WriteMilliseconds, double ReadMilliseconds, bool Verified);
