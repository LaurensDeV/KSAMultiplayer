using KSAMultiplayer.Server;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// A craft its owner walked away from goes to a player who has been watching it and is flying
/// nothing, and to nobody else. And a player joining is not held up by a game that will not save.
/// </summary>
public class WatchingTests
{
    private static Client Seed(Rig rig, string name = "Alice", double universe = 5000)
    {
        Client a = rig.Join(name);
        rig.Until(() => a.Joined && a.SnapshotWanted is not null, what: "the server to ask for a world");
        a.SnapshotWanted = null;
        a.Upload(new Blob(BlobKind.World, "", a.Id, universe, Rig.FakeWorld()));
        rig.Until(() => rig.Server.HasWorld && rig.Server.Players.First(p => p.Id == a.Id).Loaded, what: "the world to arrive");
        return a;
    }

    private static Client JoinAndLoad(Rig rig, string name)
    {
        Client b = rig.Join(name);
        rig.Until(() => b.Blobs.Any(x => x.Kind == BlobKind.World), seconds: 20, what: $"{name} to get the world");
        b.SayLoaded(b.Blobs.First(x => x.Kind == BlobKind.World).Universe);
        rig.Until(() => rig.Server.Players.First(p => p.Id == b.Id).Loaded, what: $"{name} to load");
        return b;
    }

    // Alice holds a rocket and flies a lander.
    private static (Client Alice, Client Bob) AliceWalkedAwayFromTheRocket(Rig rig)
    {
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        a.Claim("Rocket", controlling: true, made: false);
        a.Claim("Lander", controlling: true, made: false);
        rig.Until(() => rig.Server.OwnerOf("Rocket") == a.Id && rig.Server.OwnerOf("Lander") == a.Id, what: "Alice to hold both");
        return (a, b);
    }

    [Fact]
    public void ACraftTheCameraPassesOverIsNotLetGoOf()
    {
        using Rig rig = new(new ServerOptions { LapseWithin = 1.0 });
        (Client a, Client b) = AliceWalkedAwayFromTheRocket(rig);

        // A second on it and then on to the next, as flicking through the vessel list is.
        for (int i = 0; i < 4; i++)
        {
            b.Watching = i % 2 == 0 ? "Rocket" : "Lander";
            rig.For(1.5);
        }
        Assert.Equal(a.Id, rig.Server.OwnerOf("Rocket"));

        b.Watching = "Rocket";
        rig.Until(() => rig.Server.OwnerOf("Rocket") == 0, seconds: 10.0, what: "the rocket to be let go of");
        Assert.Contains(rig.Server.Said, s => s.Contains("Bob has watched it for"));
    }

    [Fact]
    public void ACraftIsNotLetGoOfForAPlayerFlyingSomethingElse()
    {
        using Rig rig = new(new ServerOptions { LapseWithin = 1.0, WatchToTake = 0.5 });
        (Client a, Client b) = AliceWalkedAwayFromTheRocket(rig);
        b.Claim("Probe", controlling: true, made: true);
        rig.Until(() => rig.Server.OwnerOf("Probe") == b.Id, what: "Bob to fly his probe");

        b.Watching = "Rocket";
        rig.For(4.0);
        Assert.Equal(a.Id, rig.Server.OwnerOf("Rocket"));
    }

    /// <summary>
    /// Alice drops and comes straight back. Bob, who was watching her rocket and flying nothing,
    /// cannot take it in between, and she has it again once she is in.
    /// </summary>
    [Fact]
    public void ACraftIsKeptForAPlayerWhoComesStraightBack()
    {
        using Rig rig = new(new ServerOptions { LapseWithin = 1.0, WatchToTake = 0.5 });
        (Client a, Client b) = AliceWalkedAwayFromTheRocket(rig);
        b.Watching = "Rocket";
        rig.For(1.0);

        a.Dispose();
        rig.Until(() => rig.Server.OwnerOf("Rocket") == Wire.Away, what: "the rocket to be kept for Alice");
        b.Claim("Rocket", controlling: true, made: false);
        rig.For(1.0);
        Assert.Equal(Wire.Away, rig.Server.OwnerOf("Rocket"));

        Client again = JoinAndLoad(rig, "Alice");
        rig.Until(() => rig.Server.OwnerOf("Rocket") == again.Id && rig.Server.OwnerOf("Lander") == again.Id, what: "Alice to have both again");
        Assert.Contains(rig.Server.Said, s => s.Contains("Alice is back, and has"));
    }

