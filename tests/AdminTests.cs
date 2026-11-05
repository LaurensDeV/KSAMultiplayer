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
        return c;
    }

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

    // The token has to reach a game, and is said once: there is nowhere to leave it for somebody who
    // is not here, and nothing is written down that could say it a second time.
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
        Assert.Contains(b.Chat, said => said.Text.Contains("not an admin"));
    }

    [Fact]
    public void AnAdminComesBackAnAdminWithNothingTyped()
    {
        using Rig rig = new();
        Leave(rig, Opped(rig, out string token));

        Client again = rig.Join("Alice", token: token);
        rig.Until(() => again.Admin, what: "Alice to be an admin again");
        // Nothing new to write down: what she offered is what she has.
        Assert.Empty(again.Tokens);
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

        a.Ask("status");
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
            Assert.True(read.Check(token, "Alice"));
            Assert.False(read.Check(token[..^1] + "x", "Alice"));
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
        Assert.Equal("Alice", Assert.Single(rig.Server.Admins.Names));

        Leave(rig, a);
        Client old = rig.Join("Alice", token: first);
        rig.Until(() => old.Joined, what: "Alice to join with the token she had");
        rig.For(0.3);
        Assert.False(old.Admin);
    }
}
