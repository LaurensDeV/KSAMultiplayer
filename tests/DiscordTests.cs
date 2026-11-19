using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using KSAMultiplayer.Directories;
using KSAMultiplayer.Server;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// Logging in with Discord through the one login service, and a Discord user who is an admin from
/// whichever machine they log in on. The service is stood in for by a key made here: what is
/// tested is what the server takes and refuses, and what the service signs.
/// </summary>
public class DiscordTests
{
    private const string Alice = "111111111111111111", Bob = "222222222222222222";

    private sealed class FakeLogin : ILoginService
    {
        public readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public int Resolved;

        public Task<LoginKey> Key_() => Task.FromResult(new LoginKey(Key, []));

        Task<LoginKey> ILoginService.Key(CancellationToken cancel) => Key_();

        public Task<IPAddress[]> Resolve(string typed, CancellationToken cancel)
        {
            Interlocked.Increment(ref Resolved);
            // Somewhere that is not this machine, as another server would be.
            return typed == "rogue.example" ? Task.FromResult<IPAddress[]>([IPAddress.Parse("203.0.113.9")]) : Addresses.Of(typed, cancel);
        }

        public string Pass(string id, string nonce, string aud = "127.0.0.1", long? exp = null, ECDsa? by = null) =>
            new DiscordPass(id, id == Alice ? "alice" : "bob", aud, nonce, exp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300).Sign(by ?? Key);
    }

    private static Rig Rigged(out FakeLogin login, params string[] discordAdmins)
    {
        login = new FakeLogin();
        Rig rig = new(discord: login);
        foreach (string id in discordAdmins) rig.Server.OpDiscord(id);
        return rig;
    }

    private static Client Joined(Rig rig, string name)
    {
        Client c = rig.Join(name);
        rig.Until(() => c.Joined, what: $"{name} to join");
        return c;
    }

    private static void LogIn(Rig rig, Client c, string pass)
    {
        c.LogInWithDiscord(pass);
        rig.Until(() => !c.DiscordAsking, what: "the server to answer the Discord login");
    }

    private static void Leave(Rig rig, Client c)
    {
        c.Dispose();
        rig.Clients.Remove(c);
        rig.Until(() => rig.Server.Players.Count == 0, what: "the server to let go of them");
    }

    // Nobody to recognise, nothing to log in with: a server with no Discord admin never asks the service.
    [Fact]
    public void AServerWithNoDiscordAdminTakesNoLogins()
    {
        using Rig rig = Rigged(out FakeLogin login);
        Client c = Joined(rig, "Alice");
        Assert.Equal("", c.DiscordNonce);
        LogIn(rig, c, login.Pass(Alice, "anything"));
        Assert.Contains("doesn't take Discord logins", c.Discord!.Refused);
        Assert.False(rig.Server.DiscordOn);
    }

    [Fact]
    public void ADiscordAdminLogsInAndIsAnAdmin()
    {
        using Rig rig = Rigged(out FakeLogin login, Alice);
        Client c = Joined(rig, "Alice");
        Assert.NotEqual("", c.DiscordNonce);
        rig.For(0.3);
        Assert.False(c.Admin);

        LogIn(rig, c, login.Pass(Alice, c.DiscordNonce));
        Assert.Equal(Alice, c.Discord!.Id);
        Assert.Equal("alice", c.Discord.Name);
        rig.Until(() => c.Admin && c.Tokens.Count > 0, what: "Alice to be made an admin and handed a token");
        Assert.Equal(Alice, rig.Server.Admins.DiscordOf("Alice"));
    }