    /// <summary>
    /// Alice holds a station and docks her own craft with it. She flies and watches the lander,
    /// but it is beside the station, so the station stays hers however long Bob watches it.
    /// </summary>
    [Fact]
    public void ACraftIsNotLetGoOfWhileItsOwnerFliesBesideIt()
    {
        using Rig rig = new(new ServerOptions { LapseWithin = 1.0, WatchToTake = 0.5 });
        (Client a, Client b) = AliceWalkedAwayFromTheRocket(rig);
        a.Watching = "Lander";

        b.Watching = "Rocket";
        double until = rig.Now() + 4.0;
        while (rig.Now() < until)
        {
            Said(rig, a, "Rocket", new Vec3(7_000_000, 0, 0), Wire.StateFlags.Rails);
            Said(rig, a, "Lander", new Vec3(7_000_000, 30, 0), Wire.StateFlags.Controlled);
            rig.For(0.2);
        }
        Assert.Equal(a.Id, rig.Server.OwnerOf("Rocket"));

        // Flown away, she has walked away from it.
        until = rig.Now() + 10.0;
        while (rig.Server.OwnerOf("Rocket") != 0 && rig.Now() < until)
        {
            Said(rig, a, "Rocket", new Vec3(7_000_000, 0, 0), Wire.StateFlags.Rails);
            Said(rig, a, "Lander", new Vec3(7_000_000, 50_000, 0), Wire.StateFlags.Controlled);
            rig.For(0.2);
        }
        Assert.Equal(0, rig.Server.OwnerOf("Rocket"));
    }

    private static void Said(Rig rig, Client by, string key, Vec3 at, Wire.StateFlags flags) =>
        by.SendState(new Wire.State(0, key, 0, rig.Server.UniverseClock.Now(rig.Now()), "Earth", at, Vec3.Zero, at, Vec3.Zero,
                                    Quat.Identity, Vec3.Zero, 0, flags));

    /// <summary>
    /// Let go of, the craft is in its last owner's physics, and their game holds it for being near
    /// before the watcher's has heard. Kept for the watcher, it does not go back.
    /// </summary>
    [Fact]
    public void ACraftLetGoOfForAWatcherIsKeptForThem()
    {
        using Rig rig = new(new ServerOptions { LapseWithin = 1.0, WatchToTake = 0.5 });
        (Client a, Client b) = AliceWalkedAwayFromTheRocket(rig);
        Client c = JoinAndLoad(rig, "Carol");

        b.Watching = "Rocket";
        rig.Until(() => rig.Server.OwnerOf("Rocket") == 0, seconds: 10.0, what: "the rocket to be let go of");
        a.Claim("Rocket", controlling: false, made: false, near: true);
        c.Claim("Rocket", controlling: false, made: false);
        rig.For(1.0);
        Assert.Equal(0, rig.Server.OwnerOf("Rocket"));
        Assert.Contains(rig.Server.Said, s => s.Contains("which is kept for Bob"));

        b.Claim("Rocket", controlling: false, made: false);
        rig.Until(() => rig.Server.OwnerOf("Rocket") == b.Id, what: "Bob to take the rocket");
    }

