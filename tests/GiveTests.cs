using KSAMultiplayer.Server;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// Who is spectating whom, in every game's roster, and a craft handed by its pilot to a player
/// spectating it: theirs at once, and nobody else's to give or to be given.
/// </summary>
public class GiveTests
{
    private static Client Seed(Rig rig, string name = "Alice")
    {
        Client a = rig.Join(name);
        rig.Until(() => a.Joined && a.SnapshotWanted is not null, what: "the server to ask for a world");
        a.SnapshotWanted = null;
        a.Upload(new Blob(BlobKind.World, "", a.Id, 5000, Rig.FakeWorld()));
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

    // Alice flies a rocket, and Bob's camera is on it.
    private static (Client Alice, Client Bob) BobSpectatesAlice(Rig rig)
    {
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        a.Watching = "Rocket";
        a.Claim("Rocket", controlling: true, made: false);
        rig.Until(() => rig.Server.OwnerOf("Rocket") == a.Id, what: "Alice to fly the rocket");
        b.Watching = "Rocket";
        rig.Until(() => a.Players.Any(p => p.Id == b.Id && p.Watching == "Rocket"), what: "Alice's game to see Bob watching");
        return (a, b);
    }

    [Fact]
    public void EveryGameSeesWhatEachPlayerIsWatching()
    {
        using Rig rig = new();
        (Client a, Client b) = BobSpectatesAlice(rig);
        rig.Until(() => b.Players.Any(p => p.Id == a.Id && p.Watching == "Rocket" && p.Controlling == "Rocket"), what: "Bob's game to see Alice in her rocket");
        Assert.Contains(a.Players, p => p.Id == b.Id && p.Watching == "Rocket" && p.Controlling == "");
    }

    [Fact]
    public void APilotGivesTheirCraftToAPlayerSpectatingIt()
    {
        using Rig rig = new();
        (Client a, Client b) = BobSpectatesAlice(rig);

        a.Give("Rocket", b.Id);
        rig.Until(() => b.Given.Count > 0, what: "Bob's game to be told");
        Assert.Equal(("Rocket", a.Id), b.Given.Dequeue());
        // His at once in his own game, before the owners the server sends next.
        Assert.Equal(b.Id, b.OwnerOf("Rocket"));
        Assert.Equal(b.Id, rig.Server.OwnerOf("Rocket"));
        rig.Until(() => a.OwnerOf("Rocket") == b.Id, what: "Alice's game to see it is Bob's");
        Assert.Contains(rig.Server.Said, s => s.Contains("Alice gave Rocket to Bob"));
    }

    [Fact]
    public void NobodyIsGivenACraftTheyAreNotWatching()
    {
        using Rig rig = new();
        (Client a, Client b) = BobSpectatesAlice(rig);
        b.Watching = "";
        rig.Until(() => rig.Server.Players.First(p => p.Id == b.Id).Watching == "", what: "Bob to look away");

        a.Give("Rocket", b.Id);
        rig.Until(() => a.Chat.Any(l => l.Text.Contains("isn't spectating Rocket")), what: "Alice to be told why not");
        Assert.Equal(a.Id, rig.Server.OwnerOf("Rocket"));
        Assert.Empty(b.Given);
    }

    /// <summary>A game still catching up after joining flies the past, and the craft stands still in every other world.</summary>
    [Fact]
    public void NobodyIsGivenACraftWhileTheirGameIsCatchingUp()
    {
        using Rig rig = new();
        (Client a, Client b) = BobSpectatesAlice(rig);
        b.Late = 3.0;
        rig.Until(() => rig.Server.Players.First(p => p.Id == b.Id).Late > 2.0, what: "Bob's game to say it is behind");

        a.Give("Rocket", b.Id);
        rig.Until(() => a.Chat.Any(l => l.Text.Contains("still catching up")), what: "Alice to be told to wait");
        Assert.Equal(a.Id, rig.Server.OwnerOf("Rocket"));

        b.Late = 0.0;
        rig.Until(() => rig.Server.Players.First(p => p.Id == b.Id).Late < 0.5, what: "Bob's game to catch up");
        a.Give("Rocket", b.Id);
        rig.Until(() => rig.Server.OwnerOf("Rocket") == b.Id, what: "the rocket to be Bob's");
    }

    [Fact]
    public void NobodyGivesAwayACraftTheyAreNotFlying()
    {
        using Rig rig = new();
        (Client a, Client b) = BobSpectatesAlice(rig);
        Client c = JoinAndLoad(rig, "Carol");
        c.Watching = "Rocket";
        rig.For(0.5);

        // Bob, only watching, offers Alice's rocket to Carol.
        b.Give("Rocket", c.Id);
        rig.Until(() => b.Chat.Any(l => l.Text.Contains("only give the craft you're flying")), what: "Bob to be told no");
        Assert.Equal(a.Id, rig.Server.OwnerOf("Rocket"));
        Assert.Empty(c.Given);
    }
}
