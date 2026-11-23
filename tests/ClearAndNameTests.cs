using KSAMultiplayer.Server;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// What a clear takes and whom it reaches, a craft made under another game's name, and how much of
/// a player's line is lost.
/// </summary>
public class ClearAndNameTests
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

    /// <summary>
    /// A clear takes what a player's game shed and what they took for being near, and leaves a
    /// craft they hold and have flown: at one session's last clear that was a player's own ship,
    /// parked while they flew another.
    /// </summary>
    [Fact]
    public void AClearSparesACraftAPlayerHoldsAndHasFlown()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        a.Claim("Rocket", controlling: true, made: false);
        rig.Until(() => rig.Server.Players.First(p => p.Id == a.Id).Controlling == "Rocket", what: "Alice to fly the rocket");
        a.Claim("Lander", controlling: true, made: false);
        a.Claim("Debris", controlling: false, made: true);
        rig.Until(() => rig.Server.Players.First(p => p.Id == a.Id).Controlling == "Lander" && rig.Server.OwnerOf("Debris") == a.Id,
                  what: "Alice to fly the lander and hold the debris");

        rig.Server.Sweep();
        rig.Until(() => b.Swept.Count == 1, what: "Bob's game to be told to sweep");
        Assert.Contains("Rocket", b.Swept.Peek().Spared);
        Assert.DoesNotContain("Debris", b.Swept.Peek().Spared);
        Assert.Equal(a.Id, rig.Server.OwnerOf("Rocket"));
        Assert.Equal(0, rig.Server.OwnerOf("Debris"));
        Assert.Contains(rig.Server.Said, s => s.Contains("spared, as their players' own: Alice's Rocket"));
    }

    /// <summary>
    /// A game sent the world the same second as a clear is told of the clear once it is in: the
    /// world it loaded still has everything the clear took.
    /// </summary>
    [Fact]
    public void AGameStillLoadingIsToldOfAClearOnceItIsIn()
    {
        using Rig rig = new();
        Seed(rig);
        Client b = rig.Join("Bob");
        rig.Until(() => b.Blobs.Any(x => x.Kind == BlobKind.World), seconds: 20, what: "Bob to get the world");
        rig.Server.Sweep("Stage *");
        rig.For(1.0);
        Assert.Empty(b.Swept);

        b.SayLoaded(b.Blobs.First(x => x.Kind == BlobKind.World).Universe);
        rig.Until(() => b.Swept.Count == 1, what: "Bob's game to be told of the clear");
        Assert.Equal("Stage *", b.Swept.Peek().Like);
    }

    /// <summary>
    /// Two games' debris come out under one name, since KSA numbers what each game sheds by itself.
    /// The second game's is a craft of its own, not the first's taken over in every world.
    /// </summary>
    [Fact]
    public void ACraftMadeUnderTheNameOfAnotherGamesIsGivenItsOwn()
    {
        using Rig rig = new(new ServerOptions { LapseWithin = 1.0, WatchToTake = 0.5 });
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        a.Claim("Piece_1", controlling: false, made: true);
        rig.Until(() => rig.Server.OwnerOf("Piece_1") == a.Id, what: "Alice to hold her piece");
        a.Upload(new Blob(BlobKind.Vehicle, "Piece_1", a.Id, 5001, Rig.FakeWorld(2)));
        rig.Until(() => rig.Server.Said.Any(s => s.Contains("says how Piece_1 is built")), what: "the piece to be described");
        // Nobody's, as a piece is once its owner has walked away from it and somebody looks at it.
        a.Claim("Lander", controlling: true, made: false);
        b.Watching = "Piece_1";
        rig.Until(() => rig.Server.OwnerOf("Piece_1") == 0, seconds: 10.0, what: "the piece to be let go of");

        b.Claim("Piece_1", controlling: false, made: true);
        rig.Until(() => b.Renamed.Count == 1, what: "Bob's piece to be given a name of its own");
        Assert.Equal("Piece_1", b.Renamed.Peek().Was);
        Assert.Contains(rig.Server.Said, s => s.Contains("a craft Alice made is already called Piece_1"));

        // Alice's game making it again is her piece carrying on.
        a.Claim("Piece_1", controlling: false, made: true);
        rig.Until(() => rig.Server.OwnerOf("Piece_1") == a.Id, what: "Alice to have her piece back");
    }

    [Fact]
    public void APlayersLostPingsAreCounted()
    {
        PingLoss loss = new();
        Assert.True(double.IsNaN(loss.Share));
        // Fifty-one pings sent a second apart, every fifth lost.
        double sent = 1000.0;
        for (int i = 0; i < 51; i++, sent += 1.0) if (i % 5 != 4) loss.Heard(sent, sent);
        Assert.InRange(loss.Share, 0.19, 0.21);

        // A game that stopped for half a minute, loading, lost nothing on the way.
        PingLoss stalled = new();
        for (int i = 0; i < 40; i++) stalled.Heard(2000.0 + i, 2000.0 + i);
        stalled.Heard(2070.0, 2070.0);
        Assert.Equal(0.0, stalled.Share);
    }

    /// <summary>
    /// A protected station whose owner has left is held by the game near it, and goes off in that
    /// game's physics at once. That game's word is not taken: the station stays in every other world
    /// and on the list. A minute on, the game holding it is its owner like any other.
    /// </summary>
    [Fact]
    public void AProtectedCraftJustTakenOverIsNotGoneOnItsNewHoldersWord()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        Client c = JoinAndLoad(rig, "Carol");
        a.Claim("Station", controlling: false, made: false);
        rig.Until(() => rig.Server.OwnerOf("Station") == a.Id, what: "Alice to hold the station");
        Assert.Contains("Station is now protected", rig.Server.Protect("Station"));

        a.Dispose();
        rig.Until(() => rig.Server.OwnerOf("Station") == 0, seconds: 15, what: "Alice's station to be nobody's");
        b.Claim("Station", controlling: false, made: false, near: true);
        rig.Until(() => rig.Server.OwnerOf("Station") == b.Id, what: "Bob to hold the station for being near");

        b.SayGone("Station");
        rig.Until(() => rig.Server.Said.Any(s => s.Contains("it is protected, so it stays in every other world")), what: "the refusal");
        rig.For(1.0);
        Assert.True(rig.Server.Protected.Has("Station"));
        Assert.DoesNotContain("Station", c.Gone);

        // Held outright for a minute, it is Bob's like any craft, and his word stands.
        b.Claim("Station", controlling: true, made: false);
        rig.Until(() => rig.Server.OwnerOf("Station") == b.Id, what: "Bob to hold the station again");
        // A second at a time, so that nobody goes quiet.
        for (int i = 0; i < 61; i++)
        {
            rig.Skip(1.0, real: 0.0);
            rig.For(0.01);
        }
        b.SayGone("Station");
        rig.Until(() => c.Gone.Contains("Station"), what: "Carol to hear it is gone");
        Assert.False(rig.Server.Protected.Has("Station"));
    }
}
