using KSAMultiplayer.Server;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// Whose game a kitten died in. KSA's save keeps a death and nothing of who flew the kitten, and the
/// save comes from whichever game was asked last: the server is the one place it can be known.
/// </summary>
public class MemorialTests
{
    private static byte[] Datagram(Wire.Message m)
    {
        byte[] buffer = new byte[Wire.BufferBytes];
        return buffer[..Wire.Write(m, buffer)];
    }

    [Fact]
    public void TheListSurvivesTheWire()
    {
        MemorialList.Death[] deaths =
        [
            new("Pebean", "Kitten 5192", "Abandoned", "NewRocket_3", 7244356.5),
            new("Fadle", "Kitten 5192", "Abandoned", "", 7244356.5),
        ];
        Assert.Equal(deaths, MemorialList.Decode(MemorialList.Encode(deaths, 4), out uint serial)!);
        Assert.Equal(4u, serial);
        Assert.Empty(MemorialList.Decode(MemorialList.Encode([], 1), out _)!);
        Assert.Null(MemorialList.Decode(MemorialList.Encode(deaths, 1)[..^3], out _));
        Assert.Null(MemorialList.Decode([9, 0, 0, 0, 0, 0, 0], out _));

        Wire.Died died = new("Pebean", "Abandoned", "NewRocket_3");
        Assert.Equal(died, Wire.Read(Datagram(died), out _));
    }

    [Fact]
    public void ADeathIsNamedForTheGameThatSaidItInEveryGame()
    {
        using Rig rig = new();
        Client a = SaveTests.Seed(rig, "Kitten 5192");
        Client b = SaveTests.JoinAndLoad(rig, "Soda");

        a.Died("Pebean", "Abandoned", "NewRocket_3");
        rig.Until(() => b.Memorial.ContainsKey("Pebean") && a.Memorial.ContainsKey("Pebean"), what: "both games to hear of it");

        // The server's name for the game that said it: nothing in the datagram says who.
        MemorialList.Death d = b.Memorial["Pebean"];
        Assert.Equal(("Kitten 5192", "Abandoned", "NewRocket_3"), (d.By, d.Cause, d.Craft));
        Assert.Single(rig.Server.Memorial.Deaths);

        // A game joining later is told too.
        Client c = SaveTests.JoinAndLoad(rig, "Onyx");
        rig.Until(() => c.Memorial.ContainsKey("Pebean"), what: "a later game to hear of it");
    }

    [Fact]
    public void SayingADeathAgainChangesNothingAndADeathAfterReplacesIt()
    {
        using Rig rig = new();
        Client a = SaveTests.Seed(rig);
        Client b = SaveTests.JoinAndLoad(rig, "Bob");

        a.Died("Fadle", "Abandoned", "NewRocket_3");
        rig.Until(() => a.Memorial.ContainsKey("Fadle"), what: "the memorial");
        Assert.False(rig.Server.Memorial.Add(rig.Server.Memorial.Deaths[0] with { Universe = 1.0 }));

        // Brought back since, and lost again in another game: the roster shows that death now.
        b.Died("Fadle", "GroundImpact", "Hopper");
        rig.Until(() => a.Memorial.TryGetValue("Fadle", out MemorialList.Death d) && d.By == "Bob", what: "the newer death");
        Assert.Equal("GroundImpact", Assert.Single(rig.Server.Memorial.Deaths).Cause);
    }

    [Fact]
    public void AGameThatHasNotLoadedTheWorldPutsNobodyOnIt()
    {
        using Rig rig = new();
        SaveTests.Seed(rig);
        Client late = rig.Join("Mallory");
        rig.Until(() => late.Joined, what: "the join");

        late.Died("Jebediah", "Abandoned", "");
        rig.Skip(2.0);
        Assert.Empty(rig.Server.Memorial.Deaths);
    }

    [Fact]
    public void OneGameCannotFillItAtOnce()
    {
        using Rig rig = new();
        Client a = SaveTests.Seed(rig);
        for (int i = 0; i < 40; i++) a.Died($"Kitten {i}", "Collision", "Wreck");
        rig.Until(() => rig.Server.Memorial.Deaths.Count >= 16, what: "the first deaths");
        Assert.InRange(rig.Server.Memorial.Deaths.Count, 16, 20);
    }

    [Fact]
    public void TheMemorialOutlivesARestart()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ksamp-memorial-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (Rig rig = new(store: new WorldStore(folder)))
            {
                Client a = SaveTests.Seed(rig, "Kitten 5192");
                a.Died("Pebean", "Abandoned", "NewRocket_3");
                rig.Until(() => a.Memorial.ContainsKey("Pebean"), what: "the memorial");
            }
            using Rig again = new(store: new WorldStore(folder));
            Assert.Equal("Kitten 5192", Assert.Single(again.Server.Memorial.Deaths).By);
            Client b = again.Join("Bob");
            again.Until(() => b.Memorial.ContainsKey("Pebean"), seconds: 20, what: "a game joining the restarted server to hear of it");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
