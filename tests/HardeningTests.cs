using System.Net;
using System.Net.Sockets;
using KSAMultiplayer.Server;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// What a server on the internet is sent by somebody who means it harm, and by a game with more
/// craft than the tests before thought of. Each of these was open before the change it pins.
/// </summary>
public class HardeningTests
{
    private static Client Seed(Rig rig, string name = "Alice", double universe = 5000)
    {
        Client a = rig.Join(name);
        rig.Until(() => a.Joined && a.SnapshotWanted is not null, what: "the server to ask for a world");
        a.SnapshotWanted = null;
        a.Upload(new Blob(BlobKind.World, "", a.Id, universe, Rig.FakeWorld()));
        rig.Until(() => rig.Server.HasWorld && rig.Server.Players.First(p => p.Id == a.Id).Loaded, what: "the world to arrive");
        return a;
    }

    private static Client JoinAndLoad(Rig rig, string name)
    {
        Client b = rig.Join(name);
        rig.Until(() => b.Blobs.Any(x => x.Kind == BlobKind.World), seconds: 20, what: $"{name} to get the world");
        b.SayLoaded(b.Blobs.First(x => x.Kind == BlobKind.World).Universe);
        rig.Until(() => rig.Server.Players.First(p => p.Id == b.Id).Loaded, what: $"{name} to load");
        return b;
    }

    internal static byte[] Datagram(Wire.Message m)
    {
        byte[] buffer = new byte[Wire.BufferBytes];
        return buffer[..Wire.Write(m, buffer)];
    }

    internal static List<byte[]> Heard(Rig rig, UdpClient socket, double seconds)
    {
        List<byte[]> heard = [];
        double until = rig.Now() + seconds;
        while (rig.Now() < until)
        {
            rig.Server.Tick();
            while (socket.Available > 0)
            {
                IPEndPoint? from = null;
                heard.Add(socket.Receive(ref from));
            }
            Thread.Sleep(1);
        }
        return heard;
    }

    // ------------------------------------------------------------------ whose a datagram is

    [Fact]
    public void ACommandWithoutTheSessionsWordIsNotTheAdmins()
    {
        // A player is known by the address a datagram says it is from, and anybody can write one:
        // sent here from the admin's own socket, as a datagram with their address on it seems to be.
        using Rig rig = new();
        Client a = Seed(rig);
        Assert.Equal("Alice", rig.Server.Op("Alice"));
        rig.Until(() => a.Role == Wire.Role.Admin, what: "Alice to be told she is an admin");

        a.Send(new Wire.Command("kick forged"));
        a.Send(new Wire.Command("kick forged", "not the word"));
        rig.For(0.3);
        Assert.Empty(rig.Server.Asked);

        a.Ask("kick hers");
        rig.Until(() => rig.Server.Asked.Count == 1, what: "her own command");
    }

