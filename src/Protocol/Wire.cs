using System.Buffers.Binary;
using System.Text;

namespace KSAMultiplayer;

/// <summary>A position or velocity in doubles: the server has no KSA, so none of its vector types.</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    public double Length => Math.Sqrt((X * X) + (Y * Y) + (Z * Z));
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
}

/// <summary>An attitude as a unit quaternion.</summary>
public readonly record struct Quat(double X, double Y, double Z, double W)
{
    public static readonly Quat Identity = new(0, 0, 0, 1);
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z) && double.IsFinite(W);
    public double LengthSquared => (X * X) + (Y * Y) + (Z * Z) + (W * W);

    public Quat Normalized()
    {
        double n = Math.Sqrt(LengthSquared);
        return n > 0 ? new Quat(X / n, Y / n, Z / n, W / n) : Identity;
    }

    /// <summary>The shortest-path blend from a to b, for drawing a craft between two states.</summary>
    /// <summary>
    /// Two rotations one after the other: <paramref name="by"/> applied in the body's own frame and
    /// then <paramref name="a"/>, which is the order an attitude is turned by a body-frame spin.
    /// </summary>
    public static Quat operator *(Quat a, Quat by) => new(
        (a.W * by.X) + (a.X * by.W) + (a.Y * by.Z) - (a.Z * by.Y),
        (a.W * by.Y) - (a.X * by.Z) + (a.Y * by.W) + (a.Z * by.X),
        (a.W * by.Z) + (a.X * by.Y) - (a.Y * by.X) + (a.Z * by.W),
        (a.W * by.W) - (a.X * by.X) - (a.Y * by.Y) - (a.Z * by.Z));

    /// <summary>
    /// The turn an angular velocity makes in a time: a rotation about the direction of
    /// <paramref name="spin"/>, at its length in radians a second, for <paramref name="seconds"/>.
    /// </summary>
    public static Quat Turn(Vec3 spin, double seconds)
    {
        double rate = spin.Length;
        double angle = rate * seconds;
        // Nothing to turn by, and nothing to divide by either.
        if (!(angle > 1e-12)) return Identity;
        double half = angle * 0.5, k = Math.Sin(half) / rate;
        return new Quat(spin.X * k, spin.Y * k, spin.Z * k, Math.Cos(half));
    }

    public static Quat Slerp(Quat a, Quat b, double t)
    {
        double dot = (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z) + (a.W * b.W);
        if (dot < 0)
        {
            b = new Quat(-b.X, -b.Y, -b.Z, -b.W);
            dot = -dot;
        }
        if (dot > 0.9995)
        {
            return new Quat(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t), a.W + ((b.W - a.W) * t)).Normalized();
        }
        double theta = Math.Acos(dot), s = Math.Sin(theta);
        double wa = Math.Sin((1 - t) * theta) / s, wb = Math.Sin(t * theta) / s;
        return new Quat((a.X * wa) + (b.X * wb), (a.Y * wa) + (b.Y * wb), (a.Z * wa) + (b.Z * wb), (a.W * wa) + (b.W * wb));
    }
}

