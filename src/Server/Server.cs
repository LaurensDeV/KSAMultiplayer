using System.Net;

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

        /// <summary>Whether it has loaded the world and is playing in it, and since when on the server's clock.</summary>
        public bool Loaded { get; set; }
        public double LoadedAt { get; set; }

        /// <summary>Waiting for a world to be sent: there is none yet, or a fresh one has been asked for.</summary>
        public bool WaitingForWorld { get; set; }
        public double WaitingSince { get; set; }

        /// <summary>Asked for the first save of the world: the server had none when this player joined.</summary>
        public bool Seeding { get; set; }

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
    }

    private const double SilentSeconds = 10.0, LoadingSilentSeconds = 120.0;
    private const double RosterEverySeconds = 2.0, OwnersEverySeconds = 5.0, ClockEverySeconds = 0.2;

    // How long a player joining waits for a fresh save before it is sent the one there is.
    private const double FreshWithinSeconds = 15.0;

    // A world this old in universe time is saved afresh before it is handed to a player joining.
    private const double StaleSeconds = 30.0;

    // A craft defined or gone this long before a world was saved is in that save already.
    private const double SaveMarginSeconds = 10.0;

    private readonly Udp _udp;
    private readonly Func<double> _clock;
    private readonly WorldStore? _store;
    private readonly Dictionary<byte, Player> _players = [];
    private readonly Dictionary<string, Craft> _crafts = [];
    private readonly Dictionary<string, double> _gone = [];
    private Blob? _world;
    private string _worldBuild = "", _worldSystem = "";
    private string[] _worldMods = [];
    private double _rosterNext, _ownersNext, _clockNext, _snapshotNext, _snapshotAskedAt = double.NegativeInfinity;

    public GameServer(ServerOptions options, Func<double> clock, WorldStore? store = null, IPAddress? bind = null)
    {
        (Options, _clock, _store) = (options, clock, store);
        UniverseClock.Speed = options.Speed;
        _udp = new Udp(options.Port, clock, "KSAMultiplayer server", bind);
        if (store?.Load() is { } saved)
        {
            _world = saved.World;
            (_worldBuild, _worldMods, _worldSystem) = (saved.KsaBuild, saved.Mods, saved.System);
            if (saved.TimeKnown) UniverseClock.Set(saved.World.Universe, clock());
            Log($"world loaded from {store.Folder}: {saved.World.Data.Length / 1024} KB"
                + (saved.TimeKnown ? $" at universe time {saved.World.Universe:F0} s" : ", its universe time to come from the first player to load it"));
        }
        _snapshotNext = clock() + options.SnapshotEverySeconds;
    }

    public ServerOptions Options { get; }
    public UniverseClock UniverseClock { get; } = new();
    public int Port => _udp.Port;
    public Udp Socket => _udp;
    public IReadOnlyCollection<Player> Players => _players.Values;
    public bool HasWorld => _world is not null;
    public Blob? World => _world;
    public int CraftCount => _crafts.Count;

    /// <summary>What has happened, for the console.</summary>
    public Queue<string> Said { get; } = new();

    public byte OwnerOf(string key) => _crafts.TryGetValue(key, out Craft? c) ? c.Owner : (byte)0;

    /// <summary>Puts a world in place of the one there is, as an operator would from the console.</summary>
    /// <param name="timeKnown">False for a save copied in by hand, whose universe time the server cannot read: the first player to load it says it.</param>
    public void SetWorld(Blob world, string ksaBuild, string[] mods, string system, bool timeKnown = true)
    {
        _world = world;
        (_worldBuild, _worldMods, _worldSystem) = (ksaBuild, mods, system);
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

        UniverseClock.Run(Options.KeepTime || _players.Values.Any(p => p.Loaded), now);

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

        if (now >= _rosterNext)
        {
            Broadcast(Roster(), _ => true);
            _rosterNext = now + RosterEverySeconds;
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
                _udp.Send(new Wire.Pong(ping.Sent, _clock()), from);
                return;
            case Wire.Loaded loaded:
                OnLoaded(player, loaded.Universe);
                return;
            case Wire.State state when player.Loaded:
                if (Grant(player, state.Key) is null) return;
                Wire.State relayed = state with { Owner = player.Id };
                Broadcast(relayed, p => p.Loaded && p != player);
                return;
            case Wire.Claim claim when player.Loaded && claim.Key.Length > 0:
                bool had = OwnerOf(claim.Key) == player.Id;
                // Only once it is theirs: a player refused a craft is not flying it, whatever they asked.
                if (Grant(player, claim.Key, taking: claim.Controlling, made: claim.Made) is not { } granted) return;
                if (claim.Controlling) player.Controlling = granted;
                if (!had) OwnersChanged();
                return;
            case Wire.Gone gone when player.Loaded && OwnerOf(gone.Key) == player.Id:
                _crafts.Remove(gone.Key);
                _gone[gone.Key] = UniverseClock.Now(_clock());
                if (player.Controlling == gone.Key) player.Controlling = "";
                Broadcast(gone, p => p.Loaded && p != player);
                return;
            case Wire.Chat chat:
                string text = Wire.Clip(chat.Text, Wire.ChatChars);
                if (text.Length == 0) return;
                Log($"<{player.Name}> {text}");
                Broadcast(new Wire.Chat(player.Id, text), _ => true);
                return;
            case Wire.Bye:
                Remove(player, "has left");
                return;
            case Wire.Chunk or Wire.Need or Wire.Got:
                if (player.Link.Handle(message, arrived, m => _udp.Send(m, player.At)) is { } bytes) OnBlob(player, bytes);
                return;
        }
    }

    private void Admit(Wire.Hello hello, IPEndPoint from, double now)
    {
        string? refused = _players.Count >= Options.MaxPlayers ? $"the server is full: {Options.MaxPlayers} players"
            : Options.Password.Length > 0 && hello.Password != Options.Password ? "wrong password"
            : Refusal(hello);
        if (refused is not null)
        {
            _udp.Send(new Wire.Welcome(0, refused, Options.Name, false), from);
            Log($"refused {Wire.Clip(hello.Name, Wire.NameChars)} from {from}: {refused}");
            return;
        }
        byte id = 1;
        while (_players.ContainsKey(id)) id++;
        string name = UniqueName(Wire.Clip(hello.Name, Wire.NameChars));
        Player p = new(id, name, from, now)
        {
            KsaBuild = hello.KsaBuild,
            Mods = hello.Mods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            System = hello.System,
        };
        _players[id] = p;

        bool seeding = _world is null && _players.Values.All(q => !q.Seeding);
        _udp.Send(new Wire.Welcome(id, "", Options.Name, !seeding), from);
        Log($"{name} has joined from {from}{(seeding ? ", and their world becomes the server's" : "")}");
        Broadcast(new Wire.Chat(0, $"{name} has joined"), q => q != p);
        _rosterNext = 0.0;

        if (seeding)
        {
            p.Seeding = true;
            _udp.Send(new Wire.Snapshot("the server has no world yet: yours becomes it"), from);
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
        string build = Options.KsaBuild.Length > 0 ? Options.KsaBuild : _worldBuild;
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
        return missing.Length > 0 ? $"the server's world needs mods you do not have enabled: {string.Join(", ", missing)}" : null;
    }

    private void SendWorld(Player p)
    {
        if (_world is null) return;
        p.WaitingForWorld = false;
        p.Link.Send(_world.Encode(), _clock());
        Log($"sending {p.Name} the world, {_world.Data.Length / 1024} KB");
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
                // Only a world asked for: anyone else's is theirs.
                if (!p.Seeding && !p.Loaded) return;
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
            _crafts[fresh] = new Craft { Owner = p.Id };
            _gone.Remove(fresh);
            OwnersChanged();
            _udp.Send(new Wire.Rename(key, fresh), p.At);
            Log($"{holder.Name} already has a craft called {key}, so {p.Name}'s is {fresh} here");
            return fresh;
        }
        craft.Owner = p.Id;
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

    private void AskSnapshot(string why)
    {
        // The player in the world longest, whose world has had the most time to settle.
        if (_players.Values.Where(p => p.Loaded).OrderBy(p => p.LoadedAt).FirstOrDefault() is not { } from) return;
        _snapshotAskedAt = UniverseClock.Now(_clock());
        _udp.Send(new Wire.Snapshot(why), from.At);
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
        _rosterNext = 0.0;
        Log($"{p.Name} {why}");
        Broadcast(new Wire.Chat(0, $"{p.Name} {why}"), _ => true);
        // The one asked for the first world went before it came: the next to join is asked.
        if (p.Seeding && _world is null && _players.Values.FirstOrDefault(q => q.WaitingForWorld) is { } next)
        {
            (next.Seeding, next.WaitingForWorld) = (true, false);
            _udp.Send(new Wire.Snapshot("the server has no world yet: yours becomes it"), next.At);
        }
    }

    /// <summary>Puts a player out, as an operator would from the console.</summary>
    public bool Kick(string name, string why)
    {
        if (_players.Values.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } p) return false;
        Remove(p, $"was kicked{(why.Length > 0 ? $": {why}" : "")}");
        return true;
    }

    /// <summary>Says something to every player, from the console.</summary>
    public void Announce(string text) => Broadcast(new Wire.Chat(0, Wire.Clip(text, Wire.ChatChars)), _ => true);

    /// <summary>Asks for a fresh save of the world now, from the console.</summary>
    public void SnapshotNow() => AskSnapshot("the operator asked for a save");

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
        new([.. _players.Values.OrderBy(p => p.Id).Select(p => new Wire.Player(p.Id, p.Name, p.Loaded, p.Controlling, p.Rtt))]);

    private IEnumerable<Wire.Owners> OwnerPages()
    {
        Wire.Owner[] all = [.. _crafts.Where(c => c.Value.Owner != 0).Select(c => new Wire.Owner(c.Key, c.Value.Owner))];
        int pages = Math.Max(1, (all.Length + Wire.OwnersPerPage - 1) / Wire.OwnersPerPage);
        for (int i = 0; i < pages; i++) yield return new Wire.Owners((ushort)i, (ushort)pages, [.. all.Skip(i * Wire.OwnersPerPage).Take(Wire.OwnersPerPage)]);
    }

    private void Broadcast(Wire.Message m, Func<Player, bool> to)
    {
        foreach (Player p in _players.Values) if (to(p)) _udp.Send(m, p.At);
    }

    private string UniqueName(string name)
    {
        string root = name.Length > 0 ? name : "Kitten";
        string unique = root;
        for (int k = 2; _players.Values.Any(p => p.Name == unique); k++) unique = $"{root} {k}";
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
