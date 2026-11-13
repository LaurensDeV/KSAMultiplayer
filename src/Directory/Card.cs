using System.Net;
using System.Text;

namespace KSAMultiplayer.Directories;

/// <summary>
/// What a link to the page says of itself where it is pasted: the title and the line of text a
/// chat program shows under a link, which for this page is how many servers there are and how
/// many players are on them.
///
/// <para>Written into the page by the program and not by the page's own script, because what
/// reads it runs no script: a chat program fetches the address, reads the tags in the head and
/// leaves. For the same reason a server's page is an address of its own, <c>/s/cars.example.org</c>,
/// and not a fragment after a <c>#</c>, which a browser never sends anybody.</para>
///
/// <para>Every word in it that a server or a directory chose is a stranger's text going into
/// markup, and is encoded on the way.</para>
/// </summary>
public static class Card
{
    // What KSA's own list of systems calls the ones it ships with; any other is said as its id.
    private static readonly Dictionary<string, string> Systems = new(StringComparer.Ordinal) { ["SolLite"] = "Earth and Moon" };

    /// <summary>The most servers named under a link to the whole list, which is a line of text and not the list.</summary>
    private const int MostNamed = 6;

    /// <summary>Where a server's page is, under the page of them all.</summary>
    public const string Under = "/s/";

    /// <summary>A server as a player types it: the name alone where that finds it.</summary>
    public static string Where(Listed s) => s.Port == Wire.DefaultPort || s.Srv ? s.Host : $"{s.Host}:{s.Port}";

    /// <summary>The server a page's address names, by what a player types or by host and port written out.</summary>
    public static Listed? Find(Everything all, string where) =>
        all.Servers.Select(c => c.Listed).FirstOrDefault(s =>
            string.Equals(Where(s), where, StringComparison.OrdinalIgnoreCase)
            || string.Equals($"{s.Host}:{s.Port}", where, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The head's title and tags for the page of every server, or for one server's page where
    /// <paramref name="one"/> is given.
    /// </summary>
    public static string Head(Everything all, Listed? one = null)
    {
        (string title, string says) = one is null ? Everyone(all) : One(one);
        string t = WebUtility.HtmlEncode(title), d = WebUtility.HtmlEncode(says);
        return $"""
            <title>{t}</title>
            <meta name="description" content="{d}">
            <meta property="og:type" content="website">
            <meta property="og:site_name" content="KSA Multiplayer">
            <meta property="og:title" content="{t}">
            <meta property="og:description" content="{d}">
            <meta name="twitter:card" content="summary">
            <meta name="theme-color" content="#3d7bfa">
            """;
    }

    /// <summary>What is said for an address that names no server on the list.</summary>
    public static string Gone() =>
        """
        <title>Not listed — KSA Multiplayer servers</title>
        <meta name="robots" content="noindex">
        """;

    private static (string Title, string Says) Everyone(Everything all)
    {
        const string title = "KSA Multiplayer servers";
        Listed[] servers = [.. all.Servers.Select(c => c.Listed)];
        if (servers.Length == 0) return (title, "No server is listed right now.");
        int players = servers.Sum(s => Math.Max(0, s.Players));
        StringBuilder says = new($"{Count(servers.Length, "server")}, {Count(players, "player")} online.");
        says.Append(' ').AppendJoin(" · ", servers.Take(MostNamed).Select(s => $"{Named(s)} {Players(s)}"));
        if (servers.Length > MostNamed) says.Append($" · and {servers.Length - MostNamed} more");
        return (title, says.ToString());
    }

    private static (string Title, string Says) One(Listed s)
    {
        string title = $"{Named(s)} · {(s.Max > 0 ? $"{Math.Max(0, s.Players)} of {s.Max} players" : Count(Math.Max(0, s.Players), "player"))} online";
        StringBuilder says = new($"A KSA Multiplayer server at {Where(s)}.");
        if (s.System.Length > 0) says.Append($" World: {Systems.GetValueOrDefault(s.System, s.System)}.");
        string[] mods = [.. ModList.Parse(s.Mods)
            .Where(m => !string.Equals(m.Id, ModLink.Ksa, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Version.Length > 0 ? $"{m.Id} {ModList.Release(m.Version)}" : m.Id)];
        if (mods.Length > 0) says.Append($" Needs {string.Join(", ", mods)}.");
        if (s.Ksa.Length > 0) says.Append($" KSA {s.Ksa}.");
        if (s.Password) says.Append(" Password needed.");
        return (title, says.ToString());
    }

    private static string Named(Listed s) => s.Name.Length > 0 ? s.Name : Where(s);

    private static string Players(Listed s) => s.Max > 0 ? $"{Math.Max(0, s.Players)}/{s.Max}" : $"{Math.Max(0, s.Players)}";

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";
}