/// <summary>
/// What the games and the server say, as UDP datagrams: a mark and a version, so anything else is
/// dropped, a kind, and the body. Little-endian throughout.
///
/// <para>Nothing small is resent: a state is stale before a resend would come. What has to arrive
/// either goes as a <see cref="Transfer"/>, which asks for its lost pieces again, or is repeated
/// until it is answered or superseded.</para>
/// </summary>
public static class Wire
{
    /// <summary>
    /// Changed whenever what a datagram means changes: a game speaking another version is refused by
    /// name.
    ///
    /// <para>It is also the only way to say "everybody moves together", and 13 and 14 were both that
    /// rather than a change to the wire: 12, 13 and 14 carry identical datagrams.</para>
    ///
    /// <para>13 was for fixes one straggler could undo for the room -- an old mod asks everybody to
    /// pause each time its player opens the vehicle editor, and describes a craft coming apart
    /// twenty times over for the rest to rebuild.</para>
    ///
    /// <para>14 is for the launchpads, which are the other way round: they are laid out by each game
    /// from its own content, so a game without them has no landmark where another game just launched.
    /// KSA gives a craft within a pad's footprint <c>GroundOffset + SurfaceHeight</c> and everything
    /// else nothing, so the same craft stands on concrete in one world and sunk in the grass in the
    /// next, with no pad under it and nothing for its physics to rest on. The shape of the shared
    /// world changed without a byte of the wire changing, which is the case this number is for.</para>
    ///
    /// <para>15 is the same test again. A game of 14 launching from the launch menu puts its craft
    /// on a site's own pad however many are standing there, so two craft overlap and everybody
    /// watches them go up; and a game of 14 tears another player's craft down and builds it again
    /// every time it changes shape, which drops exploding copies into its world whose pieces it
    /// then adopts and sends to everyone as craft of its own. Both are one game making a mess the
    /// rest have to look at.</para>
    ///
    /// <para>16 is the rest of that one, found by staging a rocket apart with two games watching.
    /// A game of 15 keeps every piece its own copy of another player's rocket sheds: it is right not
    /// to claim them, since they are nearer that player's craft than its own, but it leaves them
    /// standing. Nothing drives them, the owner's sweep does not name them, and they are in physics.
    /// Twenty of a world's thirty-seven craft were that, and where the distance went the other way
    /// -- two players at one launch site -- the same pieces were claimed and sent to everybody
    /// instead. A game that does not read a name as saying what it came off cannot tell either
    /// from the launch its own player just made.</para>
    ///
    /// <para>17 does change the datagrams: a hello and a welcome each carry a cookie, because a
    /// server would otherwise hold a place on its roster for any address a datagram claims to come
    /// from. It also follows a review of the whole protocol, and a game of 16 in the room still
    /// takes a roster round it only half received and tells its player that somebody else is flying
    /// their craft.</para>
    ///
    /// <para>18 carries the datagrams of 17 unchanged and moves for the usual reason: a game of 17
    /// makes a mess the rest have to look at. It can ask the whole room to run at ten times speed
    /// from inside its own pause menu, where it sees none of it and is not running at it itself. And
    /// every craft it claimed on one frame sends on one frame for ever, so sixty fragments of a
    /// broken rocket arrive as a burst twice a second whose losses fall together -- which is what
    /// runs a craft past the end of what the others can carry it on from, in their worlds rather
    /// than its own.</para>
    ///
    /// <para>19 has a welcome say which system the server's world is in and which mods it wants, the
    /// cookie's included, so a game can ask both before it has a universe: start in the right system,
    /// and be told what to enable while there is still a start screen to do it from. KSA reads its
    /// mods once, at startup, so a mod a player learns about after a world has loaded costs them a
    /// restart. A refusal grew with the same reasoning, from 200 characters to 600, since the one
    /// worth reading names every mod that is missing.</para>
    ///
    /// <para>20 adds <see cref="Alive"/>, which a game sends off its own thread while its frame loop
    /// is stalled. Before it, ten seconds of stutter read as a player who had gone, and a game that
    /// hitched on a rebuild was thrown out of a world it was still in.</para>
    ///
    /// <para>21 is KSA 2026.11.10.5801 rather than anything of this protocol's own: a craft
    /// definition is KSA's own serialisation of one, and that build drops the <c>ResourceManager</c>
    /// element from it. The build each player runs is checked too, but only when the world says
    /// which build it is -- a world the server carries records none, so nothing else would stop a
    /// game on either side of the change from building another's craft from a definition it reads
    /// differently.</para>
    ///
    /// <para>22 is the ground at a launch site, which this mod now levels out to where it puts its
    /// own pads. Nothing of the protocol changed and nothing of the terrain crosses the wire: each
    /// game works it out from the same content and arrives at the same answer. That is exactly why
    /// it has to be gated here -- a game that had not, sharing a world with games that had, would
    /// place every craft at a launch site against ground of a different shape, and at Vandenberg,
    /// where the levelling is 52 m deep, another player's rocket would stand buried in a hillside
    /// that is not there for anybody else.</para>
    ///
    /// <para>23 has a welcome carry how many players are on a server, the most it takes, whether it
    /// wants a password and which KSA build it wants. All of it was already known to a server and
    /// said nowhere a game could read it before joining: the player count only in its own console,
    /// the most players only in the refusal for being the one over it, and the build only by being
    /// refused for the wrong one. A list of servers is unreadable without them -- a row with no
    /// player count is half a row, and a row that cannot say "needs KSA 2026.11.10.5801" sends a
    /// player to find that out by being thrown out.</para>
    /// </summary>
    public const ushort Version = 24;

    /// <summary>The port a server listens on unless it is given another, and what an address with no port in it means.</summary>
    public const int DefaultPort = 50100;

    public const int BufferBytes = 8192;
    public const int NameChars = 24;
    public const int KeyChars = 96;
    public const int ChatChars = 200;

    /// <summary>The longest an admin's token is: 32 random bytes in base64url come to 43 characters.</summary>
    public const int TokenChars = 64;

    /// <summary>The longest a join cookie is: 16 bytes in base64url come to 22 characters.</summary>
    public const int CookieChars = 32;

    /// <summary>
    /// The most characters a refusal carries. Long enough to name every mod a world wants and the
    /// player has not: a refusal nobody can act on is the whole reason this is not 200.
    /// </summary>
    public const int RefusedChars = 600;

    /// <summary>
    /// The most characters of the world's mod list a welcome carries, which is fewer than the hello's
    /// 4,000 on purpose.
    ///
    /// <para>The welcome that carries it is the cookie's, answered to an address the server has not
    /// heard from before, so its size is an amplifier aimed at whoever a datagram claims to be from.
    /// A hello is about 140 bytes; this keeps the answer near 470 rather than 4,400, which is a
    /// reflector nobody wants. Twenty mod ids fit, the authoritative comparison happens at the real
    /// handshake, and a list longer than this is clipped rather than refused.</para>
    ///
    /// <para>The player count, the most players, the flags and the build added in version 23 come to
    /// about twenty bytes of that, taking the factor from roughly 3.2 to 3.4. Anything further put
    /// in a welcome is spent out of the same budget, and a field worth fewer bytes than it costs
    /// reflection belongs in a datagram only a joined player gets.</para>
    /// </summary>
    public const int ProbeModsChars = 300;

