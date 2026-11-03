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

    public Client Join(string name, string build = "B1", string mods = "Core", string password = "")
    {
        Client c = new(Now);
        c.Connect(new IPEndPoint(IPAddress.Loopback, Server.Port), name, build, mods, password);
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
        a.Claim("Rocket", controlling: true);
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
        a.Claim("Rocket", controlling: true);
        rig.Until(() => rig.Server.OwnerOf("Rocket") == a.Id, what: "Alice to own the rocket");
        b.Claim("Rocket", controlling: true);
        // States from someone who does not own it go nowhere.
        b.SendState(Rig.State("Rocket", 6000));
        rig.For(0.3);
        Assert.Equal(a.Id, rig.Server.OwnerOf("Rocket"));
        Assert.Null(a.TrackOf("Rocket").Newest);

        // Alice flies something else. The rocket is still hers and Bob still only watches: a craft
        // taken back the moment its owner looked away put two players on one craft.
        a.Claim("Lander", controlling: true);
        rig.Until(() => rig.Server.Players.First(p => p.Id == a.Id).Controlling == "Lander", what: "Alice to fly the lander");
        b.Claim("Rocket", controlling: true);
        rig.For(0.5);
        Assert.Equal(a.Id, rig.Server.OwnerOf("Rocket"));

        // It is let go of when she leaves, and then it is anyone's.
        a.Dispose();
        rig.Clients.Remove(a);
        rig.Until(() => rig.Server.OwnerOf("Rocket") != a.Id, what: "Alice's craft to be let go of");
        b.Claim("Rocket", controlling: true);
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
        a.Claim("Rocket", controlling: true);
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
        a.Claim("Debris", controlling: false);
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
        b.Claim("Rover", controlling: true);
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

        Wire.Player bob = a.Players.First(p => p.Name == "Bob");
        Assert.InRange(bob.Rtt, 0f, 1f);
        Assert.Equal((float)b.Ping, bob.Rtt, 3);
    }
}
