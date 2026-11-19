using System.Net;
using System.Security.Cryptography;
using System.Text;
using KSAMultiplayer.Server;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// Who may run the server's commands from inside a game. An operator ops a player at the console, the
/// token that makes them one lives on their machine, and taking it back takes it back everywhere.
/// </summary>
public class AdminTests
{
    private static Client Opped(Rig rig, out string token, string name = "Alice")
    {
        Client c = rig.Join(name);
        rig.Until(() => c.Joined, what: $"{name} to join");
        Assert.False(c.Admin);
        Assert.Equal(name, rig.Server.Op(name.ToLowerInvariant()));
        rig.Until(() => c.Admin && c.Tokens.Count > 0, what: $"{name} to be handed a token");
        token = c.Tokens.Dequeue();
        Keep(rig, c, token);
        return c;
    }

    /// <summary>What a game does with a token it has written down, and the server hearing of it.</summary>
    private static void Keep(Rig rig, Client c, string token)
    {
        c.Kept(token);
        rig.Until(() => Heard(rig), what: "the server to hear the token is kept");
    }

    /// <summary>Whether every game has said back what the server last told it of being an admin.</summary>
    private static bool Heard(Rig rig) => rig.Server.Players.All(p => p.Telling is null);

    private static void Leave(Rig rig, Client c)
    {
        c.Dispose();
        rig.Clients.Remove(c);
        rig.Until(() => rig.Server.Players.Count == 0, what: "the server to let go of them");
    }

    [Fact]
    public void OppingAPlayerHandsTheirGameATokenToKeep()
    {
        using Rig rig = new();
        Opped(rig, out string token);
        Assert.NotEmpty(token);
        Assert.Equal("Alice", Assert.Single(rig.Server.Admins.Names));
    }

    // The token has to reach a game: there is nowhere to leave it for somebody who is not here, and
    // nothing is written down that could say it to them later.
    [Fact]
    public void NobodyWhoIsNotPlayingCanBeOpped()
    {
        using Rig rig = new();
        Assert.Null(rig.Server.Op("Nobody"));
        Assert.Empty(rig.Server.Admins.Names);
    }

    [Fact]
    public void AnAdminsLineIsRunAndAnotherPlayersIsRefused()
    {
        using Rig rig = new();
        Client b = rig.Join("Bob");
        Client a = Opped(rig, out _);
        rig.Until(() => b.Joined, what: "Bob to join");

        a.Ask("clear Rocket_*");
        rig.Until(() => rig.Server.Asked.Count > 0, what: "the server to take Alice's line");
        Assert.Equal("clear Rocket_*", rig.Server.Asked.Dequeue().Line);

        b.Ask("clear Rocket_*");
        rig.For(0.5);
        Assert.Empty(rig.Server.Asked);
        Assert.Contains(b.Chat, said => said.Text.Contains("only trusted players and admins"));
    }

    [Fact]
    public void AnAdminComesBackAnAdminWithNothingTyped()
    {
        using Rig rig = new();
        Leave(rig, Opped(rig, out string token));

        Client again = rig.Join("Alice", token: token);
        rig.Until(() => again.Admin, what: "Alice to be an admin again");
        // Nothing new to write down: what she offered is what she is told to go on keeping, so that
        // it can be filed under wherever the server was found this time.
        Assert.Equal(token, Assert.Single(again.Tokens));
    }

    [Fact]
    public void ARevokedTokenIsThrownAwayAndIsNoGoodAgain()
    {
        using Rig rig = new();
        Client a = Opped(rig, out string token);

        Assert.True(rig.Server.Deop("Alice"));
        rig.Until(() => !a.Admin, what: "Alice to hear she is no admin");
        Assert.Equal("", a.Tokens.Dequeue());
        Assert.Empty(rig.Server.Admins.Names);

        // Not status, which anybody may ask for: a line that needs what she was.
        a.Ask("clear Rocket_*");
        rig.For(0.5);
        Assert.Empty(rig.Server.Asked);

        Leave(rig, a);
        Client again = rig.Join("Alice", token: token);
        rig.Until(() => again.Joined, what: "Alice to join again");
        rig.For(0.3);
        Assert.False(again.Admin);
    }

