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
    public const int DefaultPort = Wire.DefaultPort;

    // The craft being flown is sent this often, and any other owned craft this often.
    private const double ControlledEverySeconds = 1.0 / 20.0, OwnedEverySeconds = 0.5;

    // Unless it is being pushed about, which the pieces of a crash are and a parked craft is not.
    // Where a craft is drawn is worked out from the last two words about it, and two words half a
    // second apart about a piece that is bouncing put its copy ten metres out and stepped it
    // nineteen between two frames. So a craft under way is said this often, and all of them
    // together no more than this many times a second: sixty pieces are said three times a second
    // each rather than ten.
    private const double UnderWayEverySeconds = 0.1, MostUnderWayASecond = 200.0;

    // A claim is said again this often, since a datagram can be lost.
    private const double ClaimEverySeconds = 2.0;

    // A speed the player wants is said again this often while they still want it.
    private const double AskWarpEverySeconds = 1.0;

    // A puppet with no physics here is placed by its orbit or on the ground at most this often, and only when this far out.
    private const double PlaceEverySeconds = 1.0, PlaceNearEnoughM = 25.0;

    // How far a coasting craft may stand from where its owner has it before it is put on its
    // owner's orbit, and how long before it may be again. Above what carrying a state on over an
    // ordinary line gets wrong, so a craft that is in place is not moved for the wire's sake.
    private const double CoastingOutM = 0.3, CoastingEverySeconds = 3.0;
    private const double CoastingFarM = 1.0, CoastingFarEverySeconds = 0.5;

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

    // Craft of this player's that a piece has lately come off, and when. One is described again
    // sooner than others: every other game is holding that piece back until it has been told how
    // the craft is built without it.
    private static readonly Dictionary<string, double> ShedLately = [];
    private const double RedescribeShedEverySeconds = 1.0, ShedLatelySeconds = 3.0;

    private static double RedescribeWait(string key) =>
        ShedLately.TryGetValue(key, out double at) && Wall() - at < ShedLatelySeconds ? RedescribeShedEverySeconds : RedescribeEverySeconds;

    // The least time between two rebuilds of one craft. A rebuild is not a correction, it is a
    // destruction and a fresh 119-part rocket dropped into the world, which lands on a pad and
    // explodes: watched on the real server, one craft built 87 times and destroyed 62 in a single
    // session, every copy making the fragments that made the next definition that made the next
    // copy. Longer than the sender's own interval, because it costs far more.
    private const double RebuildEverySeconds = 5.0;

    // And the least between two rebuilds of a craft that is standing here, which is not the same
    // thing: that wait is for a copy that will not stand, and this is only how long a craft still
    // changing is left in the shape it had. Left for five seconds, the hull of a rocket that had
    // fallen over was one shape held against the centre of mass of another, 2 to 3 m out.
    private const double RebuildStandingEverySeconds = 2.0;

    /// <summary>
    /// Definitions the rebuild wait pushed back, the newest for each craft.
    ///
    /// <para>They used to be dropped on the floor, on the reasoning that another would be along
    /// once the wait was over. One is along only if the craft changes shape again, and a craft that
    /// settles into its final shape during the wait never does -- so a hull that lost its last parts
    /// inside those five seconds stood here in its old shape for the rest of the session. Two
    /// players on mp.ksarmory.com saw exactly that: one of them watching a craft with parts the
    /// other's copy had already shed, and the owner's game had said so twice, two and four seconds
    /// after a rebuild.</para>
    /// </summary>
    private static readonly Dictionary<string, byte[]> Deferred = [];

    // When the server was given each definition held here: the one a copy would be built from,
    // the one held back by the rebuild wait, and the one the copy standing here was built from.
    private static readonly Dictionary<string, double> DefinedAt = [], DeferredAt = [], BuiltFrom = [];

    // Pieces not built yet because the craft they came off still stands here with them on it,
    // and since when. Not for ever: a hull whose owner never says how it is built now is one
    // this game cannot wait for.
    private static readonly Dictionary<string, double> PieceWaiting = [];
    private const double PieceWaitsSeconds = 5.0;

    // Craft a piece was waiting on when last looked at, which are built again sooner than others:
    // what is waiting is a piece everybody else can already see.
    private static readonly HashSet<string> Hurried = [];
    private const double SetDownBeyondM = 0.1;
    private const double BuiltLookAgainSeconds = 0.5;
    private const double RebuildHurriedEverySeconds = 1.0;

    private static double StandingWait(string key) => Hurried.Contains(key) ? RebuildHurriedEverySeconds : RebuildStandingEverySeconds;

    // Craft taken away to be built again in another shape, until they have been. The wait between
    // two builds of one name is for a copy that will not stand; it is not for these, which went
    // unbuilt for the whole of it -- a rocket that dropped a stage or docked was in no other world
    // at all for five seconds.
    private static readonly HashSet<string> Rebuilding = [];

    // Craft built here that the pull has not had yet.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Vehicle, double> Unpulled = new();

    // How long after a copy is built its first place may still be given outright. Later than that
    // it has been seen standing where it was built, and is drawn to its place like any other.
    private const double BornWithinSeconds = 0.5;

    // Copies standing here in another shape than their owner says, and since when. Waited for
    // only so long: a copy that is never built again in its owner's shape is better put where
    // the wrong centre says than left to drift for good.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Vehicle, double> OtherShape = new();
    private const double OtherShapeForSeconds = 6.0;

    private static bool StillAnotherShape(Vehicle v, ushort parts)
    {
        if (parts == 0 || parts == v.Parts.Count)
        {
            if (!OtherShape.IsEmpty) OtherShape.TryRemove(v, out _);
            return false;
        }
        return Wall() - OtherShape.GetOrAdd(v, static _ => Wall()) < OtherShapeForSeconds;
    }

    // Coasting craft the pull put where they were told at the last physics step, by name.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> PutBack = new();

    public enum Stage { Off, Joining, Seeding, Waiting, Loading, Playing }

    private sealed class Mine(Vehicle craft)
    {
        public Vehicle Craft { get; } = craft;
        public int Shape { get; set; } = World.Shape(craft);

        /// <summary>Whether the server has ever said this craft is this player's, which is when its name became its own.</summary>
        public bool Granted { get; set; }

        /// <summary>Whether it was being stepped through physics when last looked at.</summary>
        public bool UnderWay { get; set; }

        /// <summary>The last state said of it and the one before, and when the last was said.</summary>
        public Wire.State? Said { get; set; }
        public Wire.State? SaidBefore { get; set; }
        public double SaidAt { get; set; }

        /// <summary>
        /// When this craft's state may next be sent, spread across the first interval rather than
        /// left at zero.
        ///
        /// <para>Because every craft claimed on one frame was claimed against one reading of the
        /// clock, so all of them came due on the same frame for ever after. A rocket coming apart
        /// into sixty fragments then sent sixty datagrams at once, twice a second, which the server
        /// turned into sixty relays to every player inside one of its own five-millisecond ticks --
        /// a burst that holds up the clock and the roster it sends in the same tick, and whose
        /// losses fall together on one instant rather than being spread over the second. Correlated
        /// loss is what runs a craft past the end of what its watchers can carry it on from.</para>
        /// </summary>
        public double SendNext { get; set; } = Wall() + (OwnedEverySeconds * Random.Shared.NextDouble());
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

    // Part failures the bridge has asked for, kept for the quiet window in Settle.
    private static readonly List<(Vehicle Craft, int Parts, bool Whole)> ToFail = [];
    private static readonly List<(Vehicle One, Vehicle Other)> ToDock = [];

    // Craft that were nobody's when KSA took them out of this world, by the name the room shares:
    // when, when to claim it next, and whether it has been said gone.
    private static readonly Dictionary<string, (double Since, double ClaimNext, bool Said)> DockedAway = [];
    private const double DockedAwayClaimEverySeconds = 0.5, DockedAwayForSeconds = 10.0;

    // Dockings done in this game that the server has yet to answer for, by the name of the craft
    // that is left: the craft taken into it, when, when to say so next and how often it has been.
    private static readonly Dictionary<string, (string Consumed, double Since, double SayNext, int Said)> DockedHere = [];
    private const double DockedSayEverySeconds = 0.4, DockedForSeconds = 6.0;
    private const int DockedSaidAtLeast = 3;

    // Names a docking has taken out of this world, and until when no craft is built under them: a
    // craft's owner may not have heard yet, and what they last said of it is how to build it.
    private static readonly Dictionary<string, double> Consumed = [];
    private const double ConsumedForSeconds = 10.0;

    // Craft that were other players' before a docking in this game took them, and whose.
    private static readonly Dictionary<string, byte> Carried = [];

    // The craft this player rides in since theirs docked with it in another game, and what theirs
    // was called, which is the name it has again once it undocks.
    private static string _aboard = "", _cameAs = "";

    // A craft to fly from the next frame, asked for where KSA is in the middle of splitting one.
    private static Vehicle? _toGoTo;

    // Craft nobody owned when last looked at, and those this game has itself taken away since.
    private static readonly Dictionary<Vehicle, string> Nobodys = [];
    private static readonly HashSet<Vehicle> TakenAway = [];
    private const int MostGoneAtOnce = 8;

    // How often every craft within this game's physics that nobody has is asked for.
    private const double NearEverySeconds = 1.0;
    private static double _nearNext;

    private static void ForgetDockings()
    {
        DockedAway.Clear();
        DockedHere.Clear();
        Consumed.Clear();
        Carried.Clear();
        Nobodys.Clear();
        TakenAway.Clear();
        DefinedAt.Clear();
        DeferredAt.Clear();
        BuiltFrom.Clear();
        PieceWaiting.Clear();
        ShedLately.Clear();
        Hurried.Clear();
        _earlyTokens = 0.0;
        JustDocked.Clear();
        DockedInto.Clear();
        SplitHere.Clear();
        OtherShape.Clear();
        _rode = null;
        JustUndocked.Clear();
        Halves.Clear();
        (_aboard, _cameAs, _toGoTo) = ("", "", null);
    }
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

    /// <summary>
    /// Another player's craft to be put where they say it is, waited for the frame's quiet window.
    ///
    /// <para>Because placing one writes a flight plan, and <c>Vehicle.SetFlightPlan</c> moves the
    /// craft between two celestials' <c>Children</c> lists there and then -- plain lists that KSA
    /// walks on the vehicle solver in <c>PhysicsStates.CheckSoiTransitions</c>, and on another
    /// worker in <c>Orbit.IsOccluded</c>. KSA never calls it from a frame hook itself: it has a
    /// whole Unsynchronized/Synchronized split so the worker only stashes the old parent and the
    /// list is edited from one serialized point, and it defers the landed case through
    /// <c>InputEvents.TeleportInputBuffer</c>. This mod was the one caller in the process that did
    /// it with the solvers running, and `Collection was modified` out of
    /// <c>FullPhysicsConstrainedStep</c> -- which is where that walk lives -- is in the logs of the
    /// night everybody crashed.</para>
    ///
    /// <para>The same call hands an orbit's cached points back to an <c>ArrayPool</c> while a worker
    /// reads them, which needs no change of parent and so happened on every placement.</para>
    /// </summary>
    private static readonly Dictionary<string, (Vehicle Craft, Wire.State State)> ToPlace = [];
    // Craft coasting on their owners' games and out of place here, each with the newest word of it
    // and how far out it stood, to be put on that orbit in the frame's quiet window.
    private static readonly Dictionary<string, (Vehicle Craft, Wire.State Said, double OutM)> ToOrbit = [];
    // Other players' craft that were being pushed when last looked at: engines, the ground, the air.
    private static readonly HashSet<string> UnderWay = [];

    // A sweep asks for the smoke to go with the craft, which the quiet window answers.
    private static bool _toClearSmoke;
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

    /// <summary>
    /// The last server this game was pointed at, kept after it has left: the address itself is let
    /// go of on disconnecting, and the menu bar still has to be able to say which world this game
    /// is out of rather than quietly going blank.
    /// </summary>
    public static string LastServer { get; private set; } = "";

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
        // Faster, from what this player last asked for where that is more than the warp has reached.
        // A warp starts a step above normal and climbs to what was agreed, so a second press a
        // moment after the first found the world between the rung it left and the rung it was
        // going to, stepped from there, and asked for the rung already asked for: the keys did
        // nothing until the climb was over. Slower is still from where the world is, since that is
        // what the player is looking at and wants less of.
        bool mine = c.Warp is { Phase: Wire.WarpPhase.Running or Wire.WarpPhase.Asking } own && own.Who == c.Id;
        if (direction > 0 && mine && _askedSpeed > running) running = _askedSpeed;
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
    /// Whether a craft is another player's to fly and not this one's: held here for them, unless
    /// they hold it only for being near it, which keeps nobody out.
    /// </summary>
    public static bool IsTheirs(Vehicle v) =>
        Puppets.TryGetValue(v, out string? key) && !DockedInto.ContainsKey(v) && !(_client is { } c && c.NearOf(key));

    // Craft another craft has just been docked into in this game, until the frame has taken that
    // up. KSA hands its player the craft that is left in the same call that docks them: still
    // held here for its last owner, that was refused, and a player who had flown a capsule onto
    // another player's rocket had the rocket and was at no controls.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Vehicle, byte> DockedInto = new();

    /// <summary>Whether a craft is held by a game only for being within its physics, this one or another.</summary>
    public static bool HeldNear(Vehicle v) => _client is { Joined: true } c && c.NearOf(KeyOf(v));

    /// <summary>
    /// Whether what this world's physics would do to a craft is to be left undone: another
    /// player's, or one waiting to be taken out of this world. The second because a craft its
    /// owner says has crashed is no puppet for the rest of the frame it is told in, and the crash
    /// this world's own physics had ready for it went through in that frame: its pieces were shed
    /// here under the names its owner's pieces have, were nobody's, and stood in for them -- one
    /// fell through the ground at 65 m/s for two minutes while its owner had it lying on a pad.
    /// </summary>
    public static bool Spared(Vehicle v) => Puppets.ContainsKey(v) || ToRemove.Contains(v);

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
        if (c.HeldBy(key) != 0 || Owned.ContainsKey(key) || MadeHere.Contains(v)) return;
        // Nor one on its way out. A craft its owner has said is gone is nobody's for the frame it
        // takes to leave this world, and a game watching it took it in that frame and flew it:
        // watching a capsule dock, it was given the capsule that had just become part of a rocket,
        // every other world built that capsule again inside the rocket, and both came apart.
        if (ToRemove.Contains(v)) return;

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
        // Held outright, and not only for being near: that keeps nobody out, and another game
        // looking at the same capsule would send the same kitten out of it.
        if (Owned.ContainsKey(key) && !c.NearOf(key)) return true;

        byte owner = c.HeldBy(key);
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
        if (!TryAddress(address, out string host, out int port, out bool portGiven))
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
        (_asking, _address, LastServer) = ((name, password), address, address);
        _finding = Find(host, port, portGiven);
        Now = Stage.Joining;
        Say($"looking up {host}...");
    }

    // A server asked which system its world is in, from KSA's configuration at start: there is no
    // universe to join with yet, and the one about to be made has to be the server's.
    private static Client? _ahead;
    private static Task<IPEndPoint?>? _aheadFinding;

    public static bool AskingAhead => _ahead is not null;

    /// <summary>What the last server asked answered about itself, and the address that was asked.</summary>
    public static ServerFacts? Server { get; private set; }

    public static string ServerAsked { get; private set; } = "";

    /// <summary>Whether a server is being asked about right now.</summary>
    public static bool Asking => _ahead is not null;

    private static bool _lookOnly;

    private static string _askedOf = "";

    /// <summary>
    /// The same question as <see cref="AskAhead"/> and none of the answer acted on: what a window
    /// shows about a server before anybody has decided to join it.
    /// </summary>
    public static void Look(string address)
    {
        if (Asking || ServerAsked == address) return;
        AskAhead(address, quietly: true);
    }

    /// <summary>Why the last look found nothing, for the window to show in place of an answer.</summary>
    public static string ServerTrouble { get; private set; } = "";

    /// <summary>Forgotten, so the next window asks again rather than showing a server nobody chose.</summary>
    public static void Forget() => (Server, ServerAsked) = (null, "");

    public static void AskAhead(string address, bool quietly = false)
    {
        EndAhead();
        // Set before anything can speak: a look the window asked for says nothing to the player,
        // whether it answers, is refused, or finds nobody there.
        _lookOnly = quietly;
        ServerTrouble = "";
        (Server, ServerAsked) = (null, "");
        if (!TryAddress(address, out string host, out int port, out bool portGiven))
        {
            Trouble(address, "that is not an address: give host:port, or a host for port 50100");
            return;
        }
        try
        {
            _ahead = new Client(Wall);
        }
        catch (SocketException e)
        {
            Trouble(address, $"could not open a socket: {e.Message}");
            return;
        }
        _aheadFinding = Find(host, port, portGiven);
        _askedOf = address;
        if (!_lookOnly) Say($"asking {host} which world it holds...");
    }

    // Kept for the window to show either way; said out loud only for an ask the player made, and
    // then for the panel in a running game rather than for the one that is about to show the row.
    private static void Trouble(string address, string why)
    {
        (ServerTrouble, ServerAsked) = (why, address);
        if (!_lookOnly) Say(why);
    }

    /// <summary>
    /// Pumped from the configuration's frames, which are the only ones there are before the game
    /// loads. The system the server's world is in once it has said, empty when that is the
    /// player's to choose; null until then, and after.
    /// </summary>
    public static string? AnsweredAhead()
    {
        if (_ahead is not { } c) return null;
        if (_aheadFinding is { IsCompleted: true } finding)
        {
            _aheadFinding = null;
            if (finding.IsCompletedSuccessfully && finding.Result is { } at) c.Probe(at);
            else
            {
                Trouble(_askedOf, "could not be found");
                EndAhead();
                return null;
            }
        }
        c.Update();
        while (c.Said.TryDequeue(out string? said))
        {
            ServerTrouble = said;
            if (!_lookOnly) Say(said);
        }
        if (c.Out.Length > 0)
        {
            (ServerTrouble, ServerAsked) = (c.Out, _askedOf);
            EndAhead();
            return null;
        }
        if (c.Probed is not { } p) return null;
        (Server, ServerAsked) = (p, _askedOf);
        EndAhead();
        // Asked only so a window could say what is there; nobody is going anywhere yet.
        if (_lookOnly)
        {
            _lookOnly = false;
            return null;
        }
        string name = p.ServerName.Length > 0 ? p.ServerName : "the server";
        // Said before a world is started rather than after it is refused. KSA reads its mods once,
        // at startup, so a player who learns here has a start screen to act from and one who learns
        // after the join has a restart ahead of them either way.
        (string missing, string extra) = ModsAgainst(p.Mods);
        // Into the window as well as the log. Said alone, this reached a player through the chat
        // they have not opened and a file they have no reason to read, so pressing CONNECT looked
        // like pressing nothing.
        if (missing.Length > 0)
        {
            ServerTrouble = $"needs mods you have not got: {missing}";
            Say($"{name} needs mods you have not got: {missing}. Turn them on in KSA's Mods and start again.");
            return null;
        }
        if (extra.Length > 0)
        {
            ServerTrouble = $"doesn't run mods you have enabled: {extra}";
            Say($"{name} doesn't run mods you have enabled: {extra}. Turn them off and start again.");
            return null;
        }
        // Nothing said about the system: the window has a World row that says it already, in the name
        // KSA's own list uses rather than the id, and a second telling of it under the buttons is a
        // line the player reads twice. A refusal above is said because nothing else says it.
        return p.System;
    }

    /// <summary>
    /// What this game would be refused for, measured against the mods a world wants: the ones it has
    /// not got, and the ones it is running that the world has not. Both empty when a world asks
    /// nothing, which is what an imported world does until somebody saves over it.
    ///
    /// <para>The same comparison the server makes in its own refusal, made here as well so it can be
    /// put to the player while it is still worth something. The server's is the one that decides.</para>
    /// </summary>
    public static (string Missing, string Extra) ModsAgainst(string worldMods)
    {
        string[] wants = Split(worldMods);
        if (wants.Length == 0) return ("", "");
        HashSet<string> here = [.. Split(World.Mods)];
        HashSet<string> world = [.. wants];
        return (string.Join(", ", wants.Where(m => !here.Contains(m)).Order()),
                string.Join(", ", here.Where(m => !world.Contains(m)).Order()));

        static string[] Split(string list) =>
            [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    public static void EndAhead()
    {
        _ahead?.Dispose();
        (_ahead, _aheadFinding) = (null, null);
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
        ForgetDockings();
        Definitions.Clear();
        Describes.Clear();
        Rebuilt.Clear();
        Deferred.Clear();
        Rebuilding.Clear();
        Unpulled.Clear();
        PutBack.Clear();
        WentAs.Clear();
        PuppetOf.Clear();
        ToBuild.Clear();
        ToPlace.Clear();
        ToOrbit.Clear();
        UnderWay.Clear();
        _toClearSmoke = false;
        // Not ToRemove: what is in it still has to go, and Settle takes it away on the next frame
        // whether this game is in a shared world or its own again.
        PlacedAt.Clear();
        Engines.Clear();
        World.ForgetEngines();
        (_worldToLoad, _notLoading, _notSaving) = (null, "", "");
        (_claimed, _address, _watching, Behind, Now) = ("", "", "", null, Stage.Off);
        _worldCame = false;
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

    /// <summary>
    /// A craft this game has just made, said at the moment it is made.
    ///
    /// <para>Worked out after the fact everywhere else, and for a craft launched from the editor
    /// that is too late: KSA names it <c>NewRocket_N</c> with N stepped only against this world, so
    /// two games mint the same name and every player's first launch of a session is
    /// <c>NewRocket_1</c>. A player who launched after somebody else had taken that name was read as
    /// flying another player's craft -- the only thing standing between a new craft and that reading
    /// is whether it is known to have been made here, and nothing knew yet. They were told somebody
    /// else was flying their own rocket, control was taken off them, and no claim was ever sent.</para>
    ///
    /// <para>Marked made here and nothing else. <see cref="Seen"/> means the craft was in the world
    /// this game loaded, and not being in it is what <see cref="ClaimFlown"/> and <see cref="Adopt"/>
    /// read as a craft made here: one that wants a name of its own, and a definition the others can
    /// build it from. Marked seen as well, a craft launched from the editor is claimed with made
    /// false -- which the server refuses outright where another player holds the name, leaving the
    /// same refused claim resent every two seconds for the rest of the session -- and is never
    /// written down for anybody. Nobody else already in the world could build it, so the rocket
    /// stood on its pad in one game only. The launch menu does not come through here.</para>
    /// </summary>
    public static void Made(Vehicle v) => MadeHere.Add(v);

    /// <summary>Fail parts on a craft at the next quiet window. See <see cref="World.Fail"/>.</summary>
    public static void FailLater(Vehicle v, int parts, bool whole) => ToFail.Add((v, parts, whole));

    /// <summary>
    /// Whether KSA may dock these two in this world: both this game's to move, or one of them this
    /// player's and the other another player's.
    ///
    /// <para>A docking is done in one world and told to the rest. Two craft that are both other
    /// players' are docked by one of their games and not by this one, which only watches; and a
    /// craft on its way out of this world docks with nothing.</para>
    /// </summary>
    public static bool MayDock(Vehicle one, Vehicle other)
    {
        if (_client is not { Joined: true } c || Now != Stage.Playing) return true;
        if (ToRemove.Contains(one) || ToRemove.Contains(other)) return false;
        // By the roster as well as by what is held here for them. A copy is not held for anybody
        // until the frame after it is built: two craft that had just undocked were built here port
        // to port, a tenth of a second after they parted, and this game docked them again before
        // it had looked at either -- and then took back a rocket with a capsule on it that every
        // other world had as a rocket and a capsule.
        bool Theirs(Vehicle v) => Puppets.ContainsKey(v)
            || (c.OwnerOf(KeyOf(v)) is var owner && owner != 0 && owner != c.Id && !Owned.ContainsKey(KeyOf(v)) && !MadeHere.Contains(v));
        bool theirs = Theirs(one), others = Theirs(other);
        if (!theirs && !others) return true;
        if (theirs && others) return false;
        Vehicle mine = theirs ? other : one;
        // And this player's in earnest. A craft held only for being near is held so that it is
        // in one place, and docking another player's craft with it is that player's game's to do:
        // a game that was only watching docked the pair, and was given the craft its player had
        // never flown.
        if (ReferenceEquals(mine, World.Controlled)) return true;
        return (Owned.ContainsKey(KeyOf(mine)) || MadeHere.Contains(mine)) && !c.NearOf(KeyOf(mine));
    }

    /// <summary>
    /// KSA has docked two craft in this world, and <paramref name="gone"/> is the one it took into
    /// the other.
    ///
    /// <para>The craft that is left is this player's from here on and the other is out of the
    /// world, whoever had either: the server is told, says so to everybody, and the player whose
    /// craft it was rides in what is left. Until the server has answered, the craft that is left
    /// is held as this player's own here, since a craft still pulled to where its last owner had
    /// it would be pulled by the whole length of what has just joined it.</para>
    /// </summary>
    public static void Docked(Vehicle gone, Vehicle into)
    {
        if (_client is not { Joined: true } || Now != Stage.Playing) return;
        // Only where KSA did dock them. This is reached after a docking this game refused as
        // well, and one taken for done made the craft that would have been left this player's
        // for as long as it takes the server not to answer: long enough for the two, still port
        // to port, to touch again and dock in earnest.
        if (World.IsAlive(gone) || !World.IsAlive(into)) return;
        DockedInto[into] = 0;
        JustDocked.Enqueue((gone, into, KeyOf(gone), KeyOf(into)));
    }

    // Taken up by the frame and not where KSA docks them, which is not a moment this game's own
    // account of whose craft is whose may change.
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(Vehicle Gone, Vehicle Into, string Went, string Left)> JustDocked = new();

    private static void TakeOwnDockings(Client c)
    {
        while (JustDocked.TryDequeue(out (Vehicle Gone, Vehicle Into, string Went, string Left) d))
        {
            DockedInThisGame(c, d.Gone, d.Into, d.Went, d.Left);
            DockedInto.TryRemove(d.Into, out _);
        }
    }

    private static void DockedInThisGame(Client c, Vehicle gone, Vehicle into, string went, string left)
    {
        if (!World.IsAlive(into) || went.Length == 0 || left.Length == 0 || went == left) return;
        foreach (string key in new[] { went, left })
        {
            if (c.HeldBy(key) is var had && had != 0 && had != c.Id) Carried[key] = had;
        }
        // Nothing is built under the name that went, whatever its owner said of it a moment ago.
        // What they said is kept: it is how to build the craft again if the server will not have
        // the docking.
        Consumed[went] = Wall() + ConsumedForSeconds;
        Puppets.Remove(gone);
        Puppets.Remove(into);
        TakenAway.Add(gone);
        if (!Owned.TryGetValue(left, out Mine? mine) || !ReferenceEquals(mine.Craft, into)) Owned[left] = new Mine(into) { Shape = 0 };
        else mine.Shape = 0;
        c.SayDocked(went, left);
        DockedHere[left] = (went, Wall(), Wall() + DockedSayEverySeconds, 1);
        Log.Info($"{went} docked into {left} in this game: telling the server");
    }

    // Said until the roster shows it, and a few times whatever the roster shows: nothing answers
    // a docking but the roster, and where both craft were this player's it showed it before
    // anything was said.
    private static void SayDockedHere(Client c)
    {
        double now = Wall();
        foreach ((string left, (string went, double since, double next, int said)) in DockedHere.ToArray())
        {
            bool shown = c.OwnerOf(left) == c.Id && !c.NearOf(left) && c.OwnerOf(went) == 0;
            if ((shown && said >= DockedSaidAtLeast) || now - since > DockedForSeconds)
            {
                DockedHere.Remove(left);
                if (shown)
                {
                    Definitions.Remove(went);
                    Deferred.Remove(went);
                    Rebuilding.Remove(went);
                    continue;
                }
                Log.Warn($"the server never answered that {went} docked into {left}: putting both back as their owners have them");
                // Not this player's to have docked. What went is gone from this world all the
                // same, so it is said gone if it was this player's; and where it and the craft it
                // went into are other players', both are built again as those players have them.
                if (c.Owns(went)) c.SayGone(went);
                Consumed.Remove(went);
                if (c.OwnerOf(left) is var whose && whose != 0 && whose != c.Id && Owned.Remove(left, out Mine? merged) && World.IsAlive(merged.Craft))
                {
                    MadeHere.Remove(merged.Craft);
                    Puppets.Remove(merged.Craft);
                    Built.Remove(left);
                    Later(merged.Craft);
                    Rebuilding.Add(left);
                }
                continue;
            }
            if (now < next) continue;
            c.SayDocked(went, left);
            DockedHere[left] = (went, since, now + DockedSayEverySeconds, said + 1);
        }
        foreach (string key in Consumed.Where(k => now > k.Value).Select(k => k.Key).ToArray()) Consumed.Remove(key);
    }

    /// <summary>
    /// Dockings done in other games: the craft that went is taken out of this world whoever had
    /// it, and the craft that is left is its new owner's, this player's own included.
    /// </summary>
    private static void TakeDockings(Client c, Dictionary<string, Vehicle> here, Vehicle? flown)
    {
        while (c.Docked.TryDequeue(out (string Consumed, string Survivor, byte By) d))
        {
            if (d.By == c.Id) continue;
            // Asked before anything is taken away, and of the last claim as well as of what stands
            // here: a game that docked the two itself a moment later has no craft under the name
            // that went, and its player was in it all the same.
            string rode = Flew(d.Consumed) ? d.Consumed : Flew(d.Survivor) ? d.Survivor : "";
            Definitions.Remove(d.Consumed);
            Deferred.Remove(d.Consumed);
            Rebuilding.Remove(d.Consumed);
            Describes.Remove(d.Consumed);
            // This game docked them as well, a moment later, and is not the one that has them.
            DockedHere.Remove(d.Survivor);
            DockedHere.Remove(d.Consumed);
            Consumed.Remove(d.Survivor);
            Carried.Remove(d.Consumed);
            Carried.Remove(d.Survivor);
            if (here.TryGetValue(d.Consumed, out Vehicle? gone) && World.IsAlive(gone))
            {
                if (ReferenceEquals(gone, flown) || Flew(d.Consumed))
                {
                    rode = d.Consumed;
                    World.LetGo(gone);
                }
                Owned.Remove(d.Consumed);
                MadeHere.Remove(gone);
                Puppets.Remove(gone);
                Built.Remove(d.Consumed);
                if (_claimed == d.Consumed) _claimed = "";
                Later(gone);
                here.Remove(d.Consumed);
            }
            if (here.TryGetValue(d.Survivor, out Vehicle? left) && (Owned.ContainsKey(d.Survivor) || MadeHere.Contains(left) || Flew(d.Survivor)))
            {
                if (ReferenceEquals(left, flown) || Flew(d.Survivor))
                {
                    rode = d.Survivor;
                    World.LetGo(left);
                }
                Owned.Remove(d.Survivor);
                MadeHere.Remove(left);
                if (_claimed == d.Survivor) _claimed = "";
            }
            if (rode.Length == 0) continue;
            Log.Info($"{d.Consumed} docked into {d.Survivor} in {c.NameOf(d.By)}'s game, and this player was flying {rode}");
            (_aboard, _cameAs) = (d.Survivor, rode);
            Say($"{d.Consumed} has docked with {d.Survivor} in {c.NameOf(d.By)}'s game: the craft is theirs to fly, and you are aboard");
        }
    }

    /// <summary>
    /// Whether this player was at the controls of a craft a moment ago, by the last claim made
    /// for it. The roster is told of a docking as it arrives and this game acts on it a frame
    /// later: asked then what its player is flying, a game that had just been told its craft was
    /// somebody else's had already let go of it, took its player for a bystander, and left them
    /// at no craft with nothing to bring theirs back.
    /// </summary>
    private static bool Flew(string key) => key.Length > 0 && _claimed == key && Wall() < _claimNext + 1.0;

    /// <summary>
    /// Keeps a player whose craft docked in another game looking at the craft they ride in, and
    /// at their own again once it has undocked.
    ///
    /// <para>Theirs to take back the way any craft is: its owner flies the other half away, and a
    /// craft nobody has been near for a while is whoever is watching it's.</para>
    /// </summary>
    private static void RideAlong(Client c, Dictionary<string, Vehicle> here, Vehicle? flown)
    {
        if (_aboard.Length == 0) return;
        if (flown is not null)
        {
            (_aboard, _cameAs) = ("", "");
            return;
        }
        // Their own craft under its own name again: undocked, and built here from what its owner said.
        if (_cameAs != _aboard && here.TryGetValue(_cameAs, out Vehicle? back) && World.IsAlive(back) && !ToRemove.Contains(back))
        {
            Say($"{_cameAs} has undocked: it is yours once {c.NameOf(c.OwnerOf(_cameAs))} has left it");
            _aboard = _cameAs;
        }
        if (!here.TryGetValue(_aboard, out Vehicle? ride) || !World.IsAlive(ride) || ToRemove.Contains(ride)) return;
        // Once for each craft that is the ride, and again when it is built again in a new shape:
        // put back on it every frame, a rider could look at nothing else for as long as they rode.
        if (ReferenceEquals(_rode, ride)) return;
        _rode = ride;
        if (!ReferenceEquals(World.Watched, ride)) World.Watch(ride);
    }

    private static Vehicle? _rode;

    /// <summary>
    /// KSA has undocked a craft of this player's into two. One that was another player's before it
    /// docked is left for them: this player flies the other half.
    /// </summary>
    public static void Undocked(Vehicle stack, Vehicle? split)
    {
        if (_client is not { Joined: true } || split is null) return;
        JustUndocked.Enqueue((stack, split, Wall()));
    }

    // Looked at by the frame for a moment afterwards: KSA decides which half its player flies
    // after it has split them, so where it splits them the answer is still the craft as it was.
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(Vehicle Stack, Vehicle Split, double At)> JustUndocked = new();
    private static readonly List<(Vehicle Stack, Vehicle Split, double At)> Halves = [];

    // Craft that came off a craft of this player's at an undocking and have not been looked at
    // yet. Theirs by that, as a piece is by its name: settled by distance, a capsule left for the
    // player it was had another player's craft nearer it than the stack's centre, was nobody's,
    // and was never written down for anybody.
    private static readonly HashSet<Vehicle> SplitHere = [];
    private const double HalvesForSeconds = 1.0;

    private static void LeaveWhatWasTheirs(Client c, Vehicle? flown)
    {
        while (JustUndocked.TryDequeue(out (Vehicle Stack, Vehicle Split, double At) u))
        {
            Halves.Add(u);
            SplitHere.Add(u.Split);
        }
        for (int i = Halves.Count - 1; i >= 0; i--)
        {
            (Vehicle stack, Vehicle split, double at) = Halves[i];
            bool over = Wall() - at > HalvesForSeconds || !World.IsAlive(stack) || !World.IsAlive(split);
            if (!over && flown is not null && (ReferenceEquals(flown, stack) || ReferenceEquals(flown, split))
                && Carried.TryGetValue(KeyOf(flown), out byte whose) && c.Players.Any(p => p.Id == whose))
            {
                string key = KeyOf(flown);
                _toGoTo = ReferenceEquals(flown, stack) ? split : stack;
                Say($"{key} was {c.NameOf(whose)}'s before it docked: left for them to take back");
                over = true;
            }
            if (!over) continue;
            // Either half is whoever takes it from here on.
            Carried.Remove(KeyOf(stack));
            Carried.Remove(KeyOf(split));
            Halves.RemoveAt(i);
        }
    }

    // Claimed again until it is given, because a claim is said once and a wire loses things; and said
    // gone again for as long as the roster still has it down as this player's, for the same reason.
    private static void SayDockedAway(Client c, Dictionary<string, Vehicle> here)
    {
        double now = Wall();
        foreach ((string key, (double since, double next, bool said)) in DockedAway.ToArray())
        {
            byte owner = c.OwnerOf(key);
            // A craft stands under the name again: KSA hands a freed name to the next piece shed,
            // and said gone for ten seconds more that piece went out of every world but this.
            if (here.TryGetValue(key, out Vehicle? again) && World.IsAlive(again) && !ToRemove.Contains(again))
            {
                DockedAway.Remove(key);
            }
            else if (owner == c.Id)
            {
                c.SayGone(key);
                DockedAway[key] = (since, next, true);
            }
            else if (said || c.HeldBy(key) != 0 || now - since > DockedAwayForSeconds)
            {
                DockedAway.Remove(key);
            }
            else if (!said && now >= next)
            {
                c.Claim(key, controlling: false);
                DockedAway[key] = (since, now + DockedAwayClaimEverySeconds, false);
            }
        }
    }

    /// <summary>Two craft to dock at the next quiet window, for the bridge.</summary>
    public static void DockLater(Vehicle a, Vehicle b) => ToDock.Add((a, b));

    /// <summary>Whether any game in this world has claimed a name.</summary>
    public static bool SomebodyOwns(string key) => _client is { Joined: true } c && c.OwnerOf(key) != 0;

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
        byte owner = c.HeldBy(KeyOf(v));
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
                    // Either says the world is here: KSA saying it has finished loading one, which
                    // is the only word there is for a world with no craft in it, or a craft to fly,
                    // which is what said it before and still does if that patch did not take.
                    if ((_worldCame || World.AtACraft) && ++_settle > SettleFrames) StartPlaying(c);
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
            if (blob.Kind == BlobKind.Vehicle) (Definitions[blob.Key], DefinedAt[blob.Key]) = (blob.Data, blob.Universe);
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
        Say($"the world has come, {Blob.Size(world.Data.Length)}: loading it");
        (_loadingSince, _settle, _worldCame, Now) = (Wall(), 0, false, Stage.Loading);
        Puppets.Clear();
        ToPlace.Clear();
        ToOrbit.Clear();
        UnderWay.Clear();
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

    /// <summary>
    /// A world about to be loaded in this game that the mod did not ask for: the player opened a
    /// save of their own, or a tool did it for them.
    ///
    /// <para>This game cannot stay in the shared world across one. KSA destroys every vehicle and
    /// deserializes new objects, and everything this session knows a craft by -- what it has seen,
    /// built, claimed or made here -- is the identity of the old objects. So every craft of the
    /// loaded save reads as one this game has just made, and the next frame claims the lot, writes
    /// each one down and sends it to everybody: a single-player save uploaded into the room, which
    /// is the same cascade a night of debris was spent on. Leaving is the honest answer, since the
    /// world in front of this player is no longer the one the others are in.</para>
    ///
    /// <para>The mod's own loads say <see cref="Stage.Loading"/> before they start, which is how
    /// this tells the server's world from the player's.</para>
    /// </summary>
    // Set by a patch when KSA has finished putting a save into this game, and read by the loading
    // stage as the world having arrived.
    private static bool _worldCame;

    /// <summary>
    /// KSA has finished loading a save. While this mod is waiting on one, that is the world being
    /// here -- which is the only thing that says so for a world with nothing in it.
    ///
    /// <para>What used to say it was a craft to fly. Every world handed over had one, so waiting
    /// for one worked until a world did not: an empty one never finished loading at all, and the
    /// window sat on "loading the world..." until the three minutes ran out and it gave up.</para>
    /// </summary>
    public static void WorldHasLoaded()
    {
        if (Now == Stage.Loading) _worldCame = true;
    }

    public static void WorldLoadedHere()
    {
        if (Now != Stage.Playing) return;
        Say("a save was loaded in this game, which the others are not in: leaving the shared world");
        Disconnect();
    }

    private static void StartPlaying(Client c)
    {
        Renamed.Clear();
        ForgetDockings();
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
        Log.Info($"sent the server a save of this world, {Blob.Size(save.Zip.Length)} at {save.Universe:F1} s");
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

        // What the rebuild wait pushed back, now that it is over: put back at the head of the queue
        // so it is taken exactly as the word of its owner, and overwritten by a fresher word if one
        // is behind it.
        foreach ((string key, byte[] data) in Deferred.ToArray())
        {
            if (Wall() - Rebuilt.GetValueOrDefault(key) < StandingWait(key)) continue;
            Deferred.Remove(key);
            if (!here.TryGetValue(key, out Vehicle? waited) || Owned.ContainsKey(key) || MadeHere.Contains(waited))
            {
                // No longer ours to rebuild: swept, taken over, or gone.
                continue;
            }
            Rebuilt[key] = Wall();
            Definitions[key] = data;
            DefinedAt[key] = DeferredAt.GetValueOrDefault(key);
            Log.Info($"taking up the word about {key} that the rebuild wait held back: {data.Length} bytes");
            Puppets.Remove(waited);
            Built.Remove(key);
            Later(waited);
            here.Remove(key);
            Rebuilding.Add(key);
        }

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
            // filled a world with exploding copies. Kept rather than dropped, and taken up when the
            // wait is over, because the craft may never change shape again and the last word about
            // it is the one that matters.
            if (standing && Wall() - Rebuilt.GetValueOrDefault(blob.Key) < StandingWait(blob.Key))
            {
                Deferred[blob.Key] = blob.Data;
                DeferredAt[blob.Key] = blob.Universe;
                continue;
            }
            Deferred.Remove(blob.Key);
            Definitions[blob.Key] = blob.Data;
            DefinedAt[blob.Key] = blob.Universe;
            // Bound again rather than taken from the flag: standing can only be true where the
            // lookup above found one, but that is a bool by now and nothing can see through it.
            if (standing && old is { } was)
            {
                Rebuilt[blob.Key] = Wall();
                // Said because one session rebuilt a single fragment of another player's rocket 89
                // times, and the guard above means each of those definitions really did differ --
                // or the one before it had been forgotten. Which of the two it is decides the fix.
                Log.Info($"rebuilding {blob.Key}: {(had is null ? "nothing was known of it" : $"{had.Length} bytes became {blob.Data.Length}")}");
                Puppets.Remove(was);
                Built.Remove(blob.Key);
                Later(was);
                here.Remove(blob.Key);
                Rebuilding.Add(blob.Key);
            }

        }
        while (c.Gone.TryDequeue(out string? key))
        {
            Definitions.Remove(key);
            Deferred.Remove(key);
            Rebuilding.Remove(key);
            Describes.Remove(key);
            DefinedAt.Remove(key);
            DeferredAt.Remove(key);
            BuiltFrom.Remove(key);
            PieceWaiting.Remove(key);
            if (here.TryGetValue(key, out Vehicle? gone) && !Owned.ContainsKey(key) && !MadeHere.Contains(gone))
            {
                if (PuppetOf.Remove(key, out byte theirs)) WentAs[key] = (theirs, Wall());
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
                Deferred.Remove(key);
                Describes.Remove(key);
                PlacedAt.Remove(key);
                Seen.Remove(v);
                if (_claimed == key) _claimed = "";
                here.Remove(key);
                Later(v);
                gone++;
            }
            // Whatever was swept, and even if this game held none of it: what the craft left behind
            // outlives them, and goes on being stepped and drawn with nothing left to have made it.
            _toClearSmoke = true;
            Say(gone > 0
                ? $"the world was cleared of {gone} craft nobody is flying"
                : "the world was cleared, and this game had nothing matching to take away");
        }

        TakeOwnDockings(c);
        TakeDockings(c, here, flown);
        // What was flown may have been taken into another player's craft, or given up to them.
        if (flown is not null && (ToRemove.Contains(flown) || !ReferenceEquals(flown, World.Controlled))) flown = null;
        LeaveWhatWasTheirs(c, flown);
        if (_toGoTo is { } other)
        {
            _toGoTo = null;
            if (World.IsAlive(other) && World.GoTo(other)) flown = World.Controlled;
        }
        RideAlong(c, here, flown);
        SayNobodysGone(c, here);
        ClaimFlown(c, flown);
        Adopt(c, here, flown);
        HoldWhatIsNear(c, here, flown);
        // Every craft in this world while a run is recording, whoever's it is or nobody's: how far
        // apart two craft stand is asked of each game, and one of the two is often neither's.
        if (Motion.On)
        {
            foreach ((string key, Vehicle v) in here)
            {
                if (World.StateOf(v, key, controlled: false) is { } stands) Motion.Standing(stands, v.Parts.Count);
            }
        }
        SendOwned(c, here, flown);
        SayDockedAway(c, here);
        SayDockedHere(c);
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
        if (c.HeldBy(key) is var held && held != 0 && held != c.Id && !MadeHere.Contains(flown!) && !DockedHere.ContainsKey(key))
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
    // A name that says which rocket a piece came off answers for it either way: another player's, and it is
    // neither this game's nor anybody's to keep here; this player's, and it is theirs. Only a craft whose
    // name says nothing is settled by distance -- the one flown, or nearer to this player's craft than to
    // anyone else's.
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
            (byte Owner, string From)? shed = Whose(c, key);
            if (shed is { Owner: var owner, From: var from } && owner != c.Id)
            {
                // Their rocket shed it in this world, which is this world's business and nobody
                // else's: their own game sheds its own pieces and says so. Left standing it is an
                // orphan -- nothing drives it, their sweep does not name it, and it is in physics
                // and will hit something. Two games watching one rocket come apart filled a server
                // with these. Logged and not said, because a breakup makes dozens at a time and the
                // player did not do anything.
                Later(v);
                Log.Info($"{from} is {c.NameOf(owner)}'s, so {key} is not this game's to keep: taking it away");
                continue;
            }
            MadeHere.Add(v);
            // Came off a rocket of this player's, which settles it without measuring anything. The
            // rocket it came off is usually gone by the time its pieces are here, and a crash leaves
            // nothing else of this player's alive for a piece to be nearer to: measured against an
            // empty set every piece was further from this player than from anybody, so none was
            // claimed, none was described, and the others watched the rocket vanish whole and never
            // saw it come apart. The name is the surer answer in any case -- it is already the one
            // trusted a few lines up to give another player their debris.
            if (ReferenceEquals(v, flown) || shed is not null || SplitHere.Remove(v))
            {
                if (shed is { From: var hull }) ShedLately[hull] = Wall();
                Own(c, v, define: true, made: true);
                continue;
            }
            double mine = Owned.Values.Where(m => World.IsAlive(m.Craft)).Select(m => World.Between(m.Craft, v)).DefaultIfEmpty(double.PositiveInfinity).Min();
            double theirs = Puppets.Keys.Where(World.IsAlive).Select(p => World.Between(p, v)).DefaultIfEmpty(double.PositiveInfinity).Min();
            // Made here, so the server gives it a name of its own if another player has that one.
            if (mine < theirs) Own(c, v, define: true, made: true);
            // A piece, by its name, that came off nothing of this player's and is nearer somebody
            // else's craft: this world's own breaking of a copy, with nobody left to answer for
            // the name it came off. Kept, it stood in this world alone -- one part of a rocket
            // that had crashed in another game, there for good in the game that watched.
            else if (Wire.ShedFrom(key).Any())
            {
                MadeHere.Remove(v);
                Later(v);
                Log.Info($"{key} came off a craft that is not this player's, in this world alone: taking it away");
            }
        }
    }

    /// <summary>
    /// Whose rocket a craft came off and which of its names said so, or null where no name in its
    /// ancestry is one anybody owns. The nearest answer wins: a piece of a piece belongs to whoever
    /// owns the piece, not to whoever owned the stack it started as.
    /// </summary>
    private static (byte Owner, string From)? Whose(Client c, string key)
    {
        foreach (string from in Wire.ShedFrom(key))
        {
            string shared = Shared(from);
            if (Owned.ContainsKey(shared)) return (c.Id, shared);
            if (c.OwnerOf(shared) is var owner && owner != 0) return (owner, shared);
            // Another player's until a moment ago, and gone: a rocket that crashes is said gone in
            // the frame its pieces are shed, so the name that would have answered for a piece of
            // it shed here answers for nothing by the time the piece is looked at.
            if (WentAs.TryGetValue(shared, out (byte Owner, double At) went) && Wall() - went.At < WentForSeconds) return (went.Owner, shared);
        }
        return null;
    }

    // Other players' craft that have been said gone, by name: whose they were, and when. And whose
    // each craft held here for another player is, which the roster no longer says once it has gone.
    private static readonly Dictionary<string, (byte Owner, double At)> WentAs = [];
    private static readonly Dictionary<string, byte> PuppetOf = [];
    private const double WentForSeconds = 10.0;

    /// <summary>
    /// The name everybody shares for the craft this game calls <paramref name="local"/>, which is
    /// that name itself unless the craft has been renamed.
    ///
    /// <para>KSA builds a shed piece's name out of its parent's local <see cref="Vehicle.Id"/>,
    /// while both <see cref="Owned"/> and the server's roster are kept under the names the room
    /// shares. A renamed rocket's ancestry therefore appears in neither, and every piece it sheds
    /// is nobody's: not taken away when it is another player's, not claimed and described when it
    /// is this one's, and never looked at twice. A breakup renames nearly everything, since both
    /// games shed their own copy's pieces under the same names and whoever receives the other's
    /// definition second has to find a free one.</para>
    /// </summary>
    private static string Shared(string local)
    {
        Vehicle? dead = null;
        foreach (Vehicle v in Renamed.Keys)
        {
            if (v.Id != local) continue;
            // A live craft of that name answers over a destroyed one, whose name KSA may have handed
            // on already. A destroyed one still answers, though, and has to: a crash sheds its
            // debris and destroys the rocket in the same call, and the rename is not forgotten until
            // the next prune -- so the one case this whole rule exists for is the one where the
            // parent is gone by the time its pieces are looked at.
            if (World.IsAlive(v)) return KeyOf(v);
            dead = v;
        }
        return dead is null ? local : KeyOf(dead);
    }

    private static void Own(Client c, Vehicle v, bool define, bool made)
    {
        string key = KeyOf(v);
        Mine mine = new(v);
        Owned[key] = mine;
        WentAs.Remove(key);
        DockedAway.Remove(key);
        c.Claim(key, controlling: ReferenceEquals(v, World.Controlled), made: made);
        // A shape of 0 is one no craft has, so the sending round tries again. Mine records the
        // craft's shape as it is made, so a definition that failed here would otherwise never be
        // asked for again: the craft would be claimed, its states sent, its name in everybody's
        // roster, and no other game able to build it.
        if (define && !Define(c, v)) mine.Shape = 0;
    }

    /// <summary>Whether this craft was written down for the others, or will be asked for again.</summary>
    private static bool Define(Client c, Vehicle v)
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
            return false;
        }
        Offer(c, key, bytes);
        return true;
    }

    /// <summary>
    /// Whether a craft's name may be said again, asked before it is written down rather than after.
    ///
    /// <para>Writing one down is a whole serialization of its part tree and a gzip -- about 4 ms for
    /// a 138-part stack, 20 ms for a 741-part one -- and <see cref="Offer"/> would only then decide
    /// it was too soon to send. A rocket shedding parts changes shape most frames, so the work was
    /// paid every frame and about one result a window was ever used.</para>
    /// </summary>
    private static bool MaySayHow(string key) =>
        !Describes.TryGetValue(key, out Description? told)
        || told.At <= 0.0
        || Wall() - told.At >= RedescribeWait(key);

    /// <summary>
    /// A craft described to the others, at most once every <see cref="RedescribeEverySeconds"/> for
    /// any one name.
    ///
    /// <para>Because a rocket coming apart changes shape every few frames, and the shape is what
    /// asks for it to be written down again. Shedding a part does not remake the stack -- KSA's
    /// Split reduces the host's part tree in place and the Vehicle object carries on, which is why
    /// the shape is what notices at all -- but a craft destroyed and rebuilt is a new object, and a
    /// breakup described one craft over and over: watched on a real server, 23 times in six
    /// seconds, each copy relayed to every other game to be rebuilt. Which shape arrives matters
    /// far less than that the last one does, so the newest is held and sent when the name may speak
    /// again.</para>
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
        if (told.At > 0.0 && now - told.At < RedescribeWait(key))
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
            if (told.Waiting is not { } bytes || now - told.At < RedescribeWait(key)) continue;
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
    // A craft this game made is never taken on another player's word, because until the server has
    // answered for its name the name says nothing: another player's craft can be standing under it.
    // Once the server has said it is this player's the name is this craft's, and whoever has the
    // name next has this craft. Kept out of their hands for ever, a rocket one player launched and
    // another took over stood in its maker's world as it was left, 89 parts and 14 m from where its
    // new owner had it, whatever was done with it.
    private static void HandedOn(Mine m)
    {
        if (m.Granted) MadeHere.Remove(m.Craft);
    }

    private static void SendOwned(Client c, Dictionary<string, Vehicle> here, Vehicle? flown)
    {
        foreach (string key in c.Owners.Where(o => o.Value == c.Id).Select(o => o.Key))
        {
            if (Owned.ContainsKey(key) || !here.TryGetValue(key, out Vehicle? taken)) continue;
            // The server gave this player a craft it took control of: it is no puppet now.
            Puppets.Remove(taken);
            // A shape of 0 is one no craft has, so the sending round writes this one down before it
            // starts sending states for it. Taken rather than claimed, it may never have been
            // described by this game, and a Mine starting at the craft's shape as it stands never
            // notices: the shape it compares against is already the answer.
            // One held only for being near is the craft every game already has, and is not written
            // down until it changes: forty parked craft would each be torn down and built again
            // in every other game for a player having walked past them.
            Owned[key] = new Mine(taken) { Shape = c.NearOf(key) ? World.Shape(taken) : 0 };
        }
        double now = Wall();
        int underWay = 0;
        foreach (Mine other in Owned.Values)
        {
            if (World.IsAlive(other.Craft) && !ReferenceEquals(other.Craft, flown) && World.UnderWay(other.Craft)) underWay++;
        }
        double underWayEvery = Math.Min(OwnedEverySeconds, Math.Max(UnderWayEverySeconds, underWay / MostUnderWayASecond));
        _earlyTokens = Math.Min(EarlyAtOnce, _earlyTokens + (Math.Clamp(now - _earlyAt, 0.0, 0.25) * EarlyASecond));
        _earlyAt = now;
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
                    Owned[key] = m = new Mine(again) { Shape = 0, Granted = m.Granted };
                }
                else
                {
                    Owned.Remove(key);
                    // Nothing left waiting to be said about it either: a definition held back for
                    // the window outlives the craft, since it is kept by name, and the window
                    // opening after the craft had gone sent everybody how to build something this
                    // game had just said was gone.
                    Describes.Remove(key);
                    // Not one a docking in this game took: the docking is what says that, and said
                    // gone first the craft is nobody's by the time the server hears it docked --
                    // which is then a docking of two craft neither of which is this player's.
                    if (c.Owns(key) && !DockedHere.Values.Any(d => d.Consumed == key)) c.SayGone(key);
                    continue;
                }
            }
            Vehicle v = m.Craft;
            byte owner = c.OwnerOf(key);
            if (owner == c.Id) m.Granted = true;
            if (owner != 0 && owner != c.Id && !DockedHere.ContainsKey(key))
            {
                // Taken by another player, who is flying it now.
                Owned.Remove(key);
                HandedOn(m);
                continue;
            }
            // Let go of by the server because this player walked away from it, and let go of here
            // too: a state sent for a craft nobody owns takes it straight back, and the two would
            // hand it to each other for ever. The same rule as the server's, so they agree on it.
            if (owner == 0 && !ReferenceEquals(v, flown) && KeyOf(v) != (World.Watched is { } at ? KeyOf(at) : "") && !DockedHere.ContainsKey(key))
            {
                Owned.Remove(key);
                HandedOn(m);
                continue;
            }
            // Every frame while a run is recording, not only the frames a state is sent on: what a
            // puppet is held against is where this craft was at the time it is drawn for.
            if (Motion.On && World.StateOf(v, key, ReferenceEquals(v, flown)) is { } where) Motion.Owned(where, ReferenceEquals(v, flown), v.Parts.Count);
            int shape = World.Shape(v);
            // The shape is recorded only once the craft has been written down, and only when the
            // window is open to write it: left unrecorded, this asks again next frame and the craft
            // goes out with the shape it has then rather than one captured frames earlier.
            // Not of a craft a docking has just given this player until the server says it is
            // theirs: how a craft is built is taken only from its owner, and is said once.
            bool answered = !DockedHere.TryGetValue(key, out (string Consumed, double Since, double SayNext, int Said) docking)
                            || (owner == c.Id && c.OwnerOf(docking.Consumed) == 0);
            if (shape != m.Shape && MaySayHow(key) && answered && Define(c, v)) m.Shape = shape;
            bool controlled = ReferenceEquals(v, flown);
            bool moving = World.UnderWay(v);
            // Said at once when it is set moving, and not at the next turn it was given while it lay still.
            if (moving && !m.UnderWay) m.SendNext = now;
            m.UnderWay = moving;
            double every = controlled ? ControlledEverySeconds : moving ? underWayEvery : OwnedEverySeconds;
            // A craft that has just been set moving is not left waiting out the half second it was
            // given while it stood still.
            if (m.SendNext - now > every) m.SendNext = now + (every * Random.Shared.NextDouble());
            Wire.State? stands = null;
            if (now < m.SendNext)
            {
                // Before its turn, where it is no longer where its last word would have it drawn.
                if (controlled || !moving || _earlyTokens < 1.0 || now - m.SaidAt < EarlyAfterSeconds || m.Said is not { } said) continue;
                if ((stands = World.StateOf(v, key, controlled)) is not { } isAt || !LeftItsPath(said, m.SaidBefore, isAt)) continue;
                _earlyTokens -= 1.0;
                m.SendNext = now;
            }
            // Counted on from when it was due, not from now. Set from now, the gap between sends is
            // however many whole frames the interval needs -- so twenty a second asked for on a
            // machine managing thirty frames came out as fifteen, and anything faster would land
            // under the rate its watchers need to draw it smoothly at all. Never further back than
            // one interval, or a game that was busy would try to make up a minute of sends at once.
            m.SendNext = Math.Max(now - every, m.SendNext) + every;
            if ((stands ?? World.StateOf(v, key, controlled)) is not { } state) continue;
            // Kept, where it is held only for being near, by being said to be.
            if (c.NearOf(key) && Near(v, flown)) state = state with { Flags = state.Flags | Wire.StateFlags.Near };
            c.SendState(state);
            (m.SaidBefore, m.Said, m.SaidAt) = (m.Said, state, now);
        }
    }

    // How far a craft nobody is flying may be from where its last word has it drawn before it is
    // said again ahead of its turn, how soon after that word, and how many such a second.
    private const double EarlyOutM = 0.25, EarlyAfterSeconds = 0.05, EarlyASecond = 200.0, EarlyAtOnce = 40.0;
    private static double _earlyTokens, _earlyAt;

    /// <summary>
    /// Whether a craft is no longer where its last word would have it drawn: carried on from that
    /// word at the speed it gave, changing as it had been between the last two.
    ///
    /// <para>Forty pieces of a rocket share two hundred states a second, a fifth of a second
    /// each, and most of them spend that falling exactly as their last word says. The ones that
    /// do not are the ones that have hit something, and those were drawn a metre or two out until
    /// their turn came. A turn is taken early by a piece that has left its path.</para>
    /// </summary>
    private static bool LeftItsPath(Wire.State said, Wire.State? before, Wire.State at)
    {
        if (said.Body != at.Body) return true;
        double dt = at.Universe - said.Universe;
        if (dt <= 0.0) return false;
        Vec3 guess = said.PositionCcf + (said.VelocityCcf * dt);
        if (before is { } b && b.Body == said.Body && said.Universe - b.Universe is > 1e-6 and < 0.6)
        {
            guess += (said.VelocityCcf - b.VelocityCcf) * (0.5 * dt * dt / (said.Universe - b.Universe));
        }
        return (at.PositionCcf - guess).Length > EarlyOutM;
    }

    // How far from what a player flies or watches a craft is held for being near. KSA steps every
    // craft in a world, wherever it is: held by that alone, the first game into a world held all
    // of it, and said twice a second where each craft in it was.
    private const double NearWithinM = 10_000.0;

    /// <summary>
    /// Whether a craft is being stepped by this game within reach of what its player flies, or
    /// watches while flying nothing -- or is moving under this game's physics wherever it is.
    /// A craft at rest or coasting goes the same way in every world and needs nobody; one that is
    /// rolling down a hill with nobody near goes its own way in each.
    /// </summary>
    private static bool Near(Vehicle v, Vehicle? flown)
    {
        // Not by a game that is only watching another player's craft: that player's game is there,
        // and what is near their craft is theirs to hold. Held by the watcher, a piece of old
        // wreckage their rocket fell on was struck in one game and held in the other, each working
        // out its own half of the blow, and was put 5 m to make the two agree.
        // Asked of the roster as well as of what is held here for them: a copy built again is not
        // held for anybody until the frame after, and in that frame a watcher took the rocket
        // the player it was watching was about to dock with.
        if (flown is null && World.Watched is { } watched && _client is { } c
            && (IsTheirs(watched) || (c.HeldBy(KeyOf(watched)) is var whose && whose != 0 && whose != c.Id))) return false;
        return World.InBubble(v) && (World.UnderWay(v) || ((flown ?? World.Watched) is { } by && World.IsAlive(by) && World.Between(by, v) < NearWithinM));
    }

    /// <summary>
    /// Asks for every craft within this game's physics that nobody has.
    ///
    /// <para>A craft nobody owns is stepped by each game that is near it, each in its own way: a
    /// rocket nobody flew was 3.4 m from itself in two games after a walk of warps, so one player
    /// saw a capsule dock that far from the port, and a parked craft one player drives into moves
    /// in that player's world alone. Held by one game, it is where that game has it for everybody.
    /// The server gives such a hold up to whoever asks to fly the craft, and lets it lapse when
    /// its holder stops saying the craft is near.</para>
    /// </summary>
    private static void HoldWhatIsNear(Client c, Dictionary<string, Vehicle> here, Vehicle? flown)
    {
        double now = Wall();
        if (now < _nearNext) return;
        _nearNext = now + NearEverySeconds;
        foreach ((string key, Vehicle v) in here)
        {
            if (ReferenceEquals(v, flown) || Owned.ContainsKey(key) || Puppets.ContainsKey(v) || ToRemove.Contains(v)) continue;
            if (c.OwnerOf(key) != 0 || DockedAway.ContainsKey(key) || !Near(v, flown)) continue;
            c.Claim(key, controlling: false, made: false, near: true);
        }
    }

    /// <summary>
    /// Craft nobody owned that KSA has taken out of this world since it was last looked at: said
    /// gone, as their owner would have said had they had one.
    ///
    /// <para>What nobody owns was each world's own to destroy. A piece of wreckage whose owner had
    /// left was expired by each game when that game liked, and thirteen stood on in one world of
    /// two. Only a craft's owner may say it is gone, so it is claimed first. Not a great many at
    /// once: that is a world being unloaded and not a world losing craft.</para>
    /// </summary>
    private static void SayNobodysGone(Client c, Dictionary<string, Vehicle> here)
    {
        List<string> gone = [];
        foreach ((Vehicle v, string key) in Nobodys)
        {
            if (World.IsAlive(v) || TakenAway.Contains(v) || here.ContainsKey(key) || c.HeldBy(key) != 0 || Consumed.ContainsKey(key)) continue;
            gone.Add(key);
        }
        // Most of what there was, and not merely many: KSA frees the places old wreckage holds
        // when a new breakup needs them, a dozen at a time.
        if (gone.Count > MostGoneAtOnce && gone.Count * 2 > Nobodys.Count) Log.Warn($"{gone.Count} craft nobody owns left this world at once: not said gone");
        else
        {
            foreach (string key in gone)
            {
                DockedAway[key] = (Wall(), 0.0, false);
                Log.Info($"{key} was nobody's and has gone from this world: claiming it to say so");
            }
        }
        Nobodys.Clear();
        TakenAway.RemoveWhere(v => !World.IsAlive(v));
        foreach ((string key, Vehicle v) in here)
        {
            // Or held by another game only for being near it, which is nobody's as much: that
            // game cannot say how to build it again, since it never wrote it down, so a piece KSA
            // took out of this world to make room stood on in the other for good.
            if (Owned.ContainsKey(key) || ToRemove.Contains(v) || MadeHere.Contains(v) || c.HeldBy(key) != 0) continue;
            Nobodys[v] = key;
        }
    }

    // Every craft another player owns, as a puppet here: built from its definition if this world has none, pulled in physics, placed by its orbit without.
    private static void PlaceOthers(Client c, Dictionary<string, Vehicle> here, Vehicle? flown)
    {
        HashSet<Vehicle> wanted = [];
        Hurried.Clear();
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
                if (Consumed.ContainsKey(key)) continue;
                if (track.Newest is not null && Definitions.ContainsKey(key) && WaitsForItsHull(key, here, now)) continue;
                // One a frame: each is a craft built.
                if (builtOne || track.Newest is null || !Definitions.TryGetValue(key, out byte[]? definition)) continue;
                // And one every few seconds per name, however often it goes. The last limit only
                // caught a craft that was still standing here, and these are not: each copy is
                // dropped in, explodes at once, and the next definition finds nothing standing and
                // builds it again. 429 builds of 95 craft in one session, with the limit in.
                if (!Rebuilding.Contains(key) && Wall() - Rebuilt.GetValueOrDefault(key) < RebuildEverySeconds) continue;
                Rebuilding.Remove(key);
                builtOne = true;
                // Asked for rather than done: a craft is added to the world where KSA is not
                // walking its vehicles. It stands here from the next frame, found through Built.
                ToBuild[key] = (definition, c.NameOf(owner));
                continue;
            }
            if (universe - track.HeardAt > lost) continue;
            wanted.Add(v);
            Puppets[v] = key;
            PuppetOf[key] = owner;
            if (track.At(universe) is not { } s) continue;
            bool lit = (s.Flags & Wire.StateFlags.EngineOn) != 0;
            World.SetLights(v, (s.Flags & Wire.StateFlags.Lights) != 0);
            // The input, which lights the engine for real in a craft this game is stepping, and the
            // word for the renderer, which is all there is for one it is not.
            World.SetEngine(v, lit, s.Throttle);
            Engines[v] = (lit, s.Throttle);
            // Or when this game has it going round another body than its owner says: that is the one
            // case the pull cannot answer, and it does not fix itself. A craft standing on a pad is
            // inside the Earth's physics radius, where KSA's own SOI check gives up on its first
            // line, so the copy can never follow its owner to the Moon by itself.
            bool elsewhere = World.BodyOf(v) is { } at && at != s.Body;
            if ((elsewhere || !v.HasPhysicsBubble) && now >= PlacedAt.GetValueOrDefault(key))
            {
                PlacedAt[key] = now + PlaceEverySeconds;
                ToPlace[key] = (v, s);
            }
            // Or coasting on its owner's game and not where its owner has it. Nothing the pull
            // writes takes on a craft nothing is pushing: KSA steps it along its own orbit and the
            // position and speed written into it each step are gone by the next, so a craft left
            // half a metre a second out when its owner's engine cut drifted away for good, with
            // the pull putting it back 120 times a second to no effect. What moves such a craft is
            // its orbit, and its owner's newest word is one: where it was and how fast, at a time.
            else if (track.Newest is { } said && (said.Flags & Wire.StateFlags.Rails) != 0 && (said.Flags & Wire.StateFlags.Landed) == 0)
            {
                // And at once when its owner has just stopped pushing it, however near it stands.
                // What the pull leaves a craft with as it lets go is a speed with the last of a
                // correction still in it, and on a craft nothing pushes that is an orbit a little
                // off its owner's: a tenth of a metre a second, and the first metre out a step
                // somebody sees. Its owner's first word of coasting is the orbit it is on.
                // Only against a place it was told to stand now. Carried on as far as it may be, or
                // this world too far behind to be told, the place is for another time and a craft
                // standing where it should looks kilometres from it.
                // Nor a craft that is another shape here than its owner has it: its centre is not theirs.
                // Looked at this often, and sooner for a craft that is far out: one knocked by
                // another copy built against it as the two undocked left at 4 m/s, and waiting
                // its three seconds it was 13 m out and had been put back 250 times to no effect.
                bool due = now >= PlacedAt.GetValueOrDefault(key);
                bool soon = now >= PlacedAt.GetValueOrDefault(key) - (CoastingEverySeconds - CoastingFarEverySeconds);
                if ((due || soon) && Math.Abs(s.Universe - universe) < 1e-6
                    && !StillAnotherShape(v, said.Parts)
                    && World.StateOf(v, key, controlled: false) is { } stands)
                {
                    double outM = (stands.PositionCcf - s.PositionCcf).Length;
                    // Or put back at the last step, which is a craft that will be put back at every
                    // step: looked at here it stands where the pull has just written it, and at the
                    // next step KSA has it on its old orbit again. A craft its owner moved to
                    // another orbit while it was in this world's physics was put 11,000 km at
                    // every step for seventeen seconds, and never given the orbit.
                    bool putBack = PutBack.TryRemove(key, out _);
                    if (outM > CoastingFarM || putBack || (due && (UnderWay.Remove(key) || outM > CoastingOutM)))
                    {
                        PlacedAt[key] = now + CoastingEverySeconds;
                        ToOrbit[key] = (v, said, outM);
                    }
                }
            }
            else UnderWay.Add(key);
        }
        foreach (Vehicle v in Puppets.Keys.Where(p => !wanted.Contains(p)).ToArray()) Puppets.Remove(v);
        foreach (Vehicle v in Engines.Keys.Where(p => !Puppets.ContainsKey(p)).ToArray()) Engines.Remove(v);
        UnderWay.RemoveWhere(k => !Puppets.ContainsValue(k));
        foreach (string k in Built.Where(p => !World.IsAlive(p.Value)).Select(p => p.Key).ToArray()) Built.Remove(k);
        foreach (Vehicle v in Renamed.Keys.Where(x => !World.IsAlive(x)).ToArray()) Renamed.Remove(v);
        foreach (Vehicle v in Seen.Where(x => !World.IsAlive(x)).ToArray()) Seen.Remove(v);
        foreach (Vehicle v in MadeHere.Where(x => !World.IsAlive(x)).ToArray()) MadeHere.Remove(v);
        foreach (Vehicle v in Unpulled.Keys) if (!World.IsAlive(v)) Unpulled.TryRemove(v, out _);
        foreach (Vehicle v in OtherShape.Keys) if (!World.IsAlive(v)) OtherShape.TryRemove(v, out _);
        SplitHere.RemoveWhere(v => !World.IsAlive(v));
    }

    /// <summary>
    /// Whether a piece is not to be built yet, because the craft it came off still stands here
    /// with that piece on it.
    ///
    /// <para>A piece that breaks off is described at once and built here at once, and the hull it
    /// left is built again up to two seconds later. Until then the hull's copy still has the
    /// piece on it, the piece's copy is built inside that, and KSA's collisions hold it there:
    /// told to fall at 10 m/s it moved at one, and was put where it belonged once it was five
    /// metres out. So the piece is built when the hull is built from a word its owner wrote after
    /// the piece's own, and until then it is still to be seen where it was, on the hull.</para>
    /// </summary>
    private static bool WaitsForItsHull(string key, Dictionary<string, Vehicle> here, double now)
    {
        double written = DefinedAt.GetValueOrDefault(key);
        foreach (string from in Wire.ShedFrom(key))
        {
            // A piece's name is its hull's and a number after an underscore. A name the server
            // made out of another's, "Rocket 2", is not a piece of it.
            if (key.Length <= from.Length || key[from.Length] != '_') break;
            if (!here.TryGetValue(from, out Vehicle? hull) || !World.IsAlive(hull) || !Puppets.ContainsKey(hull)) continue;
            if (BuiltFrom.GetValueOrDefault(from, double.NegativeInfinity) >= written) break;
            // Kept once it has run out, until the piece is built: taken away then, a piece that
            // was not the one built that frame began its five seconds again, and thirty of them
            // came out one every five seconds.
            if (!PieceWaiting.TryGetValue(key, out double since)) PieceWaiting[key] = since = now;
            if (now - since < PieceWaitsSeconds)
            {
                Hurried.Add(from);
                return true;
            }
            break;
        }
        return false;
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
            if (owner == 0 || owner == c.Id || Owned.ContainsKey(key) || c.NearOf(key)) continue;

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
        Track track = c.TrackOf(key);
        if (track.At(World.Universe) is not { } s) return;
        World.Pulled? pulled = World.Pull(v, s, dt, first: Unpulled.TryGetValue(v, out double built) && Wall() - built < BornWithinSeconds,
                                          patient: StillAnotherShape(v, s.Parts));
        // A piece of a rocket is built where it was when its owner described it, a few frames or
        // half a second ago, and thirty of them are built one a frame: drawn there over a few
        // steps, each started its life with a step of metres that everybody saw.
        // Not by a pull that was held: a copy built while this world was behind has not been put
        // anywhere yet, and is still owed its first place.
        if (pulled is { Held: false }) Unpulled.TryRemove(v, out _);
        if (pulled is { Snap: true } && (s.Flags & Wire.StateFlags.Rails) != 0 && (s.Flags & Wire.StateFlags.Landed) == 0) PutBack[key] = 0;
        if (Motion.On && pulled is { } found) Motion.Puppet(key, World.Universe, dt, found, s, track.HeardAt);
    }

    // The speed this world runs at while connected: the server's time kept, warped forward to catch up after a load.
    private static void HoldTheClock(Client c)
    {
        // Held wherever this world runs, at a craft or at none. A game whose camera is on a planet,
        // or whose kitten has died, flies and watches nothing and still draws every other player's
        // craft, at its own universe time: let go of there, it kept real time while the room warped
        // and drifted from it while the room did not, and drew a craft in orbit kilometres from
        // where its owner had it.
        if (Now is Stage.Loading or Stage.Joining || World.InEditor)
        {
            // The editor is the one place KSA stops the world itself (Program.OnFrameEditor), so
            // there is nothing to hold there and nothing to be late by. Let go of at real time and
            // not at whatever this world was last run at, which after a join is the catch-up warp.
            // KSA's own load is left to run as it does.
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
        // player who was not looking able to come back rather than having to sprint.
        double most = World.Analytic ? ClockFollow.MostCatchUpOnRails : ClockFollow.MostCatchUp;
        HeldSpeed = ClockFollow.Asked(ClockFollow.Speed(behind, server.Rate, most), FrameSeconds, World.StepCap);
        World.SetSpeed(HeldSpeed.Value);
    }

    /// <summary>
    /// How long a name is asked about for a port before the default one is used. Short because it is
    /// spent before the first datagram goes anywhere, and a player watching a window that says
    /// "looking up" has no way to tell a slow resolver from a server that is not there.
    /// </summary>
    private static readonly TimeSpan SrvWait = TimeSpan.FromSeconds(1.5);

    private static async Task<IPEndPoint?> Find(string host, int port, bool portGiven)
    {
        if (IPAddress.TryParse(host, out IPAddress? ip)) return new IPEndPoint(ip.MapToIPv4(), port);
        // Only when nobody said which port. A port typed after the name is the player being specific,
        // and a record in somebody else's zone does not get to overrule them.
        if (!portGiven && await Srv.LookupAsync(host, SrvWait) is { } said) (host, port) = (said.Host, said.Port);
        IPAddress[] found = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork);
        return found.Length > 0 ? new IPEndPoint(found[0], port) : null;
    }

    /// <summary>A server's address read: "host:port", or a host alone for the default port.</summary>
    public static bool TryAddress(string text, out string host, out int port) =>
        TryAddress(text, out host, out port, out _);

    /// <param name="portGiven">Whether the port was typed. A name without one is a name worth asking
    /// an SRV record about, since one machine holds several worlds and only a record says which port.</param>
    /// <inheritdoc cref="TryAddress(string, out string, out int)"/>
    public static bool TryAddress(string text, out string host, out int port, out bool portGiven)
    {
        text = text.Trim();
        (host, port, portGiven) = (text, DefaultPort, false);
        int colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon)
        {
            host = text[..colon];
            if (!int.TryParse(text[(colon + 1)..], out port) || port is < 1 or > 65535) return false;
            portGiven = true;
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
        // Out before in: a name freed this frame is a name the next craft may want. Taken away
        // whatever this game is doing, because a craft waiting in here is one this world should not
        // have -- debris its copy of somebody's rocket shed, a hull being rebuilt, something swept
        // -- and the quiet window is the only place it may go. Disconnecting used to empty the queue
        // instead, which left every one of them standing in a world with nothing to drive them.
        foreach (Vehicle going in ToRemove)
        {
            if (World.IsAlive(going)) World.Destroy(going);
        }
        ToRemove.Clear();
        if (_toClearSmoke)
        {
            _toClearSmoke = false;
            World.ClearSmoke();
        }
        // Asked for from outside the game and applied here rather than where the command was read:
        // shedding debris and destroying a craft both add to and remove from the world's vehicles,
        // and the frame hook the bridge is served from is not a moment when that list may change.
        foreach ((Vehicle craft, int parts, bool whole) in ToFail)
        {
            if (!World.IsAlive(craft)) continue;
            if (World.Fail(craft, parts, whole) is { Length: > 0 } why) Log.Warn($"could not fail parts on {KeyOf(craft)}: {why}");
        }
        ToFail.Clear();
        foreach ((Vehicle one, Vehicle other) in ToDock)
        {
            if (!World.IsAlive(one) || !World.IsAlive(other)) continue;
            if (World.Dock(one, other) is { Length: > 0 } why) Log.Warn($"could not dock {KeyOf(one)} with {KeyOf(other)}: {why}");
        }
        ToDock.Clear();
        if (Now != Stage.Playing) return;
        foreach ((Vehicle craft, Wire.State state) in ToPlace.Values)
        {
            // Asked a frame ago, so asked of a craft that may since have been taken away.
            if (!World.IsAlive(craft) || !Puppets.ContainsKey(craft)) continue;
            if (World.BodyOf(craft) is { } at && at != state.Body)
            {
                if (World.Rebase(craft, state)) Log.Info($"{KeyOf(craft)} is round {state.Body} now, and was round {at}");
                continue;
            }
            // Only where the solver has not given it a bubble since, which is the one thing the
            // analytic placing must not fight.
            if (craft.HasPhysicsBubble) continue;
            World.PlaceAnalytic(craft, state, PlaceNearEnoughM);
        }
        ToPlace.Clear();
        foreach ((string key, (Vehicle craft, Wire.State said, double outM)) in ToOrbit)
        {
            if (!World.IsAlive(craft) || !Puppets.ContainsKey(craft)) continue;
            if (World.Rebase(craft, said)) Log.Info($"{key} was {outM:0.0} m off its owner's orbit, and is put on it");
        }
        ToOrbit.Clear();
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
                BuiltFrom[key] = DefinedAt.GetValueOrDefault(key);
                PieceWaiting.Remove(key);
                Unpulled[built] = Wall();
                Log.Info($"built {key}, {owner}'s{(localId != key ? $", called {localId} here" : "")}");
                // And put where its owner has it before it is first drawn. What it is built from
                // says where it was when that was written, and a kitten out of a hatch in orbit
                // was drawn for its first two frames a kilometre and a half from the capsule.
                // Or lying where its owner has it, where that is not where it was built: a piece
                // that waited for its hull was drawn for a frame thirteen metres up, where it
                // had broken off.
                if (_client?.TrackOf(key).Newest is { } said)
                {
                    if ((said.Flags & Wire.StateFlags.Landed) == 0) World.Rebase(built, said);
                    else World.SetDown(built, said, SetDownBeyondM);
                }
                // And looked at again soon, as a craft whose owner has just stopped pushing it is.
                // The word it was put by may be from before it was this craft: a capsule built
                // as it undocked was put on the orbit it had before the push that parted them,
                // was 4 m out three seconds later when a coasting craft is next looked at, and
                // was put right in one step.
                PlacedAt[key] = Wall() + BuiltLookAgainSeconds;
                UnderWay.Add(key);
            }
            else
            {
                Say($"{owner}'s {key} cannot be built here: {why}");
                Definitions.Remove(key);
                Deferred.Remove(key);
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
        TakenAway.Add(v);
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
