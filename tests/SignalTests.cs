using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using KSAMultiplayer.Server;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// The server process itself, stopped as a host stops it: SIGTERM, which is what `docker stop` and
/// a redeploy send. The countdown, the last save and the goodbye all hang off the signal in the
/// console program, which nothing driving <see cref="GameServer"/> directly reaches.
/// </summary>
public class SignalTests
{
    private static int FreePort()
    {
        using Socket s = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    private sealed class Running : IDisposable
    {
        public Process Process { get; }
        public string Folder { get; }
        public int Port { get; }
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _out = new();
        public IEnumerable<string> Said => _out;

        public Running(string extra = "")
        {
            Folder = Path.Combine(Path.GetTempPath(), "ksamp-signal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Folder);
            Port = FreePort();
            Process = Process.Start(new ProcessStartInfo("dotnet",
                $"\"{typeof(GameServer).Assembly.Location}\" --config \"{Path.Combine(Folder, "ksamp-server.toml")}\" --data \"{Path.Combine(Folder, "data")}\" "
                + $"--bind 127.0.0.1 --port {Port} --ksa-build B1 {extra}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = Folder,
            })!;
            Process.OutputDataReceived += (_, e) => { if (e.Data is { } line) _out.Enqueue(line); };
            Process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) _out.Enqueue(line); };
            Process.BeginOutputReadLine();
            Process.BeginErrorReadLine();
            Assert.True(SpinWait.SpinUntil(() => Said.Any(l => l.Contains("listening on UDP")), TimeSpan.FromSeconds(30)), "the server never came up");
        }

        public void Signal(string name) => Process.Start("kill", $"-{name} {Process.Id}")!.WaitForExit();

        /// <summary>
        /// Waits for the server to exit and then for the last of what it wrote to be read: a wait
        /// with a timeout returns as the process goes, before the lines it wrote on the way out
        /// have reached the handlers, and a check of them then failed on a busier machine.
        /// </summary>
        public bool Exited(int ms)
        {
            if (!Process.WaitForExit(ms)) return false;
            Process.WaitForExit();
            return true;
        }

        public void Dispose()
        {
            if (!Process.HasExited) Process.Kill();
            Process.WaitForExit(5000);
            Process.Dispose();
            try { Directory.Delete(Folder, recursive: true); } catch (IOException) { }
        }
    }

    private static void Pump(Client c, Func<bool> until, double seconds, string what)
    {
        Stopwatch w = Stopwatch.StartNew();
        while (!until())
        {
            if (w.Elapsed.TotalSeconds > seconds) throw new Xunit.Sdk.XunitException($"timed out waiting for {what}; the game was told: '{c.Out}'");
            c.Update();
            // A game answers the server's asks for a save, as one in the world does.
            if (c.SnapshotWanted is not null)
            {
                c.SnapshotWanted = null;
                c.Upload(new Blob(BlobKind.World, "", c.Id, 5000 + w.Elapsed.TotalSeconds, Rig.FakeWorld(7)));
            }
            Thread.Sleep(2);
        }
    }

    [Fact]
    public void SigtermCountsThePlayersDownSavesAndSaysGoodbye()
    {
        if (!OperatingSystem.IsLinux()) return;
        using Running server = new("--stop-notice 3");
        Stopwatch clock = Stopwatch.StartNew();
        using Client a = new(() => clock.Elapsed.TotalSeconds);
        a.Connect(new IPEndPoint(IPAddress.Loopback, server.Port), "Alice", "B1", "Core", "SolLite", "", "");
        Pump(a, () => server.Said.Any(l => l.Contains("Alice is in the world")), 20, "Alice to be in the world");

        server.Signal("TERM");
        Pump(a, () => double.IsFinite(a.ClosingAt), 5, "the countdown");
        Assert.Equal("the server is restarting", a.ClosingWhy);
        Assert.False(server.Process.HasExited);

        Pump(a, () => a.Out.Length > 0, 15, "the goodbye");
        Assert.Equal("the server closed the connection: the server is restarting", a.Out);
        Assert.True(server.Exited(10000), "the server did not exit");
        Assert.Equal(0, server.Process.ExitCode);
        Assert.Contains(server.Said, l => l.Contains("stopping in 3 s: the server is restarting"));
        Assert.Contains(server.Said, l => l.Contains("Alice saved the world"));
        Assert.DoesNotContain(server.Said, l => l.Contains("without a last save"));
    }

    [Fact]
    public void SigtermWithNobodyPlayingStopsAtOnce()
    {
        if (!OperatingSystem.IsLinux()) return;
        using Running server = new();
        Stopwatch w = Stopwatch.StartNew();
        server.Signal("TERM");
        Assert.True(server.Exited(10000), "the server did not exit");
        Assert.Equal(0, server.Process.ExitCode);
        Assert.InRange(w.Elapsed.TotalSeconds, 0.0, 5.0);
        Assert.Contains(server.Said, l => l.Contains("stopping: the server is restarting"));
    }

    [Fact]
    public void ASecondSigtermStopsWithoutWaitingOutTheCountdown()
    {
        if (!OperatingSystem.IsLinux()) return;
        using Running server = new("--stop-notice 60");
        Stopwatch clock = Stopwatch.StartNew();
        using Client a = new(() => clock.Elapsed.TotalSeconds);
        a.Connect(new IPEndPoint(IPAddress.Loopback, server.Port), "Alice", "B1", "Core", "SolLite", "", "");
        Pump(a, () => server.Said.Any(l => l.Contains("Alice is in the world")), 20, "Alice to be in the world");

        server.Signal("TERM");
        Pump(a, () => double.IsFinite(a.ClosingAt), 5, "the countdown");
        Stopwatch w = Stopwatch.StartNew();
        server.Signal("TERM");
        Assert.True(server.Exited(10000), "the server did not exit");
        Assert.InRange(w.Elapsed.TotalSeconds, 0.0, 8.0);
    }
}
