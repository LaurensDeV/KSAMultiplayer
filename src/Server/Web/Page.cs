using Microsoft.AspNetCore.Http;

namespace KSAMultiplayer.Server;

/// <summary>
/// The viewer's page, compiled into the server so it is still one program to run: no file beside
/// it to lose and nothing fetched from anywhere else, which is also what lets the policy below say
/// "only from here".
/// </summary>
internal static class Page
{
    public static readonly (string Path, string Type)[] Files =
    [
        ("index.html", "text/html; charset=utf-8"),
        ("viewer.css", "text/css; charset=utf-8"),
        ("viewer.js", "text/javascript; charset=utf-8"),
        ("map2d.js", "text/javascript; charset=utf-8"),
        ("map3d.js", "text/javascript; charset=utf-8"),
        // three.js r170, MIT, vendored from the npm package with its licence header kept: nothing is fetched from elsewhere.
        ("three.module.min.js", "text/javascript; charset=utf-8"),
    ];

    private static readonly Dictionary<string, byte[]> Read = Files.ToDictionary(f => f.Path, f => Load(f.Path));

    private static byte[] Load(string path)
    {
        using Stream? from = typeof(Page).Assembly.GetManifestResourceStream("web/" + path);
        if (from is null) return "<!doctype html><p>This build of the server carries no viewer page."u8.ToArray();
        using MemoryStream into = new();
        from.CopyTo(into);
        return into.ToArray();
    }

    public static IResult Serve(HttpResponse r, string path, string type)
    {
        // Script and style from this page only, the socket back to this server, and framed by nobody.
        r.Headers.ContentSecurityPolicy = "default-src 'self'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'";
        r.Headers.XContentTypeOptions = "nosniff";
        r.Headers["Referrer-Policy"] = "no-referrer";
        r.Headers.CacheControl = "no-cache";
        return Results.Bytes(Read[path], type);
    }
}