    // Revoked while they were away: the list is the server's, not the game's, so the token it kept is
    // worth nothing when it comes back with it.
    [Fact]
    public void ATokenRevokedWhileItsOwnerWasAwayIsNoGood()
    {
        using Rig rig = new();
        Leave(rig, Opped(rig, out string token));
        Assert.True(rig.Server.Deop("Alice"));

        Client again = rig.Join("Alice", token: token);
        rig.Until(() => again.Joined, what: "Alice to join again");
        rig.For(0.3);
        Assert.False(again.Admin);
    }

    [Fact]
    public void TheAdminListOutlivesTheServerThatWroteIt()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ksamp-admins-" + Guid.NewGuid().ToString("N"));
        try
        {
            string token;
            using (Rig rig = new(admins: new Admins(folder)))
            {
                Opped(rig, out token);
            }

            Admins read = new(folder);
            Assert.Equal("Alice", Assert.Single(read.Names));
            Assert.Equal(Wire.Role.Admin, read.Check(token, "Alice"));
            Assert.Equal(Wire.Role.Player, read.Check(token[..^1] + "x", "Alice"));
            // Only the hash is kept, so the file is enough to take an admin away and not to become one.
            Assert.DoesNotContain(token, File.ReadAllText(Path.Combine(folder, "admins.json")));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    // A token outlives the name it was given to, and an operator revokes the player they can see.
    [Fact]
    public void AnAdminIsRememberedUnderTheNameTheyAreSeenUnder()
    {
        using Rig rig = new();
        Leave(rig, Opped(rig, out string token));

        Client renamed = rig.Join("Alicia", token: token);
        rig.Until(() => renamed.Admin, what: "the same machine to be an admin under its new name");
        Assert.Equal("Alicia", Assert.Single(rig.Server.Admins.Names));
        Assert.True(rig.Server.Deop("Alicia"));
    }

    // Opping somebody who is already an admin replaces their token, which is how one that was lost is
    // replaced; the one before it stops working.
    [Fact]
    public void OppingAnAdminAgainRetiresTheTokenTheyHad()
    {
        using Rig rig = new();
        Client a = Opped(rig, out string first);

        Assert.Equal("Alice", rig.Server.Op("Alice"));
        rig.Until(() => a.Tokens.Count > 0, what: "a second token");
        string second = a.Tokens.Dequeue();
        Assert.NotEqual(first, second);
        Keep(rig, a, second);
        Assert.Equal("Alice", Assert.Single(rig.Server.Admins.Names));

        Leave(rig, a);
        Client old = rig.Join("Alice", token: first);
        rig.Until(() => old.Joined, what: "Alice to join with the token she had");
        rig.For(0.3);
        Assert.False(old.Admin);
    }

    // The hand-over is a datagram, and datagrams are lost. The token before it is not retired for
    // one that never arrived, and the new one is said until the game has it.
    [Fact]
    public void ATokenLostOnItsWayIsSaidAgainAndTheOneBeforeItWorksUntilItArrives()
    {
        using Rig rig = new();
        Client a = Opped(rig, out string first);

        rig.Server.Socket.Loss = 1.0;
        Assert.Equal("Alice", rig.Server.Op("Alice"));
        rig.For(0.3);
        Assert.Empty(a.Tokens);
        Assert.Equal(Wire.Role.Admin, rig.Server.Admins.Check(first, "Alice"));
        Assert.Equal("Alice", Assert.Single(rig.Server.Admins.Names));
        rig.Server.Socket.Loss = 0.0;

        rig.Until(() => a.Tokens.Count > 0, what: "the token to be said again");
        string second = a.Tokens.Dequeue();
        Keep(rig, a, second);
        Assert.Equal(Wire.Role.Player, rig.Server.Admins.Check(first, "Alice"));
        Assert.Equal(Wire.Role.Admin, rig.Server.Admins.Check(second, "Alice"));
    }

    // Arriving is not being kept: a game that could not write the token down has the old one on
    // disk when it next starts, and that has to be worth something.
    [Fact]
    public void ATokenIsNotRetiredUntilTheGameSaysItIsWrittenDown()
    {
        using Rig rig = new();
        Client a = Opped(rig, out string first);

        Assert.Equal("Alice", rig.Server.Op("Alice"));
        rig.Until(() => a.Tokens.Count > 0, what: "a second token");
        // Several tellings on, it is still one token to write down and still unanswered.
        rig.For(1.2);
        string second = Assert.Single(a.Tokens);
        Assert.False(Heard(rig));
        Assert.Equal(Wire.Role.Admin, rig.Server.Admins.Check(first, "Alice"));

        Keep(rig, a, a.Tokens.Dequeue());
        Assert.Equal(Wire.Role.Player, rig.Server.Admins.Check(first, "Alice"));
        Assert.Equal(Wire.Role.Admin, rig.Server.Admins.Check(second, "Alice"));
    }

    [Fact]
    public void AGamesAnswerLostOnItsWayIsGivenAgain()
    {
        using Rig rig = new();
        Client a = Opped(rig, out string first);

        Assert.Equal("Alice", rig.Server.Op("Alice"));
        rig.Until(() => a.Tokens.Count > 0, what: "a second token");
        a.Socket.Loss = 1.0;
        a.Kept(a.Tokens.Dequeue());
        rig.For(0.3);
        Assert.False(Heard(rig));
        Assert.Equal(Wire.Role.Admin, rig.Server.Admins.Check(first, "Alice"));
        a.Socket.Loss = 0.0;

        // Nothing more is asked of whoever runs the game: it was told once that the token is kept.
        rig.Until(() => Heard(rig), what: "the server to hear the token is kept");
        Assert.Empty(a.Tokens);
        Assert.Equal(Wire.Role.Player, rig.Server.Admins.Check(first, "Alice"));
    }

    [Fact]
    public void ATokenIsHandedOverALineThatLosesAThirdOfEverything()
    {
        using Rig rig = new();
        Client a = Opped(rig, out string first);
        (rig.Server.Socket.Loss, a.Socket.Loss) = (0.33, 0.33);

        for (int round = 0; round < 5; round++)
        {
            Assert.Equal("Alice", rig.Server.Op("Alice"));
            rig.Until(() => a.Tokens.Count > 0, seconds: 20, what: "a token over a lossy line");
            string next = Assert.Single(a.Tokens);
            a.Tokens.Clear();
            a.Kept(next);
            rig.Until(() => Heard(rig), seconds: 20, what: "the server to hear the token is kept over a lossy line");
            Assert.Equal(Wire.Role.Player, rig.Server.Admins.Check(first, "Alice"));
            Assert.Equal(Wire.Role.Admin, rig.Server.Admins.Check(next, "Alice"));
            first = next;
        }
        Assert.Equal("Alice", Assert.Single(rig.Server.Admins.Names));
    }

    // Gone before their game answered, the server cannot know which of the two they hold, so either
    // makes them an admin and the one they come back with is the one that stays.
    [Fact]
    public void AnAdminWhoLeftBeforeTheirGameAnsweredComesBackOneWithTheTokenTheyHad()
    {
        using Rig rig = new();
        Client a = Opped(rig, out string first);
        rig.Server.Socket.Loss = 1.0;
        Assert.Equal("Alice", rig.Server.Op("Alice"));
        rig.For(0.2);
        rig.Server.Socket.Loss = 0.0;
        Leave(rig, a);

        Client again = rig.Join("Alice", token: first);
        rig.Until(() => again.Admin, what: "Alice to be an admin by the token she had");
        Assert.Equal("Alice", Assert.Single(rig.Server.Admins.Names));
    }

    [Fact]
    public void AnAdminWhoLeftBeforeTheirGameAnsweredComesBackOneWithTheNewToken()
    {
        using Rig rig = new();
        Client a = Opped(rig, out string first);
        Assert.Equal("Alice", rig.Server.Op("Alice"));
        rig.Until(() => a.Tokens.Count > 0, what: "a second token");
        string second = a.Tokens.Dequeue();
        // Written down, and the game gone before it could say so.
        Leave(rig, a);

        Client again = rig.Join("Alice", token: second);
        rig.Until(() => again.Admin, what: "Alice to be an admin by the token she was handed");
        // Shown at a join is shown: the one before it has stopped working.
        Assert.Equal(Wire.Role.Player, rig.Server.Admins.Check(first, "Alice"));
        Assert.Equal("Alice", Assert.Single(rig.Server.Admins.Names));
    }

    // A deop is not a hand-over and waits for nothing: both the token kept and the one on its way
    // stop working the moment it is typed.
    [Fact]
    public void RevokingAnAdminTakesBackATokenStillOnItsWay()
    {
        using Rig rig = new();
        Client a = Opped(rig, out string first);
        Assert.Equal("Alice", rig.Server.Op("Alice"));
        rig.Until(() => a.Tokens.Count > 0, what: "a second token");
        string second = a.Tokens.Dequeue();

        Assert.True(rig.Server.Deop("Alice"));
        Assert.Empty(rig.Server.Admins.Names);
        Assert.Equal(Wire.Role.Player, rig.Server.Admins.Check(first, "Alice"));
        Assert.Equal(Wire.Role.Player, rig.Server.Admins.Check(second, "Alice"));
        // Too late to answer for it, and nothing it says now makes it an admin.
        a.Kept(second);
        rig.Until(() => !a.Admin && Heard(rig), what: "Alice to hear she is no admin");
        Assert.Empty(rig.Server.Admins.Names);
    }

    [Fact]
    public void ARevocationLostOnItsWayIsSaidAgain()
    {
        using Rig rig = new();
        Client a = Opped(rig, out _);

        rig.Server.Socket.Loss = 1.0;
        Assert.True(rig.Server.Deop("Alice"));
        rig.For(0.3);
        Assert.True(a.Admin);
        rig.Server.Socket.Loss = 0.0;

        rig.Until(() => !a.Admin, what: "Alice to hear she is no admin");
        rig.Until(() => Heard(rig), what: "the server to hear that she knows");
        Assert.Equal("", Assert.Single(a.Tokens));
    }

    // admins.json as every server before this one wrote it, with nothing said of whether a token
    // had arrived: all of them had, as far as that server knew.
    [Fact]
    public void AnAdminListWrittenByAnEarlierServerIsRead()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ksamp-admins-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("token-a")));
            File.WriteAllText(Path.Combine(folder, "admins.json"),
                              $$"""[{"Name": "Alice", "Hash": "{{hash}}", "SinceUtc": "2026-11-05T19:12:44.1Z"}]""");

            Admins read = new(folder);
            Assert.Equal("Alice", Assert.Single(read.Names));
            Assert.Equal(Wire.Role.Admin, read.Check("token-a", "Alice"));
            // Opped again by the server that read it, and nothing is lost until the new one is shown.
            string next = read.Offer("Alice");
            Assert.Equal(Wire.Role.Admin, new Admins(folder).Check("token-a", "Alice"));
            Assert.Equal(Wire.Role.Admin, read.Check(next, "Alice"));
            Assert.Equal(Wire.Role.Player, new Admins(folder).Check("token-a", "Alice"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    // Where a game keeps its tokens. A folder of its own for each test, and a book opened afresh
    // wherever a game would have been restarted.
    private sealed class Kept : IDisposable
    {
        private readonly string _folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ksamp-tokens-" + Guid.NewGuid().ToString("N"));

        public string Path => System.IO.Path.Combine(_folder, "KSAMultiplayer", "tokens.json");
        public List<string> Warned { get; } = [];
        public TokenBook Book() => new(Path, Warned.Add);

        public void Dispose()
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        }
    }

    private static readonly IPEndPoint Here = new(IPAddress.Parse("203.0.113.7"), 50100), Elsewhere = new(IPAddress.Parse("198.51.100.9"), 50100);

    // The day a server takes a new name, every admin's game looks under a name nothing is filed
    // under. The machine the name leads to is the one that handed the token over.
    [Fact]
    public void ATokenIsFoundWhenItsServerIsReachedUnderAnotherName()
    {
        using Kept kept = new();
        TokenBook book = kept.Book();
        Assert.Equal("", book.Joining("mp.ksarmory.com", Here));
        Assert.True(book.Keep("token-a"));

        book = kept.Book();
        Assert.Equal("token-a", book.Joining("ksamp.gg", Here));
        Assert.Equal("token-a", book.Joining("KSAMP.gg:50100 ", Here));
        Assert.Equal("token-a", book.Joining("203.0.113.7:50100", Here));
        Assert.Empty(kept.Warned);
    }

    // The other half of the same rule: a name that leads somewhere else is somebody else's server
    // until it has handed over a token of its own.
    [Fact]
    public void ATokenIsNotOfferedToAnotherAddress()
    {
        using Kept kept = new();
        TokenBook book = kept.Book();
        book.Joining("ksamp.gg", Here);
        Assert.True(book.Keep("token-a"));

        Assert.Equal("", book.Joining("other.example", Elsewhere));
        Assert.Equal("", book.Joining("other.example", new IPEndPoint(Here.Address, 50101)));
        // The name it was typed under still finds it wherever that name leads now, as it always has.
        Assert.Equal("token-a", book.Joining("ksamp.gg", Elsewhere));
    }

    [Fact]
    public void WhatIsFiledUnderTheTypedAddressComesBeforeWhatIsFiledUnderWhereItLed()
    {
        using Kept kept = new();
        TokenBook book = kept.Book();
        book.Joining("one.example", Here);
        book.Keep("token-one");
        // The address passes to another server, which hands over a token of its own.
        book.Joining("two.example", Here);
        book.Keep("token-two");

        Assert.Equal("token-one", book.Joining("one.example", Here));
        Assert.Equal("token-two", book.Joining("two.example", Here));
        Assert.Equal("token-two", book.Joining("three.example", Here));
    }

    // tokens.json as every mod before this one wrote it: the typed address and nothing else.
    [Fact]
    public void ATokenTheServerTakesIsFiledUnderWhereTheServerIs()
    {
        using Kept kept = new();
        Directory.CreateDirectory(Path.GetDirectoryName(kept.Path)!);
        File.WriteAllText(kept.Path, """{"mp.ksarmory.com":"token-a"}""");

        TokenBook book = kept.Book();
        Assert.Equal("token-a", book.Joining("mp.ksarmory.com", Here));
        Assert.Equal("", kept.Book().Joining("ksamp.gg", Here));
        // The server said it is an admin's, which is the queue handing back what was offered.
        Assert.True(book.Keep("token-a"));
        Assert.Equal("token-a", kept.Book().Joining("ksamp.gg", Here));
    }

    [Fact]
    public void ARefusedTokenIsThrownAwayAndNothingElseIs()
    {
        using Kept kept = new();
        TokenBook book = kept.Book();
        book.Joining("one.example", Here);
        book.Keep("token-one");
        book.Joining("two.example", Elsewhere);
        book.Keep("token-two");

        // Reached under a third name, at the first one's address, and refused there.
        Assert.Equal("token-one", book.Joining("three.example", Here));
        book.Forget();

        book = kept.Book();
        Assert.Equal("", book.Joining("three.example", Here));
        // Still filed under the name it was typed as, which may lead to the server that takes it.
        Assert.Equal("token-one", book.Joining("one.example", Elsewhere));
        Assert.Equal("token-two", book.Joining("two.example", Elsewhere));
        // A game that offered nothing and is told it is no admin has nothing to throw away.
        Assert.Equal("", book.Joining("four.example", new IPEndPoint(Elsewhere.Address, 50200)));
        book.Forget();
        Assert.Equal("token-two", kept.Book().Joining("two.example", Elsewhere));
    }

    [Fact]
    public void ATokenThatCouldNotBeWrittenIsNotSaidToBeKept()
    {
        using Kept kept = new();
        // A file where the folder should be.
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetDirectoryName(kept.Path)!)!);
        File.WriteAllText(Path.GetDirectoryName(kept.Path)!, "");

        TokenBook book = kept.Book();
        book.Joining("ksamp.gg", Here);
        Assert.False(book.Keep("token-a"));
        Assert.False(book.Keep("token-a"));
        Assert.Equal(2, kept.Warned.Count);
        // For as long as the game runs it is still what is offered.
        Assert.Equal("token-a", book.Joining("ksamp.gg", Here));
    }

    // Both halves together, as a game does them: opped under one name, restarted, and back under
    // another with nothing typed.
    [Fact]
    public void AnAdminIsOneAgainWhenTheServerHasTakenAnotherName()
    {
        using Rig rig = new();
        using Kept kept = new();
        IPEndPoint at = new(IPAddress.Loopback, rig.Server.Port);

        TokenBook book = kept.Book();
        Client a = rig.Join("Alice", token: book.Joining("mp.ksarmory.com", at));
        rig.Until(() => a.Joined, what: "Alice to join");
        Assert.Equal("Alice", rig.Server.Op("Alice"));
        rig.Until(() => a.Tokens.Count > 0, what: "Alice to be handed a token");
        string token = a.Tokens.Dequeue();
        Assert.True(book.Keep(token));
        Keep(rig, a, token);
        Leave(rig, a);

        book = kept.Book();
        Client again = rig.Join("Alice", token: book.Joining("ksamp.gg", at));
        rig.Until(() => again.Admin, what: "Alice to be an admin under the server's new name");
        Assert.True(book.Keep(again.Tokens.Dequeue()));
        Assert.Equal(token, kept.Book().Joining("ksamp.gg", Elsewhere));
    }
}