    // The first Discord admin is what turns logins on: whoever was already playing is told the
    // nonce then, rather than having to join again.
    [Fact]
    public void APlayerAlreadyHereCanLogInOnceTheFirstDiscordAdminIsMade()
    {
        using Rig rig = Rigged(out FakeLogin login);
        Client c = Joined(rig, "Alice");
        Assert.Equal("", c.DiscordNonce);
        rig.Server.OpDiscord(Alice);
        rig.Until(() => c.DiscordNonce.Length > 0, what: "Alice to be given a nonce");
        LogIn(rig, c, login.Pass(Alice, c.DiscordNonce));
        rig.Until(() => c.Admin, what: "Alice to be an admin");
    }

    [Fact]
    public void AnotherDiscordUserLogsInAndIsNoAdmin()
    {
        using Rig rig = Rigged(out FakeLogin login, Alice);
        Client c = Joined(rig, "Bob");
        LogIn(rig, c, login.Pass(Bob, c.DiscordNonce));
        Assert.Equal(Bob, c.Discord!.Id);
        Assert.Equal(Bob, Assert.Single(rig.Server.Players).DiscordId);
        rig.For(0.3);
        Assert.False(c.Admin);
    }

    // The relay a pass has to survive: a server that had Alice log in on it hands her pass on. It
    // names that server's address, which does not lead here.
    [Fact]
    public void APassForAnotherServersAddressIsRefused()
    {
        using Rig rig = Rigged(out FakeLogin login, Alice);
        Client c = Joined(rig, "Mallory");
        LogIn(rig, c, login.Pass(Alice, c.DiscordNonce, aud: "rogue.example"));
        Assert.Equal("", c.Discord!.Id);
        Assert.Contains("isn't this server", c.Discord.Refused);
        rig.For(0.3);
        Assert.False(c.Admin);
    }

    // And with the right address, a pass made for one player's login is no good for another's.
    [Fact]
    public void APassForAnotherLoginIsRefused()
    {
        using Rig rig = Rigged(out FakeLogin login, Alice);
        Client alice = Joined(rig, "Alice");
        Client mallory = Joined(rig, "Mallory");
        LogIn(rig, mallory, login.Pass(Alice, alice.DiscordNonce));
        Assert.Contains("another login", mallory.Discord!.Refused);
    }

    [Fact]
    public void APassNotSignedByTheServiceIsRefused()
    {
        using Rig rig = Rigged(out FakeLogin login, Alice);
        Client c = Joined(rig, "Alice");
        using ECDsa forged = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        LogIn(rig, c, login.Pass(Alice, c.DiscordNonce, by: forged));
        Assert.Contains("isn't signed", c.Discord!.Refused);
        LogIn(rig, c, "not.a-pass");
        Assert.False(c.Admin);
    }

    [Fact]
    public void APassThatHasRunOutIsRefused()
    {
        using Rig rig = Rigged(out FakeLogin login, Alice);
        Client c = Joined(rig, "Alice");
        LogIn(rig, c, login.Pass(Alice, c.DiscordNonce, exp: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1));
        Assert.Contains("run out", c.Discord!.Refused);
    }

    // A pass said again because its answer was lost is answered from what the server kept.
    [Fact]
    public void APassSaidAgainIsAnsweredAgainAndCheckedOnce()
    {
        using Rig rig = Rigged(out FakeLogin login, Alice);
        Client c = Joined(rig, "Alice");
        string pass = login.Pass(Alice, c.DiscordNonce);
        LogIn(rig, c, pass);
        LogIn(rig, c, pass);
        Assert.Equal(Alice, c.Discord!.Id);
        Assert.Equal(1, login.Resolved);
    }

