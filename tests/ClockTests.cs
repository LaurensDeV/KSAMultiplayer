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
        Assert.Equal(1.0 + ClockFollow.MostNudge, ClockFollow.Asked(1.0, 1.0 / 20, 1.0 / 30), 9);
        Assert.Equal(10.0, ClockFollow.Asked(10.0, 1.0 / 60, 1.0 / 30), 9);
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
}
