using System.Net;

namespace KSAMultiplayer;

/// <summary>
/// A game's end of a server: asking in, keeping its clock against the server's, sending its own
/// craft and hearing everyone else's, and the transfers both ways. Nothing of KSA is here, so a test
/// can run a server and several of these on one machine.
///
/// <para>What the game has to act on is queued: worlds and craft that have arrived, craft that are
/// gone, chat, and the server asking for a save. It is drained once a frame by whoever runs the game.</para>
/// </summary>
public sealed class Client : IDisposable
{
    private const double HelloEverySeconds = 0.5, JoinWithinSeconds = 15.0;

    // How often a game says again that it is in the world, until the server's roster shows it knows.
    private const double LoadedEverySeconds = 2.0;
    private const double PingEverySeconds = 1.0, SilentSeconds = 10.0;

    /// <summary>
    /// How quiet this end has to have gone before the keepalive thread speaks for it, s, and how
    /// often it does. Twice the gap between pings: while the frame loop is running it is never
    /// reached, and a game that has stopped still gets four or five words in before the server's ten
    /// seconds are up, which is enough for some of them to be lost.
    /// </summary>
    private const double AliveAfterSeconds = 2.0;

    /// <summary>
    /// How long a stalled frame loop is spoken for, s, before this end stops holding the place open.
    ///
    /// <para>A stutter is seconds and a world loading is tens of them; a minute of nothing is a game
    /// that is not coming back, and its thread would otherwise hold a seat and a rocket in the world
    /// for as long as the process existed. Past this the server's own silence takes over and the
    /// session is let go of, which is what used to happen the moment a frame ran long.</para>
    /// </summary>
    private const double AliveWhileStalledSeconds = 60.0;

    // A pong slower than this says nothing of the clocks: a game loading a world stands still for seconds with one on its way.
    private const double ClockRttSeconds = 0.5;

    /// <summary>
    /// How long the clocks' difference may go unmeasured, s, before any answer is taken as the best
    /// there is going to be.
    ///
    /// <para>The quickest answer says the difference best, so a slower one is passed over -- but the
    /// quickest ever seen is not a bar a line that has got slower can clear. One lucky packet used to
    /// close the window for the rest of the session: on a link whose usual round trip is more than
    /// half again its quickest, every later answer was refused, and the difference went on driving
    /// this world's clock while drifting with the two machines' crystals. A line that changes
    /// mid-session -- moving between access points, a new route, a loaded server -- did the same with
    /// no way back short of reconnecting.</para>
    /// </summary>
    private const double OffsetStaleSeconds = 20.0;

    // How much of each new measure of the server's rate is taken, while the server says its speed
    // is what it was.
    private const double RateSmoothing = 0.3;

    private readonly Udp _udp;
    private readonly Func<double> _clock;
    private readonly Link _link = new();
    private readonly Dictionary<string, byte> _owners = [];
    private readonly Dictionary<string, Track> _tracks = [];
    private Dictionary<string, byte> _ownersComing = [];

    // The page of the round being gathered, or -1 for no round: a round whose pages do not arrive in
    // order is dropped rather than mixed with the one before it.
    private int _ownersWant = -1;
    private IPEndPoint? _server;
    private Wire.Hello? _hello;
    private double _helloNext, _joinUntil, _pingNext, _heardAt;
    private double _bestRtt = double.PositiveInfinity;
    private double _offsetAt = double.NegativeInfinity;

    // The quickest round trip is what the clock is set by; the latest is what a player is shown,
    // since a scoreboard wants the line as it is now, not at its best since joining.
    private double _lastRtt = double.NaN;

    // Read and written by both the frame loop and the keepalive thread. Plain doubles: 64 bits are
    // written whole on the runtimes this is built for, and the worst a stale read costs is one
    // keepalive that was not needed.
    private double _sentAt, _tickedAt;
    private readonly Thread _alive;
    private volatile bool _stopped;
    private (double Universe, double Sent, double Rate)? _serverClock;
    private double _serverRate;
    private double? _loadedSaid;
    private double _loadedNext;
    private uint _sequence;

