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
    /// <summary>How long a small gap is taken to close, s.</summary>
    public const double CloseOverSeconds = 5.0;

    /// <summary>The most a world runs faster or slower to close a small gap, as a share.</summary>
    public const double MostNudge = 0.1;

    /// <summary>Further behind than this, s, a world is warped to catch up rather than nudged.</summary>
    public const double CatchUpAboveSeconds = 3.0;

    /// <summary>The most a world is warped to catch up: KSA keeps a craft in physics up to here.</summary>
    public const double MostCatchUp = 10.0;

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
    public static double Speed(double behind, double serverRate)
    {
        if (!(serverRate > 0.0) || !double.IsFinite(behind)) return 0.0;
        double late = behind / serverRate;
        if (late > CatchUpAboveSeconds) return serverRate * Math.Min(MostCatchUp, 1.0 + (late / 2.0));
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
        return Math.Clamp(speed * lost, 0.0, Math.Max(1.0 + MostNudge, speed));
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
            // Out of order, or a world that went back in time on a reload: kept only if it is not already there.
            if (_states.Any(k => k.Universe == s.Universe)) return;
            if (s.Universe < _states[0].Universe - 10.0) _states.Clear();
        }
        int at = _states.FindLastIndex(k => k.Universe < s.Universe) + 1;
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
            return newest with
            {
                Universe = newest.Universe + ahead,
                PositionCcf = newest.PositionCcf + (newest.VelocityCcf * ahead),
                PositionCci = newest.PositionCci + (newest.VelocityCci * ahead),
            };
        }
        if (t <= _states[0].Universe) return _states[0];
        int i = _states.FindLastIndex(k => k.Universe <= t);
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
