using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using KSAMultiplayer.Server;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>Asking many servers at once, over loopback, on the one socket a list of servers gets.</summary>
public sealed class ProbesTests
{
    private static readonly Func<double> Clock = () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Ticks whatever is given and drains the probes until a condition holds.</summary>
    private static void Until(Probes probes, Action tick, Func<bool> done, double seconds = 5.0, string what = "the condition")
    {
        double until = Clock() + seconds;
        while (!done())
        {
            if (Clock() > until) throw new Xunit.Sdk.XunitException($"timed out waiting for {what}");
            tick();
            probes.Update();
            Thread.Sleep(1);
        }
    }

    [Fact]
    public void TwoServersAnswerOneSocketAndAreToldApartByWhereTheyAnsweredFrom()
    {
        using Rig one = new(new ServerOptions { Name = "One", MaxPlayers = 4 });
        using Rig two = new(new ServerOptions { Name = "Two", MaxPlayers = 9, Password = "hunter2" });
        IPEndPoint at1 = new(IPAddress.Loopback, one.Server.Port);
        IPEndPoint at2 = new(IPAddress.Loopback, two.Server.Port);

        using Probes probes = new(Clock);
        probes.Ask(at1);
        probes.Ask(at2);
        Until(probes, () => { one.Server.Tick(); two.Server.Tick(); },
              () => probes.Answered.Count == 2, what: "both servers to answer");

        // Each answer is filed under the address it came from, which is the only thing about a
        // datagram its sender does not choose -- and so the one thing two listings of one server
        // have in common when the addresses they were listed under do not match.
        Assert.Equal("One", probes.Answered[at1].ServerName);
        Assert.Equal(4, probes.Answered[at1].MaxPlayers);
        Assert.False(probes.Answered[at1].Password);
        Assert.Equal("Two", probes.Answered[at2].ServerName);
        Assert.Equal(9, probes.Answered[at2].MaxPlayers);
        Assert.True(probes.Answered[at2].Password);

        // Asking is not joining: neither server holds a place for this socket.
        one.For(0.3);
        two.For(0.3);
        Assert.Empty(one.Server.Players);
        Assert.Empty(two.Server.Players);
    }

    [Fact]
    public void AskingTwiceAboutOneServerSendsOneQuestion()
    {
        using Rig rig = new();
        IPEndPoint at = new(IPAddress.Loopback, rig.Server.Port);
        using Probes probes = new(Clock);

        probes.Ask(at);
        probes.Ask(at);
        probes.Ask(at);
        Assert.Equal(1, probes.Waiting);
        Until(probes, rig.Server.Tick, () => probes.Answered.Count == 1, what: "the one answer");
        Assert.Equal(0, probes.Waiting);

        // Already answered: asked again and nothing goes out, which is what stops a list of thirty
        // servers being asked once per directory that carries it.
        probes.Ask(at);
        Assert.Equal(0, probes.Waiting);
    }

    [Fact]
    public void AnAddressNothingAnswersFromGoesSilentRatherThanWaitingForEver()
    {
        // A port bound and dropped, so nothing is listening on it and nothing else takes it either.
        int free;
        using (Socket borrow = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            borrow.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            free = ((IPEndPoint)borrow.LocalEndPoint!).Port;
        }
        IPEndPoint nowhere = new(IPAddress.Loopback, free);

        using Probes probes = new(Clock);
        probes.Ask(nowhere);
        Until(probes, () => { }, () => probes.Silent.Count == 1,
              seconds: Probes.WaitSeconds + 3.0, what: "the address to be given up on");

        Assert.Contains(nowhere, probes.Silent);
        Assert.Empty(probes.Answered);
        Assert.Equal(0, probes.Waiting);
    }

    /// <summary>One datagram sent to a server from a socket of the test's own, and what came back within a moment, or nothing.</summary>
    private static byte[]? Answer(Rig rig, ReadOnlySpan<byte> question)
    {
        using Socket asker = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        asker.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        asker.SendTo(question, new IPEndPoint(IPAddress.Loopback, rig.Server.Port));
        byte[] heard = new byte[Wire.BufferBytes];
        double until = Clock() + 1.0;
        while (Clock() < until)
        {
            rig.Server.Tick();
            if (asker.Available > 0) return heard[..asker.Receive(heard)];
            Thread.Sleep(1);
        }
        return null;
    }

    private static byte[] Ask(ushort version)
    {
        byte[] ask = new byte[Wire.BufferBytes];
        int n = Wire.Write(new Wire.Ask(), ask);
        BinaryPrimitives.WriteUInt16LittleEndian(ask.AsSpan(4), version);
        return ask[..n];
    }

    [Fact]
    public void AServerSaysWhatItIsToAnAskerOfAnotherVersion()
    {
        // The directory somebody else runs, built before this server's release or after it.
        using Rig rig = new(new ServerOptions { Name = "Moon base", MaxPlayers = 7 });
        foreach (ushort theirs in new[] { (ushort)(Wire.Version - 3), (ushort)(Wire.Version + 3) })
        {
            byte[] answer = Assert.IsType<byte[]>(Answer(rig, Ask(theirs)));
            Wire.Facts told = Assert.IsType<Wire.Facts>(Wire.Read(answer, out ushort version));
            // In its own version, which is what the asker lists it under.
            Assert.Equal((Wire.Version, Wire.Version), (version, told.Speaks));
            Assert.Equal(("Moon base", 7), (told.ServerName, told.MaxPlayers));
        }
        Assert.Empty(rig.Server.Players);
    }

