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

        /// <summary>What a game logged in as this user proves at a join: a certificate for a key of its own, and that key's signature over the nonce and the address.</summary>
        public (string Cert, string Aud, string Signature) Proof(string id, string nonce, string aud = "127.0.0.1", long? exp = null, ECDsa? by = null, ECDsa? machine = null)
        {
            ECDsa mine = machine ?? ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string spki = System.Buffers.Text.Base64Url.EncodeToString(mine.ExportSubjectPublicKeyInfo());
            string cert = new DiscordCert(id, id == Alice ? "alice" : "bob", spki, exp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Discord.CertSeconds).Sign(by ?? Key);
            string signature = System.Buffers.Text.Base64Url.EncodeToString(mine.SignData(Discord.Proof(nonce, aud), HashAlgorithmName.SHA256));
            return (cert, aud, signature);
        }
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

    private static void LogIn(Rig rig, Client c, (string Cert, string Aud, string Signature) proof)
    {
        c.ProveDiscord(proof.Cert, proof.Aud, proof.Signature);
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
        LogIn(rig, c, login.Proof(Alice, "anything"));
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

        LogIn(rig, c, login.Proof(Alice, c.DiscordNonce));
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
        LogIn(rig, c, login.Proof(Alice, c.DiscordNonce));
        rig.Until(() => c.Admin, what: "Alice to be an admin");
    }

    // Everybody's list marks a player whose login the server checked, and says nothing of who they are on Discord.
    [Fact]
    public void EveryPlayersListMarksWhoHasLoggedIn()
    {
        using Rig rig = Rigged(out FakeLogin login, Alice);
        Client alice = Joined(rig, "Alice");
        Client bob = Joined(rig, "Bob");
        LogIn(rig, alice, login.Proof(Alice, alice.DiscordNonce));
        rig.Until(() => bob.Players.Any(p => p.Name == "Alice" && p.Discord), what: "Bob to see Alice marked");
        Assert.False(bob.Players.Single(p => p.Name == "Bob").Discord);
        Assert.True(alice.Players.Single(p => p.Name == "Alice").Discord);
    }

    [Fact]
    public void AnotherDiscordUserLogsInAndIsNoAdmin()
    {
        using Rig rig = Rigged(out FakeLogin login, Alice);
        Client c = Joined(rig, "Bob");
        LogIn(rig, c, login.Proof(Bob, c.DiscordNonce));
        Assert.Equal(Bob, c.Discord!.Id);
        Assert.Equal(Bob, Assert.Single(rig.Server.Players).DiscordId);
        rig.For(0.3);
        Assert.False(c.Admin);
    }

    // The relay a proof has to survive: a server Alice joined hands on what she proved to it. It
    // names that server's address, which does not lead here.
    [Fact]
    public void AProofForAnotherServersAddressIsRefused()
    {
        using Rig rig = Rigged(out FakeLogin login, Alice);
        Client c = Joined(rig, "Mallory");
        LogIn(rig, c, login.Proof(Alice, c.DiscordNonce, aud: "rogue.example"));
        Assert.Equal("", c.Discord!.Id);
        Assert.Contains("isn't this server", c.Discord.Refused);
        rig.For(0.3);
        Assert.False(c.Admin);
    }

    // And with the right address, a proof made for one player's join is no good for another's.
    [Fact]
    public void AProofForAnotherJoinIsRefused()
    {
        using Rig rig = Rigged(out FakeLogin login, Alice);
        Client alice = Joined(rig, "Alice");
        Client mallory = Joined(rig, "Mallory");
        LogIn(rig, mallory, login.Proof(Alice, alice.DiscordNonce));
        Assert.Contains("wasn't proved for this join", mallory.Discord!.Refused);
    }

    [Fact]
    public void ACertificateNotSignedByTheServiceIsRefused()
    {
        using Rig rig = Rigged(out FakeLogin login, Alice);
        Client c = Joined(rig, "Alice");
        using ECDsa forged = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        LogIn(rig, c, login.Proof(Alice, c.DiscordNonce, by: forged));
        Assert.Contains("isn't signed by the login service", c.Discord!.Refused);
        LogIn(rig, c, ("not.a-cert", "127.0.0.1", "x"));
        Assert.False(c.Admin);
    }

    [Fact]
    public void ALoginThatHasRunOutIsRefusedByTheServer()
    {
        using Rig rig = Rigged(out FakeLogin login, Alice);
        Client c = Joined(rig, "Alice");
        LogIn(rig, c, login.Proof(Alice, c.DiscordNonce, exp: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1));
        Assert.Contains("run out", c.Discord!.Refused);
    }

    // A proof said again because its answer was lost is answered from what the server kept.
    [Fact]
    public void AProofSaidAgainIsAnsweredAgainAndCheckedOnce()
    {
        using Rig rig = Rigged(out FakeLogin login, Alice);
        Client c = Joined(rig, "Alice");
        var proof = login.Proof(Alice, c.DiscordNonce);
        LogIn(rig, c, proof);
        LogIn(rig, c, proof);
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
        LogIn(rig, first, login.Proof(Alice, first.DiscordNonce));
        Assert.Equal("Alice", rig.Server.Op("Alice"));
        rig.Until(() => first.Admin, what: "Alice to be an admin");
        Leave(rig, first);

        Client elsewhere = Joined(rig, "AliceOnHerLaptop");
        rig.For(0.3);
        Assert.False(elsewhere.Admin);
        LogIn(rig, elsewhere, login.Proof(Alice, elsewhere.DiscordNonce));
        rig.Until(() => elsewhere.Admin, what: "Alice to be an admin on her laptop");
        Assert.Equal(Alice, rig.Server.Admins.DiscordOf("AliceOnHerLaptop"));
    }

    [Fact]
    public void DeoppingADiscordUserTakesItAwayFromThePlayerAndEveryTokenOfTheirs()
    {
        using Rig rig = Rigged(out FakeLogin login, Alice);
        Client c = Joined(rig, "Alice");
        LogIn(rig, c, login.Proof(Alice, c.DiscordNonce));
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
        LogIn(rig, c, login.Proof(Alice, c.DiscordNonce));
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

    // A certificate is public: every server a player joins is handed it. Without the key it names,
    // which never leaves their machine, it proves nothing.
    [Fact]
    public void ACertificateWithoutItsKeyIsRefused()
    {
        using Rig rig = Rigged(out FakeLogin login, Alice);
        Client c = Joined(rig, "Mallory");
        using ECDsa alices = ECDsa.Create(ECCurve.NamedCurves.nistP256), mallorys = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        (string cert, string aud, _) = login.Proof(Alice, "elsewhere", machine: alices);
        string signed = System.Buffers.Text.Base64Url.EncodeToString(mallorys.SignData(Discord.Proof(c.DiscordNonce, aud), HashAlgorithmName.SHA256));
        LogIn(rig, c, (cert, aud, signed));
        Assert.Contains("wasn't proved for this join", c.Discord!.Refused);
    }

    // ------------------------------------------------------------------ the service and the game's side

    // The whole of a machine's login, from the key it makes to what it hands a server at a join.
    [Fact]
    public void AMachineLogsInOnceAndProvesItAtEveryJoin()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ksamp-discord-" + Guid.NewGuid().ToString("N"));
        try
        {
            using ECDsa signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Login service = new("1573012247616164064", "secret", "https://servers.ksamp.gg/v1/discord/callback", signing);
            Assert.True(service.On);

            DiscordIdentity machine = new(Path.Combine(folder, "discord.json"));
            Assert.Equal("", machine.Cert);
            (ECDsa key, string spki) = machine.NewKey();
            string state = Discord.State(spki, "mine");
            Discord.LoginState asked = Discord.ReadState(state)!;

            string back = Login.BackToGame(state, service.Cert(asked, Alice, "alice", DateTimeOffset.UtcNow), null);
            Assert.StartsWith("http://127.0.0.1:50180/ksamp?", back);
            (string? cert, string? error) = Discord.ReadCallback(back[back.IndexOf("/ksamp", StringComparison.Ordinal)..], state);
            Assert.Null(error);
            Assert.True(machine.Keep(key, cert!));

            // Read back by another game started on the same machine.
            DiscordIdentity again = new(Path.Combine(folder, "discord.json"));
            Assert.Equal("alice", again.Who!.Name);
            using ECDsa checker = ECDsa.Create();
            checker.ImportSubjectPublicKeyInfo(Convert.FromBase64String(service.PublicKey), out _);
            foreach (string nonce in new[] { "first-join", "second-join" })
            {
                (string c, string signature) = again.Prove(nonce, " KSA.Example.com ")!.Value;
                DiscordCert said = DiscordCert.Verify(c, checker)!;
                Assert.Equal(Alice, said.Id);
                Assert.True(said.Signed(nonce, "ksa.example.com", signature));
                Assert.False(said.Signed("another-join", "ksa.example.com", signature));
                Assert.False(said.Signed(nonce, "rogue.example", signature));
            }

            again.Forget();
            Assert.Null(new DiscordIdentity(Path.Combine(folder, "discord.json")).Prove("n", "a"));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ALoginThatHasRunOutIsNoLogin()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ksamp-discord-" + Guid.NewGuid().ToString("N"));
        try
        {
            using ECDsa signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            DiscordIdentity machine = new(Path.Combine(folder, "discord.json"));
            (ECDsa key, string spki) = machine.NewKey();
            machine.Keep(key, new DiscordCert(Alice, "alice", spki, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1).Sign(signing));
            Assert.Equal("", machine.Cert);
            Assert.Null(machine.Prove("n", "a"));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
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
        Assert.Equal(("c.s", null), Discord.ReadCallback("/ksamp?cert=c.s&state=s1", "s1"));
        Assert.Null(Discord.ReadCallback("/ksamp?cert=c.s&state=other", "s1").Cert);
        Assert.Null(Discord.ReadCallback("/ksamp?cert=c.s", "s1").Cert);
        Assert.Contains("cancelled", Discord.ReadCallback("/ksamp?error=access_denied&state=s1", "s1").Error);
        Assert.Null(Discord.ReadState("not base64 json"));
        // A state whose key is no key is no login's.
        Assert.Null(Discord.ReadState(Discord.State("bm90IGEga2V5", "mine")));
    }

    [Fact]
    public void TheCallbackWaitsOutAnAnswerForAnotherLoginAndTakesItsOwn()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using DiscordCallback callback = new(System.Buffers.Text.Base64Url.EncodeToString(key.ExportSubjectPublicKeyInfo()), port: 0);
        Assert.Contains("400", Get(callback.Port, "/ksamp?cert=theirs&state=wrong"));
        Assert.Contains("404", Get(callback.Port, "/favicon.ico"));
        Assert.False(callback.Finished);

        Assert.Contains("200", Get(callback.Port, $"/ksamp?cert=ours&state={Uri.EscapeDataString(callback.State)}"));
        SpinWait.SpinUntil(() => callback.Finished, TimeSpan.FromSeconds(5));
        Assert.Equal("ours", callback.Cert);
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
        string cert = new DiscordCert(Alice, "alice", "a2V5", 1).Sign(key);
        byte[] buffer = new byte[Wire.BufferBytes];
        foreach (Wire.Message m in new Wire.Message[]
                 {
                     new Wire.DiscordProof(cert, "ksa.example.com", "c2lnbmF0dXJl"),
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
