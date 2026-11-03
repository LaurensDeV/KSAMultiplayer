using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using KSA;

namespace KSAMultiplayer;

/// <summary>
/// This game in a shared world, once a frame from the frame hook: joining a server and taking its
/// world on, keeping the server's universe time, sending the craft this player flies and putting
/// every other player's craft where its game says it is.
///
/// <para>A craft is flown by one player at a time, the server says whom. A player owns what it takes
/// control of, unless someone else is flying it, and what its own game makes: a craft launched, a
/// stage dropped, a kitten out on EVA. What is owned is sent; what someone else owns is a puppet
/// here, pulled to its states in the physics window, and built from its definition if this world has
/// never had it. What nobody owns is left to each world, where a craft on rails goes the same way in
/// every one.</para>
///
/// <para>Never throws to the frame hook.</para>
/// </summary>
internal static class Sync
{
    public const int DefaultPort = 50100;

    // The craft being flown is sent this often, and any other owned craft this often.
    private const double ControlledEverySeconds = 1.0 / 20.0, OwnedEverySeconds = 0.5;

    // A claim is said again this often, since a datagram can be lost.
    private const double ClaimEverySeconds = 2.0;

    // A puppet with no physics here is placed by its orbit or on the ground at most this often, and only when this far out.
    private const double PlaceEverySeconds = 1.0, PlaceNearEnoughM = 25.0;

    // A puppet whose owner has said nothing for this long of universe time is let go of and left where it is.
    private const double LostSeconds = 5.0;

    // Frames a loaded world is given to settle before it is played in.
    private const int SettleFrames = 30;

    // How long a world is given to load, s.
    private const double LoadWithinSeconds = 180.0;

    private const int ChatKept = 50, SaidKept = 8;

    public enum Stage { Off, Joining, Seeding, Waiting, Loading, Playing }

    private sealed class Mine(Vehicle craft)
    {
        public Vehicle Craft { get; } = craft;
        public int Shape { get; set; } = World.Shape(craft);
        public double SendNext { get; set; }
    }

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly Dictionary<string, byte[]> Definitions = [];
    private static readonly Dictionary<string, Mine> Owned = [];
    private static readonly Dictionary<Vehicle, string> Puppets = [];
    private static readonly Dictionary<string, double> PlacedAt = [];
    private static readonly HashSet<string> Known = [];
    private static readonly HashSet<string> Conflicts = [];

    // Craft this game made itself, which are never another player's puppet here, whatever the server says of their name.
    private static readonly HashSet<Vehicle> MadeHere = [];
    private static readonly List<string> ChatLines = [];
    private static readonly List<string> SaidLines = [];

    private static Client? _client;
    private static Task<IPEndPoint?>? _finding;
    private static (string Name, string Password)? _asking;
    private static double _frameAt, _loadingSince, _claimNext;
    private static string _claimed = "";
    private static int _settle;

    public static Stage Now { get; private set; } = Stage.Off;
    public static double FrameSeconds { get; private set; }

    /// <summary>The speed every warp and pause is held to while connected, or null when this game's is its player's. Read inside KSA's own calls.</summary>
    public static double? HeldSpeed { get; private set; }

    /// <summary>How far this world's universe time is behind the server's, s.</summary>
    public static double? Behind { get; private set; }

    public static Client? Client => _client;
    public static IReadOnlyList<string> Chat => ChatLines;
    public static IReadOnlyList<string> Said => SaidLines;
    public static IReadOnlyCollection<string> ConflictingCraft => Conflicts;

    public static double Wall() => Clock.Elapsed.TotalSeconds;

    /// <summary>What the panel shows under the server's name.</summary>
    public static string Status => Now switch
    {
        Stage.Off => "not connected",
        Stage.Joining => "connecting...",
        Stage.Seeding => "sending your world to the server...",
        Stage.Waiting => _client?.Arriving is { } share ? $"receiving the world: {share * 100:F0}%" : "waiting for the world...",
        Stage.Loading => "loading the world...",
        Stage.Playing => Behind is { } b && Math.Abs(b) > ClockFollow.CatchUpAboveSeconds ? $"catching up: {b:F0} s behind" : "playing",
        _ => "",
    };

    public static bool IsPuppet(Vehicle v) => Puppets.ContainsKey(v);

    /// <summary>Connects to a server at "host:port" or "host"; this world is then the server's, or becomes it.</summary>
    public static void Connect(string address, string name, string password)
    {
        Disconnect();
        if (!TryAddress(address, out string host, out int port))
        {
            Say("that is not an address: give host:port, or a host for port 50100");
            return;
        }
        try
        {
            _client = new Client(Wall);
        }
        catch (SocketException e)
        {
            Say($"could not open a socket: {e.Message}");
            return;
        }
        _asking = (name, password);
        _finding = Find(host, port);
        Now = Stage.Joining;
        Say($"looking up {host}...");
    }