    /// <summary>The most entries one <see cref="Owners"/> carries.</summary>
    public const int OwnersPerPage = 40;

    /// <summary>
    /// The most of a datagram one <see cref="Owners"/> page may fill, leaving room for its header.
    ///
    /// <para>Because a count of entries is not a size. A key is clipped to <see cref="KeyChars"/>
    /// *characters* and may still be 255 *bytes* -- 85 of most of the world's writing systems -- so
    /// forty of them came to 10 KB in an 8 KB buffer and the write threw at the thirty-second. It
    /// threw from outside the socket's own guard, unwinding past the line that says when to send the
    /// next roster, so every tick after it threw in the same place: a server that went on reading
    /// datagrams and reaping sessions while never again sending a clock or pumping a transfer.</para>
    /// </summary>
    public const int OwnersBudgetBytes = BufferBytes - 512;

    /// <summary>What one owners entry costs in a datagram: a length byte, the key, the owner, and how it is held.</summary>
    public static int OwnerBytes(string key) =>
        3 + Math.Min(255, System.Text.Encoding.UTF8.GetByteCount(key));

    /// <summary>The most pieces one <see cref="Need"/> asks for.</summary>
    public const int MostNeeded = 400;

    private static ReadOnlySpan<byte> Mark => "KSMP"u8;

    public enum Kind : byte
    {
        Hello = 1,
        Welcome = 2,
        Roster = 3,
        Bye = 4,
        Ping = 5,
        Pong = 6,
        Clock = 7,
        Chunk = 8,
        Need = 9,
        Got = 10,
        Loaded = 11,
        State = 12,
        Claim = 13,
        Owners = 14,
        Gone = 15,
        Chat = 16,
        Snapshot = 17,
        Rename = 18,
        WarpAsk = 19,
        WarpVote = 20,
        Warp = 21,
        NoWorld = 22,
        Swept = 23,
        Command = 24,
        Op = 25,
        Alive = 26,
        Dock = 27,
    }

    /// <summary>Where a warp has got to: nobody has asked, somebody has and is waiting, or it is running.</summary>
    public enum WarpPhase : byte
    {
        None = 0,
        Asking = 1,
        Running = 2,
    }

    /// <summary>What a server says of itself in a welcome past the plain fields, so the next one of these costs no bytes.</summary>
    [Flags]
    public enum WelcomeFlags : byte
    {
        None = 0,

        /// <summary>A password is wanted: a list of servers says so on the row rather than letting a player find out by being refused.</summary>
        Password = 1,
    }

    public abstract record Message(Kind Kind);

    /// <param name="Mods">Every mod the game has enabled, comma separated: a world holding a part of one it lacks does not load.</param>
    /// <param name="System">The celestial system being played, such as the whole of Sol or Earth and its Moon alone: a world of one is not a world of the other.</param>
    /// <param name="Token">What an admin was given last time, kept by that game and offered again here; empty from anybody else.</param>
    /// <param name="Cookie">What the server last answered this game's hello with, or empty on the
    /// first one. The server will not hold a place for an address until it has said something that
    /// address has sent back, since nothing else about a datagram says where it really came
    /// from.</param>
    public sealed record Hello(string Name, string KsaBuild, string Mods, string System, string Password, string Token, string Cookie = "") : Message(Kind.Hello);

    /// <param name="Id">The number the player is known by from now, or zero with <paramref name="Refused"/> saying why.</param>
    /// <param name="HasWorld">Whether the server has a world to send; without one this player's open world becomes it.</param>
    /// <param name="Cookie">A word to say again in the next hello, when the server has not yet heard
    /// anything from this address that it chose itself. Empty on a welcome that admits or refuses.</param>
    /// <param name="Mods">The mods the server's world wants, comma separated; empty when it asks none.</param>
    /// <param name="Players">How many players are on the server now, and <paramref name="MaxPlayers"/> the most it takes. Both are nothing to a game that has joined, which has a roster; they are what a list of servers is read for.</param>
    /// <param name="KsaBuild">The build this server's world wants, empty when it wants whichever it is given. On a row of a list because a game cannot change its own build without being started again, so this is the one thing a player has to act on before clicking rather than after.</param>
    public sealed record Welcome(byte Id, string Refused, string ServerName, bool HasWorld, string Cookie = "", string System = "", string Mods = "",
                                 byte Players = 0, byte MaxPlayers = 0, WelcomeFlags Flags = WelcomeFlags.None, string KsaBuild = "") : Message(Kind.Welcome);

    /// <param name="Rtt">The player's round trip to the server, s, as that player last measured it; negative until it has.</param>
    /// <param name="Can">The speed that player's game is managing, as a multiple of real time:
    /// what the room's own speed is held to, and whose name is on it.</param>
    public readonly record struct Player(byte Id, string Name, bool Loaded, string Controlling, float Rtt, float Can);