    [Fact]
    public void AGoodbyeWithoutTheSessionsWordIsNotTheirs()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        a.Send(new Wire.Bye("left"));
        rig.For(0.3);
        Assert.Single(rig.Server.Players);
    }

    [Fact]
    public void AGameFromBeforeTheSessionsWordStillCommandsAndLeaves()
    {
        // It never said it says one back, so it is given none and held to none.
        using Rig rig = new();
        using UdpClient old = new(new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint server = new(IPAddress.Loopback, rig.Server.Port);
        byte[] hello = Datagram(new Wire.Hello("Old", "B1", "Core", "SolLite", "", ""));
        old.Send(hello, hello.Length, server);
        Wire.Welcome cookie = Heard(rig, old, 0.3).Select(d => Wire.Read(d, out _)).OfType<Wire.Welcome>().First();
        hello = Datagram(new Wire.Hello("Old", "B1", "Core", "SolLite", "", "", cookie.Cookie));
        old.Send(hello, hello.Length, server);
        Wire.Welcome welcome = Heard(rig, old, 0.3).Select(d => Wire.Read(d, out _)).OfType<Wire.Welcome>().First(w => w.Id != 0);
        Assert.Equal("", welcome.Session);

        byte[] command = Datagram(new Wire.Command("help"));
        old.Send(command, command.Length, server);
        Heard(rig, old, 0.3);
        Assert.Single(rig.Server.Asked);

        byte[] bye = Datagram(new Wire.Bye("left"));
        old.Send(bye, bye.Length, server);
        Heard(rig, old, 0.3);
        Assert.Empty(rig.Server.Players);
    }

    [Fact]
    public void AGameOfAnotherVersionIsAnsweredInNoMoreThanItSent()
    {
        using Rig rig = new();
        using UdpClient old = new(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] alive = Datagram(new Wire.Alive());
        alive[4] = 1;
        old.Send(alive, alive.Length, new IPEndPoint(IPAddress.Loopback, rig.Server.Port));

        byte[] answer = Assert.Single(Heard(rig, old, 0.3));

        Assert.True(answer.Length <= alive.Length, $"{alive.Length} bytes drew {answer.Length}");
        Wire.Read(answer, out ushort version);
        Assert.Equal(Wire.Version, version);
    }

    [Fact]
    public void AnAddressThatGuessesThePasswordWrongIsRefusedForAWhile()
    {
        using Rig rig = new(new ServerOptions { Password = "pw" });
        for (int i = 0; i < 5; i++)
        {
            Client wrong = rig.Join("Mallory", password: $"guess {i}");
            rig.Until(() => wrong.Out.Length > 0, what: "a refusal");
            Assert.Contains("wrong password", wrong.Out);
        }
        Client right = rig.Join("Mallory", password: "pw");
        rig.Until(() => right.Out.Length > 0, what: "a refusal");
        Assert.Contains("too many wrong passwords", right.Out);

        // And let in once the minute is out.
        rig.Skip(61);
        Client later = rig.Join("Mallory", password: "pw");
        rig.Until(() => later.Joined, what: "the right password to be taken again");
    }

    // ------------------------------------------------------------------ limits

    [Fact]
    public void APlayerHoldsNoMoreCraftThanTheServerAllows()
    {
        using Rig rig = new(new ServerOptions { MaxCraftPerPlayer = 3 });
        Client a = Seed(rig);
        for (int i = 0; i < 10; i++) a.Claim($"Missile {i}", controlling: false, made: true);
        rig.Until(() => rig.Server.Said.Any(s => s.Contains("over a limit")), what: "the limit to be said");
        Assert.Equal(3, rig.Server.CraftCount);

        // One that is gone makes room for the next.
        rig.Until(() => a.Owns("Missile 0"), what: "the owners to reach Alice");
        a.SayGone("Missile 0");
        rig.For(1.2);
        a.Claim("Missile 10", controlling: false, made: true);
        rig.Until(() => rig.Server.OwnerOf("Missile 10") == a.Id, what: "the next craft");
    }

    [Fact]
    public void ASalvoOfAHundredCraftEachSendingAsAGameDoesIsPassedOnWhole()
    {
        // The limits are for one craft and for how many, not for a player's whole traffic.
        using Rig rig = new();
        Client a = Seed(rig), b = JoinAndLoad(rig, "Bob");
        for (int i = 0; i < 100; i++) a.Claim($"Missile {i}", controlling: false, made: true);
        rig.Until(() => Enumerable.Range(0, 100).All(i => rig.Server.OwnerOf($"Missile {i}") == a.Id), what: "every missile to be Alice's");
        for (int step = 0; step < 5; step++)
        {
            for (int i = 0; i < 100; i++) a.SendState(Rig.State($"Missile {i}", 5000.0 + step) with { Flags = Wire.StateFlags.None });
            rig.For(0.05);
        }
        rig.Until(() => Enumerable.Range(0, 100).All(i => b.TrackOf($"Missile {i}").HeardAt >= 5004.0), what: "every missile's last state");
        Assert.DoesNotContain(rig.Server.Said, s => s.Contains("over a limit"));
    }

    [Fact]
    public void OneCraftSendingFarFasterThanAnyGameIsNotPassedOnInFull()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        JoinAndLoad(rig, "Bob");
        a.Claim("Rocket", controlling: true, made: true);
        rig.Until(() => rig.Server.OwnerOf("Rocket") == a.Id, what: "the rocket to be Alice's");
        for (int i = 0; i < 500; i++) a.SendState(Rig.State("Rocket", 5000.0 + (i * 0.001)));
        rig.Until(() => rig.Server.Said.Any(s => s.Contains("over a limit") && s.Contains("states")), what: "the limit to be said");
    }

    [Fact]
    public void AFloodOfChatIsNotPassedOnInFull()
    {
        using Rig rig = new();
        Client a = Seed(rig), b = JoinAndLoad(rig, "Bob");
        b.Chat.Clear();
        for (int i = 0; i < 100; i++) a.SayChat($"line {i}");
        rig.For(0.5);
        Assert.InRange(b.Chat.Count(c => c.Who == "Alice"), 1, 15);
    }

    [Fact]
    public void ADeleteOfTwoHundredCraftAtOnceIsTwoHundredCommandsHeard()
    {
        // A trusted player's game asks for each craft's removal in a command of its own, and the
        // wreckage of a few rockets is this many.
        using Rig rig = new();
        Client a = Seed(rig);
        Assert.Equal("Alice", rig.Server.Trust("Alice"));
        rig.Until(() => a.Role == Wire.Role.Trusted, what: "Alice to be told she is trusted");
        for (int i = 0; i < 200; i++)
        {
            a.Ask($"remove Craft {i}");
            if (i % 20 == 19) rig.For(0.02);
        }
        rig.Until(() => rig.Server.Asked.Count == 200, what: "every command");
        Assert.DoesNotContain(rig.Server.Said, s => s.Contains("over a limit"));
    }

    [Fact]
    public void AFloodOfCommandsIsNotHeardInFull()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        for (int i = 0; i < 2000; i++)
        {
            a.Ask("saves");
            if (i % 100 == 99) rig.For(0.01);
        }
        rig.Until(() => rig.Server.Said.Any(s => s.Contains("over a limit") && s.Contains("commands")), what: "the limit to be said");
        Assert.InRange(rig.Server.Asked.Count + rig.Server.Said.Count(s => s.Contains("asked for 'saves'")), 200, 400);
    }

    // ------------------------------------------------------------------ the world and transfers

    [Theory]
    [InlineData("junk", "not a zip")]
    [InlineData("climbs", "not a name a save has")]
    [InlineData("empty", "holds no files")]
    public void AWorldAskedForIsLookedAtBeforeItIsHandedOut(string what, string why)
    {
        using Rig rig = new();
        Client a = Seed(rig);
        byte[] before = rig.Server.World!.Data;
        rig.Server.SnapshotNow();
        rig.Until(() => a.SnapshotWanted is not null, what: "the server to ask");

        byte[] world = what switch
        {
            "junk" => [1, 2, 3, 4],
            "climbs" => Blob.Zip([("universe.xml", [1]), ("../../evil.dll", [1])]),
            _ => Blob.Zip([]),
        };
        a.Upload(new Blob(BlobKind.World, "", a.Id, 5001, world));
        rig.Until(() => rig.Server.Said.Any(s => s.Contains("will not hand over a world") && s.Contains(why)), what: "the world to be refused");

        Assert.Same(before, rig.Server.World!.Data);
    }

    [Fact]
    public void APlayerNotAskedForAWorldIsKeptNoRoomForOne()
    {
        Link link = new() { MostPieces = Link.VehiclePieces };
        link.Handle(new Wire.Chunk(1, 0, ushort.MaxValue, new byte[Link.ChunkBytes]), 0, _ => { });
        Assert.Equal(0, link.Busy);
        link.Handle(new Wire.Chunk(2, 0, Link.VehiclePieces, new byte[Link.ChunkBytes]), 0, _ => { });
        Assert.Equal(1, link.Busy);
    }

    [Fact]
    public void ALinkSaysWhatBecameOfWhatItSent()
    {
        // What an event batch waits on before the next goes. Busy cannot say it: that is every
        // transfer on the link, and a world on its way would hold events for as long as it took.
        Link sender = new(), receiver = new();
        Assert.True(sender.Came(0));

        uint came = sender.Send(new byte[Link.ChunkBytes * 2], 0);
        Assert.Null(sender.Came(came));
        Assert.Equal(0u, receiver.Last);
        List<Wire.Message> said = [];
        sender.Pump(0, said.Add);
        byte[]? whole = null;
        foreach (Wire.Message m in said.ToArray())
        {
            Assert.Null(whole);
            whole = receiver.Handle(m, 0, said.Add);
        }
        Assert.NotNull(whole);
        Assert.Equal(came, receiver.Last);
        Assert.Null(sender.Came(came));
        sender.Handle(said.OfType<Wire.Got>().Single(), 0, _ => { });
        Assert.True(sender.Came(came));

        // Two minutes with nothing heard is given up on, and stays so: nothing tells acked from
        // given up but this, since both are only the transfer being gone.
        uint lost = sender.Send(new byte[10], 0), later = sender.Send(new byte[10], 200);
        sender.Pump(0, _ => { });
        sender.Pump(121, _ => { });
        Assert.False(sender.Came(lost));
        Assert.Null(sender.Came(later));
        Assert.True(sender.Came(came));
        sender.Pump(1000, _ => { });
        Assert.False(sender.Came(lost));
        Assert.False(sender.Came(later));
    }

    [Fact]
    public void ATransferStillBeingAskedForIsNotGivenUpOn()
    {
        // Given up on for having started two minutes ago, a world of 50 MB could never arrive.
        Link sender = new();
        uint id = sender.Send(new byte[Link.ChunkBytes * 2], 0);
        sender.Pump(0, _ => { });
        sender.Handle(new Wire.Need(id, [1]), 100, _ => { });
        sender.Pump(150, _ => { });
        Assert.Equal(1, sender.Busy);

        sender.Pump(400, _ => { });
        Assert.Equal(0, sender.Busy);
    }

    [Fact]
    public void ACraftThatUnpacksToMoreThanACraftIsRefused()
    {
        byte[] bomb = Blob.Gzip(new byte[Blob.VehicleMostBytes + 1]);
        Assert.True(bomb.Length < 200_000);
        Assert.Throws<InvalidDataException>(() => Blob.Gunzip(bomb));
        Assert.Equal(1000, Blob.Gunzip(Blob.Gzip(new byte[1000])).Length);
    }

    [Fact]
    public void ARosterOfManyPlayersFlyingLongNamedCraftStillFitsADatagram()
    {
        using Rig rig = new(new ServerOptions { MaxPlayers = 40 });
        Client first = Seed(rig, "Player 00");
        List<Client> all = [first, .. Enumerable.Range(1, 29).Select(i => JoinAndLoad(rig, $"Player {i:00}"))];
        foreach (Client c in all) c.Claim(c.Id.ToString("00") + new string('月', 83), controlling: true, made: true);

        rig.Until(() => first.Players.Length == 30 && first.Players.All(p => p.Controlling.Length > 0), seconds: 20, what: "the whole roster");
    }

    // ------------------------------------------------------------------ names and the console

    [Fact]
    public void NobodyIsCalledServerOrByANameThatOnlyLooksDifferent()
    {
        using Rig rig = new();
        Client s = rig.Join("Server"), a = rig.Join("Alice"), a2 = rig.Join("Ali​ce"), a3 = rig.Join("‮ALICE");
        rig.Until(() => s.Joined && a.Joined && a2.Joined && a3.Joined, what: "everyone to join");
        string[] names = [.. rig.Server.Players.Select(p => p.Name)];
        Assert.DoesNotContain(names, n => n.Equals("server", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, n => Assert.All(n, c => Assert.True(c < 0x2000)));
    }

    [Fact]
    public void APlayerWithASpaceInTheirNameCanBeKickedByNameOrByNumber()
    {
        using Rig rig = new();
        Client k1 = rig.Join(""), k2 = rig.Join(""), k3 = rig.Join("");
        rig.Until(() => k1.Joined && k2.Joined && k3.Joined, what: "everyone to join");
        Assert.Contains(rig.Server.Players, p => p.Name == "Kitten 2");

        Assert.Equal("Kitten 2", rig.Server.KickLine("kitten 2 for griefing"));
        Assert.DoesNotContain(rig.Server.Players, p => p.Name == "Kitten 2");
        Assert.Contains(rig.Server.Players, p => p.Name == "Kitten");

        byte id = rig.Server.Players.First(p => p.Name == "Kitten 3").Id;
        Assert.Equal("Kitten 3", rig.Server.KickLine($"#{id}"));
        Assert.Null(rig.Server.KickLine("nobody"));
        Assert.Single(rig.Server.Players);
    }

    [Fact]
    public void WhatAGameSaysOfItselfIsOneLineOfTheLog()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        a.Claim("Rocket\nMallory has joined as admin", controlling: true, made: true);
        rig.For(0.3);
        Assert.All(rig.Server.Said, line => Assert.DoesNotContain('\n', line));
    }
}
