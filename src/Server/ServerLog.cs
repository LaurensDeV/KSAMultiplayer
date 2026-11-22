using System.Text;

namespace KSAMultiplayer.Server;

/// <summary>
/// Everything the console says, kept a file a day in <c>logs</c> beside the world. A container's own
/// log is the host's to read and an operator is not on the host, and a redeploy throws a container's
/// log away with it; the world's folder is a volume, which outlives both. The newest
/// <see cref="LogText.MostDays"/> days are kept and older ones deleted as a day begins.
///
/// <para>Named by the day the lines are stamped with, which is the server's local time, so a file
/// holds the lines that say its date.</para>
/// </summary>
public sealed class ServerLog(string folder)
{
    public string Folder => folder;

    private string _day = "";
    private bool _failed;

    public void Write(DateTime local, string line)
    {
        string day = local.ToString("yyyy-MM-dd");
        try
        {
            if (day != _day)
            {
                Directory.CreateDirectory(folder);
                _day = day;
                Prune();
            }
            File.AppendAllText(Path.Combine(folder, day + ".log"), line + Environment.NewLine);
            _failed = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Once, not at every line: the console is where this would go, and it is what failed to be kept.
            if (!_failed) Console.Error.WriteLine($"could not write the log in {folder}: {e.Message}");
            _failed = true;
            _day = "";
        }
    }

    /// <summary>The days kept, oldest first, as their files are named.</summary>
    public string[] Days() =>
        Directory.Exists(folder)
            ? [.. Directory.GetFiles(folder, "????-??-??.log").Select(f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.Ordinal)]
            : [];

    /// <summary>
    /// The newest <paramref name="days"/> days, oldest first, each under a line naming it, cut to the
    /// newest quarter of <see cref="LogText.MostBytes"/> in characters, which UTF-8 cannot make more than that in bytes. Read shared, because this is still writing today's.
    /// </summary>
    public string Read(int days)
    {
        StringBuilder text = new();
        foreach (string day in Days().TakeLast(Math.Clamp(days, 1, LogText.MostDays)))
        {
            text.Append("==== ").Append(day).AppendLine(" ====");
            try
            {
                using FileStream f = new(Path.Combine(folder, day + ".log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using StreamReader r = new(f);
                text.Append(r.ReadToEnd());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                text.AppendLine($"(could not be read: {e.Message})");
            }
        }
        return text.Length <= LogText.MostBytes / 4 ? text.ToString() : "(older lines cut)\n" + text.ToString(text.Length - (LogText.MostBytes / 4), LogText.MostBytes / 4);
    }

    // Before the day's first line: room is made for it, among the days kept, whether or not it is there yet.
    private void Prune()
    {
        foreach (string day in Days().Where(d => d != _day).SkipLast(LogText.MostDays - 1))
        {
            try { File.Delete(Path.Combine(folder, day + ".log")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
