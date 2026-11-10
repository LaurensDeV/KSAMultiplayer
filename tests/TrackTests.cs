using Xunit;
using Xunit.Abstractions;

namespace KSAMultiplayer.Tests;

/// <summary>
/// A craft flown in one game and drawn in another, with a wire between them and no game at either
/// end: a path whose every position is known, said twenty times a second, held and lost and
/// overtaken on the way, and asked of <see cref="Track"/> once a frame.
///
/// <para>What is measured is what a player would call jitter or a teleport, at its source. How far
/// the place a craft is drawn is from where it truly was at the time it is drawn for; how far that
/// error moves between one frame and the next, which is the pop; and how much of the time the
/// craft is drawn past the newest thing anybody has said of it, which over any wire is all of it. It stops at what <see cref="Track"/>
/// answers. What the mod then does to chase that answer through KSA's physics is the other half,
/// and needs a game.</para>
/// </summary>
public class TrackTests(ITestOutputHelper output)
{
    /// <summary>Where a craft is and how it is moving at a time: the truth a drawing is held against.</summary>
    private delegate (Vec3 At, Vec3 Moving) Path(double t);

    /// <param name="PathSeconds">How long a state takes from the game that says it to the game that draws it: both legs, through the server.</param>
    /// <param name="Jitter">How much that varies, give or take, s. Enough of it and states overtake each other.</param>
    /// <param name="Loss">The share never arriving.</param>
    private readonly record struct Line(double PathSeconds, double Jitter = 0.0, double Loss = 0.0);

    private readonly record struct Frame(double Now, double DrawnFor, double Error, double Pop, bool Past, bool Held);

    private sealed record Flown(List<Frame> Frames)
    {
        public double WorstError => Frames.Max(f => f.Error);
        public double WorstPop => Frames.Max(f => f.Pop);
        public double PastShare => Frames.Count(f => f.Past) / (double)Frames.Count;
        public double HeldShare => Frames.Count(f => f.Held) / (double)Frames.Count;
        public double MeanError => Frames.Average(f => f.Error);
    }

    // A low orbit of the Earth: 7.8 km/s, turning at 9.2 m/s^2.
    private static readonly Path Orbit = t =>
    {
        const double R = 6_571_000, V = 7_788;
        double a = t * V / R;
        return (new Vec3(R * Math.Cos(a), R * Math.Sin(a), 0), new Vec3(-V * Math.Sin(a), V * Math.Cos(a), 0));
    };

    // A rocket off the pad at three g.
    private static readonly Path Burn = t => (new Vec3(0, 0, 15.0 * t * t), new Vec3(0, 0, 30.0 * t));

    // The same rocket with its engine cut at ten seconds: what a guess carried on from before gets wrong.
    private static readonly Path Cutoff = t => t < 10.0
        ? (new Vec3(0, 0, 15.0 * t * t), new Vec3(0, 0, 30.0 * t))
        : (new Vec3(0, 0, 1500.0 + (300.0 * (t - 10.0))), new Vec3(0, 0, 300.0));

    // A craft being flown by hand: five metres side to side every four seconds, 12 m/s^2 at the turn.
    private static readonly Path Weave = t =>
    {
        const double A = 5.0, W = Math.PI / 2;
        return (new Vec3(20.0 * t, A * Math.Sin(W * t), 0), new Vec3(20.0, A * W * Math.Cos(W * t), 0));
    };