    public sealed record Roster(Player[] Players) : Message(Kind.Roster);

    public sealed record Bye(string Why) : Message(Kind.Bye);

    /// <param name="Rtt">The round trip this game last measured, s, so the server can tell every player what every other player's is. Negative until it has one.</param>
    /// <param name="Late">How far behind the server's time this game's world is, s, as it last measured.</param>
    /// <param name="Can">The speed this game is managing to run its world at, as a multiple of real
    /// time. The server never warps the world past the slowest of these: a warp nobody can follow is
    /// a warp that leaves somebody behind, and no amount of catching up afterwards is worth that.</param>
    /// <param name="Here">Whether this game's window has the player's attention. An unfocused window is
    /// throttled by Windows, so what it manages says nothing about the machine: it is an absent
    /// player, not a slow one, and the world does not slow down for somebody who is not looking.</param>
    /// <param name="Watching">The craft this game's camera is on, which need not be the one it flies:
    /// a craft its owner is neither in nor looking at is one they have walked away from, and after a
    /// while it goes back to being anybody's.</param>
    public sealed record Ping(double Sent, float Rtt, float Late, float Can, bool Here, string Watching) : Message(Kind.Ping);

    /// <summary>
    /// Nothing but "this game is still here", sent from a thread of its own so a frame loop that has
    /// stopped does not read as a player who has left.
    ///
    /// <para>Empty on purpose. A <see cref="Ping"/> carries the clock, the round trip and what is
    /// being flown, all of it worked out on the game's own thread: sending one from somewhere else
    /// would hand the server a measurement that was never taken, and be answered with a
    /// <see cref="Pong"/> whose timings mean nothing. This says the one thing that is still true
    /// while a game is stalled, and the server reads it for that and nothing else.</para>
    /// </summary>
    public sealed record Alive() : Message(Kind.Alive);

    /// <param name="Echo">The ping's own time, on the asker's clock.</param>
    /// <param name="Answered">When it was answered, on the answerer's clock.</param>
    public sealed record Pong(double Echo, double Answered) : Message(Kind.Pong);

    /// <param name="Universe">The server's universe time, s.</param>
    /// <param name="Sent">When it was that, on the server's own clock, s.</param>
    /// <param name="Speed">How fast universe time runs there: 1, or 0 while it is held.</param>
    public sealed record Clock(double Universe, double Sent, float Speed) : Message(Kind.Clock);

    public sealed record Chunk(uint Transfer, ushort Index, ushort Count, byte[] Data) : Message(Kind.Chunk);

    public sealed record Need(uint Transfer, ushort[] Missing) : Message(Kind.Need);

    public sealed record Got(uint Transfer) : Message(Kind.Got);

    /// <param name="Universe">The universe time the world it loaded was at, which starts a server's clock that has none.</param>
    public sealed record Loaded(double Universe) : Message(Kind.Loaded);

    [Flags]
    public enum StateFlags : byte
    {
        None = 0,

        /// <summary>On rails on its owner's game: its orbit says where it is, and nothing need pull it.</summary>
        Rails = 1,

        /// <summary>Touching the ground or the sea, where an orbit says nothing useful.</summary>
        Landed = 2,

        /// <summary>Its engine is lit, so a puppet of it burns rather than coasting silently.</summary>
        EngineOn = 4,
        Lights = 8,

        /// <summary>The craft its owner is flying, sent often; any other it owns is sent seldom.</summary>
        Controlled = 16,

        /// <summary>
        /// Within its owner's physics, said of a craft held only for being there: what keeps the
        /// hold, and what gives the craft up when it is no longer said.
        /// </summary>
        Near = 32,
    }

    /// <summary>
    /// A craft where it is: in its parent body's fixed frame, which a craft on the ground is still in,
    /// and in its inertial frame, which an orbit is set from.
    /// </summary>
    /// <param name="Universe">The universe time it was there.</param>
    /// <param name="Body2Ccf">Its attitude in the body's fixed frame.</param>
    /// <param name="Spin">Its angular velocity in its own frame, rad/s.</param>
    /// <param name="Parts">
    /// How many parts it has, or 0 where that is not said. Where a craft is, is where its centre
    /// is, and a craft that sheds a stage or docks has its centre somewhere else from that moment:
    /// a copy still in the old shape that is put where the new one stands is put metres out.
    /// </param>
    public sealed record State(byte Owner, string Key, uint Sequence, double Universe, string Body,
                               Vec3 PositionCcf, Vec3 VelocityCcf, Vec3 PositionCci, Vec3 VelocityCci,
                               Quat Body2Ccf, Vec3 Spin, float Throttle, StateFlags Flags, ushort Parts = 0) : Message(Kind.State);

