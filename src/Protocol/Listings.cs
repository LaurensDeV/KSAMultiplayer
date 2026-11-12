using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace KSAMultiplayer;

/// <summary>
/// One server as a directory lists it: an address worth asking about, and what the directory saw
/// when it last asked.
///
/// <para>Everything past the address is a hint and is labelled as one. It decides what a window
/// shows first and what it leaves out before its own answers arrive, and is written over by them
/// within the second. A directory that lists a server it never reached, or a player count it was
/// told rather than saw, is a directory whose hints are wrong and whose addresses are still worth
/// having.</para>
/// </summary>
/// <param name="Seen">When the directory last had an answer from it, as seconds since 1970. Zero from a directory that keeps no times, such as a hand-written list.</param>
/// <param name="Srv">
/// Whether the name alone finds the server: every SRV record under it gives this port, so a
/// reader may show <c>cars.example.com</c> where it would have shown <c>cars.example.com:50101</c>,
/// and a player may type it so. A hint like the rest: a game looks the record up for itself
/// before it drops the port.
/// </param>
public sealed record Listed(
    string Host,
    int Port,
    string Name = "",
    int Protocol = 0,
    string Ksa = "",
    int Players = 0,
    int Max = 0,
    bool Password = false,
    string System = "",
    string Mods = "",
    long Seen = 0,
    bool Srv = false);

/// <summary>
/// What a directory answers, and the whole of what one has to be able to do.
///
/// <para>Deliberately a document rather than a protocol: a file served by anything is a directory,
/// so a clan's three servers can be a static file and a player cannot tell it from a service with
/// heartbeats and probes behind it. Registration is the other half, and a directory is allowed to
/// have no such thing.</para>
/// </summary>
/// <param name="V">The shape of this document, 1. A reader that does not know a version leaves the whole listing alone rather than guessing at its fields.</param>
/// <param name="Directory">What the directory calls itself, for a row to say where it was found and for a player to know which one to switch off.</param>
public sealed record Listing(int V, string Directory, Listed[] Servers);

/// <summary>What a server sends a directory to be listed by it.</summary>
/// <param name="Host">The name it wants listing under, empty to be listed at whatever address the directory sees this arrive from. A name has to resolve to the address the announcement came from, and is asked as well: without the first a directory is a port scanner anybody may aim, and without the second it lists servers nobody can reach.</param>
public sealed record Announcement(int V, int Port, string Host = "");

/// <summary>
/// What a directory says back about an announcement, which goes into the server's log whatever it
/// says.
///
/// <para>The failure worth designing against is the quiet one: an address published, nothing able
/// to reach it, and nobody told. So this carries the address the directory saw and whether the
/// server is listed, and one that is not says why in words somebody can act on.</para>
/// </summary>
/// <param name="Seen">The address the directory saw the announcement come from, which is the one thing about it the sender did not choose.</param>
/// <param name="Listed">Whether the server is in the list now. False while a directory has not managed to reach it.</param>
public sealed record Noted(bool Ok, string Seen = "", bool Listed = false, string Say = "");

/// <summary>
/// Reading directories and announcing to them: the whole of this mod's dealings with anything that
/// is neither a game nor a server.
///
/// <para>Over HTTPS and never over UDP, which is not a style choice. A list answered over UDP turns
/// one small datagram into one unconnected reply per registered server -- a few hundred times
/// amplification, aimed at whatever address the request claimed to come from. A connection that has
/// to be established cannot be aimed at a third party.</para>
///
/// <para>Written against <c>System.Net.Http</c> and <c>System.Text.Json</c> and nothing else. KSA
/// holds an <c>HttpClient</c> of its own and this mod already reads its settings with
/// <c>System.Text.Json</c>, so both are known to be in the process; <c>System.Net.Http.Json</c> is
/// a third assembly that a trimmed install need not carry, and a mod that ships one DLL cannot
/// bring it along.</para>
/// </summary>
public static class Listings
{
    /// <summary>The shape of the documents this speaks.</summary>
    public const int Version = 1;

    /// <summary>
    /// Where to look when nobody has said otherwise.
    ///
    /// <para>The last resort and not the first, because a list compiled into a build is a list that
    /// cannot be fixed: a directory that stops answering has to be replaceable without shipping
    /// anything, which is what <see cref="KnownAt"/> is for.</para>
    /// </summary>
    public static readonly string[] BuiltIn = ["https://servers.ksarmory.com/v1/servers"];

    /// <summary>Where the list of directories is kept, so that retiring one is a commented-out line rather than a release.</summary>
    public static string KnownAt { get; set; } = "https://raw.githubusercontent.com/LaurensDeV/KSAMultiplayer/main/directories.txt";

    /// <summary>The most directories read from that list, since it is fetched over the internet like anything else.</summary>
    public const int MostKnown = 16;

    /// <summary>
    /// Which directories to use: the list as it stands, or the last one that was read, or the one
    /// built in -- in that order, and never nothing.
    ///
    /// <para><paramref name="cache"/> is a file to keep the answer in. It is written when a list is
    /// read and used when one cannot be, so a machine that starts up with no internet still knows
    /// every directory it knew yesterday. Null to keep nothing.</para>
    /// </summary>
    public static async Task<string[]> KnownAsync(string? cache, CancellationToken stop = default)
    {
        string[] said = [];
        try
        {
            said = Lines(await Http.GetStringAsync(KnownAt, stop).ConfigureAwait(false));
        }
        catch (Exception e) when (Ordinary(e))
        {
        }
        if (said.Length > 0)
        {
            if (cache is not null)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                    await File.WriteAllLinesAsync(cache, said, stop).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                {
                }
            }
            return said;
        }
        if (cache is not null)
        {
            try
            {
                if (File.Exists(cache) && Lines(await File.ReadAllTextAsync(cache, stop).ConfigureAwait(false)) is { Length: > 0 } kept) return kept;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
        return BuiltIn;
    }

    /// <summary>
    /// One URL to a line, <c>#</c> for a comment. Over HTTPS only: this list is fetched from the
    /// internet, and a directory read over plain HTTP is one anybody on the path may add servers to.
    /// A directory given by hand in a setting is not held to this, because testing one needs a
    /// machine without a certificate.
    /// </summary>
    public static string[] Lines(string text) =>
        [.. text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && l.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MostKnown)];

