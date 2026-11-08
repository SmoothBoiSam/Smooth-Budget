using Microsoft.AspNetCore.StaticFiles;
using Npgsql;

namespace SmoothBudget.Api.Hosting;

/// <summary>What the app needs to run on a host like Render (DECISIONS.md 123-128).</summary>
public static class HostingSetup
{
    /// <summary>
    /// Accepts the database connection either as Npgsql settings ("Host=…;Username=…") or as the URL hosts hand out
    /// ("postgresql://user:pass@host:5432/db"), which Npgsql doesn't read directly.
    /// </summary>
    public static string ConnectionString(string value)
    {
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return value;
        var uri = new Uri(value);
        var userInfo = uri.UserInfo.Split(':', 2);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
            // A host's internal network may not use TLS; its external address does. Prefer it, don't demand it.
            SslMode = SslMode.Prefer,
        };
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase) && kv.Length == 2 && Enum.TryParse<SslMode>(kv[1], true, out var mode))
                b.SslMode = mode;
        }
        return b.ConnectionString;
    }

    /// <summary>
    /// Serves the built website (copied into wwwroot by the Dockerfile) from the same origin as the API, so the auth
    /// cookie just works. The page, manifest and service worker are always re-checked; built files are cached for a year.
    /// </summary>
    public static void UseWebsite(this WebApplication app)
    {
        if (!Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot"))) return;
        app.UseDefaultFiles();
        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".webmanifest"] = "application/manifest+json";
        app.UseStaticFiles(new StaticFileOptions
        {
            ContentTypeProvider = types,
            OnPrepareResponse = ctx =>
            {
                var path = ctx.Context.Request.Path.Value ?? "";
                ctx.Context.Response.Headers.CacheControl = path.StartsWith("/assets/", StringComparison.Ordinal)
                    ? "public, max-age=31536000, immutable"
                    : "no-cache";
            },
        });
        app.MapFallback("/api/{**rest}", () => Results.NotFound());
        app.MapFallbackToFile("index.html", new StaticFileOptions
        {
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
        });
    }

    /// <summary>Basic browser protections on every response.</summary>
    public static void UseSecurityHeaders(this WebApplication app) => app.Use(async (ctx, next) =>
    {
        var h = ctx.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        h.XFrameOptions = "DENY";
        h["Referrer-Policy"] = "same-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        await next();
    });
}