    [Fact]
    public void AnAskIsNeverAnsweredWithMoreThanItCostToSend()
    {
        // Anybody can put any address on a datagram, so whatever an ask is answered with is sent
        // to somebody who may never have asked.
        using Rig rig = new(new ServerOptions { Name = "Moon base", Mods = string.Join(",", Enumerable.Repeat("ModWithALongName", 20)) });
        byte[] ask = Ask(Wire.Version);
        Assert.InRange(Assert.IsType<byte[]>(Answer(rig, ask)).Length, 1, ask.Length);

        // Cut down to its header and its one byte, it is still an ask, and is not answered at all.
        Assert.Null(Answer(rig, ask.AsSpan(0, 8)));
        Assert.Null(Answer(rig, ask.AsSpan(0, Wire.AskBytes - 1)));
    }

    [Fact]
    public void AModListTooLongToAnswerWithIsLeftOutRatherThanTheServerGoingUnlisted()
    {
        // Three hundred characters are nine hundred bytes in most of the world's writing, and with
        // a name and a build written the same way the answer is longer than the ask.
        string name = new('基', 64);
        using Rig rig = new(new ServerOptions { Name = name, KsaBuild = new string('版', 64), Mods = new string('模', 400) });
        byte[] ask = Ask(Wire.Version);
        byte[] answer = Assert.IsType<byte[]>(Answer(rig, ask));
        Assert.InRange(answer.Length, 1, ask.Length);
        Wire.Facts told = Assert.IsType<Wire.Facts>(Wire.Read(answer, out _));
        Assert.Equal((name, ""), (told.ServerName, told.Mods));
    }

    [Fact]
    public void AServerOfAnotherVersionIsAnsweredForWithWhatItIs()
    {
        using Socket elsewhere = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        elsewhere.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint at = (IPEndPoint)elsewhere.LocalEndPoint!;

        byte[] answer = new byte[Wire.BufferBytes];
        int n = Wire.Write(new Wire.Facts("Next year's", "Sol", true, "Core", 2, 8, Wire.WelcomeFlags.None, ""), answer);
        BinaryPrimitives.WriteUInt16LittleEndian(answer.AsSpan(4), 99);

        using CancellationTokenSource stop = new();
        Thread answering = new(() =>
        {
            byte[] heard = new byte[2048];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    elsewhere.ReceiveFrom(heard, ref any);
                    elsewhere.SendTo(answer.AsSpan(0, n), SocketFlags.None, any);
                }
                catch (Exception)
                {
                    return;
                }
            }
        }) { IsBackground = true };
        answering.Start();

        using Probes probes = new(Clock);
        probes.Ask(at);
        Until(probes, () => { }, () => probes.Answered.Count == 1, what: "what a server of another version is");

        Assert.Equal(("Next year's", 2, (ushort)99), (probes.Answered[at].ServerName, probes.Answered[at].Players, probes.Answered[at].Protocol));
        Assert.Empty(probes.Elsewhere);

        stop.Cancel();
        elsewhere.Close();
    }

    [Fact]
    public void AServerFromBeforeItCouldBeAskedIsRememberedAsItsVersionRatherThanAsSilent()
    {
        // Such a server refuses an ask as it refuses anything of another version, and the refusal
        // is written in the version that sent it, so it cannot be read as one. A row that says
        // which version a server speaks is a row a player can act on; one that says "no answer"
        // sends them to check a firewall that is fine.
        using Socket elsewhere = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        elsewhere.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint at = (IPEndPoint)elsewhere.LocalEndPoint!;

        byte[] answer = new byte[7];
        "KSMP"u8.CopyTo(answer);
        BinaryPrimitives.WriteUInt16LittleEndian(answer.AsSpan(4), 99);
        answer[6] = (byte)Wire.Kind.Welcome;

        using CancellationTokenSource stop = new();
        Thread answering = new(() =>
        {
            byte[] heard = new byte[2048];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    elsewhere.ReceiveFrom(heard, ref any);
                    elsewhere.SendTo(answer, any);
                }
                catch (Exception)
                {
                    return;
                }
            }
        }) { IsBackground = true };
        answering.Start();

        using Probes probes = new(Clock);
        probes.Ask(at);
        Until(probes, () => { }, () => probes.Elsewhere.Count == 1, what: "an answer in another version");

        Assert.Equal((ushort)99, probes.Elsewhere[at]);
        Assert.Empty(probes.Answered);
        Assert.DoesNotContain(at, probes.Silent);

        stop.Cancel();
        elsewhere.Close();
    }

    [Fact]
    public void ForgettingClearsEverythingSoAListCanBeBuiltAgain()
    {
        using Rig rig = new();
        IPEndPoint at = new(IPAddress.Loopback, rig.Server.Port);
        using Probes probes = new(Clock);

        probes.Ask(at);
        Until(probes, rig.Server.Tick, () => probes.Answered.Count == 1, what: "the answer");

        probes.Forget();
        Assert.Empty(probes.Answered);
        Assert.Equal(0, probes.Waiting);

        probes.Ask(at);
        Until(probes, rig.Server.Tick, () => probes.Answered.Count == 1, what: "the answer again");
        Assert.Equal("KSA server", probes.Answered[at].ServerName);
    }
}
