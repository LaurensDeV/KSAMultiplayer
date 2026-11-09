using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace KSAMultiplayer;

/// <summary>
/// The list of servers a player picks from: the addresses the directories carry, asked about by this
/// game directly, and held to what this game could actually join.
///
/// <para>Nothing a directory says about a server is shown once this game has asked the server
/// itself. The directory's account decides what to show while the answers are still coming and what
/// not to bother asking about, and is written over within the second. A server's account of itself
/// is unchecked and so is free to be wrong: an address that never worked, a name left at its
/// default, a player count from an hour ago. A list built out of such accounts is mostly rows
/// nobody can join.</para>
///
/// <para>Never on the connect path. A player who has an address types it and presses CONNECT, and
/// nothing here can stop them -- not a directory that is down, not one that is slow, not one that
/// has never heard of the server they are joining.</para>
/// </summary>
internal static class Browser
{
    /// <summary>How long a list is used before it is worth fetching again, s.</summary>
    private const double FreshForSeconds = 60.0;

    /// <summary>One server, as the list shows it.</summary>
    internal sealed class Row
    {
        public required string Host { get; init; }
        public required int Port { get; init; }

        /// <summary>The directories carrying it, by name, so a player can see which one to switch off.</summary>
        public List<string> Found { get; } = [];

        /// <summary>What the directory last saw of it, shown until this game has asked for itself.</summary>
        public Listed Hint { get; set; } = null!;

        /// <summary>What it said to this game. Null until it has said anything.</summary>
        public ServerFacts? Facts { get; set; }

        /// <summary>Where it answered from, which is how two rows that are secretly one server are found out.</summary>
        public IPEndPoint? At { get; set; }

        /// <summary>Why it cannot be reached or joined, empty while it can.</summary>
        public string Trouble { get; set; } = "";

        public bool Waiting => Facts is null && Trouble.Length == 0;

        public string Where => Port == Wire.DefaultPort ? Host : $"{Host}:{Port}";

        /// <summary>The name to show: its own, else what a directory called it, else its address.</summary>
        public string Title =>
            Facts is { ServerName.Length: > 0 } mine ? mine.ServerName
            : Hint is { Name.Length: > 0 } said ? said.Name
            : Where;
    }

    private static readonly List<Row> Listed = [];
    private static Probes? _probes;
    private static Task<Gathered>? _fetching;
    private static readonly Dictionary<IPEndPoint, List<Row>> Asked = [];
    private static double _fetchedAt = double.NegativeInfinity;

    /// <summary>What a fetch of every directory came to.</summary>
    private sealed record Gathered(int Asked, string[] Names, List<(string Name, Listed Server)> Servers);

    /// <summary>Where the player's own choice of directories lives, beside the settings and the tokens.</summary>
    private static string MinePath => Path.Combine(KSA.Constants.DocumentsFolderPath, "KSAMultiplayer", "directories.json");

    private static string CachePath => Path.Combine(KSA.Constants.DocumentsFolderPath, "KSAMultiplayer", "directories-seen.txt");

    /// <param name="Use">Directories to use instead of the ones everybody else uses. Empty to take those and go on from them.</param>
    /// <param name="Add">Directories to use as well, which is how a group shares a list of its own servers.</param>
    /// <param name="Remove">Directories not to use, which is how a player who does not trust one stops seeing it without waiting for anybody.</param>
    private sealed record Picked(string[]? Use = null, string[]? Add = null, string[]? Remove = null);

    /// <summary>What the player's own remembered servers are called in a row's <see cref="Row.Found"/>.</summary>
    public const string Yours = "yours";

    /// <summary>
    /// Addresses this player has joined before, asked about and shown like any other row.
    ///
    /// <para>Rows rather than a dropdown, because the useful thing about a server you have played on
    /// is whether anybody is on it now, and a list of addresses cannot say.</para>
    /// </summary>
    public static string[] Remembered { get; set; } = [];

    /// <summary>Whether a list has been asked for at all yet, so a window knows to ask for one.</summary>
    public static bool Started { get; private set; }

    /// <summary>How many directories were asked, and how many answered. A list that is short because nothing answered must say so.</summary>
    public static (int Asked, int Answered) Directories { get; private set; }

    public static bool Fetching => _fetching is { IsCompleted: false };

    /// <summary>The rows, with what a player is most likely to want at the top: servers with players on them, then ones they can join.</summary>
    public static IReadOnlyList<Row> Rows => Listed;

    public static int Joinable => Listed.Count(r => r.Facts is not null && Why(r).Length == 0);

    /// <summary>Fetches the directories again, unless one is already being fetched or the last one is still fresh.</summary>
    public static void Refresh(bool anyway = false)
    {
        if (Fetching) return;
        double now = Sync.Wall();
        if (!anyway && now - _fetchedAt < FreshForSeconds) return;
        Started = true;
        _fetchedAt = now;
        _fetching = Task.Run(Gather);
    }

