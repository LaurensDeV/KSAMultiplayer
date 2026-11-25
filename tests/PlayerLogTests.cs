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
        (string file, string text) = rig.Server.PlayerLogs.Kept[0];
        // By the player's number and their Discord account, which Alice has none of; never by name.
        Assert.StartsWith($"no-discord.player-{a.Id}.KSAMultiplayer_261123-152935.", file);
        Assert.DoesNotContain("Alice", file);
        Assert.StartsWith($"player {a.Id}, playing as Alice, not logged in with Discord", text);
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
            string? old = logs.Keep("Bob", 2, "", "", "KSAMultiplayer.261101-100000", "old", now.AddDays(-20));
            Assert.True(File.Exists(old));
            string? kept = logs.Keep("Alice/../..\nforged line", 1, "254041610990387200", "laurens", "../../KSAMultiplayer.261123-152935", Written, now);
            Assert.True(File.Exists(kept));
            Assert.StartsWith(Path.Combine(folder, "player-logs", "2026-11-23", "discord-254041610990387200.player-1."), kept);
            Assert.StartsWith("player 1, playing as Alice/../.. forged line, logged in with Discord as laurens (254041610990387200)", LogText.Unpack(File.ReadAllBytes(kept!)));
            Assert.False(File.Exists(old));
            Assert.DoesNotContain("devoo", LogText.Unpack(File.ReadAllBytes(kept!)));
            Assert.Single(Directory.GetFiles(Path.Combine(folder, "player-logs", "2026-11-23")));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static Blob Piece(Client a, string log, int first, string text) =>
        new(BlobKind.Log, LogPieces.Key(log, first), a.Id, 0.0, LogText.Pack(text));

    /// <summary>
    /// A log sent a piece at a time is kept in one file a join, each line once: a piece sent again,
    /// as a game whose ack was lost does, adds only what is new of it, and the game hears that the
    /// server has it.
    /// </summary>
    [Fact]
    public void PiecesOfALogAreKeptInOneFileEachLineOnce()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ksamp-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            using Rig rig = new(new ServerOptions { CollectLogs = true }, store: new WorldStore(folder));
            Client a = Seed(rig);
            const string log = "KSAMultiplayer.261125-100000";
            uint first = a.Upload(Piece(a, log, 0, "one at 203.0.113.7:50100\ntwo\n"));
            rig.Until(() => a.Arrived(first), what: "the server to have the first piece");
            Assert.False(a.Uploading(first));
            uint again = a.Upload(Piece(a, log, 0, "one at 203.0.113.7:50100\ntwo\nthree\n"));
            rig.Until(() => a.Arrived(again), what: "the piece sent again");
            uint more = a.Upload(Piece(a, log, 3, "four\n"));
            rig.Until(() => a.Arrived(more), what: "the next piece");

            string[] files = Directory.GetFiles(rig.Server.PlayerLogs.Folder, "*.log.gz", SearchOption.AllDirectories);
            string file = Assert.Single(files);
            Assert.Contains($"no-discord.player-{a.Id}.KSAMultiplayer_261125-100000.", file);
            string kept = LogText.Unpack(File.ReadAllBytes(file))!;
            Assert.StartsWith($"player {a.Id}, playing as Alice, not logged in with Discord", kept);
            Assert.EndsWith("\none at (address)\ntwo\nthree\nfour\n", kept);
            Assert.Single(rig.Server.Said, s => s.StartsWith("keeping Alice's game log of KSAMultiplayer.261125-100000, from line 1"));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A piece that starts past what came says the lines that never did; one that starts a join past the first line says so.</summary>
    [Fact]
    public void LinesThatNeverCameAreSaidWhereTheyWouldHaveBeen()
    {
        PlayerLogs logs = new();
        Dictionary<string, PlayerLogs.Open> open = [];
        DateTime now = new(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(2, logs.Append("Bob", 2, "", "", "KSAMultiplayer.261125-100000", 40, "a\nb\n", now, open));
        Assert.Equal(1, logs.Append("Bob", 2, "", "", "KSAMultiplayer.261125-100000", 50, "c\n", now, open));
        Assert.Equal(0, logs.Append("Bob", 2, "", "", "KSAMultiplayer.261125-100000", 40, "a\n", now, open));
        string all = string.Concat(logs.Kept.Select(k => k.Text));
        Assert.Contains("(from line 41: the lines before it were sent before this join)\na\nb\n", all);
        Assert.EndsWith("b\n(lines 43 to 50 never arrived)\nc\n", all);
        Assert.Equal(51, open["KSAMultiplayer.261125-100000"].Lines);
    }

    /// <summary>An operator asks for logs mid-session: the game is told, and may send a full set again.</summary>
    [Fact]
    public void AnOperatorCanAskEveryGameForItsLogsNow()
    {
        using Rig rig = new(new ServerOptions { CollectLogs = true });
        Client a = Seed(rig);
        for (int i = 0; i < PlayerLogs.MostPerSession; i++) a.Upload(Sent(a, $"KSAMultiplayer.26112{i}-000000"));
        rig.Until(() => rig.Server.PlayerLogs.Kept.Count == PlayerLogs.MostPerSession, what: "a full set");

        Assert.StartsWith("asked Alice for their game's logs", rig.Server.AskLogs("", "the station is wobbling"));
        rig.Until(() => a.LogsAsked is not null, what: "Alice's game to be asked");
        Assert.Equal("the station is wobbling", a.LogsAsked);
        a.Upload(Sent(a, "KSAMultiplayer.now-180000"));
        rig.Until(() => rig.Server.PlayerLogs.Kept.Count == PlayerLogs.MostPerSession + 1, what: "the log sent when asked");

        Assert.Equal("nobody called 'Bob'", rig.Server.AskLogs("Bob", "why"));
    }

    [Fact]
    public void AServerNotCollectingLogsAsksForNone()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Assert.Contains("collect-logs is off", rig.Server.AskLogs("", "why"));
        rig.For(1.0);
        Assert.Null(a.LogsAsked);
    }

    /// <summary>Asking for players' logs is the console's alone: a game is told there is no such command, whoever plays it.</summary>
    [Fact]
    public void NoPlayerCanAskForPlayersLogsFromAGame()
    {
        Assert.True(Permissions.OnlyAtTheConsole("playerlogs"));
        Assert.True(Permissions.OnlyAtTheConsole("PlayerLogs Bob"));
        Assert.False(Permissions.OnlyAtTheConsole("logs"));
    }
}
