using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aegis.Agent.Infrastructure;

namespace Aegis.Agent.Services;

/// <summary>
/// IP cameras reachable on this PC or the local network: JPEG snapshot URLs or
/// MJPEG streams (first frame is taken). The agent fetches frames so the UI keeps
/// its strict CSP; only loopback/private addresses are allowed (no Internet SSRF).
/// RTSP needs a transcoder and is not supported directly — most cameras also
/// expose an HTTP snapshot URL.
/// </summary>
public sealed class CameraService
{
    private const string SettingKey = "cameras";
    private const int MaxFrameBytes = 8 * 1024 * 1024;
    private readonly StateDatabase _database;
    private readonly IncidentStoreService _incidents;
    private readonly HttpClient _http;
    private readonly Dictionary<string, DateTimeOffset> _lastMotion = new();
    private readonly Dictionary<string, CameraHealth> _health = new();

    public CameraService(StateDatabase database, IncidentStoreService incidents)
    {
        _database = database;
        _incidents = incidents;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            UseProxy = false
        }) { Timeout = TimeSpan.FromSeconds(8) };
    }

    public async Task<IReadOnlyList<CameraConfig>> ListAsync(CancellationToken token)
    {
        await using var connection = _database.OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM settings WHERE key=$k;";
        cmd.Parameters.AddWithValue("$k", SettingKey);
        return await cmd.ExecuteScalarAsync(token) is string json
            ? JsonSerializer.Deserialize<List<CameraConfig>>(json, TelemetryJson.Options) ?? []
            : [];
    }

    public async Task<IReadOnlyList<CameraView>> ViewAsync(CancellationToken token) =>
        (await ListAsync(token)).Select(c =>
        {
            _health.TryGetValue(c.Id, out var h);
            return new CameraView(c.Id, c.Name, Mask(c.Url), c.Motion, c.Sensitivity, h?.Status ?? "unknown", h?.Error, h?.LastFrameAt, h?.Width);
        }).ToList();

    public async Task<IReadOnlyList<CameraView>> SaveAsync(IReadOnlyList<CameraInput> input, CancellationToken token)
    {
        if (input.Count > 32) throw new ArgumentException("Не больше 32 камер.");
        var existing = (await ListAsync(token)).ToDictionary(c => c.Id);
        var saved = new List<CameraConfig>();
        foreach (var item in input)
        {
            var id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N")[..12] : item.Id.Trim();
            var url = item.Url?.Trim() ?? "";
            // A masked URL from the UI keeps the stored credentials.
            if (url.Contains("***", StringComparison.Ordinal) && existing.TryGetValue(id, out var old)) url = old.Url;
            var uri = ValidateUrl(url);
            var name = string.IsNullOrWhiteSpace(item.Name) ? uri.Host : item.Name.Trim();
            saved.Add(new CameraConfig(id, name.Length > 80 ? name[..80] : name, uri.ToString(), item.Motion, Math.Clamp(item.Sensitivity, 1, 100)));
        }
        await using var connection = _database.OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key,value_json,updated_at) VALUES($k,$v,$t) ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json, updated_at=excluded.updated_at;";
        cmd.Parameters.AddWithValue("$k", SettingKey);
        cmd.Parameters.AddWithValue("$v", JsonSerializer.Serialize(saved, TelemetryJson.Options));
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(token);
        await _incidents.AppendAuditAsync(new AuditRecord(DateTimeOffset.UtcNow, "cameras.updated", "cameras", saved.Count + " камер в конфигурации", "AEGIS UI"), token);
        return await ViewAsync(token);
    }

    public static Uri ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Адрес камеры должен начинаться с http:// или https://. Для RTSP укажи HTTP-адрес снимка (snapshot/JPEG/MJPEG) из инструкции камеры.");
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && !IsLocal(ip))
            throw new ArgumentException("Разрешены только камеры этого компьютера и локальной сети (10.x, 172.16–31.x, 192.168.x).");
        return uri;
    }

    private static bool IsLocal(IPAddress ip)
    {
        var scope = ConnectionClassifier.Scope(ip.ToString());
        return scope is ConnectionClassifier.Loopback or ConnectionClassifier.Lan;
    }

    private static string Mask(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0
            ? url.Replace(uri.UserInfo + "@", uri.UserInfo.Split(':')[0] + ":***@", StringComparison.Ordinal)
            : url;

    public async Task<(byte[] Data, string ContentType)> FrameAsync(string id, CancellationToken token)
    {
        var camera = (await ListAsync(token)).FirstOrDefault(c => c.Id == id) ?? throw new KeyNotFoundException();
        var health = _health.TryGetValue(id, out var h) ? h : _health[id] = new CameraHealth();
        try
        {
            var uri = ValidateUrl(camera.Url);
            // Resolve host names and refuse anything that leaves the local network.
            if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out _))
            {
                var addresses = await Dns.GetHostAddressesAsync(uri.Host, token);
                if (addresses.Length == 0 || addresses.Any(a => !IsLocal(a)))
                    throw new ArgumentException("Имя камеры указывает не на локальную сеть.");
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, new UriBuilder(uri) { UserName = "", Password = "" }.Uri);
            if (uri.UserInfo.Length > 0)
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(uri.UserInfo))));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("Камера ответила HTTP " + (int)response.StatusCode);
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var frame = type.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)
                ? await FirstJpegAsync(stream, token)
                : await ReadLimitedAsync(stream, token);
            var contentType = IsJpeg(frame) ? "image/jpeg" : IsPng(frame) ? "image/png" : throw new InvalidDataException("Камера вернула не изображение (" + (type.Length > 0 ? type : "неизвестный тип") + ").");
            health.Status = "online"; health.Error = null; health.LastFrameAt = DateTimeOffset.UtcNow;
            return (frame, contentType);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidDataException or ArgumentException or IOException or System.Net.Sockets.SocketException)
        {
            health.Status = "offline";
            health.Error = error is TaskCanceledException ? "Камера не ответила за 8 секунд" : error.Message;
            throw;
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxFrameBytes) throw new InvalidDataException("Кадр больше 8 МиБ.");
        }
        return buffer.ToArray();
    }

    /// <summary>Extracts the first complete JPEG (FFD8 … FFD9) from an MJPEG stream.</summary>
    public static async Task<byte[]> FirstJpegAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        int start = -1, scan = 0, read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            buffer.Write(chunk, 0, read);
            var data = buffer.GetBuffer();
            var length = (int)buffer.Length;
            for (var i = scan; i < length - 1; i++)
            {
                if (data[i] != 0xFF) continue;
                if (start < 0 && data[i + 1] == 0xD8) start = i;
                else if (start >= 0 && i > start + 1 && data[i + 1] == 0xD9) return data[start..(i + 2)];
            }
            scan = Math.Max(0, length - 1);
            if (length > MaxFrameBytes) break;
        }
        throw new InvalidDataException("В потоке MJPEG не найден полный кадр JPEG.");
    }

    private static bool IsJpeg(byte[] d) => d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8;
    private static bool IsPng(byte[] d) => d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47;

    /// <summary>The UI detected motion (frame difference above the camera's threshold).</summary>
    public async Task<bool> ReportMotionAsync(string id, double changedPercent, CancellationToken token)
    {
        var name = id == "local" ? "Камера этого компьютера" : (await ListAsync(token)).FirstOrDefault(c => c.Id == id)?.Name;
        if (name is null) return false;
        var now = DateTimeOffset.UtcNow;
        lock (_lastMotion)
        {
            if (_lastMotion.TryGetValue(id, out var last) && now - last < TimeSpan.FromMinutes(5)) return false;
            _lastMotion[id] = now;
        }
        await _incidents.UpsertAsync(new IncidentRecord(
            $"CAM-MOTION-{id}-{now:yyyyMMddHHmmss}", "Движение в кадре: " + name, "Видеонаблюдение / " + name, "Средний", "Авто",
            $"Изменилось {changedPercent:0.#}% кадра. Повторные срабатывания этой камеры подавляются 5 минут.",
            [$"{now.LocalDateTime:HH:mm:ss} — детектор движения AEGIS"], true, false, "camera:motion:" + id, now), token);
        return true;
    }

    private sealed class CameraHealth
    {
        public string Status = "unknown";
        public string? Error;
        public DateTimeOffset? LastFrameAt;
        public int? Width;
    }
}

public sealed record CameraConfig(string Id, string Name, string Url, bool Motion, int Sensitivity);
public sealed record CameraInput(string? Id, string? Name, string? Url, bool Motion, int Sensitivity);
public sealed record CameraView(string Id, string Name, string Url, bool Motion, int Sensitivity, string Status, string? Error, DateTimeOffset? LastFrameAt, int? Width);
