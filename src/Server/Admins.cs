using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KSAMultiplayer.Server;

/// <summary>
/// Who may run commands, kept beside the world so a restart does not take everybody's admin away.
///
/// <para>A token is handed to a player's game and only its hash is kept here. That is enough to take an
/// admin away and not enough to become one, so the file is no worse to lose than the world beside
/// it, and an operator reading it learns nothing they could log in with.</para>
///
/// <para>A token is offered before it is held. Until the game it was made for has shown it has it,
/// whatever that name was an admin by goes on working beside it: the token crosses a wire that
/// loses things, and one retired for a replacement that never arrived is an admin nobody holds.</para>
///
/// <para>A Discord user can be an admin too, by a row of their own with no hash: an admin whoever's
/// machine they log in from, for as long as the row is there. Logging in still hands their game a
/// token, so the login is once per machine rather than once per join.</para>
///
/// <para>With no folder nothing is read or written and the list lives only as long as the server:
/// what the tests drive, and what a server given no data directory gets.</para>
/// </summary>
public sealed class Admins(string folder = "")
{
    /// <param name="Name">Who they were when they last joined with this token, so that revoking one names whoever is seen playing.</param>
    /// <param name="Offered">Whether nothing has shown this token yet: it was sent, and may or may not have arrived.</param>
    /// <param name="Discord">The Discord user this row makes an admin, on a row with no hash; empty on a token's.</param>
    private sealed record Entry(string Name, string Hash, DateTime SinceUtc, bool Offered = false, string Discord = "");

    private readonly List<Entry> _admins = Read(folder);

    private string Path => System.IO.Path.Combine(folder, "admins.json");

    /// <summary>Each admin once, though a name with a token on its way to it has two rows.</summary>
    public IReadOnlyList<string> Names => [.. _admins.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Whether this token is an admin's, and the name it is now remembered under: a token outlives the
    /// name it was given to, and an operator revokes the player they can see rather than the one who
    /// was opped months ago.
    ///
    /// <para>A token shown for the first time is a token that arrived, which is when whatever else
    /// its name was an admin by stops working, and not before.</para>
    /// </summary>
    public bool Check(string token, string name)
    {
        if (token.Length == 0) return false;
        string hash = Hash(token);
        int i = _admins.FindIndex(a => Same(a.Hash, hash));
        if (i < 0) return false;
        Entry held = _admins[i];
        if (!held.Offered && string.Equals(held.Name, name, StringComparison.Ordinal)) return true;
        Entry now = held with { Name = name, Offered = false };
        _admins[i] = now;
        // By the name it was offered under, not the one it is seen under now: the rows it replaces
        // are that name's tokens. Not its Discord user, which is what the token may have been
        // handed out for.
        if (held.Offered)
        {
            _admins.RemoveAll(a => !ReferenceEquals(a, now) && a.Discord.Length == 0
                                   && string.Equals(a.Name, held.Name, StringComparison.OrdinalIgnoreCase));
        }
        Write();
        return true;
    }

    /// <summary>
    /// Makes this name an admin and returns the token to hand them, which is written down nowhere
    /// but on their machine. Whatever they were an admin by goes on working until this one is shown
    /// in <see cref="Check"/>, so opping somebody again is how a token that was lost is replaced, and
    /// a replacement lost on its way costs nothing. An earlier offer nobody took up is withdrawn:
    /// a name has one token on its way to it at most.
    /// </summary>
    public string Offer(string name)
    {
        string token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        _admins.RemoveAll(a => a.Offered && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        _admins.Add(new Entry(name, Hash(token), DateTime.UtcNow, Offered: true));
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

    /// <summary>
    /// Makes a Discord user an admin, under the name they are known by here: a player's when they are
    /// playing, or their id until they first log in. Says whether they were not one already.
    /// </summary>
    public bool AddDiscord(string id, string name)
    {
        int i = _admins.FindIndex(a => a.Discord == id);
        if (i >= 0)
        {
            if (string.Equals(_admins[i].Name, name, StringComparison.Ordinal)) return false;
            _admins[i] = _admins[i] with { Name = name };
            Write();
            return false;
        }
        _admins.Add(new Entry(name, "", DateTime.UtcNow, Discord: id));
        Write();
        return true;
    }

    /// <summary>
    /// Whether this Discord user is an admin, renaming their row to the name they are seen playing
    /// under now: as with a token, an operator takes admin away from the player they can see.
    /// </summary>
    public bool CheckDiscord(string id, string name)
    {
        if (id.Length == 0) return false;
        int i = _admins.FindIndex(a => a.Discord == id);
        if (i < 0) return false;
        if (!string.Equals(_admins[i].Name, name, StringComparison.Ordinal))
        {
            _admins[i] = _admins[i] with { Name = name };
            Write();
        }
        return true;
    }

    /// <summary>
    /// Takes admin away from a Discord user, and from every token of the name they were last known
    /// by: those were handed out for being this user, and the name is all a token is filed under.
    /// Returns that name, or null for a user who was no admin.
    /// </summary>
    public string? RemoveDiscord(string id)
    {
        if (_admins.FirstOrDefault(a => a.Discord == id) is not { } row) return null;
        Remove(row.Name);
        _admins.RemoveAll(a => a.Discord == id);
        Write();
        return row.Name;
    }

    /// <summary>The name a Discord user's row is under, or null for one who is no admin.</summary>
    public string? NameOfDiscord(string id) => _admins.FirstOrDefault(a => a.Discord.Length > 0 && a.Discord == id)?.Name;

    /// <summary>The Discord user that makes this name an admin, or empty.</summary>
    public string DiscordOf(string name) =>
        _admins.FirstOrDefault(a => a.Discord.Length > 0 && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))?.Discord ?? "";

    /// <summary>Whether any Discord user is an admin here, and so whether Discord logins are worth taking.</summary>
    public bool HasDiscord => _admins.Any(a => a.Discord.Length > 0);

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
