using Xunit;

namespace KSAMultiplayer.Tests;

public class ClockTests
{
    [Fact]
    public void TheServersClockHoldsWhileNobodyPlaysAndCarriesOnFromWhereItWas()
    {
        UniverseClock c = new();
        Assert.False(c.Known);
        c.Set(1000, now: 0);
        c.Run(true, now: 0);
        Assert.Equal(1010, c.Now(10), 9);
        c.Run(false, now: 10);
        Assert.Equal(1010, c.Now(500), 9);
        Assert.Equal(0, c.Rate);
        c.Run(true, now: 500);
        Assert.Equal(1015, c.Now(505), 9);
    }

    [Fact]
    public void AWorldAlmostInStepIsNudgedAndOneFarBehindIsWarped()
    {
        Assert.Equal(1.0, ClockFollow.Speed(0, 1), 9);
        Assert.InRange(ClockFollow.Speed(0.2, 1), 1.0, 1.0 + ClockFollow.MostNudge);
        Assert.InRange(ClockFollow.Speed(-0.2, 1), 1.0 - ClockFollow.MostNudge, 1.0);
        Assert.Equal(1.0 + ClockFollow.MostNudge, ClockFollow.Speed(2.9, 1), 9);
        Assert.Equal(ClockFollow.MostCatchUp, ClockFollow.Speed(600, 1), 9);
        Assert.True(ClockFollow.Speed(5, 1) > 1.0 + ClockFollow.MostNudge);
        Assert.Equal(0, ClockFollow.Speed(-10, 1));
        Assert.Equal(0, ClockFollow.Speed(5, 0));

        // A world with nothing of its own in physics may close a gap far faster: KSA works a railed
        // craft out in closed form, so an hour of world costs what a frame of it costs.
        Assert.Equal(ClockFollow.MostCatchUpOnRails, ClockFollow.Speed(6000, 1, ClockFollow.MostCatchUpOnRails), 9);
        Assert.True(ClockFollow.Speed(6000, 1, ClockFollow.MostCatchUpOnRails) > ClockFollow.Speed(6000, 1) * 50.0);

        // A clock whose speed changes carries on from where it is rather than reading every second
        // since it was set at the new speed. Coming out of a warp it used to jump backwards.
        UniverseClock clock = new();
        clock.Set(1000.0, 0.0);
        clock.Run(true, 0.0);
        Assert.Equal(1010.0, clock.Now(10.0), 6);

        clock.SetSpeed(5.0, 10.0);
        Assert.Equal(1010.0, clock.Now(10.0), 6);     // no jump at the moment it changes
        Assert.Equal(1060.0, clock.Now(20.0), 6);     // and five times as fast after it

        clock.SetSpeed(1.0, 20.0);
        Assert.Equal(1060.0, clock.Now(20.0), 6);     // nor back the other way
        Assert.Equal(1070.0, clock.Now(30.0), 6);

        // While the world is warped, how late a game is counts in its own seconds. Five seconds of
        // universe time at 5x is one second late, which is a nudge and not a chase.
        Assert.Equal(5.0 * (1.0 + ClockFollow.MostNudge), ClockFollow.Speed(5, 5), 9);
        Assert.Equal(5.0, ClockFollow.Speed(0, 5), 9);

        // Properly late at 5x -- twenty seconds of its own -- and it catches up, from 5x not from 1x.
        Assert.True(ClockFollow.Speed(100, 5) > 5.0 * (1.0 + ClockFollow.MostNudge));
        Assert.Equal(5.0 * ClockFollow.MostCatchUp, ClockFollow.Speed(5 * 600, 5), 9);

        // Ahead of a warped world by more than its own three seconds: wait, as at any other speed.
        Assert.Equal(0, ClockFollow.Speed(-20, 5));
        Assert.True(ClockFollow.Speed(-10, 5) > 0.0);
    }

