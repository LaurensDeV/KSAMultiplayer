namespace KSAMultiplayer;

/// <summary>
/// The server's universe time: a clock it keeps itself, since it runs no world. It is started from the
/// time of the world it hands out, runs at <see cref="Speed"/> while anyone is playing, and holds
/// where it is while nobody is, so a world left overnight is not hours ahead of every save of it.
/// </summary>
public sealed class UniverseClock
{
    private double _base, _baseAt;
    private bool _running;

    public bool Known { get; private set; }

    /// <summary>How fast universe time runs while the clock runs: 1 unless the server is told otherwise.</summary>
    public double Speed { get; private set; } = 1.0;

    public bool Running => Known && _running;

    /// <summary>Sets the clock to a universe time, as of <paramref name="now"/> on the server's own clock.</summary>
    public void Set(double universe, double now)
    {
        (_base, _baseAt, Known) = (universe, now, true);
    }

    /// <summary>
    /// Changes how fast the clock runs from now on, carrying on from where it is.
    ///
    /// <para>From where it is, because the time it reads is worked out from when it was last set:
    /// changing the speed without fixing that in place first would read every second since then at
    /// the new speed, and the clock would jump. Coming out of a warp it jumped backwards, by as much
    /// as the warp had added, and every game was left hundreds of seconds ahead of a server that had
    /// quietly rewound.</para>
    /// </summary>
    /// <summary>
    /// Not known again, so the next world loaded says what time it is. For a world thrown away and
    /// replaced: the time that went with it was the old world's, and holding it would have every
    /// game sprinting to catch a clock the new world has never been anywhere near.
    /// </summary>
    public void Forget() => Known = false;

    public void SetSpeed(double speed, double now)
    {
        if (Known) (_base, _baseAt) = (Now(now), now);
        Speed = speed;
    }

    /// <summary>Lets the clock run or holds it, carrying on from where it is either way.</summary>
    public void Run(bool running, double now)
    {
        if (running == _running) return;
        if (Known) (_base, _baseAt) = (Now(now), now);
        _running = running;
    }

    public double Now(double now) => !Known ? 0.0 : _running ? _base + ((now - _baseAt) * Speed) : _base;

    /// <summary>How fast it runs now: <see cref="Speed"/>, or 0 while held.</summary>
    public double Rate => Running ? Speed : 0.0;
}

/// <summary>
/// A player's world kept to the server's universe time by how fast it is run: a gap of a second or two
/// is closed a tenth faster or slower over a few seconds, and a world that has just loaded an older
/// save is warped forward to catch up.
/// </summary>
public static class ClockFollow
{
    /// <summary>
    /// The latest universe time anybody is believed about, s: about three thousand years, which no
    /// world reaches and every real one is far under.
    ///
    /// <para>Because a universe time arrives from a game and used to be taken on its word, checked
    /// only for being a number at all. A game saying it had loaded at 1e12 set the server's clock
    /// there and every world in the room then chased a time it could never reach; a world snapshot
    /// timed at -1e308 was written to disk as the server's time, and after a restart no world
    /// advanced at all, since <see cref="Speed"/> holds a world that is far ahead.</para>
    /// </summary>
    public const double MostUniverseSeconds = 1e11;

    /// <summary>Whether a universe time can be believed: a real number, not before the start, not absurd.</summary>
    public static bool SaneUniverse(double universe) =>
        double.IsFinite(universe) && universe >= 0.0 && universe <= MostUniverseSeconds;

    /// <summary>How long a small gap is taken to close, s.</summary>
    public const double CloseOverSeconds = 5.0;

    /// <summary>The most a world runs faster or slower to close a small gap, as a share.</summary>
    public const double MostNudge = 0.1;

    /// <summary>Further behind than this, s, a world is warped to catch up rather than nudged.</summary>
    public const double CatchUpAboveSeconds = 3.0;

