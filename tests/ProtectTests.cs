using KSAMultiplayer.Server;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// The craft an operator has said are not debris, and the two rules of KSA's a server decides for
/// its world. Both are things every game has to hold the same answer to, since each sweeps its own
/// world and runs its own kittens.
/// </summary>
public class ProtectTests
{
    [Fact]
    public void AProtectedCraftIsSparedUnlessItIsNamedInFull()
    {
        HashSet<string> names = ["Station", "Mun Base"];
        Assert.True(ProtectedList.Spares(names, "Station", ""));
        Assert.True(ProtectedList.Spares(names, "Station", "*"));
        Assert.True(ProtectedList.Spares(names, "Station", "Stat*"));
        Assert.True(ProtectedList.Spares(names, "Station", "Statio?"));
        Assert.False(ProtectedList.Spares(names, "Station", "Station"));
        Assert.False(ProtectedList.Spares(names, "Station", " station "));
        Assert.True(ProtectedList.Spares(names, "Mun Base", "Station"));
        Assert.False(ProtectedList.Spares(names, "Debris", "*"));
    }

    [Fact]
    public void TheListSurvivesTheWire()
    {
        Assert.Equal(["Mun Base", "Station"], ProtectedList.Decode(ProtectedList.Encode(["Mun Base", "Station"], 9), out uint serial)!);
        Assert.Equal(9u, serial);
        Assert.Empty(ProtectedList.Decode(ProtectedList.Encode([], 1), out _)!);
        Assert.Null(ProtectedList.Decode(ProtectedList.Encode(["Station"], 1)[..^2], out _));
        Assert.Null(ProtectedList.Decode([9, 0, 0, 0, 0, 0, 0], out _));
    }

    [Fact]
    public void TheBroomLeavesAProtectedCraftInEveryGame()
    {
        using Rig rig = new();
        Client a = SaveTests.Seed(rig);
        Client b = SaveTests.JoinAndLoad(rig, "Bob");
        a.Claim("Station", controlling: false, made: true);
        a.Claim("Stage 1", controlling: false, made: true);
        rig.Until(() => b.OwnerOf("Station") == a.Id && b.OwnerOf("Stage 1") == a.Id, what: "the owners table");

        Assert.Contains("Station is now protected", rig.Server.Protect("station"));
        Assert.Contains("already protected", rig.Server.Protect("Station"));
        Assert.Contains("without wildcards", rig.Server.Protect("Stat*"));

        Assert.Equal(["Stage 1"], rig.Server.Sweep());
        rig.Until(() => a.Swept.Count == 1 && b.Swept.Count == 1, what: "both games to be told");
        // Each game sweeps its own world by the name, and holds this craft back from it.
        foreach (Client c in new[] { a, b })
        {
            Assert.True(c.Spared("Station", c.Swept.Peek()));
            Assert.False(c.Spared("Stage 1", c.Swept.Peek()));
            Assert.Equal(a.Id, c.OwnerOf("Station"));
            Assert.Equal(0, c.OwnerOf("Stage 1"));
        }
    }

    [Fact]
    public void AProtectedCraftGoesWhenItIsNamedInFullOrUnprotected()
    {
        using Rig rig = new();
        Client a = SaveTests.Seed(rig);
        a.Claim("Station", controlling: false, made: true);
        a.Claim("Hab", controlling: false, made: true);
        rig.Until(() => a.Owns("Station") && a.Owns("Hab"), what: "the owners table");
        rig.Server.Protect("Station");
        rig.Server.Protect("Hab");

        Assert.Empty(rig.Server.Sweep("*"));
        Assert.Equal(["Station"], rig.Server.Sweep("station"));

        Assert.Contains("no longer protected", rig.Server.Unprotect("hab"));
        Assert.Contains("is not protected", rig.Server.Unprotect("Hab"));
        Assert.Equal(["Hab"], rig.Server.Sweep("*"));
    }

    // The list goes by a transfer and the sweep by a datagram, so the sweep can come first: a game
    // that swept then would take the craft protected a moment before.
    [Fact]
    public void AGameDoesNotSweepBeforeItHasTheListTheSweepWasMadeAgainst()
    {
        using Rig rig = new();
        Client a = SaveTests.Seed(rig);
        a.Claim("Station", controlling: false, made: true);
        rig.Until(() => a.Owns("Station"), what: "the owners table");

        rig.Server.Protect("Station");
        rig.Server.Sweep();
        rig.Until(() => a.Swept.Count == 1, what: "Alice's game to be told to sweep");
        // Whichever arrived first, the list is here by the time the sweep is handed on.
        Assert.Contains("Station", a.Protected);
        Assert.True(a.Owns("Station"));
    }

    [Fact]
    public void SomebodyJoiningIsToldWhatIsProtected()
    {
        using Rig rig = new();
        SaveTests.Seed(rig);
        rig.Server.Protect("Station");
        Client b = SaveTests.JoinAndLoad(rig, "Bob");
        rig.Until(() => b.Protected.Contains("Station"), seconds: 20, what: "Bob's game to be told");
    }

    [Fact]
    public void TheListOutlivesTheServerAndAFreshWorldEmptiesIt()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ksamp-protected-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (Rig rig = new(protect: new ProtectedCraft(folder)))
            {
                rig.Server.Protect("Station");
                rig.Server.Protect("Mun Base");
            }
            using Rig again = new(new ServerOptions { EmptyWorld = "SolLite" }, protect: new ProtectedCraft(folder));
            Assert.Equal(["Mun Base", "Station"], again.Server.Protected.Names.Order());

