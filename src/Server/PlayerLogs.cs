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

    /// <summary>The most logs one player's game is taken from in one session: it sends its last few at most.</summary>
    public const int MostPerSession = 5;

    public string Folder => Path.Combine(folder, "player-logs");

    /// <summary>What was kept since the server started, for the tests: player, session, text.</summary>
    public List<(string Player, string Session, string Text)> Kept { get; } = [];

    /// <summary>Keeps one log, and says where; null where it could not be written.</summary>
    public string? Keep(string player, string session, string text, DateTime utc)
    {
        string clean = LogText.Scrubbed(text);
        Kept.Add((player, session, clean));
        if (folder.Length == 0) return "";
        try
        {
            string day = Path.Combine(Folder, utc.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(day);
            string path = Path.Combine(day, $"{Safe(player)}.{Safe(session)}.{utc:HHmmss}.log.gz");
            File.WriteAllBytes(path, LogText.Pack(clean));
            Prune(utc);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"could not keep {player}'s game log: {e.Message}");
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

    // A player's name and a session's are theirs to choose, and land in a path.
    private static string Safe(string name)
    {
        StringBuilder b = new();
        foreach (char c in name.Take(48)) b.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return b.Length > 0 ? b.ToString() : "_";
    }
}
