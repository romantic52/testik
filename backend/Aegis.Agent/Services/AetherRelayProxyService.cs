using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aegis.Agent.Services;

public sealed class AetherRelayProxyService
{
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    private static readonly Regex UserPath =
        new(@"^/users/[A-Za-z0-9_]{2,64}/(?:devices|profile)$", RegexOptions.Compiled);

    private static readonly Regex ClaimPath =
        new(@"^/keys/claim/[A-Za-z0-9_]{2,64}\?device_id=[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    private static readonly Regex CountPath =
        new(@"^/keys/count\?device_id=[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    private static readonly Regex InboxPath =
        new(@"^/messages/inbox/[A-Za-z0-9_]{2,64}\?device_id=[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    public async Task<AetherProxyResult> ForwardAsync(AetherRelayRequest request, CancellationToken cancellationToken)
    {
        var server = ValidateServer(request.ServerUrl);
        var method = ParseMethod(request.Method);
        var path = NormalizeAndValidatePath(method, request.Path);
        var target = new Uri(server, path);

        using var message = new HttpRequestMessage(method, target);
        message.Headers.TryAddWithoutValidation("Bypass-Tunnel-Reminder", "true");
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(request.Token))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Token.Trim());

        if (request.Body is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null })
        {
            var body = request.Body.Value.GetRawText();
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await Client.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/json";

        return new AetherProxyResult((int)response.StatusCode, content, contentType);
    }

    private static Uri ValidateServer(string? raw)
    {
        if (!Uri.TryCreate(raw?.Trim(), UriKind.Absolute, out var uri))
            throw new ArgumentException("Invalid AETHER server URL");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("AETHER server URL must not contain credentials");

        var isHttps = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isHttp = uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        if (!isHttps && !isHttp)
            throw new ArgumentException("Only HTTP(S) AETHER servers are supported");

        if (isHttp && !IsLoopbackHost(uri.Host))
            throw new ArgumentException("Remote AETHER servers must use HTTPS");

        if (uri.AbsolutePath is not "/" && !string.IsNullOrWhiteSpace(uri.AbsolutePath))
            throw new ArgumentException("Use the AETHER server origin without an extra path");

        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    private static HttpMethod ParseMethod(string? method) =>
        (method ?? "").Trim().ToUpperInvariant() switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "DELETE" => HttpMethod.Delete,
            _ => throw new ArgumentException("Unsupported relay method")
        };

    private static string NormalizeAndValidatePath(HttpMethod method, string? rawPath)
    {
        var path = (rawPath ?? "").Trim();
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("Invalid relay path");

        if (Uri.TryCreate(path, UriKind.Absolute, out _))
            throw new ArgumentException("Absolute relay URLs are not allowed");

        var allowed =
            (method == HttpMethod.Post && path == "/users/login")
            || (method == HttpMethod.Post && path == "/logout")
            || (method == HttpMethod.Post && path == "/users/me/heartbeat")
            || (method == HttpMethod.Put && path == "/keys/upload")
            || (method == HttpMethod.Put && path == "/sessions/me/device")
            || (method == HttpMethod.Post && path == "/messages")
            || (method == HttpMethod.Post && path == "/messages/ack")
            || (method == HttpMethod.Get && UserPath.IsMatch(path))
            || (method == HttpMethod.Post && ClaimPath.IsMatch(path))
            || (method == HttpMethod.Get && CountPath.IsMatch(path))
            || (method == HttpMethod.Get && InboxPath.IsMatch(path));

        if (!allowed)
            throw new ArgumentException("Relay path is not allowed by the local bridge");

        return path;
    }
}

public sealed record AetherRelayRequest(
    string ServerUrl,
    string Method,
    string Path,
    string? Token,
    JsonElement? Body);

public sealed record AetherProxyResult(int StatusCode, string Content, string ContentType);
