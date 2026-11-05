using System.Diagnostics;
using System.Net;
using KSAMultiplayer.Server;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>A real server and real clients over loopback, ticked on one thread as fast as they go.</summary>
public sealed class Rig : IDisposable
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    public readonly List<Client> Clients = [];
    public GameServer Server { get; }

    public Rig(ServerOptions? options = null, WorldStore? store = null)
    {
        Server = new GameServer((options ?? new ServerOptions()) with { Port = 0 }, Now, store, IPAddress.Loopback);
    }

    public double Now() => _watch.Elapsed.TotalSeconds;

    public Client Join(string name, string build = "B1", string mods = "Core", string password = "", string system = "SolLite")
    {
        Client c = new(Now);
        c.Connect(new IPEndPoint(IPAddress.Loopback, Server.Port), name, build, mods, system, password);
        Clients.Add(c);
        return c;
    }

    public void Tick()
    {
        Server.Tick();
        foreach (Client c in Clients) c.Update();
    }

    /// <summary>Ticks until a condition holds, failing after so long.</summary>
    public void Until(Func<bool> done, double seconds = 5.0, string what = "the condition")
    {
        double until = Now() + seconds;
        while (!done())
        {
            if (Now() > until) throw new Xunit.Sdk.XunitException($"timed out waiting for {what}; server said: {string.Join(" | ", Server.Said)}");
            Tick();
            Thread.Sleep(1);
        }
    }

    /// <summary>Ticks for a while whatever happens.</summary>
    public void For(double seconds)
    {
        double until = Now() + seconds;
        while (Now() < until)
        {
            Tick();
            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        foreach (Client c in Clients) c.Dispose();
        Server.Dispose();
    }

    public static byte[] FakeWorld(int kb = 40)
    {
        Random r = new(kb);
        byte[] noise = new byte[kb * 1024];
        r.NextBytes(noise);
        return Blob.Zip([("universe.xml", "<Universe/>"u8.ToArray()), ("meta.xml", noise)]);
    }

    public static Wire.State State(string key, double universe, double x = 0) =>
        new(0, key, 0, universe, "Earth", new Vec3(6_371_000 + x, 0, 0), Vec3.Zero, new Vec3(x, 0, 0), Vec3.Zero, Quat.Identity, Vec3.Zero, 0, Wire.StateFlags.Controlled);
}

public class ServerTests
{
    // The first player in gives an empty server its world, and the clock starts from it.
    private static Client Seed(Rig rig, string name = "Alice", double universe = 5000)
    {
        Client a = rig.Join(name);
        rig.Until(() => a.Joined && a.SnapshotWanted is not null, what: "the server to ask for a world");
        Assert.False(a.ServerHasWorld);
        a.SnapshotWanted = null;
        a.Upload(new Blob(BlobKind.World, "", a.Id, universe, Rig.FakeWorld()));
        rig.Until(() => rig.Server.HasWorld && rig.Server.Players.First(p => p.Id == a.Id).Loaded, what: "the world to arrive");
        return a;
    }

    private static Client JoinAndLoad(Rig rig, string name)
    {
        Client b = rig.Join(name);
        rig.Until(() => b.Blobs.Any(x => x.Kind == BlobKind.World), seconds: 20, what: $"{name} to get the world");
        Assert.True(b.ServerHasWorld);
        Blob world = b.Blobs.First(x => x.Kind == BlobKind.World);
        Assert.Equal(rig.Server.World!.Data, world.Data);
        b.SayLoaded(world.Universe);
        rig.Until(() => rig.Server.Players.First(p => p.Id == b.Id).Loaded, what: $"{name} to load");
        return b;
    }