    public static void Disconnect()
    {
        bool was = _client is not null;
        _client?.Dispose();
        _client = null;
        _finding = null;
        foreach (Vehicle v in Puppets.Keys) Log.Info($"let go of {v.Id}");
        Puppets.Clear();
        Owned.Clear();
        Known.Clear();
        Conflicts.Clear();
        MadeHere.Clear();
        Definitions.Clear();
        PlacedAt.Clear();
        (_claimed, Behind, Now) = ("", null, Stage.Off);
        if (HeldSpeed is not null)
        {
            HeldSpeed = null;
            if (World.InFlight) World.SetSpeed(1.0);
        }
        if (was) Say("disconnected");
    }

    public static void SayChat(string text) => _client?.SayChat(text);

    /// <summary>Once a frame, from the frame hook.</summary>
    public static void Update()
    {
        double now = Wall();
        FrameSeconds = _frameAt > 0 ? Math.Min(now - _frameAt, 1.0) : 0.0;
        _frameAt = now;
        if (_client is not { } c) return;
        try
        {
            if (_finding is { IsCompleted: true } finding)
            {
                _finding = null;
                if (finding.IsCompletedSuccessfully && finding.Result is { } at && _asking is { } ask)
                {
                    c.Connect(at, ask.Name, World.KsaBuild, World.Mods, ask.Password);
                    Log.Info($"connecting to {at}");
                }
                else
                {
                    Say("that server could not be found");
                    Disconnect();
                    return;
                }
            }
            c.Update();
            while (c.Said.TryDequeue(out string? said)) Say(said);
            while (c.Chat.TryDequeue(out (string Who, string Text) line)) Chatted($"<{line.Who}> {line.Text}");
            if (c.Out.Length > 0)
            {
                Disconnect();
                return;
            }
            if (!c.Joined) return;

            switch (Now)
            {
                case Stage.Joining:
                    Now = c.ServerHasWorld ? Stage.Waiting : Stage.Seeding;
                    if (Now == Stage.Seeding) Say("the server has no world yet: yours becomes it");
                    break;
                case Stage.Seeding when c.SnapshotWanted is not null && World.InFlight:
                    c.SnapshotWanted = null;
                    SendWorld(c);
                    StartPlaying(c);
                    break;
                case Stage.Waiting:
                    TakeWorld(c);
                    break;
                case Stage.Loading:
                    if (Wall() - _loadingSince > LoadWithinSeconds)
                    {
                        Say("the world did not load");
                        Disconnect();
                        return;
                    }
                    if (World.InFlight && ++_settle > SettleFrames) StartPlaying(c);
                    break;
                case Stage.Playing:
                    Play(c);
                    break;
            }
            HoldTheClock(c);
        }
        catch (Exception e)
        {
            Say($"multiplayer went wrong: {e.GetBaseException().Message}");
            Log.Error("multiplayer failed", e);
        }
    }

    private static void TakeWorld(Client c)
    {
        while (c.Blobs.TryDequeue(out Blob? blob))
        {
            if (blob.Kind == BlobKind.Vehicle) Definitions[blob.Key] = blob.Data;
            if (blob.Kind != BlobKind.World) continue;
            Say($"the world has come, {blob.Data.Length / 1024} KB: loading it");
            (_loadingSince, _settle, Now) = (Wall(), 0, Stage.Loading);
            Puppets.Clear();
            MadeHere.Clear();
            World.LoadWorld(blob.Data);
            return;
        }
    }

    private static void StartPlaying(Client c)
    {
        Known.Clear();
        foreach (Vehicle v in World.Vehicles()) Known.Add(v.Id);
        c.ForgetTracks();
        c.SayLoaded(World.Universe);
        Now = Stage.Playing;
        Say("in the shared world");
    }

    private static void SendWorld(Client c)
    {
        (byte[] zip, double universe) = World.SaveWorld();
        c.Upload(new Blob(BlobKind.World, "", c.Id, universe, zip));
        Log.Info($"sent the server a save of this world, {zip.Length / 1024} KB at {universe:F1} s");
    }

