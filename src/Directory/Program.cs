using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace KSAMultiplayer.Directories;

/// <summary>
/// A directory: addresses of servers worth asking about, and nothing anybody told it that it has not
/// seen for itself.
///
/// <para>Two endpoints and no database. <c>GET /v1/servers</c> is a document any static host could
/// serve, which is the point -- a clan's three servers can be a file and a player cannot tell the
/// difference. <c>POST /v1/announce</c> is the half only a service can do, and all it earns an
/// announcement is a probe.</para>
/// </summary>
public static class Program
{
    /// <summary>How long a reader may hold on to the list, s. Short enough that a server appearing is noticed, long enough that a hundred games starting at once is one answer.</summary>
    private const int HoldForSeconds = 20;

    public static async Task<int> Main(string[] args)
    {
        // The image carries no curl and no wget, so the health check is this program asking itself.
        if (args is ["--healthcheck", ..]) return await Healthy(args).ConfigureAwait(false);

        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        string name = Environment.GetEnvironmentVariable("KSAMP_DIRECTORY_NAME") is { Length: > 0 } said
            ? Wire.Clip(said, 48) : "a KSA Multiplayer directory";

        builder.Services.AddSingleton<Ticker>();
        builder.Services.AddSingleton<Roster>();
        builder.Services.AddHostedService<Watch>();

        // Behind a reverse proxy the address every announcement appears to come from is the proxy's,
        // and the address is the whole of what this trusts. So the forwarded header is read -- but
        // only from a hop on a private network, because a header is the sender's to choose and this
        // would otherwise take anybody's word for where they are. One hop, since there is one.
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            o.ForwardLimit = 1;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            string trust = Environment.GetEnvironmentVariable("KSAMP_TRUST_PROXY")
                           ?? "127.0.0.0/8,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16";
            foreach (string cidr in trust.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (System.Net.IPNetwork.TryParse(cidr, out System.Net.IPNetwork network)) o.KnownIPNetworks.Add(network);
            }
        });

        // Per address, because the thing worth limiting is one party's share of this. Generous for
        // reading, because a game asks on a screen a player may open repeatedly, and tighter for
        // announcing, where a server needs one a minute.
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy("reading", http => RateLimitPartition.GetFixedWindowLimiter(
                Caller(http), _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1.0), QueueLimit = 0 }));
            o.AddPolicy("announcing", http => RateLimitPartition.GetFixedWindowLimiter(
                Caller(http), _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1.0), QueueLimit = 0 }));
        });

        WebApplication app = builder.Build();
        app.UseForwardedHeaders();
        app.UseRateLimiter();

        app.MapGet("/health", (Roster roster, Ticker clock) =>
        {
            (int known, int listed) = roster.Count(clock.Now);
            return Results.Ok(new { ok = true, known, listed, protocol = Wire.Version, document = Listings.Version });
        });

        app.MapGet("/v1/servers", (Roster roster, Ticker clock, HttpResponse r) =>
        {
            r.Headers.CacheControl = $"public, max-age={HoldForSeconds}";
            return Results.Ok(roster.Snapshot(name, clock.Now, clock.Wall));
        }).RequireRateLimiting("reading");

        app.MapPost("/v1/announce", ([FromBody] Announcement said, Roster roster, Ticker clock, HttpContext http) =>
        {
            if (said.V != Listings.Version)
            {
                return Results.BadRequest(new Noted(false, Say: $"this directory speaks announcements of version {Listings.Version}, not {said.V}"));
            }
            if (Address(http) is not { } from) return Results.BadRequest(new Noted(false, Say: "could not tell where this came from"));
            Noted noted = roster.Announce(said.Host ?? "", said.Port, from, clock.Now);
            // An announcement this directory will not list is still a well-formed announcement: the
            // refusal is in the body, where the server logs it, rather than in a status code that
            // would read as the directory being broken.
            return Results.Ok(noted);
        }).RequireRateLimiting("announcing");

        app.MapDelete("/v1/announce", ([FromBody] Announcement said, Roster roster, HttpContext http) =>
        {
            if (Address(http) is not { } from) return Results.BadRequest(new Noted(false, Say: "could not tell where this came from"));
            bool gone = roster.Withdraw(said.Host ?? "", said.Port, from);
            return Results.Ok(new Noted(true, from.ToString(), false, gone ? "taken out of the list" : "was not in the list"));
        }).RequireRateLimiting("announcing");

        app.Logger.LogInformation("{Name}: listing protocol {Protocol}, documents of version {Document}", name, Wire.Version, Listings.Version);
        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static string Caller(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "nowhere";

    /// <summary>
    /// Where a request came from, as an IPv4 address. A v4 address arriving over a v6 socket is
    /// mapped, which is how it looks on a dual-stack listener and is not what anybody means by it.
    /// </summary>
    private static IPAddress? Address(HttpContext http)
    {
        IPAddress? ip = http.Connection.RemoteIpAddress;
        if (ip is null) return null;
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    private static async Task<int> Healthy(string[] args)
    {
        string at = args.Length > 1 ? args[1] : "http://127.0.0.1:8080/health";
        try
        {
            using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(4.0) };
            using HttpResponseMessage r = await http.GetAsync(at).ConfigureAwait(false);
            return r.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