            // The craft went with the world, and a name left behind would protect whatever is next called it.
            again.Server.Fresh("the operator started the world over");
            Assert.Empty(again.Server.Protected.Names);
            Assert.Empty(new ProtectedCraft(folder).Names);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    // What is protected is that world's: a craft is known by its name, and the names are the ones
    // that world's craft have.
    [Fact]
    public void ASaveBringsBackWhatWasProtectedWhenItWasKept()
    {
        using Rig rig = new();
        Client a = SaveTests.Seed(rig);
        rig.Server.Protect("Station");
        rig.Until(() => a.Protected.Contains("Station"), what: "Alice's game to hear the station is protected");

        rig.Server.Keep("With the station");
        rig.Until(() => a.SnapshotWanted is not null, what: "Alice's game to be asked for a save");
        a.SnapshotWanted = null;
        a.Upload(new Blob(BlobKind.World, "", a.Id, 5200, Rig.FakeWorld(41)));
        rig.Until(() => rig.Server.Saves.List().Count == 1, seconds: 20, what: "the save to be kept");

        rig.Server.Unprotect("Station");
        rig.Server.Protect("Mun Base");
        rig.Until(() => a.Protected.SetEquals(["Mun Base"]), what: "Alice's game to hear the list change");

        Assert.Contains("loaded 'With the station'", rig.Server.LoadSave("With the station"));
        Assert.Equal(["Station"], rig.Server.Protected.Names);
        rig.Until(() => a.Protected.SetEquals(["Station"]), seconds: 20, what: "Alice's game to be told the list that came back");

        // And taking the load back puts the list back as it was with the world it replaced.
        string before = rig.Server.Saves.List().Single(s => s.Auto).Name;
        Assert.Contains("loaded", rig.Server.LoadSave(before));
        Assert.Equal(["Mun Base"], rig.Server.Protected.Names);
        rig.Until(() => a.Protected.SetEquals(["Mun Base"]), seconds: 20, what: "Alice's game to be told the list is back");
    }

    // Protecting a craft is a change to what a save would hold, so a load straight after one keeps
    // the world again rather than taking it for a world that is already a save.
    [Fact]
    public void WhatWasProtectedSinceTheLastSaveIsNotLostToALoad()
    {
        using Rig rig = new();
        rig.Server.SetWorld(new Blob(BlobKind.World, "", 0, 100.0, Rig.FakeWorld(10)), "B1", ["Core"], "SolLite");
        rig.Server.Keep("One");
        rig.Server.LoadSave("One");
        Assert.DoesNotContain(rig.Server.Saves.List(), s => s.Auto);

        rig.Server.Protect("Station");
        rig.Server.LoadSave("One");
        Assert.Empty(rig.Server.Protected.Names);
        string before = rig.Server.Saves.List().Single(s => s.Auto).Name;
        rig.Server.LoadSave(before);
        Assert.Equal(["Station"], rig.Server.Protected.Names);
    }

    [Fact]
    public void ASaveThatSaysNothingOfProtectedCraftLeavesTheListAlone()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ksamp-saves-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            // Copied in by hand, and so with nothing beside it to say what was protected.
            File.WriteAllBytes(Path.Combine(folder, "By hand.zip"), Blob.Zip([("universe.xml", "<Universe/>"u8.ToArray())]));
            using Rig rig = new(saves: new SaveShelf(folder));
            // Kept before the two were kept together: a word beside it, and nothing in that word of them.
            rig.Server.Saves.Keep("Older", new Blob(BlobKind.World, "", 0, 10.0, Rig.FakeWorld(8)), "B1", ["Core"], "SolLite", true, "Alice");
            rig.Server.Protect("Station");

            Assert.Contains("loaded 'By hand'", rig.Server.LoadSave("By hand"));
            Assert.Equal(["Station"], rig.Server.Protected.Names);
            Assert.Contains("loaded 'Older'", rig.Server.LoadSave("Older"));
            Assert.Equal(["Station"], rig.Server.Protected.Names);

            // One kept with nothing protected does say so, and empties it.
            rig.Server.Unprotect("Station");
            rig.Server.Keep("Bare");
            rig.Server.Protect("Station");
            Assert.Contains("loaded 'Bare'", rig.Server.LoadSave("Bare"));
            Assert.Empty(rig.Server.Protected.Names);
            // Written down, so it is the same after a restart.
            Assert.Empty(new SaveShelf(folder).Take("Bare")!.Value.Protected!);
            Assert.Null(new SaveShelf(folder).Take("Older")!.Value.Protected);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void EveryGameIsToldTheServersRulesAndHearsThemChange()
    {
        using Rig rig = new(new ServerOptions { Radiation = false });
        Client a = SaveTests.Seed(rig);
        rig.Until(() => a.Rules is not null, what: "the clock to say the rules");
        Assert.Equal(Wire.WorldRules.LifeSupport, a.Rules);

        rig.Server.Reconfigure(rig.Server.Options with { LifeSupport = false, Radiation = true });
        rig.Until(() => a.Rules == Wire.WorldRules.Radiation, what: "the change to reach Alice's game");
        Assert.Contains(a.Chat, said => said.FromServer && said.Text == "life support is now off on this server");
        Assert.Contains(a.Chat, said => said.FromServer && said.Text == "radiation is now on on this server");
    }

    [Fact]
    public void BothRulesAreOnUnlessTheOperatorSaysOtherwise()
    {
        Dictionary<string, string> none = [];
        (ServerOptions byDefault, _, _) = Settings.Options(Settings.Resolve(none, _ => null, none));
        Assert.True(byDefault.LifeSupport && byDefault.Radiation);

        (ServerOptions off, _, _) = Settings.Options(Settings.Resolve(none, _ => null, Settings.Read("life-support = false\nradiation = false\n")));
        Assert.False(off.LifeSupport || off.Radiation);

        using Rig rig = new();
        Assert.Equal(Wire.WorldRules.All, rig.Server.Rules);
    }
}
