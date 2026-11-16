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