    [Fact]
    public void AFrameLongerThanKsasStepIsMadeUp()
    {
        Assert.Equal(1.0, ClockFollow.Asked(1.0, 1.0 / 60, 1.0 / 30), 9);
        // Half again, not a tenth: the make-up for a frame longer than the step cap is the point of
        // this, and the ceiling used to clamp it away whenever little speed was being asked for.
        Assert.Equal(1.5, ClockFollow.Asked(1.0, 1.0 / 20, 1.0 / 30), 9);
        Assert.Equal(10.0, ClockFollow.Asked(10.0, 1.0 / 60, 1.0 / 30), 9);
    }

    [Fact]
    public void AUniverseTimeIsBelievedOnlyIfItCouldBeOne()
    {
        Assert.True(ClockFollow.SaneUniverse(0.0));
        Assert.True(ClockFollow.SaneUniverse(170007.0));
        Assert.True(ClockFollow.SaneUniverse(ClockFollow.MostUniverseSeconds));

        // A game saying it had loaded at 1e12 set the server's clock there, and every world in the
        // room then chased a time it could never reach. A snapshot timed at -1e308 was written to
        // disk as the server's own, and after a restart no world advanced at all.
        Assert.False(ClockFollow.SaneUniverse(1e12));
        Assert.False(ClockFollow.SaneUniverse(-1e308));
        Assert.False(ClockFollow.SaneUniverse(-1.0));
        Assert.False(ClockFollow.SaneUniverse(double.NaN));
        Assert.False(ClockFollow.SaneUniverse(double.PositiveInfinity));

        // And the two it used to let through do what they did.
        Assert.Equal(0.0, ClockFollow.Speed(-1e308, 1.0));
        Assert.Equal(ClockFollow.MostCatchUpOnRails, ClockFollow.Speed(1e12, 1.0, ClockFollow.MostCatchUpOnRails), 9);
    }

    [Fact]
    public void AWorldBehindAlwaysClosesTheGap()
    {
        const double cap = 1.0 / 30;

        // Whatever the frame rate, a world behind the server is asked for more than the server's own
        // rate, so the gap is repaid rather than held. A ceiling worked out from the frame rate was
        // tried here and pinned two players 200 s behind for a whole session: at exactly the minimum
        // frame rate it allowed no catch-up at all, and below it the world lost time every second.
        foreach (double frame in new[] { 1.0 / 144, 1.0 / 60, cap, 1.0 / 20, 0.5 })
        {
            double asked = ClockFollow.Asked(ClockFollow.Speed(200.0, 1.0), frame, cap);
            Assert.True(asked > 1.0, $"a world 200 s behind at {1.0 / frame:F0} fps asked for {asked}");
        }

        // And a world that is only a little behind still makes up what a long frame costs it: KSA
        // steps at most the cap a frame, so 20 frames a second needs half again to hold station.
        Assert.Equal(1.5, ClockFollow.Asked(1.0, 1.0 / 20, cap), 9);
        Assert.True(ClockFollow.Asked(1.0, 1.0 / 20, cap) > 1.0 + ClockFollow.MostNudge,
                    "the make-up has to survive the ceiling, or a slow world loses time every second");
    }