    private static void Play(Client c)
    {
        if (c.SnapshotWanted is { } why)
        {
            c.SnapshotWanted = null;
            Log.Info($"the server asks for a save: {why}");
            SendWorld(c);
        }

        Dictionary<string, Vehicle> here = [];
        foreach (Vehicle v in World.Vehicles()) here.TryAdd(v.Id, v);
        Vehicle? flown = World.Controlled;

        while (c.Blobs.TryDequeue(out Blob? blob))
        {
            if (blob.Kind != BlobKind.Vehicle || blob.Key.Length == 0) continue;
            Definitions[blob.Key] = blob.Data;
            // A craft rebuilt by its owner, as one that drops a stage is: the old one here goes, and the new one is built below.
            if (here.TryGetValue(blob.Key, out Vehicle? old) && !Owned.ContainsKey(blob.Key) && !ReferenceEquals(old, flown))
            {
                Puppets.Remove(old);
                World.Destroy(old);
                here.Remove(blob.Key);
            }
            else if (old is not null && Owned.ContainsKey(blob.Key) && Conflicts.Add(blob.Key))
            {
                Say($"another player has a craft called '{blob.Key}' too: rename yours for them to see it");
            }
        }
        while (c.Gone.TryDequeue(out string? key))
        {
            Definitions.Remove(key);
            if (here.TryGetValue(key, out Vehicle? gone) && !Owned.ContainsKey(key))
            {
                Puppets.Remove(gone);
                World.Destroy(gone);
                here.Remove(key);
            }
        }

        ClaimFlown(c, flown);
        Adopt(c, here, flown);
        SendOwned(c, here, flown);
        PlaceOthers(c, here, flown);
    }

    // The craft this player flies, claimed whenever it changes and now and then after.
    private static void ClaimFlown(Client c, Vehicle? flown)
    {
        string key = flown?.Id ?? "";
        if (key.Length == 0 || (key == _claimed && Wall() < _claimNext)) return;
        c.Claim(key, controlling: true);
        (_claimed, _claimNext) = (key, Wall() + ClaimEverySeconds);
        if (Known.Add(key) && flown is not null)
        {
            MadeHere.Add(flown);
            if (!Owned.ContainsKey(key) && c.OwnerOf(key) == 0) Own(c, flown, define: true);
        }
    }

    // Craft this game has made since it was last looked at: a launch, a dropped stage, a kitten on EVA.
    // Each is this player's when it is the one flown or nearer to this player's craft than to anyone else's.
    private static void Adopt(Client c, Dictionary<string, Vehicle> here, Vehicle? flown)
    {
        foreach ((string key, Vehicle v) in here)
        {
            if (Known.Contains(key) || Puppets.ContainsKey(v)) continue;
            Known.Add(key);
            MadeHere.Add(v);
            if (c.OwnerOf(key) is var owner && owner != 0 && owner != c.Id)
            {
                if (Conflicts.Add(key)) Say($"another player has a craft called '{key}' too: rename yours for them to see it");
                continue;
            }
            double mine = Owned.Values.Where(m => World.IsAlive(m.Craft)).Select(m => World.Between(m.Craft, v)).DefaultIfEmpty(double.PositiveInfinity).Min();
            double theirs = Puppets.Keys.Where(World.IsAlive).Select(p => World.Between(p, v)).DefaultIfEmpty(double.PositiveInfinity).Min();
            if (ReferenceEquals(v, flown) || mine < theirs) Own(c, v, define: true);
        }
    }

    private static void Own(Client c, Vehicle v, bool define)
    {
        Owned[v.Id] = new Mine(v);
        c.Claim(v.Id, controlling: ReferenceEquals(v, World.Controlled));
        if (define) Define(c, v);
    }

    private static void Define(Client c, Vehicle v)
    {
        try
        {
            c.Upload(new Blob(BlobKind.Vehicle, v.Id, c.Id, World.Universe, World.Define(v)));
            Log.Info($"sent the server how {v.Id} is built");
        }
        catch (Exception e)
        {
            Log.Warn($"{v.Id} could not be written down for the others: {e.GetBaseException().Message}");
        }
    }

    // Every craft this player owns, sent: often the one flown, seldom the rest; and any that is gone, said so.
    private static void SendOwned(Client c, Dictionary<string, Vehicle> here, Vehicle? flown)
    {
        foreach (string key in c.Owners.Where(o => o.Value == c.Id).Select(o => o.Key))
        {
            if (Owned.ContainsKey(key) || !here.TryGetValue(key, out Vehicle? v)) continue;
            // The server gave this player a craft it took control of: it is no puppet now.
            Puppets.Remove(v);
            Owned[key] = new Mine(v);
        }
        double now = Wall();
        foreach ((string key, Mine entry) in Owned.ToArray())
        {
            Mine m = entry;
            if (!here.TryGetValue(key, out Vehicle? v))
            {
                Owned.Remove(key);
                if (c.Owns(key)) c.SayGone(key);
                Conflicts.Remove(key);
                continue;
            }
            if (!ReferenceEquals(v, m.Craft))
            {
                // The same craft as another object, as KSA can rebuild one: still this player's, and described again.
                Owned[key] = m = new Mine(v) { Shape = 0 };
            }
            if (c.OwnerOf(key) is var owner && owner != 0 && owner != c.Id)
            {
                // Taken by another player, who is flying it now.
                Owned.Remove(key);
                continue;
            }
            int shape = World.Shape(v);
            if (shape != m.Shape)
            {
                m.Shape = shape;
                Define(c, v);
            }
            if (now < m.SendNext) continue;
            bool controlled = ReferenceEquals(v, flown);
            m.SendNext = now + (controlled ? ControlledEverySeconds : OwnedEverySeconds);
            if (World.StateOf(v, controlled) is { } state) c.SendState(state);
        }
    }

