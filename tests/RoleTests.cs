using System.Security.Cryptography;
using KSAMultiplayer.Server;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// Admins and trusted players: who may run what, what the role is on the wire, the bin every lost
/// save goes to for thirty days, and the log of who did what.
/// </summary>
public class RoleTests
{
    private static Client Given(Rig rig, string name, Wire.Role role)
    {
        Client c = rig.Join(name);
        rig.Until(() => c.Joined, what: $"{name} to join");
        Assert.Equal(name, role == Wire.Role.Admin ? rig.Server.Op(name) : rig.Server.Trust(name));
        rig.Until(() => c.Role == role, what: $"{name} to hear what they are");
        return c;
    }

    private static string Asks(Rig rig, Client c, string line)
    {
        rig.Server.Asked.Clear();
        int heard = c.Chat.Count;
        c.Ask(line);
        rig.Until(() => rig.Server.Asked.Count > 0 || c.Chat.Count > heard, what: $"the server to take or refuse '{line}'");
        return rig.Server.Asked.Count > 0 ? "run" : c.Chat.Last().Text;
    }

    [Fact]
    public void ATrustedPlayerKeepsTheWorldTidyAndAnAdminDoesWhatCannotBePutBack()
    {
        using Rig rig = new();
        Client t = Given(rig, "Tess", Wire.Role.Trusted);
        Assert.True(t.Trusted);
        Assert.False(t.Admin);
        foreach (string allowed in new[] { "clear Rocket_*", "remove Rocket", "protect Station", "unprotect Station", "rename A -> B", "save Monday", "load Monday", "kick Bob", "say hi", "status" })
        {
            Assert.Equal("run", Asks(rig, t, allowed));
        }
        foreach (string refused in new[] { "delete Monday", "reload", "op Bob", "trust Bob", "deop Bob", "deleted", "restore Monday", "something new" })
        {
            Assert.Contains("only an admin", Asks(rig, t, refused));
        }

        Client a = Given(rig, "Ada", Wire.Role.Admin);
        Assert.Equal("run", Asks(rig, a, "delete Monday"));
        Assert.Equal("run", Asks(rig, a, "trust Bob"));
    }

    [Fact]
    public void APlayerMayReadButNotChange()
    {
        using Rig rig = new();
        Client p = rig.Join("Pat");
        rig.Until(() => p.Joined, what: "Pat to join");
        Assert.Equal("run", Asks(rig, p, "players"));
        Assert.Equal("run", Asks(rig, p, "saves"));
        Assert.Contains("only trusted players and admins", Asks(rig, p, "clear *"));
    }

    // A trusted player never loses a save: saving over one is losing it.
    [Fact]
    public void ATrustedPlayerCannotSaveOverASave()
    {
        using Rig rig = new();
        Client a = Given(rig, "Ada", Wire.Role.Admin);
        Client t = Given(rig, "Tess", Wire.Role.Trusted);
        rig.Server.SetWorld(new Blob(BlobKind.World, "", 0, 10.0, Rig.FakeWorld(2)), "B1", ["Core"], "SolLite");
        rig.Server.Saves.Keep("Monday", rig.Server.World!, "B1", ["Core"], "SolLite", true, "Ada");
        GameServer.Player tess = rig.Server.Players.Single(p => p.Name == "Tess");
        GameServer.Player ada = rig.Server.Players.Single(p => p.Name == "Ada");
        Assert.Contains("already a save called 'Monday'", rig.Server.Keep("monday", tess));
        Assert.DoesNotContain("already a save", rig.Server.Keep("Monday", ada));
        Assert.DoesNotContain("already a save", rig.Server.Keep("Tuesday", tess));
    }

    // An admin trusted is made trusted, token and all: a name is one thing on a server.
    [Fact]
    public void TrustingAnAdminMakesThemTrusted()
    {
        using Rig rig = new();
        Client c = Given(rig, "Ada", Wire.Role.Admin);
        rig.Until(() => c.Tokens.Count > 0, what: "Ada's game to be handed a token");
        string token = c.Tokens.Dequeue();
        // Kept, as a game does: an offer nobody took up is withdrawn by the next.
        c.Kept(token);
        rig.Until(() => rig.Server.Players.All(p => p.Telling is null), what: "the token to be kept");
        rig.Server.Trust("Ada");
        rig.Until(() => c.Role == Wire.Role.Trusted, what: "Ada to hear she is trusted");
        Assert.Equal(Wire.Role.Trusted, rig.Server.Admins.Check(token, "Ada"));
        Assert.Equal(Wire.Role.Trusted, rig.Server.Admins.RoleOf("Ada"));
    }

    [Fact]
    public void ADiscordUserIsTrustedBySetting()
    {
        const string Tess = "333333333333333333";
        using ECDsa service = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        StoodIn login = new(service);
        using Rig rig = new(new ServerOptions { DiscordTrusted = [Tess] }, discord: login);
        Client c = rig.Join("Tess");
        rig.Until(() => c.Joined && c.DiscordNonce.Length > 0, what: "Tess to join a server taking Discord logins");
        using ECDsa machine = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string spki = System.Buffers.Text.Base64Url.EncodeToString(machine.ExportSubjectPublicKeyInfo());
        string cert = new DiscordCert(Tess, "tess", spki, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60).Sign(service);
        c.ProveDiscord(cert, "127.0.0.1", System.Buffers.Text.Base64Url.EncodeToString(machine.SignData(Discord.Proof(c.DiscordNonce, "127.0.0.1"), HashAlgorithmName.SHA256)));
        rig.Until(() => c.Role == Wire.Role.Trusted, what: "Tess to be trusted by the setting");
        Assert.Equal(Wire.Role.Trusted, rig.Server.RoleBySetting(Tess));
        Assert.Contains(rig.Server.Audit.Lines, l => l.Contains("Tess") && l.Contains("trusted by the setting"));
    }