    /// <summary>
    /// Flies a path for a while in one game and draws it in another.
    /// </summary>
    /// <param name="every">Seconds between states: a twentieth for the craft a player flies, a half for any other they own.</param>
    /// <param name="warp">Universe seconds a real second. States are sent by the clock on the wall and drawn by the universe's.</param>
    /// <param name="behind">How far the game that draws is behind the game that flies, in universe seconds: what a game that has just joined is.</param>
    private static Flown Fly(Path path, Line line, double every = 0.05, double seconds = 30.0, double warp = 1.0, double fps = 60.0, int seed = 1, double behind = 0.0)
    {
        Random chance = new(seed);
        // Each state with the moment on the wall it reaches the other game, soonest first.
        PriorityQueue<Wire.State, double> onTheWay = new();
        uint sequence = 0;
        for (double wall = 0.0; wall < seconds + 2.0; wall += every)
        {
            if (chance.NextDouble() < line.Loss) continue;
            (Vec3 at, Vec3 moving) = path(wall * warp);
            Wire.State said = new(1, "R", sequence++, wall * warp, "Earth", at, moving, at, moving, Quat.Identity, Vec3.Zero, 0, Wire.StateFlags.Controlled);
            onTheWay.Enqueue(said, wall + line.PathSeconds + (((chance.NextDouble() * 2.0) - 1.0) * line.Jitter));
        }

        Track track = new();
        List<Frame> frames = [];
        Vec3? errorWas = null;
        // From two seconds in, so the first states have had every chance to arrive, and from as
        // much later again as the drawing game is behind, so there is a state before what it asks.
        for (double wall = 2.0 + (behind / warp); wall < seconds; wall += 1.0 / fps)
        {
            while (onTheWay.TryPeek(out Wire.State? next, out double arrives) && arrives <= wall) track.Add(onTheWay.Dequeue());
            double now = (wall * warp) - behind;
            if (track.At(now) is not { } drawn || track.Newest is not { } newest) continue;
            Vec3 error = drawn.PositionCcf - path(drawn.Universe).At;
            // Asked to draw a time later than it could be carried on to: the craft has stopped
            // where its last word and a second's coasting left it.
            bool held = now - drawn.Universe > 1e-9;
            frames.Add(new Frame(now, drawn.Universe, error.Length, errorWas is { } was ? (error - was).Length : 0.0,
                                 drawn.Universe > newest.Universe + 1e-9, held));
            errorWas = error;
        }
        return new Flown(frames);
    }

    private static readonly (string Name, Line Line)[] Lines =
    [
        ("loopback", new Line(0.0005)),
        ("25 ms each way", new Line(0.05)),
        ("50 ms each way", new Line(0.10)),
        ("150 ms each way", new Line(0.30)),
        ("50 ms +-20, 2% lost", new Line(0.10, 0.04, 0.02)),
        ("50 ms +-20, 10% lost", new Line(0.10, 0.04, 0.10)),
    ];

    [Fact]
    public void WhatEachWireDoesToEachPath()
    {
        foreach ((string what, Path path, double every) in new (string, Path, double)[]
                 {
                     ("an orbit, flown", Orbit, 0.05), ("a burn at 3 g, flown", Burn, 0.05), ("a weave, flown", Weave, 0.05),
                     ("a burn cut off, flown", Cutoff, 0.05),
                     ("an orbit, not flown", Orbit, 0.5), ("a weave, not flown", Weave, 0.5),
                 })
        {
            output.WriteLine($"{what}, a state every {every * 1000:0} ms");
            output.WriteLine("  wire                      worst error  mean error   worst pop   drawn past newest   held");
            foreach ((string name, Line line) in Lines)
            {
                Flown f = Fly(path, line, every);
                output.WriteLine($"  {name,-24} {f.WorstError,10:0.000} m {f.MeanError,9:0.000} m {f.WorstPop,9:0.000} m {f.PastShare,14:P0} {f.HeldShare,10:P0}");
            }
        }
        output.WriteLine("an orbit, flown, under warp on loopback");
        foreach (double warp in new[] { 2.0, 5.0, 10.0, 100.0 })
        {
            Flown f = Fly(Orbit, new Line(0.0005), warp: warp, seconds: 20.0);
            output.WriteLine($"  {warp,5:0}x                    {f.WorstError,10:0.000} m {f.MeanError,9:0.000} m {f.WorstPop,9:0.000} m {f.PastShare,14:P0} {f.HeldShare,10:P0}");
        }
    }

    /// <summary>Lines a game is ordinarily played over: up to 50 ms each way, steady or not.</summary>
    private static readonly Line[] Ordinary = [new(0.0005), new(0.05), new(0.10), new(0.10, 0.04, 0.02)];

    [Fact]
    public void ACraftCoastingOrBurningIsDrawnWhereItIsOverAnyOrdinaryLine()
    {
        foreach (Path path in new[] { Orbit, Burn })
        {
            foreach (Line line in Ordinary)
            {
                Flown f = Fly(path, line);
                // Every frame is drawn past the newest word, by the trip from one game to the other.
                Assert.Equal(1.0, f.PastShare, 3);
                // Carried on at the speed of that word alone, an orbit was 6 cm out and a burn 20
                // whenever a state was late, and then put back.
                Assert.InRange(f.WorstError, 0.0, 0.01);
                Assert.InRange(f.WorstPop, 0.0, 0.01);
            }
        }
    }

    [Fact]
    public void ACraftBeingThrownAboutIsDrawnWithinAHandsWidth()
    {
        // Twelve metres a second squared, reversing every two seconds: what a guess a round trip
        // long cannot know is how the turn has changed since it was last said.
        foreach (Line line in Ordinary)
        {
            Flown f = Fly(Weave, line);
            Assert.InRange(f.WorstError, 0.0, 0.05);
            Assert.InRange(f.WorstPop, 0.0, 0.05);
        }
    }

