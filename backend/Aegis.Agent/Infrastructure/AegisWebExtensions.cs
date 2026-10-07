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

            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; " +
                "script-src 'self'; " +
                "style-src 'self' https://fonts.googleapis.com; " +
                "font-src 'self' https://fonts.gstatic.com; " +
                "img-src 'self' data:; " +
                "connect-src 'self' http: https: ws: wss:; " +
                "object-src 'none'; base-uri 'self'; frame-ancestors 'none'";

            if (context.Request.Path.StartsWithSegments("/api"))
                context.Response.Headers.CacheControl = "no-store";

            await next();
        });

        return app;
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
