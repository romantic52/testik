using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;

namespace Aegis.Agent.Services;

/// <summary>
/// Authenticode signature and publisher of an executable, cached by path+write time
/// so control-mode scans stay cheap. Also flags executables living in user-writable
/// locations (Temp, Downloads, AppData), a classic place for freshly dropped malware.
/// </summary>
public static class SignatureInspector
{
    private static readonly ConcurrentDictionary<string, FileTrust> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static FileTrust Inspect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new FileTrust(false, false, null, false, "no-path");
        try
        {
            if (!File.Exists(path))
                return new FileTrust(false, false, null, false, "missing");
        }
        catch { }
        return InspectExisting(path);
    }

    private static FileTrust InspectExisting(string path)
    {
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(path); } catch { stamp = DateTime.MinValue; }
        var cacheKey = path + "|" + stamp.Ticks;
        if (Cache.TryGetValue(cacheKey, out var cached)) return cached;

        bool signed = false;
        string? publisher = null;
        try
        {
            using var cert = X509Certificate.CreateFromSignedFile(path);
            signed = true;
            using var cert2 = new X509Certificate2(cert);
            publisher = cert2.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch { signed = false; }

        var userWritable = IsUserWritable(path);
        var trust = new FileTrust(true, signed, publisher, userWritable,
            signed ? "signed" : userWritable ? "unsigned-user-writable" : "unsigned");
        if (Cache.Count > 8192) Cache.Clear();
        Cache[cacheKey] = trust;
        return trust;
    }

    private static bool IsUserWritable(string path)
    {
        var p = path.ToLowerInvariant();
        string[] markers =
        [
            @"\appdata\local\temp\", @"\appdata\local\", @"\appdata\roaming\",
            @"\downloads\", @"\temp\", @"\users\public\", @"\windows\temp\"
        ];
        return markers.Any(m => p.Contains(m));
    }
}

public sealed record FileTrust(bool Exists, bool Signed, string? Publisher, bool UserWritable, string Status);
