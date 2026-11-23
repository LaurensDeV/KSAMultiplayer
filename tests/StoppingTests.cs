using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using KSAMultiplayer.Server;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// A server stopping with players in it: counted down on their screens, the world saved, and a
/// goodbye that says why. And a game that was in a server which came back updated is told so.
/// </summary>
public class StoppingTests
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

    [Fact]
    public void AStopIsCountedDownOnEveryScreen()
    {
        using Rig rig = new();
        Client a = Seed(rig);
        Assert.True(rig.Server.AnyonePlaying);

        rig.Server.Close(15, "the server is restarting");
        rig.Until(() => double.IsFinite(a.ClosingAt), what: "the countdown to arrive");
        Assert.InRange(a.ClosingAt - rig.Now(), 13.0, 15.5);
        Assert.Equal("the server is restarting", a.ClosingWhy);
        rig.Until(() => a.Chat.Any(c => c.FromServer && c.Text.Contains("stops in 15 seconds")), what: "the countdown in the chat");
    }

    [Fact]
    public void NobodyJoinsAServerThatIsStopping()
    {
        using Rig rig = new();
        Seed(rig);
        rig.Server.Close(15, "the server is restarting");
        Client b = rig.Join("Bob");
        rig.Until(() => b.Out.Length > 0, what: "Bob to be refused");
        Assert.Contains("stopping", b.Out);
    }

    [Fact]
    public void TheLastSaveIsAskedForAndTaken()
    {
        using Rig rig = new();
        Client a = Seed(rig, universe: 5000);
        rig.Server.Close(0, "the server is restarting");
        double? until = rig.Server.AskLastSave();
        Assert.NotNull(until);
        Assert.False(rig.Server.SavedSince(until!.Value + 10.0));
        rig.Until(() => a.SnapshotWanted is not null, what: "the ask for a last save");
        a.Upload(new Blob(BlobKind.World, "", a.Id, until.Value, Rig.FakeWorld(41)));
        rig.Until(() => rig.Server.SavedSince(until.Value), what: "the last save");
    }

    [Fact]
    public void NobodyToSaveIsNoLastSave()
    {
        using Rig rig = new();
        Assert.False(rig.Server.AnyonePlaying);
        Assert.Null(rig.Server.AskLastSave());
    }

    [Fact]
    public void TheGoodbyeSaysWhyTheServerStopped()
    {
        Rig rig = new();
        Client a = Seed(rig);
        rig.Server.Close(0, "the server is restarting");
        rig.Server.Dispose();
        double until = rig.Now() + 5;
        while (a.Out.Length == 0 && rig.Now() < until)
        {
            a.Update();
            Thread.Sleep(1);
        }
        Assert.Equal("the server closed the connection: the server is restarting", a.Out);
        rig.Dispose();
    }

    /// <summary>
    /// The server a game was in is replaced by a newer one, which answers its pings in a protocol it
    /// does not speak. That is not acted on while joined -- anybody can write the server's address
    /// on a datagram -- but when the old server's silence ends the session, it is the reason given.
    /// </summary>
    [Fact]
    public void AGameWhoseServerCameBackUpdatedIsToldToUpdate()
    {
        double skipped = 0;
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        double Now() => watch.Elapsed.TotalSeconds + skipped;

        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using Client c = new(Now);
        c.Connect(new IPEndPoint(IPAddress.Loopback, port), "Alice", "B1", "Core", "SolLite", "", "");

        byte[] buffer = new byte[Wire.BufferBytes];
        IPEndPoint game = new(IPAddress.Any, 0);
        double until = Now() + 5;
        while (!c.Joined && Now() < until)
        {
            c.Update();
            if (server.Available > 0)
            {
                byte[] got = server.Receive(ref game);
                if (Wire.Read(got, out _) is Wire.Hello)
                {
                    server.Send(buffer, Wire.Write(new Wire.Welcome(1, "", "Old", true, Session: "s"), buffer), game);
                }
            }
            Thread.Sleep(1);
        }
        Assert.True(c.Joined);

        int n = Wire.Write(new Wire.Alive(), buffer);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)(Wire.Version + 1));
        server.Send(buffer, n, game);
        until = Now() + 2;
        while (Now() < until)
        {
            c.Update();
            Thread.Sleep(1);
        }
        // Heard, and not acted on.
        Assert.True(c.Joined);

        skipped += 11;
        c.Update();
        Assert.False(c.Joined);
        Assert.Contains("the server was updated", c.Out);
        Assert.Contains("install the newest KSA Multiplayer", c.Out);
    }
}