    public Client(Func<double> clock, int port = 0)
    {
        _clock = clock;
        _udp = new Udp(port, clock, "KSAMultiplayer client");
        (_sentAt, _tickedAt) = (clock(), clock());
        _alive = new Thread(KeepAlive) { IsBackground = true, Name = "KSAMultiplayer keepalive" };
        _alive.Start();
    }

    /// <summary>
    /// Says this game is still here while its frame loop is not saying anything.
    ///
    /// <para>Everything else this client sends goes out of <see cref="Update"/>, on the game's own
    /// thread, so a game that stalls -- a rocket being rebuilt, a scene loading, a machine thrashing
    /// -- sends nothing at all, and ten seconds of that is a player the server drops. The stall is
    /// one-sided: datagrams coming the other way are stamped by the reader thread and found waiting
    /// afterwards, so this end never gave up on the server. Only the server gave up on this end.</para>
    ///
    /// <para>Sleeps in quarters of a second and does its deciding by the clock it was given, so a
    /// thread that oversleeps still speaks at the right moment rather than counting its own naps.</para>
    /// </summary>
    private void KeepAlive()
    {
        while (!_stopped)
        {
            Thread.Sleep(250);
            if (_stopped || !Joined || _server is null) continue;
            double now = _clock();
            // Nothing to do while the frame loop is sending for itself, and nothing worth doing once
            // it has been gone long enough that this is holding a seat for a game that has died.
            if (now - _sentAt < AliveAfterSeconds || now - _tickedAt > AliveWhileStalledSeconds) continue;
            Send(new Wire.Alive());
        }
    }

    public byte Id { get; private set; }
    public bool Joined => Id != 0;
    public string ServerName { get; private set; } = "";

    /// <summary>Whether the server had a world when this player was let in; without one, this player's world becomes it.</summary>
    public bool ServerHasWorld { get; private set; }

    /// <summary>What a server said of itself to <see cref="Probe"/>, or null until one has.</summary>
    public ServerFacts? Probed { get; private set; }

    private bool _probing;

    /// <summary>Why this player is out, once it is: refused, never answered, or the server gone.</summary>
    public string Out { get; private set; } = "";

    public Wire.Player[] Players { get; private set; } = [];
    public IReadOnlyDictionary<string, byte> Owners => _owners;

    /// <summary>
    /// How far behind the server's time this game's world is, s, as whoever runs the world measures
    /// it. Told to the server with every ping: a server that runs on regardless leaves whoever
    /// cannot keep up thousands of seconds adrift with no way back.
    /// </summary>
    public double Late { get; set; }

    /// <summary>
    /// The speed this game is managing to run its world at, as a multiple of real time, as whoever
    /// runs the world measures it. The server warps no faster than the slowest of these.
    /// </summary>
    /// <para>Zero until this game has measured itself, which the room is not held to.</para>
    public double Can { get; set; }

    /// <summary>Whether this game's window has the player's attention; an unfocused one is throttled.</summary>
    public bool Here { get; set; } = true;

    /// <summary>The craft this game's camera is on, by the name the world knows it by, or empty.</summary>
    public string Watching { get; set; } = "";

    /// <summary>The server's clock less this game's, s, once a ping has been answered.</summary>
    public double ServerOffset { get; private set; }

    public bool ClockKnown => double.IsFinite(_bestRtt);

    /// <summary>The quickest round trip to the server, s.</summary>
    public double Rtt => _bestRtt;

    /// <summary>The round trip last measured, s, or NaN before any: what this player's ping is now.</summary>
    public double Ping => _lastRtt;

    public double? Arriving => _link.Arriving;
    public bool Sending => _link.Busy > 0;

    public Queue<Blob> Blobs { get; } = new();
    public Queue<string> Gone { get; } = new();

    /// <summary>Names the operator has swept the world by: this game takes away every craft of its own that matches.</summary>
    public Queue<string> Swept { get; } = new();

    /// <summary>Craft of this game the server has given another name, because a player already had that one.</summary>
    public Queue<(string Was, string Now)> Renamed { get; } = new();

    /// <summary>What the shared world's speed is doing: who asked, how fast, and whether it is being answered or running.</summary>
    public Wire.Warp? Warp { get; private set; }

