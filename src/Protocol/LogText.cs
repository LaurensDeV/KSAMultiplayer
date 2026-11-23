using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace KSAMultiplayer;

/// <summary>
/// The server's log as it travels to an operator's game: gzipped text, with every address taken out.
/// An operator is told who came and went and what went wrong, which is what "something happened"
/// asks; where each player connects from stays on the server's console, as it does for <c>players</c>.
/// </summary>
public static class LogText
{
    /// <summary>The most days of log one asking sends: as many as a server keeps.</summary>
    public const int MostDays = 14;

    /// <summary>
    /// The most text one asking sends, the newest kept. A busy day has been a third of a megabyte, so
    /// this is far past anything real, and bounds what a game unpacks from a server it cannot trust.
    /// </summary>
    public const int MostBytes = 32 * 1024 * 1024;

    // An endpoint as IPEndPoint prints one: 1.2.3.4:5, [::1]:5, or either without the port. The
    // lookbehind and lookahead keep a dotted KSA build such as 2026.11.16.5901 from reading as one;
    // a full stop after one, as in "looking up 1.2.3.4...", is not more of it.
    private static readonly Regex Address = new(
        @"(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?(?!\w|\.\d)|\[[0-9A-Fa-f:.%]+\](?::\d{1,5})?",
        RegexOptions.CultureInvariant);

    /// <summary>The text with every address in it said as "(address)".</summary>
    public static string Unaddressed(string text) => Address.Replace(text, "(address)");

    // A user's folder, which carries their name: C:\Users\name, /Users/name, /home/name, and the
    // same under WSL's /mnt/c. Their own name, often their real one, and nothing a log needs.
    private static readonly Regex UserFolder = new(
        @"(?i)(?<=[\\/](?:Users|home)[\\/])[^\\/\r\n:*?""<>|]+",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// A game's log fit to leave the machine it was written on: every address taken out, as for
    /// <see cref="Unaddressed"/>, and the user's name in every folder path.
    /// </summary>
    public static string Scrubbed(string text) => UserFolder.Replace(Unaddressed(text), "(user)");

    public static byte[] Pack(string text)
    {
        using MemoryStream m = new();
        using (GZipStream z = new(m, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            z.Write(bytes, 0, bytes.Length);
        }
        return m.ToArray();
    }

    /// <summary>The text, or null for what is not gzip or would unpack past <see cref="MostBytes"/>.</summary>
    public static string? Unpack(byte[] data)
    {
        try
        {
            using GZipStream z = new(new MemoryStream(data), CompressionMode.Decompress);
            using MemoryStream m = new();
            byte[] buffer = new byte[81920];
            int read;
            while ((read = z.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (m.Length + read > MostBytes) return null;
                m.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(m.GetBuffer(), 0, (int)m.Length);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