    private sealed class StoodIn(ECDsa key) : ILoginService
    {
        public Task<LoginKey> Key(CancellationToken cancel) => Task.FromResult(new LoginKey(key, []));
        public Task<System.Net.IPAddress[]> Resolve(string typed, CancellationToken cancel) => Addresses.Of(typed, cancel);
    }

    // ------------------------------------------------------------------ the wire

    [Fact]
    public void TheRoleCrossesTheWireAndAGameOf0260IsReadAsItWas()
    {
        byte[] buffer = new byte[Wire.BufferBytes];
        Wire.Op told = new(true, "tok", Wire.Role.Trusted);
        int n = Wire.Write(told, buffer);
        Assert.Equal(told, Wire.Read(buffer.AsSpan(0, n), out _));

        // What a game of 0.26.0 says back: the same, without the role on the end.
        Wire.Op back = (Wire.Op)Wire.Read(buffer.AsSpan(0, n - 1), out _)!;
        Assert.Equal(Wire.Role.Admin, back.Role);
        Assert.True(back.SaysBack(told));
        Assert.False(new Wire.Op(true, "other").SaysBack(told));
    }

    [Fact]
    public void AListWrittenBeforeRolesIsEveryAdminAnAdmin()
    {
        string folder = Folder();
        try
        {
            File.WriteAllText(Path.Combine(folder, "admins.json"),
                """[{"Name":"Ada","Hash":"","SinceUtc":"2026-01-01T00:00:00Z","Offered":false,"Discord":"111111111111111111"}]""");
            Assert.Equal(Wire.Role.Admin, new Admins(folder).CheckDiscord("111111111111111111", "Ada"));
            Admins admins = new(folder);
            admins.AddDiscord("222222222222222222", "Tess", Wire.Role.Trusted);
            Assert.Contains("\"Trusted\"", File.ReadAllText(Path.Combine(folder, "admins.json")));
            Assert.Equal(Wire.Role.Trusted, new Admins(folder).CheckDiscord("222222222222222222", "Tess"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ------------------------------------------------------------------ the bin

    [Fact]
    public void EverySaveLostGoesToTheBinAndComesBack()
    {
        string folder = Folder();
        try
        {
            SaveShelf shelf = new(folder);
            Blob world = new(BlobKind.World, "", 0, 1.0, Rig.FakeWorld(1));
            shelf.Keep("Monday", world, "B1", [], "SolLite", true, "Ada");
            shelf.Keep("Monday", world with { Universe = 2.0 }, "B1", [], "SolLite", true, "Ada");
            Assert.True(shelf.Delete("Monday"));
            Assert.Null(shelf.Find("Monday"));

            // Saved over once and deleted once: two worlds in the bin, neither lost.
            Assert.Equal(2, shelf.Binned().Count);
            string first = shelf.Binned()[0].Name;
            Assert.StartsWith("Monday (deleted ", first);
            Assert.Equal("Monday", shelf.Restore(first));
            Assert.NotNull(shelf.Find("Monday"));
            // Its name is taken now, so the other comes back beside it.
            Assert.Equal("Monday (restored)", shelf.Restore(shelf.Binned()[0].Name));
            Assert.Empty(shelf.Binned());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AWorldKeptBeforeALoadThatIsLetGoGoesToTheBinToo()
    {
        string folder = Folder();
        try
        {
            SaveShelf shelf = new(folder);
            Blob world = new(BlobKind.World, "", 0, 1.0, Rig.FakeWorld(1));
            for (int i = 0; i < SaveShelf.MostAuto + 2; i++) shelf.KeepBeforeLoad(world, "B1", [], "SolLite", true, "Ada");
            Assert.Equal(SaveShelf.MostAuto, shelf.List().Count(s => s.Auto));
            Assert.Equal(2, shelf.Binned().Count);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TheBinKeepsThirtyDaysAndNoMore()
    {
        string folder = Folder();
        try
        {
            SaveShelf shelf = new(folder);
            shelf.Keep("Old", new Blob(BlobKind.World, "", 0, 1.0, Rig.FakeWorld(1)), "B1", [], "SolLite", true, "Ada");
            shelf.Delete("Old");
            string zip = Directory.GetFiles(Path.Combine(folder, ".deleted"), "*.zip").Single();
            File.SetLastWriteTimeUtc(zip, DateTime.UtcNow.AddDays(-SaveShelf.BinDays - 1));
            Assert.Empty(shelf.Binned());
            Assert.Empty(Directory.GetFiles(Path.Combine(folder, ".deleted")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TheAuditLogIsAFileThatOnlyGrows()
    {
        string folder = Folder();
        try
        {
            new AuditLog(folder).Write("console", "delete Monday", "deleted 'Monday'");
            new AuditLog(folder).Write("Tess (trusted, Discord 3)", "clear\nRocket_*", "clearing");
            string[] lines = File.ReadAllLines(Path.Combine(folder, "audit.log"));
            Assert.Equal(2, lines.Length);
            Assert.Contains("console  delete Monday  ->  deleted 'Monday'", lines[0]);
            Assert.Contains("clear Rocket_*", lines[1]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string Folder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ksamp-roles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
