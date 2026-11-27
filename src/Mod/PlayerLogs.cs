using System.Net;

namespace KSAMultiplayer;

/// <summary>
/// This game's logs, the mod's and KSA's own, handed to the developer's test server -- and to no
/// other server, whatever it says of itself. The game decides, by the address it joined: what
/// <see cref="Host"/> is, on <see cref="Port"/>. An operator of another server cannot change that;
/// only a build of the mod of their own could, and that is their players' own choice to run.
///
/// <para>What of each log the server does not have yet, and nothing it has: as the world is joined,
/// every <see cref="EverySeconds"/> after that, and whenever the server asks
/// (<see cref="Wire.LogsWanted"/>). The sessions before this one are where a crash or a mod that
/// switched itself off is written down, which the player cannot be asked for once it has been
/// written over; this one, for a player who never joins again. KSA's log as well as the mod's, since
/// KSA writes what it did to its own world -- a name registered twice, an exception in the editor --
/// and the mod's log cannot. Every file of a KSA session goes: one that did not end cleanly leaves
/// an <c>.abnormal-exit</c> summary of its last lines beside the full log.</para>
///
/// <para>How far each file has been sent is <see cref="SentPath"/>, written only once the server
/// has said it has the piece, so a piece lost on the way goes again. Pieces end with a line: the
/// cleaning (<see cref="LogText.Scrubbed"/>), which takes addresses and the user's name out, reads
/// whole lines, and half an address on each side of a cut is neither. A session over is sent to its
/// end, since a crash's last line may have no newline.</para>
/// </summary>
internal static class PlayerLogs
{
    private const string Host = "ksamp.gg";
    private const int Port = 50100;

    // The sessions before this one whose logs are looked at, newest first, and the most of a file
    // one piece carries: past it, its end, which is where a session went wrong.
    private const int Sessions = 3;
    private const int MostBytes = 8 * 1024 * 1024;

    private const double EverySeconds = 600;

    // A log nothing has written to in this long is a session over, whatever its process is doing.
    private static readonly TimeSpan Over = TimeSpan.FromMinutes(5);

    private static Task<IPAddress[]>? _resolving;
    private static bool? _isTheTestServer;
    private static Task<List<Piece>>? _reading;
    private static bool _joinSent;
    private static string? _askedWhy;
    private static DateTime _nextAt;
    private static readonly List<(uint Upload, Piece Piece)> Going = [];

