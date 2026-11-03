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
    /// <summary>Changed whenever what a datagram means changes: a game speaking another version is refused by name.</summary>
    public const ushort Version = 5;

    public const int BufferBytes = 8192;
    public const int NameChars = 24;
    public const int KeyChars = 96;
    public const int ChatChars = 200;

    /// <summary>The most entries one <see cref="Owners"/> carries: 40 keys of 96 characters is under 4 KB.</summary>
    public const int OwnersPerPage = 40;

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
    }

    /// <summary>Where a warp has got to: nobody has asked, somebody has and is waiting, or it is running.</summary>
    public enum WarpPhase : byte
    {
        None = 0,
        Asking = 1,
        Running = 2,
    }

    public abstract record Message(Kind Kind);

    /// <param name="Mods">Every mod the game has enabled, comma separated: a world holding a part of one it lacks does not load.</param>
    /// <param name="System">The celestial system being played, such as the whole of Sol or Earth and its Moon alone: a world of one is not a world of the other.</param>
    public sealed record Hello(string Name, string KsaBuild, string Mods, string System, string Password) : Message(Kind.Hello);

    /// <param name="Id">The number the player is known by from now, or zero with <paramref name="Refused"/> saying why.</param>
    /// <param name="HasWorld">Whether the server has a world to send; without one this player's open world becomes it.</param>
    public sealed record Welcome(byte Id, string Refused, string ServerName, bool HasWorld) : Message(Kind.Welcome);

    /// <param name="Rtt">The player's round trip to the server, s, as that player last measured it; negative until it has.</param>
    public readonly record struct Player(byte Id, string Name, bool Loaded, string Controlling, float Rtt);

    public sealed record Roster(Player[] Players) : Message(Kind.Roster);

    public sealed record Bye(string Why) : Message(Kind.Bye);

    /// <param name="Rtt">The round trip this game last measured, s, so the server can tell every player what every other player's is. Negative until it has one.</param>
    public sealed record Ping(double Sent, float Rtt) : Message(Kind.Ping);

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

        EngineOn = 4,
        Lights = 8,

        /// <summary>The craft its owner is flying, sent often; any other it owns is sent seldom.</summary>
        Controlled = 16,
    }

    /// <summary>
    /// A craft where it is: in its parent body's fixed frame, which a craft on the ground is still in,
    /// and in its inertial frame, which an orbit is set from.
    /// </summary>
    /// <param name="Universe">The universe time it was there.</param>
    /// <param name="Body2Ccf">Its attitude in the body's fixed frame.</param>
    /// <param name="Spin">Its angular velocity in its own frame, rad/s.</param>
    public sealed record State(byte Owner, string Key, uint Sequence, double Universe, string Body,
                               Vec3 PositionCcf, Vec3 VelocityCcf, Vec3 PositionCci, Vec3 VelocityCci,
                               Quat Body2Ccf, Vec3 Spin, float Throttle, StateFlags Flags) : Message(Kind.State);

    /// <param name="Controlling">Whether the player is flying it now, which nobody may take it from; otherwise only kept, as debris it dropped.</param>
    /// <param name="Made">
    /// Whether this game made the craft rather than finding it in the world everyone loaded. Two games
    /// that each send a kitten out of the same capsule call it what KSA calls it and so make one name
    /// for two craft; the server can only tell that apart from two players reaching for one shared
    /// craft because the game says which it is.
    /// </param>
    public sealed record Claim(string Key, bool Controlling, bool Made) : Message(Kind.Claim);

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

    public readonly record struct Owner(string Key, byte Id);

    /// <summary>Who owns which craft, a page at a time: an owner of zero lets a craft go.</summary>
    public sealed record Owners(ushort Page, ushort Pages, Owner[] Entries) : Message(Kind.Owners);

    /// <summary>A craft its owner's game no longer has: destroyed, recovered or boarded.</summary>
    public sealed record Gone(string Key) : Message(Kind.Gone);

    public sealed record Chat(byte From, string Text) : Message(Kind.Chat);

    /// <summary>The server asking a player for a save of its world, to hand players who join after.</summary>
    public sealed record Snapshot(string Why) : Message(Kind.Snapshot);

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
                break;
            case Welcome m:
                w.U8(m.Id);
                w.Text(m.Refused, 200);
                w.Text(m.ServerName, 64);
                w.U8(m.HasWorld ? (byte)1 : (byte)0);
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
                }
                break;
            case Bye b:
                w.Text(b.Why, 200);
                break;
            case Ping p:
                w.F64(p.Sent);
                w.F32(p.Rtt);
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
                break;
            case Claim c:
                w.Text(c.Key, KeyChars);
                w.U8(c.Controlling ? (byte)1 : (byte)0);
                w.U8(c.Made ? (byte)1 : (byte)0);
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
                }
                break;
            case Gone g:
                w.Text(g.Key, KeyChars);
                break;
            case Chat c:
                w.U8(c.From);
                w.Text(c.Text, ChatChars);
                break;
            case Snapshot s:
                w.Text(s.Why, 200);
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
                Kind.Hello => new Hello(r.Text(NameChars), r.Text(64), r.LongText(4000), r.Text(NameChars), r.Text(64)),
                Kind.Welcome => new Welcome(r.U8(), r.Text(200), r.Text(64), r.U8() != 0),
                Kind.Roster => ReadRoster(ref r),
                Kind.Bye => new Bye(r.Text(200)),
                Kind.Ping => new Ping(r.F64(), r.F32()),
                Kind.Pong => new Pong(r.F64(), r.F64()),
                Kind.Clock => new Clock(r.F64(), r.F64(), r.F32()),
                Kind.Chunk => new Chunk(r.U32(), r.U16(), r.U16(), r.Take(r.U16()).ToArray()),
                Kind.Need => ReadNeed(ref r),
                Kind.Got => new Got(r.U32()),
                Kind.Loaded => new Loaded(r.F64()),
                Kind.State => new State(r.U8(), r.Text(KeyChars), r.U32(), r.F64(), r.Text(64), r.V3d(), r.V3f(), r.V3d(), r.V3f(),
                                        new Quat(r.F32(), r.F32(), r.F32(), r.F32()).Normalized(), r.V3f(), r.F32(), (StateFlags)r.U8()),
                Kind.Claim => new Claim(r.Text(KeyChars), r.U8() != 0, r.U8() != 0),
                Kind.Rename => new Rename(r.Text(KeyChars), r.Text(KeyChars)),
                Kind.WarpAsk => new WarpAsk(r.F32()),
                Kind.WarpVote => new WarpVote(r.U8() != 0),
                Kind.Warp => ReadWarp(ref r),
                Kind.Owners => ReadOwners(ref r),
                Kind.Gone => new Gone(r.Text(KeyChars)),
                Kind.Chat => new Chat(r.U8(), r.Text(ChatChars)),
                Kind.Snapshot => new Snapshot(r.Text(200)),
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
        for (int i = 0; i < players.Length; i++) players[i] = new Player(r.U8(), r.Text(NameChars), r.U8() != 0, r.Text(KeyChars), r.F32());
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
        for (int i = 0; i < entries.Length; i++) entries[i] = new Owner(r.Text(KeyChars), r.U8());
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