    [Fact]
    public void AnEmptyServerTakesTheFirstPlayersWorldAndStartsItsClockFromIt()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Assert.True(rig.Server.UniverseClock.Known);
        Assert.InRange(rig.Server.UniverseClock.Now(rig.Now()), 5000, 5010);
        rig.Until(() => a.ServerUniverse is not null, what: "a clock word");
        Assert.InRange(a.ServerUniverse!.Value.Universe, 5000, 5010);
    }

    [Fact]
    public void ASecondPlayerIsHandedTheWorldAndKeepsTheServersTime()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        rig.Until(() => b.ServerUniverse is not null && b.ClockKnown, what: "Bob's clock");
        double serverNow = rig.Server.UniverseClock.Now(rig.Now());
        Assert.InRange(b.ServerUniverse!.Value.Universe - serverNow, -0.05, 0.05);
        Assert.Equal(2, b.Players.Length);
        Assert.Contains(b.Players, p => p.Name == "Alice" && p.Loaded);
    }

    [Fact]
    public void ACraftsStatesReachEveryoneElseAndWhoFliesItIsSaid()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        a.Claim("Rocket", controlling: true, made: false);
        rig.Until(() => b.OwnerOf("Rocket") == a.Id && a.Owns("Rocket"), what: "the owners table");
        for (int i = 0; i < 5; i++) a.SendState(Rig.State("Rocket", 5000 + i, x: i));
        rig.Until(() => b.TrackOf("Rocket").Newest is { Universe: 5004 }, what: "the states");
        Assert.Equal(a.Id, b.TrackOf("Rocket").Newest!.Owner);
        Assert.Empty(a.Tracked.Where(k => k == "Rocket" && a.TrackOf(k).Newest is not null));
        rig.Until(() => b.Players.Any(p => p.Id == a.Id && p.Controlling == "Rocket"), what: "the roster");
    }

    [Fact]
    public void NobodyTakesACraftAnotherPlayerHoldsUntilThatPlayerLeaves()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        a.Claim("Rocket", controlling: true, made: false);
        rig.Until(() => rig.Server.OwnerOf("Rocket") == a.Id, what: "Alice to own the rocket");
        b.Claim("Rocket", controlling: true, made: false);
        // States from someone who does not own it go nowhere.
        b.SendState(Rig.State("Rocket", 6000));
        rig.For(0.3);
        Assert.Equal(a.Id, rig.Server.OwnerOf("Rocket"));
        Assert.Null(a.TrackOf("Rocket").Newest);

        // Alice flies something else. The rocket is still hers and Bob still only watches: a craft
        // taken back the moment its owner looked away put two players on one craft.
        a.Claim("Lander", controlling: true, made: false);
        rig.Until(() => rig.Server.Players.First(p => p.Id == a.Id).Controlling == "Lander", what: "Alice to fly the lander");
        b.Claim("Rocket", controlling: true, made: false);
        rig.For(0.5);
        Assert.Equal(a.Id, rig.Server.OwnerOf("Rocket"));

        // It is let go of when she leaves, and then it is anyone's.
        a.Dispose();
        rig.Clients.Remove(a);
        rig.Until(() => rig.Server.OwnerOf("Rocket") != a.Id, what: "Alice's craft to be let go of");
        b.Claim("Rocket", controlling: true, made: false);
        rig.Until(() => rig.Server.OwnerOf("Rocket") == b.Id, what: "Bob to take the rocket now");
    }

    [Fact]
    public void ACraftMadeAfterTheWorldWasSavedIsBuiltByThoseAlreadyInAndThoseWhoJoinLater()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        byte[] definition = new byte[30_000];
        new Random(1).NextBytes(definition);
        a.Claim("Rocket", controlling: true, made: false);
        a.Upload(new Blob(BlobKind.Vehicle, "Rocket", a.Id, 5001, definition));
        rig.Until(() => b.Blobs.Any(x => x.Kind == BlobKind.Vehicle), what: "Bob to get the rocket");
        Blob got = b.Blobs.First(x => x.Kind == BlobKind.Vehicle);
        Assert.Equal("Rocket", got.Key);
        Assert.Equal(a.Id, got.Owner);
        Assert.Equal(definition, got.Data);

        Client c = JoinAndLoad(rig, "Carol");
        rig.Until(() => c.Blobs.Any(x => x.Kind == BlobKind.Vehicle && x.Key == "Rocket"), what: "Carol to get the rocket");
        rig.Until(() => c.OwnerOf("Rocket") == a.Id, what: "Carol to hear who flies it");
    }

    [Fact]
    public void ACraftThatIsGoneIsSaidToBeGoneToEveryoneAndToThoseWhoJoinLater()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        a.Claim("Debris", controlling: false, made: false);
        rig.Until(() => b.OwnerOf("Debris") == a.Id, what: "Bob to hear of the debris");
        a.SayGone("Debris");
        rig.Until(() => b.Gone.Contains("Debris"), what: "Bob to hear it is gone");
        Assert.Equal(0, b.OwnerOf("Debris"));

        Client c = JoinAndLoad(rig, "Carol");
        rig.Until(() => c.Gone.Contains("Debris"), what: "Carol to hear it is gone");
    }

    [Fact]
    public void APlayerWhoLeavesLetsGoOfWhatTheyFlew()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        b.Claim("Rover", controlling: true, made: false);
        rig.Until(() => a.OwnerOf("Rover") == b.Id, what: "Alice to hear Bob flies the rover");
        b.Dispose();
        rig.Clients.Remove(b);
        rig.Until(() => a.OwnerOf("Rover") == 0 && a.Players.Length == 1, what: "the rover to be let go");
        Assert.Contains(a.Chat, line => line.Text.Contains("Bob has left"));
    }

    [Fact]
    public void APlayerJoiningAnOldWorldWaitsForAFreshSaveFromSomeoneInIt()
    {
        using Rig rig = new(new ServerOptions { KeepTime = true });
        Client a = Seed(rig, universe: 100);
        // Thirty seconds of universe time on, the world kept is stale.
        rig.Server.UniverseClock.Set(200, rig.Now());
        Client b = rig.Join("Bob");
        rig.Until(() => a.SnapshotWanted is not null, what: "Alice to be asked for a save");
        a.SnapshotWanted = null;
        a.Upload(new Blob(BlobKind.World, "", a.Id, 200, Rig.FakeWorld(41)));
        rig.Until(() => b.Blobs.Any(x => x.Kind == BlobKind.World), seconds: 20, what: "Bob to get a world");
        Assert.Equal(200, b.Blobs.First(x => x.Kind == BlobKind.World).Universe);
    }

    [Fact]
    public void AWorldGoesWholeOverALineThatLosesAFifthOfEverything()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        rig.Server.Socket.Loss = 0.2;
        Client b = rig.Join("Bob");
        b.Socket.Loss = 0.2;
        rig.Until(() => b.Blobs.Any(x => x.Kind == BlobKind.World), seconds: 30, what: "the world over a lossy line");
        Assert.Equal(rig.Server.World!.Data, b.Blobs.First(x => x.Kind == BlobKind.World).Data);
    }

    [Theory]
    [InlineData("wrong", "B1", "Core", "wrong password")]
    [InlineData("pw", "B2", "Core", "you need the same build")]
    [InlineData("pw", "B1", "", "mods you do not have enabled: Core")]
    [InlineData("pw", "B1", "Core,Shiny,Louder", "mods this world does not have (Louder, Shiny)")]
    public void APlayerWhoCannotLoadTheWorldIsToldWhy(string password, string build, string mods, string why)
    {
        using Rig rig = new(new ServerOptions { Password = "pw" });
        Client a = rig.Join("Alice", password: "pw");
        rig.Until(() => a.SnapshotWanted is not null, what: "the seed request");
        a.SnapshotWanted = null;
        a.Upload(new Blob(BlobKind.World, "", a.Id, 1, Rig.FakeWorld()));
        rig.Until(() => rig.Server.HasWorld, what: "the world");

        Client b = rig.Join("Bob", build, mods, password);
        rig.Until(() => b.Out.Length > 0, what: "a refusal");
        Assert.Contains(why, b.Out);
        Assert.False(b.Joined);
    }

    [Fact]
    public void TwoPlayersOfOneNameAreToldApart()
    {
        using Rig rig = new();
        Seed(rig, "Jeb");
        Client b = JoinAndLoad(rig, "Jeb");
        rig.Until(() => b.Players.Length == 2, what: "the roster");
        Assert.Contains(b.Players, p => p.Name == "Jeb 2" && p.Id == b.Id);
    }

    [Fact]
    public void ChatReachesEveryone()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        b.SayChat("  hi\u0007 there  ");
        rig.Until(() => a.Chat.Any(l => l.Text == "hi there"), what: "the chat");
        Assert.Contains(a.Chat, l => l.Who == "Bob" && l.Text == "hi there");
    }

    [Fact]
    public void AServerRestartedCarriesOnFromTheWorldItKept()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ksamp-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (Rig rig = new(store: new WorldStore(folder))) Seed(rig, universe: 7777);
            using Rig again = new(store: new WorldStore(folder));
            Assert.True(again.Server.HasWorld);
            Assert.True(again.Server.UniverseClock.Known);
            Assert.Equal(7777, again.Server.UniverseClock.Now(again.Now()), 3);
            Client b = again.Join("Bob");
            again.Until(() => b.Blobs.Any(x => x.Kind == BlobKind.World), seconds: 20, what: "the kept world");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void ASaveCopiedInByHandIsFoundWhereverInTheZipItIs()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ksamp-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            string save = Path.Combine(folder, "My Save");
            Directory.CreateDirectory(Path.Combine(save, "sub"));
            File.WriteAllText(Path.Combine(save, "universe.xml"), "<Universe/>");
            File.WriteAllText(Path.Combine(save, "sub", "x.xml"), "<X/>");
            string[] fromFolder = [.. Blob.Unzip(WorldStore.Import(folder)).Select(f => f.Name).Order()];
            Assert.Equal(["sub/x.xml", "universe.xml"], fromFolder);

            string zip = Path.Combine(folder, "save.zip");
            File.WriteAllBytes(zip, Blob.Zip([("My Save/universe.xml", [1]), ("My Save/sub/x.xml", [2])]));
            string[] fromZip = [.. Blob.Unzip(WorldStore.Import(zip)).Select(f => f.Name).Order()];
            Assert.Equal(["sub/x.xml", "universe.xml"], fromZip);

            File.WriteAllBytes(zip, Blob.Zip([("readme.txt", [1])]));
            Assert.Throws<InvalidDataException>(() => WorldStore.Import(zip));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void AServerWhoseFirstWorldGiverLeavesAsksTheNextPlayer()
    {
        using Rig rig = new();
        Client a = rig.Join("Alice");
        rig.Until(() => a.SnapshotWanted is not null, what: "Alice to be asked");
        Client b = rig.Join("Bob");
        rig.Until(() => b.Joined, what: "Bob to join");
        Assert.Null(b.SnapshotWanted);
        a.Dispose();
        rig.Clients.Remove(a);
        rig.Until(() => b.SnapshotWanted is not null, what: "Bob to be asked instead");
    }

    [Fact]
    public void TwoGamesThatEachMakeACraftOfOneNameEachGetACraftOfTheirOwn()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        // Each game sends a kitten out of its own capsule, and KSA calls both of them the same thing.
        a.Claim("Pamont", controlling: true, made: true);
        rig.Until(() => rig.Server.OwnerOf("Pamont") == a.Id, what: "Alice to take her kitten");

        b.Claim("Pamont", controlling: true, made: true);
        rig.Until(() => b.Renamed.Count > 0, what: "Bob's kitten to be given a name of its own");

        (string was, string now) = b.Renamed.Dequeue();
        Assert.Equal("Pamont", was);
        Assert.NotEqual("Pamont", now);

        // Two craft, one each, and neither is the other.
        Assert.Equal(a.Id, rig.Server.OwnerOf("Pamont"));
        Assert.Equal(b.Id, rig.Server.OwnerOf(now));
        rig.Until(() => a.OwnerOf(now) == b.Id, what: "Alice to hear of Bob's kitten");

        // Bob's states under the new name reach Alice; under the old one they are Alice's craft.
        b.SendState(Rig.State(now, 5001));
        rig.Until(() => a.TrackOf(now).Newest is not null, what: "Alice to get Bob's kitten's state");
        Assert.Null(b.TrackOf("Pamont").Newest);
    }

    [Fact]
    public void ACraftFromTheSharedWorldIsNeverRenamed()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        // Both games loaded this one with the world, so it is one craft and Bob only watches it.
        a.Claim("Rocket", controlling: true, made: false);
        rig.Until(() => rig.Server.OwnerOf("Rocket") == a.Id, what: "Alice to take the rocket");

        b.Claim("Rocket", controlling: true, made: false);
        rig.For(0.5);
        Assert.Empty(b.Renamed);
        Assert.Equal(a.Id, rig.Server.OwnerOf("Rocket"));
    }

    [Fact]
    public void APlayerInAnotherCelestialSystemIsTurnedAway()
    {
        using Rig rig = new();
        Seed(rig);

        // The world is Earth and its Moon; this player is playing the whole of Sol.
        Client other = rig.Join("Bob", system: "Sol");
        rig.Until(() => other.Out.Length > 0, what: "Bob to be turned away");
        Assert.Contains("SolLite", other.Out);
        Assert.Contains("Sol", other.Out);

        // One in the same system is let in.
        Client same = rig.Join("Carol");
        rig.Until(() => same.Joined, what: "Carol to be let in");
    }

    [Fact]
    public void AnEmptyServersTimeIsTheTimeOfTheWorldItHandsOut()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        rig.For(1.0);

        // The clock runs while she is in the world, and so runs past the save it was set from.
        Assert.True(rig.Server.UniverseClock.Now(rig.Now()) > 5000.0);

        a.Dispose();
        rig.Clients.Remove(a);
        rig.Until(() => !rig.Server.Players.Any(p => p.Loaded), what: "Alice to leave");
        rig.For(0.3);

        // What she simulated past the save went with her, so the clock waits where the world is.
        Assert.Equal(5000.0, rig.Server.UniverseClock.Now(rig.Now()), 1);

        // The next player therefore has nothing to catch up on.
        Client b = JoinAndLoad(rig, "Bob");
        Assert.InRange(rig.Server.UniverseClock.Now(rig.Now()) - rig.Server.World!.Universe, -0.5, 1.5);
        Assert.True(b.Joined);
    }

    [Fact]
    public void TimeRunsOnWithNobodyInItWhenTheOperatorAsksForThat()
    {
        using Rig rig = new(new ServerOptions { KeepTime = true });
        Client a = Seed(rig);
        a.Dispose();
        rig.Clients.Remove(a);
        rig.Until(() => !rig.Server.Players.Any(p => p.Loaded), what: "Alice to leave");

        double at = rig.Server.UniverseClock.Now(rig.Now());
        rig.For(0.5);
        Assert.True(rig.Server.UniverseClock.Now(rig.Now()) > at, "--keep-time means the clock keeps going");
    }

    [Fact]
    public void APlayerLeavingAsksWhoeverIsStillHereForASave()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        (a.SnapshotWanted, b.SnapshotWanted) = (null, null);

        a.Dispose();
        rig.Clients.Remove(a);

        // What Alice did since the last save is in nobody's world unless Bob writes it down now.
        rig.Until(() => b.SnapshotWanted is not null, what: "Bob to be asked for a save");
    }

    [Fact]
    public void AWarpRunsOnlyWhenEveryoneAgrees()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        a.AskWarp(5.0);
        rig.Until(() => b.WarpWantsAnswer, what: "Bob to be asked");

        // Asked, not running: Alice agreeing with herself is not everyone.
        Assert.Equal(Wire.WarpPhase.Asking, rig.Server.Warp.Phase);
        Assert.Equal(1.0, rig.Server.UniverseClock.Speed);

        b.VoteWarp(true);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.Running, what: "the warp to run");
        Assert.Equal(5.0, rig.Server.UniverseClock.Speed, 3);
        rig.Until(() => a.Warp is { Phase: Wire.WarpPhase.Running }, what: "Alice to be told");
    }

    [Fact]
    public void TheLastPlayerLeftStillHasTheWarp()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        a.AskWarp(5.0);
        rig.Until(() => b.WarpWantsAnswer, what: "Bob to be asked");
        b.VoteWarp(true);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.Running, what: "the warp to run");

        b.Dispose();
        rig.Clients.Remove(b);
        rig.Until(() => rig.Server.Players.Count == 1, seconds: 15.0, what: "Bob to be gone");

        // Alice's warp, and Alice is all there is: hers to speed up and hers to end.
        a.AskWarp(10.0);
        rig.Until(() => Math.Abs(rig.Server.UniverseClock.Speed - 10.0) < 0.001, what: "the warp to change");

        a.AskWarp(1.0);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.None, what: "the warp to end");
    }

    [Fact]
    public void AWarpOutlivesNobodyWhoStartedIt()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        b.AskWarp(5.0);
        rig.Until(() => a.WarpWantsAnswer, what: "Alice to be asked");
        a.VoteWarp(true);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.Running, what: "the warp to run");

        b.Dispose();
        rig.Clients.Remove(b);

        // Nobody else may change a warp that is not theirs, so one left running by a player who has
        // gone is a world nobody can slow down.
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.None, seconds: 15.0, what: "the warp to end with its owner");
        Assert.Equal(1.0, rig.Server.UniverseClock.Speed, 3);

        // And the one still here can start a warp of their own, once the quiet after one has passed.
        rig.For(5.5);
        a.AskWarp(2.0);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.Running, seconds: 15.0, what: "Alice's own warp to run");
    }

    [Fact]
    public void TheOneAskingMayChangeTheSpeedTheyAreAskingFor()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        a.AskWarp(2.0);
        rig.Until(() => b.Warp is { Phase: Wire.WarpPhase.Asking, Speed: 2f }, what: "Bob to be asked for 2x");

        // Still her question, at a different number: Bob sees it change and is still being asked.
        a.AskWarp(4.0);
        rig.Until(() => b.Warp is { Phase: Wire.WarpPhase.Asking, Speed: 4f }, what: "Bob to be asked for 4x");
        Assert.True(b.WarpWantsAnswer);

        // And what he agrees to is the number in front of him.
        b.VoteWarp(true);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.Running, what: "the warp to run");
        Assert.Equal(4.0, rig.Server.UniverseClock.Speed, 3);
    }

    [Fact]
    public void NobodyAsksOverSomebodyElsesWarp()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        a.AskWarp(2.0);
        rig.Until(() => b.WarpWantsAnswer, what: "Bob to be asked");

        // Bob answers or he does not; what he may not do is ask for something else instead.
        b.AskWarp(100.0);
        rig.For(1.0);
        Assert.Equal(Wire.WarpPhase.Asking, rig.Server.Warp.Phase);
        Assert.Equal(2.0, rig.Server.Warp.Speed, 3);
        Assert.Equal(a.Id, rig.Server.Warp.Who);
    }

    [Fact]
    public void AWarpIsPutAtWhatTheRoomCanFly()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        // Bob's machine manages six times real time and he is looking at it.
        b.Can = 6.0;
        rig.Until(() => rig.Server.Players.Any(p => p.Name == "Bob" && p.Can is > 0.0 and < 7.0), seconds: 10.0, what: "Bob to say what he can fly");

        // Alice asks for fifty; what is put to the room is six.
        a.AskWarp(50.0);
        rig.Until(() => b.WarpWantsAnswer, what: "Bob to be asked");
        Assert.Equal(6.0, rig.Server.Warp.Speed, 1);

        b.VoteWarp(true);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.Running, what: "the warp to run");
        Assert.Equal(6.0, rig.Server.UniverseClock.Speed, 1);
    }

    [Fact]
    public void AWarpFallsBackWhenAMachineCannotFlyIt()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        (a.Can, b.Can) = (20.0, 20.0);
        rig.Until(() => rig.Server.Players.All(p => p.Can > 10.0), seconds: 10.0, what: "both to say what they can fly");

        a.AskWarp(10.0);
        rig.Until(() => b.WarpWantsAnswer, what: "Bob to be asked");
        b.VoteWarp(true);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.Running, what: "the warp to run");
        Assert.Equal(10.0, rig.Server.UniverseClock.Speed, 1);

        // Bob's machine gives out. The world slows to what he can fly rather than leaving him behind.
        b.Can = 3.0;
        rig.Until(() => rig.Server.UniverseClock.Speed < 5.0, seconds: 10.0, what: "the world to slow to Bob");
        Assert.Equal(Wire.WarpPhase.Running, rig.Server.Warp.Phase);
        Assert.True(rig.Server.UniverseClock.Speed >= 1.0);

        // And climbs back when he recovers, without anybody asking again.
        b.Can = 20.0;
        rig.Until(() => rig.Server.UniverseClock.Speed > 8.0, seconds: 20.0, what: "the world to climb back");
    }

    [Fact]
    public void NobodyIsHeldToAWindowThatIsNotBeingLookedAt()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        (a.Can, b.Can) = (20.0, 20.0);
        rig.Until(() => rig.Server.Players.All(p => p.Can > 10.0), seconds: 10.0, what: "both to say what they can fly");

        a.AskWarp(10.0);
        rig.Until(() => b.WarpWantsAnswer, what: "Bob to be asked");
        b.VoteWarp(true);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.Running, what: "the warp to run");

        // Bob alt-tabs. His window is throttled, so what it manages says nothing about his machine,
        // and the room does not crawl for somebody who is not watching it.
        (b.Here, b.Can) = (false, 1.0);
        rig.For(3.0);
        Assert.True(rig.Server.UniverseClock.Speed > 8.0);
    }

    [Fact]
    public void WhatAPlayerIsFlyingFollowsThemAndIsGivenUp()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        a.Claim("Rocket", controlling: true, made: false);
        rig.Until(() => b.Players.Any(p => p.Id == a.Id && p.Controlling == "Rocket"), what: "Bob to see Alice in the rocket");

        // She moves. Her name belongs on the lander now, and on nothing else -- though the rocket
        // stays hers, which is what stops Bob taking it.
        a.Claim("Lander", controlling: true, made: false);
        rig.Until(() => b.Players.Any(p => p.Id == a.Id && p.Controlling == "Lander"), what: "Bob to see Alice in the lander");
        Assert.Equal(a.Id, rig.Server.OwnerOf("Rocket"));

        // And a craft sent without the controlled flag is one nobody is in: she is flying nothing.
        a.SendState(new Wire.State(0, "Lander", 1, 0.0, "Earth", default, default, default, default,
                                   new Quat(0, 0, 0, 1), default, 0f, Wire.StateFlags.None));
        rig.Until(() => b.Players.Any(p => p.Id == a.Id && p.Controlling.Length == 0), what: "Bob to see Alice fly nothing");
    }

    [Fact]
    public void ACraftItsOwnerWalkedAwayFromGoesToWhoeverIsWaitingOnIt()
    {
        using Rig rig = new(new ServerOptions { LapseWithin = 1.0 });
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        a.Claim("Rocket", controlling: true, made: false);
        a.Claim("Lander", controlling: true, made: false);
        rig.Until(() => rig.Server.OwnerOf("Rocket") == a.Id && rig.Server.OwnerOf("Lander") == a.Id,
                  what: "Alice to hold both");

        // She is in the lander and not looking at the rocket, but nobody else wants it, so it stays
        // hers: a parked craft changing hands for nobody is churn and nothing else.
        rig.For(3.0);
        Assert.Equal(a.Id, rig.Server.OwnerOf("Rocket"));

        // Bob looks at it, and now it is not hers to hold.
        b.Watching = "Rocket";
        rig.Until(() => rig.Server.OwnerOf("Rocket") == 0, seconds: 15.0, what: "the rocket to be let go of");
        Assert.Equal(a.Id, rig.Server.OwnerOf("Lander"));

        // And Bob may have it, which before this he could not until she left.
        b.Claim("Rocket", controlling: true, made: false);
        rig.Until(() => rig.Server.OwnerOf("Rocket") == b.Id, what: "Bob to take the rocket");
    }

    [Fact]
    public void ACraftItsOwnerIsLookingAtStaysTheirs()
    {
        using Rig rig = new(new ServerOptions { LapseWithin = 1.0 });
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        a.Claim("Rocket", controlling: true, made: false);
        a.Claim("Lander", controlling: true, made: false);
        rig.Until(() => rig.Server.OwnerOf("Rocket") == a.Id, what: "Alice to hold the rocket");

        // Watching is being near it: she has stepped out to look at it, not walked away from it.
        a.Watching = "Rocket";
        rig.For(3.0);
        Assert.Equal(a.Id, rig.Server.OwnerOf("Rocket"));
    }

    [Fact]
    public void AGameThatWillNotHandOverItsWorldHasSomebodyElseAskedInstead()
    {
        using Rig rig = new();
        Client a = rig.Join("Alice");
        rig.Until(() => a.Joined && a.SnapshotWanted is not null, what: "Alice to be asked for a world");
        a.SnapshotWanted = null;
        Client b = rig.Join("Bob");
        rig.Until(() => b.Joined, what: "Bob to join");

        // Hers holds a craft KSA has lost inside a body: a world nobody could open is worse than none.
        a.SayNoWorld("Rocket is inside the body it belongs to");
        rig.Until(() => b.SnapshotWanted is not null, what: "Bob to be asked instead");

        b.SnapshotWanted = null;
        b.Upload(new Blob(BlobKind.World, "", b.Id, 5000, Rig.FakeWorld()));
        rig.Until(() => rig.Server.HasWorld, what: "Bob's world");
    }

    [Fact]
    public void OneNoIsEnoughToStopAWarp()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        a.AskWarp(10.0);
        rig.Until(() => b.WarpWantsAnswer, what: "Bob to be asked");
        b.VoteWarp(false);

        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.None, what: "the warp to be refused");
        Assert.Equal(1.0, rig.Server.UniverseClock.Speed);
    }

    [Fact]
    public void SayingNothingIsNotAgreeing()
    {
        // The ask runs out rather than passing without the player who never answered.
        using Rig rig = new(new ServerOptions());
        Client a = Seed(rig);
        JoinAndLoad(rig, "Bob");

        a.AskWarp(4.0);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.Asking, what: "the ask to go out");
        rig.For(1.0);

        // Bob never votes. Nothing has started.
        Assert.Equal(Wire.WarpPhase.Asking, rig.Server.Warp.Phase);
        Assert.Equal(1.0, rig.Server.UniverseClock.Speed);
    }

    [Fact]
    public void AnAskNobodyAnswersRunsOut()
    {
        // A second to answer rather than the thirty a player gets, so the test is not thirty seconds.
        using Rig rig = new(new ServerOptions { WarpAnswerSeconds = 1.0 });
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        a.AskWarp(6.0);
        rig.Until(() => b.WarpWantsAnswer, what: "Bob to be asked");

        // Bob never answers. The ask goes away and the world never warped.
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.None, seconds: 10.0, what: "the ask to run out");
        Assert.Equal(1.0, rig.Server.UniverseClock.Speed);
    }

    [Fact]
    public void AWarpIsTheAskersToStopAndNobodyElses()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        a.AskWarp(5.0);
        rig.Until(() => b.WarpWantsAnswer, what: "Bob to be asked");
        b.VoteWarp(true);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.Running, what: "the warp to run");

        // Bob did not start it, so Bob cannot steer it or end it.
        b.AskWarp(1.0);
        b.AskWarp(50.0);
        rig.For(0.5);
        Assert.Equal(Wire.WarpPhase.Running, rig.Server.Warp.Phase);
        Assert.Equal(5.0, rig.Server.UniverseClock.Speed, 3);

        a.AskWarp(1.0);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.None, what: "Alice to end her warp");
        Assert.Equal(1.0, rig.Server.UniverseClock.Speed);
    }

    [Fact]
    public void AWarpCannotBeAskedForAgainTheInstantItEnds()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        a.AskWarp(5.0);
        rig.Until(() => b.WarpWantsAnswer, what: "Bob to be asked");
        b.VoteWarp(true);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.Running, what: "the warp to run");
        a.AskWarp(1.0);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.None, what: "the warp to end");

        // Straight back in: ignored, or a warp would be one nobody could get out of.
        b.AskWarp(8.0);
        rig.For(0.5);
        Assert.Equal(Wire.WarpPhase.None, rig.Server.Warp.Phase);
    }

    [Fact]
    public void APauseIsAWarpLikeAnyOther()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        a.AskWarp(0.0);
        rig.Until(() => b.WarpWantsAnswer, what: "Bob to be asked");
        b.VoteWarp(true);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.Running, what: "the pause to take");

        // Stopped for everyone: the clock the games follow does not move.
        Assert.Equal(0.0, rig.Server.UniverseClock.Speed);
        double at = rig.Server.UniverseClock.Now(rig.Now());
        rig.For(0.4);
        Assert.Equal(at, rig.Server.UniverseClock.Now(rig.Now()), 3);
    }

    [Fact]
    public void EveryPlayerIsToldWhatEveryOtherPlayersPingIs()
    {
        using Rig rig = new();
        Client a = rig.Join("Alice");
        Client b = rig.Join("Bob");
        rig.Until(() => a.Joined && b.Joined, what: "both to join");

        // A game says what its own round trip is; the server passes it on, so each sees the other's.
        // A ping goes once a second and a roster every two, so this is given room to be slow.
        rig.Until(() => double.IsFinite(a.Ping) && double.IsFinite(b.Ping), 15.0, "both to measure a round trip");
        rig.Until(() => a.Players.Any(p => p.Name == "Bob" && p.Rtt >= 0f), 15.0, "Alice to be told Bob's ping");

        // Not b.Ping exactly: that is whatever Bob has measured by now, while this is what he last
        // told the server and the server last passed on. Over loopback both are small and real.
        Wire.Player bob = a.Players.First(p => p.Name == "Bob");
        Assert.InRange(bob.Rtt, 0f, 1f);
        Assert.True(double.IsFinite(b.Ping));
    }

    [Fact]
    public void TheOperatorsBroomTakesACraftOutOfEveryGameIncludingItsOwners()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        // Alice is flying one and has left two behind, which is what a world fills up with.
        a.Claim("Lander", controlling: true, made: true);
        a.Claim("Stage 1", controlling: false, made: true);
        a.Claim("Stage 2", controlling: false, made: true);
        a.SendState(Rig.State("Lander", 5000));
        rig.Until(() => b.OwnerOf("Stage 2") == a.Id && a.Owns("Stage 1"), what: "the owners table");

        Assert.Equal(2, rig.Server.Sweep("Stage *").Count);
        rig.Until(() => a.Swept.Count == 2 && b.Swept.Count == 2, what: "both games to be told");
        Assert.Equal(["Stage 1", "Stage 2"], a.Swept.OrderBy(k => k).ToArray());

        // Gone from the server too, and what is being flown is left alone.
        Assert.Equal(0, rig.Server.OwnerOf("Stage 1"));
        Assert.Equal(a.Id, rig.Server.OwnerOf("Lander"));
        Assert.Empty(rig.Server.Sweep("Stage *"));
    }

    [Fact]
    public void EverybodyIsPutBackOnTheWorldWithoutReconnecting()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        a.Blobs.Clear();
        b.Blobs.Clear();
        rig.For(1.5);

        Assert.Equal(2, rig.Server.ReloadAll("the operator asked"));
        rig.Until(() => a.Blobs.Any(x => x.Kind == BlobKind.World) && b.Blobs.Any(x => x.Kind == BlobKind.World),
                  seconds: 20, what: "both to be handed the world again");
        Assert.DoesNotContain(rig.Server.Players, p => p.Loaded);

        // Nobody is in the world while they load it, so the clock goes back to the world's own time:
        // the one thing that moves everybody's time at once, and why this is the way to put it back.
        rig.Until(() => Math.Abs(rig.Server.UniverseClock.Now(rig.Now()) - rig.Server.World!.Universe) < 0.1,
                  what: "the clock to go back to the world's time");

        // And back in when their games say so, with no hello of any kind in between.
        a.SayLoaded(rig.Server.World!.Universe);
        b.SayLoaded(rig.Server.World!.Universe);
        rig.Until(() => rig.Server.Players.All(p => p.Loaded), what: "both back in the world");
        Assert.Equal(2, rig.Server.Players.Count);
    }

    [Fact]
    public void TheWorldEverybodyIsPutBackOnIsTheClearedOne()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        a.Claim("Stage 1", controlling: false, made: true);
        rig.Until(() => b.OwnerOf("Stage 1") == a.Id, what: "the owners table");
        a.Blobs.Clear();
        b.Blobs.Clear();

        // A sweep asks whoever has been in longest for a save, since the world on disk still holds
        // what was swept. Nobody goes back on the world until that save is the one they would get.
        Assert.Single(rig.Server.Sweep());
        rig.Until(() => a.SnapshotWanted is not null, what: "Alice to be asked for a save of the cleared world");
        Assert.DoesNotContain(rig.Server.Players, p => !p.Loaded);

        a.SnapshotWanted = null;
        a.Upload(new Blob(BlobKind.World, "", a.Id, 6000, Rig.FakeWorld(20)));
        rig.Until(() => a.Blobs.Any(x => x.Kind == BlobKind.World) && b.Blobs.Any(x => x.Kind == BlobKind.World),
                  seconds: 20, what: "both to be put back on the cleared world");
        Assert.Equal(6000, rig.Server.World!.Universe);
        Assert.Equal(rig.Server.World.Data, b.Blobs.Last(x => x.Kind == BlobKind.World).Data);
    }

    [Fact]
    public void ACraftSomebodyIsFlyingIsNotSweptAway()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        a.Claim("Lander", controlling: true, made: true);
        a.SendState(Rig.State("Lander", 5000));
        rig.Until(() => rig.Server.Players.Any(p => p.Controlling == "Lander"), what: "the server to see what Alice flies");

        Assert.Empty(rig.Server.Sweep());
        Assert.Equal(a.Id, rig.Server.OwnerOf("Lander"));
    }

    [Fact]
    public void AWorldNobodyAskedForIsNotTheServersWorld()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        byte[] was = rig.Server.World!.Data;

        // Alice is in the world and has been asked for nothing since she gave it. A world taken from
        // whoever cares to send one is written to disk along with that sender's build and mods, and
        // everybody else is then held to those: one client could lock the rest out of the server.
        a.Upload(new Blob(BlobKind.World, "", a.Id, 9000.0, Rig.FakeWorld(60)));
        rig.Until(() => rig.Server.Said.Any(said => said.Contains("nobody asked for")), seconds: 20, what: "the server to leave it where it came from");
        Assert.Equal(was, rig.Server.World!.Data);
        Assert.Equal(5000.0, rig.Server.World!.Universe, 1);
    }

    [Fact]
    public void NobodyIsInAWorldTheyWereNeverGiven()
    {
        using Rig rig = new();
        Client a = rig.Join("Alice");
        rig.Until(() => a.Joined && a.SnapshotWanted is not null, what: "Alice to be asked for a world");
        Client b = rig.Join("Bob");
        rig.Until(() => b.Joined, what: "Bob to join");

        // Bob has been handed nothing: there is no world yet, and Alice's has not come. A game that
        // said it was in the shared world anyway claimed craft and blocked every warp.
        b.SayLoaded(4321.0);
        rig.For(2.0);
        Assert.DoesNotContain(rig.Server.Players, p => p.Id == b.Id && p.Loaded);
        Assert.False(rig.Server.UniverseClock.Known);
    }

    [Fact]
    public void AWorldSaidToBeLoadedAtATimeThatIsNoTimeIsNotTheClock()
    {
        using Rig rig = new();
        // A world copied in by hand, whose universe time the server cannot read: the first player to
        // load it says what it is, and from then on the whole room follows that one number.
        rig.Server.SetWorld(new Blob(BlobKind.World, "", 0, 0.0, Rig.FakeWorld()), "B1", ["Core"], "SolLite", timeKnown: false);
        Client a = rig.Join("Alice");
        rig.Until(() => a.Blobs.Any(x => x.Kind == BlobKind.World), seconds: 20, what: "Alice to be sent the world");

        a.SayLoaded(double.NaN);
        rig.For(1.0);
        Assert.False(rig.Server.UniverseClock.Known);
        Assert.DoesNotContain(rig.Server.Players, p => p.Loaded);

        a.SayLoaded(5000.0);
        rig.Until(() => rig.Server.UniverseClock.Known, what: "the clock to start from Alice's world");
        Assert.InRange(rig.Server.UniverseClock.Now(rig.Now()), 5000, 5010);
    }

    [Fact]
    public void APauseIsNotUndoneByTheGovernor()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        a.AskWarp(0.0);
        rig.Until(() => b.WarpWantsAnswer, what: "Bob to be asked");
        b.VoteWarp(true);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.Running, what: "the pause to take");

        // Bob's world is a long way behind the server's, which is what moves the governor. It has
        // nothing to say about a world that is stopped: a pause asks nothing of anybody's machine,
        // and nothing here climbs back down to 0 once it has decided the floor is 1x.
        b.Late = 30.0;
        rig.Until(() => rig.Server.Players.Any(p => p.Id == b.Id && p.Late > 20.0), seconds: 10.0, what: "the server to hear how far behind Bob is");
        rig.For(4.0);
        Assert.Equal(Wire.WarpPhase.Running, rig.Server.Warp.Phase);
        Assert.Equal(0.0, rig.Server.UniverseClock.Speed);
    }

    [Fact]
    public void AWarpDoesNotClimbBackIntoACeilingAMachineHasHit()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        (a.Can, b.Can) = (20.0, 20.0);
        rig.Until(() => rig.Server.Players.All(p => p.Can > 10.0), seconds: 10.0, what: "both to say what they can fly");

        a.AskWarp(10.0);
        rig.Until(() => b.WarpWantsAnswer, what: "Bob to be asked");
        b.VoteWarp(true);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.Running, what: "the warp to run");

        b.Can = 4.0;
        rig.Until(() => rig.Server.UniverseClock.Speed < 6.0, seconds: 10.0, what: "the world to slow to Bob");

        // And now Bob says nothing at all, which is what a game says once it is comfortable: it has
        // found no ceiling at the speed it is running at. The ten it could not fly is not tried
        // again on his silence, or the world climbs into it and falls out of it once a second.
        b.Can = 0.0;
        rig.For(12.0);
        Assert.Equal(Wire.WarpPhase.Running, rig.Server.Warp.Phase);
        Assert.InRange(rig.Server.UniverseClock.Speed, 1.0, 8.5);
    }
}
