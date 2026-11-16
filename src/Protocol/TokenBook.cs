using System.Net;
using System.Text.Json;

namespace KSAMultiplayer;

/// <summary>
/// The tokens servers have handed this machine, by where each server was found: what makes a player
/// an admin again with nothing to type. Nothing of KSA is here, so a test can keep one in a folder
/// of its own.
///
/// <para>A token is filed twice: under the address as it was typed, and under the address the
/// datagrams went to. The typed one alone lost every admin their place the day a server took a new
/// name, and loses it between "host" and "host:port", which a list of servers moves between on the
/// strength of an SRV record. Looked up, the typed address comes first, and the other answers only
/// when nothing is filed under what was typed.</para>
///
/// <para>Never under anything a server says of itself -- its name, its id, what its operator calls
/// it. A token is a bearer secret, offered in the first datagram of a join to whatever is at the far
/// end, so a key the far end chooses is a way to ask for somebody else's: a server saying it is
/// another would be handed that one's admin. Where the datagrams go is the one thing here a server
/// cannot say for itself. That is no stronger than the network: an address that has passed to
/// another machine is offered the token once, exactly as a name that has passed to another machine
/// already was, and a token that has got out is a <c>deop</c> away from worthless.</para>
/// </summary>
/// <param name="path">The file they are kept in.</param>
/// <param name="warn">Told when the file cannot be read or written.</param>
public sealed class TokenBook(string path, Action<string>? warn = null)
{
    private Dictionary<string, string>? _tokens;

    // The server being joined, and what this machine holds for it: what a refusal throws away, and
    // nothing else that happens to be filed nearby.
    private string _typed = "", _at = "", _held = "";

    /// <summary>
    /// The token to offer a server about to be joined, or empty. Everything said afterwards is said
    /// of this server, until the next one.
    /// </summary>
    /// <param name="typed">Its address as the player gave it.</param>
    /// <param name="at">Where that address led.</param>
    public string Joining(string typed, IPEndPoint at)
    {
        (_typed, _at) = (Key(typed), Key(at.ToString()));
        Dictionary<string, string> all = Read();
        return _held = all.TryGetValue(_typed, out string? named) ? named : all.GetValueOrDefault(_at, "");
    }

    /// <summary>
    /// Files a token under both of the joined server's addresses, and says whether it is on disk:
    /// one this server handed over, or the one it was offered and took, which may be filed under
    /// only one of them.
    /// </summary>
    public bool Keep(string token)
    {
        if (token.Length == 0 || _at.Length == 0) return false;
        Dictionary<string, string> all = Read();
        _held = token;
        bool filed = all.GetValueOrDefault(_typed) == token && all.GetValueOrDefault(_at) == token;
        (all[_typed], all[_at]) = (token, token);
        return (filed && _written) || Write(all);
    }

    /// <summary>
    /// Throws away what the joined server will no longer take. Only where it is filed under that
    /// server's two addresses, and only the token it refused: the same address may hold another
    /// server's under another name, and what this server says is no reason to lose that one.
    /// </summary>
    public void Forget()
    {
        if (_held.Length == 0) return;
        Dictionary<string, string> all = Read();
        bool gone = false;
        foreach (string key in new[] { _typed, _at })
        {
            if (all.GetValueOrDefault(key) == _held) gone |= all.Remove(key);
        }
        _held = "";
        if (gone) Write(all);
    }

    // Bar case and the spaces around it. An address typed as numbers with its port is the same key
    // as where it led, which is the same server.
    private static string Key(string address) => address.Trim().ToLowerInvariant();

    // Whether what is in memory is what is on disk: a token that could not be written is not one
    // to tell a server has been kept.
    private bool _written = true;

    private bool Write(Dictionary<string, string> all)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
            // Written beside and moved over, so a game killed mid-write keeps the tokens it had.
            File.WriteAllText(path + ".new", JsonSerializer.Serialize(all));
            File.Move(path + ".new", path, overwrite: true);
            return _written = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"could not write {path}: {e.Message}; this admin lasts until the game is closed");
            return _written = false;
        }
    }

    private Dictionary<string, string> Read()
    {
        if (_tokens is not null) return _tokens;
        try
        {
            if (File.Exists(path)) _tokens = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            warn?.Invoke($"could not read {path}: {e.Message}; this game is an admin nowhere until it is opped again");
        }
        return _tokens ??= [];
    }
}
