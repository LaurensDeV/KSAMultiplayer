namespace KSAMultiplayer;

/// <summary>
/// A list of mods as it goes over the wire and as an operator writes it: ids separated by commas,
/// each with the version it is at or must be at after an <c>@</c> where that is said.
///
/// <para><c>Core,KSAMultiplayer@0.18.0,FastAndPurrious@0.1.12+8b152a3</c>. A game says what it
/// has, with the version each mod's own assembly carries, which for a mod built out of a
/// repository ends in the commit it was built from. A server says what it wants. An id alone
/// wants that mod at any version; a version wants that release, whatever commit it was built
/// from; and a version with the beginning of a commit after a <c>+</c> wants that build and no
/// other.</para>
///
/// <para>Two mods of one name and two versions are one mod to KSA and two to anybody playing with
/// them: a car mod that says how its cars are steering and one that does not are both called the
/// car mod, and the world they make together is one where half the cars fight their own
/// copies.</para>
/// </summary>
public static class ModList
{
    public readonly record struct Entry(string Id, string Version);

    public static Entry[] Parse(string list) =>
    [
        .. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.Split('@', 2, StringSplitOptions.TrimEntries))
            .Where(e => e[0].Length > 0)
            .Select(e => new Entry(e[0], e.Length > 1 ? e[1] : "")),
    ];

    /// <summary>The ids alone, in the order they were given.</summary>
    public static string[] Ids(string list) => [.. Parse(list).Select(e => e.Id)];

    /// <summary>Whether a mod at version <paramref name="has"/> is one a server wanting <paramref name="wanted"/> will have.</summary>
    public static bool Fits(string wanted, string has)
    {
        if (wanted.Length == 0) return true;
        if (has.Length == 0) return false;
        if (string.Equals(has, wanted, StringComparison.OrdinalIgnoreCase)) return true;
        // A release, said without the commit a build of it carries; or the beginning of that commit.
        return wanted.Contains('+')
            ? has.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)
            : has.StartsWith(wanted + "+", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What stands between a game with <paramref name="has"/> and a world wanting
    /// <paramref name="wanted"/>: the mods it lacks, the mods it has that the world has not, and
    /// those it has at another version than the world wants, each said as a player would read it.
    /// A world that wants nothing takes any game.
    /// </summary>
    public static (string[] Missing, string[] Extra, string[] Wrong) Against(string wanted, string has)
    {
        Entry[] wants = Parse(wanted);
        if (wants.Length == 0) return ([], [], []);
        Dictionary<string, string> theirs = [];
        foreach (Entry e in Parse(has)) theirs[e.Id] = e.Version;
        HashSet<string> world = [.. wants.Select(e => e.Id)];
        string[] missing = [.. wants.Where(e => !theirs.ContainsKey(e.Id)).Select(e => e.Id).Order()];
        string[] extra = [.. theirs.Keys.Where(id => !world.Contains(id)).Order()];
        string[] wrong =
        [
            .. wants.Where(e => theirs.TryGetValue(e.Id, out string? version) && !Fits(e.Version, version))
                .Select(e => Said(e, theirs[e.Id]))
                .Order(),
        ];
        return (missing, extra, wrong);
    }

    /// <summary>A version without the commit it was built from: the release a player picks to install.</summary>
    public static string Release(string version) => version.IndexOf('+') is >= 0 and var plus ? version[..plus] : version;

    // How many characters of a commit are said: what tells two builds apart, and what a page of
    // releases shows beside each.
    private const int CommitChars = 7;

    // The releases, where they differ, since that is the whole of it and a release is what a
    // player can go and get. The commits only where one release was built twice, and then only
    // their beginnings: forty characters of one say nothing seven do not.
    private static string Said(Entry wanted, string has)
    {
        if (has.Length == 0) return $"{wanted.Id} {Release(wanted.Version)} (you have a version it does not say)";
        return string.Equals(Release(wanted.Version), Release(has), StringComparison.OrdinalIgnoreCase)
            ? $"{wanted.Id} {Cut(wanted.Version)} (you have {Cut(has)})"
            : $"{wanted.Id} {Release(wanted.Version)} (you have {Release(has)})";
    }

    private static string Cut(string version) =>
        version.IndexOf('+') is >= 0 and var plus && version.Length > plus + 1 + CommitChars ? version[..(plus + 1 + CommitChars)] : version;
}
