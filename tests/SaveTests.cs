using KSAMultiplayer.Server;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// Worlds a server keeps under names: an admin saves the one being played, and loads one back over
/// it for everybody. The list of them reaches an admin's game without its asking, and nobody else's.
/// </summary>
public class SaveTests
{
    // One world of each size, made once: a zip carries the time it was made to the two seconds, so
    // the same world made again is not always the same bytes, and these tests compare them.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> Worlds = new();

    private static byte[] World(int kb = 40) => Worlds.GetOrAdd(kb, Rig.FakeWorld);

    internal static Client Seed(Rig rig, string name = "Alice", double universe = 5000, int kb = 40)
    {
        Client a = rig.Join(name);
        rig.Until(() => a.Joined && a.SnapshotWanted is not null, what: "the server to ask for a world");
        a.SnapshotWanted = null;
        a.Upload(new Blob(BlobKind.World, "", a.Id, universe, World(kb)));
        rig.Until(() => rig.Server.HasWorld && rig.Server.Players.First(p => p.Id == a.Id).Loaded, what: "the world to arrive");
        return a;
    }

    internal static Client JoinAndLoad(Rig rig, string name, string system = "SolLite", string mods = "Core")
    {
        Client b = rig.Join(name, system: system, mods: mods);
        rig.Until(() => b.Blobs.Any(x => x.Kind == BlobKind.World), seconds: 20, what: $"{name} to get the world");
        b.SayLoaded(b.Blobs.First(x => x.Kind == BlobKind.World).Universe);
        b.Blobs.Clear();
        rig.Until(() => rig.Server.Players.First(p => p.Id == b.Id).Loaded, what: $"{name} to load");
        return b;
    }

    // Answers the server's ask for a save as a game does: with the world as that game now has it.
    private static void HandOver(Rig rig, Client c, double universe, int kb)
    {
        rig.Until(() => c.SnapshotWanted is not null, what: "the game to be asked for a save");
        c.SnapshotWanted = null;
        c.Upload(new Blob(BlobKind.World, "", c.Id, universe, World(kb)));
    }

    [Fact]
    public void AListOfSavesSurvivesTheWire()
    {
        SavedWorld[] said =
        [
            new("Before the landing", new DateTime(2026, 11, 16, 15, 39, 10, DateTimeKind.Utc), 197738.2, 43494, "SolLite", "2026.11.10.5801", "Onyx", false),
            new(SaveShelf.AutoPrefix + "2026-11-05 21-35-07", new DateTime(2026, 11, 5, 21, 35, 7, DateTimeKind.Utc), 0.0, 16455, "", "", "", true),
        ];
        Assert.Equal(said, SaveList.Decode(SaveList.Encode(said, serial: 7), out uint serial));
        Assert.Equal(7u, serial);
        Assert.Empty(SaveList.Decode(SaveList.Encode([]))!);
        // Cut short, or in a form from some later version: nothing, rather than half a list.
        Assert.Null(SaveList.Decode(SaveList.Encode(said)[..^3]));
        Assert.Null(SaveList.Decode([9, 0, 0, 0, 0, 0, 0]));
        Assert.Null(SaveList.Decode([]));
    }

    [Theory]
    [InlineData("Before the landing", "Before the landing")]
    [InlineData("  Mun base (v2)  ", "Mun base (v2)")]
    [InlineData("\"quoted name\"", "quoted name")]
    [InlineData("", null)]
    [InlineData("../world", null)]
    [InlineData("a/b", null)]
    [InlineData("a\\b", null)]
    [InlineData("Good##hidden", null)]
    [InlineData("C:evil", null)]
    [InlineData(".hidden", null)]
    [InlineData("trailing.", null)]
    [InlineData("NUL", null)]
    [InlineData("com1.old", null)]
    [InlineData("this name is far too long to be the name of any save at all", null)]
    public void ASaveIsNamedWhatAFileCanBeNamedAnywhere(string asked, string? name)
    {
        Assert.Equal(name, SaveList.Name(asked, out string why));
        Assert.Equal(name is null, why.Length > 0);
    }

    [Fact]
    public void AnAdminIsToldWhatIsKeptAndNobodyElseIs()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Client b = JoinAndLoad(rig, "Bob");
        Assert.Null(a.Saves);

