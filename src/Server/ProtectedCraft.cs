using System.Text.Json;

namespace KSAMultiplayer.Server;

/// <summary>
/// The craft an operator has said are not debris, kept beside the world so a restart does not
/// leave a station one <c>clear</c> from gone.
///
/// <para>Names and nothing else. A craft is known here by what the shared world calls it, the
/// server has heard of only those somebody has claimed, and a name that is nothing yet costs
/// nothing to keep: it protects the craft that is given it.</para>
///
/// <para>With no folder nothing is read or written and the list lives only as long as the server:
/// what the tests drive, and what a server given no data directory gets.</para>
/// </summary>
public sealed class ProtectedCraft(string folder = "")
{
    private readonly HashSet<string> _names = Read(folder);

    private string Path => System.IO.Path.Combine(folder, "protected.json");

    public IReadOnlySet<string> Names => _names;

    public bool Has(string name) => _names.Contains(name);

    /// <summary>False for a name already on it, or when the list is full.</summary>
    public bool Add(string name)
    {
        if (_names.Count >= ProtectedList.Most || !_names.Add(name)) return false;
        Write();
        return true;
    }

    public bool Remove(string name)
    {
        if (!_names.Remove(name)) return false;
        Write();
        return true;
    }

    /// <summary>Makes the list these names and no others: a world coming back, and what was protected in it.</summary>
    /// <returns>Whether anything changed.</returns>
    public bool Replace(IEnumerable<string> names)
    {
        HashSet<string> next = new(names.Take(ProtectedList.Most), StringComparer.Ordinal);
        if (_names.SetEquals(next)) return false;
        _names.Clear();
        _names.UnionWith(next);
        Write();
        return true;
    }

    public void Clear()
    {
        if (_names.Count == 0) return;
        _names.Clear();
        Write();
    }

    private static HashSet<string> Read(string folder)
    {
        string path = System.IO.Path.Combine(folder, "protected.json");
        if (folder.Length == 0 || !File.Exists(path)) return new(StringComparer.Ordinal);
        try
        {
            return new(JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [], StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Nothing protected is a world an operator can put right from the console; refusing to
            // start would be a world nobody can get into over a file that is not the world.
            Console.Error.WriteLine($"could not read {path}: {e.Message}; no craft is protected until one is named again");
            return new(StringComparer.Ordinal);
        }
    }

    // Written beside and moved over, so a server killed mid-write keeps the list it had.
    private void Write()
    {
        if (folder.Length == 0) return;
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path + ".new", JsonSerializer.Serialize(_names.Order().ToArray(), Indented));
            File.Move(Path + ".new", Path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"could not write {Path}: {e.Message}; this list lasts until the server stops");
        }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