    /// <param name="Controlling">Whether the player is flying it now, which nobody may take it from; otherwise only kept, as debris it dropped.</param>
    /// <param name="Made">
    /// Whether this game made the craft rather than finding it in the world everyone loaded. Two games
    /// that each send a kitten out of the same capsule call it what KSA calls it and so make one name
    /// for two craft; the server can only tell that apart from two players reaching for one shared
    /// craft because the game says which it is.
    /// </param>
    /// <param name="Near">
    /// Whether the craft is asked for only because it is within this game's physics. A craft
    /// nobody owns is stepped by every game near it, each in its own way, and is in as many places
    /// as there are games: held by one, it is in one. Such a hold is given up to whoever asks to
    /// fly the craft, and lapses when its holder stops saying the craft is near.
    /// </param>
    public sealed record Claim(string Key, bool Controlling, bool Made, bool Near = false) : Message(Kind.Claim);

    /// <summary>A craft of this game's whose name another player already had, and what it is called from now on.</summary>
    public sealed record Rename(string Was, string Now) : Message(Kind.Rename);

    /// <summary>
    /// A game asking for the shared world to run at this speed, 0 being a pause. One world has one
    /// time, so this is a request put to everyone else rather than a thing a game may simply do.
    /// </summary>
    public sealed record WarpAsk(float Speed) : Message(Kind.WarpAsk);

    /// <summary>A game's answer to what somebody asked for.</summary>
    public sealed record WarpVote(bool Yes) : Message(Kind.WarpVote);

    /// <param name="Who">Whose warp it is: who asked, and who alone may change or stop it.</param>
    /// <param name="Waiting">Players yet to answer, while asking. Empty otherwise.</param>
    public sealed record Warp(byte Who, float Speed, WarpPhase Phase, byte[] Waiting) : Message(Kind.Warp);

    /// <param name="Near">Whether it is held only for being within its holder's physics, and is anybody's to fly.</param>
    public readonly record struct Owner(string Key, byte Id, bool Near = false);

    /// <summary>Who owns which craft, a page at a time: an owner of zero lets a craft go.</summary>
    public sealed record Owners(ushort Page, ushort Pages, Owner[] Entries) : Message(Kind.Owners);

    /// <summary>A craft its owner's game no longer has: destroyed, recovered or boarded.</summary>
    public sealed record Gone(string Key) : Message(Kind.Gone);

    /// <summary>
    /// Two craft that have docked in one game: <paramref name="Consumed"/> was taken into
    /// <paramref name="Survivor"/>, which is the one craft left.
    ///
    /// <para>From a game, it is that game saying so, of a docking its own craft was in. From the
    /// server, it is the world being told: the craft that is left is <paramref name="By"/>'s,
    /// whoever had it, and the other is no longer in the world, whoever had that. A docking is done
    /// in one world and cannot be done in two, so the game that docks first has the craft and the
    /// other player rides in it.</para>
    /// </summary>
    public sealed record Dock(string Consumed, string Survivor, byte By) : Message(Kind.Dock);

    /// <summary>
    /// The operator sweeping craft out of the world: every craft whose name matches, in every game,
    /// bar the ones being flown.
    ///
    /// <para>A name and not a list, because the server cannot name most of what is out there. It
    /// knows a craft once somebody claims it or says how it is built; the twenty one-part fragments
    /// a spent stage breaks into are claimed by nobody, so a list drawn up here would miss exactly
    /// what the operator is trying to sweep. Each game knows its own world and matches the name
    /// against it.</para>
    ///
    /// <para>Not a <see cref="Gone"/> either: that is one game saying what became of a craft of its
    /// own, and a game that owns the craft is right to ignore it. This is the world being told what
    /// is no longer in it, which is nobody's to disagree with.</para>
    /// </summary>
    public sealed record Swept(string Like) : Message(Kind.Swept);

    /// <summary>
    /// Whether a craft's name matches what the operator swept by: empty or "*" is everything, and
    /// * and ? stand for any run and any one character. Both ends match the same way or they sweep
    /// different craft.
    /// </summary>
    public static bool Matches(string key, string like) =>
        like.Length == 0 || like == "*" || Glob(key.AsSpan(), like.AsSpan());

    /// <summary>
    /// The craft a name was shed from, nearest first: KSA names a piece that comes off a stack after
    /// the stack it left, so <c>Rocket_2_5</c> came off <c>Rocket_2</c>, which came off
    /// <c>Rocket</c>. A piece whose name is already taken gets a <c>" 2"</c> on the end instead,
    /// which is the same thing said another way.
    ///
    /// <para>This is how a game tells whose debris a craft is. A world holds craft nobody asked for
    /// -- its copy of another player's rocket sheds pieces of its own as the stack is remade under
    /// it -- and distance cannot tell those from the launch this player just made, because both are
    /// next to somebody. The name can: a piece of another player's rocket says so in its own name.</para>
    /// </summary>
    public static IEnumerable<string> ShedFrom(string key)
    {
        // A name is only ever shortened here, so this ends.
        while (true)
        {
            int cut = key.LastIndexOfAny(['_', ' ']);
            if (cut <= 0 || cut == key.Length - 1) yield break;
            foreach (char c in key.AsSpan()[(cut + 1)..]) if (!char.IsAsciiDigit(c)) yield break;
            key = key[..cut];
            yield return key;
        }
    }