    /// <param name="Name">The file's name as the next launch leaves it, without <c>.log</c>.</param>
    /// <param name="Bytes">How far into the file it reaches.</param>
    /// <param name="Lines">How many lines of the file it reaches past.</param>
    private sealed record Piece(string Name, string Key, byte[] Packed, long Bytes, int Lines);

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
            // Asked of a game on any other server, the ask is answered with nothing.
            if (_isTheTestServer != true)
            {
                c.LogsAsked = null;
                return;
            }
            Settle(c);
            // Nothing is read while a piece is on its way: read from where the server is known to
            // have got to, it would be sent again. An ask waits for that too.
            if (_reading is null && Going.Count == 0)
            {
                DateTime now = DateTime.UtcNow;
                if (c.LogsAsked is { } why)
                {
                    c.LogsAsked = null;
                    _askedWhy = why;
                }
                else if (_joinSent && now < _nextAt) return;
                _joinSent = true;
                _nextAt = now.AddSeconds(EverySeconds);
                _reading = Task.Run(Read);
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
            bool joining = !_saidJoined;
            _saidJoined = true;
            // Once a session, and only as it joins: read every ten minutes, it would say it every ten.
            if (joining && _noKsaLog is { } missing) Log.Info(missing);
            if (reading.Result.Count == 0) return;
            foreach (Piece piece in reading.Result)
            {
                if (c.Upload(new Blob(BlobKind.Log, piece.Key, c.Id, 0.0, piece.Packed)) is var id and > 0) Going.Add((id, piece));
            }
            // Only what a player has been told of is said in the log: one line every ten minutes would
            // be the next piece, and so on for as long as the game runs.
            if (joining)
            {
                Log.Info($"sending the test server this game's logs of {string.Join(", ", reading.Result.Select(r => r.Key))}");
                Sync.Say("this is the developer's test server, so your game has sent it its logs of your last sessions, with addresses and your user name taken out, to help find what went wrong");
            }
            else if (asked is not null)
            {
                Log.Info($"sending the test server this game's logs of {string.Join(", ", reading.Result.Select(r => r.Key))}, asked for: {asked}");
                Sync.Say($"the test server asked for your game's logs ({asked}), so it has sent them, with addresses and your user name taken out");
            }
        }
        catch (Exception e)
        {
            _reading = null;
            Log.Warn($"could not send the test server this game's logs: {e.GetBaseException().Message}");
        }
    }

    private static bool _saidJoined;

    // Written by the read, on its own thread, and said by the frame that takes up what it read.
    private static volatile string? _noKsaLog;

    // Pieces the server has said it has are written down; those given up on are let go, and go again.
    private static void Settle(Client c)
    {
        if (Going.Count == 0) return;
        List<Piece> arrived = [];
        for (int i = Going.Count - 1; i >= 0; i--)
        {
            (uint id, Piece piece) = Going[i];
            if (c.Arrived(id)) arrived.Add(piece);
            else if (c.Uploading(id)) continue;
            Going.RemoveAt(i);
        }
        if (arrived.Count > 0) Remember(arrived);
    }

    /// <summary>A new session with a server: looked at afresh on the next join, and what was on its way to the last one sent again.</summary>
    public static void Reset()
    {
        (_resolving, _isTheTestServer, _reading, _joinSent, _askedWhy, _saidJoined) = (null, null, null, false, null, false);
        Going.Clear();
    }

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

    private static List<Piece> Read()
    {
        List<Piece> found = [];
        if (Folder.Length == 0 || !Directory.Exists(Folder)) return found;
        Dictionary<string, (long Bytes, int Lines)> sent = Ledger();
        DirectoryInfo folder = new(Folder);
        DateTime now = DateTime.UtcNow;

        foreach (FileInfo f in folder.GetFiles("KSAMultiplayer.??????-??????.log").OrderByDescending(f => f.Name).Take(Sessions))
        {
            Add(f, Path.GetFileNameWithoutExtension(f.Name), mine: false);
        }
        // This session's, under the name the next launch gives it, so that what is sent of it now is
        // not sent again then.
        if (Log.Path is { } path && File.Exists(path) && Began(path) is { } began)
        {
            Add(new FileInfo(path), $"KSAMultiplayer.{began:yyMMdd-HHmmss}", mine: true);
        }
        FileInfo[] ksa = folder.GetFiles("KittenSpaceAgency.??????-??????.*.log");
        string own = $".{Environment.ProcessId}";
        string[] before = [.. ksa.Select(f => KsaSession(f.Name)).Where(s => !s.EndsWith(own, StringComparison.Ordinal))
                                 .Distinct().OrderByDescending(s => s, StringComparer.Ordinal).Take(Sessions)];
        foreach (FileInfo f in ksa)
        {
            string session = KsaSession(f.Name);
            bool mine = session.EndsWith(own, StringComparison.Ordinal);
            if (mine || before.Contains(session)) Add(f, Path.GetFileNameWithoutExtension(f.Name), mine);
        }
        // KSA's log of this very session is the one that says what KSA did to a craft, and on
        // ksamp.gg one player's never came while the sessions before it did. What was there instead
        // is what tells a log KSA had not written yet from one it wrote somewhere else.
        _noKsaLog = null;
        if (!ksa.Any(f => KsaSession(f.Name).EndsWith(own, StringComparison.Ordinal)))
        {
            string[] seen = [.. folder.GetFiles("KittenSpaceAgency*").OrderByDescending(f => f.LastWriteTimeUtc).Take(4)
                                      .Select(f => $"{f.Name} ({f.Length / 1024} KB)")];
            _noKsaLog = $"no KSA log of this session (process {Environment.ProcessId}) beside the mod's; the newest of KSA's there: {(seen.Length > 0 ? string.Join(", ", seen) : "none")}";
        }
        return found;

        void Add(FileInfo f, string name, bool mine)
        {
            (long from, int line) = sent.GetValueOrDefault(name);
            if (from == f.Length) return;
            using FileStream open = new(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bool over = !mine && now - f.LastWriteTimeUtc > Over;
            if (LogPieces.Next(open, from, line, over, MostBytes) is not { } piece) return;
            found.Add(new Piece(name, LogPieces.Key(name, piece.First), LogText.Pack(LogText.Scrubbed(piece.Text)), piece.Bytes, piece.Lines));
        }
    }

    // When the session of the mod's log being written began, as its first line says: what the next
    // launch names it by (Log.Keep).
    private static DateTime? Began(string path)
    {
        using FileStream open = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(open);
        return reader.ReadLine() is { } first && first.EndsWith(" began")
            && DateTime.TryParse(first[..^" began".Length], out DateTime said) ? said : null;
    }

    private static Dictionary<string, (long Bytes, int Lines)> Ledger() =>
        File.Exists(SentPath) ? LogPieces.Ledger(File.ReadAllLines(SentPath)) : [];

    // Written whole, read first: another game on this machine may have written to it since, and of
    // two reaches the further holds. Names whose file is gone are let go.
    private static void Remember(List<Piece> arrived)
    {
        try
        {
            Dictionary<string, (long Bytes, int Lines)> sent = Ledger();
            foreach (Piece p in arrived) LogPieces.Further(sent, p.Name, (p.Bytes, p.Lines));
            string mine = Log.Path is { } path && Began(path) is { } began ? $"KSAMultiplayer.{began:yyMMdd-HHmmss}" : "";
            string[] rows = [.. LogPieces.Rows(sent.Where(e => e.Key == mine || File.Exists(Path.Combine(Folder, e.Key + ".log"))))];
            string writing = SentPath + ".new";
            File.WriteAllLines(writing, rows);
            File.Move(writing, SentPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"could not write down which logs were sent: {e.Message}");
        }
    }
}