    // Every craft another player owns, as a puppet here: built from its definition if this world has none, pulled in physics, placed by its orbit without.
    private static void PlaceOthers(Client c, Dictionary<string, Vehicle> here, Vehicle? flown)
    {
        HashSet<Vehicle> wanted = [];
        bool builtOne = false;
        double universe = World.Universe, now = Wall();
        foreach ((string key, byte owner) in c.Owners)
        {
            if (owner == c.Id || Owned.ContainsKey(key)) continue;
            if (here.TryGetValue(key, out Vehicle? ours) && MadeHere.Contains(ours)) continue;
            Track track = c.TrackOf(key);
            if (!here.TryGetValue(key, out Vehicle? v))
            {
                // One a frame: each is a craft built.
                if (builtOne || track.Newest is null || !Definitions.TryGetValue(key, out byte[]? definition)) continue;
                builtOne = true;
                if (World.Build(definition, out string why) is { } built)
                {
                    Known.Add(key);
                    here[key] = v = built;
                    Log.Info($"built {key}, {c.NameOf(owner)}'s");
                }
                else
                {
                    Say($"{c.NameOf(owner)}'s {key} cannot be built here: {why}");
                    Definitions.Remove(key);
                    continue;
                }
            }
            if (universe - track.HeardAt > LostSeconds) continue;
            wanted.Add(v);
            Puppets[v] = key;
            if (track.At(universe) is not { } s) continue;
            World.SetLights(v, (s.Flags & Wire.StateFlags.Lights) != 0);
            if (!v.HasPhysicsBubble && now >= PlacedAt.GetValueOrDefault(key))
            {
                PlacedAt[key] = now + PlaceEverySeconds;
                World.PlaceAnalytic(v, s, PlaceNearEnoughM);
            }
        }
        foreach (Vehicle v in Puppets.Keys.Where(p => !wanted.Contains(p)).ToArray()) Puppets.Remove(v);
    }

    /// <summary>A puppet pulled to its owner's state, from the physics window.</summary>
    public static void Physics(Vehicle v, double dt)
    {
        if (!Puppets.TryGetValue(v, out string? key) || _client is not { } c) return;
        if (c.TrackOf(key).At(World.Universe) is { } s) World.Pull(v, s, dt);
    }

    // The speed this world runs at while connected: the server's time kept, warped forward to catch up after a load.
    private static void HoldTheClock(Client c)
    {
        if (Now is Stage.Loading or Stage.Joining || !World.InFlight)
        {
            // KSA's own load is left to run as it does.
            HeldSpeed = null;
            return;
        }
        if (Now != Stage.Playing || c.ServerUniverse is not { } server)
        {
            HeldSpeed = Now == Stage.Waiting ? 1.0 : null;
            if (HeldSpeed is { } h) World.SetSpeed(h);
            return;
        }
        double behind = server.Universe - World.Universe;
        Behind = behind;
        HeldSpeed = ClockFollow.Asked(ClockFollow.Speed(behind, server.Rate), FrameSeconds, World.StepCap);
        World.SetSpeed(HeldSpeed.Value);
    }

    private static async Task<IPEndPoint?> Find(string host, int port)
    {
        if (IPAddress.TryParse(host, out IPAddress? ip)) return new IPEndPoint(ip.MapToIPv4(), port);
        IPAddress[] found = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork);
        return found.Length > 0 ? new IPEndPoint(found[0], port) : null;
    }

    /// <summary>A server's address read: "host:port", or a host alone for the default port.</summary>
    public static bool TryAddress(string text, out string host, out int port)
    {
        text = text.Trim();
        (host, port) = (text, DefaultPort);
        int colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon)
        {
            host = text[..colon];
            if (!int.TryParse(text[(colon + 1)..], out port) || port is < 1 or > 65535) return false;
        }
        return host.Length > 0 && !host.Contains(' ');
    }

    private static void Say(string what)
    {
        Log.Info($"multiplayer: {what}");
        SaidLines.Add(what);
        if (SaidLines.Count > SaidKept) SaidLines.RemoveAt(0);
    }

    private static void Chatted(string line)
    {
        Log.Info($"chat: {line}");
        ChatLines.Add(line);
        if (ChatLines.Count > ChatKept) ChatLines.RemoveAt(0);
    }

    /// <summary>Says goodbye on the way out of the game; nothing of the world is touched, which is going with it.</summary>
    public static void Shutdown()
    {
        _client?.Dispose();
        _client = null;
    }
}