    // What "permanently" means: opped once by name while logged in, they are an admin on a machine
    // that has never seen this server, under whatever name they play as there.
    [Fact]
    public void OppingAPlayerLoggedInWithDiscordFollowsThemToAnotherMachine()
    {
        using Rig rig = Rigged(out FakeLogin login, Bob);
        Client first = Joined(rig, "Alice");
        LogIn(rig, first, login.Pass(Alice, first.DiscordNonce));
        Assert.Equal("Alice", rig.Server.Op("Alice"));
        rig.Until(() => first.Admin, what: "Alice to be an admin");
        Leave(rig, first);

        Client elsewhere = Joined(rig, "AliceOnHerLaptop");
        rig.For(0.3);
        Assert.False(elsewhere.Admin);
        LogIn(rig, elsewhere, login.Pass(Alice, elsewhere.DiscordNonce));
        rig.Until(() => elsewhere.Admin, what: "Alice to be an admin on her laptop");
        Assert.Equal(Alice, rig.Server.Admins.DiscordOf("AliceOnHerLaptop"));
    }

    [Fact]
    public void DeoppingADiscordUserTakesItAwayFromThePlayerAndEveryTokenOfTheirs()
    {
        using Rig rig = Rigged(out FakeLogin login, Alice);
        Client c = Joined(rig, "Alice");
        LogIn(rig, c, login.Pass(Alice, c.DiscordNonce));
        rig.Until(() => c.Admin && c.Tokens.Count > 0, what: "Alice to be an admin");
        string token = c.Tokens.Dequeue();
        c.Kept(token);
        rig.Until(() => rig.Server.Players.All(p => p.Telling is null), what: "the token to be kept");
        Assert.Equal(Alice, rig.Server.Admins.DiscordOf("Alice"));

        Assert.Equal("Alice", rig.Server.DeopDiscord(Alice));
        rig.Until(() => !c.Admin, what: "Alice to hear she is no admin");
        Assert.Empty(rig.Server.Admins.Names);
        Assert.Null(rig.Server.DeopDiscord(Alice));

        Leave(rig, c);
        Client again = rig.Join("Alice", token: token);
        rig.Until(() => again.Joined, what: "Alice to join again");
        rig.For(0.3);
        Assert.False(again.Admin);
    }

    [Fact]
    public void DeoppingByNameTakesTheDiscordUserOffTheListToo()
    {
        using Rig rig = Rigged(out FakeLogin login, Alice);
        Client c = Joined(rig, "Alice");
        LogIn(rig, c, login.Pass(Alice, c.DiscordNonce));
        rig.Until(() => c.Admin, what: "Alice to be an admin");
        Assert.True(rig.Server.Deop("Alice"));
        Assert.Null(rig.Server.Admins.NameOfDiscord(Alice));
        Assert.False(rig.Server.DiscordOn);
    }

    [Fact]
    public void ADiscordAdminIsKeptAcrossARestart()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ksamp-discord-" + Guid.NewGuid().ToString("N"));
        try
        {
            new Admins(folder).AddDiscord(Alice, "Alice");
            Assert.True(new Admins(folder).CheckDiscord(Alice, "Alicia"));
            Assert.Equal(Alice, new Admins(folder).DiscordOf("Alicia"));
            Assert.False(new Admins(folder).CheckDiscord(Bob, "Bob"));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    // ------------------------------------------------------------------ the service and the game's side

    [Fact]
    public void TheServiceSignsAPassForTheLoginItWasAskedAndTheGameReadsItBack()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Login service = new("1573012247616164064", "secret", "https://servers.ksamp.gg/v1/discord/callback", key);
        Assert.True(service.On);

        string state = Discord.State(" KSA.Example.com:50101 ", "n0nce", "mine");
        Discord.LoginState asked = Discord.ReadState(state)!;
        Assert.Equal("ksa.example.com:50101", asked.A);

        string pass = service.Pass(asked, Alice, "alice", DateTimeOffset.UtcNow);
        string back = Login.BackToGame(state, pass, null);
        Assert.StartsWith("http://127.0.0.1:50180/ksamp?", back);
        (string? got, string? error) = Discord.ReadCallback(back[back.IndexOf("/ksamp", StringComparison.Ordinal)..], state);
        Assert.Null(error);
        Assert.Equal(pass, got);

        using ECDsa checker = ECDsa.Create();
        checker.ImportSubjectPublicKeyInfo(Convert.FromBase64String(service.PublicKey), out _);
        DiscordPass said = DiscordPass.Verify(got!, checker)!;
        Assert.Equal((Alice, "ksa.example.com:50101", "n0nce"), (said.Id, said.Aud, said.N));
    }

