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

    public Rig(ServerOptions? options = null, WorldStore? store = null, Admins? admins = null)
    {
        Server = new GameServer((options ?? new ServerOptions()) with { Port = 0 }, Now, store, IPAddress.Loopback, admins);
    }

    // Time nobody waited for. The clocks here are the rig's, so a test about something that happens
    // after ten seconds of silence can have those ten seconds without taking them.
    private double _skipped;

    public double Now() => _watch.Elapsed.TotalSeconds + _skipped;

    /// <summary>
    /// Puts the clock forward and then lets the machine run for a moment, so anything living on a
    /// thread of its own -- the client's keepalive -- gets to notice the time it was given.
    /// </summary>
    public void Skip(double seconds, double real = 0.3)
    {
        _skipped += seconds;
        double until = Now() + real;
        while (Now() < until)
        {
            Server.Tick();
            Thread.Sleep(5);
        }
    }

    public Client Join(string name, string build = "B1", string mods = "Core", string password = "", string system = "SolLite", string token = "")
    {
        Client c = new(Now);
        c.Connect(new IPEndPoint(IPAddress.Loopback, Server.Port), name, build, mods, system, password, token);
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

    /// <summary>
    /// An empty world the server carries, so a room does not wait on somebody to bring one. The
    /// save is KSA's own and nothing here reads inside it; what is checked is that it goes out as
    /// any other world does and arrives whole.
    /// </summary>
    [Fact]
    public void AServerCanStartWithAnEmptyWorldOfItsOwn()
    {
        byte[] empty = StockWorld.Zip("SolLite")!;
        using Rig rig = new(new ServerOptions { EmptyWorld = "SolLite" });
        rig.Server.SetWorld(new Blob(BlobKind.World, "", 0, 0.0, empty), "", [], "SolLite", timeKnown: false);

        Client a = rig.Join("Alice");
        rig.Until(() => a.Blobs.Any(x => x.Kind == BlobKind.World), seconds: 20, what: "the world to come");
        // Nobody was asked for one, and what came is the world the server was carrying.
        Assert.Null(a.SnapshotWanted);
        Assert.Equal(empty, a.Blobs.First(x => x.Kind == BlobKind.World).Data);
    }

    /// <summary>
    /// fresh: the world thrown away under everybody and an empty one handed out where they stand.
    /// </summary>
    [Fact]
    public void FreshThrowsTheWorldAwayAndHandsOutAnEmptyOne()
    {
        using Rig rig = new(new ServerOptions { EmptyWorld = "SolLite" });
        Client a = Seed(rig);
        rig.Until(() => rig.Server.CraftCount >= 0 && rig.Server.HasWorld, what: "a world");
        a.Upload(new Blob(BlobKind.Vehicle, "Rocket", a.Id, 5000, Rig.FakeWorld(2)));
        rig.For(0.3);
        byte[] was = rig.Server.World!.Data;

        string said = rig.Server.Fresh("a test asked");
        Assert.Contains("SolLite", said);
        Assert.NotEqual(was, rig.Server.World!.Data);
        Assert.Equal(StockWorld.Zip("SolLite"), rig.Server.World!.Data);
        // Nothing of the old world is still known, and its time went with it.
        Assert.Equal(0, rig.Server.CraftCount);
        Assert.False(rig.Server.UniverseClock.Known);
        // And it goes out: the player is put back on the new one rather than left in the old.
        rig.Until(() => a.Blobs.Any(x => x.Kind == BlobKind.World && x.Data.SequenceEqual(StockWorld.Zip("SolLite")!)),
                  seconds: 20, what: "the empty world to arrive");
    }

    [Fact]
    public void FreshWithNothingToStartFromSaysSo()
    {
        using Rig rig = new();
        Seed(rig);
        string said = rig.Server.Fresh("a test asked");
        Assert.Contains("set empty-world", said);
        // And the world everybody is in is left exactly where it was.
        Assert.True(rig.Server.HasWorld);
    }

    /// <summary>
    /// A game whose frame loop has stopped. Everything a client sends goes out of <c>Update</c>, so
    /// a stall sends nothing at all -- and ten seconds of nothing used to be a player the server
    /// dropped, which is what put people out of a world every time KSA hitched on a rebuild.
    /// </summary>
    [Fact]
    public void AGameThatStallsStaysInTheWorld()
    {
        using Rig rig = new();
        Client a = Seed(rig);

        // Nobody calls Update: this is the game frozen, and only the server runs.
        for (int i = 0; i < 8; i++) rig.Skip(2.0);

        Assert.Contains(rig.Server.Players, p => p.Id == a.Id);
        Assert.True(a.Joined);
        Assert.DoesNotContain(rig.Server.Said, said => said.Contains("gone quiet", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// And the other end of it: a seat is not held open for ever by a process that has hung. Past
    /// the minute a stall is spoken for, the keepalive stops and the server's own silence takes over.
    /// </summary>
    [Fact]
    public void AGameThatNeverComesBackIsLetGoOf()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        for (int i = 0; i < 40; i++) rig.Skip(2.0, real: 0.05);
        Assert.DoesNotContain(rig.Server.Players, p => p.Id == a.Id);
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
        Assert.DoesNotContain(a.Tracked, k => k == "Rocket" && a.TrackOf(k).Newest is not null);
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
    public void ACraftThatIsGoneIsSaidGoneMoreThanOnce()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        a.Claim("Debris", controlling: false, made: false);
        rig.Until(() => b.OwnerOf("Debris") == a.Id, what: "Bob to hear of the debris");
        a.SayGone("Debris");
        rig.Until(() => b.Gone.Count(k => k == "Debris") >= 2, what: "Bob to be told twice that it is gone");
    }

    private static Wire.State Standing(string key, Wire.StateFlags flags, double x = 1) =>
        new(0, key, 0, 5000, "Earth", new Vec3(x, 2, 3), Vec3.Zero, new Vec3(x, 2, 3), Vec3.Zero, Quat.Identity, Vec3.Zero, 0, flags);

    /// <summary>Two players each in a craft, the two craft said to be so many metres apart.</summary>
    private static void Beside(Rig rig, Client a, string mine, Client b, string theirs, double metres = 20)
    {
        a.Claim(mine, controlling: true);
        b.Claim(theirs, controlling: true);
        rig.Until(() => a.OwnerOf(theirs) == b.Id && b.OwnerOf(mine) == a.Id, what: "each to hear of the other's craft");
        a.SendState(Standing(mine, Wire.StateFlags.Controlled));
        b.SendState(Standing(theirs, Wire.StateFlags.Controlled, 1 + metres));
        rig.For(0.3);
    }

    [Fact]
    public void ACraftHeldForBeingNearIsOneCraftAndAnybodysToFly()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        a.Claim("Station", controlling: false, made: false, near: true);
        rig.Until(() => b.OwnerOf("Station") == a.Id && b.NearOf("Station"), what: "Bob to hear Alice has the station near her");
        Assert.Equal(0, b.HeldBy("Station"));

        // Near Bob as well, which gives him nothing: one game steps it and the other is told where it is.
        b.Claim("Station", controlling: false, made: false, near: true);
        rig.For(0.5);
        Assert.Equal(a.Id, b.OwnerOf("Station"));

        // Flown, it is his at once and his outright.
        b.Claim("Station", controlling: true);
        rig.Until(() => a.OwnerOf("Station") == b.Id && !a.NearOf("Station"), what: "the station to be Bob's for flying it");
    }

    [Fact]
    public void ACraftHeldForBeingNearIsLetGoWhenItsHolderNoLongerSaysItIs()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        a.Claim("Hull", controlling: false, made: false, near: true);
        a.Claim("Tank", controlling: false, made: false, near: true);
        rig.Until(() => b.OwnerOf("Hull") == a.Id && b.OwnerOf("Tank") == a.Id, what: "Bob to hear of both");

        // Said to be near, it is kept past the time a hold lasts with nothing said.
        for (int i = 0; i < 8; i++)
        {
            a.SendState(Standing("Hull", Wire.StateFlags.Landed | Wire.StateFlags.Near));
            rig.For(0.5);
        }
        Assert.Equal(a.Id, b.OwnerOf("Hull"));
        // The other, of which nothing was said, has lapsed by now.
        Assert.Equal(0, b.OwnerOf("Tank"));

        // And the first word without it gives the craft up, for whoever has it near next.
        a.SendState(Standing("Hull", Wire.StateFlags.Landed));
        rig.Until(() => b.OwnerOf("Hull") == 0, what: "the hull to be nobody's");
        b.Claim("Hull", controlling: false, made: false, near: true);
        rig.Until(() => a.OwnerOf("Hull") == b.Id, what: "the hull to be held by Bob, who has it near now");
    }

    [Fact]
    public void ACraftHeldOutrightIsNotGivenUpForBeingNearSomebodyElse()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        a.Claim("Lander", controlling: true);
        rig.Until(() => b.OwnerOf("Lander") == a.Id, what: "Bob to hear of the lander");
        b.Claim("Lander", controlling: false, made: false, near: true);
        // Nor does saying so of her own craft make it anybody's to take.
        a.Claim("Lander", controlling: false, made: false, near: true);
        rig.For(0.5);
        Assert.Equal(a.Id, b.OwnerOf("Lander"));
        Assert.False(b.NearOf("Lander"));
    }

    [Fact]
    public void TwoPlayersCraftDockIntoOneThatIsWhoeverDockedFirsts()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        Beside(rig, a, "Capsule", b, "Station");

        // In Alice's game her capsule went into Bob's station, which is the craft that is left.
        a.SayDocked("Capsule", "Station");
        rig.Until(() => b.Docked.Count > 0, what: "Bob to be told of the docking");
        Assert.Equal(("Capsule", "Station", a.Id), b.Docked.Peek());
        // Known to his game in the same breath, before any roster.
        Assert.Equal(a.Id, b.OwnerOf("Station"));
        rig.Until(() => a.OwnerOf("Station") == a.Id && a.OwnerOf("Capsule") == 0, what: "the roster to give Alice the station and nobody the capsule");
        rig.Until(() => b.Gone.Contains("Capsule"), what: "the capsule to be said gone as any craft is");
        Assert.Equal("", rig.Server.Players.First(p => p.Id == b.Id).Controlling);

        // Bob's game docked them too, a moment later and the other way round. It is not his to say.
        b.SayDocked("Station", "Capsule");
        rig.For(0.5);
        Assert.Equal(a.Id, b.OwnerOf("Station"));
        Assert.Equal(0, a.OwnerOf("Capsule"));
        Assert.Empty(a.Docked);

        // And a game that goes on speaking of the craft it lost is told again what became of it.
        b.Docked.Clear();
        b.Claim("Station", controlling: true);
        rig.Until(() => b.Docked.Count > 0, what: "Bob to be told again");
    }

    [Fact]
    public void ADockingTakesNoCraftThatIsNotBesideTheOther()
    {
        // A game's word that two craft docked takes one from whoever had it. Taken on trust, a
        // player could have any craft in the world by saying so of one of their own.
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        Beside(rig, a, "Capsule", b, "Station", metres: 40_000);
        a.SayDocked("Capsule", "Station");
        a.SayDocked("Station", "Capsule");
        rig.For(0.5);
        Assert.Equal(b.Id, a.OwnerOf("Station"));
        Assert.Equal(a.Id, b.OwnerOf("Capsule"));
        Assert.Empty(b.Docked);
        Assert.DoesNotContain("Station", a.Gone);

        // Nor one its owner has said nothing of: there is nothing to hold the docking against.
        b.Claim("Tug", controlling: false, made: true);
        rig.Until(() => a.OwnerOf("Tug") == b.Id, what: "Alice to hear of the tug");
        a.SayDocked("Tug", "Capsule");
        rig.For(0.5);
        Assert.Equal(b.Id, a.OwnerOf("Tug"));

        // Held only for being near, it is nobody's in earnest and is taken as it always was.
        b.Claim("Hull", controlling: false, made: false, near: true);
        rig.Until(() => a.OwnerOf("Hull") == b.Id && a.NearOf("Hull"), what: "Alice to hear Bob has the hull near him");
        a.SayDocked("Hull", "Capsule");
        rig.Until(() => b.Gone.Contains("Hull"), what: "the hull to be gone");
    }

    [Fact]
    public void ACraftADockingTookIsNotBroughtBackByItsPilotAskingForItOnceMore()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        Beside(rig, a, "Station", b, "Capsule");
        a.SayDocked("Capsule", "Station");
        rig.Until(() => b.Gone.Contains("Capsule"), what: "the capsule to be gone");
        b.Docked.Clear();

        // Bob's game was flying it and says so every two seconds; one of those was on its way.
        b.Claim("Capsule", controlling: true);
        rig.Until(() => b.Docked.Count > 0, what: "Bob to be told again that it docked");
        Assert.Equal(0, a.OwnerOf("Capsule"));
        Assert.Equal("", rig.Server.Players.First(p => p.Id == b.Id).Controlling);

        // Undocked and docked again inside half a minute, it is a second docking and not the first said again.
        a.Claim("Capsule", controlling: false, made: true);
        rig.Until(() => b.OwnerOf("Capsule") == a.Id, what: "the capsule to be back, Alice's");
        b.Gone.Clear();
        a.SayDocked("Capsule", "Station");
        rig.Until(() => b.Gone.Contains("Capsule"), what: "the capsule to be gone a second time");
        Assert.Equal(0, a.OwnerOf("Capsule"));
    }

    [Fact]
    public void HowACraftIsBuiltDoesNotTakeACraftSomebodyHasNearThem()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        b.Claim("Debris 3", controlling: false, made: false, near: true);
        rig.Until(() => a.OwnerOf("Debris 3") == b.Id, what: "Alice to hear Bob has the wreckage near him");

        // A piece of Alice's that KSA gave the same name: asked for as made, it is given a name of
        // its own, and what was said of it under the old one is not the wreckage.
        a.Claim("Debris 3", controlling: false, made: true);
        a.Upload(new Blob(BlobKind.Vehicle, "Debris 3", a.Id, 5001, [9, 9, 9]));
        rig.Until(() => a.Renamed.Count > 0, what: "Alice's piece to be given a name of its own");
        rig.For(0.5);
        Assert.Equal(b.Id, a.OwnerOf("Debris 3"));
        Assert.True(a.NearOf("Debris 3"));
        Assert.DoesNotContain(b.Blobs, x => x.Key == "Debris 3");
    }

    [Fact]
    public void ACraftHeldForBeingNearIsItsPilotsOnceItIsFlown()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        a.Claim("Rover", controlling: false, made: false, near: true);
        rig.Until(() => b.NearOf("Rover"), what: "Bob to hear Alice has the rover near her");

        // The claim that says she is flying it is lost; what she says of it from the seat is not.
        a.SendState(Standing("Rover", Wire.StateFlags.Controlled | Wire.StateFlags.Near));
        rig.Until(() => b.OwnerOf("Rover") == a.Id && !b.NearOf("Rover"), what: "the rover to be Alice's outright");
        b.Claim("Rover", controlling: true);
        rig.For(0.5);
        Assert.Equal(a.Id, b.OwnerOf("Rover"));
    }

    [Theory]
    [InlineData("", "0.1.12+abc", true)]
    [InlineData("0.1.12", "0.1.12", true)]
    [InlineData("0.1.12", "0.1.12+8b152a3f", true)]
    [InlineData("0.1.12", "0.1.120", false)]
    [InlineData("0.1.12", "0.1.13+8b152a3f", false)]
    [InlineData("0.1.12+8b15", "0.1.12+8b152a3f", true)]
    [InlineData("0.1.12+8b15", "0.1.12+2eeedf83", false)]
    [InlineData("0.1.12", "", false)]
    public void AModIsHeldToTheVersionAnOperatorNames(string wanted, string has, bool fits) =>
        Assert.Equal(fits, ModList.Fits(wanted, has));

    [Fact]
    public void AGameWithAnotherBuildOfAModIsToldWhichOneIsWanted()
    {
        (string[] missing, string[] extra, string[] wrong) = ModList.Against(
            "Core,KSAMultiplayer,FastAndPurrious@0.1.13", "Core@1.0,KSAMultiplayer@0.19.0+f0fd6cb,FastAndPurrious@0.1.12+2eeedf8,KSArmory@0.3.0");
        Assert.Empty(missing);
        Assert.Equal(["KSArmory"], extra);
        Assert.Equal(["FastAndPurrious 0.1.13 (you have 0.1.12)"], wrong);
        // The commit is said only where it is all that differs, and cut to what tells two apart.
        Assert.Equal(["Cars 0.1.12+d2411b6 (you have 0.1.12+6477cbd)"],
                     ModList.Against("Cars@0.1.12+d2411b6", "Cars@0.1.12+6477cbdbabafb5aba007d43ea59aa8311f362a10").Wrong);
        Assert.Equal(["Cars 0.19.1 (you have 0.19.0)"],
                     ModList.Against("Cars@0.19.1+d2411b6", "Cars@0.19.0+6477cbdbabafb5aba007d43ea59aa8311f362a10").Wrong);
        // A world that names no versions takes any, and one that wants nothing takes anything.
        Assert.Empty(ModList.Against("Core,FastAndPurrious", "Core@1.0,FastAndPurrious@0.1.12+2eeedf8").Wrong);
        Assert.Empty(ModList.Against("", "Anything@1").Extra);
    }

    [Fact]
    public void AServerHeldToOneBuildOfAModTurnsAwayAnother()
    {
        using Rig rig = new(new ServerOptions { Mods = "Core,Cars@0.1.13" });
        Client a = rig.Join("Alice", mods: "Core,Cars@0.1.13+8b152a3");
        rig.Until(() => a.Joined, what: "the build that is wanted to be let in");

        Client b = rig.Join("Bob", mods: "Core,Cars@0.1.12+2eeedf8");
        rig.Until(() => b.Out.Length > 0, what: "the other build to be turned away");
        Assert.Contains("Cars 0.1.13 (you have 0.1.12)", b.Out);
    }

    [Fact]
    public void AWorldSomebodyBroughtHoldsNobodyToTheVersionsTheyHad()
    {
        using Rig rig = new();
        Client a = rig.Join("Alice", mods: "Core,Cars@0.1.13+8b152a3");
        rig.Until(() => a.Joined && a.SnapshotWanted is not null, what: "the server to ask Alice for a world");
        a.SnapshotWanted = null;
        a.Upload(new Blob(BlobKind.World, "", a.Id, 5000, Rig.FakeWorld()));
        rig.Until(() => rig.Server.HasWorld, what: "the server to have her world");
        Client b = rig.Join("Bob", mods: "Core,Cars@0.1.12+2eeedf8");
        rig.Until(() => b.Joined, what: "Bob to be let in with another build of the same mod");
        Assert.Equal(["Cars", "Core"], rig.Server.RequiredMods.Order());
    }

    [Fact]
    public void WhatAModSaysOfACraftIsPassedOnFromItsOwnerAndNobodyElse()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        a.Claim("Buggy", controlling: true);
        rig.Until(() => b.OwnerOf("Buggy") == a.Id, what: "Bob to hear of the buggy");

        byte[] steering = [1, 40, 0, 255];
        a.SendExtra("Buggy", "cars:drive/v1", steering);
        rig.Until(() => b.Extras.Count > 0, what: "Bob to be told what the car mod says of it");
        Wire.Extra heard = b.Extras.Dequeue();
        Assert.Equal(("Buggy", "cars:drive/v1", a.Id), (heard.Key, heard.Channel, heard.Owner));
        Assert.Equal(steering, heard.Data);
        Assert.Empty(a.Extras);

        // Said by a game that does not have the craft, it goes nowhere; nor does one too long to be a word.
        b.SendExtra("Buggy", "cars:drive/v1", [9]);
        a.SendExtra("Buggy", "cars:drive/v1", new byte[Wire.ExtraBytes + 1]);
        rig.For(0.5);
        Assert.Empty(a.Extras);
        Assert.Empty(b.Extras);
    }

    [Fact]
    public void ADockingOfTwoCraftThatAreOtherPlayersIsNobodysToSay()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        Client c = JoinAndLoad(rig, "Cas");
        a.Claim("Capsule", controlling: true);
        b.Claim("Station", controlling: true);
        rig.Until(() => c.OwnerOf("Station") == b.Id && c.OwnerOf("Capsule") == a.Id, what: "Cas to hear of both");
        c.SayDocked("Capsule", "Station");
        rig.For(0.5);
        Assert.Equal(a.Id, c.OwnerOf("Capsule"));
        Assert.Equal(b.Id, c.OwnerOf("Station"));
        Assert.Empty(a.Docked);
    }

    [Fact]
    public void ACraftThatUndocksHasItsNameBack()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        Beside(rig, a, "Station", b, "Capsule");
        a.SayDocked("Capsule", "Station");
        rig.Until(() => b.Gone.Contains("Capsule"), what: "the capsule to be gone");

        // Undocked in Alice's game, the capsule is a craft she has just made, under its old name.
        a.Claim("Capsule", controlling: false, made: true);
        rig.Until(() => b.OwnerOf("Capsule") == a.Id, what: "the capsule to be in the world again, Alice's");
        b.Docked.Clear();
        rig.For(1.5);
        // Not said gone again, and Bob asking after it is not told it docked.
        Assert.Equal(a.Id, b.OwnerOf("Capsule"));
        b.Claim("Capsule", controlling: true);
        rig.For(0.5);
        Assert.Empty(b.Docked);
    }

    [Fact]
    public void AnOlderWordOfHowACraftIsBuiltDoesNotReplaceANewerOne()
    {
        // A piece of wreckage is written down twice, two seconds apart, and on a wire that loses
        // datagrams the first can finish arriving after the second. The last to arrive is not
        // the last that was written.
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        byte[] elevenParts = [1, 2, 3, 4], tenParts = [5, 6, 7];
        a.Claim("Piece", controlling: false, made: true);
        a.Upload(new Blob(BlobKind.Vehicle, "Piece", a.Id, 5002, tenParts));
        rig.Until(() => b.Blobs.Any(x => x.Key == "Piece"), what: "Bob to be told how the piece is built now");
        a.Upload(new Blob(BlobKind.Vehicle, "Piece", a.Id, 5000, elevenParts));
        rig.For(1.0);
        Assert.Equal(tenParts, b.Blobs.Last(x => x.Key == "Piece").Data);

        // Nor is it what somebody who joins afterwards is given.
        Client c = JoinAndLoad(rig, "Carol");
        rig.Until(() => c.Blobs.Any(x => x.Key == "Piece"), what: "Carol to get the piece");
        Assert.Equal(tenParts, c.Blobs.Last(x => x.Key == "Piece").Data);
    }

    [Fact]
    public void ANameGivenToANewCraftIsNotSaidGoneAgain()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        a.Claim("Piece", controlling: false, made: true);
        rig.Until(() => b.OwnerOf("Piece") == a.Id, what: "Bob to hear of the piece");
        a.SayGone("Piece");
        rig.Until(() => b.Gone.Contains("Piece"), what: "Bob to hear it is gone");
        a.Claim("Piece", controlling: false, made: true);
        rig.Until(() => b.OwnerOf("Piece") == a.Id, what: "the piece that took the name to be Alice's");
        rig.For(1.5);
        Assert.Equal(1, b.Gone.Count(k => k == "Piece"));
        Assert.Equal(a.Id, b.OwnerOf("Piece"));
    }

    [Fact]
    public void ACraftMadeUnderANameThatHasGoneIsANewCraft()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        a.Claim("Rocket_4", controlling: false, made: true);
        rig.Until(() => b.OwnerOf("Rocket_4") == a.Id, what: "Bob to hear of the piece");
        a.SayGone("Rocket_4");
        rig.Until(() => b.Gone.Contains("Rocket_4"), what: "Bob to hear it is gone");

        // Speaking of the one that went does not bring it back.
        b.Claim("Rocket_4", controlling: false, made: false);
        a.Claim("Marker", controlling: false, made: true);
        rig.Until(() => b.OwnerOf("Marker") == a.Id, what: "a claim made after it to be answered");
        Assert.Equal(0, a.OwnerOf("Rocket_4"));

        // KSA hands a destroyed piece's name to the next piece shed, which is another craft.
        a.Claim("Rocket_4", controlling: false, made: true);
        rig.Until(() => b.OwnerOf("Rocket_4") == a.Id, what: "the piece that took the name to be Alice's");
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

    [Fact]
    public void AnAddressThatCannotHearTheAnswerIsGivenNoPlace()
    {
        using Rig rig = new();
        using Udp raw = new(0, rig.Now, "raw", IPAddress.Loopback);
        IPEndPoint server = new(IPAddress.Loopback, rig.Server.Port);
        Wire.Hello hello = new("Spoofed", "B1", "Core", "SolLite", "", "");

        // Nothing in a datagram says where it really came from, so a hello that carries no cookie
        // buys nothing: sixteen made-up addresses used to fill a server for two minutes at a time,
        // and each place held waited for a world the server then sent to the made-up address.
        raw.Send(hello, server);
        double until = rig.Now() + 1.0;
        rig.Until(() => rig.Now() > until, seconds: 5.0, what: "the server to answer the first hello");
        Assert.Empty(rig.Server.Players);

        // The answer carries a word to say back, and saying it is what holds a place.
        string cookie = "";
        while (raw.TryReceive(out (byte[] Data, IPEndPoint From, double Arrived) d))
        {
            if (Wire.Read(d.Data, out _) is Wire.Welcome w) cookie = w.Cookie;
        }
        Assert.NotEqual("", cookie);

        raw.Send(hello with { Cookie = cookie }, server);
        rig.Until(() => rig.Server.Players.Count == 1, what: "the hello that says the word back");
        Assert.Equal("Spoofed", rig.Server.Players.First().Name);
    }

    [Fact]
    public void AGameCanAskWhichSystemTheWorldIsInWithoutJoining()
    {
        using Rig rig = new();
        IPEndPoint server = new(IPAddress.Loopback, rig.Server.Port);

        // No world yet: the game starting up keeps the system it would have chosen.
        Client early = new(rig.Now);
        rig.Clients.Add(early);
        early.Probe(server);
        rig.Until(() => early.Probed is not null, what: "an answer about a server with no world");
        // The counts and the build come with it from version 23, so a list of servers can be drawn
        // from this answer alone: nobody on it, room for sixteen, no password, and no build wanted
        // because a server with no world takes whichever build brings it one.
        Assert.Equal(new ServerFacts("KSA server", "", false, "", 0, 16, false, ""), early.Probed);

        rig.Server.SetWorld(new Blob(BlobKind.World, "", 0, 0.0, Rig.FakeWorld()), "B1", ["Core", "KSArmory"], "SolLite", timeKnown: false);
        Client later = new(rig.Now);
        rig.Clients.Add(later);
        later.Probe(server);
        rig.Until(() => later.Probed is not null, what: "an answer about a server with a world");
        // The mods come with it, so a player is told what to turn on before KSA has read its own,
        // and so does the build the world was made by, which a player cannot change without
        // starting KSA again -- the one thing on a row worth acting on before clicking it.
        Assert.Equal(new ServerFacts("KSA server", "SolLite", true, "Core,KSArmory", 0, 16, false, "B1"), later.Probed);

        // Asking is not joining: nothing held on the roster, nothing said to anybody.
        rig.For(0.3);
        Assert.Empty(rig.Server.Players);
        Assert.False(later.Joined);
        Assert.Equal("", later.Out);
    }

    [Fact]
    public void ARefusalNamesEveryModTheWorldWantsAndTheGameHasNot()
    {
        using Rig rig = new();
        string[] wanted = [.. Enumerable.Range(1, 20).Select(i => $"SomeMod{i:00}")];
        rig.Server.SetWorld(new Blob(BlobKind.World, "", 0, 0.0, Rig.FakeWorld()), "B1", wanted, "", timeKnown: false);

        Client game = new(rig.Now);
        rig.Clients.Add(game);
        game.Connect(new IPEndPoint(IPAddress.Loopback, rig.Server.Port), "Alpha", "", "B1", "Core", "");
        rig.Until(() => game.Out.Length > 0, what: "a refusal");

        // Every one of them, not the first few: the old field held 200 characters and this list is
        // longer than that, so a player read half a sentence and could not tell what was missing.
        foreach (string mod in wanted) Assert.Contains(mod, game.Out);
    }

    [Fact]
    public void OneNameIsOnARosterHoweverItIsCapitalised()
    {
        using Rig rig = new();
        Client a = rig.Join("laurens");
        rig.Until(() => a.Joined, what: "laurens joining");
        Client b = rig.Join("Laurens");
        rig.Until(() => b.Joined, what: "Laurens joining");

        // Or every operator verb would be a coin toss: they all resolve a name without regard to
        // case, so two of them on one roster made op hand the token to whoever joined first and
        // made deop clear the flag on one of the two while taking the other's row away.
        Assert.Equal("laurens", rig.Server.Players.First(p => p.Id == a.Id).Name);
        Assert.Equal("Laurens 2", rig.Server.Players.First(p => p.Id == b.Id).Name);
    }

    [Fact]
    public void AGameComingBackTakesItsOwnNameBack()
    {
        using Rig rig = new();
        Client a = rig.Join("Bob");
        rig.Until(() => a.Joined, what: "Bob joining");

        // It stops answering without saying goodbye, which is every ungraceful drop: a crash, a
        // kill, a lost Bye. Not disposed, because that says goodbye and is the case that already
        // worked -- this one leaves the server still holding the session.
        //
        // Dropped on the floor rather than merely left unticked: a game that has died is not sending
        // a keepalive either, and the keepalive is what tells a stall from a death. Ticking it would
        // now be a game that is frozen and still here, which is a session the server should keep.
        rig.Clients.Remove(a);
        a.Socket.Loss = 1.0;
        double quiet = rig.Now() + 4.0;
        rig.Until(() => rig.Now() > quiet, seconds: 10.0, what: "the session to go quiet");
        Assert.Single(rig.Server.Players);

        Client back = rig.Join("Bob");
        rig.Until(() => back.Joined, what: "Bob joining again");
        // Not "Bob 2", and not beside its own ghost: one session, under the name it asked for.
        Assert.Single(rig.Server.Players);
        Assert.Equal("Bob", rig.Server.Players.First().Name);
        a.Dispose();
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
    public void AServerHoldsPlayersToTheBuildItWasBuiltFor()
    {
        using Rig rig = new(new ServerOptions { BuiltFor = "B1" });
        // Before anybody has joined or brought a world, which is when a list of servers asks.
        Assert.Equal("B1", rig.Server.RequiredBuild);
        Client asking = new(rig.Now);
        rig.Clients.Add(asking);
        asking.Probe(new IPEndPoint(IPAddress.Loopback, rig.Server.Port));
        rig.Until(() => asking.Probed is not null, what: "an answer from a server with no world");
        Assert.Equal("B1", asking.Probed!.KsaBuild);

        Client b = rig.Join("Bob", "B2");
        rig.Until(() => b.Out.Length > 0, what: "another build to be turned away from an empty server");
        Assert.Contains("this server is for KSA B1 and you run B2", b.Out);
    }

    [Fact]
    public void AWorldSavedByAnotherBuildDoesNotMoveWhatAServerWasBuiltFor()
    {
        // The day after KSA updates: the world on disk was saved by the old build, and the server
        // has been brought up to the release made for the new one.
        using Rig rig = new(new ServerOptions { BuiltFor = "B2" });
        rig.Server.SetWorld(new Blob(BlobKind.World, "", 0, 0.0, Rig.FakeWorld()), "B1", ["Core"], "SolLite", timeKnown: false);
        Assert.Equal("B2", rig.Server.RequiredBuild);
        Client a = rig.Join("Alice", "B2");
        rig.Until(() => a.Joined, what: "a player on the build the server is for to be let in");
    }

    [Fact]
    public void AnOperatorsBuildIsTakenOverTheOneAServerWasBuiltFor()
    {
        using Rig rig = new(new ServerOptions { BuiltFor = "B1", KsaBuild = "B9" });
        Assert.Equal("B9", rig.Server.RequiredBuild);
        // And a setting changed while it runs does not lose what it was built for.
        rig.Server.Reconfigure(new ServerOptions { Port = rig.Server.Port });
        Assert.Equal("B1", rig.Server.RequiredBuild);
    }

    [Fact]
    public void ABuildIsHeldToFromTheFirstPlayerInAndNotFromTheirWorldArriving()
    {
        using Rig rig = new();
        Client a = rig.Join("Alice", "B1");
        rig.Until(() => a.Joined && a.SnapshotWanted is not null, what: "the server to ask Alice for a world");
        Assert.False(rig.Server.HasWorld);
        Assert.Equal("B1", rig.Server.RequiredBuild);

        Client b = rig.Join("Bob", "B2");
        rig.Until(() => b.Out.Length > 0, what: "another build to be turned away while the world is still on its way");
        Assert.Contains("you need the same build", b.Out);
        Assert.False(b.Joined);
    }

    [Fact]
    public void AnEmptyWorldOfTheServersOwnIsHeldToTheBuildOfWhoeverIsInIt()
    {
        using Rig rig = new(new ServerOptions { EmptyWorld = "SolLite" });
        rig.Server.SetWorld(new Blob(BlobKind.World, "", 0, 0.0, StockWorld.Zip("SolLite")!), "", [], "SolLite", timeKnown: false);
        Assert.Equal("", rig.Server.RequiredBuild);
        Client a = rig.Join("Alice", "B1");
        rig.Until(() => a.Joined, what: "Alice to be let into the empty world");

        Client b = rig.Join("Bob", "B2");
        rig.Until(() => b.Out.Length > 0, what: "another build to be turned away");
        Assert.Contains("you need the same build", b.Out);
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
    public void AWarpGoesWithWhoeverAskedForIt()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        a.AskWarp(5.0);
        rig.Until(() => b.WarpWantsAnswer, what: "Bob to be asked");
        Assert.Equal(Wire.WarpPhase.Asking, rig.Server.Warp.Phase);

        // Whoever asked drops. The question was theirs, so it goes with them rather than standing in
        // the name of a player who is not there -- which left the next answer from anybody ending
        // "somebody's warp", and a game that reconnected saying no to a warp its own lost session
        // had asked for.
        a.Dispose();
        rig.Clients.Remove(a);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.None, what: "the ask to go with them");
        Assert.Equal(1.0, rig.Server.UniverseClock.Speed, 3);
        Assert.DoesNotContain(rig.Server.Said, l => l.Contains("somebody"));
    }

    [Fact]
    public void TheClockKeepsUpWhenTheWholeRoomHasRunPastIt()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        // What the end of a warp leaves behind. A game cannot learn the warp has stopped until the
        // word reaches it and runs at the old speed until then, so at 1000x the round trip is
        // hundreds of universe-seconds of overshoot -- made by the ending, not present before it.
        (a.Late, b.Late) = (-400.0, -300.0);
        rig.Until(() => rig.Server.Players.Count(p => p.Late < -200.0) == 2,
                  what: "the server to hear that both have run past it");

        double before = rig.Server.UniverseClock.Now(rig.Now());
        // The least-ahead game is what it moves to, so it passes nobody: Bob's 300, not Alice's 400.
        rig.Until(() => rig.Server.UniverseClock.Now(rig.Now()) - before >= 299.0,
                  what: "the clock to keep up with the room");
        Assert.True(rig.Server.UniverseClock.Now(rig.Now()) - before < 400.0,
                    "the clock passed the game that was least far ahead");
    }

    [Fact]
    public void TheClockIgnoresOneGameThatHasRunAhead()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        // One game ahead and one behind is one game with a problem, not a clock with one. Moving
        // for it would drag everybody else further back, and a game cannot rewind to follow.
        (a.Late, b.Late) = (-4000.0, 20.0);
        rig.Until(() => rig.Server.Players.Any(p => p.Late < -1000.0), what: "the server to hear it");
        double before = rig.Server.UniverseClock.Now(rig.Now());
        rig.For(2.5);
        Assert.True(rig.Server.UniverseClock.Now(rig.Now()) - before < 100.0,
                    "one game's word moved the clock");
    }

    [Fact]
    public void AServerAsksAgainForAWorldItWasNeverSent()
    {
        using Rig rig = new();
        Client a = rig.Join("Alice");
        rig.Until(() => a.Joined && a.SnapshotWanted is not null, what: "the server to ask for a world");
        Assert.False(a.ServerHasWorld);

        // As a game that never heard the ask: it is one datagram, sent on the heels of the welcome,
        // and one lost or arriving ahead of that welcome is gone. Said once, the game sits at
        // "sending your world" with nothing to send it for and the server waits on it for good.
        a.SnapshotWanted = null;
        rig.Until(() => a.SnapshotWanted is not null, seconds: 8.0, what: "the server to ask again for a world");

        // And not once more after the world has come.
        a.SnapshotWanted = null;
        a.Upload(new Blob(BlobKind.World, "", a.Id, 5000, Rig.FakeWorld()));
        rig.Until(() => rig.Server.HasWorld, what: "the world to arrive");
        rig.For(4.0);
        Assert.Null(a.SnapshotWanted);
    }

    [Fact]
    public void TheEndOfAWarpIsSaidMoreThanOnce()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        a.AskWarp(5.0);
        rig.Until(() => b.WarpWantsAnswer, what: "Bob to be asked");
        b.VoteWarp(true);
        rig.Until(() => b.Warp is { Phase: Wire.WarpPhase.Running }, what: "Bob to be told it runs");

        // Whatever the server says as the warp ends is lost.
        rig.Server.Socket.Loss = 1.0;
        a.AskWarp(1.0);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.None, what: "the warp to end");
        rig.For(0.3);
        rig.Server.Socket.Loss = 0.0;

        // A game that missed it is told again, rather than left showing a warp nobody is in.
        rig.Until(() => b.Warp is null, seconds: 4.0, what: "Bob to hear that the warp has ended");
    }

    [Fact]
    public void AGameSaysItIsInTheWorldUntilTheServerKnows()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = rig.Join("Bob");
        rig.Until(() => b.Blobs.Any(x => x.Kind == BlobKind.World), seconds: 20, what: "Bob to get the world");

        // The one datagram that says so is lost.
        b.Socket.Loss = 1.0;
        b.SayLoaded(5000.0);
        rig.For(0.3);
        b.Socket.Loss = 0.0;
        rig.Until(() => rig.Server.Players.First(p => p.Id == b.Id).Loaded, seconds: 8.0, what: "the server to hear that Bob is in the world");
    }

    [Fact]
    public void AfterAWarpEndsTheClockGoesOnToTheGameFurthestPastIt()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        (a.Can, b.Can) = (2000.0, 2000.0);
        rig.Until(() => rig.Server.Players.All(p => p.Can > 1000.0), seconds: 10.0, what: "both to say what they can fly");

        a.AskWarp(1000.0);
        rig.Until(() => b.WarpWantsAnswer, what: "Bob to be asked");
        b.VoteWarp(true);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.Running, what: "the warp to run");
        a.AskWarp(1.0);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.None, what: "the warp to end");

        // As two games were measured at the end of one: each ran on at 1000x until it heard, and
        // one heard a sixth of a second after the other. To the nearer of them, and Alice stands
        // still for 158 seconds while a clock at normal speed walks up to her. To the further, and
        // Bob has 158 seconds to catch up, which on rails is a moment.
        (double began, double before) = (rig.Now(), rig.Server.UniverseClock.Now(rig.Now()));
        double on = 0.0;
        while (rig.Now() - began < 4.0)
        {
            // What the clock has gone on by, past what its own speed carried it: each game is that
            // much less far past it, and says so, as a game working its lateness out afresh does.
            on = rig.Server.UniverseClock.Now(rig.Now()) - before - (rig.Now() - began);
            (a.Late, b.Late) = (-(359.0 - on), -(201.0 - on));
            rig.Tick();
            Thread.Sleep(1);
        }
        Assert.InRange(on, 350.0, 365.0);
    }

    [Fact]
    public void AGameTakesAChangeOfTheServersSpeedAsSaid()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        a.Can = 2000.0;
        rig.Until(() => rig.Server.Players.All(p => p.Can > 1000.0), seconds: 10.0, what: "Alice to say what she can fly");
        rig.Until(() => a.ServerUniverse is not null, seconds: 10.0, what: "Alice to know the server's clock");

        // Up: within a word or two of the server saying so, and not eased towards over a second.
        a.AskWarp(1000.0);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.Running, what: "the warp to run");
        rig.For(0.5);
        Assert.InRange(a.ServerUniverse!.Value.Rate, 900.0, 1100.0);

        // And down, which is the one that matters: a game that goes on believing in a warp that
        // has ended runs on at it, and is then that far past a clock at normal speed.
        rig.For(1.5);
        a.AskWarp(1.0);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.None, what: "the warp to end");
        rig.For(0.5);
        Assert.InRange(a.ServerUniverse!.Value.Rate, 0.5, 2.5);
    }

    [Fact]
    public void TheClockLeavesAnOrdinaryWobbleAlone()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        // Inside the band ClockFollow nudges across on its own. A clock that chased this would be
        // chasing every frame's jitter instead of avoiding a standstill.
        (a.Late, b.Late) = (-1.0, -1.5);
        rig.Until(() => rig.Server.Players.Any(p => p.Late <= -1.0), what: "the server to hear it");
        double before = rig.Server.UniverseClock.Now(rig.Now());
        rig.For(2.5);
        Assert.True(rig.Server.UniverseClock.Now(rig.Now()) - before < 10.0, "the clock chased a wobble");
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
        Assert.Equal(5.0, rig.Server.WarpWanted, 3);
        rig.Until(() => a.Warp is { Phase: Wire.WarpPhase.Running }, what: "Alice to be told");
        // Nobody had said what they can fly, so it gets there by climbing.
        rig.Until(() => Math.Abs(rig.Server.UniverseClock.Speed - 5.0) < 0.001, seconds: 15.0, what: "the warp to reach what was agreed");
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
        rig.Until(() => Math.Abs(rig.Server.UniverseClock.Speed - 10.0) < 0.001, seconds: 20.0, what: "the warp to change");

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
        Assert.Equal(4.0, rig.Server.WarpWanted, 3);
        rig.Until(() => Math.Abs(rig.Server.UniverseClock.Speed - 4.0) < 0.001, seconds: 15.0, what: "the warp to reach the number he agreed to");
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

        // Alice has said nothing of what she can fly, so it climbs to the six and stops there.
        rig.Until(() => Math.Abs(rig.Server.UniverseClock.Speed - 6.0) < 0.1, seconds: 15.0, what: "the warp to reach six");
        rig.For(2.5);
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
        Assert.Equal(5.0, rig.Server.WarpWanted, 3);
        Assert.InRange(rig.Server.UniverseClock.Speed, 1.0, 5.0);

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
    public void ATraceWritesAChangeOfRateFromBothSides()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        // A machine that has said what it can fly, so the warp is run at what is asked in one change.
        a.Can = 60.0;
        rig.Until(() => rig.Server.Players.All(p => p.Can > 50.0), seconds: 10.0, what: "Alice to say what she can fly");

        // Nothing written for being due, past the first line: what is left is what a change writes.
        StringWriter written = new();
        ClockTrace trace = new(written, everySeconds: 1000.0);
        void Tick()
        {
            trace.Before(rig.Server, rig.Now(), rig.Now());
            rig.Server.Tick();
            trace.After(rig.Server, rig.Now(), rig.Now());
            foreach (Client c in rig.Clients) c.Update();
            Thread.Sleep(1);
        }

        for (int i = 0; i < 50; i++) Tick();
        // Alone in the room, so asking is everybody agreeing.
        a.AskWarp(50.0);
        double until = rig.Now() + 5.0;
        while (rig.Server.Warp.Phase != Wire.WarpPhase.Running && rig.Now() < until) Tick();
        for (int i = 0; i < 50; i++) Tick();

        System.Text.Json.JsonElement[] lines = [.. written.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)];
        Assert.Equal(3, lines.Length);
        (System.Text.Json.JsonElement before, System.Text.Json.JsonElement after) = (lines[1], lines[2]);
        Assert.Equal("None", before.GetProperty("phase").GetString());
        Assert.Equal(1.0, before.GetProperty("rate").GetDouble());
        Assert.Equal("Running", after.GetProperty("phase").GetString());
        Assert.Equal(50.0, after.GetProperty("rate").GetDouble());
        Assert.Equal("Alice", after.GetProperty("who").GetString());
        // A tick apart, so the clock is known to within a tick of the faster rate across the change:
        // sampled on a timer alone, the two lines either side of it would be a quarter of a second
        // of 50x apart, with nothing to say where in it the rate changed.
        double apart = after.GetProperty("t").GetDouble() - before.GetProperty("t").GetDouble();
        Assert.InRange(apart, 0.0, 0.05);
        Assert.InRange(after.GetProperty("universe").GetDouble() - before.GetProperty("universe").GetDouble(), 0.0, (apart * 50.0) + 0.01);
        Assert.Equal("Alice", after.GetProperty("players")[0].GetProperty("name").GetString());
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
        rig.Until(() => a.Swept.Count == 1 && b.Swept.Count == 1, what: "both games to be told");
        // The name, not the list: a game sweeps its own world by it, since most of what is out
        // there was claimed by nobody and the server has never heard of it.
        Assert.Equal("Stage *", a.Swept.Peek());
        Assert.True(Wire.Matches("Stage 2", a.Swept.Peek()));
        Assert.False(Wire.Matches("Lander", a.Swept.Peek()));

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
    public void ClearingTheWorldKeepsTheClearedSaveAndCostsNobodyAReload()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        a.Claim("Stage 1", controlling: false, made: true);
        rig.Until(() => b.OwnerOf("Stage 1") == a.Id, what: "the owners table");
        a.Blobs.Clear();
        b.Blobs.Clear();

        // A sweep asks whoever has been in longest for a save, since the world on disk still holds
        // what was swept. That save comes from a game that has already swept, so it is clean.
        Assert.Single(rig.Server.Sweep());
        rig.Until(() => a.SnapshotWanted is not null, what: "Alice to be asked for a save of the cleared world");

        a.SnapshotWanted = null;
        a.Upload(new Blob(BlobKind.World, "", a.Id, 6000, Rig.FakeWorld(20)));
        rig.Until(() => rig.Server.World!.Universe == 6000, seconds: 20, what: "the cleared world to be kept");

        // And nobody is put back on it: everybody is standing in a world they swept themselves, so a
        // clear costs no reload and leaves the clock alone. Only reload moves anybody.
        rig.For(1.0);
        Assert.DoesNotContain(a.Blobs, x => x.Kind == BlobKind.World);
        Assert.DoesNotContain(b.Blobs, x => x.Kind == BlobKind.World);
        Assert.DoesNotContain(rig.Server.Players, p => !p.Loaded);
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
        // Swept by name all the same, so a game can take away what the server never knew of: what
        // is being flown is spared at both ends.
        rig.Until(() => a.Swept.Count > 0, what: "the game to be told to sweep");
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

    [Fact]
    public void AWarpPutToARoomThatHasSaidNothingStartsAStepAboveNormalAndClimbs()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        // Neither machine has said what it can fly, which is what every machine says at normal speed.
        double before = rig.Server.UniverseClock.Now(rig.Now());
        a.AskWarp(1000.0);
        rig.Until(() => b.WarpWantsAnswer, what: "Bob to be asked");
        b.VoteWarp(true);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.Running, what: "the warp to run");
        Assert.InRange(rig.Server.UniverseClock.Speed, 1.0, 2.0);

        // A second in, the clock is seconds on and not a thousand of them: run at what was asked,
        // that second was the whole of the gap no game ever closed while the warp was worth having.
        rig.For(1.0);
        Assert.InRange(rig.Server.UniverseClock.Now(rig.Now()) - before, 0.0, 10.0);

        // And it gets there, a step at a time, with nobody asking again.
        rig.Until(() => rig.Server.UniverseClock.Speed > 5.0, seconds: 10.0, what: "the warp to climb");
        Assert.Equal(Wire.WarpPhase.Running, rig.Server.Warp.Phase);
    }

    [Fact]
    public void AWarpIsNotCutAgainWhileTheGameBehindIsCatchingUp()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        (a.Can, b.Can) = (60.0, 60.0);
        rig.Until(() => rig.Server.Players.All(p => p.Can > 50.0), seconds: 10.0, what: "both to say what they can fly");

        a.AskWarp(50.0);
        rig.Until(() => b.WarpWantsAnswer, what: "Bob to be asked");
        b.VoteWarp(true);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.Running, what: "the warp to run");
        Assert.Equal(50.0, rig.Server.UniverseClock.Speed, 1);

        // Bob is two thousand universe-seconds behind, forty of his own, and making up three hundred
        // of them every second. Falling behind is worth one step back. Catching up is worth none:
        // the gap he has left is longer in the seconds of a slower clock, so a second step back
        // makes the case for a third, and the room ends at normal speed still waiting for him.
        for (int i = 0; i < 6; i++)
        {
            b.Late = 2000.0 - (300.0 * i);
            rig.For(1.0);
        }
        Assert.Equal(Wire.WarpPhase.Running, rig.Server.Warp.Phase);
        Assert.InRange(rig.Server.UniverseClock.Speed, 29.0, 50.0);
    }

    [Fact]
    public void AGameGainingTooSlowlyToEverBeLevelIsStillBackedOffFor()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        (a.Can, b.Can) = (60.0, 60.0);
        rig.Until(() => rig.Server.Players.All(p => p.Can > 50.0), seconds: 10.0, what: "both to say what they can fly");

        a.AskWarp(50.0);
        rig.Until(() => b.WarpWantsAnswer, what: "Bob to be asked");
        b.VoteWarp(true);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.Running, what: "the warp to run");

        // A hundred of his own seconds behind and gaining three universe-seconds a second: at that
        // pace he is level in half an hour. That is a machine at its limit, not one catching up,
        // and the only thing that helps it is a slower clock.
        for (int i = 0; i < 6; i++)
        {
            b.Late = 5000.0 - (3.0 * i);
            rig.For(1.0);
        }
        Assert.InRange(rig.Server.UniverseClock.Speed, 1.0, 20.0);
    }

    [Fact]
    public void AWarpClimbsNoFurtherWhileSomebodyIsBeingLeftBehind()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");

        a.AskWarp(100.0);
        rig.Until(() => b.WarpWantsAnswer, what: "Bob to be asked");
        b.VoteWarp(true);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.Running, what: "the warp to run");

        // Bob loses a universe-second every second, which at this speed is most of what he is being
        // asked for, and has said nothing of what he can fly: a game finds that out over a couple of
        // seconds, and the server can see it in two pings. Not yet far enough behind to back off
        // for, and no reason to ask him for more.
        for (int i = 1; i <= 5; i++)
        {
            b.Late = i;
            rig.For(1.0);
        }
        Assert.Equal(Wire.WarpPhase.Running, rig.Server.Warp.Phase);
        // One step is allowed for: the first look may come before his first word.
        Assert.InRange(rig.Server.UniverseClock.Speed, 1.0, 2.3);
    }

    [Fact]
    public void AMachineSayingItFliesALittleLessIsNoCeiling()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        (a.Can, b.Can) = (10.0, 10.0);
        rig.Until(() => rig.Server.Players.All(p => p.Can > 9.5), seconds: 10.0, what: "both to say what they can fly");

        // Twenty asked, ten flown, because ten is what the room says it flies.
        a.AskWarp(20.0);
        rig.Until(() => b.WarpWantsAnswer, what: "Bob to be asked");
        b.VoteWarp(true);
        rig.Until(() => rig.Server.Warp.Phase == Wire.WarpPhase.Running, what: "the warp to run");
        Assert.Equal(10.0, rig.Server.UniverseClock.Speed, 1);

        // Bob says nine. That is not a machine giving out, and the world stays where it is.
        b.Can = 9.0;
        rig.For(2.5);
        Assert.Equal(10.0, rig.Server.UniverseClock.Speed, 1);

        // Then both are comfortable and say nothing, and there is twenty still to climb to. Read as
        // a fall, the nine left a ceiling at eight that was nobody's to lift, and each look after
        // it stepped down to the ceiling the last one had left.
        (a.Can, b.Can) = (0.0, 0.0);
        rig.Until(() => rig.Server.UniverseClock.Speed > 12.0, seconds: 10.0, what: "the warp to climb on");
    }
}
