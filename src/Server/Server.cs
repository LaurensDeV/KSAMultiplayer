using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace KSAMultiplayer.Server;

/// <summary>How a server is set up: from the command line or its config file.</summary>
public sealed record ServerOptions
{
    public int Port { get; init; } = Wire.DefaultPort;
    public string Name { get; init; } = "KSA server";
    public string Password { get; init; } = "";
    public int MaxPlayers { get; init; } = 16;

    /// <summary>The KSA build every player must run; empty to take the build of whoever gave the server its world.</summary>
    public string KsaBuild { get; init; } = "";

    /// <summary>
    /// The mods every player must have and no more, separated by commas; empty to take those of
    /// whoever gave the server its world.
    ///
    /// <para>Worth pinning because the world's own list follows whoever last handed one over, and a
    /// server asks a player for a fresh save every few minutes: one player joining with a mod
    /// enabled writes it into what the world wants, and from then on nobody without it can join.</para>
    /// </summary>
    public string Mods { get; init; } = "";

    /// <summary>
    /// Mods a player may have enabled or not, separated by commas, at any version: ones that change
    /// how this player's own game works and write nothing another game cannot read, such as an
    /// editor without its limits on scaling. Never in what the world wants, whoever gives it, and
    /// never a reason to turn a player away. A mod <see cref="Mods"/> names as well is required.
    /// </summary>
    public string OptionalMods { get; init; } = "";

    /// <summary>Seconds between asking a player for a fresh save of the world, so a player joining starts near now.</summary>
    public double SnapshotEverySeconds { get; init; } = 300.0;

    /// <summary>
    /// Seconds the players are counted down before the server stops. A redeploy gave them a
    /// "lost connection" in the middle of a flight, and two minutes of play went with it.
    /// </summary>
    public double StopNoticeSeconds { get; init; } = 15.0;

    /// <summary>Whether universe time runs with nobody playing; held by default, so a save handed out is never far behind.</summary>
    public bool KeepTime { get; init; }

    /// <summary>
    /// Whether kittens need food, water and air, and whether they take a dose from radiation: KSA's
    /// two rules, which each game otherwise takes from its own settings. One answer for the room,
    /// said with the clock.
    /// </summary>
    public bool LifeSupport { get; init; } = true;
    public bool Radiation { get; init; } = true;

    /// <summary>Whether every game clears debris left on a launchpad for five minutes, and a craft nobody has flown or been near for as long when its site is full.</summary>
    public bool ClearPads { get; init; } = true;

    /// <summary>How long everyone has to answer a warp before the ask runs out. Saying nothing is not agreeing.</summary>
    public double WarpAnswerSeconds { get; init; } = 30.0;

    /// <summary>
    /// How long a craft stays its owner's while they are neither in it nor looking at it. Sticky
    /// ownership is what stops two games pulling one hull in two directions; sticky for ever meant
    /// the first player to touch a thing held it until they left, and a second could fly nothing.
    /// </summary>
    public double LapseWithin { get; init; } = 10.0;

    /// <summary>
    /// How long a player's craft are kept for them after they leave, 0 for not at all. A game that
    /// drops comes back within seconds, and its craft let go of at once went to whoever was watching.
    /// </summary>
    public double KeptAway { get; init; } = 120.0;

    /// <summary>
    /// How long a player must have watched a craft before it is let go of for them. Flicking the
    /// camera through the vessel list is looking, not wanting: one player's minute of it freed
    /// thirty craft, and those were handed round for twenty minutes after.
    /// </summary>
    public double WatchToTake { get; init; } = 3.0;

    /// <summary>
    /// Whether players' games may hand this server their logs, which it keeps for two weeks. On for
    /// the developer's test server alone: the games send to nothing else whatever this says.
    /// </summary>
    public bool CollectLogs { get; init; }

    /// <summary>The most craft one player holds at once: each is kept here, and told to every game.</summary>
    public int MaxCraftPerPlayer { get; init; } = 2048;

    public double Speed { get; init; } = 1.0;

    /// <summary>
    /// The celestial system of the empty world this server starts with when it has none, as KSA
    /// names it; empty to wait for the first player to hand theirs over instead. Also the world
    /// <c>fresh</c> puts in place of the one there is.
    /// </summary>
    public string EmptyWorld { get; init; } = "";

    /// <summary>
    /// Whether this server is announced to directories, so players can find it without being told its
    /// address. Off unless an operator says so: being listed publishes the address the server is
    /// reached at, which is a decision for whoever runs the machine and not for whoever wrote this.
    /// </summary>
    public bool Advertise { get; init; }

    /// <summary>The name to be listed under, empty to be listed at whatever address a directory sees an announcement arrive from.</summary>
    public string AdvertiseAs { get; init; } = "";

    /// <summary>
    /// The KSA build this server was built for: the one its own release of the mod was compiled
    /// against. A server has no game to ask, so this is written into it when it is built. Empty
    /// in one that was built without it, which then goes by its world and its players.
    /// </summary>
    public string BuiltFor { get; init; } = "";

    /// <summary>
    /// What this server is called where a name that never changes is wanted, as its name may and
    /// its address may: a mod manager knows a list of mods it has made a game from by it. The
    /// operator's to say and nothing the server makes up or keeps, since a world is thrown away
    /// and begun again far more often than a server becomes another server.
    /// </summary>
    public string Id { get; init; } = "";

    /// <summary>Where to announce to, separated by commas; empty for the built-in list.</summary>
    public string Directories { get; init; } = "";

    /// <summary>
    /// The login service whose certificates say who a player is on Discord; empty to take none. Asked
    /// only while some Discord user is an admin here: a server with nobody to recognise has no
    /// reason to call anybody.
    /// </summary>
    public string DiscordLogin { get; init; } = Discord.Service;

    /// <summary>
    /// Discord users who are admins here whatever admins.json says: an operator with several servers
    /// says it once, where they say everything else, rather than at each console after each reset.
    /// Not taken away by deop, which says where they come from.
    /// </summary>
    public string[] DiscordAdmins { get; init; } = [];

    /// <summary>Discord users who are trusted here whatever admins.json says, as <see cref="DiscordAdmins"/> are admins.</summary>
    public string[] DiscordTrusted { get; init; } = [];

    /// <summary>
    /// Discord users who are operators here: admins who may also have the server's log. Only ever by
    /// this setting and a Discord login, never by a command or a token, so being one is never more
    /// than this list says now. Listed here and in another list too, they are an operator.
    /// </summary>
    public string[] DiscordOperators { get; init; } = [];
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
        /// <summary>The system this game is in: the one it joined from, then that of the world it last loaded.</summary>
        public string System { get; set; } = "";
        public Link Link { get; } = new();

        /// <summary>Which commands this player's are run for: what the token offered at the join, or their Discord login, made them.</summary>
        public Wire.Role Role { get; set; }

        public bool Admin => Role >= Wire.Role.Admin;

        /// <summary>
        /// What this player's game is being told of being an admin, until it says the same back, and
        /// when to say it next. A token on its way is held here and nowhere else: it has to be said
        /// again when a datagram is lost, and it is gone once the game has it or the player has left.
        /// </summary>
        public Wire.Op? Telling { get; set; }
        public double TellNext { get; set; }

        /// <summary>Whether it has loaded the world and is playing in it, and since when on the server's clock.</summary>
        public bool Loaded { get; set; }
        public double LoadedAt { get; set; }

        /// <summary>How many notes this game may still have written down, and when that was last counted.</summary>
        public double Notes { get; set; } = NotesAtOnce;
        public double NotesAt { get; set; } = now;

        /// <summary>How many deaths this game may still have put on the memorial, and when that was last counted.</summary>
        public double Deaths { get; set; } = DeathsAtOnce;
        public double DeathsAt { get; set; } = now;

        /// <summary>How far behind the server's time this player's world was when it last said, s.</summary>
        public double Late { get; set; }

        /// <summary>When it last said, on the server's clock, and what universe time the server had then; zero until it has.</summary>
        public double LateAt { get; set; }
        public double LateUniverse { get; set; }

        /// <summary>
        /// What share of the clock's advance between the last two times it said this game failed to
        /// keep: 0.3 is a world run at seven tenths of what it was asked for, negative is one
        /// catching up, and zero is one keeping pace or nothing to go on. A share of what the clock
        /// did rather than seconds of either kind, so it reads the same at every speed and through
        /// a change of speed.
        /// </summary>
        public double Slip { get; set; }

        /// <summary>
        /// The times the clock has gone on to the room that this game cannot yet have heard of, and
        /// by how much each: what it says of its lateness until then is said about a clock that is
        /// gone.
        /// </summary>
        public List<(double At, double By)> Unheard { get; } = [];

        /// <summary>The speed this player's game is managing, a multiple of real time; zero until it says.</summary>
        public double Can { get; set; }

        /// <summary>Whether their window has their attention; an unfocused one is throttled and is not asked to keep up.</summary>
        public bool Here { get; set; } = true;

        /// <summary>The craft their camera is on, which need not be the one they are flying.</summary>
        public string Watching { get; set; } = "";
        public double WatchingSince { get; set; }

        /// <summary>Waiting for a world to be sent: there is none yet, or a fresh one has been asked for.</summary>
        public bool WaitingForWorld { get; set; }
        public double WaitingSince { get; set; }

        /// <summary>When the world was last sent to this game, on the server's clock, and the universe time it was saved at; zero until one is.</summary>
        public double SentAt { get; set; }
        public double SentUniverse { get; set; }

        /// <summary>How far behind the server's time its world was when it said it was in, s: its lateness until it next says.</summary>
        public double LoadedBehind { get; set; }

        /// <summary>When it was last written down as too far behind to be handed a craft.</summary>
        public double ToldBehindAt { get; set; } = double.NegativeInfinity;

        /// <summary>Asked for the first save of the world: the server had none when this player joined.</summary>
        public bool Seeding { get; set; }

        /// <summary>
        /// Asked for a save of the world and has not sent one yet. A world is taken from a player
        /// only while this is set: one sent unasked would be written to disk along with the build and
        /// the mods its sender claimed, which everybody else is then held to.
        /// </summary>
        public bool WorldAsked { get; set; }

        /// <summary>When it was last asked, and in what words, so it can be asked again.</summary>
        public double WorldAskedAt { get; set; }
        public string WorldAskedWhy { get; set; } = "";

        /// <summary>When it was first asked of the asks it has not answered, however many times it was asked since.</summary>
        public double WorldAskedSince { get; set; }

        /// <summary>Whether the world has been handed to this player: nobody is in a world they were never given.</summary>
        public bool WorldGiven => !Seeding && !WaitingForWorld;

        public string Controlling { get; set; } = "";

        /// <summary>
        /// Whether the craft they fly says they are in the vehicle editor, where KSA writes no save:
        /// asked for one there, a game answers nothing, and a server stopping waited on it in vain.
        /// </summary>
        public bool Editing { get; set; }

        /// <summary>Whether their game says it is in the vehicle editor, which it does flying nothing as well.</summary>
        public bool InEditor { get; set; }

        /// <summary>Who this player logged in as on Discord, empty until they have.</summary>
        public string DiscordId { get; set; } = "";
        public string DiscordName { get; set; } = "";

        /// <summary>What this player's game signs to prove its Discord login here: made at the join, so a signature made for any other is no good.</summary>
        public string DiscordNonce { get; set; } = "";

        /// <summary>The last proof this player's game handed over, and the answer to it once there is one: a game says a proof until it is answered.</summary>
        public string DiscordProof { get; set; } = "";
        public Wire.DiscordUser? DiscordAnswer { get; set; }
        public double DiscordAt { get; set; } = double.NegativeInfinity;

        /// <summary>The player's round trip, s, as that player measured it; negative until it has one.</summary>
        public float Rtt { get; set; } = -1f;

        /// <summary>How many of their game's logs sent whole have been kept this session, as games of 0.37.0 and before send them.</summary>
        public int LogsKept { get; set; }

        /// <summary>The logs their game is sending in pieces this join, and how much of them has come.</summary>
        public Dictionary<string, PlayerLogs.Open> LogFiles { get; } = [];

        public long LogChars { get; set; }

        /// <summary>The share of this player's pings lost on the way over the last minute; NaN until there is enough to say.</summary>
        public double Loss => Pings.Share;

        public PingLoss Pings { get; } = new();

        /// <summary>
        /// The word its commands and its goodbye must carry, empty for a game from before there was
        /// one: a player is otherwise known only by the address a datagram says it is from.
        /// </summary>
        public string Session { get; init; } = "";

        /// <summary>Chat lines and commands it may still say, each filling again as time passes, and as of when.</summary>
        public double Chats = ChatsAtOnce, ChatsAt = now, Commands = CommandsAtOnce, CommandsAt = now;

        /// <summary>How many craft it held when they were last counted, with those given since, and when that was.</summary>
        public int Crafts { get; set; }
        public double CraftsAt { get; set; } = double.NegativeInfinity;

