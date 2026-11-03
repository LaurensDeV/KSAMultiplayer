using System.Text.Json;

namespace KSAMultiplayer.Server;

/// <summary>
/// The world kept on disk, so a server restarted carries on from the last save of it: the save
/// folder zipped as it came, and beside it what it was saved at and which game it needs.
/// </summary>
public sealed class WorldStore(string folder)
{
    private sealed record Meta(double Universe, bool TimeKnown, string KsaBuild, string[] Mods, DateTime SavedUtc);

    public string Folder { get; } = folder;

    private string ZipPath => Path.Combine(Folder, "world.zip");
    private string MetaPath => Path.Combine(Folder, "world.json");

    public (Blob World, bool TimeKnown, string KsaBuild, string[] Mods)? Load()
    {
        if (!File.Exists(ZipPath)) return null;
        byte[] zip = File.ReadAllBytes(ZipPath);
        Meta meta = File.Exists(MetaPath) ? JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaPath)) ?? Unknown : Unknown;
        return (new Blob(BlobKind.World, "", 0, meta.Universe, zip), meta.TimeKnown, meta.KsaBuild, meta.Mods);
    }

    private static readonly Meta Unknown = new(0.0, false, "", [], DateTime.MinValue);

    /// <summary>Written beside and moved over, so a server killed mid-write keeps the save before.</summary>
    public void Save(Blob world, string ksaBuild, string[] mods, bool timeKnown)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(ZipPath + ".new", world.Data);
        File.Move(ZipPath + ".new", ZipPath, overwrite: true);
        File.WriteAllText(MetaPath, JsonSerializer.Serialize(new Meta(world.Universe, timeKnown, ksaBuild, mods, DateTime.UtcNow), Indented));
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// A KSA save copied in by hand, as its folder or a zip of it: the folder that holds the save's own
    /// files, wherever in the zip that is, so a zip of the folder and a zip of what is in it both work.
    /// </summary>
    public static byte[] Import(string path)
    {
        if (Directory.Exists(path))
        {
            string root = SaveRoot(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(path, f).Replace('\\', '/')));
            return Blob.ZipFolder(root.Length > 0 ? Path.Combine(path, root) : path);
        }
        List<(string Name, byte[] Data)> files = Blob.Unzip(File.ReadAllBytes(path));
        string prefix = SaveRoot(files.Select(f => f.Name));
        if (prefix.Length == 0) return Blob.Zip(files);
        return Blob.Zip(files.Where(f => f.Name.StartsWith(prefix + "/", StringComparison.Ordinal)).Select(f => (f.Name[(prefix.Length + 1)..], f.Data)));
    }

    // The folder universe.xml is in, which is the top of a KSA save; nothing if it is at the top already.
    private static string SaveRoot(IEnumerable<string> names)
    {
        string? universe = names.Where(n => n == "universe.xml" || n.EndsWith("/universe.xml", StringComparison.Ordinal)).OrderBy(n => n.Length).FirstOrDefault()
            ?? throw new InvalidDataException("that is not a KSA save: it has no universe.xml");
        return universe.Length > "universe.xml".Length ? universe[..^"/universe.xml".Length] : "";
    }
}