    /// <summary>Whether this game has been asked to answer a warp and has not yet.</summary>
    public bool WarpWantsAnswer => Warp is { Phase: Wire.WarpPhase.Asking } w && w.Waiting.Contains(Id);
    /// <param name="FromServer">Said by the server itself rather than by a player, so that a game can
    /// tell an answer to a command from conversation without reading the name.</param>
    public readonly record struct ChatLine(bool FromServer, string Who, string Text);

    public Queue<ChatLine> Chat { get; } = new();

    /// <summary>Whether this server runs what this game asks of it.</summary>
    public bool Admin { get; private set; }

    /// <summary>
    /// What to write down for this server: a token to keep and offer at every join after, or empty to
    /// throw away the one kept, which is how admin is taken back from a machine rather than a name.
    /// </summary>
    public Queue<string> Tokens { get; } = new();
    public Queue<string> Said { get; } = new();

    /// <summary>Set when the server asks for a save of this world; cleared by whoever answers it.</summary>
    public string? SnapshotWanted { get; set; }

    public Udp Socket => _udp;

    /// <summary>The server's universe time now and how fast it runs, once it has said and the clocks are measured.</summary>
    public (double Universe, double Rate)? ServerUniverse =>
        _serverClock is { } c && ClockKnown ? (ClockFollow.ServerNow(c.Universe, c.Sent, _serverRate, _clock() + ServerOffset), _serverRate) : null;

    public byte OwnerOf(string key) => _owners.GetValueOrDefault(key);
    public bool Owns(string key) => Joined && OwnerOf(key) == Id;
    public string NameOf(byte id) => Players.FirstOrDefault(p => p.Id == id).Name ?? $"player {id}";

    public Track TrackOf(string key)
    {
        if (!_tracks.TryGetValue(key, out Track? t)) _tracks[key] = t = new Track();
        return t;
    }

    public void ForgetTrack(string key) => _tracks.Remove(key);

    /// <summary>Forgets every craft's states: for a game whose world, and with it its universe time, has just been replaced.</summary>
    public void ForgetTracks() => _tracks.Clear();

    public IEnumerable<string> Tracked => _tracks.Keys;

    /// <param name="token">What this server gave this machine when it was opped, to be an admin again without being asked.</param>
    public void Connect(IPEndPoint server, string name, string ksaBuild, string mods, string system, string password, string token = "")
    {
        (_server, Id, Out, ServerName, ServerHasWorld) = (server, 0, "", "", false);
        Admin = false;
        _hello = new Wire.Hello(Wire.Clip(name, Wire.NameChars), ksaBuild, mods, system, password, Wire.Clip(token, Wire.TokenChars));
        (_helloNext, _joinUntil, _heardAt) = (0.0, _clock() + JoinWithinSeconds, _clock());
        (_bestRtt, _lastRtt, ServerOffset, _serverClock, _serverRate) = (double.PositiveInfinity, double.NaN, 0.0, null, 0.0);
        _saidGone.Clear();
        _loadedSaid = null;
        _offsetAt = double.NegativeInfinity;
        Warp = null;
        _owners.Clear();
        (_ownersComing, _ownersWant) = ([], -1);
        _tracks.Clear();
        Players = [];
        (_probing, Probed) = (false, null);
    }

    /// <summary>
    /// Asks a server what world it holds without joining it: the hello carries no cookie, so the
    /// answer is the one the server gives anybody, and no place is held on its roster.
    /// </summary>
    public void Probe(IPEndPoint server)
    {
        Connect(server, "", "", "", "", "");
        _probing = true;
    }

    public void Send(Wire.Message m)
    {
        _sentAt = _clock();
        _udp.Send(m, _server);
    }

    /// <summary>One of this game's own craft where it is; the owner and sequence are filled in here.</summary>
    public void SendState(Wire.State s)
    {
        if (!Joined) return;
        Send(s with { Owner = Id, Sequence = ++_sequence });
    }

