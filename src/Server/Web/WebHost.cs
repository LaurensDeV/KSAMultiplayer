using System.Collections.Concurrent;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KSAMultiplayer.Server;

/// <summary>A command line from the web viewer's admin, for the tick thread to run and answer.</summary>
public sealed record WebCommand(string Line, string From)
{
    public List<string> Lines { get; } = [];
    public TaskCompletionSource<string[]> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// The web viewer: a page and the few routes behind it, served from inside the server process on
/// a TCP port of its own. Off unless <c>web-port</c> is set.
///
/// <para>Nothing here touches <see cref="GameServer"/>. The tick thread builds a view a few times a
/// second and hands it over in <see cref="Tick"/>, events come through <see cref="GameServer.Viewed"/>,
/// and commands go back the other way in <see cref="Commands"/>. Everything the web's own threads
/// read is either immutable or theirs.</para>
/// </summary>
public sealed class WebHost : IAsyncDisposable
{
    /// <summary>How long a web command waits on the tick thread before it is answered that the server is busy.</summary>
    public static readonly TimeSpan CommandWithin = TimeSpan.FromSeconds(2.0);

    private const string SessionCookie = "ksamp-admin";
    private const string Guard = "X-KSAMP";

    private readonly WebApplication _app;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pumping;
    private readonly Func<double> _wall;
    private double _viewedAt = double.NegativeInfinity;

    public ViewFeed Feed { get; }
    public WebAdmin Admin { get; } = new();

    /// <summary>Lines the web's admin has asked for, for the tick thread to run as it runs the console's.</summary>
    public ConcurrentQueue<WebCommand> Commands { get; } = new();

    /// <summary>The address and port it really listens on, which a port of 0 picks.</summary>
    public IPEndPoint At { get; }

    private WebHost(WebApplication app, ViewFeed feed, IPEndPoint at, Func<double> wall)
    {
        (_app, Feed, At, _wall) = (app, feed, at, wall);
        _pumping = Task.Run(Pump);
    }

    /// <summary>The viewer, listening. Throws <see cref="IOException"/> when the port cannot be had.</summary>
    /// <param name="port">0 picks a free one, which is what a test wants; <c>web-port</c> 0 is never started at all.</param>
    public static async Task<WebHost> StartAsync(ServerOptions options, int port, Func<double> wall)
    {
        IPAddress bind = options.WebBind.Length > 0 ? IPAddress.Parse(options.WebBind) : IPAddress.Any;
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        // The server's console is the operator's log, and a web framework's chatter would drown it.
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k =>
        {
            k.Listen(bind, port);
            k.AddServerHeader = false;
        });

