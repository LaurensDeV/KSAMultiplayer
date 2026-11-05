using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KSAMultiplayer.Server;

/// <summary>
/// Who may run commands, kept beside the world so a restart does not take everybody's admin away.
///
/// <para>A token is handed to a player once and only its hash is kept here. That is enough to take an
/// admin away and not enough to become one, so the file is no worse to lose than the world beside
/// it, and an operator reading it learns nothing they could log in with.</para>
///
/// <para>With no folder nothing is read or written and the list lives only as long as the server:
/// what the tests drive, and what a server given no data directory gets.</para>
/// </summary>
public sealed class Admins(string folder = "")
{
    /// <param name="Name">Who they were when they last joined with this token, so that revoking one names whoever is seen playing.</param>
    private sealed record Entry(string Name, string Hash, DateTime SinceUtc);

    private readonly List<Entry> _admins = Read(folder);

    private string Path => System.IO.Path.Combine(folder, "admins.json");

    public IReadOnlyList<string> Names => [.. _admins.Select(a => a.Name)];

    /// <summary>
    /// Whether this token is an admin's, and the name it is now remembered under: a token outlives the
    /// name it was given to, and an operator revokes the player they can see rather than the one who
    /// was opped months ago.
    /// </summary>
    public bool Check(string token, string name)
    {
        if (token.Length == 0) return false;
        string hash = Hash(token);
        int i = _admins.FindIndex(a => Same(a.Hash, hash));
        if (i < 0) return false;
        if (!string.Equals(_admins[i].Name, name, StringComparison.Ordinal))
        {
            _admins[i] = _admins[i] with { Name = name };
            Write();
        }
        return true;
    }

    /// <summary>
    /// Makes this name an admin and returns the token to hand them, which is the only time it exists
    /// anywhere but on their machine. Any token they had stops working, so opping somebody again is
    /// how one that was lost is replaced.
    /// </summary>
    public string Add(string name)
    {
        string token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        _admins.RemoveAll(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        _admins.Add(new Entry(name, Hash(token), DateTime.UtcNow));
        Write();
        return token;
    }

    /// <summary>How many were taken off the list, which is zero for a name that was never on it.</summary>
    public int Remove(string name)
    {
        int gone = _admins.RemoveAll(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (gone > 0) Write();
        return gone;
    }

    public bool Is(string name) => _admins.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static bool Same(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static List<Entry> Read(string folder)
    {
        string path = System.IO.Path.Combine(folder, "admins.json");
        if (folder.Length == 0 || !File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A list that cannot be read leaves nobody an admin, which the console can put right; refusing
            // to start would leave a world nobody can get into over a file that is not the world.
            Console.Error.WriteLine($"could not read {path}: {e.Message}; nobody is an admin until somebody is opped again");
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
            File.WriteAllText(Path + ".new", JsonSerializer.Serialize(_admins, Indented));
            File.Move(Path + ".new", Path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"could not write {Path}: {e.Message}; this admin is one until the server stops");
        }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
