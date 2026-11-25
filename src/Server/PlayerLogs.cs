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

    /// <summary>
    /// The most logs a game of 0.37.0 or before is taken from in one session, each sent whole:
    /// it sends its last three of the mod's and three of KSA's at most.
    /// </summary>
    public const int MostPerSession = 8;

    /// <summary>
    /// The most text one join is taken in pieces (<see cref="Piece"/>), every log's together: a game
    /// sends each line once, and a session's log has been 40 KB packed, so this is only ever reached
    /// by a game sending what no game writes.
    /// </summary>
    public const long MostCharsPerJoin = 64L * 1024 * 1024;

    /// <summary>One log a join is sending in pieces: where it is kept, and how many of its lines that holds.</summary>
    public sealed class Open(string path, string file, int lines)
    {
        public string Path { get; } = path;
        public string File { get; } = file;
        public int Lines { get; set; } = lines;
    }

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
        string file = FileFor(id, discord, session, utc);
        string clean = Header(name, id, discord, discordName, utc) + LogText.Scrubbed(text);
        Kept.Add((file, clean));
        if (folder.Length == 0) return "";
        try
        {
            string path = PathFor(file, utc);
            File.WriteAllBytes(path, LogText.Pack(clean));
            Prune(utc);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"could not keep {OneLine(name)}'s game log: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Keeps a piece of a log a join is sending a line at a time, after what this join already holds
    /// of it, in one file a join: a gzip member each, which <c>zcat</c> and <see cref="LogText.Unpack"/>
    /// read as one. Lines this join already has are left out, as a game whose ack was lost sends them
    /// again, and lines that never came are said where they would have been. Counted in lines, not
    /// bytes: what comes is cleaned here and comes out of it another length, and cleaning never makes
    /// or takes away a line.
    /// </summary>
    /// <param name="open">What this join is sending, by log; a join's own, so that no game can write into another's file.</param>
    /// <returns>The lines kept, none for a piece this join already had; null where it could not be written.</returns>
    public int? Append(string name, byte id, string discord, string discordName, string log, int first, string text, DateTime utc, Dictionary<string, Open> open)
    {
        string[] lines = Lines(LogText.Scrubbed(text));
        StringBuilder b = new();
        if (!open.TryGetValue(log, out Open? o))
        {
            b.Append(Header(name, id, discord, discordName, utc));
            if (first > 0) b.Append($"(from line {first + 1}: the lines before it were sent before this join)\n");
            string file = FileFor(id, discord, log, utc);
            o = new Open(folder.Length == 0 ? "" : PathFor(file, utc), file, first);
        }
        int skip = 0;
        if (first > o.Lines) b.Append($"(lines {o.Lines + 1} to {first} never arrived)\n");
        else skip = o.Lines - first;
        if (skip >= lines.Length && open.ContainsKey(log)) return 0;
        foreach (string line in lines.Skip(skip)) b.Append(line);
        string piece = b.ToString();
        try
        {
            if (o.Path.Length > 0)
            {
                using FileStream f = new(o.Path, FileMode.Append, FileAccess.Write, FileShare.Read);
                f.Write(LogText.Pack(piece));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"could not keep {OneLine(name)}'s game log: {e.Message}");
            return null;
        }
        bool opened = open.TryAdd(log, o);
        o.Lines = Math.Max(o.Lines, first + lines.Length);
        Kept.Add((o.File, piece));
        if (opened && o.Path.Length > 0) Prune(utc);
        return Math.Max(0, lines.Length - skip);
    }

    // Each line with its newline, and a last one without, as a session still being written or one
    // that ended mid-line has.
    private static string[] Lines(string text)
    {
        List<string> lines = [];
        int at = 0;
        while (at < text.Length)
        {
            int end = text.IndexOf('\n', at);
            end = end < 0 ? text.Length : end + 1;
            lines.Add(text[at..end]);
            at = end;
        }
        return [.. lines];
    }

    private static string FileFor(byte id, string discord, string session, DateTime utc) =>
        $"{(discord.Length > 0 ? $"discord-{Safe(discord)}" : "no-discord")}.player-{id}.{Safe(session)}.{utc:HHmmss}.log.gz";

    private string PathFor(string file, DateTime utc)
    {
        string day = Path.Combine(Folder, utc.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(day);
        return Path.Combine(day, file);
    }

    private static string Header(string name, byte id, string discord, string discordName, DateTime utc) =>
        $"player {id}, playing as {OneLine(name)}"
        + (discord.Length > 0 ? $", logged in with Discord as {discordName} ({discord})" : ", not logged in with Discord")
        + $"; sent {utc:yyyy-MM-dd HH:mm:ss}Z\n";

    // Whatever was typed for a name.
    private static string OneLine(string name) => string.Concat(name.Select(c => char.IsControl(c) ? ' ' : c));

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

    // A session's name comes from the game, and lands in a path. As long as a key can be: KSA's
    // own names are 48 already, and two logs cut to one name would be kept in one file.
    private static string Safe(string name)
    {
        StringBuilder b = new();
        foreach (char c in name.Take(Wire.KeyChars)) b.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return b.Length > 0 ? b.ToString() : "_";
    }
}