    private static bool Glob(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        while (pattern.Length > 0)
        {
            if (pattern[0] == '*')
            {
                pattern = pattern[1..];
                if (pattern.Length == 0) return true;
                for (int i = 0; i <= text.Length; i++) if (Glob(text[i..], pattern)) return true;
                return false;
            }
            if (text.Length == 0) return false;
            if (pattern[0] != '?' && char.ToLowerInvariant(pattern[0]) != char.ToLowerInvariant(text[0])) return false;
            text = text[1..];
            pattern = pattern[1..];
        }
        return text.Length == 0;
    }

    public sealed record Chat(byte From, string Text) : Message(Kind.Chat);

    /// <summary>A console line typed into a game's chat, which the server runs only for an admin.</summary>
    public sealed record Command(string Line) : Message(Kind.Command);

    /// <param name="Admin">What this player is on this server from now. A game told it is no admin
    /// throws away whatever token it had for that server, so revoking one needs nothing else said.</param>
    /// <param name="Token">A token to keep and offer at the next join, empty when there is nothing new
    /// to keep: an admin who was recognised at the join already has one.</param>
    public sealed record Op(bool Admin, string Token) : Message(Kind.Op);

    /// <summary>The server asking a player for a save of its world, to hand players who join after.</summary>
    public sealed record Snapshot(string Why) : Message(Kind.Snapshot);

    /// <param name="Why">Why this game will not hand its world over, said to everyone.</param>
    /// <remarks>A world KSA cannot load is worse than a world an hour old: a game that has one keeps
    /// it to itself and the server asks somebody else.</remarks>
    public sealed record NoWorld(string Why) : Message(Kind.NoWorld);

    public static int Write(Message message, Span<byte> into)
    {
        Writer w = new(into);
        w.Bytes(Mark);
        w.U16(Version);
        w.U8((byte)message.Kind);
        switch (message)
        {
            case Hello h:
                w.Text(h.Name, NameChars);
                w.Text(h.KsaBuild, 64);
                w.LongText(h.Mods, 4000);
                w.Text(h.System, NameChars);
                w.Text(h.Password, 64);
                w.Text(h.Token, TokenChars);
                w.Text(h.Cookie, CookieChars);
                break;
            case Welcome m:
                w.U8(m.Id);
                w.LongText(m.Refused, RefusedChars);
                w.Text(m.ServerName, 64);
                w.U8(m.HasWorld ? (byte)1 : (byte)0);
                w.Text(m.Cookie, CookieChars);
                w.Text(m.System, NameChars);
                w.LongText(m.Mods, ProbeModsChars);
                w.U8(m.Players);
                w.U8(m.MaxPlayers);
                w.U8((byte)m.Flags);
                w.Text(m.KsaBuild, 64);
                break;
            case Roster r:
                w.U8((byte)Math.Min(r.Players.Length, 64));
                foreach (Player p in r.Players.Take(64))
                {
                    w.U8(p.Id);
                    w.Text(p.Name, NameChars);
                    w.U8(p.Loaded ? (byte)1 : (byte)0);
                    w.Text(p.Controlling, KeyChars);
                    w.F32(p.Rtt);
                    w.F32(p.Can);
                }
                break;
            case Bye b:
                w.Text(b.Why, 200);
                break;
            case Alive:
                break;
            case Ping p:
                w.F64(p.Sent);
                w.F32(p.Rtt);
                w.F32(p.Late);
                w.F32(p.Can);
                w.U8(p.Here ? (byte)1 : (byte)0);
                w.Text(p.Watching, KeyChars);
                break;
            case Pong p:
                w.F64(p.Echo);
                w.F64(p.Answered);
                break;
            case Clock c:
                w.F64(c.Universe);
                w.F64(c.Sent);
                w.F32(c.Speed);
                break;
            case Chunk c:
                w.U32(c.Transfer);
                w.U16(c.Index);
                w.U16(c.Count);
                w.U16((ushort)c.Data.Length);
                w.Bytes(c.Data);
                break;
            case Need n:
                w.U32(n.Transfer);
                w.U16((ushort)Math.Min(n.Missing.Length, MostNeeded));
                foreach (ushort i in n.Missing.Take(MostNeeded)) w.U16(i);
                break;
            case Got g:
                w.U32(g.Transfer);
                break;
            case Loaded l:
                w.F64(l.Universe);
                break;
            case State s:
                w.U8(s.Owner);
                w.Text(s.Key, KeyChars);
                w.U32(s.Sequence);
                w.F64(s.Universe);
                w.Text(s.Body, 64);
                w.V3d(s.PositionCcf);
                w.V3f(s.VelocityCcf);
                w.V3d(s.PositionCci);
                w.V3f(s.VelocityCci);
                w.F32((float)s.Body2Ccf.X);
                w.F32((float)s.Body2Ccf.Y);
                w.F32((float)s.Body2Ccf.Z);
                w.F32((float)s.Body2Ccf.W);
                w.V3f(s.Spin);
                w.F32(s.Throttle);
                w.U8((byte)s.Flags);
                w.U16(s.Parts);
                break;
            case Claim c:
                w.Text(c.Key, KeyChars);
                w.U8(c.Controlling ? (byte)1 : (byte)0);
                w.U8(c.Made ? (byte)1 : (byte)0);
                w.U8(c.Near ? (byte)1 : (byte)0);
                break;
            case Rename n:
                w.Text(n.Was, KeyChars);
                w.Text(n.Now, KeyChars);
                break;
            case WarpAsk a:
                w.F32(a.Speed);
                break;
            case WarpVote v:
                w.U8(v.Yes ? (byte)1 : (byte)0);
                break;
            case Warp warp:
                w.U8(warp.Who);
                w.F32(warp.Speed);
                w.U8((byte)warp.Phase);
                w.U8((byte)Math.Min(warp.Waiting.Length, 64));
                foreach (byte id in warp.Waiting.Take(64)) w.U8(id);
                break;
            case Owners o:
                w.U16(o.Page);
                w.U16(o.Pages);
                w.U8((byte)Math.Min(o.Entries.Length, OwnersPerPage));
                foreach (Owner e in o.Entries.Take(OwnersPerPage))
                {
                    w.Text(e.Key, KeyChars);
                    w.U8(e.Id);
                    w.U8(e.Near ? (byte)1 : (byte)0);
                }
                break;
            case Gone g:
                w.Text(g.Key, KeyChars);
                break;
            case Dock d:
                w.Text(d.Consumed, KeyChars);
                w.Text(d.Survivor, KeyChars);
                w.U8(d.By);
                break;
            case Swept s2:
                w.Text(s2.Like, KeyChars);
                break;
            case Chat c:
                w.U8(c.From);
                w.Text(c.Text, ChatChars);
                break;
            case Command c2:
                w.Text(c2.Line, ChatChars);
                break;
            case Op o:
                w.U8(o.Admin ? (byte)1 : (byte)0);
                w.Text(o.Token, TokenChars);
                break;
            case Snapshot s:
                w.Text(s.Why, 200);
                break;
            case NoWorld n:
                w.Text(n.Why, 200);
                break;
        }
        return w.At;
    }

