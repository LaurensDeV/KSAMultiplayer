using System.Text;

namespace KSAMultiplayer;

/// <summary>
/// A game's log sent a piece at a time, each line once: what of a file is still to go, and the
/// game's own record of how far each file has gone. Here rather than in the mod so that the tests
/// can drive it; the mod compiles it in.
/// </summary>
public static class LogPieces
{
    /// <summary>A piece: its text, and how far into the file, in bytes and in lines, it reaches.</summary>
    public readonly record struct Piece(string Text, int First, long Bytes, int Lines);

    /// <summary>How far a file has been sent, for one sent whole by a game of 0.37.0 or before.</summary>
    public const long Whole = long.MaxValue;

    /// <summary>
    /// What of a log is still to go after <paramref name="from"/> bytes and <paramref name="line"/>
    /// lines of it have gone, or null for nothing. A file still being written goes to its last whole
    /// line, since what cleans a log (<see cref="LogText.Scrubbed"/>) reads whole lines and half an
    /// address on each side of a cut is neither; one that is <paramref name="over"/> goes to its end,
    /// since a crash's last line may have no newline. Past <paramref name="most"/> bytes, the end of
    /// it from a line, the lines passed over counted so that where they would have been is known.
    /// </summary>
    public static Piece? Next(Stream file, long from, int line, bool over, int most)
    {
        if (from == Whole) return null;
        long end = file.Length;
        // A file shorter than what was sent of it is another under the same name.
        if (from > end) (from, line) = (0, 0);
        if (from == end) return null;
        if (end - from > most)
        {
            long skipTo = end - most;
            file.Position = from;
            line += Newlines(file, skipTo - from, out long last, first: false);
            if (last >= 0) from = last + 1;
            else
            {
                // One line longer than all of it so far: from past its end.
                line += Newlines(file, end - skipTo, out long next, first: true);
                if (next < 0) return null;
                from = next + 1;
            }
        }
        file.Position = from;
        byte[] bytes = new byte[end - from];
        file.ReadExactly(bytes);
        int length = over ? bytes.Length : Array.LastIndexOf(bytes, (byte)'\n') + 1;
        if (length == 0) return null;
        int bom = from == 0 && length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        string text = Encoding.UTF8.GetString(bytes, bom, length - bom);
        return new Piece(text, line, from + length, line + LineCount(text));
    }

    /// <summary>The lines in a piece: each newline, and a last line without one.</summary>
    public static int LineCount(string text) => text.Count(c => c == '\n') + (text.Length > 0 && text[^1] != '\n' ? 1 : 0);

    /// <summary>A piece's key: the log's name, a colon, and the lines of it before the piece.</summary>
    public static string Key(string name, int first) => $"{name}:{first}";

    /// <summary>
    /// A piece's key read back; null for a key of a log sent whole, as games of 0.37.0 and
    /// before send them.
    /// </summary>
    public static (string Log, int First)? Parse(string key)
    {
        int colon = key.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(key.AsSpan(colon + 1), System.Globalization.NumberStyles.None, null, out int first)) return null;
        return (key[..colon], first);
    }

    /// <summary>
    /// How far each file has been sent, from its rows: a name, its bytes and its lines, by tabs, or a
    /// name alone for one sent whole. Of two rows for a name the further holds, as two games on one
    /// machine may each have written one.
    /// </summary>
    public static Dictionary<string, (long Bytes, int Lines)> Ledger(IEnumerable<string> rows)
    {
        Dictionary<string, (long, int)> sent = [];
        foreach (string row in rows)
        {
            string[] parts = row.Split('\t');
            if (parts[0].Length == 0) continue;
            (long Bytes, int Lines) reach = parts.Length == 3 && long.TryParse(parts[1], out long bytes) && int.TryParse(parts[2], out int lines)
                ? (bytes, lines)
                : (Whole, 0);
            Further(sent, parts[0], reach);
        }
        return sent;
    }

    /// <summary>Writes down that a file has been sent this far, unless it is known to have gone further.</summary>
    public static void Further(Dictionary<string, (long Bytes, int Lines)> sent, string name, (long Bytes, int Lines) reach)
    {
        if (!sent.TryGetValue(name, out (long Bytes, int Lines) had) || had.Bytes < reach.Bytes) sent[name] = reach;
    }

    /// <summary>The rows of a ledger, as <see cref="Ledger"/> reads them.</summary>
    public static IEnumerable<string> Rows(IEnumerable<KeyValuePair<string, (long Bytes, int Lines)>> sent) =>
        sent.Select(e => e.Value.Bytes == Whole ? e.Key : $"{e.Key}\t{e.Value.Bytes}\t{e.Value.Lines}");

    // The newlines in the next count bytes of the stream, and where the last of them was, or with
    // first where the first was, counting no further; -1 for none.
    private static int Newlines(Stream s, long count, out long at, bool first)
    {
        at = -1;
        int found = 0;
        byte[] buffer = new byte[81920];
        long start = s.Position, read = 0;
        while (read < count)
        {
            int n = s.Read(buffer, 0, (int)Math.Min(buffer.Length, count - read));
            if (n <= 0) break;
            for (int i = 0; i < n; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                found++;
                at = start + read + i;
                if (first) return found;
            }
            read += n;
        }
        return found;
    }
}