    /// <summary>
    /// Drained once a frame by whoever draws the window. Everything here happens on the game's own
    /// thread; the fetching and the resolving are tasks that are only ever looked at for having
    /// finished.
    /// </summary>
    public static void Update()
    {
        Resolved();
        if (_fetching is { IsCompleted: true } done)
        {
            _fetching = null;
            try
            {
                Take(done.Result);
            }
            catch (Exception e)
            {
                Log.Warn($"could not read the server directories: {e.Message}");
            }
        }
        if (_probes is null) return;
        _probes.Update();
        foreach ((IPEndPoint at, List<Row> rows) in Asked.ToArray())
        {
            if (_probes.Answered.TryGetValue(at, out ServerFacts? facts))
            {
                foreach (Row r in rows) (r.Facts, r.At, r.Trouble) = (facts, at, "");
            }
            else if (_probes.Elsewhere.TryGetValue(at, out ushort version))
            {
                foreach (Row r in rows) r.Trouble = $"speaks protocol {version}, this game speaks {Wire.Version}";
            }
            else if (_probes.Silent.Contains(at))
            {
                foreach (Row r in rows) r.Trouble = "no answer from here";
            }
            else continue;
            Asked.Remove(at);
            Collapse();
        }
    }

    /// <summary>
    /// Why a row cannot be joined, in words a player can act on, or empty when it can.
    ///
    /// <para>The protocol version, the KSA build and the mod set all have to match before a join
    /// works, so most of any list is unjoinable to any one game. A row that will not say which of
    /// the three it failed leaves a player clicking through rows to learn one at a time which of
    /// them were never for them.</para>
    /// </summary>
    public static string Why(Row r)
    {
        if (r.Facts is not { } f) return r.Trouble;
        if (f.KsaBuild.Length > 0 && World.KsaBuild.Length > 0 && f.KsaBuild != World.KsaBuild) return $"needs KSA {f.KsaBuild}";
        (string missing, _) = Sync.ModsAgainst(f.Mods);
        if (missing.Length > 0) return $"needs {missing}";
        // Last, because it is the only one of these that fixes itself.
        if (f.Full) return "full";
        return "";
    }

    private static async Task<Gathered> Gather()
    {
        string[] urls = Choose(await Listings.KnownAsync(CachePath).ConfigureAwait(false));
        Listing?[] said = await Task.WhenAll(urls.Select(u => Listings.ReadAsync(u))).ConfigureAwait(false);
        List<(string, Listed)> servers = [];
        List<string> names = [];
        foreach (Listing? l in said)
        {
            if (l is null) continue;
            string name = l.Directory.Length > 0 ? l.Directory : "a directory";
            names.Add(name);
            foreach (Listed s in l.Servers) servers.Add((name, s));
        }
        return new Gathered(urls.Length, [.. names], servers);
    }

    /// <summary>The directories to ask, after the player's own file has had its say.</summary>
    private static string[] Choose(string[] known)
    {
        Picked picked;
        try
        {
            if (!File.Exists(MinePath)) return known;
            picked = JsonSerializer.Deserialize<Picked>(File.ReadAllText(MinePath)) ?? new Picked();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"could not read {MinePath}: {e.Message}");
            return known;
        }
        IEnumerable<string> using_ = picked.Use is { Length: > 0 } only ? only : known;
        if (picked.Add is { Length: > 0 } also) using_ = using_.Concat(also);
        if (picked.Remove is { Length: > 0 } not) using_ = using_.Where(u => !not.Contains(u, StringComparer.OrdinalIgnoreCase));
        return [.. using_.Select(u => u.Trim()).Where(u => u.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(Listings.MostKnown)];
    }

