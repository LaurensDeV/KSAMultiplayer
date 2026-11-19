using System.Text.Json;

namespace KSAMultiplayer.Server;

/// <summary>
/// Worlds kept under names, beside the one being played: what an admin saves to come back to, and
/// what they load over the world for everybody.
///
/// <para>Each is a zip named for the save, with what it was saved at beside it, in a folder of
/// their own under the data folder. A zip an operator copies in there by hand is a save like any
/// other: it has no word beside it, so its time comes from whoever loads it first, as an imported
/// world's does.</para>
///
/// <para>Nothing kept here is ever simply gone. A save deleted, saved over, or let go as one of too
/// many kept before a load goes to <c>.deleted</c> beside the rest, under its name and when it went,
/// for <see cref="BinDays"/> days: a world is a few megabytes and a disk is not, and a save lost by
/// a slip or by somebody who should not have is a world nobody can have back.</para>
///
/// <para>With no folder nothing is read or written and the saves live only as long as the server:
/// what the tests drive, and what a server given no data directory gets.</para>
/// </summary>
public sealed class SaveShelf(string folder = "")
{
    /// <summary>
    /// What the name of a world kept before a load starts with; the time of the load follows. No
    /// other save may be named so, or one could be written over by asking for a save of its name.
    /// </summary>
    public const string AutoPrefix = "before load ";

    /// <summary>As <see cref="AutoPrefix"/>, for the world kept before a fresh one replaced it.</summary>
    public const string FreshPrefix = "before fresh ";

    /// <summary>How many worlds kept before a load are held on to: the newest, so a run of loads does not grow without end.</summary>
    public const int MostAuto = 5;

    /// <summary>The most the named saves may weigh together. A world can be tens of megabytes, and a hundred of them is a disk.</summary>
    public const long MostBytes = 1L << 30;

    /// <summary>How long a save that went is kept in the bin.</summary>
    public const int BinDays = 30;

    /// <summary>What a binned save's name ends with: when it went, so two of one name are two.</summary>
    private const string WentStamp = " (deleted ";

    private string Bin => Path.Combine(Folder, ".deleted");