    /// <summary>
    /// The most a world is warped to catch up while anything of its own is in physics.
    ///
    /// <para>A ceiling worked out from the frame rate was tried here and taken out again. It read a
    /// frame's length against KSA's step cap as though that were spare capacity, and the two are not
    /// the same thing: a game held at 60 Hz by its display measures a frame of 16.7 ms whether it
    /// spent 2 ms of that working or 16, and KSA's own per-frame ceiling is not the step cap but
    /// twice the step before it, which ramps. Worse, its floor of one meant a game running at
    /// exactly its own minimum frame rate was never allowed to catch up at all, and a game below it
    /// lost universe time every second and fell behind for ever: two players sat 200 s behind a
    /// server and stayed there. If a ceiling of this kind is wanted again, the signal to use is
    /// KSA's own <c>_achievedSpeedFraction</c> -- what share of the asked speed the solver is
    /// actually paying for -- and its floor must be above one.</para>
    /// </summary>
    public const double MostCatchUp = 10.0;

    /// <summary>
    /// The most a world is warped to catch up when every craft of its own is on rails or sitting
    /// still. KSA works those out in closed form, so a step of an hour costs what a step of a frame
    /// costs and the whole gap closes in a moment rather than in minutes of everything running fast.
    /// A craft in physics is stepped through, and a step of an hour would be an hour of stepping.
    /// </summary>
    public const double MostCatchUpOnRails = 1000.0;

    /// <summary>
    /// How fast to run a world that is <paramref name="behind"/> seconds behind the server, whose
    /// time runs at <paramref name="serverRate"/>: stopped with it, warped to catch up from far
    /// behind, and held while it is well ahead.
    ///
    /// <para>How far behind it is counts in seconds of its own, not in seconds of the world's: a
    /// world running at 5x is a second late when it is five seconds of universe time behind, and
    /// reading that as five would have it catch up ten times over, overshoot, stop, and judder
    /// between the two for as long as the warp lasted.</para>
    /// </summary>
    /// <param name="mostCatchUp">The most it may be warped by to catch up, which is far more when
    /// nothing of its own is in physics.</param>
    public static double Speed(double behind, double serverRate, double mostCatchUp = MostCatchUp)
    {
        if (!(serverRate > 0.0) || !double.IsFinite(behind)) return 0.0;
        double late = behind / serverRate;
        if (late > CatchUpAboveSeconds) return serverRate * Math.Min(mostCatchUp, 1.0 + (late / 2.0));
        if (late < -CatchUpAboveSeconds) return 0.0;
        return serverRate * Math.Clamp(1.0 + (late / CloseOverSeconds), 1.0 - MostNudge, 1.0 + MostNudge);
    }

    /// <summary>
    /// The speed to ask of KSA to run at <paramref name="speed"/> when a frame took
    /// <paramref name="frameSeconds"/>: KSA steps a world by no more than <paramref name="capSeconds"/>
    /// a frame, and a slower frame loses the rest unless the speed makes it up.
    /// </summary>
    public static double Asked(double speed, double frameSeconds, double capSeconds)
    {
        double lost = capSeconds > 0.0 && frameSeconds > capSeconds ? Math.Min(frameSeconds / capSeconds, 2.0) : 1.0;
        // The ceiling carries the make-up with it. Without that, a world asking for about its own
        // speed had the make-up clamped away again -- so a game at 20 frames a second needed 1.5x
        // to hold station, was allowed 1.1, and lost sixteen seconds of universe time a minute. It
        // went unnoticed while any gap over three seconds pushed the asked speed to ten and took the
        // ceiling up with it.
        return Math.Clamp(speed * lost, 0.0, Math.Max((1.0 + MostNudge) * lost, speed));
    }

    /// <summary>The server's universe time now, from its last word carried on at its rate for as long as has passed on its clock.</summary>
    public static double ServerNow(double said, double saidAt, double rate, double serverClockNow) =>
        said + (Math.Max(0.0, serverClockNow - saidAt) * rate);
}

/// <summary>
/// Another player's craft as the states it has sent, by the universe time each was at: every world
/// keeps the same universe time, so a craft is drawn a little in the past, between the two states
/// either side of then, and carried on by its velocity past the newest for a moment.
/// </summary>
public sealed class Track
{
    /// <summary>How far in the past a craft is drawn, s: enough for the next state to have come at 20 a second with some lost.</summary>
    public const double DelaySeconds = 0.15;

    /// <summary>The longest a craft is carried on past its newest state, s.</summary>
    public const double MostAheadSeconds = 1.0;

    private const int Kept = 32;

    private readonly List<Wire.State> _states = [];

    public Wire.State? Newest => _states.Count > 0 ? _states[^1] : null;

