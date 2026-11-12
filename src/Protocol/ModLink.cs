using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace KSAMultiplayer;

/// <summary>
/// A link that has a mod manager make a game with the mods a world wants, so a player who is
/// turned away for their mods has one thing to click rather than a list to go and find.
///
/// <para>It is Borea's: <c>borea://modlist?mods=Id@1.2.3,Other@0.4.1&amp;name=Cars&amp;id=ksamp-cars</c>.
/// Borea shows the list and installs nothing until the player says so, then makes an instance
/// holding those mods and what they need. Nothing here knows more of Borea than that line, and
/// Borea knows nothing of this mod: the link is a list of ids and versions and would do as well
/// in a forum post.</para>
///
/// <para>What a link cannot say decides whether there is one at all, since Borea refuses the
/// whole of a link it does not like:</para>
/// <list type="bullet">
/// <item>Every mod needs a version, and a world often names none. A mod the world leaves open is
/// given the version this game has of it, which is one the world takes by definition; one this
/// game has not got either has no version anybody here knows, and there is no link.</item>
/// <item>A version is a release. The commit a build came from is left off, so a world held to one
/// build of a release gets a link to the release, which is the nearest thing a manager has.</item>
/// <item>KSA's own content is not a mod to a manager and is left out.</item>
/// <item>The id says which server a list is for, so a manager that has made a game from this
/// link before can offer to bring that one up to date. It must not change while the server's name
/// may. It is what the server's operator set as its <c>id</c>, and for a server that has none,
/// the address the player reaches it by: such a server under two addresses is two lists, and one
/// at an address that moves is a new list each time it does.</item>
/// </list>
/// </summary>
public static class ModLink
{
    /// <summary>KSA's own content, which every game has and no manager installs.</summary>
    public const string Ksa = "Core";

    // Borea's own limits, past which it refuses a link whole.
    private const int MaxChars = 2048, MaxMods = 100, NameChars = 64, IdChars = 64;

    private const string Start = "borea://modlist?mods=", Mark = "ksamp-";

    // An id as Borea takes one: letters, digits, dots, dashes and underscores, the first and the
    // last a letter or a digit.
    private static readonly Regex IdShape = new("^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,62}[A-Za-z0-9])?$", RegexOptions.CultureInvariant);

    // Three numbers and what may follow a dash. An assembly that carries four says its release
    // in the first three, and its fourth is nought.
    private static readonly Regex VersionShape = new(@"^(\d+\.\d+\.\d+)(?:\.0)?(-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The link for a world wanting <paramref name="wanted"/>, to a game that has
    /// <paramref name="has"/>, or null where a link cannot say what the world wants.
    /// </summary>
    /// <param name="name">What the world is called, which the manager shows and may name the new game by.</param>
    /// <param name="where">The address the player reaches the world by.</param>
    /// <param name="id">What the server's operator calls it, empty where they have not said.</param>
    public static string? For(string wanted, string has, string name, string where, string id = "")
    {
        Dictionary<string, string> theirs = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModList.Entry e in ModList.Parse(has)) theirs[e.Id] = e.Version;

        List<string> mods = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModList.Entry e in ModList.Parse(wanted))
        {
            if (string.Equals(e.Id, Ksa, StringComparison.OrdinalIgnoreCase) || !seen.Add(e.Id)) continue;
            if (!IdShape.IsMatch(e.Id)) return null;
            string version = e.Version.Length > 0 ? e.Version : theirs.GetValueOrDefault(e.Id, "");
            if (Release(version) is not { } release) return null;
            mods.Add($"{e.Id}@{release}");
        }
        if (mods.Count is 0 or > MaxMods) return null;

        StringBuilder link = new(Start);
        link.AppendJoin(',', mods);
        if (Name(name) is { Length: > 0 } called) link.Append("&name=").Append(called);
        // Under one word either way, so nothing this makes can be taken for a list of anybody
        // else's making. A server's own word for itself is as much a stranger's text as its
        // name is, and one that is not the shape of an id is not said.
        if ((IsId(id) ? Mark + id : Id(where)) is { Length: > 0 } list) link.Append("&id=").Append(list);
        return link.Length <= MaxChars ? link.ToString() : null;
    }

    /// <summary>Whether a string is a link this made, and so one that opens a mod manager and nothing else.</summary>
    public static bool Is(string link) => link.StartsWith(Start, StringComparison.Ordinal) && link.All(c => c is > ' ' and <= '~');

    /// <summary>Whether an operator's word for their server is one a link can carry.</summary>
    public static bool IsId(string id) => id.Length is > 0 and <= Wire.IdChars && IdShape.IsMatch(id);

    /// <summary>
    /// Which server a list is for where its operator has not said, from the address a player
    /// reaches it by: <c>ksamp-mpcars.ksarmory.com</c>. Empty for no address.
    /// </summary>
    public static string Id(string where)
    {
        string at = where.Trim().ToLowerInvariant();
        if (at.Length == 0) return "";
        StringBuilder id = new(Mark);
        foreach (char c in at)
        {
            // A port's colon and an IPv6 address's brackets are not letters of an id; one dash
            // stands for however many of them come together.
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_') id.Append(c);
            else if (id[^1] != '-') id.Append('-');
        }
        if (id.Length > IdChars) id.Length = IdChars;
        while (id.Length > 0 && !char.IsAsciiLetterOrDigit(id[^1])) id.Length--;
        return id.Length > Mark.Length ? id.ToString() : "";
    }

    // The release of a version as a manager knows it, or null for one that is no release at all.
    private static string? Release(string version) =>
        VersionShape.Match(ModList.Release(version.Trim())) is { Success: true } m ? m.Groups[1].Value + m.Groups[2].Value : null;

    // A name as Borea reads one: plain ASCII, a space as a plus and anything but letters, digits
    // and four marks escaped. A name in another script has no letters left and is not said.
    private static string Name(string name)
    {
        string plain = new string([.. name.Where(c => c is >= ' ' and <= '~')]).Trim();
        if (plain.Length > NameChars) plain = plain[..NameChars].TrimEnd();
        StringBuilder said = new(plain.Length);
        foreach (char c in plain)
        {
            if (c == ' ') said.Append('+');
            else if (char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~') said.Append(c);
            else said.Append('%').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
        }
        return said.ToString();
    }
}