    [Fact]
    public void TheServiceAsksDiscordForIdentifyAlone()
    {
        Login service = new("1573012247616164064", "secret", "https://servers.ksamp.gg/v1/discord/callback", ECDsa.Create());
        string url = service.Authorize("st");
        Assert.StartsWith("https://discord.com/oauth2/authorize?", url);
        Assert.Contains("client_id=1573012247616164064", url);
        Assert.Contains("scope=identify&", url);
        Assert.Contains("redirect_uri=https%3A%2F%2Fservers.ksamp.gg%2Fv1%2Fdiscord%2Fcallback", url);
        Assert.False(new Login("", "", "", null).On);
    }

    [Fact]
    public void AnAnswerIsReadOnlyWithTheStateThisLoginSent()
    {
        Assert.Equal(("p.s", null), Discord.ReadCallback("/ksamp?pass=p.s&state=s1", "s1"));
        Assert.Null(Discord.ReadCallback("/ksamp?pass=p.s&state=other", "s1").Pass);
        Assert.Null(Discord.ReadCallback("/ksamp?pass=p.s", "s1").Pass);
        Assert.Contains("cancelled", Discord.ReadCallback("/ksamp?error=access_denied&state=s1", "s1").Error);
        Assert.Null(Discord.ReadState("not base64 json"));
    }

    [Fact]
    public void TheCallbackWaitsOutAnAnswerForAnotherLoginAndTakesItsOwn()
    {
        using DiscordCallback callback = new("127.0.0.1", "n0nce", port: 0);
        Assert.Contains("400", Get(callback.Port, "/ksamp?pass=theirs&state=wrong"));
        Assert.Contains("404", Get(callback.Port, "/favicon.ico"));
        Assert.False(callback.Finished);

        Assert.Contains("200", Get(callback.Port, $"/ksamp?pass=ours&state={Uri.EscapeDataString(callback.State)}"));
        SpinWait.SpinUntil(() => callback.Finished, TimeSpan.FromSeconds(5));
        Assert.Equal("ours", callback.Pass);
    }

    [Theory]
    [InlineData("ksa.example.com", "ksa.example.com", null)]
    [InlineData("ksa.example.com:50200", "ksa.example.com", 50200)]
    [InlineData("[2001:db8::1]:50200", "2001:db8::1", 50200)]
    [InlineData("2001:db8::1", "2001:db8::1", null)]
    public void AnAddressIsAHostAndThePortTypedAfterIt(string typed, string host, int? port) =>
        Assert.Equal((host, port), Addresses.Split(typed));

    [Fact]
    public void TheDiscordDatagramsReadBackAsTheyWereWritten()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pass = new DiscordPass(Alice, "alice", "ksa.example.com", "n0nce", 1).Sign(key);
        byte[] buffer = new byte[Wire.BufferBytes];
        foreach (Wire.Message m in new Wire.Message[]
                 {
                     new Wire.DiscordPass(pass),
                     new Wire.DiscordUser(Alice, "alice", ""),
                     new Wire.Welcome(3, "", "Moon base", true, System: "Sol", Discord: "n0nce"),
                 })
        {
            int n = Wire.Write(m, buffer);
            Assert.Equal(m, Wire.Read(buffer.AsSpan(0, n), out _));
        }
    }

    private static string Get(int port, string target)
    {
        using TcpClient client = new();
        client.Connect(IPAddress.Loopback, port);
        using NetworkStream stream = client.GetStream();
        stream.Write(Encoding.ASCII.GetBytes($"GET {target} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));
        using StreamReader reader = new(stream);
        return reader.ReadLine() ?? "";
    }
}
