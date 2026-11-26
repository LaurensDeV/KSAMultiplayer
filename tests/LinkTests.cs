using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// The link over a line like a player's: so many bytes a second, a router that holds a little and
/// drops the rest, and a delay. Loopback has none of that, and a burst that arrives whole there
/// is what jammed every other game on ksamp.gg when one player's craft came apart into hundreds.
/// </summary>
public class LinkTests
{
    /// <summary>One direction of a line, in time that is stepped rather than waited for.</summary>
    private sealed class Line(double bytesPerSecond, int bufferBytes, double delaySeconds)
    {
        private readonly Queue<(double At, Wire.Message M, int Bytes)> _queued = new();
        private readonly List<(double At, Wire.Message M)> _flying = [];
        private double _freeAt;
        private int _held;
        public int Dropped { get; private set; }

        public void Put(Wire.Message m, double now)
        {
            int bytes = Size(m);
            if (_held + bytes > bufferBytes)
            {
                Dropped++;
                return;
            }
            _held += bytes;
            _freeAt = Math.Max(_freeAt, now) + (bytes / bytesPerSecond);
            _queued.Enqueue((_freeAt, m, bytes));
        }

        public List<Wire.Message> Take(double now)
        {
            while (_queued.TryPeek(out var q) && q.At <= now)
            {
                _queued.Dequeue();
                _held -= q.Bytes;
                _flying.Add((q.At + delaySeconds, q.M));
            }
            List<Wire.Message> due = [.. _flying.Where(f => f.At <= now).Select(f => f.M)];
            _flying.RemoveAll(f => f.At <= now);
            return due;
        }

        private static int Size(Wire.Message m)
        {
            byte[] buffer = new byte[Wire.BufferBytes];
            return Wire.Write(m, buffer) + 28;
        }
    }

    // A server sending to a player whose line takes 250 KB a second and holds 64 KB, 80 ms away.
    private static (int Arrived, double Seconds, int Dropped) Burst(int count, int bytes, double untilSeconds,
                                                                    Func<Link, double, List<uint>>? send = null)
    {
        Link server = new(), player = new();
        Line down = new(250_000, 64 * 1024, 0.04), up = new(250_000, 64 * 1024, 0.04);
        Random r = new(7);
        HashSet<int> got = [];
        double now = 0.0, last = 0.0;
        for (int i = 0; i < count; i++)
        {
            byte[] data = new byte[bytes];
            r.NextBytes(data);
            BitConverter.GetBytes(i).CopyTo(data, 0);
            server.Send(data, now);
        }
        while (now < untilSeconds && got.Count < count)
        {
            server.Pump(now, m => down.Put(m, now));
            player.Pump(now, m => up.Put(m, now));
            foreach (Wire.Message m in down.Take(now))
            {
                if (player.Handle(m, now, a => up.Put(a, now)) is { } whole && got.Add(BitConverter.ToInt32(whole, 0))) last = now;
            }
            foreach (Wire.Message m in up.Take(now)) server.Handle(m, now, a => down.Put(a, now));
            now += 0.005;
        }
        return (got.Count, last, down.Dropped);
    }

    [Fact]
    public void ABurstOfCraftAllArrivesOverAHomeLine()
    {
        // What one player's game said in a minute on ksamp.gg: forty definitions of 80 KB.
        (int arrived, double seconds, int dropped) = Burst(40, 80 * 1024, 120.0);
        Assert.True(arrived == 40, $"{arrived} of 40 arrived in 120 s, {dropped} pieces dropped on the line");
        // 3.2 MB at 250 KB a second is 13 s; a link that keeps the line full takes not much longer.
        Assert.True(seconds < 30.0, $"the last arrived after {seconds:F1} s, {dropped} pieces dropped on the line");
        // And without flooding the line: 2,980 pieces carry it, and the old link lost ten times as
        // many, every transfer at its own pace. The link's one pace is still faster than this line,
        // so some are lost at the router however few transfers go at once.
        Assert.True(dropped < 2980, $"{dropped} pieces dropped on the line, the last arriving after {seconds:F1} s");
    }

    [Fact]
    public void OneTransferStillGoesAtTheLinesPace()
    {
        (int arrived, double seconds, _) = Burst(1, 500 * 1024, 60.0);
        Assert.Equal(1, arrived);
        Assert.True(seconds < 5.0, $"half a megabyte took {seconds:F1} s");
    }

    // Two links over a line, stepped until done or out of time; what arrives, in order, by its first four bytes.
    private static List<int> Run(Action<Link> send, double seconds, double bytesPerSecond = 250_000, Action<Link, double>? during = null)
    {
        Link server = new(), player = new();
        Line down = new(bytesPerSecond, 64 * 1024, 0.04), up = new(bytesPerSecond, 64 * 1024, 0.04);
        List<int> got = [];
        send(server);
        for (double now = 0.0; now < seconds; now += 0.005)
        {
            during?.Invoke(server, now);
            server.Pump(now, m => down.Put(m, now));
            player.Pump(now, m => up.Put(m, now));
            foreach (Wire.Message m in down.Take(now))
            {
                if (player.Handle(m, now, a => up.Put(a, now)) is { } whole) got.Add(BitConverter.ToInt32(whole, 0));
            }
            foreach (Wire.Message m in up.Take(now)) server.Handle(m, now, a => down.Put(a, now));
        }
        return got;
    }

    private static byte[] Numbered(int n, int bytes)
    {
        byte[] data = new byte[bytes];
        BitConverter.GetBytes(n).CopyTo(data, 0);
        return data;
    }

    [Fact]
    public void AWorldGoesAheadOfCraftNotYetBegun()
    {
        List<int> got = Run(l =>
        {
            for (int i = 0; i < 30; i++) l.Send(Numbered(i, 80 * 1024), 0.0, replaces: $"craft {i}");
            l.Send(Numbered(1000, 400 * 1024), 0.0, first: true);
        }, 60.0);
        Assert.Equal(31, got.Count);
        // Behind only the few already going when it was asked for.
        Assert.InRange(got.IndexOf(1000), 0, 4);
    }

    [Fact]
    public void OnlyTheNewestWordAboutACraftWaitingItsTurnGoes()
    {
        Link link = new();
        List<uint> ids = [];
        for (int i = 0; i < 6; i++) link.Send(Numbered(i, 8 * 1024), 0.0, replaces: $"busy {i}");
        uint old = link.Send(Numbered(100, 8 * 1024), 0.0, replaces: "Cube");
        uint newer = link.Send(Numbered(101, 8 * 1024), 0.0, replaces: "Cube");
        Assert.False(link.Going(old));
        Assert.True(link.Going(newer));

        List<int> got = Run(l =>
        {
            for (int i = 0; i < 6; i++) l.Send(Numbered(i, 80 * 1024), 0.0, replaces: $"busy {i}");
            l.Send(Numbered(100, 8 * 1024), 0.0, replaces: "Cube");
            l.Send(Numbered(101, 8 * 1024), 0.0, replaces: "Cube");
        }, 30.0);
        Assert.Contains(101, got);
        Assert.DoesNotContain(100, got);
    }

    [Fact]
    public void ATransferWaitingItsTurnIsNotGivenUpOn()
    {
        // Minutes of craft on a slow line: the last waits far longer than a transfer is
        // given before it is let go, and still goes.
        List<int> got = Run(l =>
        {
            for (int i = 0; i < 40; i++) l.Send(Numbered(i, 80 * 1024), 0.0);
        }, 900.0, bytesPerSecond: 20_000);
        Assert.Equal(40, got.Distinct().Count());
    }
}