        /// <summary>When each limit it went over was last written in the log.</summary>
        public Dictionary<string, double> LimitSaid { get; } = [];
    }

    /// <summary>A craft the server knows of: who flies it and how it is built, if it was made after the world was saved.</summary>
    private sealed class Craft
    {
        public byte Owner;

        /// <summary>Who a craft owned by <see cref="Wire.Away"/> is kept for, by name and Discord id, and until when on the server's clock.</summary>
        public string KeptName = "", KeptDiscord = "";
        public double KeptUntil;
        public byte[]? Definition;
        public double Universe;

        /// <summary>When its owner was last in it or looking at it, on the server's own clock.</summary>
        public double AttendedAt;

        /// <summary>
        /// Whether it is held only for being within its holder's physics, and when the holder last
        /// said it was. Such a hold keeps a craft in one place for everybody and keeps nobody out
        /// of it.
        /// </summary>
        public bool Near;
        public double NearAt;

        /// <summary>
        /// Who it was let go of for, and when. Kept for them a moment: let go of, it was in reach of
        /// its last owner's physics, whose game held it again for being near before the watcher's
        /// had heard, and it went back where it came from.
        /// </summary>
        public byte FreedFor;
        public double FreedAt;
        public bool FreedSaid;

        /// <summary>The last player to fly it: held by them as well, it is their craft and not their debris.</summary>
        public byte FlownBy;

        /// <summary>When it last changed hands, on the server's clock; negative infinity for a craft that never has.</summary>
        public double TakenAt = double.NegativeInfinity;

        /// <summary>Who last said how it is built, and the universe time their game wrote that down at.</summary>
        public byte WrittenBy;
        public double Written;

        /// <summary>When that was heard, on the server's own clock.</summary>
        public double WrittenHeard;

        /// <summary>Where its owner last said it was, or null with nothing said: what a docking is held against.</summary>
        public Wire.State? Said;

        /// <summary>How many times its owner has said how it is built. Every one of these is the craft
        /// torn down and built again in every other game, so a craft described over and over is a
        /// fault worth seeing from here.</summary>
        public int Described;

        // States and mods' words about it that may still be passed on, and as of when.
        public double States, StatesAt = double.NegativeInfinity, Extras, ExtrasAt = double.NegativeInfinity;
    }

    private const double SilentSeconds = 10.0, LoadingSilentSeconds = 120.0;

    // What one craft passes on a second: states at three times what a game sends of the craft it
    // flies, and mods' words about it. A limit on a craft and not on a player, whose salvo of a
    // hundred craft is play.
    private const double StatesASecond = 60.0, ExtrasASecond = 120.0;

    // Chat lines and commands a player may say in a burst, and how fast each fills again: more than
    // anybody types, and far under what a loop sends. A command reads the saves folder and writes
    // the log. A game deleting many craft at once asks for each in its own command, and a rocket
    // that has fallen over is fifty pieces.
    private const double ChatsAtOnce = 10.0, ChatsASecond = 2.0, CommandsAtOnce = 250.0, CommandsASecond = 5.0;

    // Who holds what is told again no sooner than this after the last telling, and a hundredth of a
    // second later for every page of it: told at the next tick, a table of a thousand craft was sent
    // whole to everybody two hundred times a second by one game claiming and letting go.
    private const double OwnersAtLeastSeconds = 0.05, OwnersPerPageSeconds = 0.01;

    // The most craft remembered as gone, and the most bytes of craft definitions kept from one player and from all.
    private const int MostGone = 16_384;
    private const long PlayerDefinitionBytes = 256L * 1024 * 1024, AllDefinitionBytes = 1024L * 1024 * 1024;

    // Wrong passwords from one address before it is refused for the rest of the minute.
    private const int WrongPasswords = 5;
    private const double WrongPasswordSeconds = 60.0;

    // The login service is asked for its key again, for a certificate the one held did not sign, no
    // more often than this: anybody admitted can send a certificate nobody signed.
    private const double LoginKeyAgainSeconds = 60.0;

    /// <summary>
    /// How long a session must have been silent, s, before a game joining under its name is taken to
    /// be that session coming back rather than somebody else. A living game pings once a second.
    /// </summary>
    private const double QuietEnoughToReplaceSeconds = 3.0;

    // How long a session from the same machine may go without answering its welcome before a new
    // hello under its name is taken as the same game trying again. A round trip is a fraction of it.
    private const double NeverAnsweredSeconds = 1.0;

    /// <summary>How long one join cookie stands, s. Two are accepted, so it is good for twice this.</summary>
    private const double CookieSeconds = 15.0;

    /// <summary>Where an answer is written to learn how long it is, before it is sent.</summary>
    private readonly byte[] _measure = new byte[Wire.BufferBytes];
    private const double RosterSoonSeconds = 0.5;
    private const double RosterEverySeconds = 2.0, OwnersEverySeconds = 5.0, ClockEverySeconds = 0.2;

    // A count that climbs while nobody is launching anything is a world filling with craft that
    // should not be there, which is not visible in any other line here.
    private const double CraftCountEverySeconds = 30.0;

    // How long a player joining waits for a fresh save before it is sent the one there is.
    private const double FreshWithinSeconds = 15.0;

    // The most a player joining waits for a fresh save while one is still to come: on its way in, or
    // asked of a game less than FreshWithinSeconds ago. A join on ksamp.gg was sent a world 3351 s
    // old one second before the fresh one came, which the next game asked had been asked for 5 s
    // into the wait and taken 11 to send.
    private const double FreshAtMostSeconds = 60.0;

    // How far a save may be behind the ask and still be the fresh one a joiner waits for. The game
    // giving it is often a second or two behind the server's time, and a test of one second sent
    // every joiner the save it was asked for only after the full wait.
    private const double FreshBehindSeconds = 5.0;

    // How long one game is waited on for a save before the next is asked as well. A game with the
    // vehicle editor open holds the ask unanswered, and the world sent at the end of the wait was
    // minutes old: seven joins started 90 to 455 s behind in one evening.
    private const double AskNextAfterSeconds = 5.0;

    // How long after a protected craft changes hands its new holder's word that it is gone is not taken.
    private const double ProtectedTakenSeconds = 60.0;

    // How long a craft let go of for a watcher is kept for them, against anybody else's game.
    private const double FreedForSeconds = 5.0;

    // How long a world may have been on its way into a game before it is too old to play in, where
    // there is a newer. A slow machine's first load takes a minute or two; this is the editor.
    private const double StaleWorldAfterSeconds = 180.0;

    // How far behind the server's time a game may be and still be handed a craft it does not have.
    // One that is further is flying the past: as owner, its world is the one every other game is
    // pulled to, and a station given to a game 5,500 s behind came apart in it 45 s later.
    private const double TakeBehindSeconds = 30.0;

    // How far behind a game may be and still be given a craft by its pilot.
    private const double GiveBehindSeconds = 1.0;

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
    private readonly Dictionary<IPAddress, (int Count, double Since)> _wrong = [];
    private double _ownersSaidAt = double.NegativeInfinity, _loginKeyAgainAt = double.NegativeInfinity;
    private int _ownersPages = 1;
    private readonly Func<double> _clock;
    private readonly WorldStore? _store;
    private readonly Dictionary<byte, Player> _players = [];
    private readonly Dictionary<string, Craft> _crafts = [];
    private readonly Dictionary<string, double> _gone = [];

    // A craft that has gone, to be said again, and when: one datagram is one chance, and a game
    // that never hears it keeps the craft, and takes it for its own if it is watching.
    private readonly List<(string Key, byte From, double At)> _goneAgain = [];
    private static readonly double[] GoneAgainAfterSeconds = [0.3, 1.0];
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
    private double _warpAskedAt, _warpNext, _warpQuietSince = double.NegativeInfinity, _governNext, _followNext;

    // How far the clock's own speed has lately fallen and when: what a game being past the clock is
    // explained by.
    private double _fellBy, _fellAt = double.NegativeInfinity;

    // The fastest a running warp has been found not to work, and whose machine it was found on.
    // Nothing climbs back to it, or the world climbs into the same ceiling and falls out of it once a
    // second for as long as the warp lasts.
    private double _warpCeiling = double.PositiveInfinity;
    private byte _warpCeilingWho;
    private readonly Dictionary<byte, bool> _warpVotes = [];

    /// <param name="admins">Who may run commands; by default the list beside the world, or none kept at all without one.</param>
    /// <param name="saves">The worlds kept under names; by default a folder of them beside the world, or kept only while the server runs without one.</param>
    /// <param name="protect">The craft not to clear; by default the list beside the world, or kept only while the server runs without one.</param>
    /// <param name="discord">What certificates are checked against; by default the options' login service.</param>
    public GameServer(ServerOptions options, Func<double> clock, WorldStore? store = null, IPAddress? bind = null, Admins? admins = null, SaveShelf? saves = null,
                      ProtectedCraft? protect = null, ILoginService? discord = null)
    {
        (Options, _clock, _store, _discordGiven) = (options, clock, store, discord);
        Protected = protect ?? new ProtectedCraft(store?.Folder ?? "");
        _protectedSerial = Protected.Names.Count > 0 ? 1u : 0u;
        Memorial = new KittenMemorial(store?.Folder ?? "");
        _memorialSerial = Memorial.Deaths.Count > 0 ? 1u : 0u;
        Admins = admins ?? new Admins(store?.Folder ?? "");
        Audit = new AuditLog(store?.Folder ?? "");
        PlayerLogs = new PlayerLogs(store?.Folder ?? "");
        Saves = saves ?? new SaveShelf(store is null ? "" : Path.Combine(store.Folder, "saves"));
        UniverseClock.SetSpeed(options.Speed, clock());
        _udp = new Udp(options.Port, clock, "KSAMultiplayer server", bind);
        if (store?.Load() is { } saved)
        {
            _world = saved.World;
            (_worldBuild, _worldMods, _worldSystem) = (saved.KsaBuild, saved.Mods, saved.System);
            _worldTimeKnown = saved.TimeKnown;
            // Including one written before that was checked.
            if (saved.TimeKnown && ClockFollow.SaneUniverse(saved.World.Universe)) UniverseClock.Set(saved.World.Universe, clock());
            Log($"world loaded from {store.Folder}: {Blob.Size(saved.World.Data.Length)}"
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
        // Nor what it was built for, which is no setting and is in nothing a setting is read from.
        Options = next with { Port = was.Port, BuiltFor = was.BuiltFor };
        // A shorter wait is not made to sit out the longer one already counting down.
        if (next.SnapshotEverySeconds != was.SnapshotEverySeconds) _snapshotNext = Math.Min(_snapshotNext, now + next.SnapshotEverySeconds);
        // A warp holds the clock for as long as it runs and gives it back at this speed when it ends.
        if (next.Speed != was.Speed && _warpPhase == Wire.WarpPhase.None) UniverseClock.SetSpeed(next.Speed, now);
        // Said, because the games take it up within the second and a kitten that stops eating, or
        // starts, is otherwise the first anybody hears of it.
        if (next.LifeSupport != was.LifeSupport) Broadcast(new Wire.Chat(0, $"life support is now {(next.LifeSupport ? "on" : "off")} on this server"), _ => true);
        if (next.Radiation != was.Radiation) Broadcast(new Wire.Chat(0, $"radiation is now {(next.Radiation ? "on" : "off")} on this server"), _ => true);
        if (next.ClearPads != was.ClearPads) Broadcast(new Wire.Chat(0, next.ClearPads
            ? "craft left on a launchpad for five minutes are cleared away again on this server"
            : "craft left on a launchpad are no longer cleared away on this server"), _ => true);
        // A Discord admin added to the setting is one now, if they are here and logged in.
        WelcomeForDiscord();
        foreach (Player p in _players.Values.Where(p => p.DiscordId.Length > 0 && RoleIn(next, p.DiscordId) > p.Role).ToArray())
        {
            MakeRole(p, RoleIn(next, p.DiscordId));
        }
    }
    public UniverseClock UniverseClock { get; } = new();
    public int Port => _udp.Port;
    public Udp Socket => _udp;
    public IReadOnlyCollection<Player> Players => _players.Values;
    public bool HasWorld => _world is not null;
    public Blob? World => _world;
    public int CraftCount => _crafts.Count;
    public Admins Admins { get; }

    /// <summary>Who did what that changes the world, a save or a role: kept beside the world.</summary>
    public AuditLog Audit { get; }

    /// <summary>The logs players' games have handed over, where <see cref="ServerOptions.CollectLogs"/> is on.</summary>
    public PlayerLogs PlayerLogs { get; }

    /// <summary>The worlds kept under names, which an admin saves to and loads from.</summary>
    public SaveShelf Saves { get; }

    /// <summary>The craft <c>clear</c> leaves standing unless asked for by name.</summary>
    public ProtectedCraft Protected { get; }

    /// <summary>The kittens that have died in this world, and whose game each died in.</summary>
    public KittenMemorial Memorial { get; }

    /// <summary>KSA's rules as this server's settings have them, as the clock says them.</summary>
    public Wire.WorldRules Rules =>
        (Options.LifeSupport ? Wire.WorldRules.LifeSupport : 0) | (Options.Radiation ? Wire.WorldRules.Radiation : 0)
        | (Options.ClearPads ? Wire.WorldRules.ClearPads : 0);

    /// <summary>
    /// The KSA build a player must run: the operator's if they named one, otherwise the build this
    /// server was built for. Only a server built without one goes on to the build of the world
    /// being handed out, and then of whoever is in; empty when none of those is known, and then
    /// any build is let in.
    ///
    /// <para>What it was built for and not what its world was saved by, which is what this was.
    /// A mod and a server are released together for one build of KSA, so that build is the one a
    /// player who can speak to this server at all is running. Going by the world meant a server
    /// with no world yet wanted no build and said none on a list of servers, and a world saved
    /// before KSA updated turned away everybody who had updated, on a server that had been brought
    /// up to date for them.</para>
    ///
    /// <para>Whoever is in, because a world is not there the moment its first player is: it is
    /// asked of them and takes seconds to arrive, and an empty world the server started with was
    /// written by no player at all. Somebody joining meanwhile on another build would be sent a
    /// save their game did not write, or share a world with a game whose parts are not theirs.</para>
    /// </summary>
    public string RequiredBuild => Options.KsaBuild.Length > 0 ? Options.KsaBuild
        : Options.BuiltFor.Length > 0 ? Options.BuiltFor
        : _worldBuild.Length > 0 ? _worldBuild
        : _players.Values.Select(p => p.KsaBuild).FirstOrDefault(b => b.Length > 0) ?? "";

    /// <summary>What a player must have enabled: what the operator pinned, or the world's own list when they pinned nothing.</summary>
    /// <remarks>
    /// The operator's entries are kept as written, versions and all: <c>FastAndPurrious@0.1.13</c>
    /// is how a server is held to one build of a mod. The world's own list is ids alone, as it
    /// always was: whoever gave the world had some version of each mod, and nobody said the next
    /// player must have that one.
    /// </remarks>
    public string[] RequiredMods => Options.Mods.Length > 0
        ? [.. Options.Mods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
        // A world saved before a mod was made optional still names it, and would otherwise hold
        // every player to it until somebody without it saved the world, which nobody could join to do.
        : [.. _worldMods.SelectMany(ModList.Ids).Except(ModList.Ids(OptionalMods), StringComparer.OrdinalIgnoreCase)];

    // ------------------------------------------------------------------ Discord

    private readonly ILoginService? _discordGiven;
    private LoginService? _discordService;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(Player Who, string Proof, DiscordCert? Said, string Why)> _discordDone = new();

    // The key and where this server is seen from, fetched once and again after an hour or a
    // certificate that does not check against it: the service may have been given a new key.
    private Task<LoginKey>? _loginKey;
    private double _loginKeyAt = double.NegativeInfinity;
    private const double LoginKeyHoldSeconds = 3600.0;

    // A proof a second from one game would be a server resolving names as fast as one player can
    // make it; a few seconds apart is more than a player clicking ever needs.
    private const double DiscordAgainSeconds = 3.0, DiscordWithinSeconds = 15.0;

    /// <summary>Whether this server takes Discord logins: it has a login service, and some Discord user to recognise.</summary>
    public bool DiscordOn => Login is not null && (Admins.HasDiscord || Options.DiscordAdmins.Length > 0 || Options.DiscordTrusted.Length > 0 || Options.DiscordOperators.Length > 0);

    /// <summary>What this Discord user is here: the better of what the settings and admins.json say.</summary>
    private Wire.Role DiscordRole(string id, string name)
    {
        Wire.Role listed = Admins.CheckDiscord(id, name);
        Wire.Role set = RoleBySetting(id);
        return listed > set ? listed : set;
    }

    /// <summary>What the discord-operators, discord-admins and discord-trusted settings make this Discord user, which deop cannot change.</summary>
    public Wire.Role RoleBySetting(string id) => RoleIn(Options, id);

    /// <summary>The setting that makes this Discord user what they are here, or null for none.</summary>
    public string? SettingFor(string id) => RoleBySetting(id) switch
    {
        Wire.Role.Operator => "discord-operators",
        Wire.Role.Admin => "discord-admins",
        Wire.Role.Trusted => "discord-trusted",
        _ => null,
    };

    /// <summary>Discord users named in more than one of the role settings, with the one they are taken from.</summary>
    public IEnumerable<(string Id, string Setting)> ListedTwice() =>
        Options.DiscordOperators.Concat(Options.DiscordAdmins).Concat(Options.DiscordTrusted)
            .GroupBy(id => id).Where(g => g.Count() > 1).Select(g => (g.Key, SettingFor(g.Key)!));

    private static Wire.Role RoleIn(ServerOptions o, string id) =>
        o.DiscordOperators.Contains(id) ? Wire.Role.Operator
        : o.DiscordAdmins.Contains(id) ? Wire.Role.Admin
        : o.DiscordTrusted.Contains(id) ? Wire.Role.Trusted : Wire.Role.Player;

    // Whoever is here was welcomed with nothing to log in with when logins were off: they are welcomed
    // again, which a joined game reads for the nonce alone.
    private void WelcomeForDiscord()
    {
        if (!DiscordOn) return;
        foreach (Player p in _players.Values.Where(p => p.DiscordNonce.Length == 0))
        {
            p.DiscordNonce = Discord.Nonce();
            _udp.Send(new Wire.Welcome(p.Id, "", Options.Name, true, System: _worldSystem, Discord: p.DiscordNonce), p.At);
        }
    }

    private ILoginService? Login
    {
        get
        {
            if (_discordGiven is not null) return _discordGiven;
            if (Options.DiscordLogin.Length == 0) return null;
            if (_discordService?.Url != Options.DiscordLogin) (_discordService, _loginKey) = (new LoginService(Options.DiscordLogin), null);
            return _discordService;
        }
    }

    private Task<LoginKey> LoginKey(bool again = false)
    {
        double now = _clock();
        if (_loginKey is null || again || _loginKey.IsFaulted || now - _loginKeyAt > LoginKeyHoldSeconds)
        {
            (_loginKey, _loginKeyAt) = (Login!.Key(CancellationToken.None), now);
        }
        return _loginKey;
    }

    private void OnDiscordProof(Player p, Wire.DiscordProof asked, double now)
    {
        if (asked.Cert.Length == 0) return;
        string proof = $"{asked.Cert}|{asked.Aud}|{asked.Signature}";
        // Said again because the answer was lost, or is still being worked out.
        if (proof == p.DiscordProof)
        {
            if (p.DiscordAnswer is { } answer) _udp.Send(answer, p.At);
            return;
        }
        string? refused = !DiscordOn || p.DiscordNonce.Length == 0 ? "this server doesn't take Discord logins"
            : p.DiscordProof.Length > 0 && p.DiscordAnswer is null ? "a Discord login is already being checked: wait for it"
            : now - p.DiscordAt < DiscordAgainSeconds ? "wait a few seconds before logging in with Discord again"
            : null;
        if (refused is not null)
        {
            _udp.Send(new Wire.DiscordUser("", "", refused), p.At);
            return;
        }
        (p.DiscordProof, p.DiscordAnswer, p.DiscordAt) = (proof, null, now);
        bool again = now - _loginKeyAgainAt > LoginKeyAgainSeconds;
        Task<LoginKey> key = LoginKey(), fresh = key;
        ILoginService login = Login!;
        string nonce = p.DiscordNonce;
        // The name the operator says this server is reached by, which is this server's whatever
        // the machine can see of itself: in a container behind a router, with the login service on
        // the same machine, neither its own interfaces nor where the service saw it from is the
        // address players reach it at. The operator's word for their own server, which no other
        // server can say for it.
        string named = Options.AdvertiseAs;
        Task.Run(async () =>
        {
            using CancellationTokenSource within = new(TimeSpan.FromSeconds(DiscordWithinSeconds));
            try
            {
                DiscordCert? said = DiscordCert.Verify(asked.Cert, (await key.WaitAsync(within.Token)).Key);
                // Not signed by the key held: the service may have a new one.
                if (said is null && again)
                {
                    _loginKeyAgainAt = now;
                    fresh = Fetched(login, within.Token);
                    said = DiscordCert.Verify(asked.Cert, (await fresh).Key);
                }
                string? why = said is null ? "that Discord login isn't signed by the login service"
                    : DateTimeOffset.UtcNow.ToUnixTimeSeconds() > said.Exp ? "your Discord login has run out: log in again"
                    : !said.Signed(nonce, asked.Aud, asked.Signature) ? "that Discord login wasn't proved for this join"
                    : null;
                if (why is null && !Names(named, asked.Aud))
                {
                    HashSet<IPAddress> mine = Addresses.Mine((await fresh).Seen);
                    if (named.Length > 0) mine.UnionWith((await login.Resolve(named, within.Token)).Select(Addresses.Plain));
                    IPAddress[] led = await login.Resolve(asked.Aud, within.Token);
                    if (!led.Any(ip => mine.Contains(Addresses.Plain(ip)))) why = $"that Discord login was proved for {asked.Aud}, which isn't this server";
                }
                _discordDone.Enqueue((p, proof, why is null ? said : null, why ?? ""));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or TimeoutException or CryptographicException)
            {
                _discordDone.Enqueue((p, proof, null, $"couldn't reach the Discord login service: {e.Message}"));
            }
        });
    }

    // Whether an address a player typed is the name this server is listed under: the host, whatever
    // port was typed after it.
    private static bool Names(string named, string aud) =>
        named.Length > 0 && string.Equals(Addresses.Split(aud).Host, named.Trim(), StringComparison.OrdinalIgnoreCase);

    // From a thread of the pool; the next proof on the server's own thread starts from it.
    private Task<LoginKey> Fetched(ILoginService login, CancellationToken cancel)
    {
        Task<LoginKey> fetched = login.Key(cancel);
        (_loginKey, _loginKeyAt) = (fetched, _clock());
        return fetched;
    }

    // On the server's own thread, as everything else about a player is.
    private void DiscordAnswers()
    {
        while (_discordDone.TryDequeue(out (Player Who, string Proof, DiscordCert? Said, string Why) done))
        {
            Player p = done.Who;
            if (!_players.TryGetValue(p.Id, out Player? still) || still != p || p.DiscordProof != done.Proof) continue;
            if (done.Said is not { } said)
            {
                p.DiscordAnswer = new Wire.DiscordUser("", "", Wire.Clip(done.Why, 200));
                _udp.Send(p.DiscordAnswer, p.At);
                Log($"{p.Name} could not log in with Discord: {done.Why}");
                continue;
            }
            (p.DiscordId, p.DiscordName) = (said.Id, Wire.Clip(said.Name, Wire.NameChars * 2));
            // Everybody's list shows the dot within the frame, not at the next roster.
            _rosterNext = 0.0;
            p.DiscordAnswer = new Wire.DiscordUser(said.Id, p.DiscordName, "");
            _udp.Send(p.DiscordAnswer, p.At);
            Wire.Role role = DiscordRole(said.Id, p.Name);
            Log($"{p.Name} logged in with Discord as {p.DiscordName} ({said.Id}){(role > Wire.Role.Player ? $", {Permissions.Said(role)}" : "")}");
            if (role > p.Role)
            {
                MakeRole(p, role);
                Audit.Write(AuditLog.Who(p), $"logged in with Discord as {p.DiscordName}", $"{Permissions.Said(role)} by {(RoleBySetting(said.Id) == role ? "the setting" : "admins.json")}");
            }
        }
    }

    /// <summary>
    /// Makes a Discord user an admin whether or not they are playing, returning the name they are
    /// known by. Any player logged in as them now is one at once.
    /// </summary>
    public string OpDiscord(string id) => GrantDiscord(id, Wire.Role.Admin);

    /// <summary>As <see cref="OpDiscord"/>, to make a Discord user trusted.</summary>
    public string TrustDiscord(string id) => GrantDiscord(id, Wire.Role.Trusted);

    private string GrantDiscord(string id, Wire.Role role)
    {
        Player[] here = [.. _players.Values.Where(p => p.DiscordId == id)];
        string name = here.FirstOrDefault()?.Name ?? Admins.NameOfDiscord(id) ?? id;
        Admins.AddDiscord(id, name, role);
        WelcomeForDiscord();
        foreach (Player p in here.Where(p => p.Role != role && p.Role != Wire.Role.Operator)) MakeRole(p, role);
        if (here.Length == 0) Log($"Discord user {id} is {Permissions.Said(role)} from now, once they log in");
        return name;
    }

    /// <summary>Takes admin away from a Discord user, returning the name they were known by, or null for one who was no admin.</summary>
    public string? DeopDiscord(string id)
    {
        string? name = Admins.RemoveDiscord(id);
        HashSet<string> told = new(StringComparer.OrdinalIgnoreCase);
        foreach (Player p in _players.Values.Where(p => p.DiscordId == id).ToArray()) if (told.Add(p.Name)) Deop(p.Name);
        if (name is not null && told.Add(name)) Deop(name);
        if (name is not null) Log($"Discord user {id} ({name}) is no longer an admin");
        return name;
    }

    /// <summary>Console lines typed in a game by an admin, for the console program to run as its own.</summary>
    public Queue<(Player Who, string Line)> Asked { get; } = new();

    /// <summary>Whose warp it is, how fast, and whether it is asked for or running.</summary>
    public (byte Who, double Speed, Wire.WarpPhase Phase) Warp => (_warpWho, _warpSpeed, _warpPhase);

    /// <summary>The speed the running warp is on its way to: what its asker asked for, which it is held below by what the room can fly.</summary>
    public double WarpWanted => _warpWanted;

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
        (_world, _worldKept) = (world, false);
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
        DiscordAnswers();

        foreach (Player gone in _players.Values.Where(p => now - p.HeardAt > (p.Loaded ? SilentSeconds : LoadingSilentSeconds)).ToArray())
        {
            Remove(gone, "has gone quiet");
        }

        bool anyone = _players.Values.Any(p => p.Loaded);
        UniverseClock.Run(Options.KeepTime || anyone, now);
        if (!anyone) HoldTimeAtTheWorld(now);

        AskAgainForTheWorld(now);
        AskSomebodyElse(now);
        KeepLate(now);
        GiveTheCaughtUp(now);

        // Players waiting on a fresh save are sent the one there is once it comes, or after a while without.
        if (_world is not null)
        {
            // Waited for only while a game in the world has been asked for one and not answered: on
            // ksamp.gg, with nobody in it, two joins each waited out the 15 s for a save asked of a
            // player who had since left.
            bool asked = _players.Values.Any(q => q.WorldAsked && q.Loaded && !q.Seeding);
            bool coming = _players.Values.Any(q => q.WorldAsked && q.Link.Arriving is not null) || now - _worldAskSent <= FreshWithinSeconds;
            foreach (Player p in _players.Values.Where(p => p.WaitingForWorld && (!asked || Fresh() || now - p.WaitingSince > (coming ? FreshAtMostSeconds : FreshWithinSeconds))).ToArray())
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
            EndWarp(now, $"{Named(_warpWho)}'s warp was cancelled: not everyone answered");
        }
        // Either phase, not only a running one: an ask whose asker is gone is nobody's to answer.
        if (_warpPhase != Wire.WarpPhase.None && !_players.ContainsKey(_warpWho))
        {
            EndWarp(now, "the warp ended because the player who started it left");
        }
        Govern(now);
        FollowTheRoom(now);
        Lapse(now);
        SayGoneAgain(now);
        SayRenameAgain(now);
        if (double.IsFinite(_closingAt) && now >= _closingNext)
        {
            foreach (Player p in _players.Values) _udp.Send(new Wire.Closing((float)Math.Max(0.0, _closingAt - now), _closingWhy, p.Session), p.At);
            _closingNext = now + 1.0;
        }
        // Said again for a while after one has ended, as it is while one runs. The end of a warp
        // was said once, and a game that missed it went on showing a warp at 8x for as long as
        // nobody started another, with its keys stepping from a speed the world was not at.
        if ((_warpPhase != Wire.WarpPhase.None || now - _warpQuietSince < WarpQuietSeconds) && now >= _warpNext)
        {
            Broadcast(WarpState(), p => p.Loaded);
            _warpNext = now + WarpEverySeconds;
        }

        if (now >= _rosterNext)
        {
            Broadcast(Roster(), _ => true);
            _rosterNext = now + RosterEverySeconds;
        }
        if (_memorialDue && now >= _memorialSaidAt + MemorialEverySeconds)
        {
            _memorialDue = false;
            _memorialSaidAt = now;
            _memorialSerial++;
            byte[] list = MemorialBlob();
            foreach (Player p in _players.Values) p.Link.Send(list, now);
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
            int pages = 0;
            foreach (Wire.Owners page in OwnerPages())
            {
                Broadcast(page, p => p.Loaded);
                pages++;
            }
            (_ownersNext, _ownersSaidAt, _ownersPages) = (now + OwnersEverySeconds, now, pages);
        }
        if (now >= _clockNext && UniverseClock.Known)
        {
            Broadcast(new Wire.Clock(UniverseClock.Now(now), now, (float)UniverseClock.Rate, Rules), _ => true);
            _clockNext = now + ClockEverySeconds;
        }
        foreach (Player p in _players.Values)
        {
            if (p.Telling is { } telling && now >= p.TellNext)
            {
                _udp.Send(telling, p.At);
                p.TellNext = now + TellAgainSeconds;
            }
            // Room for a world is kept only for a player asked for one.
            p.Link.MostPieces = p.WorldAsked ? ushort.MaxValue : Link.VehiclePieces;
            p.Link.Pump(now, m => _udp.Send(m, p.At));
        }
    }

    private void Handle(byte[] data, IPEndPoint from, double arrived)
    {
        Player? player = _players.Values.FirstOrDefault(p => p.At.Equals(from));
        if (Wire.Read(data, out ushort version) is not { } message)
        {
            if (player is null && version != 0 && version != Wire.Version)
            {
                // A game of another version reads the version off this and nothing else, and says
                // the rest itself. As few bytes as a datagram is: the address it goes to is only
                // claimed, and a sentence in answer to seven bytes is a way to aim this server.
                _udp.Send(new Wire.Alive(), from);
            }
            return;
        }
        if (message is Wire.Ask)
        {
            Tell(data.Length, from);
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
                _udp.Send(new Wire.Welcome(player.Id, "", Options.Name, _world is not null || !player.Seeding, System: _worldSystem, Discord: player.DiscordNonce, Session: player.Session), from);
                return;
            case Wire.Ping ping:
                player.Rtt = ping.Rtt;
                player.Pings.Heard(ping.Sent, arrived);
                double late = float.IsFinite(ping.Late) ? ping.Late : 0.0, heard = _clock(), universe = UniverseClock.Now(heard);
                // A word that set out before its game could have heard the clock had gone on is
                // about the clock as it was: the game is that much less far past it now. Taken as
                // said, the same lead is followed twice -- at 150 ms each way a clock that went on
                // 177 s to one game went on 182 s more a half second later, for a game that had
                // five left, and both then had three minutes to catch up. A round trip is how long
                // that takes: the word of the clock out, and the ping that left just before it in.
                double round = ping.Rtt > 0f && ping.Rtt < MostRoundTripSeconds ? ping.Rtt : 0.0;
                foreach ((double at, double by) in player.Unheard)
                {
                    if (heard < at + round + HeardWithinSeconds) late += by;
                }
                player.Unheard.RemoveAll(u => heard >= u.At + MostRoundTripSeconds + HeardWithinSeconds);
                double apart = heard - player.LateAt, ran = universe - player.LateUniverse;
                // Between two words close enough together to be about the same stretch of flying:
                // a first word, or one after a silence, says where a game is and nothing of which
                // way it is going. Nor does any word while the clock stands still.
                player.Slip = player.LateAt > 0.0 && apart is > SlipOverAtLeast and < SlipOverAtMost && ran > 1e-6 ? (late - player.Late) / ran : 0.0;
                (player.Late, player.LateAt, player.LateUniverse) = (late, heard, universe);
                player.Can = float.IsFinite(ping.Can) && ping.Can > 0f ? ping.Can : 0.0;
                player.Here = ping.Here;
                if (ping.Watching != player.Watching)
                {
                    player.WatchingSince = _clock();
                    // Soon rather than on the roster's own round: who is spectating whom is shown
                    // in every game, and a button for giving a craft hangs on it.
                    _rosterNext = Math.Min(_rosterNext, _clock() + RosterSoonSeconds);
                }
                player.Watching = ping.Watching;
                if (ping.Editing != player.InEditor) _rosterNext = Math.Min(_rosterNext, _clock() + RosterSoonSeconds);
                player.InEditor = ping.Editing;
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
                if (OwnerOf(state.Key) != player.Id)
                {
                    SayDockedAgain(player, state.Key);
                    return;
                }
                // A claim says which craft is being flown; a state says which is not. Only the one
                // they are said to be in may give it up, so a parked craft cannot unseat another.
                bool atTheControls = (state.Flags & Wire.StateFlags.Controlled) != 0;
                if (_crafts[state.Key] is { Near: true } near)
                {
                    // Held for being near, and kept by being said to be: the first word without it
                    // gives the craft up, so whoever has it near next may hold it.
                    if (atTheControls)
                    {
                        // Flown, it is its flier's like any other, whether or not the claim that
                        // says so arrived: left held for being near, it could be taken from
                        // under its pilot by anybody who asked.
                        (near.Near, near.AttendedAt) = (false, _clock());
                        OwnersChanged();
                    }
                    else if ((state.Flags & Wire.StateFlags.Near) != 0) near.NearAt = _clock();
                    else
                    {
                        (near.Owner, near.Near) = (0, false);
                        OwnersChanged();
                    }
                }
                if (!Allowed(ref _crafts[state.Key].States, ref _crafts[state.Key].StatesAt, StatesASecond, StatesASecond, arrived))
                {
                    Limit(player, $"more than {StatesASecond:F0} states a second for one craft");
                    return;
                }
                if (atTheControls) player.Editing = (state.Flags & Wire.StateFlags.Editing) != 0;
                if (atTheControls && player.Controlling.Length == 0) Flying(player, state.Key);
                else if (!atTheControls && player.Controlling == state.Key) Flying(player, "");
                _crafts[state.Key].Said = state;
                Wire.State relayed = state with { Owner = player.Id };
                Broadcast(relayed, p => p.Loaded && p != player);
                return;
            case Wire.Claim claim when player.Loaded && claim.Key.Length > 0:
                bool had = OwnerOf(claim.Key) == player.Id;
                // Only once it is theirs: a player refused a craft is not flying it, whatever they asked.
                if (Grant(player, claim.Key, taking: claim.Controlling, made: claim.Made, near: claim.Near, undocked: claim.Made && claim.Undocked) is not { } granted)
                {
                    SayDockedAgain(player, claim.Key);
                    return;
                }
                if (claim.Controlling) Flying(player, granted);
                if (!had) OwnersChanged();
                return;
            // A protected craft its game took over moments ago is not gone for everybody on that
            // game's word: what this world's physics had for it while it was somebody else's goes
            // off as it changes hands, and a station was wrecked in one game nine seconds after its
            // owner left, said gone everywhere, and saved out of the world. Left where it is in
            // every other world, and nobody's.
            case Wire.Gone gone when player.Loaded && OwnerOf(gone.Key) == player.Id
                                     && Protected.Has(gone.Key) && _clock() - _crafts[gone.Key].TakenAt < ProtectedTakenSeconds:
                _crafts[gone.Key].Owner = 0;
                OwnersChanged();
                Log($"{player.Name}'s game says {gone.Key} is gone, {_clock() - _crafts[gone.Key].TakenAt:0} s after taking it: it is protected, so it stays in every other world");
                return;
            case Wire.Gone gone when player.Loaded && OwnerOf(gone.Key) == player.Id:
                _crafts.Remove(gone.Key);
                player.Crafts--;
                if (_gone.Count >= MostGone)
                {
                    foreach (string old in _gone.OrderBy(g => g.Value).Take(MostGone / 2).Select(g => g.Key).ToArray()) _gone.Remove(old);
                }
                _gone[gone.Key] = UniverseClock.Now(_clock());
                if (player.Controlling == gone.Key) Flying(player, "");
                Broadcast(gone, p => p.Loaded && p != player);
                foreach (double after in GoneAgainAfterSeconds) _goneAgain.Add((gone.Key, player.Id, _clock() + after));
                // Destroyed, since nothing else of a protected craft's ends here: its owner's game
                // refuses to delete or recover it, and one docked into another is a Dock, by which
                // time it is nobody's and its name stays on purpose. Left on, a name nothing has
                // stood under since would protect whatever KSA next called it.
                if (Protected.Remove(gone.Key))
                {
                    Log($"{gone.Key} was destroyed, so it is no longer protected");
                    SayProtected();
                    Broadcast(new Wire.Chat(0, $"{gone.Key} was destroyed, so it's no longer protected"), p => p.Loaded);
                }
                return;
            // A mod's word about a craft, passed on as the craft's state is and on the same
            // footing: from the player who has it, and to everybody else. Not read here.
            case Wire.Extra extra when player.Loaded && extra.Channel.Length > 0 && extra.Data.Length <= Wire.ExtraBytes:
                if (OwnerOf(extra.Key) != player.Id) return;
                if (!Allowed(ref _crafts[extra.Key].Extras, ref _crafts[extra.Key].ExtrasAt, ExtrasASecond, ExtrasASecond, arrived))
                {
                    Limit(player, $"more than {ExtrasASecond:F0} of mods' words a second about one craft");
                    return;
                }
                Wire.Extra passed = extra with { Owner = player.Id };
                Broadcast(passed, p => p.Loaded && p != player);
                return;
            case Wire.Dock dock when player.Loaded && dock.Consumed.Length > 0 && dock.Survivor.Length > 0 && dock.Consumed != dock.Survivor:
                OnDock(player, dock.Consumed, dock.Survivor);
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
                if (!MaySay(player, chat: true, arrived)) return;
                Log($"<{player.Name}> {text}");
                Broadcast(new Wire.Chat(player.Id, text), _ => true);
                return;
            // Written down as said, by a game that is in the world, and only so many: it is a line
            // in the operator's log on a player's word, and a game that said one every frame
            // would be the whole of it.
            case Wire.Note note when player.Loaded:
                string seen = Wire.Clip(note.Text, Wire.NoteChars);
                player.Notes = Math.Min(NotesAtOnce, player.Notes + ((arrived - player.NotesAt) * NotesASecond));
                player.NotesAt = arrived;
                if (seen.Length == 0 || player.Notes < 1.0) return;
                player.Notes -= 1.0;
                Log($"{player.Name}'s game notes: {seen}");
                return;
            // Whose game it was is the server's word for the sender, never anything the game says.
            // The list that comes back with it is what tells the game to stop saying it.
            case Wire.Died died when player.Loaded:
                string kitten = Wire.Clip(died.Kitten, Wire.KeyChars).Trim();
                if (kitten.Length == 0) return;
                player.Deaths = Math.Min(DeathsAtOnce, player.Deaths + ((arrived - player.DeathsAt) * DeathsASecond));
                player.DeathsAt = arrived;
                if (player.Deaths < 1.0) return;
                MemorialList.Death death = new(kitten, player.Name, Wire.Clip(died.Cause, MemorialList.CauseChars).Trim(),
                                               Wire.Clip(died.Craft, Wire.KeyChars).Trim(), UniverseClock.Now(_clock()));
                // Said again only because this game has not seen the list yet, which is on its way
                // already: the list goes as a transfer, which asks for what it lost.
                if (!Memorial.Add(death)) return;
                player.Deaths -= 1.0;
                Log($"{player.Name}'s game: {kitten} died{(death.Craft.Length > 0 && death.Craft != kitten ? $" aboard {death.Craft}" : "")}{(death.Cause.Length > 0 ? $" ({death.Cause})" : "")}");
                SayMemorial();
                return;
            case Wire.Name name when player.Loaded:
                if (RenameCraft(name.Was, name.Now, player) is { } refused) Tell(player, refused);
                return;
            case Wire.Give give when player.Loaded && give.Session == player.Session:
                if (GiveCraft(player, give.Key, give.Player) is { } notGiven) Tell(player, notGiven);
                return;
            case Wire.DiscordProof discordProof:
                OnDiscordProof(player, discordProof, arrived);
                return;
            // With the session's word, from a game that was given one: a command is one datagram, and
            // one with an admin's address written on it was otherwise the admin's.
            case Wire.Command asked when asked.Session == player.Session:
                string line = Wire.Clip(asked.Line, Wire.ChatChars).Trim();
                if (line.Length == 0) return;
                if (!MaySay(player, chat: false, arrived)) return;
                if (Permissions.OnlyAtTheConsole(line))
                {
                    Tell(player, $"no command '{Permissions.Verb(line)}'; try help");
                    return;
                }
                Wire.Role needs = Permissions.Need(line);
                if (player.Role < needs)
                {
                    Tell(player, needs switch
                    {
                        Wire.Role.Operator => "only an operator can do that on this server",
                        Wire.Role.Admin => "only an admin can do that on this server",
                        _ => "only trusted players and admins can do that on this server",
                    });
                    Log($"{player.Name} asked for '{line}' and is {Permissions.Said(player.Role)}");
                    return;
                }
                Log($"{player.Name}, {Permissions.Said(player.Role)}, asks for '{line}'");
                Asked.Enqueue((player, line));
                return;
            // Only what this game was told, said back: it has heard, and a token it was handed is
            // written down. Anything else in an Op from a game is nothing the server ever said.
            case Wire.Op back when back.SaysBack(player.Telling):
                player.Telling = null;
                // The token arrived, which is when the one before it stops working.
                if (back.Admin && back.Token.Length > 0) Admins.Check(back.Token, player.Name);
                return;
            case Wire.Bye bye when bye.Session == player.Session:
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

    /// <summary>
    /// Answers a list of servers asking what this is: what the cookie's welcome says, to anybody,
    /// in whatever version they speak. Nothing is held for the address and nothing is logged.
    /// </summary>
    /// <param name="asked">How long the ask was, bytes, which is the most it is answered with.</param>
    private void Tell(int asked, IPEndPoint from)
    {
        // Nothing here says where a datagram really came from, so an answer longer than its
        // question is a way to aim this server at somebody else. A real ask is padded to this.
        if (asked < Wire.AskBytes) return;
        Wire.Facts facts = new(Options.Name, _worldSystem, _world is not null, WorldMods,
                               (byte)Math.Min(_players.Count, 255), (byte)Math.Min(Options.MaxPlayers, 255),
                               Options.Password.Length > 0 ? Wire.WelcomeFlags.Password : Wire.WelcomeFlags.None, RequiredBuild,
                               LinkId: Options.Id, Optional: OptionalMods);
        // Only a name, a build and three hundred characters of mods, none of them in ASCII, come
        // to more than an ask. Said without the mods then, as a list too long is said clipped:
        // the handshake is what compares them.
        if (Wire.Write(facts, _measure) > asked) facts = facts with { Optional = "" };
        if (Wire.Write(facts, _measure) > asked) facts = facts with { Mods = "" };
        _udp.Send(facts, from);
    }

    private void Admit(Wire.Hello hello, IPEndPoint from, double now)
    {
        // Before anything is held for this address, and before the password is even looked at: an
        // address that cannot hear the answer cannot join, whatever else it got right.
        if (!CookieHolds(hello, from, now))
        {
            // The world's system and its mods go with the cookie: they are what a game asks before
            // it has a universe, and nothing a stranger could not learn by being refused for the
            // wrong one. The mods matter most here of anywhere, because KSA reads its own once at
            // startup: a player told after a world has loaded has to restart to act on it.
            //
            // So do the counts and the build, for the same reason one step further out: this is the
            // answer a list of servers is built from, and a row that cannot say "3/16" or "needs
            // KSA 2026.11.10.5801" sends a player to learn both by being refused. Only this welcome
            // carries them. The two that admit and refuse go to somebody who is past needing them.
            _udp.Send(new Wire.Welcome(0, "", Options.Name, _world is not null, Cookie(from, (long)(now / CookieSeconds)), _worldSystem, WorldMods,
                                       (byte)Math.Min(_players.Count, 255), (byte)Math.Min(Options.MaxPlayers, 255),
                                       Options.Password.Length > 0 ? Wire.WelcomeFlags.Password : Wire.WelcomeFlags.None, RequiredBuild, Options.Id,
                                       Optional: OptionalMods), from);
            return;
        }
        // What a hello says of its game is written in the log and said to other players.
        hello = hello with { Name = Wire.PlayerName(hello.Name), KsaBuild = Wire.Clip(hello.KsaBuild, 64), System = Wire.Clip(hello.System, Wire.NameChars) };
        // A join into a world about to stop is a load of minutes thrown away seconds later.
        string? refused = double.IsFinite(_closingAt) ? $"the server is stopping ({_closingWhy}): try again in a minute"
            : _players.Count >= Options.MaxPlayers ? $"the server is full: {Options.MaxPlayers} players"
            : PasswordRefusal(hello.Password, from.Address, now) ?? Refusal(hello);
        if (refused is not null)
        {
            _udp.Send(new Wire.Welcome(0, refused, Options.Name, false, Mods: WorldMods, Optional: OptionalMods), from);
            Log($"refused {hello.Name} from {from}: {refused}");
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
        //
        // Or sooner, a session from the same machine that has said nothing at all since it was let
        // in: a game answers its welcome within a frame or two, and one that never has gave up on
        // the join while its hello was on the way. Waited for, it stood beside its own game for the
        // two minutes a load is allowed, and on ksamp.gg a player asked why he was there twice, as
        // "Spacecat Sawyer" and "Spacecat Sawyer 2", after pressing connect twice in a tenth of a second.
        string asked = hello.Name;
        foreach (Player ghost in _players.Values
                     .Where(q => (now - q.HeardAt > QuietEnoughToReplaceSeconds
                                  || (q.HeardAt <= q.JoinedAt && now - q.JoinedAt > NeverAnsweredSeconds && q.At.Address.Equals(from.Address)))
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
            // What the world is said to want when this player saves it: a mod others may go without
            // is not one the next player must have.
            Mods = ModList.Without(hello.Mods, OptionalMods).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            System = hello.System,
            Role = Admins.Check(Wire.Clip(hello.Token, Wire.TokenChars), name),
            Session = hello.Sessions ? Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(12)) : "",
        };
        p.Link.MostPieces = Link.VehiclePieces;
        _players[id] = p;

        bool seeding = _world is null && _players.Values.All(q => !q.Seeding);
        if (DiscordOn) p.DiscordNonce = Discord.Nonce();
        _udp.Send(new Wire.Welcome(id, "", Options.Name, !seeding, System: _worldSystem, Discord: p.DiscordNonce, Session: p.Session), from);
        Log($"{name} has joined from {from}{(p.Role > Wire.Role.Player ? $" as {Permissions.Said(p.Role)}" : "")}{(seeding ? ", and their world becomes the server's" : "")}");
        // Said only when there is something to say: that they are an admin, or that the token they
        // offered is no good, which is how a game deopped while it was away forgets the one it kept.
        // Nothing at all to a player who offered none, who is no admin and knows it.
        if (p.Role > Wire.Role.Player || hello.Token.Length > 0) TellAdmin(p, new Wire.Op(p.Role > Wire.Role.Player, "", p.Role));
        if (p.Role > Wire.Role.Player)
        {
            Tell(p, $"you're {Permissions.Said(p.Role)} on this server: type /help in chat to see the commands");
            SendSaves(p);
        }
        // To everybody, before there is anything to sweep: a game sweeps its own world.
        if (_protectedSerial > 0) p.Link.Send(ProtectedBlob(), now);
        if (_memorialSerial > 0) p.Link.Send(MemorialBlob(), now);
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

    // Compared as hashes, so how long the comparing takes says nothing of how much was right.
    private bool PasswordGood(string given) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(Options.Password)));

    // Why an address may not join on the password it gave, or null: a wrong one counts against the
    // address it came from, which the cookie has shown to be its own, and enough of them close the
    // door on it for the rest of the minute, to the right one too.
    private string? PasswordRefusal(string given, IPAddress from, double now)
    {
        if (Options.Password.Length == 0) return null;
        if (_wrong.TryGetValue(from, out (int Count, double Since) wrong) && now - wrong.Since > WrongPasswordSeconds)
        {
            _wrong.Remove(from);
            wrong = default;
        }
        if (wrong.Count >= WrongPasswords) return "too many wrong passwords from your address: wait a minute";
        if (PasswordGood(given)) return null;
        if (_wrong.Count >= 4096)
        {
            foreach (IPAddress old in _wrong.Where(w => now - w.Value.Since > WrongPasswordSeconds).Select(w => w.Key).ToArray()) _wrong.Remove(old);
            if (_wrong.Count >= 4096) _wrong.Clear();
        }
        _wrong[from] = (wrong.Count + 1, wrong.Count == 0 ? now : wrong.Since);
        return "wrong password";
    }

    // Takes one from an allowance that fills at so many a second up to so many, or says there is none.
    private static bool Allowed(ref double allowance, ref double at, double aSecond, double most, double now)
    {
        allowance = Math.Min(most, allowance + ((now - at) * aSecond));
        at = now;
        if (allowance < 1.0) return false;
        allowance -= 1.0;
        return true;
    }

    // Whether a player may say another chat line or command now.
    private bool MaySay(Player p, bool chat, double now)
    {
        bool may = chat ? Allowed(ref p.Chats, ref p.ChatsAt, ChatsASecond, ChatsAtOnce, now) : Allowed(ref p.Commands, ref p.CommandsAt, CommandsASecond, CommandsAtOnce, now);
        if (!may) Limit(p, chat ? $"more than {ChatsASecond:F0} chat lines a second" : $"more than {CommandsASecond:F0} commands a second");
        return may;
    }

    // Writes a limit a player went over in the log, once a minute for each: what is over it is dropped.
    private void Limit(Player p, string what)
    {
        double now = _clock();
        if (now - p.LimitSaid.GetValueOrDefault(what, double.NegativeInfinity) < 60.0) return;
        p.LimitSaid[what] = now;
        Log($"{p.Name} is over a limit, and what is over it is dropped: {what}");
    }

    // How many craft a player holds: counted afresh once a second, and by what is given in between.
    private int Held(Player p)
    {
        double now = _clock();
        if (now - p.CraftsAt > 1.0) (p.Crafts, p.CraftsAt) = (_crafts.Values.Count(c => c.Owner == p.Id), now);
        return p.Crafts;
    }

    // Whether a player may be given one more craft.
    private bool RoomFor(Player p)
    {
        if (Held(p) < Options.MaxCraftPerPlayer) return true;
        Limit(p, $"more than {Options.MaxCraftPerPlayer} craft");
        return false;
    }

    // Why a player cannot be let in: another build of KSA, or a mod the world may hold parts of that it does not have.
    /// <summary>What this world wants of a game, as a hello says it: empty when it asks nothing.</summary>
    private string WorldMods => string.Join(",", RequiredMods);

    /// <summary>The mods a player may go without, less any the operator requires as well.</summary>
    private string OptionalMods => string.Join(",", ModList.Ids(Options.OptionalMods).Except(ModList.Ids(Options.Mods), StringComparer.OrdinalIgnoreCase));

    private string? Refusal(Wire.Hello hello)
    {
        string build = RequiredBuild;
        if (build.Length > 0 && hello.KsaBuild.Length > 0 && hello.KsaBuild != build)
        {
            return $"this server runs KSA {build} and you have {hello.KsaBuild}: you need the same build to join";
        }
        string[] wanted = RequiredMods;
        (string[] missing, string[] extra, string[] wrong) = ModList.Against(string.Join(',', wanted), hello.Mods, OptionalMods);
        if (missing.Length > 0) return $"the server's world needs mods you don't have enabled: {string.Join(", ", missing)}";
        // And none the others lack. A mod that adds parts makes craft nobody else can put together,
        // and the first they hear of it is a craft arriving that their game refuses to build, which
        // reads as the mod being broken rather than as somebody running something extra. Nothing is
        // asked of the player whose world this is about to become: they are the ones who set it.
        if (extra.Length > 0) return $"you have mods enabled that this world doesn't use ({string.Join(", ", extra)}): turn them off to join";
        // And each at the version the operator asked for, where they asked for one: two builds of
        // one mod are one name and two mods.
        return wrong.Length > 0 ? $"this server needs {string.Join(", ", wrong)}: install that version to join" : null;
    }

    private void SendWorld(Player p)
    {
        if (_world is null) return;
        p.WaitingForWorld = false;
        (p.SentAt, p.SentUniverse) = (_clock(), _world.Universe);
        p.Link.Send(_world.Encode(), _clock());
        Log($"sending {p.Name} the world, {Blob.Size(_world.Data.Length)}");
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
        // Said of a world older than the one last sent: a game says it is in every two seconds until
        // a world comes, and the one sent again takes a while to come.
        if (p.SentAt > 0.0 && universe < p.SentUniverse - 1.0) return;
        // A game holds a world it cannot load yet, as it does while the vehicle editor is open, and
        // loads it when it can: one sat on a world for an hour while the others cleared the sky twice
        // and flew a station to a new orbit, came in with none of that, and was handed the station.
        // Sent again, it goes round the loading once more, as for an operator's reload.
        if (_world is not null && p.SentAt > 0.0 && now - p.SentAt > StaleWorldAfterSeconds && _world.Universe > p.SentUniverse + 1.0)
        {
            Log($"{p.Name} loaded the world {now - p.SentAt:F0} s after it was sent, and it has been saved since: sending it again");
            SendWorld(p);
            return;
        }
        (p.Loaded, p.LoadedAt, p.Seeding) = (true, now, false);
        // A game swaps into the system of the world it loads, so the one it joined from is no
        // longer the one it plays: its saves are of this world, and are labelled as this world.
        if (_worldSystem.Length > 0) p.System = _worldSystem;
        p.LoadedBehind = UniverseClock.Known ? UniverseClock.Now(now) - universe : 0.0;
        if (!UniverseClock.Known)
        {
            UniverseClock.Set(universe, now);
            Log($"universe time starts at {universe:F0} s, from {p.Name}'s world");
        }
        // What was made and what went after the world it loaded was saved. Only another player's:
        // a game builds a craft from its definition only for the player who has it, so one that
        // was described by somebody since gone was taken down for a rebuild that never came, and
        // the joiner's next save took it out of the world.
        // Since the world this game was sent, which a save while it loaded leaves behind the one held now.
        double since = (p.SentAt > 0.0 ? p.SentUniverse : _world?.Universe ?? universe) - SaveMarginSeconds;
        // Swept first, before anything made since is built from its definition.
        foreach ((string like, string[] spared, double at, bool debris) in _sweeps)
        {
            if (at >= since) _udp.Send(new Wire.Swept(like, _protectedSerial, debris, spared.Length > 0 ? spared : null), p.At);
        }
        foreach ((string key, Craft c) in _crafts)
        {
            if (c.Definition is not null && c.Universe >= since && c.Owner != 0 && c.Owner != p.Id) p.Link.Send(c.Definition, now);
        }
        foreach ((string key, double at) in _gone) if (at >= since) _udp.Send(new Wire.Gone(key), p.At);
        // Before the owners, which list each craft under the name it has now.
        foreach ((string was, string to, byte by, double renamedAt, _, uint serial) in _renames) if (renamedAt >= since) _udp.Send(new Wire.Name(was, to, by, serial), p.At);
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
                    Log($"{p.Name} sent a world nobody asked for, {Blob.Size(blob.Data.Length)}: left where it came from");
                    return;
                }
                // It is handed to everyone who joins after and kept on disk: one that is no zip, or
                // holds a name that climbs out of a save, is asked of somebody else as one its
                // game would not hand over is.
                if (Blob.WorldFault(blob.Data) is { } fault)
                {
                    p.WorldAsked = false;
                    OnNoWorld(p, fault);
                    return;
                }
                p.WorldAsked = false;
                // The system the save says, where it says one: a game that has swapped into this
                // world's system since it joined is not in the one it joined from.
                SetWorld(blob, p.KsaBuild, p.Mods, WorldStore.SystemOf(blob.Data) is { Length: > 0 } saved ? saved : p.System);
                Log($"{p.Name} saved the world: {Blob.Size(blob.Data.Length)} at universe time {blob.Universe:F0} s");
                KeepWaiting(fresh: true, from: p.Id);
                if (p.Seeding) OnLoaded(p, blob.Universe);
                return;
            // A game's own log of a session, which it sends only to the developer's test server.
            // Not kept on any other: a game that sent one anyway is somebody's own build.
            case BlobKind.Log when blob.Key.Length > 0:
                if (!Options.CollectLogs) return;
                (string Log, int First)? piece = LogPieces.Parse(blob.Key);
                if (piece is null && p.LogsKept >= PlayerLogs.MostPerSession) return;
                if (LogText.Unpack(blob.Data) is not { } text)
                {
                    Log($"{p.Name}'s game sent a log that is not one, {Blob.Size(blob.Data.Length)}: not kept");
                    return;
                }
                if (piece is { } part)
                {
                    if (p.LogChars + text.Length > PlayerLogs.MostCharsPerJoin) return;
                    p.LogChars += text.Length;
                    // Said once a log a join: a game sends what is new of each every few minutes.
                    bool first = !p.LogFiles.ContainsKey(part.Log);
                    if (PlayerLogs.Append(p.Name, p.Id, p.DiscordId, p.DiscordName, part.Log, part.First, text, DateTime.UtcNow, p.LogFiles) is > 0 && first)
                    {
                        Log($"keeping {p.Name}'s game log of {Wire.Clip(part.Log, Wire.KeyChars)}, from line {part.First + 1}, as it comes");
                    }
                    return;
                }
                p.LogsKept++;
                if (PlayerLogs.Keep(p.Name, p.Id, p.DiscordId, p.DiscordName, blob.Key, text, DateTime.UtcNow) is not null)
                {
                    Log($"kept {p.Name}'s game log of {Wire.Clip(blob.Key, Wire.KeyChars)}, {Blob.Size(blob.Data.Length)}");
                }
                return;
            case BlobKind.Vehicle when p.Loaded && blob.Key.Length > 0:
                // A definition under a name this player was refused is dropped; the claim that goes with
                // it tells their game the name it may use, and the definition is sent again under that.
                // Asked only of a craft that is not theirs yet: a craft held for being near is still
                // held that way after its holder has said how it is built.
                // Nor of one another player has in any way: asking for it outright would take a
                // craft held for being near, and a piece KSA had named after old wreckage somebody
                // was standing beside became that wreckage, in every game.
                if (OwnerOf(blob.Key) is var has && has != p.Id && (has != 0 || Grant(p, blob.Key) is null))
                {
                    SayRenamedAgain(p, blob.Key);
                    return;
                }
                Craft craft = _crafts[blob.Key];
                long his = 0, all = 0;
                foreach (Craft kept in _crafts.Values)
                {
                    if (kept.Definition is null || kept == craft) continue;
                    all += kept.Definition.Length;
                    if (kept.Owner == p.Id) his += kept.Definition.Length;
                }
                if (his + bytes.Length > PlayerDefinitionBytes || all + bytes.Length > AllDefinitionBytes)
                {
                    Limit(p, "more craft definitions than the server keeps");
                    return;
                }
                // Written before the one already here, by the same game: the last to arrive is
                // not the last that was written. A piece of wreckage written down twice two
                // seconds apart had the first finish arriving after the second on a wire that
                // loses datagrams, and every other game, and everybody who joined after, was
                // given the piece as it had been.
                // Only against one heard lately: universe time is put back when a world is handed
                // out again or left empty, and a craft written down after that is not an old word.
                if (craft.WrittenBy == p.Id && craft.Definition is not null && blob.Universe < craft.Written
                    && _clock() - craft.WrittenHeard < OlderWordWithinSeconds) return;
                (craft.WrittenBy, craft.Written, craft.WrittenHeard) = (p.Id, blob.Universe, _clock());
                Blob stamped = blob with { Owner = p.Id, Universe = UniverseClock.Now(_clock()) };
                craft.Definition = stamped.Encode();
                craft.Described++;
                Log($"{p.Name} says how {blob.Key} is built, {Blob.Size(blob.Data.Length)}"
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
    /// <param name="near">Whether it is asked for only for being within this player's physics: given where nobody has it, and never taken from anybody.</param>
    /// <param name="undocked">Whether their game made it by undocking it at a port, which is what the log names an undocking by.</param>
    private string? Grant(Player p, string key, bool taking = false, bool made = false, bool near = false, bool undocked = false)
    {
        // A craft its game makes is its own whenever it is made; it asks again for anything else.
        if (!made && OwnerOf(key) != p.Id && TooFarBehind(p)) return null;
        bool fresh = !_crafts.TryGetValue(key, out Craft? craft);
        if (craft is null)
        {
            // A name that has gone is not claimed back, or a game that has not heard yet would
            // bring the craft back by speaking of it. But a craft a game has just made is another
            // craft: KSA hands a destroyed piece's name to the next piece shed, and refused the name,
            // that piece stood in its maker's world as nobody's and in no other world at all --
            // three of the twenty-eight a rocket falling over broke into, and six of fifty.
            // Said gone to the game asking, too: one asking for a gone name still has the craft, and
            // the name is forgotten here at the next save, after which the ask was granted. On
            // ksamp.gg 25 craft a clear had taken came back that way as one player's, in no other
            // world, until he left.
            if (_gone.ContainsKey(key) && !taking && !made)
            {
                _udp.Send(new Wire.Gone(key), p.At);
                return null;
            }
            // Nor by flying it, where it went into another player's craft at a docking: the game
            // that was flying it asks for it every two seconds until it hears, and asked for once
            // more after the docking it came back as a craft no world had, flown by a player who
            // was riding in another.
            if (!made && _dockings.Any(d => d.Consumed == key && d.By != p.Id)) return null;
            // Nor a name a craft has just been given up for, made or not: a game that has not heard
            // yet still has the craft under it, and asking for it would make it two craft.
            if (_renames.Any(r => r.Was == key && _clock() - r.At < RenameToldForSeconds)) return null;
            // Made again under its name, which is a craft that has undocked: that docking is over.
            if (!RoomFor(p)) return null;
            _dockings.RemoveAll(d => d.Consumed == key);
            _docked.Remove(key, out string? into);
            if (undocked) Log($"{p.Name} undocked {key}" + (into is null ? "" : $" from {Survivor(into)}"));
            _crafts[key] = craft = new Craft();
        }
        if (craft.Owner == p.Id)
        {
            if (near) craft.NearAt = _clock();
            else if (craft.Near)
            {
                // Asked for outright by the player who had it for being near: theirs like any other.
                (craft.Near, craft.AttendedAt) = (false, _clock());
                OwnersChanged();
            }
            return key;
        }
        // Kept for a player who has just left: theirs when they come back, and nobody else's before.
        if (craft.Owner == Wire.Away && !KeptFor(craft, p))
        {
            if (!made || !RoomFor(p)) return null;
            return Renamed($"{craft.KeptName}, who has just left, has a craft called {key}");
        }
        // Held by somebody only for being near them, and asked for by a player who means to have
        // it: given. That hold exists so a craft is in one place, not so that it is anybody's.
        bool yielded = craft.Near && !near && !made;
        if (craft.Owner != 0 && !yielded && _players.TryGetValue(craft.Owner, out Player? holder))
        {
            // A craft this game made that happens to share a name is a second craft, not the one the
            // holder has, and is given a name of its own. Anything else is the holder's to fly.
            if (!made || !RoomFor(p)) return null;
            return Renamed($"{holder.Name} already has a craft called {key}");
        }
        // Nobody's, but another game's: the last to say how it is built was somebody else. KSA
        // numbers the pieces a craft sheds in each game by itself, so two games' debris come out
        // under one name, and the second taking the first's record replaced that craft in every
        // world. A craft this game describes itself, or one undocked under its own name again, is
        // the same craft carrying on.
        if (made && !undocked && craft.Owner == 0 && craft.WrittenBy != 0 && craft.WrittenBy != p.Id)
        {
            if (!RoomFor(p)) return null;
            return Renamed($"a craft {Named(craft.WrittenBy)} made is already called {key}");
        }
        // Let go of for somebody a moment ago, and asked for by anyone else without meaning to fly
        // it: kept for them. Flying it outright is still anybody's to do.
        if (!taking && !made && craft.FreedFor != 0 && craft.FreedFor != p.Id && _clock() - craft.FreedAt < FreedForSeconds
            && _players.TryGetValue(craft.FreedFor, out Player? waited))
        {
            if (!craft.FreedSaid)
            {
                craft.FreedSaid = true;
                Log($"{p.Name}'s game asked for {key}, which is kept for {waited.Name} for {FreedForSeconds:0} s");
            }
            return null;
        }
        if (!RoomFor(p)) return null;
        p.Crafts++;
        if (_players.TryGetValue(craft.Owner, out Player? was) && was.Controlling == key) Flying(was, "");
        (craft.Owner, craft.AttendedAt, craft.Near, craft.NearAt, craft.Said) = (p.Id, _clock(), near, _clock(), null);
        // Only a craft that was here before changes hands: one just made is its maker's.
        (craft.FreedFor, craft.FreedSaid, craft.TakenAt) = (0, false, fresh ? double.NegativeInfinity : _clock());
        _gone.Remove(key);
        OwnersChanged();
        return key;

        string Renamed(string why)
        {
            p.Crafts++;
            string fresh = FreeKey(key);
            _crafts[fresh] = new Craft { Owner = p.Id, AttendedAt = _clock() };
            _gone.Remove(fresh);
            OwnersChanged();
            _udp.Send(new Wire.Rename(key, fresh), p.At);
            Log($"{why}, so {p.Name}'s is {fresh} here");
            return fresh;
        }
    }

    // Dockings lately told of, so a game that never heard can be told again when it next speaks of
    // a craft the docking took from it.
    private readonly List<(string Consumed, string Survivor, byte By, double At)> _dockings = [];
    private const double DockingToldForSeconds = 30.0;

    // What each craft a docking took went into, kept until its name is a craft again, so the log can
    // say what a craft undocked from.
    private readonly Dictionary<string, string> _docked = [];

    // The craft standing now that one went into: what it went into may itself have docked since.
    private string Survivor(string into)
    {
        for (int i = 0; i < _docked.Count && _docked.TryGetValue(into, out string? next); i++) into = next;
        return into;
    }

    /// <summary>
    /// Two craft docked in one player's game, one of them that player's: the craft that is left is
    /// theirs and the other is out of the world, whoever had either.
    ///
    /// <para>A docking happens in one world. Two games each see the ports touch, a few frames
    /// apart, and each would take the other's craft into its own: the first to say so has it, and
    /// the other player's game is told what became of the craft it was flying. Said again by the
    /// game until the roster shows it, and answered the same way each time.</para>
    /// </summary>
    private void OnDock(Player p, string consumed, string survivor)
    {
        double now = _clock();
        _dockings.RemoveAll(d => now - d.At > DockingToldForSeconds);
        // Said again only while it is still so: the same two docked a second time are a second docking.
        bool again = _dockings.Any(d => d.Consumed == consumed && d.Survivor == survivor && d.By == p.Id)
                     && !_crafts.ContainsKey(consumed) && OwnerOf(survivor) == p.Id;
        if (!again)
        {
            // Only of a craft of their own, and only while both are in the world: a game cannot
            // take two craft that are other players' by saying they docked.
            if (OwnerOf(consumed) != p.Id && OwnerOf(survivor) != p.Id) return;
            if (_gone.ContainsKey(consumed) && !_crafts.ContainsKey(consumed)) return;
            // And only of two craft that are together, where one is another player's in earnest.
            // A game's word that two craft docked takes one of them from whoever had it, and
            // taken on trust it took any craft in the world from anybody: both must have been
            // said, by the players who have them, to be in one place.
            foreach (string key in new[] { consumed, survivor })
            {
                if (OwnerOf(key) is var theirs && (theirs == 0 || theirs == p.Id || _crafts[key].Near)) continue;
                if (Apart(consumed, survivor) is not { } metres || metres > DockWithinM)
                {
                    if (now >= _dockRefusedNext)
                    {
                        _dockRefusedNext = now + 5.0;
                        Log($"{p.Name} says {consumed} docked into {survivor}, and {Named(theirs)}'s {key} is not beside the other: not taken");
                    }
                    return;
                }
            }
            string was = string.Join(" and ", new[] { consumed, survivor }
                .Select(k => (Key: k, Owner: OwnerOf(k)))
                .Where(k => k.Owner != 0 && k.Owner != p.Id && !_crafts[k.Key].Near)
                .Select(k => $"{Named(k.Owner)}'s {k.Key}"));
            _crafts.Remove(consumed);
            _gone[consumed] = UniverseClock.Now(now);
            if (!_crafts.TryGetValue(survivor, out Craft? left)) _crafts[survivor] = left = new Craft();
            if (left.Owner != p.Id) left.Said = null;
            (left.Owner, left.Near, left.AttendedAt) = (p.Id, false, now);
            _gone.Remove(survivor);
            foreach (Player q in _players.Values)
            {
                if (q.Controlling == consumed || (q != p && q.Controlling == survivor)) Flying(q, "");
            }
            _dockings.Add((consumed, survivor, p.Id, now));
            _docked[consumed] = survivor;
            foreach (double after in GoneAgainAfterSeconds) _goneAgain.Add((consumed, p.Id, now + after));
            OwnersChanged();
            Log($"{p.Name} docked {consumed} into {survivor}" + (was.Length > 0 ? $", taking {was}" : ""));
            // One craft now, and protected if either was. The name that went stays on the list:
            // it is the name KSA gives the half that comes away again when they undock.
            if ((Protected.Has(consumed) || Protected.Has(survivor)) && Protected.Add(survivor))
            {
                Log($"{survivor} is protected from now, having docked with {consumed}, which was");
                SayProtected();
            }
        }
        Broadcast(new Wire.Dock(consumed, survivor, p.Id), q => q.Loaded && q != p);
    }

    // Renames, for a game that loads a world saved before them and for one that speaks of a craft
    // under the name it had: newest last, and as many as a world is likely to see between saves.
    private readonly List<(string Was, string Now, byte By, double Universe, double At, uint Serial)> _renames = [];
    private readonly List<(string Was, string Now, byte By, double At, uint Serial)> _renameAgain = [];
    private uint _renameSerial;
    private const double RenameToldForSeconds = 30.0;
    private const int RenamesKept = 200;

    /// <summary>
    /// A rename typed at the console, or by an admin in a game: "Old name -> New name".
    /// </summary>
    public string Rename(string asked, Player? by = null)
    {
        int arrow = asked.IndexOf("->", StringComparison.Ordinal);
        if (arrow < 0) return "usage: rename <craft> -> <new name>";
        return RenameCraft(asked[..arrow], asked[(arrow + 2)..], by) ?? $"renamed to {Wire.Clip(asked[(arrow + 2)..], Wire.KeyChars)}";
    }

    /// <summary>
    /// A craft given another name in every game, or why not: null where it was done.
    ///
    /// <para>A craft's name is what every game and this server know it by, so it is renamed here
    /// first and everybody follows: who has it, how it is built, whether it is protected. A
    /// player renames a craft that is theirs; an admin any craft, and only an admin one that is
    /// protected, since protecting it is an admin's word that it stays as it is.</para>
    /// </summary>
    public string? RenameCraft(string askedWas, string askedNow, Player? by)
    {
        string was = Wire.Clip(askedWas, Wire.KeyChars), now = Wire.Clip(askedNow, Wire.KeyChars);
        if (was.Length == 0 || now.Length == 0) return "usage: rename <craft> -> <new name>";
        if (Wire.IsPattern(now) || now.Contains("->", StringComparison.Ordinal)) return $"'{now}' can't be a craft's name";
        was = _crafts.Keys.FirstOrDefault(k => string.Equals(k, was, StringComparison.OrdinalIgnoreCase)) ?? was;
        if (!_crafts.TryGetValue(was, out Craft? craft)) return $"the server doesn't know a craft called {was}: somebody has to have flown or watched it";
        if (was == now) return $"{was} is already called that";
        bool admin = by is null || by.Role >= Wire.Role.Trusted;
        if (Protected.Has(was) && !admin) return $"{was} is protected, so only a trusted player or an admin can rename it";
        if (!admin && (craft.Owner != by!.Id || craft.Near)) return $"{was} isn't yours to rename";
        if (_crafts.ContainsKey(now)) return $"there's already a craft called {now}";
        if (_dockings.Any(d => (d.Consumed == was || d.Survivor == was) && _clock() - d.At < DockingToldForSeconds)) return $"{was} has just docked: try again in a moment";

        double clock = _clock();
        _crafts.Remove(was);
        _crafts[now] = craft;
        // A name freed by a craft that went is a name this one may have, and a game that loads a
        // world from before would otherwise be told this one has gone.
        _gone.Remove(now);
        foreach ((string consumed, string into) in _docked.ToArray()) if (into == was) _docked[consumed] = now;
        if (craft.Definition is not null && Blob.Decode(craft.Definition) is { } definition) craft.Definition = (definition with { Key = now }).Encode();
        foreach (Player q in _players.Values.Where(q => q.Controlling == was)) Flying(q, now);
        if (Protected.Remove(was))
        {
            Protected.Add(now);
            SayProtected();
        }
        uint serial = ++_renameSerial;
        _renames.Add((was, now, by?.Id ?? 0, UniverseClock.Now(clock), clock, serial));
        if (_renames.Count > RenamesKept) _renames.RemoveAt(0);
        foreach (double after in GoneAgainAfterSeconds) _renameAgain.Add((was, now, by?.Id ?? 0, clock + after, serial));
        OwnersChanged();
        Broadcast(new Wire.Name(was, now, by?.Id ?? 0, serial), q => q.Loaded);
        string who = by?.Name ?? "the operator";
        Log($"{who} renamed {was} to {now}");
        // Soon, so a game that joins loads the craft under the name it has now.
        _snapshotNext = Math.Min(_snapshotNext, clock + RemoveSavesAfterSeconds);
        return null;
    }

    // One datagram is one chance, and a game that never hears keeps the craft under its old name
    // while every other game has it under the new one.
    private void SayRenameAgain(double now)
    {
        for (int i = _renameAgain.Count - 1; i >= 0; i--)
        {
            (string was, string to, byte by, double at, uint serial) = _renameAgain[i];
            if (now < at) continue;
            _renameAgain.RemoveAt(i);
            if (_crafts.ContainsKey(to)) Broadcast(new Wire.Name(was, to, by, serial), p => p.Loaded);
        }
    }

    /// <summary>Tells a player again of a rename, where they are still speaking of the craft by the name it had.</summary>
    private void SayRenamedAgain(Player p, string key)
    {
        if (_crafts.ContainsKey(key)) return;
        for (int i = _renames.Count - 1; i >= 0; i--)
        {
            (string was, string now, byte by, _, double at, uint serial) = _renames[i];
            if (was != key || _clock() - at > RenameToldForSeconds) continue;
            if (_crafts.ContainsKey(now)) _udp.Send(new Wire.Name(was, now, by, serial), p.At);
            return;
        }
    }

    // How far apart two craft may be said to be and still have docked: the length of the longest
    // of them and what a state half a second old leaves out, and nothing like across a sky.
    private const double DockWithinM = 500.0, DockSaidWithinSeconds = 10.0;
    private double _dockRefusedNext;

    /// <summary>How far apart two craft were last said to be, at one instant, or null where either has not been said lately or they go round different bodies.</summary>
    private double? Apart(string one, string other)
    {
        if (!_crafts.TryGetValue(one, out Craft? a) || !_crafts.TryGetValue(other, out Craft? b)) return null;
        if (a.Said is not { } sa || b.Said is not { } sb || sa.Body != sb.Body) return null;
        double universe = UniverseClock.Now(_clock());
        if (Math.Abs(universe - sa.Universe) > DockSaidWithinSeconds * Math.Max(UniverseClock.Rate, 1.0)
            || Math.Abs(universe - sb.Universe) > DockSaidWithinSeconds * Math.Max(UniverseClock.Rate, 1.0)) return null;
        // Each was said at its own moment, and in low orbit half a second is four kilometres.
        Vec3 then = sb.PositionCci + (sb.VelocityCci * (sa.Universe - sb.Universe));
        return (sa.PositionCci - then).Length;
    }

    /// <summary>Tells a player again of a docking that took a craft they are still speaking of as their own.</summary>
    private void SayDockedAgain(Player p, string key)
    {
        SayRenamedAgain(p, key);
        foreach ((string consumed, string survivor, byte by, double at) in _dockings)
        {
            if (by == p.Id || (consumed != key && survivor != key) || _clock() - at > DockingToldForSeconds) continue;
            // Not once the name is somebody's again in its own right: undocked, a craft has its
            // name back, and its first owner asking for it is asking for that craft.
            if (key == consumed && _crafts.ContainsKey(consumed)) continue;
            if (key == survivor && OwnerOf(survivor) != by) continue;
            _udp.Send(new Wire.Dock(consumed, survivor, by), p.At);
            return;
        }
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

    // Told at the next tick, unless it was only just told.
    private void OwnersChanged() =>
        _ownersNext = Math.Min(_ownersNext, _ownersSaidAt + Math.Clamp(_ownersPages * OwnersPerPageSeconds, OwnersAtLeastSeconds, OwnersEverySeconds));

    private string Named(byte id) => _players.TryGetValue(id, out Player? p) ? p.Name : id == Wire.Away ? "a player who has just left" : "somebody";

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
            RunWarp((float)Reached(_warpSpeed, Flyable(speed)), now);
            _governNext = now + GovernEverySeconds;
            return;
        }
        if (_warpPhase == Wire.WarpPhase.Asking)
        {
            // Whoever is asking may still reach for another speed while the rest answer: it is the
            // same question at a different number, so the answers already given stand. Nobody else
            // gets a word in, since two warps put at once is a room that agrees to neither.
            if (p.Id != _warpWho || normal || Math.Abs(speed - _warpWanted) < 0.001f) return;
            if (Unflyable(p, speed)) return;
            // Except from slower than normal to faster: a game agrees by itself to anything that
            // slows the world, and that yes stood for the 30x its asker went on to, which its player
            // had set to be asked about.
            if (_warpWanted <= 1f && speed > 1f)
            {
                _warpVotes.Clear();
                (_warpVotes[p.Id], _warpAskedAt) = (true, now);
            }
            (_warpWanted, _warpSpeed) = (speed, (float)Flyable(speed));
            Log($"{Named(_warpWho)} now asks for {speed:0.##}x");
            Broadcast(WarpState(), q => q.Loaded);
            return;
        }
        if (normal) return;
        // Just ended: nobody's ask is taken until the world has been at normal speed this long, the
        // asker's own included, since a warp another player can pick up the instant it ends is a
        // warp nobody can get out of.
        if (now - _warpQuietSince < WarpQuietSeconds) return;
        if (Unflyable(p, speed)) return;

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

    // Under this a warp is not worth everybody's answer: 1.01x and 1.02x were put to the room.
    private const double LeastWarpAsked = 1.25;

    /// <summary>
    /// Whether a speed above normal cannot be flown by somebody here at all, said to the asker. Put
    /// to the room it was held to 1x and asked as "warp to 1x", which everyone was asked to agree to
    /// and nobody understood; the asker learns whose machine it is instead.
    /// </summary>
    private bool Unflyable(Player p, float speed)
    {
        if (speed <= 1f || Flyable(speed) >= LeastWarpAsked) return false;
        (double can, byte who) = Room();
        Tell(p, who == p.Id
            ? $"your game can't run the world faster than {can:0.##}x right now, so it stays at normal speed"
            : $"{Named(who)}'s game can't run the world faster than {can:0.##}x right now, so it stays at normal speed");
        return true;
    }

    private void OnWarpVote(Player p, bool yes)
    {
        if (_warpPhase != Wire.WarpPhase.Asking || !_warpVotes.TryAdd(p.Id, yes)) return;
        double now = _clock();
        if (!yes)
        {
            EndWarp(now, $"{Named(_warpWho)}'s warp was cancelled: {p.Name} said no");
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
        float agreed = _warpSpeed, from = (float)Reached(UniverseClock.Speed, agreed);
        RunWarp(from, now);
        // A whole look away, or the first step of the climb is taken on the tick this one is.
        _governNext = now + GovernEverySeconds;
        Log($"everyone agreed: the world runs at {agreed:0.##}x for {Named(_warpWho)}");
        Broadcast(new Wire.Chat(0, from < agreed
            ? $"the world is climbing to {agreed:0.##}x, {Named(_warpWho)}'s"
            : $"the world is warping at {agreed:0.##}x, {Named(_warpWho)}'s"), q => q.Loaded);
    }

    private void RunWarp(float speed, double now)
    {
        // The server's own clock is what every game follows, so warping it warps all of them together.
        (_warpSpeed, _warpQuietSince, _warpNext) = (speed, now, 0.0);
        Fell(UniverseClock.Speed, speed, now);
        UniverseClock.SetSpeed(speed, now);
        // The clock's own word is what a game takes its speed from, and it is said five times a
        // second: left to its turn, a change of speed is up to a fifth of a second of every game
        // running at the old one, which coming down from 1000x is two hundred universe-seconds.
        _clockNext = 0.0;
        Broadcast(WarpState(), p => p.Loaded);
    }

    /// <summary>
    /// The clock is never behind the whole room: where every game says it is further past this
    /// clock than a nudge can close, the clock keeps up.
    ///
    /// <para>The end of a warp makes exactly that state, and nothing else does. A game cannot learn
    /// the warp has stopped until the word reaches it, and it runs at the old speed until then --
    /// so at 1000x, the round trip plus a frame is several hundred universe-seconds of overshoot,
    /// made <em>by</em> the ending and not present before it. Measured at 400 s over a 50 ms link.
    /// Left alone that gap is fatal rather than harmless, because <see cref="ClockFollow.Speed"/>
    /// reads it in seconds of the rate now running: 0.4 s at 1000x, 400 s at 1x, past the
    /// standstill. Every game in the room then stops dead until a 1x clock has closed a gap a 1000x
    /// warp opened -- half a minute of warp bought eight minutes of frozen game, for everyone and
    /// not just whoever asked. `errors.md` 2026-11-09.</para>
    ///
    /// <para>No game is ever asked to rewind, and one game's runaway clock cannot drag the room: it
    /// takes every one of them being ahead, and then the <em>least</em>-ahead is what the clock
    /// moves to. Except just after the clock's own speed has fallen, when it goes on to the game
    /// furthest past it, by no more than that fall explains. A game that is not at a craft is not
    /// keeping the clock and does not count.</para>
    /// </summary>
    private void FollowTheRoom(double now)
    {
        if (!UniverseClock.Running || now < _followNext) return;
        _followNext = now + FollowEverySeconds;
        double rate = Math.Max(UniverseClock.Rate, 0.001);
        (double least, double most) = (double.PositiveInfinity, double.NegativeInfinity);
        foreach (Player p in _players.Values)
        {
            if (!p.Loaded || !p.Here || !double.IsFinite(p.Late)) continue;
            (least, most) = (Math.Min(least, -p.Late), Math.Max(most, -p.Late));
        }
        if (!double.IsFinite(least)) return;
        // Every game being past it is the clock being behind, whatever the cause. One game being
        // past it is that game's affair -- unless the clock's own speed has just fallen, when
        // being past it is what every game is, by however long the word took to reach it. Those
        // differ: at the end of a warp at 1000x one game was 359 s past and the other 201 s, and
        // a clock that went to the nearer left the further standing still for the difference,
        // a second for a second. The other way round costs the nearer game a moment of catching
        // up, which is what a game does best. Held to what the fall explains, so it is still not
        // one game's word that moves a room.
        double explained = now - _fellAt < FollowAFallSeconds ? _fellBy * FollowLeadSeconds : 0.0;
        double by = Math.Max(least, Math.Min(most, explained));
        // Only past what the nudge band closes on its own, so ordinary play is left alone and this
        // is the standstill being avoided rather than a clock chasing every wobble.
        if (by <= 0.0 || by / rate <= ClockFollow.CatchUpAboveSeconds) return;
        double to = UniverseClock.Now(now) + by;
        // A lead is a number off the wire like any other.
        if (!ClockFollow.SaneUniverse(to)) return;
        UniverseClock.Set(to, now);
        // Said at once, so the next thing a game says of its lateness is said about this clock.
        _clockNext = 0.0;
        foreach (Player p in _players.Values)
        {
            (p.Late, p.LateUniverse) = (p.Late + by, p.LateUniverse + by);
            p.Unheard.Add((now, by));
        }
        Log(by > least + 0.5
            ? $"a game is {by:F0} s past the clock that has just slowed, so the clock goes on to it"
            : $"every game is {by:F0} s past the clock, so the clock keeps up");
    }

    /// <summary>Notes a fall in the clock's own speed, which is what leaves games past it: each ran at the old speed until it heard.</summary>
    private void Fell(double from, double to, double now)
    {
        if (!(to < from)) return;
        _fellBy = (now - _fellAt < FollowAFallSeconds ? _fellBy : 0.0) + (from - to);
        _fellAt = now;
    }

    private void EndWarp(double now, string why)
    {
        if (_warpPhase == Wire.WarpPhase.None) return;
        (_warpPhase, _warpSpeed, _warpWanted, _warpQuietSince, _warpNext) = (Wire.WarpPhase.None, 1f, 1f, now, 0.0);
        (_warpCeiling, _warpCeilingWho) = (double.PositiveInfinity, 0);
        _warpVotes.Clear();
        Fell(UniverseClock.Speed, Options.Speed, now);
        UniverseClock.SetSpeed(Options.Speed, now);
        _clockNext = 0.0;
        Log(why);
        Broadcast(new Wire.Chat(0, why), p => p.Loaded);
        Broadcast(WarpState(), p => p.Loaded);
    }

    private Wire.Warp WarpState() => new(_warpWho, _warpSpeed, _warpPhase,
        _warpPhase == Wire.WarpPhase.Asking
            ? [.. _players.Values.Where(q => q.Loaded && !_warpVotes.ContainsKey(q.Id)).Select(q => q.Id).Order()]
            : []);

    // Whether the world held is the save last asked for, give or take the seconds a game is behind.
    private bool Fresh() => _world is not null && _world.Universe >= _snapshotAskedAt - FreshBehindSeconds;

    private bool AskSnapshot(string why, byte notThisOne = 0)
    {
        if (Savers(notThisOne).FirstOrDefault() is not { } from) return false;
        _snapshotAskedAt = UniverseClock.Now(_clock());
        AskWorldOf(from, why);
        return true;
    }

    // Who to ask for a save, first to last: the player in the world longest, whose world has had the
    // most time to settle, unless they are in the vehicle editor and can write none, or have let an
    // ask go unanswered: on ksamp.gg one game in longest sat an hour answering nothing, and every
    // ask went to it first.
    private IEnumerable<Player> Savers(byte notThisOne = 0)
    {
        double now = _clock();
        return _players.Values.Where(p => p.Loaded && p.Id != notThisOne)
            .OrderBy(p => p.Editing)
            .ThenBy(p => p.WorldAsked && now - p.WorldAskedSince > AskNextAfterSeconds)
            .ThenBy(p => p.LoadedAt);
    }

    // Every ask for a world goes through here, because the asking is what makes the answer the
    // server's to keep: a world is written only for a player this was called for.
    private void AskWorldOf(Player p, string why)
    {
        _worldAskedLast = _worldAskSent = _clock();
        if (!p.WorldAsked) p.WorldAskedSince = _clock();
        (p.WorldAsked, p.WorldAskedAt, p.WorldAskedWhy) = (true, _clock(), why);
        _udp.Send(new Wire.Snapshot(why), p.At);
    }

    /// <summary>
    /// Asks again the player whose world this server is waiting to be given, while nothing of it
    /// has come.
    ///
    /// <para>The ask is one datagram, and it leaves on the heels of the welcome. Lost, or arriving
    /// ahead of that welcome at a game that is not yet anybody, it is never heard: the game sits
    /// at "sending your world to the server" with nothing asking it to, the server has no world
    /// and no clock, and everybody who joins after waits behind them for good. A wire that held
    /// datagrams 50 ms give or take 20 did it on the first join it was tried on.</para>
    /// </summary>
    private void AskAgainForTheWorld(double now)
    {
        foreach (Player p in _players.Values)
        {
            if (p.Seeding && p.WorldAsked && p.Link.Arriving is null && now - p.WorldAskedAt > AskAgainSeconds)
            {
                AskWorldOf(p, p.WorldAskedWhy);
            }
        }
    }

    // Long enough for a game to have written a save and begun sending it, which is what stops the asking.
    private const double AskAgainSeconds = 3.0;

    private double _worldAskedLast = double.NegativeInfinity;

    // When a game was last actually asked: _worldAskedLast also moves when there is nobody left to ask.
    private double _worldAskSent = double.NegativeInfinity;

    /// <summary>
    /// The next game in asked for a save, while the one last asked for has not come and the game
    /// asked has sent nothing: for a player waiting on it, and for the save every
    /// <see cref="ServerOptions.SnapshotEverySeconds"/>, or one game that will not answer keeps the
    /// world where it was for as long as it stays. Each game is asked once a round; an answer from
    /// any of them is taken.
    /// </summary>
    private void AskSomebodyElse(double now)
    {
        if (now - _worldAskedLast < AskNextAfterSeconds || Fresh() && !_players.Values.Any(q => q.WaitingForWorld)) return;
        if (_players.Values.Any(q => q.WorldAsked && q.Link.Arriving is not null)) return;
        List<Player> held = [.. _players.Values.Where(q => q.WorldAsked && q.Loaded && !q.Seeding)];
        if (held.Count == 0) return;
        if (Savers().FirstOrDefault(q => !q.WorldAsked) is not { } next)
        {
            // Nobody left to ask: the wait runs out and the save there is goes.
            _worldAskedLast = now;
            return;
        }
        Log($"{string.Join(", ", held.Select(h => h.Name))} sent no save in {AskNextAfterSeconds:0} s, so {next.Name} is asked too");
        AskWorldOf(next, held[0].WorldAskedWhy);
    }

    /// <summary>
    /// A game that will not hand its world over, because what it holds would not load anywhere.
    /// Somebody else is asked: a world an hour old is worth more than one nobody can open, and the
    /// world already here is left alone until a good one comes.
    /// </summary>
    private void OnNoWorld(Player p, string why)
    {
        Log($"{p.Name} will not hand over a world: {why}");
        Broadcast(new Wire.Chat(0, $"{p.Name}'s game couldn't provide a save of the world: {why}"), q => q.Loaded);
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
    /// <summary>How often the clock checks whether the whole room has run past it.</summary>
    private const double FollowEverySeconds = 0.5;

    // How long after the clock's speed falls a game past it is taken to be past it for that reason,
    // and how many seconds of the speed lost such a lead may be.
    private const double FollowAFallSeconds = 6.0, FollowLeadSeconds = 2.0;

    // How long past a round trip a game is given to have taken the clock's word up, which is a
    // frame of its own, and the longest round trip a game's own word for it is believed.
    private const double HeardWithinSeconds = 0.05, MostRoundTripSeconds = 2.0;

    private const double GovernEverySeconds = 1.0, GovernFall = 0.8, GovernStep = 1.5, GovernBackOff = 0.6;

    // How far apart two of a game's words on its lateness may be and still say which way it is going.
    private const double SlipOverAtLeast = 0.2, SlipOverAtMost = 3.0;

    // A game losing this share of what the clock ran, while already this far behind in its own
    // seconds, is being left. A step up costs a game about a sixth of the second it happens in --
    // the word has to reach it and KSA has to wind up -- so being left is set clear of that, and
    // still inside what a step too far looks like: a machine asked for half as much again as it
    // can fly loses a third.
    private const double GovernSlipping = 0.25, GovernBehindSeconds = 0.25;

    // A game behind is catching up if, at the pace of its last two words, it is level again within
    // this long. Not merely gaining: one a minute and a half behind and gaining a twentieth of a
    // second a second is a machine at its limit, and a slower clock is the only help it will get.
    private const double GovernRepaySeconds = 15.0;

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

    /// <summary>Whether every watching player's machine has said what it can fly, so the room's limit is a measurement and not a silence.</summary>
    private bool Measured()
    {
        bool anyone = false;
        foreach (Player p in _players.Values)
        {
            if (!p.Loaded || !p.Here) continue;
            if (!(p.Can > 0.0)) return false;
            anyone = true;
        }
        return anyone;
    }

    /// <summary>
    /// The speed a warp is run at on its way from <paramref name="at"/> to <paramref name="to"/>:
    /// down at once, and up at once only to a speed the room has measured itself against.
    ///
    /// <para>A game comfortable at the speed it is running says nothing of what it can fly, and at
    /// normal speed that is every game there is. So an ask is put to a room that has said nothing,
    /// and run at in full it is a second of the clock at that speed before anybody can say they
    /// cannot keep up: at 1000x, a thousand universe-seconds no game was near, and the room then
    /// nineteen seconds making them up. Silence is not room. Up, a warp starts a step above normal
    /// and climbs the way it climbs back from a fall, which is the only way a machine's limit is
    /// ever found.</para>
    /// </summary>
    private double Reached(double at, double to) =>
        to <= at || Measured() ? to : Math.Min(to, Math.Max(at, GovernStep));

    /// <summary>Whether any watching player is behind and falling further behind: asked for more than it is managing, whatever it says.</summary>
    private bool Slipping()
    {
        double rate = Math.Max(UniverseClock.Rate, 1.0);
        foreach (Player p in _players.Values)
        {
            if (p.Loaded && p.Here && p.Late > GovernBehindSeconds * rate && p.Slip > GovernSlipping) return true;
        }
        return false;
    }

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

        // Behind, and still making it up at this speed: this speed is one it can fly. Backing off
        // for a gap that is closing only reads the same gap in slower seconds, where it is longer,
        // so the next look backs off again and the one after, down to normal speed with the gap
        // not yet repaid -- a room that could fly sixty times real time sat at one.
        bool closing = _players.TryGetValue(lateWho, out Player? behind) && behind.Slip < 0.0 && late / -behind.Slip < GovernRepaySeconds;

        string why = "";
        // Named, because the game holding the room back is the one whose log says why.
        if (double.IsFinite(room) && room < at * GovernFall) (next, limits, why) = (Math.Max(1.0, room), roomWho, $"the room can fly {room:0.##}x, {Named(roomWho)}'s game no faster");
        // Nobody has hit a ceiling, but somebody is losing ground all the same: back off and watch.
        else if (late > GovernLateSeconds && !closing) (next, limits, why) = (Math.Max(1.0, at * GovernBackOff), lateWho, $"{Named(lateWho)} is {late:0.#} s behind");
        // Room to try: a machine's limit is only found by asking it for a little more, and never
        // again for as much as has already been found not to work. Not while anybody is being left,
        // which the server sees a second or two before the game has measured it. And never down:
        // a machine saying it flies a little less than this is no ceiling, and reading it as one
        // held a warp to four fifths of where it stood for good.
        else if (at < _warpWanted && late < GovernLateSeconds * 0.5 && !Slipping())
        {
            next = Math.Max(at, Math.Min(Math.Min(_warpWanted, at * GovernStep), Math.Min(room, _warpCeiling)));
        }
        if (Math.Abs(next - at) < 0.01) return;

        // What it fell from is as fast as this warp goes from here, unless the machine it fell for
        // says otherwise or its asker asks again.
        if (next < at) (_warpCeiling, _warpCeilingWho) = (Math.Max(1.0, at * GovernFall), limits);
        RunWarp((float)next, now);
        Log(next < at
            ? $"the world falls to {next:0.##}x: {why}"
            : $"the world rises to {next:0.##}x of {_warpWanted:0.##}x");
    }

    /// <summary>
    /// What a player is flying, told to everyone the moment it changes rather than at the next
    /// roster: a name still hanging over the craft its player has just stepped out of is the whole
    /// of what anybody notices.
    /// </summary>
    // Only while the name is still gone. One given since to a craft somebody has just made is that
    // craft's, and saying it gone again would take the new craft out of every world.
    private void SayGoneAgain(double now)
    {
        for (int i = _goneAgain.Count - 1; i >= 0; i--)
        {
            (string key, byte from, double at) = _goneAgain[i];
            if (now < at) continue;
            _goneAgain.RemoveAt(i);
            if (_gone.ContainsKey(key) && !_crafts.ContainsKey(key)) Broadcast(new Wire.Gone(key), p => p.Loaded && p.Id != from);
        }
    }

    /// <summary>
    /// Craft their owners have walked away from, given back. Ownership is sticky so that two games
    /// never pull one hull in two directions -- but sticky for ever meant the first player to touch
    /// a thing held it until they left, and a second player could fly nothing at all.
    /// </summary>
    private void Lapse(double now)
    {
        bool any = false;
        Dictionary<string, List<string>> back = [], lapsed = [];
        foreach ((string key, Craft craft) in _crafts)
        {
            if (craft.Owner == 0) continue;
            if (craft.Owner == Wire.Away)
            {
                if (_players.Values.FirstOrDefault(q => q.Loaded && KeptFor(craft, q)) is { } again)
                {
                    (craft.Owner, craft.AttendedAt) = (again.Id, now);
                    again.Crafts++;
                    Add(back, again.Name, key);
                }
                else if (now >= craft.KeptUntil)
                {
                    craft.Owner = 0;
                    Add(lapsed, craft.KeptName, key);
                }
                else continue;
                (craft.KeptName, craft.KeptDiscord) = ("", "");
                any = true;
                continue;
            }
            if (craft.Near)
            {
                // Held for being near and not said to be for this long: its holder has gone
                // elsewhere, or gone.
                if (now - craft.NearAt > NearForSeconds)
                {
                    (craft.Owner, craft.Near) = (0, false);
                    any = true;
                }
                continue;
            }
            if (!_players.TryGetValue(craft.Owner, out Player? owner))
            {
                continue;
            }
            if (owner.Controlling == key || owner.Watching == key || FliesBeside(owner, key))
            {
                craft.AttendedAt = now;
                continue;
            }
            if (now - craft.AttendedAt < Options.LapseWithin) continue;
            // Only a craft somebody else is waiting on. Most of a world is parked craft nobody is
            // looking at, and letting those go handed them round hundreds of times an hour, each
            // time moving the camera of whoever happened to be watching one.
            // Nor one only a game far behind is waiting on, which could not have it anyway.
            // Nor for a player flying something else, whose game would not take it: freed for them,
            // it went to whoever was near it instead, or back to the owner. And only for one who has
            // been looking at it a while.
            if (_players.Values.FirstOrDefault(q => q.Id != owner.Id && q.Loaded && q.Watching == key && q.Controlling.Length == 0
                                                    && now - q.WatchingSince >= Options.WatchToTake && BehindBy(q) <= TakeBehindSeconds) is not { } waiting) continue;

            (craft.Owner, craft.FreedFor, craft.FreedAt) = (0, waiting.Id, now);
            any = true;
            Log($"{key} is {waiting.Name}'s to take: {owner.Name} has not been near it for {Options.LapseWithin:0} s, and {waiting.Name} has watched it for {now - waiting.WatchingSince:0} s");
        }
        foreach ((string who, List<string> keys) in back) Log($"{who} is back, and has {Named(keys)} again");
        foreach ((string who, List<string> keys) in lapsed) Log($"{who} did not come back within {Options.KeptAway:0} s, so {Named(keys)} {(keys.Count == 1 ? "is" : "are")} nobody's now");
        if (any) OwnersChanged();

        static void Add(Dictionary<string, List<string>> to, string who, string key)
        {
            if (!to.TryGetValue(who, out List<string>? keys)) to[who] = keys = [];
            keys.Add(key);
        }
    }

    // By Discord id where they left logged in, which a name is not proof of; by name otherwise.
    private static bool KeptFor(Craft c, Player p) =>
        c.KeptDiscord.Length > 0 ? c.KeptDiscord == p.DiscordId : string.Equals(c.KeptName, p.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a player flies something else close by a craft they hold, which is attending it as
    /// much as looking at it: a player docking with a station they had taken earlier is flying
    /// their own craft, with the camera on it. On ksamp.gg such a station was handed to a player
    /// only watching it while the two were metres apart, and from then on the contact was between
    /// one game's craft and another's: in each world the other's copy was pushed by the touch and
    /// put back by the pull, and the station juddered against the craft docking with it.
    /// </summary>
    private bool FliesBeside(Player owner, string key) =>
        owner.Controlling.Length > 0 && Apart(owner.Controlling, key) is { } metres && metres <= AttendsWithinM;

    // As far as a game holds a craft for being near what its player flies (the mod's NearWithinM).
    private const double AttendsWithinM = 10_000.0;

    // By its last word since it loaded, or by the world it loaded until it has said.
    private static double BehindBy(Player p) => p.LateAt >= p.LoadedAt ? p.Late : p.LoadedBehind;

    private bool TooFarBehind(Player p)
    {
        double behind = BehindBy(p);
        if (behind <= TakeBehindSeconds) return false;
        double now = _clock();
        if (now - p.ToldBehindAt > ToldBehindEverySeconds)
        {
            p.ToldBehindAt = now;
            Log($"{p.Name} is {behind:F0} s behind: no craft is handed to them until they catch up");
        }
        return true;
    }

    private const double ToldBehindEverySeconds = 60.0;

    // How long after a craft was written down an older word of it can still be on its way.
    private const double OlderWordWithinSeconds = 30.0;

    // How long a craft held for being near stays held with nothing said of it. A craft at rest is
    // said twice a second.
    private const double NearForSeconds = 3.0;

    private void Flying(Player p, string key)
    {
        if (p.Controlling == key) return;
        p.Controlling = key;
        if (key.Length == 0) p.Editing = false;
        if (key.Length > 0 && _crafts.TryGetValue(key, out Craft? craft)) craft.FlownBy = p.Id;
        _rosterNext = 0.0;
    }

    private void Remove(Player p, string why)
    {
        if (!_players.Remove(p.Id)) return;
        _udp.Send(new Wire.Bye(why == "has left" ? "" : why, p.Session), p.At);
        bool released = false;
        // Kept for them a while, bar a kick, so a game that drops and comes straight back finds its
        // craft where it left it. Let go of at once, a craft somebody was watching went to the
        // watcher in the second its pilot was away: on ksamp.gg a rocket mid-docking, whose pilot
        // came back two seconds later to find it taken and its RCS propellant spent.
        bool keep = why is "has left" or "has gone quiet" && Options.KeptAway > 0.0;
        foreach (Craft c in _crafts.Values.Where(c => c.Owner == p.Id))
        {
            if (keep && !c.Near) (c.Owner, c.KeptName, c.KeptDiscord, c.KeptUntil) = (Wire.Away, p.Name, p.DiscordId, _clock() + Options.KeptAway);
            else (c.Owner, c.Near) = (0, false);
            released = true;
        }
        foreach (Craft c in _crafts.Values.Where(c => c.WrittenBy == p.Id)) c.WrittenBy = 0;
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
            EndWarp(_clock(), $"the warp ended because {p.Name} left");
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

    /// <summary>Says something to one player, which their game shows in the chat as the server speaking.</summary>
    public void Tell(Player p, string text) => _udp.Send(new Wire.Chat(0, Wire.Clip(text, Wire.ChatChars)), p.At);

    // How often a game is told again what it has not said back. A round trip is a fraction of this
    // on any line a game is played over, so one that is heard is almost never said twice.
    private const double TellAgainSeconds = 0.5;

    /// <summary>
    /// Tells a game what it is on this server, and goes on telling it until it says the same back.
    ///
    /// <para>Said once, it was one datagram between an admin and being one. A token lost on its way
    /// left the name with a hash nobody held the token of; a lost word at the join left an admin's
    /// game believing it was nobody's; and a lost revocation left a dead token in a game's keeping.</para>
    /// </summary>
    private void TellAdmin(Player p, Wire.Op op)
    {
        (p.Telling, p.TellNext) = (op, _clock() + TellAgainSeconds);
        _udp.Send(op, p.At);
    }

    /// <summary>
    /// Makes a player who is here an admin and hands their game the token to keep, returning the name
    /// it went to. Null for a name nobody is playing under: the token has to reach a game, and it is
    /// never written down where it could be said to anybody else. What they were an admin by before
    /// works until their game says it has this one.
    /// </summary>
    public string? Op(string name) => Grant(name, Wire.Role.Admin);

    /// <summary>As <see cref="Op"/>, to make a player trusted rather than an admin; an admin trusted is made one.</summary>
    public string? Trust(string name) => Grant(name, Wire.Role.Trusted);

    private string? Grant(string name, Wire.Role role)
    {
        if (Find(name) is not { } p) return null;
        // What the setting makes them is not a command's to lower.
        if (p.Role == Wire.Role.Operator) return p.Name;
        // Logged in with Discord, they are this as that user too, from whichever machine.
        if (p.DiscordId.Length > 0) Admins.AddDiscord(p.DiscordId, p.Name, role);
        MakeRole(p, role);
        return p.Name;
    }

    private void MakeRole(Player p, Wire.Role role)
    {
        // An operator's token is an admin's: what is more than admin comes of a Discord login checked
        // against the setting at each join, and a machine that has kept a token is not that.
        string token = Admins.Offer(p.Name, role == Wire.Role.Operator ? Wire.Role.Admin : role);
        p.Role = role;
        TellAdmin(p, new Wire.Op(true, token, role));
        Tell(p, $"you're {Permissions.Said(role)} on this server: type /help in chat to see the commands");
        SendSaves(p);
        Log($"{p.Name} is {Permissions.Said(role)} from now");
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
                     .Where(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase) && q.Role != Wire.Role.Operator)
                     .ToArray())
        {
            was |= p.Role > Wire.Role.Player;
            Tell(p, $"you're no longer {Permissions.Said(p.Role)} on this server");
            p.Role = Wire.Role.Player;
            TellAdmin(p, new Wire.Op(false, "", Wire.Role.Player));
        }
        if (was) Log($"{name} is no longer an admin or trusted");
        return was;
    }

    /// <summary>Whether a player of this name is here as an operator, whom op, trust and deop leave as they are.</summary>
    public bool IsOperator(string name) => Find(name) is { Role: Wire.Role.Operator };

    private Player? Find(string name) =>
        _players.Values.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Puts a player out, as an operator would from the console.</summary>
    public bool Kick(string name, string why)
    {
        if (Find(name) is not { } p) return false;
        Remove(p, $"was kicked{(why.Length > 0 ? $": {why}" : "")}");
        return true;
    }

    /// <summary>
    /// Puts out the player a line names, "name [why]" or "#id [why]", and says who that was: the
    /// longest name the line starts with, since a name may have spaces in it and the server itself
    /// calls a second Bob "Bob 2". Null if it names nobody.
    /// </summary>
    public string? KickLine(string line)
    {
        line = line.Trim();
        Player? who = null;
        string why;
        if (line.StartsWith('#'))
        {
            string[] parts = line[1..].Split(' ', 2, StringSplitOptions.TrimEntries);
            if (byte.TryParse(parts[0], out byte id)) _players.TryGetValue(id, out who);
            why = parts.Length > 1 ? parts[1] : "";
        }
        else
        {
            who = _players.Values
                .Where(p => line.StartsWith(p.Name, StringComparison.OrdinalIgnoreCase) && (line.Length == p.Name.Length || line[p.Name.Length] == ' '))
                .OrderByDescending(p => p.Name.Length).FirstOrDefault();
            why = who is null ? "" : line[who.Name.Length..].Trim();
        }
        if (who is null) return null;
        Remove(who, $"was kicked{(why.Length > 0 ? $": {why}" : "")}");
        return who.Name;
    }

    /// <summary>Says something to every player, from the console.</summary>
    public void Announce(string text) => Broadcast(new Wire.Chat(0, Wire.Clip(text, Wire.ChatChars)), _ => true);

    /// <summary>Asks for a fresh save of the world now, from the console.</summary>
    public void SnapshotNow() => AskSnapshot("the operator asked for a save");

    // What a world kept is, so one played in is told from an empty one by its name's line alone.
    private static string Described(Blob world, bool timeKnown) =>
        Blob.Size(world.Data.Length) + (timeKnown ? $", universe time {world.Universe:F0} s" : "");

    /// <summary>Whether anybody is in the world, and so anybody to count down for or to save it.</summary>
    public bool AnyonePlaying => _players.Values.Any(p => p.Loaded);

    /// <summary>
    /// A craft handed by the player flying it to one spectating it: theirs at once, and their game
    /// told so it takes the controls. Only to somebody looking at it, so nobody is put in a craft
    /// they never asked to see; and only from its pilot, so nobody gives away what is not theirs.
    /// </summary>
    /// <returns>Why not, or null when it was given.</returns>
    public string? GiveCraft(Player from, string key, byte to)
    {
        if (CannotGive(from, key, to, out Player? taker) is { } why) return why;
        // Closer than any take: a game still catching up with the world after joining is flying the
        // past, and a kitten given to one 3 s behind stood still in the giver's world while it walked.
        // Given once it has, rather than asked for again: told to give it in a moment, a player on
        // ksamp.gg pressed give fifty-five times in twenty seconds.
        if (BehindBy(taker) is var behind && behind > GiveBehindSeconds)
        {
            if (!(_givesWaiting.TryGetValue(key, out (byte From, byte To, double Until) waiting) && waiting.From == from.Id && waiting.To == to))
            {
                _givesWaiting[key] = (from.Id, to, _clock() + GiveWaitsSeconds);
            }
            return $"{taker.Name}'s game is still catching up with the world ({behind:F1} s behind): {key} is theirs once it has";
        }
        return Hand(from, key, taker);
    }

    // Gives asked for while the taker was catching up, by craft: who gave it, to whom, and until when.
    private readonly Dictionary<string, (byte From, byte To, double Until)> _givesWaiting = [];

    // How long a give waits for its taker to catch up. Longer than a join takes to come level, and
    // short enough that a craft is not handed over long after its pilot has forgotten asking.
    private const double GiveWaitsSeconds = 60.0;

    /// <summary>Each give waiting on its taker made once they have caught up, or dropped with its giver told why.</summary>
    private void GiveTheCaughtUp(double now)
    {
        if (_givesWaiting.Count == 0) return;
        foreach ((string key, (byte fromId, byte to, double until)) in _givesWaiting.ToArray())
        {
            if (!_players.TryGetValue(fromId, out Player? from))
            {
                _givesWaiting.Remove(key);
                continue;
            }
            if (CannotGive(from, key, to, out Player? taker) is { } why)
            {
                _givesWaiting.Remove(key);
                Tell(from, $"{key} wasn't given: {why}");
                continue;
            }
            if (BehindBy(taker) > GiveBehindSeconds)
            {
                if (now <= until) continue;
                _givesWaiting.Remove(key);
                Tell(from, $"{key} wasn't given: {taker.Name}'s game didn't catch up within {GiveWaitsSeconds:F0} s");
                continue;
            }
            _givesWaiting.Remove(key);
            if (Hand(from, key, taker) is { } notGiven) Tell(from, notGiven);
        }
    }

    // Why a craft cannot be given from one player to another at all, or null with the taker.
    private string? CannotGive(Player from, string key, byte to, out Player taker)
    {
        taker = null!;
        if (!_crafts.TryGetValue(key, out Craft? craft) || craft.Owner != from.Id || craft.Near || from.Controlling != key)
        {
            return $"you can only give the craft you're flying";
        }
        if (!_players.TryGetValue(to, out Player? found) || found == from || !found.Loaded) return "that player isn't here any more";
        taker = found;
        return taker.Watching != key ? $"{taker.Name} isn't spectating {key} any more" : null;
    }

    private string? Hand(Player from, string key, Player taker)
    {
        if (!RoomFor(taker)) return $"{taker.Name} already has as many craft as this server allows";
        Craft craft = _crafts[key];
        double now = _clock();
        _givesWaiting.Remove(key);
        taker.Crafts++;
        Flying(from, "");
        (craft.Owner, craft.AttendedAt, craft.Near, craft.NearAt, craft.Said) = (taker.Id, now, false, now, null);
        (craft.FreedFor, craft.FreedSaid, craft.TakenAt) = (0, false, now);
        Flying(taker, key);
        OwnersChanged();
        _udp.Send(new Wire.Give(key, from.Id, taker.Session), taker.At);
        Log($"{from.Name} gave {key} to {taker.Name}, who was spectating it");
        return null;
    }

    /// <summary>
    /// Asks every game in the world, or one player's, for its logs now, this session's among them:
    /// what is new of each, from a game that sends them in pieces, and a full set again from one
    /// that sends them whole. Kept only with <see cref="ServerOptions.CollectLogs"/>,
    /// and sent only to the developer's test server: the games decide that.
    /// </summary>
    /// <returns>What came of asking, for whoever asked.</returns>
    public string AskLogs(string who, string why)
    {
        if (!Options.CollectLogs) return "this server keeps no players' logs: collect-logs is off";
        Player[] asked = who.Length == 0
            ? [.. _players.Values.Where(p => p.Loaded)]
            : Find(who) is { } one ? [one] : [];
        if (asked.Length == 0) return who.Length == 0 ? "nobody is playing" : $"nobody called '{who}'";
        foreach (Player p in asked)
        {
            p.LogsKept = 0;
            _udp.Send(new Wire.LogsWanted(why, p.Session), p.At);
        }
        string named = string.Join(", ", asked.Select(p => p.Name));
        Log($"asked {named} for their game's logs: {why}");
        return $"asked {named} for their game's logs; they are kept under {PlayerLogs.Folder} as they arrive";
    }

    /// <summary>
    /// Counts every player down to the server stopping, on their screens, from now. Said again
    /// each second, since a datagram can be lost, until <see cref="Dispose"/>.
    /// </summary>
    public void Close(double seconds, string why)
    {
        double now = _clock();
        (_closingAt, _closingWhy, _closingNext) = (now + seconds, why, now);
        Log($"stopping in {seconds:F0} s: {why}");
        Announce($"The server stops in {seconds:F0} seconds: {why}. The world is saved first.");
    }

    /// <summary>
    /// The last save asked of every game that can write one, and the first to come taken: there is no
    /// time to go round them one by one. Asked of the one game it would have been, a player in the
    /// vehicle editor, ksamp.gg stopped without one and its world went back four and a half minutes.
    /// </summary>
    /// <returns>The universe time a save has to reach to be that one, or null with nobody to ask.</returns>
    public double? AskLastSave()
    {
        List<Player> all = [.. Savers()];
        if (all.Count == 0) return null;
        _snapshotAskedAt = UniverseClock.Now(_clock());
        foreach (Player p in all.Where(p => !p.Editing).DefaultIfEmpty(all[0])) AskWorldOf(p, "the server is stopping");
        return _snapshotAskedAt;
    }

    /// <summary>Whether the world held is at least as late as a universe time, give or take the second a save is allowed.</summary>
    public bool SavedSince(double universe) => _world is { } w && w.Universe >= universe - 1.0;

    private double _closingAt = double.NaN, _closingNext;
    private bool _disposed;
    private string _closingWhy = "";

    // Saves asked for by name that are waiting on a fresh save of the world to be one of, and the
    // player each is waiting on.
    private readonly List<(string Name, byte By, string ByName, double At, byte From)> _keeping = [];

    // Whether the world as held is already on the shelf, under a name or as the world before a
    // load: replacing it then loses nothing, and keeping it again would push an older world out.
    private bool _worldKept;

    // How long a save asked for by name waits for a fresh one before it is made of the world as
    // held. A game writes no save while its vehicle editor is open, and whoever asked is looking
    // at a window that has not changed.
    private const double KeepWithinSeconds = 20.0;

    /// <summary>
    /// Keeps the world under a name, for an admin to load again later, and says what came of asking.
    ///
    /// <para>A fresh save is asked for first, as for a player who joins: the world as held is up
    /// to <see cref="ServerOptions.SnapshotEverySeconds"/> old, and a save named "before the
    /// landing" should not be of five minutes before it. It is asked of the admin's own game where
    /// they are in the world, and only that game's answer is taken for it: a save is of what
    /// whoever asked is looking at, and what goes into it is not for another player's game to
    /// choose. It is kept of the world as held when no answer comes.</para>
    /// </summary>
    /// <param name="by">Who asked, to be told what came of it; null for the console.</param>
    public string Keep(string asked, Player? by = null)
    {
        if (SaveList.Name(asked, out string why) is not { } name) return why;
        if (SaveShelf.Reserved(name)) return $"names starting with '{SaveShelf.AutoPrefix.TrimEnd()}' or '{SaveShelf.FreshPrefix.TrimEnd()}' are reserved for automatic saves";
        if (_world is null) return "there is no world to save yet";
        // A trusted player keeps worlds and never loses one: saving over a save is losing it.
        if (by is { Admin: false } && Saves.Find(name) is { } taken) return $"there's already a save called '{taken}': pick another name, or ask an admin to save over it";
        // Counted with the ones still waiting to be written, or a burst of asks before the first
        // answer comes all pass a limit none of them has reached yet.
        string[] waiting = [.. _keeping.Select(k => k.Name).Where(k => !string.Equals(k, name, StringComparison.OrdinalIgnoreCase) && Saves.Find(k) is null)];
        if (!Saves.RoomFor(name, waiting.Length)) return $"there are already {SaveList.Most} saves: delete one first";
        if (Saves.Find(name) is null && Saves.Bytes + ((long)_world.Data.Length * (waiting.Length + 1)) > SaveShelf.MostBytes)
        {
            return $"the saves take up {Saves.Bytes / (1024 * 1024)} MB of the {SaveShelf.MostBytes / (1024 * 1024)} MB allowed: delete one first";
        }
        string who = by?.Name ?? "the operator";
        _keeping.RemoveAll(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));
        Player? from = by is { Loaded: true, Editing: false } ? by : Savers().FirstOrDefault();
        _keeping.Add((name, by?.Id ?? 0, who, _clock(), from?.Id ?? 0));
        if (from is null) return KeepWaiting(fresh: false, answering: true) ?? $"couldn't save '{name}'";
        _snapshotAskedAt = UniverseClock.Now(_clock());
        AskWorldOf(from, $"{who} is saving the world as '{name}'");
        return $"saving the world as '{name}' once a fresh save arrives from {(from == by ? "your game" : from.Name)}";
    }

    // The saves waiting to be kept, kept of the world as it is now held: all of them, or those that
    // were waiting on this player's save. What the last of them came to, which is also said to
    // whoever asked for each unless this is itself the answer to them.
    private string? KeepWaiting(bool fresh, bool answering = false, byte from = 0)
    {
        if (_world is null) return null;
        string? said = null;
        foreach ((string name, byte by, string byName, double _, byte asked) in _keeping.ToArray())
        {
            if (from != 0 && asked != from) continue;
            _keeping.RemoveAll(k => k.Name == name);
            try
            {
                Saves.Keep(name, _world, _worldBuild, _worldMods, _worldSystem, _worldTimeKnown, byName, protect: Protected.Names);
                _worldKept = true;
                said = $"saved the world as '{name}': {Blob.Size(_world.Data.Length)}"
                       + (_worldTimeKnown ? $" at universe time {_world.Universe:F0} s" : "")
                       + (fresh ? "" : " (from the last autosave: no fresh one arrived)");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                said = $"couldn't save '{name}': {e.Message}";
            }
            if (answering) continue;
            Log($"{byName}: {said}");
            if (_players.TryGetValue(by, out Player? asker) && asker.Name == byName) Tell(asker, said);
        }
        if (said is not null) SaySaves();
        return said;
    }

    // A save asked for by name that no fresh save has come for is made of the world as held, unless
    // one is on its way in.
    private void KeepLate(double now)
    {
        if (_keeping.Count == 0 || now - _keeping[0].At < KeepWithinSeconds) return;
        if (_players.Values.Any(p => p.WorldAsked && p.Link.Arriving is not null)) return;
        KeepWaiting(fresh: false);
    }

    /// <summary>
    /// Puts a kept world in place of the one being played and hands it to everybody where they
    /// stand, and says what came of asking.
    ///
    /// <para>The world it replaces is kept first, under a name of its own
    /// (<see cref="SaveShelf.KeepBeforeLoad"/>), so a load is the one thing here an admin can take
    /// back. Not when that world is already on the shelf -- nobody has played since it was loaded
    /// or saved -- or three loads in a row would push the world everybody was in out of the few
    /// that are kept. It is the world as last saved rather than a fresh one: asking for one first
    /// would put a wait, and a game that may not answer, between an admin and the load they asked
    /// for.</para>
    ///
    /// <para>Refused while somebody here could not follow: a game whose mods are not the save's
    /// would be turned away for them the next time it joined a world it had been playing in. A
    /// save in another system is not refused, since every game swaps into the system of the world
    /// it is handed.</para>
    /// </summary>
    /// <param name="by">Who asked, named to everybody; null for the console.</param>
    public string LoadSave(string asked, Player? by = null)
    {
        if (SaveList.Name(asked, out string why) is not { } name) return why;
        if (_players.Values.Any(p => p.Seeding)) return "a player is still uploading the server's first world: try again once it has arrived";
        (Blob World, bool TimeKnown, string KsaBuild, string[] Mods, string System, string[]? Protected)? found;
        try
        {
            found = Saves.Take(name);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return $"couldn't read '{name}': {e.Message}";
        }
        if (found is not { } save) return $"there is no save called '{name}' (saves lists them)";
        name = Saves.Find(name) ?? name;

        string[] needs = [.. save.Mods.SelectMany(ModList.Ids).Except(ModList.Ids(OptionalMods), StringComparer.OrdinalIgnoreCase)];
        foreach (Player p in _players.Values)
        {
            // A save copied in by hand says nothing of mods, and is taken to want what the room has.
            if (needs.Length == 0) continue;
            string[] has = [.. p.Mods.SelectMany(ModList.Ids)];
            string[] lacks = [.. needs.Except(has, StringComparer.OrdinalIgnoreCase)];
            if (lacks.Length > 0) return $"can't load '{name}': it needs {string.Join(", ", lacks)}, which {p.Name} doesn't have";
            // Where the operator pinned the mods, a join is held to those and not to the world's.
            string[] spare = Options.Mods.Length > 0 ? [] : [.. has.Except(needs, StringComparer.OrdinalIgnoreCase)];
            if (spare.Length > 0) return $"can't load '{name}': {p.Name} has mods enabled that it doesn't use ({string.Join(", ", spare)})";
        }

        string who = by?.Name ?? "the operator";
        // A world loaded mid-warp would start out running at the warp's speed.
        if (_warpPhase != Wire.WarpPhase.None) EndWarp(_clock(), $"the warp ended because {who} is loading a save");
        // What was asked to be kept is kept of the world that is about to go.
        KeepWaiting(fresh: false);
        string keptAs = "", keptWhat = "";
        if (_world is not null && !_worldKept)
        {
            keptWhat = Described(_world, _worldTimeKnown);
            try
            {
                keptAs = Saves.KeepBeforeLoad(_world, _worldBuild, _worldMods, _worldSystem, _worldTimeKnown, who, Protected.Names);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return $"didn't load '{name}': couldn't save the current world first ({e.Message})";
            }
        }
        ForgetTheWorldsCraft();
        // A save on its way in is of the world that is going, and would be put back over this one.
        foreach (Player p in _players.Values) p.WorldAsked = false;
        UniverseClock.Forget();
        SetWorld(save.World, save.KsaBuild, save.Mods, save.System, save.TimeKnown);
        // What was protected in that world comes back with it: a craft is known by its name, and
        // the names are the ones that world's craft have. A save that says nothing of them --
        // one copied in, or kept before the two were kept together -- leaves the list as it is.
        if (save.Protected is not null && Protected.Replace(save.Protected))
        {
            Log($"protected as they were when '{name}' was kept: {(save.Protected.Length > 0 ? string.Join(", ", save.Protected) : "nothing")}");
            SayProtected();
        }
        _worldKept = true;
        int back = ReloadAll($"{who} loaded the save '{name}'");
        string previous = keptAs.Length > 0 ? $"; the previous world is saved as '{keptAs}' ({keptWhat})" : "";
        Log($"the world is now the save '{name}', loaded by {who}"
            + (save.TimeKnown ? $", at universe time {save.World.Universe:F0} s" : "") + previous);
        SaySaves();
        return $"loaded '{name}'" + (back > 0 ? $" for {back} player{(back == 1 ? "" : "s")}" : " (nobody is connected)") + previous;
    }

    /// <summary>
    /// Throws a kept world away, and says what came of asking. Any of them, for an admin as for the
    /// console, the ones kept before a load included: a server run without a console has nobody
    /// else to tidy them, and an admin is already trusted with <c>load</c> and <c>clear</c>.
    /// </summary>
    public string DeleteSave(string asked, Player? by = null)
    {
        if (SaveList.Name(asked, out string why) is not { } name) return why;
        name = Saves.Find(name) ?? name;
        try
        {
            if (!Saves.Delete(name)) return $"there is no save called '{name}' (saves lists them)";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"couldn't delete '{name}': {e.Message}";
        }
        // It may have been the one copy of the world as held.
        _worldKept = false;
        Log($"{by?.Name ?? "the operator"} deleted the save '{name}'");
        SaySaves();
        return $"deleted '{name}'";
    }

    // Which state the list of protected craft is in, counted from the server starting: what a sweep
    // says it was made against, so no game sweeps on a list older than the operator was looking at.
    // Nothing while there has never been one, which is a list no game has to wait for.
    private uint _protectedSerial;

    /// <summary>
    /// Keeps a craft out of every <c>clear</c>, even one that names it in full, and says what came of
    /// asking. By the name the shared world knows it under, which need not be a craft the server
    /// has heard of: it hears only of those somebody has claimed.
    /// </summary>
    public string Protect(string asked, Player? by = null)
    {
        string name = Wire.Clip(asked, Wire.KeyChars);
        if (name.Length == 0) return "usage: protect <craft name>";
        if (name.Contains('*') || name.Contains('?')) return "protect takes one craft's exact name, without wildcards";
        // As the world spells it, where it is a craft the server knows: a name is matched exactly.
        name = _crafts.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;
        if (Protected.Has(name)) return $"{name} is already protected";
        if (!Protected.Add(name)) return $"{ProtectedList.Most} craft are protected already: unprotect one first";
        Log($"{by?.Name ?? "the operator"} protected {name}");
        SayProtected();
        return $"{name} is now protected from clear and from being abandoned"
               + (_crafts.ContainsKey(name) ? "" : " (the server hasn't seen a craft by that name yet: check the spelling)");
    }

    /// <summary>
    /// Takes one craft out of every game, whoever owns it, and says what came of asking: an admin's
    /// Delete in the universe manifest, on a craft that is another player's. Not one being flown,
    /// which is a player's hull, nor one protected, which is unprotected first on purpose.
    /// </summary>
    public string RemoveCraft(string asked, Player? by = null)
    {
        string name = Wire.Clip(asked, Wire.KeyChars).Trim();
        if (name.Length == 0) return "usage: remove <craft name>";
        if (Wire.IsPattern(name)) return "remove takes one craft's exact name, without wildcards: clear takes patterns";
        name = _crafts.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;
        if (_players.Values.FirstOrDefault(p => p.Controlling == name) is { } pilot) return $"{pilot.Name} is flying {name}, so it can't be removed";
        if (Protected.Has(name)) return $"{name} is protected: unprotect it first";
        bool known = _crafts.Remove(name);
        // As a sweep does: kept from being handed back by a game that has not heard yet.
        if (known) _gone[name] = UniverseClock.Now(_clock());
        // The name, as a sweep is: the server need not have heard of it for it to be in every world.
        Broadcast(new Wire.Swept(name, _protectedSerial), p => p.Loaded);
        Swept(name, [], UniverseClock.Now(_clock()));
        if (known) OwnersChanged();
        Log($"{by?.Name ?? "the operator"} removed {name}{(known ? "" : ", which nobody had claimed")}");
        Broadcast(new Wire.Chat(0, $"{by?.Name ?? "the operator"} removed {name}"), p => p.Loaded);
        // Soon rather than now: Clear Debris asks for one piece at a time, and a save each is a
        // world sent up the wire each.
        _snapshotNext = Math.Min(_snapshotNext, _clock() + RemoveSavesAfterSeconds);
        return $"removing {name} from every game";
    }

    private const double RemoveSavesAfterSeconds = 3.0;

    /// <summary>Puts a craft back within reach of <c>clear</c>, and says what came of asking.</summary>
    public string Unprotect(string asked, Player? by = null)
    {
        string name = Wire.Clip(asked, Wire.KeyChars);
        if (name.Length == 0) return "usage: unprotect <craft name>";
        name = Protected.Names.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;
        if (!Protected.Remove(name)) return $"{name} is not protected (protected lists what is)";
        Log($"{by?.Name ?? "the operator"} unprotected {name}");
        SayProtected();
        return $"{name} is no longer protected";
    }

    /// <summary>Tells every game which craft are protected: whenever that changes, since each sweeps its own world.</summary>
    public void SayProtected()
    {
        // The list is part of what a save holds, so the world as held is no longer one that is
        // already kept: the next load keeps it again, with the list as it now is.
        _worldKept = false;
        _protectedSerial++;
        byte[] list = ProtectedBlob();
        foreach (Player p in _players.Values) p.Link.Send(list, _clock());
    }

    private byte[] ProtectedBlob() => new Blob(BlobKind.Protected, "", 0, 0.0, ProtectedList.Encode(Protected.Names.Order(), _protectedSerial)).Encode();

    // Which state the memorial is in, counted from the server starting, so a game never takes an
    // older list over a newer one. Nothing while it is empty, which is a list no game needs.
    private uint _memorialSerial;

    // Whether a death has come since the list was last sent, and when that was. Sent at most once
    // a second: the list is the whole memorial, and a crew dying together is a death each.
    private bool _memorialDue;
    private double _memorialSaidAt = double.NegativeInfinity;
    private const double MemorialEverySeconds = 1.0;

    /// <summary>Tells every game who the dead kittens were lost by, on the next tick that may: each draws its own roster.</summary>
    private void SayMemorial() => _memorialDue = true;

    private byte[] MemorialBlob() => new Blob(BlobKind.Memorial, "", 0, 0.0, MemorialList.Encode(Memorial.Deaths, _memorialSerial)).Encode();

    /// <summary>Tells every admin here which worlds are kept: whenever that changes, so a window showing them never asks.</summary>
    public void SaySaves()
    {
        Player[] admins = [.. _players.Values.Where(p => p.Role >= Wire.Role.Trusted)];
        if (admins.Length == 0) return;
        byte[] list = SavesBlob();
        foreach (Player p in admins) p.Link.Send(list, _clock());
    }

    /// <summary>Hands an operator's game the server's log, as <see cref="LogText"/> packs it, under the days it covers.</summary>
    public void SendLog(Player p, string text, string covers)
    {
        byte[] packed = LogText.Pack(LogText.Unaddressed(text));
        p.Link.Send(new Blob(BlobKind.Log, Wire.Clip(covers, Wire.KeyChars), 0, 0.0, packed).Encode(), _clock());
        Log($"sending {p.Name} the log of {covers}, {Blob.Size(packed.Length)}");
    }

    private void SendSaves(Player p) => p.Link.Send(SavesBlob(), _clock());

    private uint _savesSaid;

    private byte[] SavesBlob() => new Blob(BlobKind.Saves, "", 0, 0.0, SaveList.Encode(Saves.List(), ++_savesSaid)).Encode();

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
        // Universe time goes back with the world, so nothing written before this is newer than
        // what is written after it.
        foreach (Craft c in _crafts.Values) c.WrittenBy = 0;
        Player[] back = [.. _players.Values.Where(p => p.Loaded || p.WaitingForWorld)];
        foreach (Player p in back)
        {
            p.Loaded = false;
            SendWorld(p);
        }
        if (back.Length > 0)
        {
            Log($"the world goes out to {back.Length} player{(back.Length == 1 ? "" : "s")} again: {why}");
            Broadcast(new Wire.Chat(0, $"reloading the world for everyone: {why}"), _ => true);
            _rosterNext = 0.0;
        }
        return back.Length;
    }

    /// <summary>
    /// Every craft of a world being replaced forgotten, and that anybody flies one. Only a word of a
    /// craft clears what a player flies, and a game left flying nothing in the new world says none:
    /// on ksamp.gg a player tabbed out through a fresh start was said to fly the old world's station
    /// until they were kicked, and spectating them found nothing. A game flying something in the
    /// new world claims it again within a couple of seconds.
    /// </summary>
    private void ForgetTheWorldsCraft()
    {
        _crafts.Clear();
        _sweeps.Clear();
        _gone.Clear();
        _docked.Clear();
        foreach (Player p in _players.Values) Flying(p, "");
    }

    /// <summary>
    /// The world thrown away and an empty one put in its place: every craft gone, universe time
    /// started over, and everybody handed the new world where they stand.
    ///
    /// <para>The empty world is one the server carries rather than one it makes, so there has to be
    /// one for a system before this can do anything -- which is also what keeps it from being a
    /// command that quietly leaves a room with no world at all. Craft are dropped here rather than
    /// swept: a sweep takes them out of the world the games are holding, and this is throwing that
    /// world away whole, so there is nothing left for anyone to be flying.</para>
    ///
    /// <para>Time is forgotten with them. Held, every game would load a world that has never been
    /// past a couple of minutes and then sprint to catch a clock still reading the old one's.</para>
    /// </summary>
    /// <param name="by">The admin who asked in a game, or null for the console.</param>
    public string Fresh(string why, Player? by = null)
    {
        if (StockWorld.Zip(Options.EmptyWorld) is not { } zip)
        {
            string carried = string.Join(", ", StockWorld.Systems);
            return Options.EmptyWorld.Length == 0
                ? $"nothing to start from: set empty-world to one of {carried} first"
                : $"no empty world for '{Options.EmptyWorld}'; there is one for {carried}";
        }
        // An empty world nobody has played in has nothing to start over from, and starting over
        // anyway kept it as a "before fresh" save that pushed a world played in towards the five
        // that are kept: a restart between two of them had forgotten the first was kept.
        if (_world is not null && _crafts.Count == 0 && Blob.SameFiles(_world.Data, zip))
        {
            return $"the world is already a new empty one in {Options.EmptyWorld}, so there is nothing to start over";
        }
        // The world thrown away is kept first, as one a load replaces is: a fresh start is the
        // one thing here nobody could otherwise take back. Not when it is already on the shelf.
        KeepWaiting(fresh: false);
        string keptAs = "", keptWhat = "";
        if (_world is not null && !_worldKept)
        {
            keptWhat = Described(_world, _worldTimeKnown);
            try
            {
                keptAs = Saves.KeepBeforeFresh(_world, _worldBuild, _worldMods, _worldSystem, _worldTimeKnown, by?.Name ?? "the operator", Protected.Names);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return $"didn't start afresh: couldn't save the current world first ({e.Message})";
            }
            SaySaves();
        }
        if (_warpPhase != Wire.WarpPhase.None) EndWarp(_clock(), $"the warp ended because {by?.Name ?? "the operator"} is starting the world afresh");
        ForgetTheWorldsCraft();
        UniverseClock.Forget();
        // The craft they named went with the world, and a name left on the list would protect
        // whatever is next given it.
        if (Protected.Names.Count > 0)
        {
            Protected.Clear();
            SayProtected();
        }
        SetWorld(new Blob(BlobKind.World, "", 0, 0.0, zip), Options.KsaBuild, [], Options.EmptyWorld, timeKnown: false);
        int back = ReloadAll(why);
        Log($"the world is new and empty, in {Options.EmptyWorld}: {why}");
        _worldKept = true;
        return $"a new empty world in {Options.EmptyWorld}"
               + (back > 0 ? $", handed to {back} player{(back == 1 ? "" : "s")}" : ", with nobody here to give it to")
               + (keptAs.Length > 0 ? $"; the world before it is kept as '{keptAs}' ({keptWhat})" : "");
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
        // Nor a craft a player here holds outright and has flown: at the 2026-11-22 session's last
        // clear that was a player's own craft, parked while they flew another. What they hold and
        // never flew -- what their game shed, or took for being near -- is debris like any other.
        string[] spared = [.. _crafts.Where(k => !flown.Contains(k.Key) && Wire.Matches(k.Key, like) && !ProtectedList.Spares(Protected.Names, k.Key)
                                                 && k.Value.Owner != 0 && !k.Value.Near && k.Value.FlownBy == k.Value.Owner && _players.ContainsKey(k.Value.Owner))
                                     .Select(k => k.Key).Take(Wire.MostSparedInASweep)];
        List<string> swept = [];
        double universe = UniverseClock.Now(_clock());
        foreach ((string key, Craft _) in _crafts.ToArray())
        {
            if (flown.Contains(key) || spared.Contains(key) || !Wire.Matches(key, like) || ProtectedList.Spares(Protected.Names, key)) continue;
            _crafts.Remove(key);
            _gone[key] = universe;
            swept.Add(key);
        }
        // The name, not the list: most of what is out there was never claimed by anybody, so the
        // server has never heard of it and a list drawn up here would miss the whole of the debris.
        // Each game matches the name against its own world, and what the server knew of them is
        // dropped above so that nothing hands them back.
        Broadcast(new Wire.Swept(like, _protectedSerial, Spared: spared.Length > 0 ? spared : null), p => p.Loaded);
        Swept(like, spared, universe);
        OwnersChanged();
        Log($"the operator is sweeping the world by '{(like.Length > 0 ? like : "*")}'"
            + $"{(swept.Count > 0 ? $"; {swept.Count} of them were known here: {Named(swept)}" : "")}"
            + $"{(spared.Length > 0 ? $"; spared, as their players' own: {SparedNamed(spared)}" : "")}");
        Broadcast(new Wire.Chat(0, "clearing out every craft that nobody is flying"
                                   + (spared.Length > 0 ? $", except {spared.Length} that players here have flown" : "")), p => p.Loaded);
        // The world on disk still holds them, so the next player to join would be handed them all
        // back: this is the one moment it is worth saving afresh. The save comes from a game that
        // has already swept, so it is clean, and nobody has to be put back on it -- everybody is
        // standing in a world they swept themselves. Clearing therefore costs nobody a reload and
        // does not touch the clock; reload is there for an operator who wants that, and says so.
        AskSnapshot("the world was cleared of craft nobody was flying");
        return swept;
    }

    private string SparedNamed(string[] keys) =>
        string.Join(", ", keys.Take(6).Select(k => $"{Named(_crafts[k].Owner)}'s {k}")) + (keys.Length > 6 ? $" and {keys.Length - 6} more" : "");

    /// <summary>
    /// Sweeps lately made, so a game still loading a world from before one is told it once it is
    /// in: a sweep goes to games in the world, and one sent the world the same second as a clear
    /// came in with everything the clear had taken. The server can say gone only what it knew of,
    /// which is not most of the debris.
    /// </summary>
    private readonly List<(string Like, string[] Spared, double Universe, bool Debris)> _sweeps = [];
    private const int SweepsKept = 32;

    private void Swept(string like, string[] spared, double universe, bool debris = false)
    {
        _sweeps.Add((like, spared, universe, debris));
        if (_sweeps.Count > SweepsKept) _sweeps.RemoveAt(0);
    }

    /// <summary>
    /// Debris swept out of the world: each game takes away what of it is debris in its own world --
    /// what KSA calls debris, and any craft with nothing on it to control it -- bar what is flown or
    /// protected, and says gone what of it is its own. Nothing is dropped here, since a name does
    /// not say whether a craft is debris and the parts that do are in the games.
    /// </summary>
    public void SweepDebris(string like = "")
    {
        Broadcast(new Wire.Swept(like, _protectedSerial, Debris: true), p => p.Loaded);
        Swept(like, [], UniverseClock.Now(_clock()), debris: true);
        Log($"sweeping the world of debris by '{(like.Length > 0 ? like : "*")}'");
        Broadcast(new Wire.Chat(0, "clearing out debris nobody is flying"), p => p.Loaded);
        // The games say gone what is theirs as they sweep; the save is asked for a moment after,
        // from a game that has swept, as it is after a clear.
        AskSnapshot("the world was cleared of debris");
    }

    private static string Named(List<string> keys) =>
        string.Join(", ", keys.Take(6)) + (keys.Count > 6 ? $" and {keys.Count - 6} more" : "");

    // Craft defined and gone before this were saved with the world, and need not be handed out again.
    private void Prune(double universe)
    {
        // Nor anything a game still loading an older world is yet to be told when it is in.
        foreach (Player p in _players.Values)
        {
            if (!p.Loaded && !p.WaitingForWorld && p.SentAt > 0.0) universe = Math.Min(universe, p.SentUniverse);
        }
        foreach ((string key, Craft c) in _crafts.ToArray())
        {
            if (c.Definition is not null && c.Universe < universe - SaveMarginSeconds) c.Definition = null;
            if (c.Definition is null && c.Owner == 0) _crafts.Remove(key);
        }
        foreach ((string key, double at) in _gone.ToArray()) if (at < universe - SaveMarginSeconds) _gone.Remove(key);
    }

    private Wire.Roster Roster()
    {
        Wire.Roster roster = new([.. _players.Values.OrderBy(p => p.Id).Select(p => new Wire.Player(p.Id, p.Name, p.Loaded, p.Controlling, p.Rtt, (float)p.Can, p.DiscordId.Length > 0, p.Watching,
                                                                                                     p.InEditor || p.Editing))]);
        // A craft's name is up to 255 bytes, and thirty players flying such are more than a datagram:
        // the roster then went to nobody. Said with the names of craft cut short, it still says who is here.
        for (int chars = 32; chars >= 0 && !Fits(roster); chars -= 16)
        {
            roster = new([.. roster.Players.Select(p => p with { Controlling = Wire.Clip(p.Controlling, chars), Watching = Wire.Clip(p.Watching, chars) })]);
        }
        return roster;
    }

    private bool Fits(Wire.Message m)
    {
        try { return Wire.Write(m, _measure) <= Wire.BufferBytes; }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { return false; }
    }

    private IEnumerable<Wire.Owners> OwnerPages()
    {
        Wire.Owner[] all = [.. _crafts.Where(c => c.Value.Owner != 0).Select(c => new Wire.Owner(c.Key, c.Value.Owner, c.Value.Near))];
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
        // "server" is how what the server says itself is shown.
        string root = name.Length > 0 && !name.Equals("server", StringComparison.OrdinalIgnoreCase) ? name : "Kitten";
        string unique = root;
        for (int k = 2; _players.Values.Any(p => string.Equals(p.Name, unique, StringComparison.OrdinalIgnoreCase)); k++)
        {
            unique = $"{root} {k}";
        }
        return unique;
    }

    // How many notes a game may have written down in a burst, and how fast that fills again.
    private const double NotesAtOnce = 5.0, NotesASecond = 0.2;

    // A craft of seven coming apart is seven at once, and nothing honest says more than that often.
    private const double DeathsAtOnce = 16.0, DeathsASecond = 0.5;

    // On one line, whatever was in it: a craft's name and a game's build come from other machines,
    // and one with a line break in it wrote a line of the log that never happened.
    private void Log(string what) => Said.Enqueue(what.AsSpan().IndexOfAny('\r', '\n') < 0 ? what : what.Replace('\r', ' ').Replace('\n', ' '));

    /// <summary>Says goodbye to everyone and closes the socket.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        string why = _closingWhy.Length > 0 ? _closingWhy : "the server is shutting down";
        foreach (Player p in _players.Values) _udp.Send(new Wire.Bye(Wire.Clip(why, 200), p.Session), p.At);
        _udp.Dispose();
    }
}

/// <summary>
/// How many of one game's pings were lost on the way, over the last minute. A game pings once a
/// second by its own clock and says when, so a gap of a few seconds between two that came is the
/// ones that did not. A longer gap is a game that stopped -- loading, or stalled -- which is not
/// the line's doing, and counts for nothing.
/// </summary>
public sealed class PingLoss
{
    private const double GapAtMostSeconds = 5.0, OverSeconds = 60.0;
    private const int AtLeastHeard = 30;
    private readonly Queue<(double At, int Missed)> _gaps = new();
    private double _sent = double.NaN;

    public void Heard(double sent, double arrived)
    {
        if (!double.IsFinite(sent)) return;
        double gap = sent - _sent;
        _sent = sent;
        // Out of order, or the first: nothing to count.
        if (!(gap > 0.0)) return;
        _gaps.Enqueue((arrived, gap <= GapAtMostSeconds ? Math.Max(0, (int)Math.Round(gap) - 1) : 0));
        while (_gaps.TryPeek(out (double At, int Missed) old) && arrived - old.At > OverSeconds) _gaps.Dequeue();
    }

    public double Share
    {
        get
        {
            if (_gaps.Count < AtLeastHeard) return double.NaN;
            int missed = _gaps.Sum(g => g.Missed);
            return (double)missed / (_gaps.Count + missed);
        }
    }
}
