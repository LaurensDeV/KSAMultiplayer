using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace KSAMultiplayer.Server;

/// <summary>How a server is set up: from the command line or its config file.</summary>
public sealed record ServerOptions
{
    public int Port { get; init; } = 50100;
    public string Name { get; init; } = "KSA server";
    public string Password { get; init; } = "";
    public int MaxPlayers { get; init; } = 16;

    /// <summary>The KSA build every player must run; empty to take the build of whoever gave the server its world.</summary>
    public string KsaBuild { get; init; } = "";

    /// <summary>Seconds between asking a player for a fresh save of the world, so a player joining starts near now.</summary>
    public double SnapshotEverySeconds { get; init; } = 300.0;

    /// <summary>Whether universe time runs with nobody playing; held by default, so a save handed out is never far behind.</summary>
    public bool KeepTime { get; init; }

    /// <summary>How long everyone has to answer a warp before the ask runs out. Saying nothing is not agreeing.</summary>
    public double WarpAnswerSeconds { get; init; } = 30.0;

    /// <summary>
    /// How long a craft stays its owner's while they are neither in it nor looking at it. Sticky
    /// ownership is what stops two games pulling one hull in two directions; sticky for ever meant
    /// the first player to touch a thing held it until they left, and a second could fly nothing.
    /// </summary>
    public double LapseWithin { get; init; } = 10.0;

    public double Speed { get; init; } = 1.0;
}

/// <summary>
/// The dedicated server: no world of its own, only what every game needs to share one. It keeps the
/// universe time, holds the latest save of the world and hands it to every player who joins, says who
/// flies which craft, passes every craft's state on, and keeps each craft's definition so a game that
/// has never seen it can build it.
///
/// <para>Ticked from one thread; datagrams are read on the socket's own and queued.</para>
/// </summary>
public sealed class GameServer : IDisposable
{
    public sealed class Player(byte id, string name, IPEndPoint at, double now)
    {
        public byte Id { get; } = id;
        public string Name { get; } = name;
        public IPEndPoint At { get; } = at;
        public double HeardAt { get; set; } = now;
        public double JoinedAt { get; } = now;
        public string KsaBuild { get; init; } = "";
        public string[] Mods { get; init; } = [];
        public string System { get; init; } = "";
        public Link Link { get; } = new();

        /// <summary>Whether this player's commands are run: the token offered at the join was on the admin list.</summary>
        public bool Admin { get; set; }

        /// <summary>Whether it has loaded the world and is playing in it, and since when on the server's clock.</summary>
        public bool Loaded { get; set; }
        public double LoadedAt { get; set; }

        /// <summary>How far behind the server's time this player's world was when it last said, s.</summary>
        public double Late { get; set; }

        /// <summary>The speed this player's game is managing, a multiple of real time; zero until it says.</summary>
        public double Can { get; set; }

        /// <summary>Whether their window has their attention; an unfocused one is throttled and is not asked to keep up.</summary>
        public bool Here { get; set; } = true;

        /// <summary>The craft their camera is on, which need not be the one they are flying.</summary>
        public string Watching { get; set; } = "";

        /// <summary>Waiting for a world to be sent: there is none yet, or a fresh one has been asked for.</summary>
        public bool WaitingForWorld { get; set; }
        public double WaitingSince { get; set; }

        /// <summary>Asked for the first save of the world: the server had none when this player joined.</summary>
        public bool Seeding { get; set; }

        /// <summary>
        /// Asked for a save of the world and has not sent one yet. A world is taken from a player
        /// only while this is set: one sent unasked would be written to disk along with the build and
        /// the mods its sender claimed, which everybody else is then held to.
        /// </summary>
        public bool WorldAsked { get; set; }

        /// <summary>Whether the world has been handed to this player: nobody is in a world they were never given.</summary>
        public bool WorldGiven => !Seeding && !WaitingForWorld;

        public string Controlling { get; set; } = "";

        /// <summary>The player's round trip, s, as that player measured it; negative until it has one.</summary>
        public float Rtt { get; set; } = -1f;
    }

    /// <summary>A craft the server knows of: who flies it and how it is built, if it was made after the world was saved.</summary>
    private sealed class Craft
    {
        public byte Owner;
        public byte[]? Definition;
        public double Universe;

        /// <summary>When its owner was last in it or looking at it, on the server's own clock.</summary>
        public double AttendedAt;

        /// <summary>How many times its owner has said how it is built. Every one of these is the craft
        /// torn down and built again in every other game, so a craft described over and over is a
        /// fault worth seeing from here.</summary>
        public int Described;
    }

    private const double SilentSeconds = 10.0, LoadingSilentSeconds = 120.0;

    /// <summary>
    /// How long a session must have been silent, s, before a game joining under its name is taken to
    /// be that session coming back rather than somebody else. A living game pings once a second.
    /// </summary>
    private const double QuietEnoughToReplaceSeconds = 3.0;

    /// <summary>How long one join cookie stands, s. Two are accepted, so it is good for twice this.</summary>
    private const double CookieSeconds = 15.0;
    private const double RosterEverySeconds = 2.0, OwnersEverySeconds = 5.0, ClockEverySeconds = 0.2;

    // A count that climbs while nobody is launching anything is a world filling with craft that
    // should not be there, which is not visible in any other line here.
    private const double CraftCountEverySeconds = 30.0;

    // How long a player joining waits for a fresh save before it is sent the one there is.
    private const double FreshWithinSeconds = 15.0;

    // A world this old in universe time is saved afresh before it is handed to a player joining.
    private const double StaleSeconds = 30.0;

    // A craft defined or gone this long before a world was saved is in that save already.
    private const double SaveMarginSeconds = 10.0;

    // How long after a warp ends before another may be asked for: one that could be re-proposed the
    // instant it stopped is a warp nobody can get out of. How long there is to answer is an option,
    // since it is the one number here a player waits on.
    private const double WarpQuietSeconds = 5.0;

    // Higher than any world can actually run, but a number rather than whatever a game sends.
    private const double MostWarp = 100_000.0;

    // Told again this often while a warp is asked for or running, since a datagram can be lost.
    private const double WarpEverySeconds = 1.0;

    private readonly Udp _udp;

    // Made afresh each time the server starts, so cookies from a previous run are not accepted and
    // nothing has to be kept on disk for them.
    private readonly byte[] _cookieKey = RandomNumberGenerator.GetBytes(32);
    private readonly Func<double> _clock;
    private readonly WorldStore? _store;
    private readonly Dictionary<byte, Player> _players = [];
    private readonly Dictionary<string, Craft> _crafts = [];
    private readonly Dictionary<string, double> _gone = [];
    private Blob? _world;
    private string _worldBuild = "", _worldSystem = "";

    // Whether the world's own universe time is anything: a save copied in by hand does not say, and
    // the clock cannot be put back to a time nobody knows.
    private bool _worldTimeKnown;

    private string[] _worldMods = [];
    private double _rosterNext, _ownersNext, _clockNext, _snapshotNext, _snapshotAskedAt = double.NegativeInfinity;
    private double _craftCountNext;
    private int _craftCountSaid = -1;