        // A forwarded header is the sender's to write, so it is believed only from a proxy the
        // operator named; with none named, the address a request came from is the one it has.
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            foreach (string a in options.WebTrustProxy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                o.KnownProxies.Add(IPAddress.Parse(a));
            }
        });
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy("reading", http => RateLimitPartition.GetFixedWindowLimiter(
                Caller(http), _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1.0), QueueLimit = 0 }));
        });

        WebApplication app = builder.Build();
        ViewFeed feed = new(wall);
        app.UseForwardedHeaders();
        app.UseRateLimiter();
        app.UseWebSockets();

        WebHost? host = null;
        WebHost Host() => host!;

        foreach ((string path, string type) in Page.Files)
        {
            app.MapGet(path == "index.html" ? "/" : "/" + path, (HttpResponse r) => Page.Serve(r, path, type)).RequireRateLimiting("reading");
        }

        app.MapGet("/api/v1/server", (HttpResponse r) =>
            Host().Feed.View is { } v ? Json(r, v.Server) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable)).RequireRateLimiting("reading");
        app.MapGet("/api/v1/view", (HttpResponse r) =>
            Host().Feed.View is { } v ? Json(r, v) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable)).RequireRateLimiting("reading");
        app.MapGet("/api/v1/chat", (long? after, HttpResponse r) => Json(r, Host().Feed.ChatAfter(after ?? 0))).RequireRateLimiting("reading");
        app.MapGet("/api/v1/bodies", (HttpRequest q, HttpResponse r) =>
        {
            if (Host().Feed.Bodies is not { } b) return Results.NotFound(new { say = "no game has described this world's system yet" });
            r.Headers.ETag = b.Etag;
            r.Headers.CacheControl = "no-cache";
            return q.Headers.IfNoneMatch == b.Etag ? Results.StatusCode(StatusCodes.Status304NotModified) : Results.Bytes(b.Json, "application/json");
        }).RequireRateLimiting("reading");

        app.Map("/api/v1/live", async (HttpContext http) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            if (Host().Feed.Sockets >= ViewFeed.MostSockets)
            {
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            using System.Net.WebSockets.WebSocket ws = await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await Host().Feed.Serve(ws, Host()._stop.Token).ConfigureAwait(false);
        }).RequireRateLimiting("reading");

        app.MapPost("/api/v1/admin/login", async (HttpContext http) =>
        {
            WebHost h = Host();
            if (!h.Admin.On) return Results.Json(new { ok = false, say = "web admin is off on this server" }, statusCode: StatusCodes.Status403Forbidden);
            string from = Caller(http);
            Login? asked = await Read<Login>(http).ConfigureAwait(false);
            if (h.Admin.Login(asked?.Password ?? "", from) is not { } token)
            {
                return h.Admin.Spent(from)
                    ? Results.Json(new { ok = false, say = "too many tries; wait a minute" }, statusCode: StatusCodes.Status429TooManyRequests)
                    : Results.Json(new { ok = false, say = "wrong password" }, statusCode: StatusCodes.Status401Unauthorized);
            }
            http.Response.Cookies.Append(SessionCookie, token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = http.Request.IsHttps,
                Path = "/api/v1/admin",
                MaxAge = WebAdmin.Idle,
            });
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/v1/admin/command", async (HttpContext http) =>
        {
            WebHost h = Host();
            if (!Guarded(http)) return Results.Json(new { ok = false, say = $"a command needs the {Guard} header" }, statusCode: StatusCodes.Status400BadRequest);
            if (!h.Admin.Valid(http.Request.Cookies[SessionCookie])) return Results.Json(new { ok = false, say = "not logged in" }, statusCode: StatusCodes.Status401Unauthorized);
            Line? asked = await Read<Line>(http).ConfigureAwait(false);
            string line = (asked?.Text ?? "").Trim();
            if (line.Length == 0) return Results.BadRequest(new { ok = false, say = "no command" });
            WebCommand c = new(Wire.Clip(line, Wire.ChatChars), Caller(http));
            h.Commands.Enqueue(c);
            Task done = await Task.WhenAny(c.Done.Task, Task.Delay(CommandWithin)).ConfigureAwait(false);
            return done == c.Done.Task
                ? Results.Ok(new { ok = true, lines = c.Done.Task.Result })
                : Results.Ok(new { ok = true, lines = new[] { "asked; the server has not answered yet, and what it says goes to the log" } });
        });

        app.MapPost("/api/v1/admin/logout", (HttpContext http) =>
        {
            if (!Guarded(http)) return Results.BadRequest(new { ok = false });
            Host().Admin.Logout(http.Request.Cookies[SessionCookie]);
            http.Response.Cookies.Delete(SessionCookie, new CookieOptions { Path = "/api/v1/admin" });
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/v1/admin/session", (HttpContext http) =>
            Results.Ok(new { on = Host().Admin.On, @in = Host().Admin.Valid(http.Request.Cookies[SessionCookie]) }));

        try
        {
            await app.StartAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not IOException and (System.Net.Sockets.SocketException or InvalidOperationException or AggregateException))
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw new IOException(e.GetBaseException().Message, e);
        }
        catch (IOException)
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        string url = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Uri uri = new(url.Replace("://+", "://localhost").Replace("://*", "://localhost"));
        host = new WebHost(app, feed, new IPEndPoint(bind, uri.Port), wall);
        host.Admin.SetPassword(options.WebAdminPassword);
        return host;
    }

    /// <summary>
    /// Why a line may not be run from the web, or null when it may. Asked before the line is logged,
    /// so a <c>set web-admin-password ...</c> sent anyway never reaches the log with its value.
    /// </summary>
    public static string? NotForTheWeb(string line)
    {
        string verb = line.TrimStart().Split(' ', 2)[0].ToLowerInvariant();
        return verb switch
        {
            "set" or "settings" or "fresh" or "quit" or "exit" or "stop" or "playerlogs" => verb,
            // A token is said once, and to a game: a page is no place to hand one out.
            "op" or "trust" or "deop" => verb,
            _ => null,
        };
    }

    private sealed record Login(string? Password);

    private sealed record Line(string? Text);

    // The header a page sets on a request it sends with fetch, and a form on another site cannot:
    // with the cookie held to this site as well, a command can only come from this page.
    private static bool Guarded(HttpContext http) => http.Request.Headers[Guard] == "1";

    private static async Task<T?> Read<T>(HttpContext http) where T : class
    {
        try
        {
            return await http.Request.ReadFromJsonAsync<T>(ViewFeed.Json).ConfigureAwait(false);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or BadHttpRequestException)
        {
            return null;
        }
    }

    private static IResult Json(HttpResponse r, object value)
    {
        r.Headers.CacheControl = "no-store";
        return Results.Json(value, ViewFeed.Json);
    }

    /// <summary>Where a request came from, after a trusted proxy has said; a v4 address on a dual-stack socket as v4.</summary>
    public static string Caller(HttpContext http)
    {
        IPAddress? ip = http.Connection.RemoteIpAddress;
        if (ip is null) return "nowhere";
        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }

    /// <summary>From the tick thread, every tick: a fresh view when one is due. Cheap when it is not.</summary>
    public void Tick(GameServer server, double now)
    {
        double wall = _wall();
        if (wall - _viewedAt < ViewFeed.EverySeconds) return;
        _viewedAt = wall;
        (byte[] Json, string Etag)? bodies = server.Bodies;
        if (bodies is null) server.AskForBodiesNow();
        Feed.Bodies = bodies;
        Feed.Publish(server.BuildView(now, wall, bodies?.Etag));
    }

    /// <summary>The settings a running viewer takes up: only the admin password. A new one ends every session.</summary>
    public void Reconfigure(ServerOptions options) => Admin.SetPassword(options.WebAdminPassword);

    private async Task Pump()
    {
        using PeriodicTimer every = new(TimeSpan.FromSeconds(ViewFeed.EverySeconds));
        try
        {
            while (await every.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                try { Feed.Pump(); }
                catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"web viewer: {e.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _pumping.ConfigureAwait(false);
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
