using System.Diagnostics;
using Brutal.Numerics;
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

    // A speed the player wants is said again this often while they still want it.
    private const double AskWarpEverySeconds = 1.0;

    // A puppet with no physics here is placed by its orbit or on the ground at most this often, and only when this far out.
    private const double PlaceEverySeconds = 1.0, PlaceNearEnoughM = 25.0;

    // A puppet whose owner has said nothing for this long of universe time is let go of and left where it is.
    // How long a craft may go unheard before it is let go of. Counted in the world's own seconds,
    // against a state sent on a real clock: at ten times speed half a second of sending is five
    // seconds of world, so the allowance is the world's speed times this rather than this alone.
    private const double LostSeconds = 5.0;

    // Frames a loaded world is given to settle before it is played in.
    private const int SettleFrames = 30;

    // How long a world is given to load, s.
    private const double LoadWithinSeconds = 180.0;

    private const int ChatKept = 50, SaidKept = 8;

    // The least time between two descriptions of one craft. Long enough that a rocket coming apart
    // is described a few times rather than twenty; short enough that a stage dropped on purpose is
    // in the others' worlds before anybody looks for it.
    private const double RedescribeEverySeconds = 2.0;

    // The least time between two rebuilds of one craft. A rebuild is not a correction, it is a
    // destruction and a fresh 119-part rocket dropped into the world, which lands on a pad and
    // explodes: watched on the real server, one craft built 87 times and destroyed 62 in a single
    // session, every copy making the fragments that made the next definition that made the next
    // copy. Longer than the sender's own interval, because it costs far more.
    private const double RebuildEverySeconds = 5.0;

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
    // What each puppet's owner last said its engines were doing. Kept because the frame hook and the
    // frame KSA draws are not the same moment: the plume is drawn from the render, after this mod has
    // run and from state a physics step would have left behind, so it is written there and read here.
    private static readonly Dictionary<Vehicle, (bool On, float Throttle)> Engines = [];
    // Another player's craft built here, by what the shared world calls it. World.Vehicles() does not
    // list a craft the moment it is made, so asking the world whether a craft is here answers no for
    // a frame or two after it was built, and the answer is taken as leave to build it again. This is
    // what was built, and it is believed over the world until the craft is gone.
    private static readonly Dictionary<string, Vehicle> Built = [];
    // Every craft this world has had since it was loaded, by craft rather than by name: one name can
    // belong to two craft here at once -- this player's kitten and the one another player sent out of
    // the same capsule -- and what matters is which object was already here, not what it is called.
    private static readonly HashSet<Vehicle> Seen = [];

    // What the shared world calls a craft, when that is not what this world calls it: two games that
    // each send a kitten out of the same capsule both call it what KSA calls it, and the server gives
    // the second one a name of its own. By craft rather than by name, because the two names this is
    // between are exactly the ones that collide: this world can hold its own Nasley and another
    // player's, and they are not the same craft.
    private static readonly Dictionary<Vehicle, string> Renamed = [];

    // Craft this game made itself, which are never another player's puppet here, whatever the server says of their name.
    private static readonly HashSet<Vehicle> MadeHere = [];
    /// <param name="NotAPlayer">Said by the server or by this game rather than by somebody playing,
    /// which the chat window draws in a colour of its own.</param>
    public readonly record struct Line(bool NotAPlayer, string Text);

    private static readonly List<Line> ChatLines = [];

    /// <summary>What was last said about a craft, when, and what is waiting to be said next.</summary>
    private sealed class Description
    {
        public byte[]? Sent;
        public double At;
        public byte[]? Waiting;
    }

    private static readonly Dictionary<string, Description> Describes = [];

    /// <summary>When each craft standing here was last torn down and built again.</summary>
    private static readonly Dictionary<string, double> Rebuilt = [];

    /// <summary>
    /// Craft waiting to be put into this world, by the name the shared world calls them.
    ///
    /// <para>Building one adds it to its body's <c>Children</c> and to the system's lookup, and every
    /// mod hook runs while the vehicle worker queued by <c>ExecuteNextVehicleSolvers</c> is still
    /// running -- walking those very collections. Adding one then is a list modified under an
    /// enumerator, which KSA reports as "Update task failed for vehicle(s)" and an
    /// InvalidOperationException out of PhysicsBubble, for every craft in the bubble at once. So the
    /// building is asked for here and done in <see cref="Settle"/>, inside the frame's one quiet
    /// window.</para>
    /// </summary>
    private static readonly Dictionary<string, (byte[] Definition, string Owner)> ToBuild = [];

    /// <summary>
    /// Craft waiting to be taken out of this world, for the same reason as <see cref="ToBuild"/>.
    ///
    /// <para>KSA-FRAME-ORDER.md says a mod may destroy a vehicle from its hooks, and that is about a
    /// destroyed craft's staged results having nowhere to land. It is not about the list the physics
    /// bubble walks: taking a vehicle out of that while a worker is mid-step is the same modification
    /// under the same enumerator as putting one in. Building alone was moved first and the failures
    /// carried on, with another player's rocket coming apart and its pieces being swept as fast as
    /// they arrived.</para>
    /// </summary>
    private static readonly List<Vehicle> ToRemove = [];
    private static readonly List<string> SaidLines = [];

    private static Client? _client;
    private static Task<IPEndPoint?>? _finding;
    private static (string Name, string Password)? _asking;
    // A world that has come and cannot be loaded yet, held rather than dropped: the server sends it
    // once, and KSA loads nothing while the vehicle editor is open.
    private static Blob? _worldToLoad;
    private static string _notLoading = "", _notSaving = "";
    private static double _frameAt, _loadingSince, _claimNext;
    private static string _claimed = "", _address = "", _watching = "";
    private static double _askedSpeed = 1.0, _askWarpNext;
    private static int _settle;

    public static Stage Now { get; private set; } = Stage.Off;
    public static double FrameSeconds { get; private set; }

    /// <summary>The speed every warp and pause is held to while connected, or null when this game's is its player's. Read inside KSA's own calls.</summary>
    public static double? HeldSpeed { get; private set; }

    /// <summary>How far this world's universe time is behind the server's, s.</summary>
    public static double? Behind { get; private set; }

    public static Client? Client => _client;
    public static IReadOnlyList<Line> Chat => ChatLines;
    public static IReadOnlyList<string> Said => SaidLines;

    /// <summary>
    /// The craft standing here that the others know by another name, as "here: there". Two games each
    /// send a kitten out of the same capsule and both call it what KSA calls it, so the server gives
    /// the second a name of its own; and a craft built here for another player takes a name this world
    /// has free when this world already has one of that name.
    /// </summary>
    public static IReadOnlyCollection<string> RenamedCraft =>
        [.. Renamed.Where(r => World.IsAlive(r.Key)).Select(r => $"{r.Key.Id}: {r.Value}")];

    /// <summary>The craft this player owns and sends, by Id.</summary>
    public static IReadOnlyCollection<string> OwnedCraft => Owned.Keys;

    /// <summary>The craft of other players standing in this world, by Id.</summary>
    public static IReadOnlyCollection<string> PuppetCraft => Puppets.Values;

    /// <summary>The server this game was told to join, as it was given.</summary>
    public static string ServerAddress => _address;

    public static double Wall() => Clock.Elapsed.TotalSeconds;

    /// <summary>
    /// A warp key, taken by which way it points rather than by the speed the game worked out from
    /// it. The game steps from the speed this world is running at, and while connected that is the
    /// shared warp nudged a hair off a round number to hold the server's clock -- a hair that lands
    /// the step on the rung the player is already on, or one past the one they wanted.
    /// </summary>
    /// <param name="direction">Up, down, or zero for the key that asks for normal speed.</param>
    /// <returns>Whether the mod took the key; false leaves it to the game, which is right when
    /// this world is nobody else's.</returns>
    /// <summary>The speed last asked of the server, for a test to read back.</summary>
    public static double AskedSpeed => _askedSpeed;

    public static bool StepSpeed(int direction)
    {
        if (_client is not { Joined: true } c || Now != Stage.Playing) return false;

        // The warp as the server has it, which is a round number, rather than as this world runs it;
        // and while this player is the one asking, the speed being asked for, so the keys go on
        // working through the question and the others watch the number they are answering change.
        double running = c.Warp switch
        {
            { Phase: Wire.WarpPhase.Running } w => w.Speed,
            { Phase: Wire.WarpPhase.Asking } w when w.Who == c.Id => w.Speed,
            _ => 1.0,
        };
        double next = direction == 0 ? 1.0 : World.WarpStepFrom(running, direction);
        // Either end of the ladder, or asked to stop a warp that is not running: taken and ignored.
        if (Math.Abs(next - running) >= 0.001)
        {
            (_askedSpeed, _askWarpNext) = (next, Wall() + AskWarpEverySeconds);
            c.AskWarp(next);
        }
        return true;
    }

    /// <summary>
    /// The player has reached for a speed, from a warp key or the pause. One world has one time, so
    /// this is put to everyone rather than done: the speed itself stays whatever keeps the server's.
    /// </summary>
    public static void WantSpeed(double speed)
    {
        if (_client is not { Joined: true } c || Now != Stage.Playing || !double.IsFinite(speed)) return;

        // Normal speed means nothing unless this player has a warp of their own to end with it.
        bool mine = c.Warp is { Phase: Wire.WarpPhase.Running } w && w.Who == c.Id;
        if (Math.Abs(speed - 1.0) < 0.01 && !mine) return;

        // Said again now and then, since a datagram can be lost, but not every frame a key is held.
        if (Math.Abs(speed - _askedSpeed) < 0.01 && Wall() < _askWarpNext) return;
        (_askedSpeed, _askWarpNext) = (speed, Wall() + AskWarpEverySeconds);
        c.AskWarp(speed);
    }

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

    /// <summary>
    /// A craft taken for this game before its crew is sent out, when nobody holds it. Two games
    /// looking at the same unclaimed capsule could each send the same kitten out of it, because a
    /// craft nobody owns is nobody's to refuse: the claim makes it somebody's, and the other game's
    /// hatch is shut within the tick.
    /// </summary>
    /// <returns>Whether this game may send the crew out: its own craft, or one just taken.</returns>
    /// <summary>
    /// A craft somebody has walked away from, taken by whoever is watching it and flying nothing.
    /// An observer is watching because they want to be in it; once it is nobody's there is nothing
    /// left to ask, and making them click a thing they are already looking at is ceremony.
    /// </summary>
    private static void TakeWhatIsWatched(Client c)
    {
        if (World.Controlled is not null || World.Watched is not { } v || !World.IsAlive(v)) return;

        string key = KeyOf(v);
        if (c.OwnerOf(key) != 0 || Owned.ContainsKey(key) || MadeHere.Contains(v)) return;

        // No longer anybody's puppet, or the hook that keeps players off each other's craft would
        // refuse this player their own.
        Puppets.Remove(v);
        Own(c, v, define: false, made: false);
        if (World.GoTo(v)) Say($"{key} is nobody's now: it is yours");
    }

    public static bool TakeBeforeEva(Vehicle v)
    {
        if (_client is not { Joined: true } c) return true;
        string key = KeyOf(v);
        if (Owned.ContainsKey(key)) return true;

        byte owner = c.OwnerOf(key);
        if (owner != 0 && owner != c.Id) return false;

        Own(c, v, define: false, made: false);
        return true;
    }

    /// <summary>What the shared world calls this craft, which is this game's own name for it unless the server renamed it.</summary>
    public static string KeyOf(Vehicle v) => Renamed.GetValueOrDefault(v, v.Id);

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
        (_asking, _address) = ((name, password), address);
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
        Built.Clear();
        Owned.Clear();
        Seen.Clear();
        Renamed.Clear();
        MadeHere.Clear();
        Definitions.Clear();
        Describes.Clear();
        Rebuilt.Clear();
        ToBuild.Clear();
        ToRemove.Clear();
        PlacedAt.Clear();
        Engines.Clear();
        World.ForgetEngines();
        (_worldToLoad, _notLoading, _notSaving) = (null, "", "");
        (_claimed, _address, _watching, Behind, Now) = ("", "", "", null, Stage.Off);
        (_askedSpeed, _askWarpNext) = (1.0, 0.0);
        if (HeldSpeed is not null)
        {
            // Wherever the player is, not only at a craft: a speed held for the shared world is
            // nobody's once this game is its own again, and one left standing is a world running at
            // ten times speed in front of a player who has disconnected.
            HeldSpeed = null;
            World.SetSpeed(1.0);
        }
        if (was) Say("disconnected");
    }

    public static void SayChat(string text) => _client?.SayChat(text);

    /// <summary>A console line for the server, as an admin types it into the chat behind a /.</summary>
    public static void Ask(string line) => _client?.Ask(line);

    /// <summary>
    /// A line typed into the chat, and whether it was a command rather than something said. A line
    /// behind a / goes to the server to run and is said to nobody, so a player who types one out of
    /// habit on a server where they are no admin has told the room nothing.
    /// </summary>
    public static bool Typed(string line)
    {
        string said = line.Trim();
        if (said.StartsWith('/'))
        {
            if (said.Length > 1 && !Here(said[1..])) Ask(said[1..]);
            return true;
        }
        if (said.Length > 0) SayChat(said);
        return false;
    }

    /// <summary>
    /// A command this game answers itself, rather than one for the server. Pointing a camera is
    /// nobody else's business and is no admin's privilege either, so it never leaves the machine.
    /// </summary>
    private static bool Here(string asked)
    {
        string[] parts = asked.Split(' ', 2, StringSplitOptions.TrimEntries);
        // Said before the server answers, and the line goes on to it all the same: the server's own
        // list is an admin's, and somebody who is no admin would otherwise never learn this one
        // exists. Tab fills in a name in any of them.
        if (parts[0] is "help" or "?")
        {
            Told("spectate <name>   put your camera on what somebody is flying; this one is your game's");
            Told("Tab               fill in a player's name from what you have typed of it");
            return false;
        }
        if (!parts[0].Equals("spectate", StringComparison.OrdinalIgnoreCase)
            && !parts[0].Equals("watch", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        Spectate(parts.Length > 1 ? parts[1] : "");
        return true;
    }

    /// <summary>Puts the camera on what a player is flying, without taking it over.</summary>
    private static void Spectate(string name)
    {
        if (_client is not { Joined: true } c) return;
        if (name.Length == 0)
        {
            Told("spectate <name>: whose craft to watch");
            return;
        }
        if (c.Players.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is not { Id: > 0 } who)
        {
            Told($"nobody called '{name}' is playing");
            return;
        }
        if (who.Controlling.Length == 0)
        {
            Told($"{who.Name} is not flying anything");
            return;
        }
        // By the name this world knows it under, which is not always the one the server uses: a craft
        // whose name was already taken here was built under another.
        if (World.Vehicles().FirstOrDefault(v => KeyOf(v) == who.Controlling) is not { } seen)
        {
            Told($"{who.Name} is flying {who.Controlling}, which is not in this world yet");
            return;
        }
        Told(World.Watch(seen)
            ? $"watching {who.Name}, flying {who.Controlling}"
            : $"could not put the camera on {who.Controlling}");
    }

    /// <summary>Whether this server runs what this game asks of it.</summary>
    public static bool Admin => _client is { Admin: true };

    /// <summary>Who flies this craft, or empty when it is this game's own or nobody is known to.</summary>
    public static string OwnerOf(Vehicle v)
    {
        if (_client is not { Joined: true } c) return "";
        byte owner = c.OwnerOf(KeyOf(v));
        return owner == 0 || owner == c.Id ? "" : c.NameOf(owner);
    }

    // Over a window rather than a frame: one slow frame is a hitch, and a hitch is not a machine's
    // measure. Long enough to mean something, short enough that letting go of a key is felt.
    private const double SustainOverSeconds = 2.0;

    private static double _sustainAt, _sustainFrom, _sustains = 1.0;

    /// <summary>
    /// The speed this game is managing to run its world at, a multiple of real time. What the world
    /// is warped to is held to the slowest of these, so a warp is never agreed that somebody's
    /// machine cannot fly.
    /// </summary>
    public static double Sustains => _sustains;

    private static void MeasureWhatThisMachineDoes(double now)
    {
        double universe = World.Universe;
        if (_sustainAt <= 0.0 || now - _sustainAt > SustainOverSeconds * 4.0)
        {
            (_sustainAt, _sustainFrom) = (now, universe);
            return;
        }
        double over = now - _sustainAt;
        if (over < SustainOverSeconds) return;

        double done = (universe - _sustainFrom) / over;
        (_sustainAt, _sustainFrom) = (now, universe);
        if (!double.IsFinite(done) || done < 0.0) return;

        // A ceiling, and only once there is one: a game comfortably inside its frame budget has not
        // found its limit, and what it happens to be running at is no measure of it. Saying 1x
        // because the world is at 1x would hold every room to 1x for ever.
        double asked = World.Speed, achieved = World.Achieved;
        _sustains = achieved < 0.98 && asked > 0.0 ? Math.Max(1.0, Math.Min(asked * achieved, done)) : 0.0;
    }

    /// <summary>Once a frame, from the frame hook.</summary>
    public static void Update()
    {
        double now = Wall();
        FrameSeconds = _frameAt > 0 ? Math.Min(now - _frameAt, 1.0) : 0.0;
        _frameAt = now;
        MeasureWhatThisMachineDoes(now);
        if (_client is { } told)
        {
            (told.Can, told.Here) = (_sustains, World.Focused);
            told.Watching = World.Watched is { } seen && World.IsAlive(seen) ? KeyOf(seen) : "";
        }
        if (_client is not { } c) return;
        try
        {
            if (_finding is { IsCompleted: true } finding)
            {
                _finding = null;
                if (finding.IsCompletedSuccessfully && finding.Result is { } at && _asking is { } ask)
                {
                    c.Connect(at, ask.Name, World.KsaBuild, World.Mods, World.System, ask.Password, Tokens.For(_address));
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
            while (c.Chat.TryDequeue(out Client.ChatLine line)) Chatted(line.FromServer, $"<{line.Who}> {line.Text}");
            while (c.Tokens.TryDequeue(out string? token)) Tokens.Keep(_address, token);
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
                case Stage.Seeding when c.SnapshotWanted is not null && World.AtACraft:
                    // In the shared world only once this world has become it. A game whose world was
                    // not handed over is in no world the server has, and waits for one from whoever
                    // the server asks next; null is an ask that cannot be answered yet.
                    switch (Snapshot(c))
                    {
                        case true: StartPlaying(c); break;
                        case false: Now = Stage.Waiting; break;
                    }
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
                    if (World.AtACraft && ++_settle > SettleFrames) StartPlaying(c);
                    break;
                case Stage.Playing:
                    Play(c);
                    TakeWhatIsWatched(c);
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
        while (_worldToLoad is null && c.Blobs.TryDequeue(out Blob? blob))
        {
            if (blob.Kind == BlobKind.Vehicle) Definitions[blob.Key] = blob.Data;
            if (blob.Kind == BlobKind.World) _worldToLoad = blob;
        }
        if (_worldToLoad is not { } world) return;
        if (World.Refuses() is { Length: > 0 } not)
        {
            if (_notLoading != not) Say($"the world has come, but it cannot be loaded: {not}");
            _notLoading = not;
            return;
        }
        (_notLoading, _worldToLoad) = ("", null);
        Say($"the world has come, {world.Data.Length / 1024} KB: loading it");
        (_loadingSince, _settle, Now) = (Wall(), 0, Stage.Loading);
        Puppets.Clear();
        Built.Clear();
        MadeHere.Clear();
        Engines.Clear();
        World.ForgetEngines();
        if (!World.LoadWorld(world.Data, out string why))
        {
            Say($"the world could not be loaded: {why}");
            Disconnect();
        }
    }

    private static void StartPlaying(Client c)
    {
        Renamed.Clear();
        Seen.Clear();
        foreach (Vehicle v in World.Vehicles()) Seen.Add(v);
        c.ForgetTracks();
        c.SayLoaded(World.Universe);
        Now = Stage.Playing;
        Say("in the shared world");
    }

    // The server has asked this game for a save of the world: sent, refused, or null while the ask
    // cannot be answered at all. KSA writes no save while the vehicle editor is open, and a player who
    // closes it a second later is still the one whose world this is -- so the ask is kept rather than
    // answered with a world that was never written.
    private static bool? Snapshot(Client c)
    {
        if (c.SnapshotWanted is not { } why) return null;
        if (World.Refuses() is { Length: > 0 } not)
        {
            if (_notSaving != not) Say($"the server wants a save of this world: {not}");
            _notSaving = not;
            return null;
        }
        _notSaving = "";
        c.SnapshotWanted = null;
        Log.Info($"the server asks for a save: {why}");
        return SendWorld(c);
    }

    private static bool SendWorld(Client c)
    {
        // A world holding a craft KSA has lost inside a body kills every game that loads it, on
        // every join, for as long as the server keeps it. The server is told no and asks somebody
        // else; what it already has is older but can be opened.
        if (World.LostCraft() is { Count: > 0 } lost)
        {
            string names = string.Join(", ", lost.Take(3)) + (lost.Count > 3 ? $" and {lost.Count - 3} more" : "");
            string why = $"{names} {(lost.Count == 1 ? "is" : "are")} inside the body {(lost.Count == 1 ? "it belongs" : "they belong")} to";
            c.SayNoWorld(why);
            Say($"this world is not fit to hand over: {why}");
            Log.Warn($"did not hand this world over: {why}");
            return false;
        }
        if (World.SaveWorld(out string not) is not { } save)
        {
            c.SayNoWorld(not);
            Say($"this world could not be saved for the others: {not}");
            Log.Warn($"did not hand this world over: {not}");
            return false;
        }
        c.Upload(new Blob(BlobKind.World, "", c.Id, save.Universe, save.Zip));
        Log.Info($"sent the server a save of this world, {save.Zip.Length / 1024} KB at {save.Universe:F1} s");
        return true;
    }

    private static void Play(Client c)
    {
        Settled(c);
        // Not while a sweep is waiting: it is applied further down this frame, and a save answered
        // first would be of the world as it was and hand the server back everything it just cleared.
        // One frame later the world is swept and the save is of that.
        if (c.Swept.Count == 0) Snapshot(c);
        TakeRenames(c);

        // By what the shared world calls each craft. Two of this world's craft can want one name for a
        // moment -- another player's kitten built here, and this player's own out of the same capsule
        // before the server has renamed it -- and the one that is already another player's keeps it.
        Dictionary<string, Vehicle> here = [];
        foreach (Vehicle v in World.Vehicles())
        {
            string k = KeyOf(v);
            if (!here.TryAdd(k, v) && (Puppets.ContainsKey(v) || Renamed.ContainsKey(v))) here[k] = v;
        }
        Vehicle? flown = World.Controlled;

        while (c.Blobs.TryDequeue(out Blob? blob))
        {
            // The world again: the operator has put everybody back on it. This game goes round the
            // loading once more rather than playing on in a world the others have left behind, and
            // its universe time comes back with the world's, which is the only way it ever can.
            if (blob.Kind == BlobKind.World)
            {
                _worldToLoad = blob;
                Now = Stage.Waiting;
                Say("the world has been handed out again: loading it");
                return;
            }
            if (blob.Kind != BlobKind.Vehicle || blob.Key.Length == 0) continue;
            // The same craft again, which its owner sends whenever KSA reshapes it. The craft standing
            // here is already that one, and destroying it to build it back is how a world comes to
            // hold two of it.
            if (Definitions.TryGetValue(blob.Key, out byte[]? had) && had.AsSpan().SequenceEqual(blob.Data)) continue;
            // A craft rebuilt by its owner, as one that drops a stage is: the old one here goes, and the
            // new one is built below. Never one this game made: another player's kitten out of the same
            // capsule arrives under the name this game gave its own, and that one is not theirs to take.
            bool standing = here.TryGetValue(blob.Key, out Vehicle? old) && !Owned.ContainsKey(blob.Key)
                            && !ReferenceEquals(old, flown) && !MadeHere.Contains(old);
            // One rebuild in a while per craft. A rocket coming apart changes shape every few frames
            // and its owner says so; tearing ours down and dropping a fresh one in each time is what
            // filled a world with exploding copies. The definition is left unrecorded so that this
            // same shape, or whatever it has become by then, is built once the wait is over.
            if (standing && Wall() - Rebuilt.GetValueOrDefault(blob.Key) < RebuildEverySeconds) continue;
            Definitions[blob.Key] = blob.Data;
            if (standing)
            {
                Rebuilt[blob.Key] = Wall();
                // Said because one session rebuilt a single fragment of another player's rocket 89
                // times, and the guard above means each of those definitions really did differ --
                // or the one before it had been forgotten. Which of the two it is decides the fix.
                Log.Info($"rebuilding {blob.Key}: {(had is null ? "nothing was known of it" : $"{had.Length} bytes became {blob.Data.Length}")}");
                Puppets.Remove(old);
                Built.Remove(blob.Key);
                Later(old);
                here.Remove(blob.Key);
            }

        }
        while (c.Gone.TryDequeue(out string? key))
        {
            Definitions.Remove(key);
            Describes.Remove(key);
            if (here.TryGetValue(key, out Vehicle? gone) && !Owned.ContainsKey(key) && !MadeHere.Contains(gone))
            {
                Puppets.Remove(gone);
                Built.Remove(key);
                Later(gone);
                here.Remove(key);
            }
        }
        // Swept by the operator, which is not the same as gone. A gone is one game's word about a
        // craft of its own, and the game that owns it is right to keep its own; this is the world
        // being told what is no longer in it, so it goes whosever it is. The server sends a name
        // rather than a list because most of what is out there was claimed by nobody and it has
        // never heard of it: this world is the one that knows what is in it.
        while (c.Swept.TryDequeue(out string? like))
        {
            int gone = 0;
            foreach (Vehicle v in World.Vehicles())
            {
                string key = KeyOf(v);
                if (!Wire.Matches(key, like) || ReferenceEquals(v, flown)) continue;
                // Nor anything anybody else is flying: a player's hull is not debris, and a sweep
                // that took one out from under them would be worse than the debris.
                if (c.Players.Any(p => p.Controlling == key)) continue;
                Owned.Remove(key);
                Puppets.Remove(v);
                Built.Remove(key);
                MadeHere.Remove(v);
                Renamed.Remove(v);
                Engines.Remove(v);
                Definitions.Remove(key);
                Describes.Remove(key);
                PlacedAt.Remove(key);
                Seen.Remove(v);
                if (_claimed == key) _claimed = "";
                here.Remove(key);
                Later(v);
                gone++;
            }
            Say(gone > 0
                ? $"the world was cleared of {gone} craft nobody is flying"
                : "the world was cleared, and this game had nothing matching to take away");
        }

        ClaimFlown(c, flown);
        Adopt(c, here, flown);
        SendOwned(c, here, flown);
        PlaceOthers(c, here, flown);
    }

    // A craft of this game's that the server has renamed, because a player already had that name.
    // Everything held under the old name follows it, and the definition goes again so that every other
    // game builds the craft under the name they will all know it by.
    private static void TakeRenames(Client c)
    {
        while (c.Renamed.TryDequeue(out (string Was, string Now) r))
        {
            // Found before the new name is written down, since that is what it is called until then.
            Vehicle? craft = World.Vehicles().FirstOrDefault(v => KeyOf(v) == r.Was && !Puppets.ContainsKey(v));
            // What is held under the old name is not this craft's: the name was taken because
            // another player already had a craft called that, so the definition filed under it is
            // theirs, and it is the one this game needs to build their craft. Nothing re-requests a
            // definition, so dropping it meant their craft could never be built here at all.
            PlacedAt.Remove(r.Was);
            if (_claimed == r.Was) _claimed = "";
            if (Owned.Remove(r.Was, out Mine? mine)) craft ??= mine.Craft;
            if (craft is null || !World.IsAlive(craft))
            {
                Say($"another player already has a craft called '{r.Was}', but this game has no such craft now");
                continue;
            }
            // Said again under the name everyone will know it by: the server dropped what came before,
            // and this game may already have let go of it while the name was somebody else's.
            Renamed[craft] = r.Now;
            Owned[r.Now] = mine ?? new Mine(craft);
            c.Claim(r.Now, controlling: ReferenceEquals(craft, World.Controlled), made: true);
            Define(c, craft);
            Say($"another player already has a craft called '{r.Was}': yours is '{r.Now}' to them");
        }
    }

    // The craft this player flies, claimed whenever it changes and now and then after.
    private static void ClaimFlown(Client c, Vehicle? flown)
    {
        string key = flown is null ? "" : KeyOf(flown);
        if (key.Length == 0) return;

        // Another player holds it, so this player is watching: never claimed, and left to be pulled to
        // wherever its owner's game says it is, which is the whole of what watching means.
        if (c.OwnerOf(key) is var held && held != 0 && held != c.Id && !MadeHere.Contains(flown!))
        {
            if (_watching != key)
            {
                _watching = key;
                Say($"{c.NameOf(held)} is flying {key}: you are watching");
            }
            // Watching means watching: control is given up here, since a game that joined already
            // holding this craft would otherwise keep steering it.
            if (flown is not null) World.LetGo(flown);
            return;
        }
        _watching = "";
        if (key == _claimed && Wall() < _claimNext) return;
        // Anything the loaded world did not have is one this game made, and may need a name of its own.
        bool made = flown is not null && !Seen.Contains(flown);
        c.Claim(key, controlling: true, made: made);
        (_claimed, _claimNext) = (key, Wall() + ClaimEverySeconds);
        if (flown is not null && Seen.Add(flown))
        {
            MadeHere.Add(flown);
            if (!Owned.ContainsKey(key) && c.OwnerOf(key) == 0) Own(c, flown, define: true, made: true);
        }
    }

    // Craft this game has made since it was last looked at: a launch, a dropped stage, a kitten on EVA.
    // Each is this player's when it is the one flown or nearer to this player's craft than to anyone else's.
    private static void Adopt(Client c, Dictionary<string, Vehicle> here, Vehicle? flown)
    {
        foreach (Vehicle v in World.Vehicles())
        {
            // Not by what is in `here`: a craft this game has just made can share its name with one
            // already standing here for another player, and only one of the two is in there.
            if (Seen.Contains(v) || Puppets.ContainsKey(v) || Renamed.ContainsKey(v)) continue;
            // On its way out: its bookkeeping has gone already, so without this it reads as a craft
            // this game has just made and would be claimed back the frame before it is destroyed.
            if (ToRemove.Contains(v)) continue;
            string key = KeyOf(v);
            Seen.Add(v);
            MadeHere.Add(v);
            double mine = Owned.Values.Where(m => World.IsAlive(m.Craft)).Select(m => World.Between(m.Craft, v)).DefaultIfEmpty(double.PositiveInfinity).Min();
            double theirs = Puppets.Keys.Where(World.IsAlive).Select(p => World.Between(p, v)).DefaultIfEmpty(double.PositiveInfinity).Min();
            // Made here, so the server gives it a name of its own if another player has that one.
            if (ReferenceEquals(v, flown) || mine < theirs) Own(c, v, define: true, made: true);
        }
    }

    private static void Own(Client c, Vehicle v, bool define, bool made)
    {
        string key = KeyOf(v);
        Owned[key] = new Mine(v);
        c.Claim(key, controlling: ReferenceEquals(v, World.Controlled), made: made);
        if (define) Define(c, v);
    }

    private static void Define(Client c, Vehicle v)
    {
        string key = KeyOf(v);
        byte[] bytes;
        try
        {
            bytes = World.Define(v, key);
        }
        catch (Exception e)
        {
            Log.Warn($"{key} could not be written down for the others: {e.GetBaseException().Message}");
            return;
        }
        Offer(c, key, bytes);
    }

    /// <summary>
    /// A craft described to the others, at most once every <see cref="RedescribeEverySeconds"/> for
    /// any one name.
    ///
    /// <para>A craft is described when this game adopts one it has not seen, and what it has seen is
    /// a set of Vehicle objects. KSA remakes the remaining stack under the same name every time a
    /// rocket sheds a part, and each remake is an object this game has never seen, so a breakup
    /// describes one craft over and over: watched on a real server, 23 times in six seconds, each
    /// copy relayed to every other game to be rebuilt. Which shape arrives matters far less than
    /// that the last one does, so the newest is held and sent when the name may speak again.</para>
    /// </summary>
    private static void Offer(Client c, string key, byte[] bytes)
    {
        if (!Describes.TryGetValue(key, out Description? told)) Describes[key] = told = new Description();
        // The same bytes are worth sending once, whenever that was: a craft remade unchanged is the
        // craft they already have.
        if (told.Sent is { } last && last.AsSpan().SequenceEqual(bytes))
        {
            told.Waiting = null;
            return;
        }
        double now = Wall();
        if (told.At > 0.0 && now - told.At < RedescribeEverySeconds)
        {
            told.Waiting = bytes;
            return;
        }
        Describe(c, key, bytes, told, now);
    }

    /// <summary>What a craft settled into while it was coming apart, sent once its name may speak again.</summary>
    private static void Settled(Client c)
    {
        double now = Wall();
        foreach ((string key, Description told) in Describes)
        {
            if (told.Waiting is not { } bytes || now - told.At < RedescribeEverySeconds) continue;
            Describe(c, key, bytes, told, now);
        }
    }

    private static void Describe(Client c, string key, byte[] bytes, Description told, double now)
    {
        c.Upload(new Blob(BlobKind.Vehicle, key, c.Id, World.Universe, bytes));
        (told.Sent, told.At, told.Waiting) = (bytes, now, null);
        Log.Info($"sent the server how {key} is built, {bytes.Length} bytes");
    }

    // Every craft this player owns, sent: often the one flown, seldom the rest; and any that is gone, said so.
    private static void SendOwned(Client c, Dictionary<string, Vehicle> here, Vehicle? flown)
    {
        foreach (string key in c.Owners.Where(o => o.Value == c.Id).Select(o => o.Key))
        {
            if (Owned.ContainsKey(key) || !here.TryGetValue(key, out Vehicle? taken)) continue;
            // The server gave this player a craft it took control of: it is no puppet now.
            Puppets.Remove(taken);
            Owned[key] = new Mine(taken);
        }
        double now = Wall();
        foreach ((string key, Mine entry) in Owned.ToArray())
        {
            Mine m = entry;
            // The craft itself, not whatever stands here under its name: another player's craft of the
            // same name can be the one `here` holds, and sending its state as this player's own would
            // tell everyone that their craft is where somebody else's is.
            if (!World.IsAlive(m.Craft))
            {
                // KSA can rebuild a craft as another object. One standing here under the same name that
                // is nobody else's is that craft carrying on; anything else means it is gone.
                if (here.TryGetValue(key, out Vehicle? again) && World.IsAlive(again) && !Puppets.ContainsKey(again))
                {
                    Owned[key] = m = new Mine(again) { Shape = 0 };
                }
                else
                {
                    Owned.Remove(key);
                    if (c.Owns(key)) c.SayGone(key);
                    continue;
                }
            }
            Vehicle v = m.Craft;
            byte owner = c.OwnerOf(key);
            if (owner != 0 && owner != c.Id)
            {
                // Taken by another player, who is flying it now.
                Owned.Remove(key);
                continue;
            }
            // Let go of by the server because this player walked away from it, and let go of here
            // too: a state sent for a craft nobody owns takes it straight back, and the two would
            // hand it to each other for ever. The same rule as the server's, so they agree on it.
            if (owner == 0 && !ReferenceEquals(v, flown) && KeyOf(v) != (World.Watched is { } at ? KeyOf(at) : ""))
            {
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
            if (World.StateOf(v, key, controlled) is { } state) c.SendState(state);
        }
    }

    // Every craft another player owns, as a puppet here: built from its definition if this world has none, pulled in physics, placed by its orbit without.
    private static void PlaceOthers(Client c, Dictionary<string, Vehicle> here, Vehicle? flown)
    {
        HashSet<Vehicle> wanted = [];
        bool builtOne = false;
        double universe = World.Universe, now = Wall();
        double lost = LostSeconds * Math.Max(1.0, World.Speed);
        foreach ((string key, byte owner) in c.Owners)
        {
            if (owner == c.Id || Owned.ContainsKey(key)) continue;
            if (here.TryGetValue(key, out Vehicle? ours) && MadeHere.Contains(ours)) continue;
            Track track = c.TrackOf(key);
            here.TryGetValue(key, out Vehicle? v);
            if (v is null && Built.TryGetValue(key, out Vehicle? standing) && World.IsAlive(standing)) here[key] = v = standing;
            if (v is null)
            {
                // One a frame: each is a craft built.
                if (builtOne || track.Newest is null || !Definitions.TryGetValue(key, out byte[]? definition)) continue;
                // And one every few seconds per name, however often it goes. The last limit only
                // caught a craft that was still standing here, and these are not: each copy is
                // dropped in, explodes at once, and the next definition finds nothing standing and
                // builds it again. 429 builds of 95 craft in one session, with the limit in.
                if (Wall() - Rebuilt.GetValueOrDefault(key) < RebuildEverySeconds) continue;
                builtOne = true;
                // Asked for rather than done: a craft is added to the world where KSA is not
                // walking its vehicles. It stands here from the next frame, found through Built.
                ToBuild[key] = (definition, c.NameOf(owner));
                continue;
            }
            if (universe - track.HeardAt > lost) continue;
            wanted.Add(v);
            Puppets[v] = key;
            if (track.At(universe) is not { } s) continue;
            bool lit = (s.Flags & Wire.StateFlags.EngineOn) != 0;
            World.SetLights(v, (s.Flags & Wire.StateFlags.Lights) != 0);
            // The input, which lights the engine for real in a craft this game is stepping, and the
            // word for the renderer, which is all there is for one it is not.
            World.SetEngine(v, lit, s.Throttle);
            Engines[v] = (lit, s.Throttle);
            if (!v.HasPhysicsBubble && now >= PlacedAt.GetValueOrDefault(key))
            {
                PlacedAt[key] = now + PlaceEverySeconds;
                World.PlaceAnalytic(v, s, PlaceNearEnoughM);
            }
        }
        foreach (Vehicle v in Puppets.Keys.Where(p => !wanted.Contains(p)).ToArray()) Puppets.Remove(v);
        foreach (Vehicle v in Engines.Keys.Where(p => !Puppets.ContainsKey(p)).ToArray()) Engines.Remove(v);
        foreach (string k in Built.Where(p => !World.IsAlive(p.Value)).Select(p => p.Key).ToArray()) Built.Remove(k);
        foreach (Vehicle v in Renamed.Keys.Where(x => !World.IsAlive(x)).ToArray()) Renamed.Remove(v);
        foreach (Vehicle v in Seen.Where(x => !World.IsAlive(x)).ToArray()) Seen.Remove(v);
        foreach (Vehicle v in MadeHere.Where(x => !World.IsAlive(x)).ToArray()) MadeHere.Remove(v);
    }

    // A player is named this far off; a craft of theirs they are not in is marked only this far.
    // Who is out there can be worth seeing across a sky; whose parked hull that is matters where you
    // are standing next to it.
    private const double NametagWithinM = 2_000_000.0, ClaimWithinM = 20_000.0;

    /// <summary>
    /// Where each other player's craft is on the screen and whose it is, for the panel to draw over
    /// the world. Only craft another player owns: this player knows which one is theirs.
    /// </summary>
    public static List<(float2 At, string Who, string Craft, double Range, bool Flying)> Nametags()
    {
        List<(float2, string, string, double, bool)> tags = [];
        if (_client is not { Joined: true } c) return tags;

        // What each player is flying, by who they are rather than by what they are in: a player is
        // in one craft, so a field of theirs can name only one, and a name cannot be in two places.
        // The same word the scoreboard reads, so the tag and that column can never disagree.
        Dictionary<byte, string> flying = [];
        foreach (Wire.Player p in c.Players)
        {
            if (p.Controlling.Length > 0) flying[p.Id] = p.Controlling;
        }

        foreach ((Vehicle v, string key) in Puppets)
        {
            if (!World.IsAlive(v)) continue;
            // Nobody's, or this player's own whatever the bookkeeping says: unnamed either way.
            byte owner = c.OwnerOf(key);
            if (owner == 0 || owner == c.Id || Owned.ContainsKey(key)) continue;

            // Asked of whoever owns it, so a word gone stale can only fail to match and never put
            // somebody's name on a craft that was never theirs.
            bool flown = flying.GetValueOrDefault(owner, "") == key;
            double range = World.RangeFromCamera(v);
            if (!(range < (flown ? NametagWithinM : ClaimWithinM))) continue;
            if (World.ScreenOf(v) is not { } at) continue;
            tags.Add((at, c.NameOf(owner), key, range, flown));
        }
        return tags;
    }

    /// <summary>
    /// A puppet's engines as its owner last said, written from the render for the frame about to be
    /// drawn. Anything else is written dark, which costs nothing unless it was lit from here.
    /// </summary>
    public static void ShowEngines(Vehicle v)
    {
        (bool on, float throttle) = Engines.TryGetValue(v, out (bool On, float Throttle) said) ? said : (false, 0f);
        World.ShowEngine(v, on, throttle);
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
        // AtACraft, not InFlight: an observer controls nothing, and a game that stops holding the
        // clock keeps whatever speed it last had -- which after a join is the catch-up warp, so it
        // runs away from the world it is watching and every state it is sent arrives stale.
        if (Now is Stage.Loading or Stage.Joining || !World.AtACraft)
        {
            // Let go of, and at real time rather than at whatever this world was last run at: out of
            // flight the catch-up warp from a join kept running, so the editor and the tracking
            // station ran at ten times speed with nothing holding them to the server, and the world
            // came back further ahead than it went. KSA's own load is the one left to run as it does.
            if (HeldSpeed is not null && Now is not (Stage.Loading or Stage.Joining)) World.SetSpeed(1.0);
            HeldSpeed = null;
            // And nothing measured is nothing to report: a number left standing here is one the
            // server holds the whole room's speed down to.
            c.Late = 0.0;
            return;
        }
        if (Now != Stage.Playing || c.ServerUniverse is not { } server)
        {
            // Nothing to be late by while there is no world in front of this game yet.
            c.Late = 0.0;
            HeldSpeed = Now == Stage.Waiting ? 1.0 : null;
            if (HeldSpeed is { } h) World.SetSpeed(h);
            return;
        }
        double behind = server.Universe - World.Universe;
        (Behind, c.Late) = (behind, behind);
        // Far more room to catch up when nothing here is being stepped through physics: the gap
        // closes in a moment instead of minutes of everything running fast, which is what makes a
        // player who was not looking able to come back rather than having to sprint. With craft in
        // physics the ceiling is whatever headroom the frame rate leaves, which in a breakup is
        // none: warping a game that is already missing its step cap only runs its clock away from
        // its own bubbles.
        double most = World.Analytic
            ? ClockFollow.MostCatchUpOnRails
            : ClockFollow.MostCatchUpInHand(FrameSeconds, World.StepCap);
        HeldSpeed = ClockFollow.Asked(ClockFollow.Speed(behind, server.Rate, most), FrameSeconds, World.StepCap);
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

    public static void Say(string what)
    {
        Log.Info($"multiplayer: {what}");
        SaidLines.Add(what);
        if (SaidLines.Count > SaidKept) SaidLines.RemoveAt(0);
    }

    /// <summary>
    /// The craft asked for since the last frame, put into the world. Called from a prefix on
    /// <c>Program.RefreshVehiclesInFrame</c>: inside <c>PrepareFrame</c>, after the solver jobs queued
    /// last frame have been waited for and before the next are queued, which is the one moment in a
    /// frame when nothing is walking the world's vehicles.
    /// </summary>
    public static void Settle()
    {
        if (Now != Stage.Playing) return;
        // Out before in: a name freed this frame is a name the next craft may want.
        foreach (Vehicle going in ToRemove)
        {
            if (World.IsAlive(going)) World.Destroy(going);
        }
        ToRemove.Clear();
        if (ToBuild.Count == 0) return;
        foreach ((string key, (byte[] definition, string owner)) in ToBuild.ToArray())
        {
            ToBuild.Remove(key);
            if (Built.ContainsKey(key)) continue;
            // This world may already hold a craft of that name -- this player's own kitten out of the
            // same capsule -- so the one built here is given a name this world has free.
            string localId = World.Taken(key) ? World.FreeLocalId(key) : key;
            Rebuilt[key] = Wall();
            if (World.Build(definition, localId, out string why) is { } built)
            {
                if (localId != key) Renamed[built] = key;
                Seen.Add(built);
                Built[key] = built;
                Log.Info($"built {key}, {owner}'s{(localId != key ? $", called {localId} here" : "")}");
            }
            else
            {
                Say($"{owner}'s {key} cannot be built here: {why}");
                Definitions.Remove(key);
                Describes.Remove(key);
            }
        }
    }

    /// <summary>
    /// A craft taken out of this world where KSA is not walking its vehicles. Its bookkeeping goes
    /// now and the craft itself goes in <see cref="Settle"/>, so for one frame it stands here owned
    /// by nobody -- which is why <see cref="Adopt"/> steps over anything waiting in here rather than
    /// taking it for this game's own.
    /// </summary>
    private static void Later(Vehicle v)
    {
        if (!ToRemove.Contains(v)) ToRemove.Add(v);
    }

    /// <summary>This game answering in the chat the player typed into, which is where they are looking.</summary>
    private static void Told(string what) => Chatted(true, $"<game> {what}");

    private static void Chatted(bool notAPlayer, string line)
    {
        Log.Info($"chat: {line}");
        ChatLines.Add(new Line(notAPlayer, line));
        if (ChatLines.Count > ChatKept) ChatLines.RemoveAt(0);
    }

    /// <summary>Says goodbye on the way out of the game; nothing of the world is touched, which is going with it.</summary>
    public static void Shutdown()
    {
        _client?.Dispose();
        _client = null;
    }
}
