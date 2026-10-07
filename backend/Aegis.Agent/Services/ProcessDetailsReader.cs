using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Aegis.Agent.Services;

public sealed record ProcessDetailsSnapshot(
    int Pid,
    string Name,
    string? Path,
    DateTimeOffset? StartedAt,
    double MemoryMb,
    double PrivateMemoryMb,
    int? Threads,
    int? Handles,
    string? Priority,
    bool? Responding,
    long? FileSizeBytes,
    string? FileVersion,
    string? ProductName,
    string? CompanyName,
    string? Sha256,
    bool? EmbeddedSignaturePresent,
    string? SignerSubject,
    string? SignerThumbprint,
    DateTimeOffset? SignerNotBefore,
    DateTimeOffset? SignerNotAfter);

public static class ProcessDetailsReader
{
    private const long MaxHashableFileBytes = 512L * 1024L * 1024L;

    public static ProcessDetailsSnapshot? Read(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var path = Try(() => process.MainModule?.FileName);
            var metadata = ReadFileMetadata(path);

            return new ProcessDetailsSnapshot(
                process.Id,
                process.ProcessName,
                path,
                Try(() => new DateTimeOffset(process.StartTime)),
                Math.Round(process.WorkingSet64 / 1024d / 1024d, 1),
                Math.Round(process.PrivateMemorySize64 / 1024d / 1024d, 1),
                Try(() => (int?)process.Threads.Count),
                Try(() => (int?)process.HandleCount),
                Try(() => process.PriorityClass.ToString()),
                Try(() => (bool?)process.Responding),
                metadata.SizeBytes,
                metadata.FileVersion,
                metadata.ProductName,
                metadata.CompanyName,
                metadata.Sha256,
                metadata.EmbeddedSignaturePresent,
                metadata.SignerSubject,
                metadata.SignerThumbprint,
                metadata.SignerNotBefore,
                metadata.SignerNotAfter);
        }
        catch
        {
            return null;
        }
    }

    private static ProcessFileMetadata ReadFileMetadata(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ProcessFileMetadata.Empty;

        try
        {
            var info = new FileInfo(path);
            var version = FileVersionInfo.GetVersionInfo(path);
            string? hash = null;

            if (info.Exists && info.Length <= MaxHashableFileBytes)
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                hash = Convert.ToHexString(SHA256.HashData(stream));
            }

            var signature = ReadEmbeddedSigner(path);

            return new ProcessFileMetadata(
                info.Exists ? info.Length : null,
                EmptyToNull(version.FileVersion),
                EmptyToNull(version.ProductName),
                EmptyToNull(version.CompanyName),
                hash,
                signature.Present,
                signature.Subject,
                signature.Thumbprint,
                signature.NotBefore,
                signature.NotAfter);
        }
        catch
        {
            return ProcessFileMetadata.Empty;
        }
    }

    private static EmbeddedSigner ReadEmbeddedSigner(string path)
    {
        try
        {
            using var certificate = new X509Certificate2(
                X509Certificate.CreateFromSignedFile(path));

            return new EmbeddedSigner(
                true,
                EmptyToNull(certificate.Subject),
                EmptyToNull(certificate.Thumbprint),
                new DateTimeOffset(certificate.NotBefore),
                new DateTimeOffset(certificate.NotAfter));
        }
        catch (CryptographicException)
        {
            return EmbeddedSigner.None;
        }
        catch
        {
            return new EmbeddedSigner(null, null, null, null, null);
        }
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static T? Try<T>(Func<T> read)
    {
        try { return read(); }
        catch { return default; }
    }

    private sealed record ProcessFileMetadata(
        long? SizeBytes,
        string? FileVersion,
        string? ProductName,
        string? CompanyName,
        string? Sha256,
        bool? EmbeddedSignaturePresent,
        string? SignerSubject,
        string? SignerThumbprint,
        DateTimeOffset? SignerNotBefore,
        DateTimeOffset? SignerNotAfter)
    {
        public static ProcessFileMetadata Empty { get; } =
            new(
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
    }

    private sealed record EmbeddedSigner(
        bool? Present,
        string? Subject,
        string? Thumbprint,
        DateTimeOffset? NotBefore,
        DateTimeOffset? NotAfter)
    {
        public static EmbeddedSigner None { get; } =
            new(false, null, null, null, null);
    }
}