    // The world is left to grow old, a second at a time so that nobody goes quiet.
    private static void Age(Rig rig, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            rig.Skip(1.0, real: 0.0);
            rig.For(0.01);
        }
    }

    /// <summary>
    /// The game in longest has the editor open and holds the ask without answering. The next is
    /// asked, and the player joining gets its save rather than the old one after the wait.
    /// </summary>
    [Fact]
    public void AJoinerIsNotHeldUpByAGameThatWillNotSave()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        Age(rig, 40);
        a.SnapshotWanted = b.SnapshotWanted = null;

        Client c = rig.Join("Carol");
        rig.Until(() => a.SnapshotWanted is not null, what: "Alice to be asked for a save");
        rig.Until(() => b.SnapshotWanted is not null, seconds: 9.0, what: "Bob to be asked as well");
        Assert.Contains(rig.Server.Said, s => s.Contains("Alice sent no save in 5 s, so Bob is asked too"));
        b.Upload(new Blob(BlobKind.World, "", b.Id, 5100, Rig.FakeWorld(43)));
        rig.Until(() => c.Blobs.Any(x => x.Kind == BlobKind.World), seconds: 5.0, what: "Carol to get the world");
        Assert.Equal(5100, c.Blobs.First(x => x.Kind == BlobKind.World).Universe);
    }

    /// <summary>
    /// The save asked for every few minutes, with nobody joining: the game in longest answers
    /// nothing, and the next is asked rather than the world staying where it was. On ksamp.gg one
    /// such game kept the world an hour old, and three players joined into it.
    /// </summary>
    [Fact]
    public void TheTimedSaveIsAskedOfTheNextGameWhenTheOneAskedWillNotAnswer()
    {
        using Rig rig = new(new ServerOptions { SnapshotEverySeconds = 30 });
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        a.SnapshotWanted = b.SnapshotWanted = null;
        Age(rig, 31);
        rig.Until(() => a.SnapshotWanted is not null, what: "Alice to be asked for the timed save");
        Assert.Null(b.SnapshotWanted);
        Age(rig, 6);
        rig.Until(() => b.SnapshotWanted is not null, what: "Bob to be asked as well");
        Assert.Contains(rig.Server.Said, s => s.Contains("Alice sent no save in 5 s, so Bob is asked too"));
        b.Upload(new Blob(BlobKind.World, "", b.Id, 5100, Rig.FakeWorld(45)));
        rig.Until(() => rig.Server.Said.Any(s => s.StartsWith("Bob saved the world")), what: "Bob's save to be taken");

        // Alice has still answered nothing, so the next ask goes to Bob first.
        a.SnapshotWanted = b.SnapshotWanted = null;
        Age(rig, 31);
        rig.Until(() => b.SnapshotWanted is not null, what: "Bob to be asked first");
        Assert.Null(a.SnapshotWanted);
    }

    /// <summary>
    /// A player joining waits past the usual wait for a save asked of a game late in it: on
    /// ksamp.gg one came a second after the wait ran out, and the joiner was sent a world 3351 s old.
    /// </summary>
    [Fact]
    public void AJoinerWaitsForASaveAskedLateInTheWait()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        Age(rig, 40);
        a.SnapshotWanted = b.SnapshotWanted = null;

        Client c = rig.Join("Carol");
        rig.Until(() => a.SnapshotWanted is not null, what: "Alice to be asked for a save");
        rig.Until(() => b.SnapshotWanted is not null, seconds: 9.0, what: "Bob to be asked as well");
        // Past the 15 s from the join, short of 15 from Bob's ask.
        Age(rig, 12);
        Assert.DoesNotContain(c.Blobs, x => x.Kind == BlobKind.World);
        b.Upload(new Blob(BlobKind.World, "", b.Id, 5200, Rig.FakeWorld(46)));
        rig.Until(() => c.Blobs.Any(x => x.Kind == BlobKind.World), seconds: 5.0, what: "Carol to get the world");
        Assert.Equal(5200, c.Blobs.First(x => x.Kind == BlobKind.World).Universe);
    }

    /// <summary>With nobody else to ask, a joiner is sent the world there is after the usual wait, not the longest.</summary>
    [Fact]
    public void AJoinerWithNobodyLeftToAskWaitsOnlyTheUsualWait()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Age(rig, 40);
        a.SnapshotWanted = null;
        Client c = rig.Join("Carol");
        rig.Until(() => a.SnapshotWanted is not null, what: "Alice to be asked for a save");
        Age(rig, 14);
        Assert.DoesNotContain(c.Blobs, x => x.Kind == BlobKind.World);
        Age(rig, 3);
        rig.Until(() => c.Blobs.Any(x => x.Kind == BlobKind.World), seconds: 3.0, what: "Carol to get the world there is");
    }

    /// <summary>
    /// A save a couple of seconds behind the ask is the fresh one: the game giving it is often that
    /// far behind the server, and was taken only once the wait for a fresh one had run out.
    /// </summary>
    [Fact]
    public void ASaveAFewSecondsBehindTheAskIsFresh()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Age(rig, 40);
        Client c = rig.Join("Carol");
        rig.Until(() => a.SnapshotWanted is not null, what: "Alice to be asked for a save");
        double? asked = rig.Server.AskLastSave();
        Assert.NotNull(asked);
        a.Upload(new Blob(BlobKind.World, "", a.Id, asked!.Value - 3.0, Rig.FakeWorld(44)));
        rig.Until(() => c.Blobs.Any(x => x.Kind == BlobKind.World), seconds: 5.0, what: "Carol to get the world");
        Assert.Equal(asked.Value - 3.0, c.Blobs.First(x => x.Kind == BlobKind.World).Universe);
    }
}