    /// <summary>
    /// A datagram read, or null for one that is not ours, is of another version, or is cut short:
    /// <paramref name="version"/> says which version it was, for a refusal to name.
    /// </summary>
    public static Message? Read(ReadOnlySpan<byte> data, out ushort version)
    {
        version = 0;
        if (data.Length < 7 || !data[..4].SequenceEqual(Mark)) return null;
        version = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if (version != Version) return null;
        Reader r = new(data[7..]);
        try
        {
            Message m = (Kind)data[6] switch
            {
                Kind.Hello => new Hello(r.Text(NameChars), r.Text(64), r.LongText(4000), r.Text(NameChars), r.Text(64), r.Text(TokenChars), r.Text(CookieChars)),
                Kind.Welcome => new Welcome(r.U8(), r.LongText(RefusedChars), r.Text(64), r.U8() != 0, r.Text(CookieChars), r.Text(NameChars), r.LongText(ProbeModsChars),
                                            r.U8(), r.U8(), (WelcomeFlags)r.U8(), r.Text(64)),
                Kind.Roster => ReadRoster(ref r),
                Kind.Bye => new Bye(r.Text(200)),
                Kind.Alive => new Alive(),
                Kind.Ping => new Ping(r.F64(), r.F32(), r.F32(), r.F32(), r.U8() != 0, r.Text(KeyChars)),
                Kind.Pong => new Pong(r.F64(), r.F64()),
                Kind.Clock => new Clock(r.F64(), r.F64(), r.F32()),
                Kind.Chunk => new Chunk(r.U32(), r.U16(), r.U16(), r.Take(r.U16()).ToArray()),
                Kind.Need => ReadNeed(ref r),
                Kind.Got => new Got(r.U32()),
                Kind.Loaded => new Loaded(r.F64()),
                Kind.State => new State(r.U8(), r.Text(KeyChars), r.U32(), r.F64(), r.Text(64), r.V3d(), r.V3f(), r.V3d(), r.V3f(),
                                        new Quat(r.F32(), r.F32(), r.F32(), r.F32()).Normalized(), r.V3f(), r.F32(), (StateFlags)r.U8(), r.U16()),
                Kind.Claim => new Claim(r.Text(KeyChars), r.U8() != 0, r.U8() != 0, r.U8() != 0),
                Kind.Rename => new Rename(r.Text(KeyChars), r.Text(KeyChars)),
                Kind.WarpAsk => new WarpAsk(r.F32()),
                Kind.WarpVote => new WarpVote(r.U8() != 0),
                Kind.Warp => ReadWarp(ref r),
                Kind.Owners => ReadOwners(ref r),
                Kind.Gone => new Gone(r.Text(KeyChars)),
                Kind.Dock => new Dock(r.Text(KeyChars), r.Text(KeyChars), r.U8()),
                Kind.Swept => new Swept(r.Text(KeyChars)),
                Kind.Chat => new Chat(r.U8(), r.Text(ChatChars)),
                Kind.Command => new Command(r.Text(ChatChars)),
                Kind.Op => new Op(r.U8() != 0, r.Text(TokenChars)),
                Kind.Snapshot => new Snapshot(r.Text(200)),
            Kind.NoWorld => new NoWorld(r.Text(200)),
                _ => throw new FormatException(),
            };
            return m is State s && !Finite(s) ? null : m;
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or FormatException or DecoderFallbackException)
        {
            return null;
        }
    }