    public static bool Reserved(string name) =>
        new[] { AutoPrefix, FreshPrefix }.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase) || name.Equals(p.TrimEnd(), StringComparison.OrdinalIgnoreCase));

    /// <summary>What the named saves weigh together, the server's own aside.</summary>
    public long Bytes => List().Where(s => !s.Auto).Sum(s => (long)s.Bytes);

    /// <summary>
    /// Keeps the world a load is about to replace, under a name that says when, and lets go of all
    /// but the newest <see cref="MostAuto"/> of them. The name it was kept under.
    /// </summary>
    public string KeepBeforeLoad(Blob world, string ksaBuild, string[] mods, string system, bool timeKnown, string by, IEnumerable<string>? protect = null) =>
        KeepBefore(AutoPrefix, world, ksaBuild, mods, system, timeKnown, by, protect);

    /// <summary>As <see cref="KeepBeforeLoad"/>, for the world a fresh one is about to replace: one of the same few kept.</summary>
    public string KeepBeforeFresh(Blob world, string ksaBuild, string[] mods, string system, bool timeKnown, string by, IEnumerable<string>? protect = null) =>
        KeepBefore(FreshPrefix, world, ksaBuild, mods, system, timeKnown, by, protect);

    private string KeepBefore(string prefix, Blob world, string ksaBuild, string[] mods, string system, bool timeKnown, string by, IEnumerable<string>? protect)
    {
        string name = $"{prefix}{DateTime.UtcNow:yyyy-MM-dd HH-mm-ss}";
        // Two in one second are two worlds, and the second is not to cost the first.
        for (int n = 2; Find(name) is not null; n++) name = $"{prefix}{DateTime.UtcNow:yyyy-MM-dd HH-mm-ss} ({n})";
        Keep(name, world, ksaBuild, mods, system, timeKnown, by, auto: true, protect);
        foreach (SavedWorld old in List().Where(s => s.Auto).Skip(MostAuto)) Delete(old.Name);
        return name;
    }

    /// <param name="Protected">
    /// The craft that were not to be cleared when this was kept, which are that world's and come
    /// back with it. Null where nothing says: a save from before they were kept together, or one
    /// copied in by hand, which leaves the list as it finds it.
    /// </param>
    private sealed record Meta(double Universe, bool TimeKnown, string KsaBuild, string[] Mods, DateTime SavedUtc, string System, string By, bool Auto,
                               string[]? Protected = null);

    private readonly Dictionary<string, (Meta Meta, byte[] Zip)> _held = new(StringComparer.OrdinalIgnoreCase);

    public string Folder { get; } = folder;

    private bool OnDisk => Folder.Length > 0;
    private string ZipPath(string name) => Path.Combine(Folder, name + ".zip");
    private string MetaPath(string name) => Path.Combine(Folder, name + ".json");

    /// <summary>Every save, newest first.</summary>
    public IReadOnlyList<SavedWorld> List()
    {
        List<SavedWorld> all = [];
        if (!OnDisk)
        {
            all.AddRange(_held.Select(h => Said(h.Key, h.Value.Meta, h.Value.Zip.Length)));
        }
        else if (Directory.Exists(Folder))
        {
            foreach (string zip in Directory.EnumerateFiles(Folder, "*.zip"))
            {
                string name = Path.GetFileNameWithoutExtension(zip);
                // A file whose name could not be asked for by name is not offered as one.
                if (SaveList.Name(name, out _) != name) continue;
                FileInfo file = new(zip);
                all.Add(Said(name, Read(name) ?? CopiedIn(file), (int)Math.Min(file.Length, int.MaxValue)));
            }
        }
        return [.. all.OrderByDescending(s => s.SavedUtc)];
    }

    private static SavedWorld Said(string name, Meta m, int bytes) => new(name, m.SavedUtc, m.TimeKnown ? m.Universe : 0.0, bytes, m.System ?? "", m.KsaBuild ?? "", m.By ?? "", m.Auto);

    private static Meta CopiedIn(FileInfo file) => new(0.0, false, "", [], file.LastWriteTimeUtc, "", "", false);

    /// <summary>The name a save is already kept under, however it was capitalised when asked for; null when there is none.</summary>
    public string? Find(string name)
    {
        if (!OnDisk) return _held.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (!Directory.Exists(Folder)) return null;
        return Directory.EnumerateFiles(Folder, "*.zip").Select(Path.GetFileNameWithoutExtension)
                        .FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether another save can be kept: one already there can always be written over.</summary>
    /// <param name="waiting">How many more are already on their way to being kept under new names.</param>
    public bool RoomFor(string name, int waiting = 0) => Find(name) is not null || List().Count(s => !s.Auto) + waiting < SaveList.Most;

    /// <summary>
    /// Keeps a world under a name, over whatever was kept under it. The zip is written beside and
    /// moved over, so a server killed mid-write keeps the save before.
    /// </summary>
    /// <param name="protect">The craft not to clear in this world, to come back with it; null to say nothing of them.</param>
    public void Keep(string name, Blob world, string ksaBuild, string[] mods, string system, bool timeKnown, string by, bool auto = false,
                     IEnumerable<string>? protect = null)
    {
        Meta meta = new(world.Universe, timeKnown, ksaBuild, mods, DateTime.UtcNow, system, by, auto, protect is null ? null : [.. protect.Order()]);
        // Under the name it already has, so that two saves cannot differ only by a capital on a
        // file system that tells them apart and collide on one that does not.
        string? had = Find(name);
        name = had ?? name;
        if (!OnDisk)
        {
            _held[name] = (meta, world.Data);
            return;
        }
        Directory.CreateDirectory(Folder);
        // The world it replaces is binned first, as a delete's is.
        if (had is not null) ToBin(had);
        File.WriteAllBytes(ZipPath(name) + ".new", world.Data);
        File.Move(ZipPath(name) + ".new", ZipPath(name), overwrite: true);
        File.WriteAllText(MetaPath(name), JsonSerializer.Serialize(meta, Indented));
    }

    /// <summary>A save as a world to put in place, or null when there is none of that name.</summary>
    /// <exception cref="InvalidDataException">The file of that name is not a KSA save.</exception>
    public (Blob World, bool TimeKnown, string KsaBuild, string[] Mods, string System, string[]? Protected)? Take(string name)
    {
        if (Find(name) is not { } found) return null;
        if (!OnDisk)
        {
            (Meta held, byte[] data) = _held[found];
            return (new Blob(BlobKind.World, "", 0, held.Universe, data), held.TimeKnown, held.KsaBuild, held.Mods, held.System, held.Protected);
        }
        if (Read(found) is { } m)
        {
            return (new Blob(BlobKind.World, "", 0, m.Universe, File.ReadAllBytes(ZipPath(found))), m.TimeKnown, m.KsaBuild ?? "", m.Mods ?? [], m.System ?? "",
                    m.Protected);
        }
        // Copied in by hand: read as a world given to --world is, since it came the same way.
        byte[] zip = WorldStore.Import(ZipPath(found));
        return (new Blob(BlobKind.World, "", 0, 0.0, zip), false, "", [], WorldStore.SystemOf(zip), null);
    }

    /// <summary>Takes a save off the shelf and into the bin, where it stays for <see cref="BinDays"/> days.</summary>
    public bool Delete(string name)
    {
        if (Find(name) is not { } found) return false;
        if (!OnDisk) return _held.Remove(found);
        ToBin(found);
        return true;
    }

    private void ToBin(string name)
    {
        Directory.CreateDirectory(Bin);
        string went = $"{name}{WentStamp}{DateTime.UtcNow:yyyy-MM-dd HH-mm-ss})";
        for (int n = 2; File.Exists(Path.Combine(Bin, went + ".zip")); n++) went = $"{name}{WentStamp}{DateTime.UtcNow:yyyy-MM-dd HH-mm-ss} {n})";
        File.Move(ZipPath(name), Path.Combine(Bin, went + ".zip"));
        if (File.Exists(MetaPath(name))) File.Move(MetaPath(name), Path.Combine(Bin, went + ".json"));
        // When it went, which is what the bin is emptied by: a move keeps the time it was saved.
        File.SetLastWriteTimeUtc(Path.Combine(Bin, went + ".zip"), DateTime.UtcNow);
        EmptyBin();
    }

    /// <summary>What is in the bin, newest first: each save's name there and when it went.</summary>
    public IReadOnlyList<(string Name, DateTime WentUtc, long Bytes)> Binned()
    {
        if (!OnDisk || !Directory.Exists(Bin)) return [];
        EmptyBin();
        return [.. new DirectoryInfo(Bin).EnumerateFiles("*.zip")
                                          .Select(f => (Path.GetFileNameWithoutExtension(f.Name), f.LastWriteTimeUtc, f.Length))
                                          .OrderByDescending(b => b.LastWriteTimeUtc)];
    }

    /// <summary>
    /// Puts a binned save back on the shelf, under the name it had or, where that is taken now, the
    /// name with "restored" after it. The name it is back under, or null for none in the bin.
    /// </summary>
    public string? Restore(string binned)
    {
        if (!OnDisk || !Directory.Exists(Bin)) return null;
        string? found = Directory.EnumerateFiles(Bin, "*.zip").Select(Path.GetFileNameWithoutExtension)
                                 .FirstOrDefault(n => string.Equals(n, binned, StringComparison.OrdinalIgnoreCase));
        if (found is null) return null;
        int stamp = found.LastIndexOf(WentStamp, StringComparison.Ordinal);
        string name = stamp > 0 ? found[..stamp] : found;
        for (int n = 1; Find(name) is not null; n++) name = $"{(stamp > 0 ? found[..stamp] : found)} (restored{(n > 1 ? $" {n}" : "")})";
        Directory.CreateDirectory(Folder);
        File.Move(Path.Combine(Bin, found + ".zip"), ZipPath(name));
        if (File.Exists(Path.Combine(Bin, found + ".json"))) File.Move(Path.Combine(Bin, found + ".json"), MetaPath(name));
        return name;
    }

    // Older than BinDays goes for good. Done whenever the bin is touched, which is often enough: a
    // server nobody deletes anything on keeps a bin that is not growing.
    private void EmptyBin()
    {
        try
        {
            DateTime before = DateTime.UtcNow.AddDays(-BinDays);
            foreach (FileInfo zip in new DirectoryInfo(Bin).EnumerateFiles("*.zip").Where(f => f.LastWriteTimeUtc < before).ToArray())
            {
                zip.Delete();
                File.Delete(Path.ChangeExtension(zip.FullName, ".json"));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private Meta? Read(string name)
    {
        try
        {
            return File.Exists(MetaPath(name)) ? JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaPath(name))) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // The world beside it is still a world: it is offered as one copied in.
            return null;
        }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
