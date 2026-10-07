using Microsoft.Extensions.FileProviders;

namespace Aegis.Agent.Infrastructure;

public static class AegisWebExtensions
{
    public static WebApplication UseAegisSecurity(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var host = context.Request.Host.Host;
            if (!host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { detail = "Invalid Host header" });
                return;
            }

            var protectedSurface =
                context.Request.Path.StartsWithSegments("/api")
                || context.Request.Path.StartsWithSegments("/ws");

            if (protectedSurface)
            {
                var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
                if (fetchSite.Equals("cross-site", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { detail = "Cross-site requests are not allowed" });
                    return;
                }

                var origin = context.Request.Headers.Origin.ToString();
                var expectedPort = context.Request.Host.Port
                    ?? (context.Request.IsHttps ? 443 : 80);
                if (!string.IsNullOrWhiteSpace(origin)
                    && !IsAllowedBrowserOrigin(origin, expectedPort))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { detail = "Browser origin is not allowed" });
                    return;
                }
            }

            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; " +
                "script-src 'self' 'wasm-unsafe-eval'; " +
                "style-src 'self'; " +
                "font-src 'self'; " +
                "img-src 'self' data:; " +
                "connect-src 'self' https: wss: http://127.0.0.1:* http://localhost:* ws://127.0.0.1:* ws://localhost:*; " +
                "object-src 'none'; base-uri 'self'; frame-ancestors 'none'";

            if (context.Request.Path.StartsWithSegments("/api"))
                context.Response.Headers.CacheControl = "no-store";

            await next();
        });

        return app;
    }

    public static bool IsAllowedBrowserOrigin(string origin, int expectedPort)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        var loopbackHost =
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);

        return loopbackHost && uri.Port == expectedPort;
    }

    public static WebApplication UseAegisFrontend(this WebApplication app)
    {
        var publishedWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var sourceWebRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", ".."));
        var webRoot = File.Exists(Path.Combine(publishedWebRoot, "index.html"))
            ? publishedWebRoot
            : sourceWebRoot;

        if (!File.Exists(Path.Combine(webRoot, "index.html")))
            return app;

        var provider = new PhysicalFileProvider(webRoot);
        var defaultFiles = new DefaultFilesOptions { FileProvider = provider };
        defaultFiles.DefaultFileNames.Clear();
        defaultFiles.DefaultFileNames.Add("index.html");

        app.UseDefaultFiles(defaultFiles);
        app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
        return app;
    }
}
