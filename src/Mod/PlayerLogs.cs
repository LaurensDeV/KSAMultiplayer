using System.Net;

namespace KSAMultiplayer;

/// <summary>
/// This game's logs, the mod's and KSA's own, handed to the developer's test server -- and to no
/// other server, whatever it says of itself. The game decides, by the address it joined: what
/// <see cref="Host"/> is, on <see cref="Port"/>. An operator of another server cannot change that;
/// only a build of the mod of their own could, and that is their players' own choice to run.
///
/// <para>As the world is joined, the sessions before this one: where a crash or a mod that switched
/// itself off is written down, and which the player cannot be asked for once it has been written
/// over. Each is sent once. KSA's log as well as the mod's, since KSA writes what it did to its own
/// world -- a name registered twice, an exception in the editor -- and the mod's log cannot. Every
/// file of a KSA session goes together: one that did not end cleanly leaves an
/// <c>.abnormal-exit</c> summary of its last lines beside the full log.</para>
///
/// <para>And whenever the server asks (<see cref="Wire.LogsWanted"/>), this session as it stands,
/// for something going wrong now. Everything leaves the machine cleaned of addresses and of the
/// user's name in its paths (<see cref="LogText.Scrubbed"/>).</para>
/// </summary>
internal static class PlayerLogs
{
    private const string Host = "ksamp.gg";
    private const int Port = 50100;

    // The sessions before this one that are sent of each log, newest first, and the most of a file.
    private const int Sessions = 3;
    private const int MostChars = 8 * 1024 * 1024;

    private static Task<IPAddress[]>? _resolving;
    private static bool? _isTheTestServer;
    private static Task<List<(string Name, byte[] Packed, bool Once)>>? _reading;
    private static bool _joinSent;
    private static string? _askedWhy;

    /// <summary>Once a frame while in a shared world.</summary>
    public static void Update(Client c)
    {
        if (!c.Joined || c.Server is not { } at) return;
        try
        {
            if (_isTheTestServer is null)
            {
                (string host, int port) = Target();
                _resolving ??= Dns.GetHostAddressesAsync(host);
                if (!_resolving.IsCompleted) return;
                _isTheTestServer = _resolving.IsCompletedSuccessfully && at.Port == port && _resolving.Result.Any(a => Same(a, at.Address));
            }
            // Asked of a game on any other server, the ask is answered with nothing; asked while a
            // send is being read, it waits for that one.
            if (_isTheTestServer != true)
            {
                c.LogsAsked = null;
                return;
            }
            if (c.LogsAsked is { } why && _reading is null)
            {
                c.LogsAsked = null;
                _askedWhy = why;
                _reading = Task.Run(() => Read(now: true));
                return;
            }
            if (!_joinSent && _reading is null)
            {
                _joinSent = true;
                _reading = Task.Run(() => Read(now: false));
                return;
            }
            if (_reading is not { IsCompleted: true } reading) return;
            _reading = null;
            string? asked = _askedWhy;
            _askedWhy = null;
            if (!reading.IsCompletedSuccessfully)
            {
                Log.Warn($"could not read this game's logs for the test server: {reading.Exception?.GetBaseException().Message}");
                return;
            }
            if (reading.Result.Count == 0) return;
            foreach ((string name, byte[] packed, _) in reading.Result) c.Upload(new Blob(BlobKind.Log, name, c.Id, 0.0, packed));
            Remember(reading.Result.Where(r => r.Once).Select(r => r.Name));
            Log.Info($"sent the test server this game's logs of {string.Join(", ", reading.Result.Select(r => r.Name))}");
            Sync.Say(asked is null
                ? "this is the developer's test server, so your game has sent it its logs of your last sessions, with addresses and your user name taken out, to help find what went wrong"
                : $"the test server asked for your game's logs ({asked}), so it has sent them, with addresses and your user name taken out");
        }
        catch (Exception e)
        {
            _reading = null;
            Log.Warn($"could not send the test server this game's logs: {e.GetBaseException().Message}");
        }
    }

    /// <summary>A new session with a server: looked at afresh on the next join.</summary>
    public static void Reset() => (_resolving, _isTheTestServer, _reading, _joinSent, _askedWhy) = (null, null, null, false, null);

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

    // A KSA session's files share their first three parts: KittenSpaceAgency, when it began, and
    // the process it ran in.
    private static string KsaSession(string name) => string.Join('.', name.Split('.').Take(3));

    /// <param name="now">Whether this session is wanted too, as it stands: asked for by the server.</param>
    private static List<(string, byte[], bool)> Read(bool now)
    {
        List<(string, byte[], bool)> found = [];
        if (Folder.Length == 0 || !Directory.Exists(Folder)) return found;
        HashSet<string> sent = File.Exists(SentPath) ? [.. File.ReadAllLines(SentPath)] : [];
        DirectoryInfo folder = new(Folder);

        foreach (FileInfo f in folder.GetFiles("KSAMultiplayer.??????-??????.log").OrderByDescending(f => f.Name).Take(Sessions))
        {
            Add(f, Path.GetFileNameWithoutExtension(f.Name), once: true);
        }
        FileInfo[] ksa = folder.GetFiles("KittenSpaceAgency.??????-??????.*.log");
        string[] before = [.. ksa.Select(f => KsaSession(f.Name)).Where(s => !s.EndsWith($".{Environment.ProcessId}", StringComparison.Ordinal))
                                 .Distinct().OrderByDescending(s => s, StringComparer.Ordinal).Take(Sessions)];
        foreach (FileInfo f in ksa.Where(f => before.Contains(KsaSession(f.Name))))
        {
            Add(f, Path.GetFileNameWithoutExtension(f.Name), once: true);
        }
        if (now)
        {
            // As they stand, under the time they were read: asked for again later, they are longer.
            string at = DateTime.Now.ToString("HHmmss");
            if (Log.Path is { } mine && File.Exists(mine)) Add(new FileInfo(mine), $"KSAMultiplayer.now-{at}", once: false);
            foreach (FileInfo f in ksa.Where(f => KsaSession(f.Name).EndsWith($".{Environment.ProcessId}", StringComparison.Ordinal)))
            {
                Add(f, $"{Path.GetFileNameWithoutExtension(f.Name)}.now-{at}", once: false);
            }
        }
        return found;

        void Add(FileInfo f, string name, bool once)
        {
            if (once && sent.Contains(name)) return;
            using FileStream open = new(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(open);
            string text = reader.ReadToEnd();
            // The end, where a session went wrong, if there is too much of it.
            if (text.Length > MostChars) text = text[^MostChars..];
            found.Add((name, LogText.Pack(LogText.Scrubbed(text)), once));
        }
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