    /// <summary>The universe time of the newest state.</summary>
    public double HeardAt => _states.Count > 0 ? _states[^1].Universe : double.NegativeInfinity;

    public void Add(Wire.State s)
    {
        if (_states.Count > 0 && s.Universe <= _states[^1].Universe)
        {
            // Out of order, or a world that went back in time on a reload: kept only if it is not
            // already there. Walked rather than asked with a lambda, which boxes the list's
            // enumerator and makes a delegate for every state heard, twenty a second per craft.
            for (int k = _states.Count - 1; k >= 0; k--)
            {
                if (_states[k].Universe == s.Universe) return;
            }
            if (s.Universe < _states[0].Universe - 10.0) _states.Clear();
        }
        int at = _states.Count;
        while (at > 0 && _states[at - 1].Universe >= s.Universe) at--;
        // Older than everything there is room for. Dropped where it is seen, rather than inserted
        // at the front and then taken off the front again as the oldest, which is what dropping the
        // oldest comes to when the newcomer is it.
        if (at == 0 && _states.Count >= Kept) return;
        _states.Insert(at, s);
        if (_states.Count > Kept) _states.RemoveAt(0);
    }

    public void Clear() => _states.Clear();

    /// <summary>Where the craft is drawn at a universe time, or null with nothing heard.</summary>
    public Wire.State? At(double universe)
    {
        if (_states.Count == 0) return null;
        double t = universe - DelaySeconds;
        Wire.State newest = _states[^1];
        if (t >= newest.Universe)
        {
            double ahead = Math.Min(t - newest.Universe, MostAheadSeconds);
            // Moving as it was last seen to be changing, not as it was. The mod writes this velocity
            // into the craft outright, so a craft past its newest state was commanded at the speed it
            // had up to a second ago -- four metres a second out in freefall. Taken from the two
            // newest states, and never carried further forward than the interval they span: a craft
            // that stages or throttles changes its acceleration all at once, and a guess from before
            // that would run away.
            (Vec3 velocityCcf, Vec3 velocityCci) = (newest.VelocityCcf, newest.VelocityCci);
            if (_states.Count > 1 && _states[^2].Body == newest.Body)
            {
                double span = newest.Universe - _states[^2].Universe;
                if (span > 1e-6)
                {
                    double k = Math.Min(ahead / span, 1.0);
                    velocityCcf += (newest.VelocityCcf - _states[^2].VelocityCcf) * k;
                    velocityCci += (newest.VelocityCci - _states[^2].VelocityCci) * k;
                }
            }
            // Turned as well as moved. Only the two positions used to be carried on, so a craft past
            // its newest state was drawn at a frozen attitude while the mod wrote it the matching
            // angular velocity -- a spent stage held still mid-tumble and then stepped most of the
            // turn at once, which is the pop. Spin is in the body's own frame, which is the frame
            // this turn is in, and it is already in every datagram.
            return newest with
            {
                Universe = newest.Universe + ahead,
                PositionCcf = newest.PositionCcf + (newest.VelocityCcf * ahead),
                PositionCci = newest.PositionCci + (newest.VelocityCci * ahead),
                VelocityCcf = velocityCcf,
                VelocityCci = velocityCci,
                Body2Ccf = (newest.Body2Ccf * Quat.Turn(newest.Spin, ahead)).Normalized(),
            };
        }
        if (t <= _states[0].Universe) return _states[0];
        // The two either side of the time wanted. The guards above put it between the oldest and
        // the newest, so there is one of each.
        int i = _states.Count - 1;
        while (i > 0 && _states[i].Universe > t) i--;
        Wire.State a = _states[i], b = _states[i + 1];
        // Across a change of parent body the two are in different frames.
        if (a.Body != b.Body) return b;
        double f = (t - a.Universe) / (b.Universe - a.Universe);
        return b with
        {
            Universe = t,
            PositionCcf = a.PositionCcf + ((b.PositionCcf - a.PositionCcf) * f),
            VelocityCcf = a.VelocityCcf + ((b.VelocityCcf - a.VelocityCcf) * f),
            PositionCci = a.PositionCci + ((b.PositionCci - a.PositionCci) * f),
            VelocityCci = a.VelocityCci + ((b.VelocityCci - a.VelocityCci) * f),
            Body2Ccf = Quat.Slerp(a.Body2Ccf, b.Body2Ccf, f),
        };
    }
}