    [Fact]
    public void OnALongLineACraftIsStillDrawnWhereItIsToAHandsWidth()
    {
        // 150 ms each way is 300 ms from one game to the other, and a rocket at three g carried on
        // at the speed of its newest word alone was 1.4 m behind itself for the whole of its burn.
        Flown orbit = Fly(Orbit, new Line(0.30)), burn = Fly(Burn, new Line(0.30));
        Assert.InRange(orbit.WorstError, 0.0, 0.05);
        Assert.InRange(burn.WorstError, 0.0, 0.16);
        Assert.InRange(burn.WorstPop, 0.0, 0.10);
    }

    [Fact]
    public void AnEngineCutOffIsOneStepAndThenNothing()
    {
        // What carrying a change of speed forward costs: until somebody has said the engine is
        // off, the craft is drawn as still burning. One step, as long as the trip between the two
        // games at three g, against being wrong for the whole of every burn.
        // On the long line the burn itself is drawn 15 cm out, so the step is counted above that.
        foreach ((Line line, double most, double quiet) in new[] { (new Line(0.0005), 0.05, 0.01), (new Line(0.05), 0.12, 0.01), (new Line(0.10), 0.30, 0.01), (new Line(0.30), 1.60, 0.2) })
        {
            Flown f = Fly(Cutoff, line);
            Assert.InRange(f.WorstError, 0.0, most);
            Assert.InRange(f.Frames.Count(x => x.Error > quiet), 0, 25);
        }
    }

    [Fact]
    public void ACraftNobodyIsFlyingIsDrawnOnItsPathBetweenItsTwoWordsASecond()
    {
        // Said twice a second, it is carried on for up to half a second and the trip. In low orbit,
        // at the speed of its newest word alone, that was 1.2 m inside its own path by the time
        // the next word came, and as much of being put back, every half second.
        foreach ((Line line, double most) in new[] { (new Line(0.0005), 0.01), (new Line(0.05), 0.02), (new Line(0.10), 0.05) })
        {
            Flown f = Fly(Orbit, line, every: 0.5);
            Assert.InRange(f.WorstError, 0.0, most);
            Assert.InRange(f.WorstPop, 0.0, most);
        }
    }

    [Fact]
    public void ACraftBeingFlownUnderWarpIsDrawnWhereItIs()
    {
        // States are sent by the clock on the wall and drawn by the universe's, so at ten times
        // real time a flown craft is said twice a universe-second, like one nobody is flying.
        Flown f = Fly(Orbit, new Line(0.0005), warp: 10.0, seconds: 20.0);
        Assert.InRange(f.WorstError, 0.0, 0.01);
    }

    [Fact]
    public void AWorldThatHasJustJoinedIsToldWhereACraftWasAndNotWhereItWillBe()
    {
        // Two and a half seconds behind the game that flies the craft, which is where a game is for
        // the half minute after it joins. Asked about a time older than anything kept, the answer
        // was the oldest state there was -- nearly a second ahead of the question, and 7 km.
        Flown f = Fly(Orbit, new Line(0.0005), behind: 2.5);
        Assert.Equal(0.0, f.PastShare, 3);
        Assert.InRange(f.WorstError, 0.0, 0.01);
        Assert.InRange(f.Frames.Max(x => Math.Abs(x.Now - x.DrawnFor)), 0.0, 1e-6);
    }

    [Fact]
    public void BetweenTwoStatesACraftIsOnTheCurveItFlewAndNotTheLineBetweenThem()
    {
        // Half a second of low orbit: the middle of the straight line is 0.29 m inside the path.
        // Drawn there only where this world is behind the states it is sent, as one that has just
        // loaded is.
        Track track = new();
        foreach (double t in new[] { 100.0, 100.5 })
        {
            (Vec3 at, Vec3 moving) = Orbit(t);
            track.Add(new Wire.State(1, "R", 0, t, "Earth", at, moving, at, moving, Quat.Identity, Vec3.Zero, 0, 0));
        }
        Wire.State drawn = Assert.IsType<Wire.State>(track.At(100.25));
        Assert.InRange((drawn.PositionCcf - Orbit(100.25).At).Length, 0.0, 0.001);
        Assert.InRange((drawn.PositionCci - Orbit(100.25).At).Length, 0.0, 0.001);
    }
}