    // One world has one time, so nobody changes its speed alone: somebody asks, everybody answers,
    // and it runs only if they all agree. It is then theirs to steer and theirs to stop.
    private byte _warpWho;
    private float _warpSpeed = 1f, _warpWanted = 1f;
    private Wire.WarpPhase _warpPhase;
    private double _warpAskedAt, _warpNext, _warpQuietSince = double.NegativeInfinity, _governNext;

    // The fastest a running warp has been found not to work, and whose machine it was found on.
    // Nothing climbs back to it, or the world climbs into the same ceiling and falls out of it once a
    // second for as long as the warp lasts.
    private double _warpCeiling = double.PositiveInfinity;
    private byte _warpCeilingWho;
    private readonly Dictionary<byte, bool> _warpVotes = [];

    /// <param name="admins">Who may run commands; by default the list beside the world, or none kept at all without one.</param>
    public GameServer(ServerOptions options, Func<double> clock, WorldStore? store = null, IPAddress? bind = null, Admins? admins = null)
    {
        (Options, _clock, _store) = (options, clock, store);
        Admins = admins ?? new Admins(store?.Folder ?? "");
        UniverseClock.SetSpeed(options.Speed, clock());
        _udp = new Udp(options.Port, clock, "KSAMultiplayer server", bind);
        if (store?.Load() is { } saved)
        {
            _world = saved.World;
            (_worldBuild, _worldMods, _worldSystem) = (saved.KsaBuild, saved.Mods, saved.System);
            _worldTimeKnown = saved.TimeKnown;
            // Including one written before that was checked.
            if (saved.TimeKnown && ClockFollow.SaneUniverse(saved.World.Universe)) UniverseClock.Set(saved.World.Universe, clock());
            Log($"world loaded from {store.Folder}: {saved.World.Data.Length / 1024} KB"
                + (saved.TimeKnown ? $" at universe time {saved.World.Universe:F0} s" : ", its universe time to come from the first player to load it"));
        }
        _snapshotNext = clock() + options.SnapshotEverySeconds;
    }

    public ServerOptions Options { get; private set; }

    /// <summary>
    /// Settings changed while the server runs, from the console. Everything read as it is used
    /// simply is the new value from here on; the port stays what the socket is bound to, and the
    /// two that were already acted on are acted on again.
    /// </summary>
    public void Reconfigure(ServerOptions next)
    {
        double now = _clock();
        ServerOptions was = Options;
        Options = next with { Port = was.Port };
        // A shorter wait is not made to sit out the longer one already counting down.
        if (next.SnapshotEverySeconds != was.SnapshotEverySeconds) _snapshotNext = Math.Min(_snapshotNext, now + next.SnapshotEverySeconds);
        // A warp holds the clock for as long as it runs and gives it back at this speed when it ends.
        if (next.Speed != was.Speed && _warpPhase == Wire.WarpPhase.None) UniverseClock.SetSpeed(next.Speed, now);
    }
    public UniverseClock UniverseClock { get; } = new();
    public int Port => _udp.Port;
    public Udp Socket => _udp;
    public IReadOnlyCollection<Player> Players => _players.Values;
    public bool HasWorld => _world is not null;
    public Blob? World => _world;
    public int CraftCount => _crafts.Count;
    public Admins Admins { get; }

    /// <summary>
    /// The KSA build a player must run: the operator's if they named one, otherwise the build of the
    /// world being handed out. Empty when neither is known, and then any build is let in.
    /// </summary>
    public string RequiredBuild => Options.KsaBuild.Length > 0 ? Options.KsaBuild : _worldBuild;

    /// <summary>Console lines typed in a game by an admin, for the console program to run as its own.</summary>
    public Queue<(Player Who, string Line)> Asked { get; } = new();

    /// <summary>Whose warp it is, how fast, and whether it is asked for or running.</summary>
    public (byte Who, double Speed, Wire.WarpPhase Phase) Warp => (_warpWho, _warpSpeed, _warpPhase);

    /// <summary>What has happened, for the console.</summary>
    public Queue<string> Said { get; } = new();

    public byte OwnerOf(string key) => _crafts.TryGetValue(key, out Craft? c) ? c.Owner : (byte)0;

    /// <summary>Puts a world in place of the one there is, as an operator would from the console.</summary>
    /// <param name="timeKnown">False for a save copied in by hand, whose universe time the server cannot read: the first player to load it says it.</param>
    public void SetWorld(Blob world, string ksaBuild, string[] mods, string system, bool timeKnown = true)
    {
        // A world's own time is believed only if it could be a time at all: a snapshot timed
        // absurdly was written to disk as the server's own and outlived a restart, after which no
        // world in the room advanced at all.
        if (timeKnown && !ClockFollow.SaneUniverse(world.Universe)) timeKnown = false;
        _world = world;
        (_worldBuild, _worldMods, _worldSystem, _worldTimeKnown) = (ksaBuild, mods, system, timeKnown);
        if (timeKnown && !UniverseClock.Known) UniverseClock.Set(world.Universe, _clock());
        if (timeKnown) Prune(world.Universe);
        _store?.Save(world, ksaBuild, mods, system, timeKnown);
    }

