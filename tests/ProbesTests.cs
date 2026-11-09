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

    [Fact]
    public void AServerSpeakingAnotherVersionIsRememberedAsThatRatherThanAsSilent()
    {
        // A refusal for the wrong version is written in the version that sent it, so it cannot be
        // read as a refusal. A row that says which version a server speaks is a row a player can
        // act on; one that says "no answer" sends them to check a firewall that is fine.
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
