using System.Net;

namespace KSAMultiplayer;

/// <summary>
/// This game's logs of its last few sessions, handed to the developer's test server -- and to no
/// other server, whatever it says of itself. The game decides, by the address it joined: what
/// <see cref="Host"/> is, on <see cref="Port"/>. An operator of another server cannot change that;
/// only a build of the mod of their own could, and that is their players' own choice to run.
///
/// <para>Sent once a session, as the world is joined: the sessions before this one, which is where a
/// crash or a mod that switched itself off is written down, and which the player cannot be asked
/// for once it has been written over. Each leaves the machine cleaned of addresses and of the
/// user's name in its paths (<see cref="LogText.Scrubbed"/>), and is sent once.</para>
/// </summary>
internal static class PlayerLogs
{
    private const string Host = "ksamp.gg";
    private const int Port = 50100;

    // The sessions before this one that are sent, newest first, and the most of each.
    private const int Sessions = 3;
    private const int MostChars = 8 * 1024 * 1024;

    private static Task<IPAddress[]>? _resolving;
    private static Task<List<(string Name, byte[] Packed)>>? _reading;
    private static bool _done;

    /// <summary>Once a frame while in a shared world.</summary>
    public static void Update(Client c)
    {
        if (_done || !c.Joined || c.Server is not { } at) return;
        try
        {
            (string host, int port) = Target();
            _resolving ??= Dns.GetHostAddressesAsync(host);
            if (!_resolving.IsCompleted) return;
            if (_reading is null)
            {
                if (!_resolving.IsCompletedSuccessfully || at.Port != port || !_resolving.Result.Any(a => Same(a, at.Address)))
                {
                    _done = true;
                    return;
                }
                _reading = Task.Run(Read);
                return;
            }
            if (!_reading.IsCompleted) return;
            _done = true;
            if (!_reading.IsCompletedSuccessfully || _reading.Result.Count == 0) return;
            foreach ((string name, byte[] packed) in _reading.Result) c.Upload(new Blob(BlobKind.Log, name, c.Id, 0.0, packed));
            Remember(_reading.Result.Select(r => r.Name));
            Log.Info($"sent the test server this game's logs of {string.Join(", ", _reading.Result.Select(r => r.Name))}");
            Sync.Say("this is the developer's test server, so your game has sent it its logs of your last sessions, with addresses and your user name taken out, to help find what went wrong");
        }
        catch (Exception e)
        {
            _done = true;
            Log.Warn($"could not send the test server this game's logs: {e.GetBaseException().Message}");
        }
    }

    /// <summary>A new session with a server: asked again on the next join.</summary>
    public static void Reset() => (_resolving, _reading, _done) = (null, null, false);

    // KSAMP_LOGS_TO=host:port points this at another server, for trying it with two games on one
    // machine. A setting on the player's own machine, so no server can make a game send it anything.
    private static (string Host, int Port) Target()
    {
        string said = Environment.GetEnvironmentVariable("KSAMP_LOGS_TO") ?? "";
        int colon = said.LastIndexOf(':');
        return colon > 0 && int.TryParse(said[(colon + 1)..], out int port) ? (said[..colon], port) : (Host, Port);
    }

    private static bool Same(IPAddress a, IPAddress b) =>
        (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Equals(b.IsIPv4MappedToIPv6 ? b.MapToIPv4() : b);

    private static string Folder => Path.GetDirectoryName(Log.Path) ?? "";
    private static string SentPath => Path.Combine(Folder, "KSAMultiplayer.sent");

    private static List<(string, byte[])> Read()
    {
        List<(string, byte[])> found = [];
        if (Folder.Length == 0 || !Directory.Exists(Folder)) return found;
        HashSet<string> sent = File.Exists(SentPath) ? [.. File.ReadAllLines(SentPath)] : [];
        foreach (FileInfo f in new DirectoryInfo(Folder).GetFiles("KSAMultiplayer.??????-??????.log")
                                                       .OrderByDescending(f => f.Name).Take(Sessions))
        {
            string name = Path.GetFileNameWithoutExtension(f.Name);
            if (sent.Contains(name)) continue;
            using FileStream open = new(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(open);
            string text = reader.ReadToEnd();
            // The end, where a session went wrong, if there is too much of it.
            if (text.Length > MostChars) text = text[^MostChars..];
            found.Add((name, LogText.Pack(LogText.Scrubbed(text))));
        }
        return found;
    }

    private static void Remember(IEnumerable<string> names)
    {
        try
        {
            File.AppendAllLines(SentPath, names);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"could not write down which logs were sent: {e.Message}");
        }
    }
}
