using System.Security.Cryptography;
using KSAMultiplayer.Server;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// Operators: admins by the discord-operators setting alone, who may also have the server's log, and
/// the log itself, kept a file a day and sent with every address taken out.
/// </summary>
public class OperatorTests
{
    private const string Onyx = "708829744329785467", Ada = "111111111111111111";

    private sealed class StoodIn(ECDsa key) : ILoginService
    {
        public Task<LoginKey> Key(CancellationToken cancel) => Task.FromResult(new LoginKey(key, []));
        public Task<System.Net.IPAddress[]> Resolve(string typed, CancellationToken cancel) => Addresses.Of(typed, cancel);
    }

    private static Client LoggedIn(Rig rig, ECDsa service, string name, string id)
    {
        Client c = rig.Join(name);
        rig.Until(() => c.Joined && c.DiscordNonce.Length > 0, what: $"{name} to join a server taking Discord logins");
        using ECDsa machine = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string spki = System.Buffers.Text.Base64Url.EncodeToString(machine.ExportSubjectPublicKeyInfo());
        string cert = new DiscordCert(id, name, spki, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60).Sign(service);
        c.ProveDiscord(cert, "127.0.0.1", System.Buffers.Text.Base64Url.EncodeToString(machine.SignData(Discord.Proof(c.DiscordNonce, "127.0.0.1"), HashAlgorithmName.SHA256)));
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
    public void AnOperatorListedAsAnAdminTooIsAnOperatorAndAloneMayHaveTheLog()
    {
        using ECDsa service = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using Rig rig = new(new ServerOptions { DiscordOperators = [Onyx], DiscordAdmins = [Onyx, Ada] }, discord: new StoodIn(service));
        Assert.Equal(Wire.Role.Operator, rig.Server.RoleBySetting(Onyx));
        Assert.Equal([(Onyx, "discord-operators")], rig.Server.ListedTwice());

        Client o = LoggedIn(rig, service, "Onyx", Onyx);
        rig.Until(() => o.Role == Wire.Role.Operator, what: "Onyx to be an operator by the setting");
        Assert.True(o.Admin);
        Assert.True(o.Operator);
        Assert.Equal("run", Asks(rig, o, "logs"));
        Assert.Equal("run", Asks(rig, o, "delete Monday"));

        Client a = LoggedIn(rig, service, "Ada", Ada);
        rig.Until(() => a.Role == Wire.Role.Admin, what: "Ada to be an admin by the setting");
        Assert.False(a.Operator);
        Assert.Contains("only an operator", Asks(rig, a, "logs 3"));
    }

    [Fact]
    public void NoCommandLowersAnOperatorAndTheirTokenIsAnAdminsOnly()
    {
        using ECDsa service = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using Rig rig = new(new ServerOptions { DiscordOperators = [Onyx] }, discord: new StoodIn(service));
        Client o = LoggedIn(rig, service, "Onyx", Onyx);
        rig.Until(() => o.Role == Wire.Role.Operator, what: "Onyx to be an operator");

        // A machine's token is no Discord login: what it gives back at a join is admin.
        Assert.Equal(Wire.Role.Admin, rig.Server.Admins.RoleOf("Onyx"));
        Assert.True(rig.Server.IsOperator("Onyx"));
        rig.Server.Trust("Onyx");
        rig.Server.OpDiscord(Onyx);
        rig.Server.Deop("Onyx");
        rig.For(0.5);
        Assert.Equal(Wire.Role.Operator, o.Role);
        Assert.Equal(Wire.Role.Operator, rig.Server.Players.Single(p => p.Name == "Onyx").Role);
    }

    [Fact]
    public void TheLogReachesAnOperatorWithNoAddressesInIt()
    {
        using ECDsa service = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using Rig rig = new(new ServerOptions { DiscordOperators = [Onyx] }, discord: new StoodIn(service));
        Client o = LoggedIn(rig, service, "Onyx", Onyx);
        rig.Until(() => o.Role == Wire.Role.Operator, what: "Onyx to be an operator");

        string log = string.Concat(Enumerable.Range(0, 2000).Select(i => $"2026-11-21 10:{i % 60:00}:00 Pat {i} has joined from 203.0.113.{i % 250}:50{i % 100:00}\n"));
        rig.Server.SendLog(rig.Server.Players.Single(p => p.Name == "Onyx"), log + "[2001:db8::1]:50100 on KSA 2026.11.16.5901\n", "2026-11-21");
        rig.Until(() => o.Logs.Count > 0, what: "the log to reach Onyx's game");
        Blob blob = o.Logs.Dequeue();
        Assert.Equal("2026-11-21", blob.Key);
        string text = LogText.Unpack(blob.Data)!;
        Assert.DoesNotContain("203.0.113.", text);
        Assert.DoesNotContain("2001:db8", text);
        Assert.Contains("Pat 1999 has joined from (address)", text);
        Assert.Contains("(address) on KSA 2026.11.16.5901", text);
    }

    [Fact]
    public void AGameThatIsNoOperatorKeepsNoLog()
    {
        using Rig rig = new();
        Client p = rig.Join("Pat");
        rig.Until(() => p.Joined, what: "Pat to join");
        rig.Server.SendLog(rig.Server.Players.Single(q => q.Name == "Pat"), "anything", "2026-11-21");
        rig.For(1.0);
        Assert.Empty(p.Logs);
        Assert.Empty(p.Blobs);
    }

    // ------------------------------------------------------------------ the wire

    [Fact]
    public void TheOperatorCrossesTheWire()
    {
        byte[] buffer = new byte[Wire.BufferBytes];
        Wire.Op told = new(true, "tok", Wire.Role.Operator);
        int n = Wire.Write(told, buffer);
        Assert.Equal(told, Wire.Read(buffer.AsSpan(0, n), out _));
        // Nothing above an operator is read as more than one.
        buffer[n - 1] = 200;
        Assert.Equal(Wire.Role.Operator, ((Wire.Op)Wire.Read(buffer.AsSpan(0, n), out _)!).Role);
    }

    // ------------------------------------------------------------------ the text

    [Theory]
    [InlineData("Pat has joined from 198.51.100.7:50100", "Pat has joined from (address)")]
    [InlineData("refused Bob from 10.0.0.2", "refused Bob from (address)")]
    [InlineData("Pat is joining again from [::ffff:198.51.100.7]:4000", "Pat is joining again from (address)")]
    [InlineData("for players on KSA 2026.11.16.5901", "for players on KSA 2026.11.16.5901")]
    [InlineData("ksamp-server 0.27.6, speaking protocol 31", "ksamp-server 0.27.6, speaking protocol 31")]
    [InlineData("universe time 1234.5 s at 2026-11-21 10:00:00", "universe time 1234.5 s at 2026-11-21 10:00:00")]
    public void AddressesAreTakenOutAndNothingElse(string line, string sent) =>
        Assert.Equal(sent, LogText.Unaddressed(line));

    [Fact]
    public void WhatIsNotGzipOrUnpacksTooFarIsRefused()
    {
        Assert.Equal("a line\n", LogText.Unpack(LogText.Pack("a line\n")));
        Assert.Null(LogText.Unpack([1, 2, 3, 4]));
        // Zeros pack to almost nothing, which is what a server meaning harm would send.
        Assert.Null(LogText.Unpack(LogText.Pack(new string('\0', LogText.MostBytes + 1))));
    }

    [Fact]
    public void TheLogIsAFileADayAndTheNewestFortnightIsKept()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ksamp-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            ServerLog log = new(folder);
            DateTime first = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Local);
            for (int d = 0; d < 20; d++)
            {
                log.Write(first.AddDays(d), $"line one of day {d}");
                log.Write(first.AddDays(d).AddHours(1), $"line two of day {d}");
            }
            string[] days = log.Days();
            Assert.Equal(LogText.MostDays, days.Length);
            Assert.Equal("2026-11-07", days[0]);
            Assert.Equal("2026-11-20", days[^1]);

            string read = log.Read(2);
            Assert.StartsWith("==== 2026-11-19 ====", read);
            Assert.Contains("line two of day 18", read);
            Assert.Contains("==== 2026-11-20 ====", read);
            Assert.DoesNotContain("day 17", read);
            Assert.Equal(log.Read(LogText.MostDays), log.Read(100));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