    // A state with anything not a number in it would put a craft nowhere.
    private static bool Finite(State s) =>
        double.IsFinite(s.Universe) && s.PositionCcf.IsFinite && s.VelocityCcf.IsFinite && s.PositionCci.IsFinite && s.VelocityCci.IsFinite
        && s.Body2Ccf.IsFinite && s.Spin.IsFinite && float.IsFinite(s.Throttle) && s.Key.Length > 0;

    private static Warp ReadWarp(ref Reader r)
    {
        byte who = r.U8();
        float speed = r.F32();
        WarpPhase phase = (WarpPhase)r.U8();
        byte[] waiting = new byte[Math.Min((int)r.U8(), 64)];
        for (int i = 0; i < waiting.Length; i++) waiting[i] = r.U8();
        return new Warp(who, speed, phase, waiting);
    }

    private static Roster ReadRoster(ref Reader r)
    {
        Player[] players = new Player[r.U8()];
        for (int i = 0; i < players.Length; i++) players[i] = new Player(r.U8(), r.Text(NameChars), r.U8() != 0, r.Text(KeyChars), r.F32(), r.F32());
        return new Roster(players);
    }

    private static Need ReadNeed(ref Reader r)
    {
        uint transfer = r.U32();
        ushort[] missing = new ushort[Math.Min((int)r.U16(), MostNeeded)];
        for (int i = 0; i < missing.Length; i++) missing[i] = r.U16();
        return new Need(transfer, missing);
    }

    private static Owners ReadOwners(ref Reader r)
    {
        ushort page = r.U16(), pages = r.U16();
        Owner[] entries = new Owner[Math.Min((int)r.U8(), OwnersPerPage)];
        for (int i = 0; i < entries.Length; i++) entries[i] = new Owner(r.Text(KeyChars), r.U8(), r.U8() != 0);
        return new Owners(page, pages, entries);
    }

    /// <summary>A name or text cut to so many characters, trimmed, with control characters taken out.</summary>
    public static string Clip(string? s, int chars)
    {
        string t = new((s ?? "").Where(c => !char.IsControl(c)).ToArray());
        t = t.Trim();
        return t.Length > chars ? t[..chars] : t;
    }

    private ref struct Writer(Span<byte> into)
    {
        private readonly Span<byte> _into = into;
        public int At { get; private set; }

        public void Bytes(ReadOnlySpan<byte> b)
        {
            b.CopyTo(_into[At..]);
            At += b.Length;
        }

        public void U8(byte v) => _into[At++] = v;

        public void U16(ushort v)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_into[At..], v);
            At += 2;
        }

        public void U32(uint v)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_into[At..], v);
            At += 4;
        }

        public void F32(float v)
        {
            BinaryPrimitives.WriteSingleLittleEndian(_into[At..], v);
            At += 4;
        }

        public void F64(double v)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(_into[At..], v);
            At += 8;
        }

        // Doubles for a position: a float is a metre out at 16,000 km from a body's centre.
        public void V3d(Vec3 v)
        {
            F64(v.X);
            F64(v.Y);
            F64(v.Z);
        }

        public void V3f(Vec3 v)
        {
            F32((float)v.X);
            F32((float)v.Y);
            F32((float)v.Z);
        }

        public void LongText(string? s, int chars)
        {
            s ??= "";
            if (s.Length > chars) s = s[..chars];
            U16((ushort)Encoding.UTF8.GetByteCount(s));
            At += Encoding.UTF8.GetBytes(s, _into[At..]);
        }

        // A length byte and UTF-8, cut to so many characters and never mid-character.
        public void Text(string? s, int chars)
        {
            s ??= "";
            if (s.Length > chars) s = s[..chars];
            int n = Encoding.UTF8.GetByteCount(s);
            while (n > 255)
            {
                s = s[..^1];
                n = Encoding.UTF8.GetByteCount(s);
            }
            U8((byte)n);
            At += Encoding.UTF8.GetBytes(s, _into[At..]);
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> from)
    {
        private readonly ReadOnlySpan<byte> _from = from;
        private int _at;

        public ReadOnlySpan<byte> Take(int n)
        {
            if (_at + n > _from.Length) throw new ArgumentOutOfRangeException(nameof(n));
            ReadOnlySpan<byte> b = _from.Slice(_at, n);
            _at += n;
            return b;
        }

        public byte U8() => Take(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
        public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
        public Vec3 V3d() => new(F64(), F64(), F64());
        public Vec3 V3f() => new(F32(), F32(), F32());

        public string LongText(int chars)
        {
            string s = Encoding.UTF8.GetString(Take(U16()));
            return s.Length > chars ? s[..chars] : s;
        }

        public string Text(int chars)
        {
            string s = Encoding.UTF8.GetString(Take(U8()));
            return s.Length > chars ? s[..chars] : s;
        }
    }
}