        Assert.Equal("Alice", rig.Server.Op("Alice"));
        rig.Until(() => a.Saves is not null, what: "Alice's game to be told what is kept");
        Assert.Empty(a.Saves!);

        rig.Server.Saves.Keep("Old", rig.Server.World!, "B1", ["Core"], "SolLite", true, "the operator");
        rig.Server.SaySaves();
        rig.Until(() => a.Saves is { Count: 1 }, what: "Alice's game to hear of the save");
        Assert.Equal("Old", a.Saves![0].Name);
        rig.For(0.3);
        Assert.Null(b.Saves);

        // Taken back with the admin it came with.
        Assert.True(rig.Server.Deop("Alice"));
        rig.Until(() => a.Saves is null, what: "Alice's game to forget the list");
    }

    [Fact]
    public void AnAdminComingBackIsToldWithoutAsking()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        rig.Server.Op("Alice");
        rig.Until(() => a.Tokens.Count > 0, what: "a token");
        string token = a.Tokens.Dequeue();
        rig.Server.Saves.Keep("Old", rig.Server.World!, "B1", ["Core"], "SolLite", true, "Alice");
        a.Dispose();
        rig.Clients.Remove(a);
        rig.Until(() => rig.Server.Players.Count == 0, what: "the server to let go of Alice");

        Client again = rig.Join("Alice", token: token);
        rig.Until(() => again.Saves is { Count: 1 }, seconds: 20, what: "the list to come with the join");
    }

    // A save called "before the landing" is of now, not of whenever the world was last saved.
    [Fact]
    public void KeepingTheWorldAsksForAFreshSaveOfIt()
    {
        using Rig rig = new();
        Client a = Seed(rig, universe: 5000, kb: 40);
        rig.Server.Op("Alice");
        GameServer.Player alice = rig.Server.Players.Single();

        Assert.Contains("once a fresh save", rig.Server.Keep("Before the landing", alice));
        Assert.Empty(rig.Server.Saves.List());
        HandOver(rig, a, universe: 5300, kb: 41);

        rig.Until(() => a.Saves is { Count: 1 }, seconds: 20, what: "the save to be kept and said");
        SavedWorld kept = a.Saves![0];
        Assert.Equal(("Before the landing", 5300.0, "Alice", false), (kept.Name, kept.Universe, kept.By, kept.Auto));
        Assert.Equal(World(41), rig.Server.Saves.Take("before the landing")!.Value.World.Data);
        Assert.Contains(a.Chat, said => said.FromServer && said.Text.Contains("saved the world as 'Before the landing'"));
    }

    [Fact]
    public void WithNobodyInTheWorldItIsKeptAsItWasLastSaved()
    {
        using Rig rig = new();
        rig.Server.SetWorld(new Blob(BlobKind.World, "", 0, 700.0, World(12)), "B1", ["Core"], "SolLite");

        Assert.Contains("saved the world as 'Empty'", rig.Server.Keep("Empty"));
        SavedWorld kept = Assert.Single(rig.Server.Saves.List());
        Assert.Equal(("Empty", 700.0, "the operator"), (kept.Name, kept.Universe, kept.By));
    }

    // A game writes no save while its vehicle editor is open, and may never answer.
    [Fact]
    public void ASaveNobodyAnswersForIsMadeOfTheWorldAsHeld()
    {
        using Rig rig = new();
        Client a = Seed(rig, universe: 5000, kb: 40);

        rig.Server.Keep("Stale");
        rig.Until(() => a.SnapshotWanted is not null, what: "Alice to be asked");
        rig.For(0.3);
        Assert.Empty(rig.Server.Saves.List());

        rig.Skip(25.0);
        SavedWorld kept = Assert.Single(rig.Server.Saves.List());
        Assert.Equal(5000.0, kept.Universe);
        Assert.Equal(World(40), rig.Server.Saves.Take("Stale")!.Value.World.Data);
    }

    [Fact]
    public void KeepingUnderANameAlreadyKeptWritesOverIt()
    {
        using Rig rig = new();
        rig.Server.SetWorld(new Blob(BlobKind.World, "", 0, 100.0, World(10)), "B1", ["Core"], "SolLite");
        rig.Server.Keep("Base");
        rig.Server.SetWorld(new Blob(BlobKind.World, "", 0, 200.0, World(11)), "B1", ["Core"], "SolLite");
        rig.Server.Keep("base");

        // One save, under the name it was first given.
        SavedWorld kept = Assert.Single(rig.Server.Saves.List());
        Assert.Equal(("Base", 200.0), (kept.Name, kept.Universe));
    }

    [Fact]
    public void LoadingASaveHandsItToEverybodyAndKeepsWhatItReplaced()
    {
        using Rig rig = new();
        Client a = Seed(rig, universe: 5000, kb: 40);
        Client b = JoinAndLoad(rig, "Bob");
        a.Blobs.Clear();
        rig.Server.Saves.Keep("Earlier", new Blob(BlobKind.World, "", 0, 1200.0, World(22)), "B1", ["Core"], "SolLite", true, "Alice");
        byte[] before = rig.Server.World!.Data;
        // A craft made since the world was saved belongs to the world that is going.
        a.Claim("Rocket", controlling: true, made: true);
        rig.Until(() => rig.Server.CraftCount == 1, what: "the server to hear of Alice's rocket");

        string said = rig.Server.LoadSave("earlier", rig.Server.Players.First(p => p.Name == "Alice"));
        Assert.Contains("loaded 'Earlier' for 2 players", said);

        rig.Until(() => a.Blobs.Any(x => x.Kind == BlobKind.World) && b.Blobs.Any(x => x.Kind == BlobKind.World), seconds: 20, what: "both to be handed the save");
        foreach (Client c in new[] { a, b })
        {
            Blob got = c.Blobs.First(x => x.Kind == BlobKind.World);
            Assert.Equal(World(22), got.Data);
            Assert.Equal(1200.0, got.Universe);
            Assert.Contains(c.Chat, line => line.FromServer && line.Text.Contains("Alice loaded the save 'Earlier'"));
        }
        Assert.Equal(0, rig.Server.CraftCount);
        Assert.All(rig.Server.Players, p => Assert.False(p.Loaded));
        // Universe time went back with the world, and waits there until somebody is in it.
        Assert.Equal(1200.0, rig.Server.UniverseClock.Now(rig.Now()), 1);

        SavedWorld auto = rig.Server.Saves.List().Single(s => s.Auto);
        Assert.StartsWith(SaveShelf.AutoPrefix, auto.Name);
        Assert.Equal(5000.0, auto.Universe);
        Assert.Contains($"saved as '{auto.Name}'", said);
        Assert.Equal(before, rig.Server.Saves.Take(auto.Name)!.Value.World.Data);
    }

    [Fact]
    public void ALoadIsTakenBackByLoadingWhatItReplaced()
    {
        using Rig rig = new();
        rig.Server.SetWorld(new Blob(BlobKind.World, "", 0, 900.0, World(30)), "B1", ["Core"], "SolLite");
        rig.Server.Saves.Keep("Earlier", new Blob(BlobKind.World, "", 0, 100.0, World(31)), "B1", ["Core"], "SolLite", true, "Alice");

        rig.Server.LoadSave("Earlier");
        Assert.Equal(World(31), rig.Server.World!.Data);
        string kept = rig.Server.Saves.List().Single(s => s.Auto).Name;
        rig.Server.LoadSave(kept);
        Assert.Equal(World(30), rig.Server.World!.Data);
        // What that replaced was a save already, so nothing more was kept of it.
        Assert.Equal(kept, rig.Server.Saves.List().Single(s => s.Auto).Name);
    }

    // Each load used to write the world it replaced over the one kept before it, so the second load
    // in a row kept the first save and lost the world everybody had been playing in.
    [Fact]
    public void LoadsInARowDoNotCostTheWorldTheFirstReplaced()
    {
        using Rig rig = new();
        rig.Server.SetWorld(new Blob(BlobKind.World, "", 0, 900.0, World(30)), "B1", ["Core"], "SolLite");
        rig.Server.Saves.Keep("One", new Blob(BlobKind.World, "", 0, 100.0, World(31)), "B1", ["Core"], "SolLite", true, "Alice");
        rig.Server.Saves.Keep("Two", new Blob(BlobKind.World, "", 0, 200.0, World(32)), "B1", ["Core"], "SolLite", true, "Alice");

        foreach (string name in new[] { "One", "Two", "One", "Two" }) Assert.Contains("loaded", rig.Server.LoadSave(name));

        SavedWorld auto = rig.Server.Saves.List().Single(s => s.Auto);
        Assert.Equal(World(30), rig.Server.Saves.Take(auto.Name)!.Value.World.Data);
    }

    [Fact]
    public void OnlyTheNewestFewWorldsKeptBeforeALoadAreHeldOnTo()
    {
        using Rig rig = new();
        rig.Server.Saves.Keep("Earlier", new Blob(BlobKind.World, "", 0, 100.0, World(31)), "B1", ["Core"], "SolLite", true, "Alice");
        for (int i = 0; i < SaveShelf.MostAuto + 3; i++)
        {
            // Played in since the last load, so each is a world nothing else holds.
            rig.Server.SetWorld(new Blob(BlobKind.World, "", 0, 1000.0 + i, World(30)), "B1", ["Core"], "SolLite");
            rig.Server.LoadSave("Earlier");
        }
        SavedWorld[] autos = [.. rig.Server.Saves.List().Where(s => s.Auto)];
        Assert.Equal(SaveShelf.MostAuto, autos.Length);
        Assert.Equal(1000.0 + SaveShelf.MostAuto + 2, autos.Max(s => s.Universe));
        Assert.Equal(1003.0, autos.Min(s => s.Universe));
    }

    // An admin can delete what a load kept, as any save: a server run without a console has
    // nobody else to tidy them. Writing over one stays refused, since the name is the server's.
    [Fact]
    public void AnAdminCanDeleteWhatALoadKeptButNotWriteOverIt()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        rig.Server.Op("Alice");
        GameServer.Player alice = rig.Server.Players.Single();
        rig.Server.Saves.Keep("Earlier", new Blob(BlobKind.World, "", 0, 100.0, World(31)), "B1", ["Core"], "SolLite", true, "Alice");
        rig.Server.LoadSave("Earlier", alice);
        string kept = rig.Server.Saves.List().Single(s => s.Auto).Name;

        Assert.Contains("reserved for automatic saves", rig.Server.Keep(kept, alice));
        Assert.Contains("reserved for automatic saves", rig.Server.Keep("Before Load of my own", alice));
        Assert.Contains(rig.Server.Saves.List(), s => s.Name == kept);
        // A save can still be called something that only begins the same way.
        Assert.DoesNotContain("reserved", rig.Server.Keep("before loading the base", alice));

        Assert.Equal($"deleted '{kept}'", rig.Server.DeleteSave(kept, alice));
        Assert.DoesNotContain(rig.Server.Saves.List(), s => s.Auto);
        // With that copy gone, the next load keeps the world it replaces again.
        rig.Server.LoadSave("Earlier", alice);
        Assert.Single(rig.Server.Saves.List(), s => s.Auto);
    }

    // A burst of asks before the first answer has come all saw the same count, under the limit.
    [Fact]
    public void SavesStillWaitingToBeWrittenCountTowardsTheLimit()
    {
        using Rig rig = new();
        Seed(rig);
        for (int i = 0; i < SaveList.Most - 1; i++) rig.Server.Saves.Keep($"Old {i}", rig.Server.World!, "B1", ["Core"], "SolLite", true, "Alice");

        Assert.Contains("once a fresh save", rig.Server.Keep("Last"));
        Assert.Contains($"already {SaveList.Most} saves", rig.Server.Keep("One too many"));
        // Asking again for one already on its way, or for one already kept, takes no room.
        Assert.Contains("once a fresh save", rig.Server.Keep("last"));
        Assert.Contains("once a fresh save", rig.Server.Keep("Old 3"));
    }

    // What goes into an admin's save is not for another player's game to choose: the server's own
    // periodic save is asked of whoever has been in longest, and theirs is not taken for it.
    [Fact]
    public void AnAdminsSaveIsOfTheirOwnGameAndNobodyElsesAnswerIsTakenForIt()
    {
        using Rig rig = new();
        Client a = Seed(rig, universe: 5000, kb: 40);
        Client b = JoinAndLoad(rig, "Bob");
        // Whatever the join asked for is not part of this.
        rig.For(0.3);
        (a.SnapshotWanted, b.SnapshotWanted) = (null, null);
        rig.Server.Op("Bob");
        GameServer.Player bob = rig.Server.Players.First(p => p.Name == "Bob");

        Assert.Contains("from your game", rig.Server.Keep("Mine", bob));
        rig.Until(() => b.SnapshotWanted is not null, what: "Bob's own game to be asked");
        rig.For(0.3);
        Assert.Null(a.SnapshotWanted);

        rig.Server.SnapshotNow();
        HandOver(rig, a, universe: 5100, kb: 44);
        rig.Until(() => rig.Server.World!.Universe == 5100, seconds: 20, what: "Alice's save to become the world");
        Assert.Empty(rig.Server.Saves.List());

        HandOver(rig, b, universe: 5200, kb: 45);
        rig.Until(() => rig.Server.Saves.List().Count == 1, seconds: 20, what: "Bob's save to be kept");
        Assert.Equal(World(45), rig.Server.Saves.Take("Mine")!.Value.World.Data);
    }

    // The room would be in a world that turns each of them away for their mods at the next join.
    [Fact]
    public void ASaveMadeWithoutAModSomebodyHereRunsIsNotLoaded()
    {
        using Rig rig = new();
        Client a = rig.Join("Alice", mods: "Core,Shiny");
        rig.Until(() => a.Joined && a.SnapshotWanted is not null, what: "the server to ask for a world");
        a.SnapshotWanted = null;
        a.Upload(new Blob(BlobKind.World, "", a.Id, 5000, World(40)));
        rig.Until(() => rig.Server.HasWorld, what: "the world to arrive");
        rig.Server.Saves.Keep("Plain", new Blob(BlobKind.World, "", 0, 10.0, World(9)), "B1", ["Core"], "SolLite", true, "Alice");

        Assert.Contains("Alice has mods enabled that it doesn't use (Shiny)", rig.Server.LoadSave("Plain"));
    }

    // A server is not trusted to have checked the names it lists: they are drawn in a window and
    // sent back as commands.
    [Fact]
    public void AGameListsOnlySavesItCouldAskForByName()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        rig.Server.Op("Alice");
        rig.Server.Saves.Keep("Fine", rig.Server.World!, "B1", ["Core"], "SolLite", true, "Alice");
        rig.Server.Saves.Keep("Good##hidden", rig.Server.World!, "B1", ["Core"], "SolLite", true, "Alice");
        rig.Server.Saves.Keep("../world", rig.Server.World!, "B1", ["Core"], "SolLite", true, "Alice");
        rig.Server.SaySaves();

        rig.Until(() => a.Saves is { Count: > 0 }, what: "the list to come");
        Assert.Equal("Fine", Assert.Single(a.Saves!).Name);
    }

    // The periodic save was asked for before the load and arrives after it: it is of the world that
    // has gone, and kept as the server's it would undo the load for everybody who joins after.
    [Fact]
    public void ASaveOfTheOldWorldArrivingAfterALoadIsNotTheWorld()
    {
        using Rig rig = new();
        Client a = Seed(rig, universe: 5000, kb: 40);
        rig.Server.Saves.Keep("Earlier", new Blob(BlobKind.World, "", 0, 1200.0, World(22)), "B1", ["Core"], "SolLite", true, "Alice");

        rig.Server.SnapshotNow();
        rig.Until(() => a.SnapshotWanted is not null, what: "Alice to be asked for a save");
        rig.Server.LoadSave("Earlier");
        a.SnapshotWanted = null;
        a.Upload(new Blob(BlobKind.World, "", a.Id, 5400.0, World(44)));

        rig.Until(() => rig.Server.Said.Any(s => s.Contains("nobody asked for")), seconds: 20, what: "the old world to be turned away");
        Assert.Equal(World(22), rig.Server.World!.Data);
    }

    [Fact]
    public void ASaveSomebodyHereCouldNotOpenIsNotLoaded()
    {
        using Rig rig = new();
        Seed(rig);
        byte[] before = rig.Server.World!.Data;
        rig.Server.Saves.Keep("Modded", new Blob(BlobKind.World, "", 0, 10.0, World(9)), "B1", ["Core", "KSArmory@1.2.0"], "SolLite", true, "Alice");

        Assert.Contains("needs KSArmory", rig.Server.LoadSave("Modded"));
        Assert.Contains("no save called 'Nothing'", rig.Server.LoadSave("Nothing"));
        Assert.Equal(before, rig.Server.World!.Data);
        Assert.DoesNotContain(rig.Server.Saves.List(), s => s.Auto);
    }

    [Fact]
    public void ASaveInAnotherSystemIsLoadedAndEveryGameFollowsIt()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        rig.Server.Saves.Keep("Sol", new Blob(BlobKind.World, "", 0, 10.0, World(8)), "B1", ["Core"], "Sol", true, "Alice");

        // Alice is in SolLite, and her game swaps into Sol to load it.
        Assert.StartsWith("loaded 'Sol'", rig.Server.LoadSave("Sol"));
        Assert.Equal(World(8), rig.Server.World!.Data);
        rig.Until(() => a.Blobs.Any(b => b.Kind == BlobKind.World && b.Data.SequenceEqual(World(8))), seconds: 20, what: "Alice to be handed the Sol world");
        a.SayLoaded(10.0);
        rig.Until(() => rig.Server.Players.Single(p => p.Id == a.Id).Loaded, what: "Alice to load it");
        Assert.Equal("Sol", rig.Server.Players.Single(p => p.Id == a.Id).System);
    }

    [Fact]
    public void ADeletedSaveIsGoneFromEveryAdminsList()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        rig.Server.Op("Alice");
        rig.Server.Saves.Keep("Old", rig.Server.World!, "B1", ["Core"], "SolLite", true, "Alice");
        rig.Server.SaySaves();
        rig.Until(() => a.Saves is { Count: 1 }, what: "the save to be said");

        Assert.Equal("deleted 'Old'", rig.Server.DeleteSave("old"));
        rig.Until(() => a.Saves is { Count: 0 }, what: "the list to empty");
        Assert.Contains("no save called", rig.Server.DeleteSave("Old"));
    }

    [Fact]
    public void SavesOutliveTheServerAndAZipCopiedInIsOneOfThem()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ksamp-saves-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (Rig rig = new(saves: new SaveShelf(folder)))
            {
                rig.Server.SetWorld(new Blob(BlobKind.World, "", 0, 4200.0, World(15)), "B1", ["Core"], "SolLite");
                rig.Server.Keep("Kept");
            }
            // A KSA save zipped as its folder, the way somebody would zip one to copy it over.
            byte[] universe = "<Universe/>"u8.ToArray();
            byte[] meta = "name = \"Mine\"\nsystems = [ \"Sol\", ]\n"u8.ToArray();
            File.WriteAllBytes(Path.Combine(folder, "By hand.zip"), Blob.Zip([("Mine/universe.xml", universe), ("Mine/meta.toml", meta)]));
            File.WriteAllBytes(Path.Combine(folder, "not a save.zip"), Blob.Zip([("readme.txt", universe)]));
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a zip at all");

            using Rig again = new(saves: new SaveShelf(folder));
            IReadOnlyList<SavedWorld> kept = again.Server.Saves.List();
            Assert.Equal(["By hand", "Kept", "not a save"], kept.Select(s => s.Name).Order());
            SavedWorld mine = kept.Single(s => s.Name == "Kept");
            Assert.Equal((4200.0, "SolLite", "B1", "the operator"), (mine.Universe, mine.System, mine.KsaBuild, mine.By));

            Assert.Contains("loaded 'Kept'", again.Server.LoadSave("Kept"));
            Assert.Equal(World(15), again.Server.World!.Data);

            // Unpacked to the save's own files, with the system read off it and its time left to
            // whoever loads it first.
            Assert.Contains("loaded 'By hand'", again.Server.LoadSave("By hand"));
            Assert.Equal(["meta.toml", "universe.xml"], Blob.Unzip(again.Server.World!.Data).Select(f => f.Name).Order());
            Assert.False(again.Server.UniverseClock.Known);

            Assert.Contains("couldn't read 'not a save'", again.Server.LoadSave("not a save"));
            Assert.Equal(["meta.toml", "universe.xml"], Blob.Unzip(again.Server.World!.Data).Select(f => f.Name).Order());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
