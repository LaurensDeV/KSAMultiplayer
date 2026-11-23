using System.Text;

namespace KSAMultiplayer.Server;

/// <summary>
/// The mod's logs players' games send, kept beside the world under <c>player-logs/&lt;day&gt;/</c>
/// for two weeks. Only a server with <c>collect-logs</c> on takes them, and only the developer's
/// test server is ever sent one: the games decide that, not the server (see the mod's PlayerLogs).
///
/// <para>What arrives is cleaned again here, whatever the game did: it comes from another machine.
/// With no folder nothing is written, for the tests and a server given no data directory.</para>
/// </summary>
public sealed class PlayerLogs(string folder = "")
{
    /// <summary>How long a log is kept.</summary>
    public const int KeptDays = 14;

    /// <summary>The most logs one player's game is taken from in one session: it sends its last three of the mod's and three of KSA's at most.</summary>
    public const int MostPerSession = 8;

    public string Folder => Path.Combine(folder, "player-logs");

    /// <summary>What was kept since the server started, for the tests: the file's name, and the text as kept.</summary>
    public List<(string File, string Text)> Kept { get; } = [];

    /// <summary>
    /// Keeps one log, and says where; null where it could not be written. Filed by the player's
    /// Discord account and their number on this server, never their name: a name is whatever a
    /// player typed, another player can type it too, and it lands in a path. The name heads the
    /// text instead, for whoever reads it.
    /// </summary>
    /// <param name="discord">The Discord account the server has checked this player logged in as, or empty.</param>
    public string? Keep(string name, byte id, string discord, string discordName, string session, string text, DateTime utc)
    {
        string who = discord.Length > 0 ? $"discord-{Safe(discord)}" : "no-discord";
        string file = $"{who}.player-{id}.{Safe(session)}.{utc:HHmmss}.log.gz";
        // One line, whatever was typed for a name.
        name = string.Concat(name.Select(c => char.IsControl(c) ? ' ' : c));
        string header = $"player {id}, playing as {name}"
                        + (discord.Length > 0 ? $", logged in with Discord as {discordName} ({discord})" : ", not logged in with Discord")
                        + $"; sent {utc:yyyy-MM-dd HH:mm:ss}Z\n";
        string clean = header + LogText.Scrubbed(text);
        Kept.Add((file, clean));
        if (folder.Length == 0) return "";
        try
        {
            string day = Path.Combine(Folder, utc.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(day);
            string path = Path.Combine(day, file);
            File.WriteAllBytes(path, LogText.Pack(clean));
            Prune(utc);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"could not keep {name}'s game log: {e.Message}");
            return null;
        }
    }

    /// <summary>Takes away every day older than <see cref="KeptDays"/>.</summary>
    public void Prune(DateTime utc)
    {
        if (folder.Length == 0 || !Directory.Exists(Folder)) return;
        foreach (string day in Directory.EnumerateDirectories(Folder))
        {
            if (DateTime.TryParseExact(Path.GetFileName(day), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime d)
                && (utc - d).TotalDays > KeptDays)
            {
                try { Directory.Delete(day, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"could not take away {day}: {e.Message}"); }
            }
        }
    }

    // A session's name comes from the game, and lands in a path.
    private static string Safe(string name)
    {
        StringBuilder b = new();
        foreach (char c in name.Take(48)) b.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return b.Length > 0 ? b.ToString() : "_";
    }
}