    /// <summary>
    /// The most of a directory's answer that is read, bytes. A directory is a stranger: the cap is
    /// what stops one handing a game at startup something it has to hold in memory.
    /// </summary>
    private const int MostBytes = 1 << 20;

    /// <summary>The most servers taken from one directory, so a list cannot be buried by whoever registers five hundred of them.</summary>
    public const int MostServers = 250;

    private const string Type = "application/json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// What this calls itself when it asks a directory anything, so a directory's log says which
    /// build asked. Set by whoever starts up, before anything is asked.
    /// </summary>
    public static string Agent { get; set; } = "KSAMultiplayer";

    private static readonly Lock Making = new();
    private static HttpClient? _http;

    /// <remarks>
    /// One of these for the process, made on first use so that <see cref="Agent"/> set at startup is
    /// in it. A client per request is the mistake that exhausts sockets, and this is asked the same
    /// few questions for as long as a game or a server runs.
    /// </remarks>
    private static HttpClient Http
    {
        get
        {
            lock (Making)
            {
                if (_http is not null) return _http;
                _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8.0), MaxResponseContentBufferSize = MostBytes };
                _http.DefaultRequestHeaders.Add("User-Agent", Agent);
                _http.DefaultRequestHeaders.Add("Accept", Type);
                return _http;
            }
        }
    }

    private static StringContent Body<T>(T what) => new(JsonSerializer.Serialize(what, Json), Encoding.UTF8, Type);

    /// <summary>
    /// What one directory lists, or null when nothing usable came back. Never throws: a directory
    /// being down is an ordinary afternoon, and it must not be able to stop a game starting or a
    /// player typing an address.
    /// </summary>
    public static async Task<Listing?> ReadAsync(string url, CancellationToken stop = default)
    {
        try
        {
            string said = await Http.GetStringAsync(url, stop).ConfigureAwait(false);
            Listing? listing = JsonSerializer.Deserialize<Listing>(said, Json);
            // A document of a version this does not know is left alone rather than read for the
            // fields that happen to look familiar.
            if (listing is null || listing.V != Version) return null;
            Listed[] servers = [.. (listing.Servers ?? [])
                .Where(s => s is { Host.Length: > 0 } && s.Port is > 0 and < 65536)
                .Take(MostServers)];
            return listing with { Servers = servers, Directory = Wire.Clip(listing.Directory, 48) };
        }
        catch (Exception e) when (Ordinary(e))
        {
            return null;
        }
    }

    /// <summary>
    /// Says a server is here, to one directory. Null when the directory could not be reached at all,
    /// which is a different thing from one that answered and declined to list the server.
    /// </summary>
    public static async Task<Noted?> AnnounceAsync(string url, int port, string host, CancellationToken stop = default)
    {
        try
        {
            using HttpResponseMessage r = await Http.PostAsync(Announce(url), Body(new Announcement(Version, port, host)), stop)
                                                    .ConfigureAwait(false);
            // A refusal that carries words is worth reading: it is how a directory says a port was
            // unreachable, or that an address is not one it will list.
            string said = await r.Content.ReadAsStringAsync(stop).ConfigureAwait(false);
            return (said.Length > 0 ? JsonSerializer.Deserialize<Noted>(said, Json) : null)
                   ?? new Noted(r.IsSuccessStatusCode, Say: $"the directory answered {(int)r.StatusCode} and nothing else");
        }
        catch (Exception e) when (Ordinary(e))
        {
            return null;
        }
    }

    /// <summary>
    /// Takes a server out of a directory at once, rather than leaving it listed until its entry goes
    /// stale. Sent when advertising is turned off and when the server stops: a row that outlives the
    /// server behind it is the thing a list of servers is most often wrong about.
    /// </summary>
    public static async Task<bool> WithdrawAsync(string url, int port, string host, CancellationToken stop = default)
    {
        try
        {
            using HttpRequestMessage ask = new(HttpMethod.Delete, Announce(url)) { Content = Body(new Announcement(Version, port, host)) };
            using HttpResponseMessage r = await Http.SendAsync(ask, stop).ConfigureAwait(false);
            return r.IsSuccessStatusCode;
        }
        catch (Exception e) when (Ordinary(e))
        {
            return false;
        }
    }

    /// <summary>
    /// Where a directory is announced to, worked out from where it is read. One address in a
    /// setting rather than two: an operator who has to keep a pair of URLs in step will one day not.
    /// </summary>
    public static string Announce(string listing) =>
        listing.EndsWith("/servers", StringComparison.OrdinalIgnoreCase)
            ? string.Concat(listing.AsSpan(0, listing.Length - "/servers".Length), "/announce")
            : listing.TrimEnd('/') + "/announce";

    /// <summary>Everything a directory on the other side of the internet can ordinarily do to this, none of which is this end's fault or worth throwing over.</summary>
    private static bool Ordinary(Exception e) =>
        e is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException
          or InvalidOperationException or UriFormatException or NotSupportedException or IOException;
}
