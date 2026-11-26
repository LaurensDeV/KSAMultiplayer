using System.Text.Json;

namespace KSAMultiplayer.Server;

/// <summary>
/// The kittens that have died in this server's world and whose game each died in, kept beside the
/// world so a restart does not forget who lost them.
///
/// <para>By the kitten's name, the newest death of each: KSA knows a kitten by its name, and a
/// kitten that dies twice is one brought back between, whose first death is no longer the one its
/// roster shows. Kept across a load and a fresh world, where a world's kittens can be older or newer
/// than the list: a game shows a name only where the death it has for the kitten is the one said
/// here, and every death in a world since is said.</para>
///
/// <para>With no folder nothing is read or written and the list lives only as long as the server:
/// what the tests drive, and what a server given no data directory gets.</para>
/// </summary>
public sealed class KittenMemorial(string folder = "")
{
    private readonly List<MemorialList.Death> _deaths = Read(folder);

    private string Path => System.IO.Path.Combine(folder, "memorial.json");

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<MemorialList.Death> Deaths => _deaths;

    /// <summary>Puts a death on the list in place of any the kitten had, the oldest going once it is full. False where it was already there as said.</summary>
    public bool Add(MemorialList.Death death)
    {
        int at = _deaths.FindIndex(d => d.Kitten == death.Kitten);
        if (at >= 0)
        {
            MemorialList.Death was = _deaths[at];
            if (was.By == death.By && was.Cause == death.Cause && was.Craft == death.Craft) return false;
            _deaths.RemoveAt(at);
        }
        _deaths.Add(death);
        if (_deaths.Count > MemorialList.Most) _deaths.RemoveRange(0, _deaths.Count - MemorialList.Most);
        Write();
        return true;
    }

    private static List<MemorialList.Death> Read(string folder)
    {
        string path = System.IO.Path.Combine(folder, "memorial.json");
        if (folder.Length == 0 || !File.Exists(path)) return [];
        try
        {
            return [.. (JsonSerializer.Deserialize<MemorialList.Death[]>(File.ReadAllText(path)) ?? []).TakeLast(MemorialList.Most)];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A memorial nobody can read is names missing from a window; refusing to start would be
            // a world nobody can get into over a file that is not the world.
            Console.Error.WriteLine($"could not read {path}: {e.Message}; deaths are named again from the next one said");
            return [];
        }
    }

    // Written beside and moved over, so a server killed mid-write keeps the list it had.
    private void Write()
    {
        if (folder.Length == 0) return;
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path + ".new", JsonSerializer.Serialize(_deaths, Indented));
            File.Move(Path + ".new", Path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"could not write {Path}: {e.Message}; this list lasts until the server stops");
        }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