    /// <param name="made">Whether this game made the craft, rather than finding it in the world everyone loaded.</param>
    public void Claim(string key, bool controlling, bool made = false)
    {
        if (!Joined || key.Length == 0) return;
        // Asked for again, so whatever was said gone under this name is not this craft.
        _saidGone.Remove(key);
        Send(new Wire.Claim(key, controlling, made));
    }

    public void SayGone(string key)
    {
        if (!Joined) return;
        Send(new Wire.Gone(key));
        _owners.Remove(key);
        _saidGone[key] = _clock();
    }

    // What this game has said is gone, and when. Said again for as long as the roster goes on
    // giving the craft to this player, which is the server never having heard: lost on the way, a
    // craft that had gone stayed in every world but its owner's.
    private readonly Dictionary<string, double> _saidGone = [];
    private const double SaidGoneForSeconds = 10.0;

    private void SayGoneAgain(double now)
    {
        if (_saidGone.Count == 0) return;
        foreach ((string key, double at) in _saidGone.ToArray())
        {
            if (now - at > SaidGoneForSeconds) _saidGone.Remove(key);
            else if (OwnerOf(key) == Id)
            {
                Send(new Wire.Gone(key));
                _owners.Remove(key);
            }
        }
    }

    public void Upload(Blob blob)
    {
        if (Joined) _link.Send(blob.Encode(), _clock());
    }

    /// <summary>
    /// Tells the server this game is in the world, and goes on telling it until the roster says so.
    ///
    /// <para>Said once, it is one datagram between a player and everything: lost, the game is in a
    /// world the server does not count it in, sent nothing and asked nothing, with no word of why.</para>
    /// </summary>
    public void SayLoaded(double universe)
    {
        if (!Joined) return;
        (_loadedSaid, _loadedNext) = (universe, _clock() + LoadedEverySeconds);
        Send(new Wire.Loaded(universe));
    }

    /// <summary>Asks for the shared world to run at this speed, 0 being a pause. Everyone has to agree.</summary>
    public void AskWarp(double speed)
    {
        if (Joined) Send(new Wire.WarpAsk((float)speed));
    }

    /// <summary>Tell the server this game's world is not fit to hand out, so it asks somebody else.</summary>
    public void SayNoWorld(string why)
    {
        if (Joined) Send(new Wire.NoWorld(why));
    }

    /// <summary>Answers what somebody asked for.</summary>
    public void VoteWarp(bool yes)
    {
        if (Joined) Send(new Wire.WarpVote(yes));
    }

    public void SayChat(string text)
    {
        string t = Wire.Clip(text, Wire.ChatChars);
        if (Joined && t.Length > 0) Send(new Wire.Chat(Id, t));
    }

    /// <summary>A console line for the server to run, which it does only for an admin and answers either way.</summary>
    public void Ask(string line)
    {
        string t = Wire.Clip(line, Wire.ChatChars).Trim();
        if (Joined && t.Length > 0) Send(new Wire.Command(t));
    }

    /// <summary>Handles what has arrived and sends what is due. Once a frame.</summary>
    public void Update()
    {
        double now = _clock();
        // Stamped first, before anything here can throw: this is "the frame loop is running", and a
        // frame that went wrong part way through is still a game somebody is sitting in front of.
        _tickedAt = now;
        int handled = 0;
        while (handled++ < 4096 && _udp.TryReceive(out (byte[] Data, IPEndPoint From, double Arrived) d))
        {
            if (!d.From.Equals(_server)) continue;
            if (Wire.Read(d.Data, out ushort version) is not { } m)
            {
                if (version != 0 && version != Wire.Version && !Joined) Leave($"the server speaks version {version} and this mod {Wire.Version}: update one of them");
                continue;
            }
            Handle(m, d.Arrived);
        }
        if (_server is null || Out.Length > 0) return;

        if (!Joined)
        {
            if (now > _joinUntil)
            {
                Leave("the server never answered: check the address, and that the server is running and its UDP port open");
                return;
            }
            if (now >= _helloNext && _hello is not null)
            {
                Send(_hello);
                _helloNext = now + HelloEverySeconds;
            }
            return;
        }
        if (now >= _pingNext)
        {
            Send(new Wire.Ping(now, double.IsFinite(_lastRtt) ? (float)_lastRtt : -1f,
                               double.IsFinite(Late) ? (float)Late : 0f,
                               double.IsFinite(Can) ? (float)Math.Max(Can, 0.0) : 0f, Here, Watching));
            _pingNext = now + PingEverySeconds;
        }
        SayGoneAgain(now);
        if (_loadedSaid is { } said && now >= _loadedNext)
        {
            if (Players.Any(p => p.Id == Id && p.Loaded)) _loadedSaid = null;
            else
            {
                Send(new Wire.Loaded(said));
                _loadedNext = now + LoadedEverySeconds;
            }
        }
        _link.Pump(now, Send);
        if (now - _heardAt > SilentSeconds) Leave("the server has not been heard from for ten seconds");
    }

