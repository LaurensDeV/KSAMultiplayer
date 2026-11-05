using System.Text.Json;

namespace KSAMultiplayer;

/// <summary>
/// What each server gave this machine when it made this player an admin, kept so that being an admin
/// survives a restart with nothing to type. One token per server as it was typed in, since a token is
/// one server's and means nothing to another.
///
/// <para>Beside the panel's settings rather than in them: a token arrives while the game is running
/// and the settings are written when the game ends, and neither write should lose the other's.</para>
/// </summary>
internal static class Tokens
{
    private static string Path => System.IO.Path.Combine(KSA.Constants.DocumentsFolderPath, "KSAMultiplayer", "tokens.json");

    private static Dictionary<string, string>? _tokens;

    public static string For(string server) => Read().GetValueOrDefault(Key(server), "");

    /// <summary>Writes a token down for a server, or throws away the one kept for it when given none.</summary>
    public static void Keep(string server, string token)
    {
        Dictionary<string, string> all = Read();
        if (token.Length > 0) all[Key(server)] = token;
        else if (!all.Remove(Key(server))) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(all));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"could not write {Path}: {e.Message}; this admin lasts until the game is closed");
        }
    }

    // The address as it was typed, bar case and the spaces around it: one server reached two ways is
    // two here, which costs an operator one op and is better than two servers sharing an entry.
    private static string Key(string server) => server.Trim().ToLowerInvariant();

    private static Dictionary<string, string> Read()
    {
        if (_tokens is not null) return _tokens;
        try
        {
            if (File.Exists(Path))
            {
                _tokens = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path));
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"could not read {Path}: {e.Message}; this game is an admin nowhere until it is opped again");
        }
        return _tokens ??= [];
    }
}
