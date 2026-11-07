using System.Reflection;

namespace KSAMultiplayer.Server;

/// <summary>
/// Empty worlds the server carries, one per celestial system, so it can start with a world rather
/// than waiting for the first player to hand theirs over.
///
/// <para>Each is a save KSA itself wrote and nothing here touched: a <c>meta.toml</c> and a
/// <c>universe.xml</c> with no craft in them, a few kilobytes apiece. The server cannot make one --
/// a world is KSA's own format and this half of the project never references KSA, on a machine with
/// no game installed -- so one was made once, in the game, and carried as bytes. They are zipped
/// into the same shape a player's handover has and sent out the same way; nothing reads inside
/// them.</para>
///
/// <para>What that costs is a version and a system. A save says which KSA build wrote it, so one of
/// these goes stale when KSA's format moves on, and the system it was made in is the system a world
/// started from it is in for ever. Hence one per system and a setting to pick, rather than one
/// world assumed.</para>
/// </summary>
public static class StockWorld
{
    private const string Prefix = "KSAMultiplayer.Server.worlds.";

    private static readonly Assembly Here = typeof(StockWorld).Assembly;

    /// <summary>The systems there is an empty world for, as KSA names them: the values the setting takes.</summary>
    public static string[] Systems { get; } =
        [.. Here.GetManifestResourceNames()
                .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
                .Select(n => n[Prefix.Length..])
                .Select(rest => rest.IndexOf('.') is > 0 and var dot ? rest[..dot] : "")
                .Where(s => s.Length > 0)
                .Distinct()
                .Order()];

    public static bool Has(string system) => Systems.Contains(system, StringComparer.Ordinal);

    /// <summary>
    /// The empty world for a system, zipped as a save folder is, or null for a system there is none
    /// for. The files go in under their own names and no folder, which is what
    /// <see cref="WorldStore.Import"/> makes of a folder and what a game expects to unpack.
    /// </summary>
    public static byte[]? Zip(string system)
    {
        if (!Has(system)) return null;
        string folder = Prefix + system + ".";
        List<(string Name, byte[] Data)> files = [];
        foreach (string resource in Here.GetManifestResourceNames().Where(n => n.StartsWith(folder, StringComparison.Ordinal)).Order())
        {
            using Stream? open = Here.GetManifestResourceStream(resource);
            if (open is null) continue;
            using MemoryStream into = new();
            open.CopyTo(into);
            files.Add((resource[folder.Length..], into.ToArray()));
        }
        return files.Count > 0 ? Blob.Zip(files) : null;
    }
}
