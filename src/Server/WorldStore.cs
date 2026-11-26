using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace KSAMultiplayer.Server;

/// <summary>
/// The world kept on disk, so a server restarted carries on from the last save of it: the save
/// folder zipped as it came, and beside it what it was saved at and which game it needs.
/// </summary>
public sealed class WorldStore(string folder)
{
    private sealed record Meta(double Universe, bool TimeKnown, string KsaBuild, string[] Mods, DateTime SavedUtc, string System = "");

    public string Folder { get; } = folder;

    private string ZipPath => Path.Combine(Folder, "world.zip");
    private string MetaPath => Path.Combine(Folder, "world.json");

    public (Blob World, bool TimeKnown, string KsaBuild, string[] Mods, string System)? Load()
    {
        if (!File.Exists(ZipPath)) return null;
        byte[] zip = File.ReadAllBytes(ZipPath);
        Meta meta = File.Exists(MetaPath) ? JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaPath)) ?? Unknown : Unknown;
        return (new Blob(BlobKind.World, "", 0, meta.Universe, zip), meta.TimeKnown, meta.KsaBuild, meta.Mods, meta.System ?? "");
    }

    private static readonly Meta Unknown = new(0.0, false, "", [], DateTime.MinValue, "");

    /// <summary>Written beside and moved over, so a server killed mid-write keeps the save before.</summary>
    public void Save(Blob world, string ksaBuild, string[] mods, string system, bool timeKnown)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(ZipPath + ".new", world.Data);
        File.Move(ZipPath + ".new", ZipPath, overwrite: true);
        File.WriteAllText(MetaPath, JsonSerializer.Serialize(new Meta(world.Universe, timeKnown, ksaBuild, mods, DateTime.UtcNow, system), Indented));
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

    /// <summary>
    /// The celestial system a save was made in, off its own <c>meta.toml</c>, or empty when it does not
    /// say: an operator's world then asks no system of a player, and the first to join settles it.
    /// </summary>
    public static string SystemOf(byte[] zip)
    {
        try
        {
            foreach ((string name, byte[] data) in Blob.Unzip(zip))
            {
                if (!name.EndsWith("meta.toml", StringComparison.OrdinalIgnoreCase)) continue;
                Match found = Regex.Match(Encoding.UTF8.GetString(data), "systems\\s*=\\s*\\[\\s*\"([^\"]+)\"");
                if (found.Success) return found.Groups[1].Value;
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException)
        {
            // A save that cannot be read here is reported when it is handed out, not now.
        }
        return "";
    }

    /// <summary>
    /// The name of every craft a save holds, or null where it cannot be read: KSA writes each as a
    /// <c>Vehicle</c> of the system, a kitten on EVA among them, under the name the shared world knows.
    ///
    /// <para>What the server forgets is decided by it. Forgetting a craft's definition because a save
    /// came after it took the save to hold that craft, and the save was of a game that had never
    /// built it: a player's 359 craft were in no world but his own, and went when he did.</para>
    /// </summary>
    public static HashSet<string>? CraftIn(byte[] zip)
    {
        try
        {
            List<(string Name, byte[] Data)> files = Blob.Unzip(zip);
            string root = SaveRoot(files.Select(f => f.Name));
            string path = root.Length > 0 ? root + "/universe.xml" : "universe.xml";
            byte[] universe = files.First(f => f.Name == path).Data;
            HashSet<string> held = new(StringComparer.Ordinal);
            using XmlReader r = XmlReader.Create(new MemoryStream(universe), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            // The root's systems, and in each the craft: Universe > System > Vehicle.
            string under = "";
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element) continue;
                if (r.Depth == 1) under = r.LocalName;
                else if (r.Depth == 2 && under == "System" && r.LocalName == "Vehicle" && r.GetAttribute("Id") is { Length: > 0 } id) held.Add(id);
            }
            return held;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or XmlException or InvalidOperationException)
        {
            return null;
        }
    }

    // The folder universe.xml is in, which is the top of a KSA save; nothing if it is at the top already.
    private static string SaveRoot(IEnumerable<string> names)
    {
        string? universe = names.Where(n => n == "universe.xml" || n.EndsWith("/universe.xml", StringComparison.Ordinal)).OrderBy(n => n.Length).FirstOrDefault()
            ?? throw new InvalidDataException("that is not a KSA save: it has no universe.xml");
        return universe.Length > "universe.xml".Length ? universe[..^"/universe.xml".Length] : "";
    }
}