    [Fact]
    public void ACraftPastItsNewestWordKeepsTurningAndKeepsChangingSpeed()
    {
        // A quarter turn a second about Z, speeding up by 10 m/s every second.
        Track track = new();
        Vec3 spin = new(0, 0, Math.PI / 2);
        track.Add(At(10.0, 0) with { Spin = spin, VelocityCcf = new Vec3(10, 0, 0) });
        track.Add(At(10.5, 5) with { Spin = spin, VelocityCcf = new Vec3(15, 0, 0) });

        // Half a second past the newest word, which is where a craft its owner is not flying spends
        // most of its life: the attitude used to be frozen there and the velocity left as it was.
        Wire.State s = Assert.IsType<Wire.State>(track.At(11.0 + Track.DelaySeconds));
        Assert.Equal(11.0, s.Universe, 6);

        // A quarter of a quarter-turn: 22.5 degrees about Z.
        Quat turned = Quat.Turn(spin, 0.5);
        Assert.Equal(Math.Sin(Math.PI / 8), turned.Z, 9);
        Assert.Equal(Math.Cos(Math.PI / 8), turned.W, 9);
        Assert.Equal(turned.Z, s.Body2Ccf.Z, 9);
        Assert.Equal(1.0, s.Body2Ccf.LengthSquared, 9);

        // And carried on at the acceleration the last two words showed, never for longer than the
        // interval they spanned -- a craft that stages changes that all at once.
        Assert.Equal(20.0, s.VelocityCcf.X, 6);
        Wire.State far = Assert.IsType<Wire.State>(track.At(12.5 + Track.DelaySeconds));
        Assert.Equal(20.0, far.VelocityCcf.X, 6);
    }

    [Fact]
    public void NothingIsTurnedByNoSpin()
    {
        Assert.Equal(Quat.Identity, Quat.Turn(Vec3.Zero, 10.0));
        Assert.Equal(Quat.Identity, Quat.Turn(new Vec3(0, 0, 1), 0.0));
        // Two turns one after the other are the turn of the whole time.
        Vec3 spin = new(0.3, -0.4, 0.5);
        Quat once = Quat.Turn(spin, 0.4);
        Quat twice = Quat.Turn(spin, 0.2) * Quat.Turn(spin, 0.2);
        Assert.Equal(once.X, twice.X, 9);
        Assert.Equal(once.W, twice.W, 9);
    }

    private static Wire.State At(double t, double x) =>
        new(1, "R", 0, t, "Earth", new Vec3(x, 0, 0), new Vec3(10, 0, 0), new Vec3(x, 0, 0), new Vec3(10, 0, 0), Quat.Identity, Vec3.Zero, 0, 0);

    [Fact]
    public void ACraftIsDrawnBetweenItsStatesALittleInThePast()
    {
        Track track = new();
        track.Add(At(10.0, 100));
        track.Add(At(10.1, 101));
        track.Add(At(10.05, 100.5));
        Wire.State s = Assert.IsType<Wire.State>(track.At(10.075 + Track.DelaySeconds));
        Assert.Equal(100.75, s.PositionCcf.X, 6);
    }

    [Fact]
    public void ACraftIsCarriedOnPastItsNewestStateForAMomentOnly()
    {
        Track track = new();
        track.Add(At(10.0, 100));
        Assert.Equal(105, track.At(10.5 + Track.DelaySeconds)!.PositionCcf.X, 6);
        Assert.Equal(100 + (10 * Track.MostAheadSeconds), track.At(60)!.PositionCcf.X, 6);
    }

    [Fact]
    public void ACraftIsNotBlendedAcrossAChangeOfBody()
    {
        Track track = new();
        track.Add(At(10.0, 100));
        track.Add(At(10.1, 5) with { Body = "Moon" });
        Assert.Equal("Moon", track.At(10.05 + Track.DelaySeconds)!.Body);
        Assert.Equal(5, track.At(10.05 + Track.DelaySeconds)!.PositionCcf.X, 6);
    }

    [Fact]
    public void AStateOlderThanEverythingKeptTakesNothingElsesPlace()
    {
        Track track = new();
        for (int i = 0; i < 40; i++) track.Add(At(10.0 + (i * 0.05), i));
        double newest = track.Newest!.Universe, oldest = track.At(0.0)!.Universe;

        // There is nothing to put it between and no room for it: what it must not do is go in at the
        // front and then come off the front again as the oldest, which is what dropping the oldest
        // comes to when the newcomer is it.
        track.Add(At(oldest - 0.5, -1.0));
        Assert.Equal(newest, track.Newest!.Universe, 6);
        Assert.Equal(oldest, track.At(0.0)!.Universe, 6);
    }
}