    private void Leave(string why)
    {
        Out = why;
        Id = 0;
        Said.Enqueue(why);
    }

    private void Handle(Wire.Message message, double arrived)
    {
        _heardAt = arrived;
        switch (message)
        {
            case Wire.Welcome w when _probing:
                Probed = ServerFacts.Of(w);
                (_probing, _server) = (false, null);
                return;
            case Wire.Welcome w when !Joined:
                if (w.Id == 0 && w.Refused.Length == 0 && w.Cookie.Length > 0)
                {
                    // Not an answer yet: the server has said a word it chose and wants to hear it
                    // back before it holds a place for this address. Said again at once, so joining
                    // costs one more datagram each way and no waiting.
                    if (_hello is not null) _hello = _hello with { Cookie = w.Cookie };
                    _helloNext = 0.0;
                    return;
                }
                if (w.Id == 0)
                {
                    Leave($"refused: {w.Refused}");
                    _server = null;
                    return;
                }
                (Id, ServerName, ServerHasWorld, _pingNext) = (w.Id, w.ServerName, w.HasWorld, 0.0);
                Said.Enqueue($"joined {(w.ServerName.Length > 0 ? w.ServerName : "the server")} as player {w.Id}");
                return;
            case Wire.Roster r when Joined:
                foreach (Wire.Player p in r.Players.Where(p => p.Id != Id && Players.All(q => q.Id != p.Id))) Said.Enqueue($"{p.Name} is here");
                foreach (Wire.Player p in Players.Where(p => p.Id != Id && r.Players.All(q => q.Id != p.Id))) Said.Enqueue($"{p.Name} has left");
                Players = r.Players;
                return;
            case Wire.Owners o when Joined:
                // Whole rounds only. A round carries every craft anybody owns, so one whose pages
                // do not all arrive says nothing, and the last whole answer is better than a mixture
                // of two. Gathering it on top of the round before left a craft owned by whoever held
                // it last -- so one somebody had just let go of still read as theirs, and this game
                // would not fly its own craft and said another player was flying it, every frame
                // until the next round. Dropping the round instead of committing what came is also
                // why the gathering is emptied here: committing a part of a round would leave the
                // map missing craft, which is two players on one hull.
                if (o.Page == 0) (_ownersComing, _ownersWant) = ([], 0);
                if (o.Page != _ownersWant)
                {
                    _ownersWant = -1;
                    return;
                }
                foreach (Wire.Owner e in o.Entries) _ownersComing[e.Key] = e.Id;
                _ownersWant++;
                if (o.Page + 1 >= o.Pages)
                {
                    _owners.Clear();
                    foreach ((string key, byte id) in _ownersComing) if (id != 0) _owners[key] = id;
                    (_ownersComing, _ownersWant) = ([], -1);
                }
                return;
            case Wire.State s when Joined && s.Owner != Id:
                TrackOf(s.Key).Add(s);
                return;
            case Wire.Pong pong:
                double rtt = arrived - pong.Echo;
                if (rtt >= 0.0 && rtt <= ClockRttSeconds) _lastRtt = rtt;
                // The quickest answer is the one least held up on the way, so it says the clocks' difference best.
                bool stale = arrived - _offsetAt > OffsetStaleSeconds;
                if (rtt >= 0.0 && rtt <= ClockRttSeconds && (rtt <= _bestRtt * 1.5 || stale))
                {
                    // Taken as the quickest there is when nothing has been accepted for a while, so
                    // the bar follows a line that has got slower instead of locking it out.
                    _bestRtt = stale ? rtt : Math.Min(rtt, _bestRtt);
                    ServerOffset = pong.Answered + (0.5 * rtt) - arrived;
                    _offsetAt = arrived;
                }
                return;
            case Wire.Clock c when Joined:
                if (c.Speed <= 0.0) _serverRate = 0.0;
                // Only while the speed it says is the speed it said. A warp is the server changing
                // its speed a thousandfold between two words, and a rate eased towards that at
                // three tenths a word is a game still running at 240x a second after the warp
                // ended -- most of the overshoot that stood every game still, on a wire with no
                // delay in it at all -- and one a third of a second behind every step of a climb.
                else if (_serverClock is { } last && c.Sent - last.Sent > 0.05 && last.Rate > 0.0
                         && Math.Abs(c.Speed - last.Rate) <= 0.001 * c.Speed)
                {
                    double rate = Math.Clamp((c.Universe - last.Universe) / (c.Sent - last.Sent), 0.0, 2.0 * c.Speed);
                    _serverRate = _serverRate > 0.0 ? _serverRate + (RateSmoothing * (rate - _serverRate)) : rate;
                }
                else _serverRate = c.Speed;
                _serverClock = (c.Universe, c.Sent, c.Speed);
                return;
            case Wire.Warp warp when Joined:
                Warp = warp.Phase == Wire.WarpPhase.None ? null : warp;
                return;
            case Wire.Rename n when Joined && n.Was.Length > 0 && n.Now.Length > 0:
                // Everything held under the old name follows it, so nothing of this craft is lost.
                if (_tracks.Remove(n.Was, out Track? moved)) _tracks[n.Now] = moved;
                Renamed.Enqueue((n.Was, n.Now));
                return;
            case Wire.Gone g when Joined:
                _owners.Remove(g.Key);
                _ownersComing.Remove(g.Key);
                _tracks.Remove(g.Key);
                Gone.Enqueue(g.Key);
                return;
            case Wire.Swept sw when Joined:
                foreach (string key in _owners.Keys.Where(k => Wire.Matches(k, sw.Like)).ToArray())
                {
                    _owners.Remove(key);
                    _tracks.Remove(key);
                }
                foreach (string key in _ownersComing.Keys.Where(k => Wire.Matches(k, sw.Like)).ToArray())
                {
                    _ownersComing.Remove(key);
                }
                Swept.Enqueue(sw.Like);
                return;
            case Wire.Chat c when Joined:
                Chat.Enqueue(new ChatLine(c.From == 0, c.From == 0 ? "server" : NameOf(c.From), c.Text));
                return;
            case Wire.Op op when Joined:
                Admin = op.Admin;
                // Nothing to write down for an admin the server recognised at the join: that token is
                // already here, and it is the only copy there is.
                if (!op.Admin) Tokens.Enqueue("");
                else if (op.Token.Length > 0) Tokens.Enqueue(op.Token);
                return;
            case Wire.Snapshot s when Joined:
                SnapshotWanted = s.Why;
                return;
            case Wire.Bye b:
                Leave(b.Why.Length > 0 ? $"the server closed the connection: {b.Why}" : "the server closed the connection");
                return;
            case Wire.Chunk or Wire.Need or Wire.Got when Joined:
                if (_link.Handle(message, arrived, Send) is { } bytes && Blob.Decode(bytes) is { } blob)
                {
                    // A world on its way in is a world this game is not in yet, whatever it said of the last.
                    if (blob.Kind == BlobKind.World) _loadedSaid = null;
                    Blobs.Enqueue(blob);
                }
                return;
        }
    }

    /// <summary>Says goodbye and closes the socket.</summary>
    public void Dispose()
    {
        _stopped = true;
        if (Joined) Send(new Wire.Bye("left"));
        Id = 0;
        // Before the socket goes, so the keepalive is not part way through sending down one that has
        // been disposed. A quarter of a second is its whole cycle; the wait is generous to a thread
        // the scheduler has not got round to.
        _alive.Join(TimeSpan.FromSeconds(1));
        _udp.Dispose();
    }
}
