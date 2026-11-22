using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using KSAMultiplayer.Server;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// The default port moved from 50100, which Windows and Linux hand out for their own use, to 28500.
/// A server set up before stays where it was, and an address with no port in it finds either.
/// </summary>
public class PortsTests
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(2);

    [Fact]
    public void TheDefaultIsUnderTheRangeEitherSystemHandsOut()
    {
        Assert.InRange(Wire.DefaultPort, 1024, 32767);
        Assert.Equal(50100, Wire.LegacyPort);
        Assert.Equal(Wire.DefaultPort, new ServerOptions().Port);
    }

    [Fact]
    public async Task AnAddressWithNoPortFindsAServerOnTheOldOne()
    {
        // A real server, on a port standing in for the old default; nothing on the new.
        using Rig rig = new();
        using Socket nobody = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        nobody.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int unused = ((IPEndPoint)nobody.LocalEndPoint!).Port;
        nobody.Dispose();

        Task<int> found = Ports.AnsweringAsync(IPAddress.Loopback, Within, first: unused, second: rig.Server.Port);
        while (!found.IsCompleted) { rig.Server.Tick(); Thread.Sleep(1); }

        Assert.Equal(rig.Server.Port, await found);
    }

    [Fact]
    public async Task AnAddressWithAServerOnBothIsTheOneOnTheNew()
    {
        using Rig one = new(), other = new();
        Task<int> found = Ports.AnsweringAsync(IPAddress.Loopback, Within, first: one.Server.Port, second: other.Server.Port);
        // The second answers first, and the first is still the one.
        Stopwatch watch = Stopwatch.StartNew();
        while (!found.IsCompleted)
        {
            other.Server.Tick();
            if (watch.ElapsedMilliseconds > 60) one.Server.Tick();
            Thread.Sleep(1);
        }
        Assert.Equal(one.Server.Port, await found);
    }

    [Fact]
    public async Task AnAddressNothingAnswersAtIsTakenForTheDefault()
    {
        using Socket nobody = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        nobody.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int unused = ((IPEndPoint)nobody.LocalEndPoint!).Port;
        nobody.Dispose();
        Assert.Equal(unused, await Ports.AnsweringAsync(IPAddress.Loopback, TimeSpan.FromSeconds(0.5), first: unused, second: unused + 1));
    }

    [Theory]
    [InlineData(true, "port = 50100")]
    [InlineData(false, "port = 28500")]
    public void AServerWithAWorldFromBeforeStaysOnThePortItHad(bool worldKept, string written)
    {
        // Its firewall and its players' saved addresses say 50100, and nothing about moving by
        // itself would fail where anybody would see. A server set up afresh has neither.
        string folder = Path.Combine(Path.GetTempPath(), "ksamp-port-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "data"));
        if (worldKept) File.WriteAllBytes(Path.Combine(folder, "data", "world.zip"), Rig.FakeWorld(1));
        string config = Path.Combine(folder, "ksamp-server.toml");
        try
        {
            using Process server = Process.Start(new ProcessStartInfo("dotnet",
                $"\"{typeof(GameServer).Assembly.Location}\" --config \"{config}\" --data \"{Path.Combine(folder, "data")}\" --bind 127.0.0.1")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = folder,
            })!;
            try
            {
                SpinWait.SpinUntil(() => File.Exists(config) && File.ReadAllText(config).Contains("port ="), TimeSpan.FromSeconds(30));
                Assert.Contains(written, File.ReadAllText(config));
            }
            finally
            {
                if (!server.HasExited) server.Kill();
                server.WaitForExit(5000);
            }
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