    /// <summary>Handles what has arrived and sends what is due. From the server's one thread, a few dozen times a second.</summary>
    public void Tick()
    {
        double now = _clock();
        int handled = 0;
        while (handled++ < 8192 && _udp.TryReceive(out (byte[] Data, IPEndPoint From, double Arrived) d)) Handle(d.Data, d.From, d.Arrived);

        foreach (Player gone in _players.Values.Where(p => now - p.HeardAt > (p.Loaded ? SilentSeconds : LoadingSilentSeconds)).ToArray())
        {
            Remove(gone, "has gone quiet");
        }

        bool anyone = _players.Values.Any(p => p.Loaded);
        UniverseClock.Run(Options.KeepTime || anyone, now);
        if (!anyone) HoldTimeAtTheWorld(now);

        // Players waiting on a fresh save are sent the one there is once it comes, or after a while without.
        if (_world is not null)
        {
            foreach (Player p in _players.Values.Where(p => p.WaitingForWorld && (_world.Universe >= _snapshotAskedAt - 1.0 || now - p.WaitingSince > FreshWithinSeconds)).ToArray())
            {
                SendWorld(p);
            }
        }
        if (now >= _snapshotNext)
        {
            AskSnapshot("the world is saved now and then for players who join");
            _snapshotNext = now + Options.SnapshotEverySeconds;
        }

        if (_warpPhase == Wire.WarpPhase.Asking && now - _warpAskedAt > Options.WarpAnswerSeconds)
        {
            EndWarp(now, $"{Named(_warpWho)}'s warp: not everyone answered");
        }
        // Either phase, not only a running one: an ask whose asker is gone is nobody's to answer.
        if (_warpPhase != Wire.WarpPhase.None && !_players.ContainsKey(_warpWho))
        {
            EndWarp(now, "the warp ends: whoever started it has gone");
        }
        Govern(now);
        Lapse(now);
        if (_warpPhase != Wire.WarpPhase.None && now >= _warpNext)
        {
            Broadcast(WarpState(), p => p.Loaded);
            _warpNext = now + WarpEverySeconds;
        }

        if (now >= _rosterNext)
        {
            Broadcast(Roster(), _ => true);
            _rosterNext = now + RosterEverySeconds;
        }
        if (now >= _craftCountNext)
        {
            _craftCountNext = now + CraftCountEverySeconds;
            if (_crafts.Count != _craftCountSaid)
            {
                int moved = _craftCountSaid < 0 ? 0 : _crafts.Count - _craftCountSaid;
                Log($"{_crafts.Count} craft known{(moved != 0 ? $", {moved:+#;-#} since" : "")}");
                _craftCountSaid = _crafts.Count;
            }
        }
        if (now >= _ownersNext)
        {
            foreach (Wire.Owners page in OwnerPages()) Broadcast(page, p => p.Loaded);
            _ownersNext = now + OwnersEverySeconds;
        }
        if (now >= _clockNext && UniverseClock.Known)
        {
            Broadcast(new Wire.Clock(UniverseClock.Now(now), now, (float)UniverseClock.Rate), _ => true);
            _clockNext = now + ClockEverySeconds;
        }
        foreach (Player p in _players.Values) p.Link.Pump(now, m => _udp.Send(m, p.At));
    }

    private void Handle(byte[] data, IPEndPoint from, double arrived)
    {
        Player? player = _players.Values.FirstOrDefault(p => p.At.Equals(from));
        if (Wire.Read(data, out ushort version) is not { } message)
        {
            if (player is null && version != 0 && version != Wire.Version)
            {
                _udp.Send(new Wire.Welcome(0, $"this server speaks version {Wire.Version} and your mod {version}: update one of them", Options.Name, false), from);
            }
            return;
        }
        if (player is null)
        {
            if (message is Wire.Hello hello) Admit(hello, from, arrived);
            return;
        }
        player.HeardAt = arrived;
        switch (message)
        {
            case Wire.Hello:
                // Its welcome was lost.
                _udp.Send(new Wire.Welcome(player.Id, "", Options.Name, _world is not null || !player.Seeding), from);
                return;
            case Wire.Ping ping:
                player.Rtt = ping.Rtt;
                player.Late = float.IsFinite(ping.Late) ? ping.Late : 0.0;
                player.Can = float.IsFinite(ping.Can) && ping.Can > 0f ? ping.Can : 0.0;
                player.Here = ping.Here;
                player.Watching = ping.Watching;
                _udp.Send(new Wire.Pong(ping.Sent, _clock()), from);
                return;
            // Only a player the world has been handed to, saying a time that is a time: this is what
            // sets the universe clock on a server whose world came off disk, and nobody is in a world
            // they were never given -- one that said so anyway claimed craft and blocked every warp.
            case Wire.Loaded loaded when player.WorldGiven && ClockFollow.SaneUniverse(loaded.Universe):
                OnLoaded(player, loaded.Universe);
                return;
            case Wire.State state when player.Loaded:
                // Owned already, or not relayed. A state used to take an unheld craft, which handed
                // one back the instant it was let go of and made the letting go meaningless.
                if (OwnerOf(state.Key) != player.Id) return;
                // A claim says which craft is being flown; a state says which is not. Only the one
                // they are said to be in may give it up, so a parked craft cannot unseat another.
                bool atTheControls = (state.Flags & Wire.StateFlags.Controlled) != 0;
                if (atTheControls && player.Controlling.Length == 0) Flying(player, state.Key);
                else if (!atTheControls && player.Controlling == state.Key) Flying(player, "");
                Wire.State relayed = state with { Owner = player.Id };
                Broadcast(relayed, p => p.Loaded && p != player);
                return;
            case Wire.Claim claim when player.Loaded && claim.Key.Length > 0:
                bool had = OwnerOf(claim.Key) == player.Id;
                // Only once it is theirs: a player refused a craft is not flying it, whatever they asked.
                if (Grant(player, claim.Key, taking: claim.Controlling, made: claim.Made) is not { } granted) return;
                if (claim.Controlling) Flying(player, granted);
                if (!had) OwnersChanged();
                return;
            case Wire.Gone gone when player.Loaded && OwnerOf(gone.Key) == player.Id:
                _crafts.Remove(gone.Key);
                _gone[gone.Key] = UniverseClock.Now(_clock());
                if (player.Controlling == gone.Key) Flying(player, "");
                Broadcast(gone, p => p.Loaded && p != player);
                return;
            case Wire.NoWorld no:
                OnNoWorld(player, Wire.Clip(no.Why, 200));
                return;
            case Wire.WarpAsk ask when player.Loaded:
                OnWarpAsk(player, ask.Speed);
                return;
            case Wire.WarpVote vote when player.Loaded:
                OnWarpVote(player, vote.Yes);
                return;
            case Wire.Chat chat:
                string text = Wire.Clip(chat.Text, Wire.ChatChars);
                if (text.Length == 0) return;
                Log($"<{player.Name}> {text}");
                Broadcast(new Wire.Chat(player.Id, text), _ => true);
                return;
            case Wire.Command asked:
                string line = Wire.Clip(asked.Line, Wire.ChatChars).Trim();
                if (line.Length == 0) return;
                if (!player.Admin)
                {
                    Tell(player, "you are not an admin here");
                    Log($"{player.Name} asked for '{line}' and is no admin");
                    return;
                }
                Log($"{player.Name}, an admin, asks for '{line}'");
                Asked.Enqueue((player, line));
                return;
            case Wire.Bye:
                Remove(player, "has left");
                return;
            case Wire.Chunk or Wire.Need or Wire.Got:
                if (player.Link.Handle(message, arrived, m => _udp.Send(m, player.At)) is { } bytes) OnBlob(player, bytes);
                return;
        }
    }

    /// <summary>
    /// A word only this server could have made, for this address and this moment. Said in answer to
    /// a hello that carries none, and expected back in the next one.
    ///
    /// <para>Nothing in a datagram says where it really came from, so without this a hello from a
    /// made-up address was a place held on the roster: sixteen of them filled a server for the two
    /// minutes it takes to let go of a session that never loads, over and over, with none of the
    /// real sender's addresses anywhere in the log. Each of those places also waited for a world,
    /// so the server sent the whole of it to the made-up address and, hearing nothing back, sent it
    /// again every couple of seconds for the life of the session.</para>
    ///
    /// <para>Worked out rather than remembered, so there is no list of half-joined addresses to
    /// fill. It is a round number of seconds old at most, and the one before it is taken too, so a
    /// hello that crosses the boundary is not refused.</para>
    /// </summary>
    private string Cookie(IPEndPoint from, long slice)
    {
        Span<byte> made = stackalloc byte[32];
        HMACSHA256.HashData(_cookieKey, Encoding.UTF8.GetBytes($"{from}|{slice}"), made);
        return Base64Url.EncodeToString(made[..12]);
    }

    private bool CookieHolds(Wire.Hello hello, IPEndPoint from, double now)
    {
        if (hello.Cookie.Length == 0) return false;
        long slice = (long)(now / CookieSeconds);
        return hello.Cookie == Cookie(from, slice) || hello.Cookie == Cookie(from, slice - 1);
    }

    private void Admit(Wire.Hello hello, IPEndPoint from, double now)
    {
        // Before anything is held for this address, and before the password is even looked at: an
        // address that cannot hear the answer cannot join, whatever else it got right.
        if (!CookieHolds(hello, from, now))
        {
            _udp.Send(new Wire.Welcome(0, "", Options.Name, false, Cookie(from, (long)(now / CookieSeconds))), from);
            return;
        }
        string? refused = _players.Count >= Options.MaxPlayers ? $"the server is full: {Options.MaxPlayers} players"
            : Options.Password.Length > 0 && hello.Password != Options.Password ? "wrong password"
            : Refusal(hello);
        if (refused is not null)
        {
            _udp.Send(new Wire.Welcome(0, refused, Options.Name, false), from);
            Log($"refused {Wire.Clip(hello.Name, Wire.NameChars)} from {from}: {refused}");
            return;
        }
        // The same player coming back, before they are named against the session they just lost. A
        // game's socket takes a fresh port every time it connects, so a reconnect never matches the
        // endpoint a session is held under and used to be let in beside its own ghost: named
        // "Bob 2" for the rest of the evening -- in the roster, in the chat and in admins.json --
        // refused every claim on its own craft, because the ghost still answered for the owner, and
        // told that a player of its own name was flying its rocket. Ten seconds of that, or two
        // minutes if the ghost was lost mid-load, which is what a reload leaves. The logs of the
        // night everybody crashed have "averageksp 2" and "MrJeranimo 2" in them.
        //
        // A living game pings once a second, so a few seconds of silence is a session that has gone
        // and not a second player who happens to have chosen the same name.
        string asked = Wire.Clip(hello.Name, Wire.NameChars);
        foreach (Player ghost in _players.Values
                     .Where(q => now - q.HeardAt > QuietEnoughToReplaceSeconds
                                 && string.Equals(q.Name, asked, StringComparison.OrdinalIgnoreCase)
                                 && !q.At.Equals(from))
                     .ToArray())
        {
            Log($"{ghost.Name} had gone quiet, and is joining again from {from}");
            Remove(ghost, "has gone quiet");
        }
        byte id = 1;
        while (_players.ContainsKey(id)) id++;
        string name = UniqueName(asked);
        Player p = new(id, name, from, now)
        {
            KsaBuild = hello.KsaBuild,
            Mods = hello.Mods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            System = hello.System,
            Admin = Admins.Check(Wire.Clip(hello.Token, Wire.TokenChars), name),
        };
        _players[id] = p;

        bool seeding = _world is null && _players.Values.All(q => !q.Seeding);
        _udp.Send(new Wire.Welcome(id, "", Options.Name, !seeding), from);
        Log($"{name} has joined from {from}{(p.Admin ? " as an admin" : "")}{(seeding ? ", and their world becomes the server's" : "")}");
        // Said only when there is something to say: that they are an admin, or that the token they
        // offered is no good, which is how a game deopped while it was away forgets the one it kept.
        // Nothing at all to a player who offered none, who is no admin and knows it.
        if (p.Admin || hello.Token.Length > 0) _udp.Send(new Wire.Op(p.Admin, ""), from);
        if (p.Admin) Tell(p, "you are an admin here; /help in the chat for what you can ask");
        Broadcast(new Wire.Chat(0, $"{name} has joined"), q => q != p);
        _rosterNext = 0.0;

        if (seeding)
        {
            p.Seeding = true;
            AskWorldOf(p, "the server has no world yet: yours becomes it");
            _snapshotAskedAt = UniverseClock.Known ? UniverseClock.Now(now) : double.NegativeInfinity;
            return;
        }
        (p.WaitingForWorld, p.WaitingSince) = (true, now);
        // A world much older than now is saved afresh by someone in it first, so the player joining does not have to catch up far.
        if (_world is not null && UniverseClock.Now(now) - _world.Universe > StaleSeconds) AskSnapshot($"{name} is joining");
    }

    // Why a player cannot be let in: another build of KSA, or a mod the world may hold parts of that it does not have.
    private string? Refusal(Wire.Hello hello)
    {
        string build = RequiredBuild;
        if (build.Length > 0 && hello.KsaBuild.Length > 0 && hello.KsaBuild != build)
        {
            return $"the server's world is KSA {build} and you run {hello.KsaBuild}: you need the same build";
        }
        // A world of Sol entire is not a world of Earth and its Moon: the save would load into a game
        // that has no such bodies, or put every craft of the others nowhere.
        if (_worldSystem.Length > 0 && hello.System.Length > 0 && hello.System != _worldSystem)
        {
            return $"this world is {_worldSystem} and you are playing {hello.System}: start a game in {_worldSystem} to join";
        }
        HashSet<string> theirs = [.. hello.Mods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        string[] missing = [.. _worldMods.Where(m => !theirs.Contains(m)).Order()];
        if (missing.Length > 0) return $"the server's world needs mods you do not have enabled: {string.Join(", ", missing)}";
        // And none the others lack. A mod that adds parts makes craft nobody else can put together,
        // and the first they hear of it is a craft arriving that their game refuses to build, which
        // reads as the mod being broken rather than as somebody running something extra. Nothing is
        // asked of the player whose world this is about to become: they are the ones who set it.
        if (_worldMods.Length == 0) return null;
        HashSet<string> world = [.. _worldMods];
        string[] extra = [.. theirs.Where(m => !world.Contains(m)).Order()];
        return extra.Length > 0
            ? $"you are running mods this world does not have ({string.Join(", ", extra)}): turn them off to join"
            : null;
    }

    private void SendWorld(Player p)
    {
        if (_world is null) return;
        p.WaitingForWorld = false;
        p.Link.Send(_world.Encode(), _clock());
        Log($"sending {p.Name} the world, {_world.Data.Length / 1024} KB");
    }

    // An empty server's time is the time of the world it hands out.
    //
    // Between the last save and the last player leaving, time was simulated by somebody who has since
    // gone, and it went with them: it is in no world the server has. A clock that kept it would make
    // the next player warp through time whose results nobody kept, and because the clock is never put
    // back, a session that ended before it had caught up would hand the one after it a longer wait
    // still, and so on. --keep-time is the operator saying they want that time anyway.
    private void HoldTimeAtTheWorld(double now)
    {
        if (Options.KeepTime || !_worldTimeKnown || _world is null || !UniverseClock.Known) return;
        double ahead = UniverseClock.Now(now) - _world.Universe;
        if (Math.Abs(ahead) < 0.001) return;
        UniverseClock.Set(_world.Universe, now);
        if (ahead > 1.0) Log($"nobody is in the world: universe time waits at {_world.Universe:F0} s, {ahead:F0} s back");
    }

    private void OnLoaded(Player p, double universe)
    {
        if (p.Loaded) return;
        double now = _clock();
        (p.Loaded, p.LoadedAt, p.Seeding) = (true, now, false);
        if (!UniverseClock.Known)
        {
            UniverseClock.Set(universe, now);
            Log($"universe time starts at {universe:F0} s, from {p.Name}'s world");
        }
        // What was made and what went after the world it loaded was saved.
        double since = (_world?.Universe ?? universe) - SaveMarginSeconds;
        foreach ((string key, Craft c) in _crafts)
        {
            if (c.Definition is not null && c.Universe >= since && c.Owner != p.Id) p.Link.Send(c.Definition, now);
        }
        foreach ((string key, double at) in _gone) if (at >= since) _udp.Send(new Wire.Gone(key), p.At);
        foreach (Wire.Owners page in OwnerPages()) _udp.Send(page, p.At);
        Log($"{p.Name} is in the world");
        _rosterNext = 0.0;
    }

    private void OnBlob(Player p, byte[] bytes)
    {
        if (Blob.Decode(bytes) is not { } blob) return;
        switch (blob.Kind)
        {
            case BlobKind.World:
                // Only the world this player was asked for. Anyone else's is theirs: a world taken
                // from whoever cared to send one is written to disk along with that sender's build
                // and mods, and every other player is held to those -- so one client could lock the
                // rest out of the server, and out of every restart of it, by sending a world.
                if (!p.WorldAsked)
                {
                    Log($"{p.Name} sent a world nobody asked for, {blob.Data.Length / 1024} KB: left where it came from");
                    return;
                }
                p.WorldAsked = false;
                SetWorld(blob, p.KsaBuild, p.Mods, p.System);
                Log($"{p.Name} saved the world: {blob.Data.Length / 1024} KB at universe time {blob.Universe:F0} s");
                if (p.Seeding) OnLoaded(p, blob.Universe);
                return;
            case BlobKind.Vehicle when p.Loaded && blob.Key.Length > 0:
                // A definition under a name this player was refused is dropped; the claim that goes with
                // it tells their game the name it may use, and the definition is sent again under that.
                if (Grant(p, blob.Key) is null) return;
                Craft craft = _crafts[blob.Key];
                Blob stamped = blob with { Owner = p.Id, Universe = UniverseClock.Now(_clock()) };
                craft.Definition = stamped.Encode();
                craft.Described++;
                Log($"{p.Name} says how {blob.Key} is built, {blob.Data.Length / 1024} KB"
                    + (craft.Described > 1 ? $" -- {craft.Described} times now" : ""));
                craft.Universe = stamped.Universe;
                _gone.Remove(blob.Key);
                foreach (Player other in _players.Values.Where(q => q.Loaded && q != p)) other.Link.Send(craft.Definition, _clock());
                return;
        }
    }

    // Whether a player may fly a craft. The first to take one that nobody has keeps it until they
    // leave: a craft another player here holds is theirs whether or not they are flying it this
    // second, and everyone else only watches. Letting a craft be taken back the moment its owner
    // looked away put two players on one kitten, each game pulling it towards its own idea of where
    // it was, and one of them was flung.
    /// <summary>
    /// The name this player may fly the craft under: the one they asked for, or one of its own when
    /// that name is another player's and this is a craft their game made. Null when it is not theirs.
    /// </summary>
    private string? Grant(Player p, string key, bool taking = false, bool made = false)
    {
        if (!_crafts.TryGetValue(key, out Craft? craft))
        {
            if (_gone.ContainsKey(key) && !taking) return null;
            _crafts[key] = craft = new Craft();
        }
        if (craft.Owner == p.Id) return key;
        if (craft.Owner != 0 && _players.TryGetValue(craft.Owner, out Player? holder))
        {
            // A craft this game made that happens to share a name is a second craft, not the one the
            // holder has, and is given a name of its own. Anything else is the holder's to fly.
            if (!made) return null;
            string fresh = FreeKey(key);
            _crafts[fresh] = new Craft { Owner = p.Id, AttendedAt = _clock() };
            _gone.Remove(fresh);
            OwnersChanged();
            _udp.Send(new Wire.Rename(key, fresh), p.At);
            Log($"{holder.Name} already has a craft called {key}, so {p.Name}'s is {fresh} here");
            return fresh;
        }
        (craft.Owner, craft.AttendedAt) = (p.Id, _clock());
        _gone.Remove(key);
        OwnersChanged();
        return key;
    }

    // A name like the one asked for that no craft here has, gone or not: one that came back under a
    // name just handed out would be taken for the craft that left.
    private string FreeKey(string root)
    {
        for (int n = 2; n < 1000; n++)
        {
            string tried = Wire.Clip($"{root} {n}", Wire.KeyChars);
            if (!_crafts.ContainsKey(tried) && !_gone.ContainsKey(tried)) return tried;
        }
        return Wire.Clip($"{root} {Guid.NewGuid():N}", Wire.KeyChars);
    }

    private void OwnersChanged() => _ownersNext = 0.0;

    private string Named(byte id) => _players.TryGetValue(id, out Player? p) ? p.Name : "somebody";

    // Somebody wants the world to run at another speed. While one is running it is the asker's alone
    // to change or stop; otherwise it is put to everyone, and the asking counts as their own yes.
    private void OnWarpAsk(Player p, float speed)
    {
        double now = _clock();
        speed = float.IsFinite(speed) ? (float)Math.Clamp(speed, 0.0, MostWarp) : 1f;
        bool normal = Math.Abs(speed - 1f) < 0.001f;

        if (_warpPhase == Wire.WarpPhase.Running)
        {
            if (p.Id != _warpWho) return;
            if (normal)
            {
                EndWarp(now, $"{p.Name} ended the warp");
                return;
            }
            // Asked for, and held to what the room can fly: the governor climbs back towards the rest.
            // Their own ask, so whatever ceiling the governor had found is let go of; if the new speed
            // cannot be flown either it will have found it again within the second.
            (_warpWanted, _warpCeiling, _warpCeilingWho) = (speed, double.PositiveInfinity, 0);
            RunWarp((float)Flyable(speed), now);
            return;
        }
        if (_warpPhase == Wire.WarpPhase.Asking)
        {
            // Whoever is asking may still reach for another speed while the rest answer: it is the
            // same question at a different number, so the answers already given stand. Nobody else
            // gets a word in, since two warps put at once is a room that agrees to neither.
            if (p.Id != _warpWho || normal || Math.Abs(speed - _warpSpeed) < 0.001f) return;
            _warpSpeed = speed;
            Log($"{Named(_warpWho)} now asks for {speed:0.##}x");
            Broadcast(WarpState(), q => q.Loaded);
            return;
        }
        if (normal) return;
        // Just ended: whoever had it keeps it until the world has been at normal speed this long, so
        // that stepping down through normal and back up is one warp and not two votes. Anybody else
        // waits out the quiet, since a warp another player can pick up the instant it ends is a warp
        // nobody can get out of.
        if (now - _warpQuietSince < WarpQuietSeconds) return;

        // Put at what the room can fly, not at what was asked: agreeing to a speed somebody's
        // machine cannot manage is agreeing to leave them behind.
        (_warpWanted, _warpCeiling, _warpCeilingWho) = (speed, double.PositiveInfinity, 0);
        (_warpWho, _warpSpeed, _warpPhase, _warpAskedAt, _warpNext) = (p.Id, (float)Flyable(speed), Wire.WarpPhase.Asking, now, 0.0);
        _warpVotes.Clear();
        _warpVotes[p.Id] = true;
        Log($"{p.Name} asks to run the world at {speed:0.##}x");
        Broadcast(WarpState(), q => q.Loaded);
        Counted(now);
    }

    private void OnWarpVote(Player p, bool yes)
    {
        if (_warpPhase != Wire.WarpPhase.Asking || !_warpVotes.TryAdd(p.Id, yes)) return;
        double now = _clock();
        if (!yes)
        {
            EndWarp(now, $"{Named(_warpWho)}'s warp: {p.Name} said no");
            return;
        }
        _warpNext = 0.0;
        Counted(now);
    }

    // Everyone in the world must have answered, and all of them yes. Somebody who says nothing is
    // somebody who has not agreed, so the ask runs out rather than passing without them.
    private void Counted(double now)
    {
        if (_warpPhase != Wire.WarpPhase.Asking) return;
        if (_players.Values.Any(q => q.Loaded && !_warpVotes.ContainsKey(q.Id))) return;
        _warpPhase = Wire.WarpPhase.Running;
        RunWarp(_warpSpeed, now);
        Log($"everyone agreed: the world runs at {_warpSpeed:0.##}x for {Named(_warpWho)}");
        Broadcast(new Wire.Chat(0, $"the world is warping at {_warpSpeed:0.##}x, {Named(_warpWho)}'s"), q => q.Loaded);
    }

    private void RunWarp(float speed, double now)
    {
        // The server's own clock is what every game follows, so warping it warps all of them together.
        (_warpSpeed, _warpQuietSince, _warpNext) = (speed, now, 0.0);
        UniverseClock.SetSpeed(speed, now);
        Broadcast(WarpState(), p => p.Loaded);
    }

    private void EndWarp(double now, string why)
    {
        if (_warpPhase == Wire.WarpPhase.None) return;
        (_warpPhase, _warpSpeed, _warpWanted, _warpQuietSince, _warpNext) = (Wire.WarpPhase.None, 1f, 1f, now, 0.0);
        (_warpCeiling, _warpCeilingWho) = (double.PositiveInfinity, 0);
        _warpVotes.Clear();
        UniverseClock.SetSpeed(Options.Speed, now);
        Log(why);
        Broadcast(new Wire.Chat(0, why), p => p.Loaded);
        Broadcast(WarpState(), p => p.Loaded);
    }

    private Wire.Warp WarpState() => new(_warpWho, _warpSpeed, _warpPhase,
        _warpPhase == Wire.WarpPhase.Asking
            ? [.. _players.Values.Where(q => q.Loaded && !_warpVotes.ContainsKey(q.Id)).Select(q => q.Id).Order()]
            : []);

    private bool AskSnapshot(string why, byte notThisOne = 0)
    {
        // The player in the world longest, whose world has had the most time to settle.
        if (_players.Values.Where(p => p.Loaded && p.Id != notThisOne).OrderBy(p => p.LoadedAt).FirstOrDefault() is not { } from) return false;
        _snapshotAskedAt = UniverseClock.Now(_clock());
        AskWorldOf(from, why);
        return true;
    }

    // Every ask for a world goes through here, because the asking is what makes the answer the
    // server's to keep: a world is written only for a player this was called for.
    private void AskWorldOf(Player p, string why)
    {
        p.WorldAsked = true;
        _udp.Send(new Wire.Snapshot(why), p.At);
    }

    /// <summary>
    /// A game that will not hand its world over, because what it holds would not load anywhere.
    /// Somebody else is asked: a world an hour old is worth more than one nobody can open, and the
    /// world already here is left alone until a good one comes.
    /// </summary>
    private void OnNoWorld(Player p, string why)
    {
        Log($"{p.Name} will not hand over a world: {why}");
        Broadcast(new Wire.Chat(0, $"{p.Name}'s world cannot be handed on: {why}"), q => q.Loaded);
        if (p.Seeding)
        {
            // Not the one whose world this becomes after all, and one of those waiting for a world
            // instead: a game that cannot hand its own over can still be given somebody else's.
            (p.Seeding, p.WaitingForWorld, p.WaitingSince) = (false, true, _clock());
            if (_players.Values.FirstOrDefault(q => q.WaitingForWorld && q.Id != p.Id) is { } next)
            {
                (next.Seeding, next.WaitingForWorld) = (true, false);
                AskWorldOf(next, "the server has no world yet: yours becomes it");
            }
            return;
        }
        AskSnapshot(why, notThisOne: p.Id);
    }

    // How often the room's speed is looked at again, and how far out of step it must be to move.
    private const double GovernEverySeconds = 1.0, GovernFall = 0.8, GovernStep = 1.5, GovernBackOff = 0.6;

    // How far behind a game may be before the world stops asking more of it. In its own seconds, so
    // it means the same at every speed.
    private const double GovernLateSeconds = 4.0;

    /// <summary>The fastest the room can fly, and whose machine says so: the slowest among those whose players are looking.</summary>
    private (double Can, byte Who) Room()
    {
        (double can, byte who) = (double.PositiveInfinity, (byte)0);
        foreach (Player p in _players.Values)
        {
            // Not an unfocused window: Windows throttles it, so what it manages measures nothing but
            // Windows. That player falls behind and is caught up afterwards rather than held to.
            // Nothing said yet is not a slow machine: a game that has not measured itself holds
            // nobody back, and will say so within a couple of seconds of playing.
            if (p.Loaded && p.Here && p.Can > 0.0 && p.Can < can) (can, who) = (p.Can, p.Id);
        }
        return (double.IsFinite(can) ? Math.Max(1.0, can) : double.PositiveInfinity, who);
    }

    /// <summary>The worst any watching player is behind, in their own seconds, and who, or zero if nobody is.</summary>
    private (double Late, byte Who) Losing()
    {
        double rate = Math.Max(UniverseClock.Rate, 1.0);
        (double worst, byte who) = (0.0, (byte)0);
        foreach (Player p in _players.Values)
        {
            if (p.Loaded && p.Here && p.Late / rate > worst) (worst, who) = (p.Late / rate, p.Id);
        }
        return (worst, who);
    }

    /// <summary>
    /// A speed held to what the room can fly. Only upwards: a pause or a world in slow motion asks
    /// nothing of anybody's machine, and is nobody's to refuse on those grounds.
    /// </summary>
    private double Flyable(double speed) => speed <= 1.0 ? speed : Math.Max(1.0, Math.Min(speed, Room().Can));

    /// <summary>
    /// The running warp held to what the room can fly. It falls as soon as a machine says it cannot
    /// keep up and climbs back a step at a time while everyone is comfortably ahead of it, so a warp
    /// decays into something flyable instead of leaving somebody thousands of seconds behind.
    /// </summary>
    private void Govern(double now)
    {
        if (_warpPhase != Wire.WarpPhase.Running || now < _governNext) return;
        // A pause or a world in slow motion asks nothing of anybody's machine and is nobody's to
        // refuse on those grounds. Governing one undid it: a pause is a warp at 0, every fall here
        // has a floor of 1x, and nothing climbs back down to 0 from there -- so a world everyone had
        // agreed to stop ran on at normal speed a second later, for good.
        if (_warpWanted <= 1.0) return;
        _governNext = now + GovernEverySeconds;

        (double room, byte roomWho) = Room();
        (double late, byte lateWho) = Losing();
        double at = _warpSpeed, next = at;
        byte limits = 0;

        // The machine that set the ceiling saying it can now fly faster than the ceiling is news, and
        // it is the only news there is: a machine comfortable at the speed it is running at says
        // nothing at all, so reading everybody's silence as room is what had the world climb into the
        // same ceiling and fall out of it once a second for as long as the warp lasted. A machine
        // that has left takes its ceiling with it.
        if (_warpCeilingWho != 0
            && (!_players.TryGetValue(_warpCeilingWho, out Player? hit) || (hit.Can > 0.0 && hit.Can > _warpCeiling)))
        {
            (_warpCeiling, _warpCeilingWho) = (double.PositiveInfinity, 0);
        }

        if (double.IsFinite(room) && room < at * GovernFall) (next, limits) = (Math.Max(1.0, room), roomWho);
        // Nobody has hit a ceiling, but somebody is losing ground all the same: back off and watch.
        else if (late > GovernLateSeconds) (next, limits) = (Math.Max(1.0, at * GovernBackOff), lateWho);
        // Room to try: a machine's limit is only found by asking it for a little more, and never
        // again for as much as has already been found not to work.
        else if (at < _warpWanted && late < GovernLateSeconds * 0.5) next = Math.Min(Math.Min(_warpWanted, at * GovernStep), Math.Min(room, _warpCeiling));
        if (Math.Abs(next - at) < 0.01) return;

        // What it fell from is as fast as this warp goes from here, unless the machine it fell for
        // says otherwise or its asker asks again.
        if (next < at) (_warpCeiling, _warpCeilingWho) = (Math.Max(1.0, at * GovernFall), limits);
        RunWarp((float)next, now);
        Log(next < at
            ? $"the world falls to {next:0.##}x: {(double.IsFinite(room) ? $"the room can fly {room:0.##}x" : $"somebody is {late:0.#} s behind")}"
            : $"the world rises to {next:0.##}x of {_warpWanted:0.##}x");
    }

    /// <summary>
    /// What a player is flying, told to everyone the moment it changes rather than at the next
    /// roster: a name still hanging over the craft its player has just stepped out of is the whole
    /// of what anybody notices.
    /// </summary>
    /// <summary>
    /// Craft their owners have walked away from, given back. Ownership is sticky so that two games
    /// never pull one hull in two directions -- but sticky for ever meant the first player to touch
    /// a thing held it until they left, and a second player could fly nothing at all.
    /// </summary>
    private void Lapse(double now)
    {
        bool any = false;
        foreach ((string key, Craft craft) in _crafts)
        {
            if (craft.Owner == 0) continue;
            if (!_players.TryGetValue(craft.Owner, out Player? owner))
            {
                continue;
            }
            if (owner.Controlling == key || owner.Watching == key)
            {
                craft.AttendedAt = now;
                continue;
            }
            if (now - craft.AttendedAt < Options.LapseWithin) continue;
            // Only a craft somebody else is waiting on. Most of a world is parked craft nobody is
            // looking at, and letting those go handed them round hundreds of times an hour, each
            // time moving the camera of whoever happened to be watching one.
            if (_players.Values.FirstOrDefault(q => q.Id != owner.Id && q.Loaded && q.Watching == key) is not { } waiting) continue;

            craft.Owner = 0;
            any = true;
            Log($"{key} is {waiting.Name}'s to take: {owner.Name} has not been near it for {Options.LapseWithin:0} s");
        }
        if (any) OwnersChanged();
    }

    private void Flying(Player p, string key)
    {
        if (p.Controlling == key) return;
        p.Controlling = key;
        _rosterNext = 0.0;
    }

    private void Remove(Player p, string why)
    {
        if (!_players.Remove(p.Id)) return;
        _udp.Send(new Wire.Bye(why == "has left" ? "" : why), p.At);
        bool released = false;
        foreach (Craft c in _crafts.Values.Where(c => c.Owner == p.Id))
        {
            c.Owner = 0;
            released = true;
        }
        if (released) OwnersChanged();
        // Gone is not an answer, but it is no longer a vote anybody waits on: a warp waiting only on
        // somebody who has left would otherwise sit there until it ran out.
        _warpVotes.Remove(p.Id);
        // The question goes with whoever asked it, while it is still being answered as well as once
        // it is running. Left standing it belonged to a player who was not there -- "somebody's
        // warp" in the log -- and the next answer from anybody ended it in their name. A game that
        // dropped and came straight back then said no to a warp its own lost session had asked for,
        // which reads as a player blocking their own warp.
        if (_warpPhase != Wire.WarpPhase.None && p.Id == _warpWho)
        {
            EndWarp(_clock(), $"{p.Name} asked for the warp and has gone");
        }
        else if (_warpPhase == Wire.WarpPhase.Asking)
        {
            Counted(_clock());
        }
        _rosterNext = 0.0;
        Log($"{p.Name} {why}");
        Broadcast(new Wire.Chat(0, $"{p.Name} {why}"), _ => true);
        // What this player did since the last save is in nobody's world once they are gone, so one of
        // those still here is asked for a fresh one. Nothing can be asked of the last to leave: that
        // much is lost, and the clock waits at the world rather than pretending otherwise.
        if (p.Loaded && _players.Values.Any(q => q.Loaded)) AskSnapshot($"{p.Name} {why}");
        // The one asked for the first world went before it came: the next to join is asked.
        if (p.Seeding && _world is null && _players.Values.FirstOrDefault(q => q.WaitingForWorld) is { } next)
        {
            (next.Seeding, next.WaitingForWorld) = (true, false);
            AskWorldOf(next, "the server has no world yet: yours becomes it");
        }
    }

    /// <summary>Puts a player out, as an operator would from the console.</summary>
    /// <summary>Says something to one player, which their game shows in the chat as the server speaking.</summary>
    public void Tell(Player p, string text) => _udp.Send(new Wire.Chat(0, Wire.Clip(text, Wire.ChatChars)), p.At);

    /// <summary>
    /// Makes a player who is here an admin and hands their game the token to keep, returning the name
    /// it went to. Null for a name nobody is playing under: the token has to reach a game, and it is
    /// said once and never stored where it could be said again.
    /// </summary>
    public string? Op(string name)
    {
        if (Find(name) is not { } p) return null;
        string token = Admins.Add(p.Name);
        p.Admin = true;
        _udp.Send(new Wire.Op(true, token), p.At);
        Tell(p, "you are an admin here; type /help in the chat for what you can ask");
        Log($"{p.Name} is an admin from now");
        return p.Name;
    }

    /// <summary>
    /// Takes admin away from a name, whether or not they are playing. The token stops working at once,
    /// and a game that is here is told to throw the one it kept away.
    /// </summary>
    public bool Deop(string name)
    {
        bool was = Admins.Remove(name) > 0;
        // Every player of that name and not the first one found: the stored hash is taken away by
        // name without regard to case, so clearing the flag on one of two would leave the other
        // playing as an admin while the console said otherwise.
        foreach (Player p in _players.Values
                     .Where(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            was |= p.Admin;
            p.Admin = false;
            _udp.Send(new Wire.Op(false, ""), p.At);
            Tell(p, "you are no longer an admin here");
        }
        if (was) Log($"{name} is no longer an admin");
        return was;
    }

    private Player? Find(string name) =>
        _players.Values.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool Kick(string name, string why)
    {
        if (Find(name) is not { } p) return false;
        Remove(p, $"was kicked{(why.Length > 0 ? $": {why}" : "")}");
        return true;
    }

    /// <summary>Says something to every player, from the console.</summary>
    public void Announce(string text) => Broadcast(new Wire.Chat(0, Wire.Clip(text, Wire.ChatChars)), _ => true);

    /// <summary>Asks for a fresh save of the world now, from the console.</summary>
    public void SnapshotNow() => AskSnapshot("the operator asked for a save");

    /// <summary>
    /// Every player put back on the world the server holds, without anybody reconnecting: each game
    /// loads it again and says when it is in. The number of players it went to.
    ///
    /// <para>This is also the only way universe time is ever put back. A game's own time comes down
    /// by loading a world and by nothing else, and while they all load there is nobody in the world,
    /// so the server's clock goes back to the world's own time the same moment theirs do -- see
    /// <see cref="HoldTimeAtTheWorld"/>. Setting the clock back any other way would leave every game
    /// ahead of the server and standing still until it caught up.</para>
    /// </summary>
    public int ReloadAll(string why)
    {
        if (_world is null) return 0;
        Player[] back = [.. _players.Values.Where(p => p.Loaded || p.WaitingForWorld)];
        foreach (Player p in back)
        {
            p.Loaded = false;
            SendWorld(p);
        }
        if (back.Length > 0)
        {
            Log($"the world goes out to {back.Length} player{(back.Length == 1 ? "" : "s")} again: {why}");
            Broadcast(new Wire.Chat(0, $"the world is being handed out again: {why}"), _ => true);
            _rosterNext = 0.0;
        }
        return back.Length;
    }

    /// <summary>
    /// Craft swept out of the world by the operator: every one nobody is flying, or only those whose
    /// name matches, taken away in every game and forgotten here. The names swept, for the console.
    ///
    /// <para>A craft somebody is flying is left where it is whatever was asked for: a world that
    /// pulls the hull out from under a player is worse than a world with debris in it. What is
    /// watched is not spared -- a camera is how an operator finds the debris in the first place.</para>
    /// </summary>
    public List<string> Sweep(string like = "")
    {
        HashSet<string> flown = [.. _players.Values.Where(p => p.Controlling.Length > 0).Select(p => p.Controlling)];
        List<string> swept = [];
        double universe = UniverseClock.Now(_clock());
        foreach ((string key, Craft _) in _crafts.ToArray())
        {
            if (flown.Contains(key) || !Wire.Matches(key, like)) continue;
            _crafts.Remove(key);
            _gone[key] = universe;
            swept.Add(key);
        }
        // The name, not the list: most of what is out there was never claimed by anybody, so the
        // server has never heard of it and a list drawn up here would miss the whole of the debris.
        // Each game matches the name against its own world, and what the server knew of them is
        // dropped above so that nothing hands them back.
        Broadcast(new Wire.Swept(like), p => p.Loaded);
        OwnersChanged();
        Log($"the operator is sweeping the world by '{(like.Length > 0 ? like : "*")}'"
            + $"{(swept.Count > 0 ? $"; {swept.Count} of them were known here: {Named(swept)}" : "")}");
        Broadcast(new Wire.Chat(0, "the world is being cleared of craft nobody is flying"), p => p.Loaded);
        // The world on disk still holds them, so the next player to join would be handed them all
        // back: this is the one moment it is worth saving afresh. The save comes from a game that
        // has already swept, so it is clean, and nobody has to be put back on it -- everybody is
        // standing in a world they swept themselves. Clearing therefore costs nobody a reload and
        // does not touch the clock; reload is there for an operator who wants that, and says so.
        AskSnapshot("the world was cleared of craft nobody was flying");
        return swept;
    }

    private static string Named(List<string> keys) =>
        string.Join(", ", keys.Take(6)) + (keys.Count > 6 ? $" and {keys.Count - 6} more" : "");

    // Craft defined and gone before this were saved with the world, and need not be handed out again.
    private void Prune(double universe)
    {
        foreach ((string key, Craft c) in _crafts.ToArray())
        {
            if (c.Definition is not null && c.Universe < universe - SaveMarginSeconds) c.Definition = null;
            if (c.Definition is null && c.Owner == 0) _crafts.Remove(key);
        }
        foreach ((string key, double at) in _gone.ToArray()) if (at < universe - SaveMarginSeconds) _gone.Remove(key);
    }

    private Wire.Roster Roster() =>
        new([.. _players.Values.OrderBy(p => p.Id).Select(p => new Wire.Player(p.Id, p.Name, p.Loaded, p.Controlling, p.Rtt, (float)p.Can))]);

    private IEnumerable<Wire.Owners> OwnerPages()
    {
        Wire.Owner[] all = [.. _crafts.Where(c => c.Value.Owner != 0).Select(c => new Wire.Owner(c.Key, c.Value.Owner))];
        // Filled by what they come to in bytes and not only by how many they hold: a key is clipped
        // to a number of characters and a character is up to three bytes, so a full page of names in
        // most of the world's writing systems did not fit the datagram it was sent in.
        List<List<Wire.Owner>> pages = [[]];
        int bytes = 0;
        foreach (Wire.Owner e in all)
        {
            int cost = Wire.OwnerBytes(e.Key);
            if (pages[^1].Count > 0 && (pages[^1].Count >= Wire.OwnersPerPage || bytes + cost > Wire.OwnersBudgetBytes))
            {
                pages.Add([]);
                bytes = 0;
            }
            pages[^1].Add(e);
            bytes += cost;
        }
        for (int i = 0; i < pages.Count; i++) yield return new Wire.Owners((ushort)i, (ushort)pages.Count, [.. pages[i]]);
    }

    private void Broadcast(Wire.Message m, Func<Player, bool> to)
    {
        foreach (Player p in _players.Values) if (to(p)) _udp.Send(m, p.At);
    }

    /// <summary>
    /// A name no other player holds. Held case-insensitively, because every operator verb resolves a
    /// name that way: with "laurens" and "Laurens" both on a roster, op handed the token to whichever
    /// of them joined first and deleted the other's row, and deop took the stored hash away while
    /// clearing the admin flag on only one of the two -- so it answered "no longer an admin" to an
    /// operator whose admin still had every right until they rejoined.
    ///
    /// <para>This makes the roster unambiguous. It does not stop a name being taken: whoever joins
    /// first gets it, and the player who meant to have it becomes "name 2", so op still names
    /// whoever is holding the name rather than whoever deserves it.</para>
    /// </summary>
    private string UniqueName(string name)
    {
        string root = name.Length > 0 ? name : "Kitten";
        string unique = root;
        for (int k = 2; _players.Values.Any(p => string.Equals(p.Name, unique, StringComparison.OrdinalIgnoreCase)); k++)
        {
            unique = $"{root} {k}";
        }
        return unique;
    }

    private void Log(string what) => Said.Enqueue(what);

    /// <summary>Says goodbye to everyone and closes the socket.</summary>
    public void Dispose()
    {
        foreach (Player p in _players.Values) _udp.Send(new Wire.Bye("the server is shutting down"), p.At);
        _udp.Dispose();
    }
}
