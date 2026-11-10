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
/// Another player's craft as the states it has sent, by the universe time each was at. Every world
/// keeps the same universe time, so a craft is drawn where it is at this world's own: carried on
/// from its newest state by the speed it had and the way that speed was changing, or between two
/// states where this world is behind them.
///
/// <para>Not a little in the past, which is how a thing that walks is drawn smoothly and how this
/// was drawn until it was measured. A craft in low orbit covers 1,083 m in 150 ms, and a game steps
/// another player's craft itself whenever nothing is pushing it, to the millimetre and at the
/// present. So a craft coasting stood where it was and a craft burning stood where it had been: it
/// was put back a kilometre the instant its engine lit, a kitten out of a hatch appeared a
/// kilometre behind the capsule it had left, and nothing two players flew was ever beside anything
/// else in orbit. What drawing in the present costs is a guess a round trip long, which the wire
/// simulation in <c>tests/TrackTests.cs</c> puts at millimetres for anything not turning sharply
/// and one step of a few tens of centimetres when an engine cuts.</para>
/// </summary>
public sealed class Track
{
    /// <summary>The longest a craft is carried on past its newest state, s.</summary>
    public const double MostAheadSeconds = 1.0;

    /// <summary>How long past its newest state a craft is taken to go on changing speed as it was, s, where its states come closer together than this.</summary>
    public const double CarrySeconds = 0.25;

    /// <summary>
    /// How far back a craft's states are kept, s, and the most of them.
    ///
    /// <para>By how long, because a world may be asked where a craft was. One that has just joined
    /// is two or three seconds behind the world it joined and gives a tenth of a second of that back
    /// every second, so for half a minute it asks about a time that far before the newest state.
    /// Thirty-two states were kept, which at twenty a second is a second and a half: a world further
    /// behind than that was answered with the oldest state there was, up to a second and more ahead
    /// of the time it had asked about, and told the craft was there -- eight kilometres away in low
    /// orbit, every step, until it had caught up. This is past what a nudge closes; beyond that a
    /// world warps to catch up and is behind for moments.</para>
    /// </summary>
    public const double KeptSeconds = 4.0;

    private const int MostKept = 512;

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
        // Older than everything that is kept. Dropped where it is seen, rather than inserted at the
        // front and then taken off the front again as the oldest, which is what dropping the oldest
        // comes to when the newcomer is it.
        if (at == 0 && _states.Count > 0 && (s.Universe < _states[^1].Universe - KeptSeconds || _states.Count >= MostKept)) return;
        _states.Insert(at, s);
        // Two at least, however far apart: under a warp they are said once in many universe-seconds,
        // and a craft is carried on from the last two.
        while (_states.Count > 2 && (_states.Count > MostKept || _states[0].Universe < _states[^1].Universe - KeptSeconds)) _states.RemoveAt(0);
    }

    public void Clear() => _states.Clear();

    /// <summary>Where the craft is drawn at a universe time, or null with nothing heard.</summary>
    public Wire.State? At(double universe)
    {
        if (_states.Count == 0) return null;
        double t = universe;
        Wire.State newest = _states[^1];
        if (t >= newest.Universe)
        {
            double ahead = Math.Min(t - newest.Universe, MostAheadSeconds);
            // Moving as it was last seen to be changing, not as it was. The mod writes this velocity
            // into the craft outright, so a craft past its newest state was commanded at the speed it
            // had up to a second ago -- four metres a second out in freefall. Taken from the two
            // newest states, and carried forward for only so long: a craft that stages or throttles
            // changes its acceleration all at once, and a guess from before that would run away.
            (Vec3 velocityCcf, Vec3 velocityCci) = (newest.VelocityCcf, newest.VelocityCci);
            (Vec3 positionCcf, Vec3 positionCci) = (newest.PositionCcf + (newest.VelocityCcf * ahead), newest.PositionCci + (newest.VelocityCci * ahead));
            if (_states.Count > 1 && _states[^2].Body == newest.Body)
            {
                double span = newest.Universe - _states[^2].Universe;
                if (span > 1e-6)
                {
                    // For the interval the two words spanned, or a quarter of a second where that
                    // is longer. A craft being flown is said twenty times a second, and on a line
                    // of 150 ms each way it is drawn a fifth of a second past its newest word the
                    // whole time: three intervals of a rocket's burn left out of where it is.
                    double changing = Math.Min(ahead, Math.Max(span, CarrySeconds)), k = changing / span;
                    (Vec3 gainedCcf, Vec3 gainedCci) = (newest.VelocityCcf - _states[^2].VelocityCcf, newest.VelocityCci - _states[^2].VelocityCci);
                    velocityCcf += gainedCcf * k;
                    velocityCci += gainedCci * k;
                    // And moved by the speed it is said to have had on the way. Carried on at the
                    // speed of its newest word alone, a craft in low orbit was drawn half a metre
                    // inside its own path by the time the next word came and then put back, twice
                    // a second, for any craft its owner was not flying -- which is drawn past its
                    // newest word seven tenths of the time on no wire at all -- and a rocket at
                    // three g on a long line was drawn 0.6 m behind itself throughout. This is the
                    // distance covered at a speed changing as above: steadily for as long as that,
                    // and not at all after it.
                    double covered = ((0.5 * changing * changing) + (changing * (ahead - changing))) / span;
                    positionCcf += gainedCcf * covered;
                    positionCci += gainedCci * covered;
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
                PositionCcf = positionCcf,
                PositionCci = positionCci,
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
        double between = b.Universe - a.Universe, f = (t - a.Universe) / between;
        return b with
        {
            Universe = t,
            PositionCcf = Along(a.PositionCcf, a.VelocityCcf, b.PositionCcf, b.VelocityCcf, f, between),
            VelocityCcf = a.VelocityCcf + ((b.VelocityCcf - a.VelocityCcf) * f),
            PositionCci = Along(a.PositionCci, a.VelocityCci, b.PositionCci, b.VelocityCci, f, between),
            VelocityCci = a.VelocityCci + ((b.VelocityCci - a.VelocityCci) * f),
            Body2Ccf = Quat.Slerp(a.Body2Ccf, b.Body2Ccf, f),
        };
    }

    /// <summary>
    /// Where a craft is part of the way between two of its states, along the curve that leaves the
    /// first at the first's speed and reaches the second at the second's.
    ///
    /// <para>The straight line between them is where it would be had it not been turning or
    /// speeding up, and a craft nobody is flying is said only twice a second: in low orbit the line
    /// cuts 0.3 m inside the path at its middle, and a craft drawn along it and then carried on from
    /// its end is seen to step sideways every half second. Each state has said how fast the craft
    /// was going all along.</para>
    /// </summary>
    private static Vec3 Along(Vec3 from, Vec3 leaving, Vec3 to, Vec3 arriving, double f, double seconds)
    {
        double f2 = f * f, f3 = f2 * f;
        return (from * ((2.0 * f3) - (3.0 * f2) + 1.0)) + (leaving * ((f3 - (2.0 * f2) + f) * seconds))
             + (to * ((3.0 * f2) - (2.0 * f3))) + (arriving * ((f3 - f2) * seconds));
    }
}