    /// <summary>
    /// Merges what the directories carry into rows, collapsing what is plainly the same server. By
    /// address written out, which catches the ordinary case where several directories carry the same
    /// canonical name -- and is what stops this game asking one server once per directory.
    /// </summary>
    private static void Take(Gathered got)
    {
        Directories = (got.Asked, got.Names.Length);
        Dictionary<string, Row> by = new(StringComparer.OrdinalIgnoreCase);
        foreach (Row had in Listed) by[$"{had.Host}:{had.Port}"] = had;

        HashSet<string> still = new(StringComparer.OrdinalIgnoreCase);
        // The player's own first, so that where two spellings of one server collapse into one it is
        // theirs that survives.
        foreach (string address in Remembered)
        {
            if (!Sync.TryAddress(address, out string host, out int port, out _)) continue;
            string key = $"{host}:{port}";
            still.Add(key);
            if (!by.TryGetValue(key, out Row? row))
            {
                by[key] = row = new Row { Host = host, Port = port };
                Listed.Add(row);
            }
            row.Hint ??= new Listed(host, port);
            if (!row.Found.Contains(Yours)) row.Found.Add(Yours);
        }
        foreach ((string name, Listed s) in got.Servers)
        {
            string key = $"{s.Host}:{s.Port}";
            still.Add(key);
            if (!by.TryGetValue(key, out Row? row))
            {
                by[key] = row = new Row { Host = s.Host, Port = s.Port };
                Listed.Add(row);
            }
            // The more recently seen of two accounts of one server, which is a rule not worth
            // arguing over because this game's own answer replaces it either way.
            if (row.Hint is null || s.Seen >= row.Hint.Seen) row.Hint = s;
            if (!row.Found.Contains(name)) row.Found.Add(name);
        }
        // Gone from every directory that carries it. A row kept after that is a ghost, which is the
        // single most common thing a list of servers is wrong about.
        Listed.RemoveAll(r => !still.Contains($"{r.Host}:{r.Port}"));
        foreach (Row r in Listed) r.Found.RemoveAll(n => n != Yours && !got.Names.Contains(n));

        _probes ??= new Probes(Sync.Wall);
        Asked.Clear();
        foreach (Row r in Listed) Start(r);
        Sort();
    }

    /// <summary>Works out where to ask a row, off the frame thread, and asks once it knows.</summary>
    private static void Start(Row row)
    {
        if (IPAddress.TryParse(row.Host, out IPAddress? literal))
        {
            Ask(new IPEndPoint(literal.MapToIPv4(), row.Port), row);
            return;
        }
        Task<IPAddress[]> finding = Dns.GetHostAddressesAsync(row.Host, AddressFamily.InterNetwork);
        _ = finding.ContinueWith(t =>
        {
            // Back onto the list on the next frame: Found is the frame thread's, as is everything
            // else here, and a continuation is not on it.
            Pending.Enqueue((row, t.IsCompletedSuccessfully && t.Result.Length > 0 ? new IPEndPoint(t.Result[0], row.Port) : null));
        }, TaskScheduler.Default);
    }

    private static readonly System.Collections.Concurrent.ConcurrentQueue<(Row Row, IPEndPoint? At)> Pending = new();

    private static void Ask(IPEndPoint at, Row row)
    {
        if (!Asked.TryGetValue(at, out List<Row>? sharing)) Asked[at] = sharing = [];
        if (!sharing.Contains(row)) sharing.Add(row);
        _probes?.Ask(at);
    }

    /// <summary>Takes up whatever resolved since the last frame, before the probes are drained.</summary>
    private static void Resolved()
    {
        while (Pending.TryDequeue(out (Row Row, IPEndPoint? At) r))
        {
            if (!Listed.Contains(r.Row)) continue;
            if (r.At is null) r.Row.Trouble = "that name does not resolve";
            else Ask(r.At, r.Row);
        }
    }

    /// <summary>
    /// Collapses rows that turned out to be one server. The exact pass, and the free one: a probe
    /// answer comes from a real socket, so two rows that answered at one address are one server
    /// however differently they were spelled.
    /// </summary>
    private static void Collapse()
    {
        foreach (IGrouping<IPEndPoint, Row> same in Listed.Where(r => r.At is not null).GroupBy(r => r.At!).Where(g => g.Count() > 1))
        {
            // A name outlives the machine's address and is what a player recognises, so it wins over
            // a bare address. Between two names, the first found, which is the order the player's
            // own directories are in.
            Row keep = same.FirstOrDefault(r => !IPAddress.TryParse(r.Host, out _)) ?? same.First();
            foreach (Row drop in same.Where(r => r != keep))
            {
                foreach (string n in drop.Found) if (!keep.Found.Contains(n)) keep.Found.Add(n);
                Listed.Remove(drop);
            }
        }
        Sort();
    }

    /// <summary>Servers with people on them, then ones that can be joined, then the rest. An empty list of three real servers beats three hundred rows of nothing.</summary>
    private static void Sort() =>
        Listed.Sort((a, b) =>
        {
            int Rank(Row r) => r.Facts is null ? 2 : Why(r).Length > 0 ? 1 : 0;
            int by = Rank(a).CompareTo(Rank(b));
            if (by != 0) return by;
            int players = (b.Facts?.Players ?? 0).CompareTo(a.Facts?.Players ?? 0);
            return players != 0 ? players : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
        });

    /// <summary>Let go of the socket and the rows. Called when the mod is unloaded.</summary>
    public static void Stop()
    {
        _probes?.Dispose();
        _probes = null;
        Asked.Clear();
        Listed.Clear();
        Started = false;
    }
}
