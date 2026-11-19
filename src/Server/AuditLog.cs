namespace KSAMultiplayer.Server;

/// <summary>
/// Who did what that changes the world, a save or who may do what: one line each, appended to
/// <c>audit.log</c> beside the world. A file in the data folder rather than the console's log,
/// which a container's redeploy throws away with the container: the question this answers --
/// who cleared the station, who deleted that save, who made them an admin -- is asked days later.
///
/// <para>With no folder the lines are kept in memory only, for the tests and a server given no data
/// directory.</para>
/// </summary>
public sealed class AuditLog(string folder = "")
{
    private readonly List<string> _kept = [];

    public string Path => System.IO.Path.Combine(folder, "audit.log");

    /// <summary>What has been written since the server started, oldest first.</summary>
    public IReadOnlyList<string> Lines => _kept;

    /// <param name="who">Who asked: "console", or a player as <see cref="Who"/> says them.</param>
    /// <param name="what">What they asked for, as typed.</param>
    /// <param name="answer">What came of it, as they were told.</param>
    public void Write(string who, string what, string answer)
    {
        string line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z  {who}  {Flat(what)}  ->  {Flat(answer)}";
        _kept.Add(line);
        if (folder.Length == 0) return;
        try
        {
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path, line + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"could not write {Path}: {e.Message}");
        }
    }

    /// <summary>A player as the log names them: the name they played under, what they were, and their Discord user where they had logged in.</summary>
    public static string Who(GameServer.Player p) =>
        $"{p.Name} ({Permissions.Said(p.Role)}{(p.DiscordId.Length > 0 ? $", Discord {p.DiscordId}" : "")})";

    // One line a thing, whatever a name or an answer had in it.
    private static string Flat(string s) => s.Replace('\r', ' ').Replace('\n', ' ');
}
