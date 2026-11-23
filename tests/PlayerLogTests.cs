using KSAMultiplayer.Server;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// Players' game logs, kept by a server with collect-logs on and by no other, and cleaned of what
/// says who and where a player is.
/// </summary>
public class PlayerLogTests
{
    private static Client Seed(Rig rig)
    {
        Client a = rig.Join("Alice");
        rig.Until(() => a.Joined && a.SnapshotWanted is not null, what: "the server to ask for a world");
        a.SnapshotWanted = null;
        a.Upload(new Blob(BlobKind.World, "", a.Id, 5000, Rig.FakeWorld()));
        rig.Until(() => rig.Server.HasWorld && rig.Server.Players.First(p => p.Id == a.Id).Loaded, what: "the world to arrive");
        return a;
    }

    private const string Written = "15:31:35.955 INFO connecting to 203.0.113.7:50100\n"
                                 + "15:31:35.918 INFO multiplayer: looking up 198.51.100.4...\n"
                                 + "15:31:36.001 INFO saved to C:\\Users\\devoo\\Documents\\My Games\\Kitten Space Agency\\Saves\n"
                                 + "15:31:36.002 INFO KSA 2026.11.16.5901, from /home/laurens/.local and /mnt/c/Users/devoo/x\n";

    private static Blob Sent(Client a, string session = "KSAMultiplayer.261123-152935") =>
        new(BlobKind.Log, session, a.Id, 0.0, LogText.Pack(Written));

    [Fact]
    public void AServerCollectingLogsKeepsAGamesLogCleaned()
    {
        using Rig rig = new(new ServerOptions { CollectLogs = true });
        Client a = Seed(rig);
        a.Upload(Sent(a));
        rig.Until(() => rig.Server.PlayerLogs.Kept.Count == 1, what: "the log to be kept");
        (string player, string session, string text) = rig.Server.PlayerLogs.Kept[0];
        Assert.Equal("Alice", player);
        Assert.Equal("KSAMultiplayer.261123-152935", session);
        Assert.DoesNotContain("203.0.113.7", text);
        Assert.DoesNotContain("198.51.100.4", text);
        Assert.DoesNotContain("devoo", text);
        Assert.DoesNotContain("laurens", text);
        // A KSA build is not an address, and a path past the user's folder is kept.
        Assert.Contains("2026.11.16.5901", text);
        Assert.Contains("C:\\Users\\(user)\\Documents\\My Games", text);
        Assert.Contains(rig.Server.Said, s => s.StartsWith("kept Alice's game log of KSAMultiplayer.261123-152935"));
    }

    [Fact]
    public void AServerNotCollectingLogsKeepsNone()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        a.Upload(Sent(a));
        rig.For(2.0);
        Assert.Empty(rig.Server.PlayerLogs.Kept);
    }

    [Fact]
    public void AGameIsTakenAFewLogsASessionAndNoMore()
    {
        using Rig rig = new(new ServerOptions { CollectLogs = true });
        Client a = Seed(rig);
        for (int i = 0; i < PlayerLogs.MostPerSession + 3; i++) a.Upload(Sent(a, $"KSAMultiplayer.26112{i}-000000"));
        rig.For(3.0);
        Assert.Equal(PlayerLogs.MostPerSession, rig.Server.PlayerLogs.Kept.Count);
    }

    [Fact]
    public void AKeptLogLandsInTheDaysFolderAndOldDaysGo()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ksamp-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            PlayerLogs logs = new(folder);
            DateTime now = new(2026, 11, 23, 15, 0, 0, DateTimeKind.Utc);
            string? old = logs.Keep("Bob", "KSAMultiplayer.261101-100000", "old", now.AddDays(-20));
            Assert.True(File.Exists(old));
            string? kept = logs.Keep("Alice/../..", "KSAMultiplayer.261123-152935", Written, now);
            Assert.True(File.Exists(kept));
            Assert.StartsWith(Path.Combine(folder, "player-logs", "2026-11-23"), kept);
            Assert.False(File.Exists(old));
            Assert.DoesNotContain("devoo", LogText.Unpack(File.ReadAllBytes(kept!)));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
