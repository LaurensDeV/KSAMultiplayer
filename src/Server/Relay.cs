using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace KSAMultiplayer.Server;

/// <summary>
/// A UDP relay that holds datagrams back, so this protocol can be tested over a wire that is not
/// loopback. It serves no world and knows nothing about what it carries.
///
/// <para>Loopback has no loss, no reordering, no jitter and a round trip of about 0.05 ms. A player
/// on a real server is 9 ms away at best and 150 ms at worst, and a family of faults here is
/// proportional to that number: the clock offset each game estimates from ping and pong, which
/// feeds straight into how far behind it thinks it is; <see cref="Track.DelaySeconds"/> and
/// <see cref="Track.MostAheadSeconds"/>, the window a puppet is drawn in, which at 150 ms stops
/// being generous; and the server's ten seconds of patience, which is how a real player kept being
/// dropped. None of it can be reached on one machine without something like this.</para>
///
/// <para>Holding every datagram for <c>latency</c> in both directions gives a game-to-server round
/// trip of twice that, and a game-to-game path of twice that as well -- which is what a real server
/// that far from each player does. Jitter is what makes datagrams pass each other, so reordering is
/// not a separate setting: it is what a delay that varies already means.</para>
///
/// <para>One socket faces the games and there is one more per game, because the port a reply
/// arrives back on is what says which game it belongs to -- the same trick a NAT uses, and the
/// reason the server needs no part in this.</para>
/// </summary>
internal sealed class Relay : IDisposable
{
    private sealed record Hop(byte[] Data, double CameAt, double ReadyAt, Socket Through, IPEndPoint To);

    /// <summary>A game that has said nothing for this long gives up its socket.</summary>
    private const double IdleSeconds = 120.0;

    private readonly IPEndPoint _upstream;
    private readonly Socket _front;
    private readonly double _latency, _jitter, _loss;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly Lock _lock = new();
    private readonly List<Hop> _waiting = [];
    private readonly Dictionary<IPEndPoint, Leg> _legs = [];
    private volatile bool _closed;
    private long _passed, _dropped, _countedAt;
    private double _heldTotal;

    private sealed record Leg(Socket Up, IPEndPoint Game)
    {
        public double HeardAt { get; set; }
    }

    public int Port { get; }

    public Relay(IPEndPoint upstream, int port, IPAddress bind, double latencyMs, double jitterMs, double loss)
    {
        (_upstream, _latency, _jitter, _loss) = (upstream, latencyMs / 1000.0, jitterMs / 1000.0, loss);
        _front = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows()) _front.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
        _front.Bind(new IPEndPoint(bind, port));
        Port = ((IPEndPoint)_front.LocalEndPoint!).Port;
        new Thread(() => Read(_front, null)) { IsBackground = true, Name = "relay-front" }.Start();
    }

    private double Now() => _watch.Elapsed.TotalSeconds;

    /// <summary>
    /// What has gone through, been dropped, and the delay actually applied -- measured rather than
    /// asked for, so a run never has to take the asked-for number on trust.
    ///
    /// <para>The mean is of what has passed since this was last called. A mean over the whole run
    /// is dominated by the burst that hands a game the world, tens of kilobytes queued at once, and
    /// read 31 ms where the steady state was 27.</para>
    /// </summary>
    public (long Passed, long Dropped, double MeanMs, int Waiting, int Games) Counts()
    {
        lock (_lock)
        {
            long since = _passed - _countedAt;
            double mean = since > 0 ? _heldTotal / since * 1000.0 : 0.0;
            (_countedAt, _heldTotal) = (_passed, 0.0);
            return (_passed, _dropped, mean, _waiting.Count, _legs.Count);
        }
    }

    /// <summary>
    /// Reads one socket for as long as the relay runs. <paramref name="leg"/> is null for the socket
    /// facing the games, where every datagram goes upstream and a sender not seen before gets a
    /// socket of its own; for any other it is the game that socket answers to.
    /// </summary>
    private void Read(Socket socket, Leg? leg)
    {
        byte[] buffer = new byte[Wire.BufferBytes];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_closed)
        {
            int n;
            try
            {
                n = socket.ReceiveFrom(buffer, ref any);
            }
            catch (Exception)
            {
                if (_closed) return;
                Thread.Sleep(10);
                continue;
            }
            if (leg is null)
            {
                if (LegFor((IPEndPoint)any) is { } found) Hold(buffer[..n], found.Up, _upstream);
            }
            else
            {
                Hold(buffer[..n], _front, leg.Game);
            }
        }
    }

    private Leg? LegFor(IPEndPoint game)
    {
        lock (_lock)
        {
            if (_legs.TryGetValue(game, out Leg? found))
            {
                found.HeardAt = Now();
                return found;
            }
            Socket up;
            try
            {
                up = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                if (OperatingSystem.IsWindows()) up.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
                up.Bind(new IPEndPoint(IPAddress.Any, 0));
            }
            catch (SocketException)
            {
                return null;
            }
            Leg leg = new(up, game) { HeardAt = Now() };
            _legs[game] = leg;
            new Thread(() => Read(up, leg)) { IsBackground = true, Name = $"relay-{game.Port}" }.Start();
            Console.WriteLine($"a game at {game} is now {_latency * 2000.0:F0} ms from the server");
            return leg;
        }
    }

    private void Hold(byte[] data, Socket through, IPEndPoint to)
    {
        if (_loss > 0.0 && Random.Shared.NextDouble() < _loss)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        double wait = Math.Max(_latency + (_jitter > 0.0 ? (Random.Shared.NextDouble() * 2.0 - 1.0) * _jitter : 0.0), 0.0);
        double came = Now();
        lock (_lock) _waiting.Add(new Hop(data, came, came + wait, through, to));
    }

    /// <summary>Everything now due, sent. Called as fast as the loop goes round.</summary>
    public void Pump()
    {
        double now = Now();
        List<Hop> due = [];
        lock (_lock)
        {
            for (int i = _waiting.Count - 1; i >= 0; i--)
            {
                if (_waiting[i].ReadyAt > now) continue;
                due.Add(_waiting[i]);
                _waiting.RemoveAt(i);
            }
            foreach ((IPEndPoint at, Leg leg) in _legs.ToArray())
            {
                if (now - leg.HeardAt <= IdleSeconds) continue;
                _legs.Remove(at);
                leg.Up.Dispose();
            }
        }
        // Oldest due first, so two that came out of the queue together keep the order their own
        // delays gave them rather than the order the list happened to hold.
        due.Sort((x, y) => x.ReadyAt.CompareTo(y.ReadyAt));
        foreach (Hop h in due)
        {
            try
            {
                h.Through.SendTo(h.Data, SocketFlags.None, h.To);
                lock (_lock)
                {
                    _passed++;
                    _heldTotal += now - h.CameAt;
                }
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>How long until the next datagram is due, so the loop sleeps rather than spins.</summary>
    public double NextDueIn()
    {
        double now = Now();
        lock (_lock)
        {
            double soonest = double.PositiveInfinity;
            foreach (Hop h in _waiting) soonest = Math.Min(soonest, h.ReadyAt);
            return double.IsFinite(soonest) ? Math.Max(soonest - now, 0.0) : 0.005;
        }
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _front.Dispose();
        lock (_lock)
        {
            foreach (Leg leg in _legs.Values) leg.Up.Dispose();
            _legs.Clear();
        }
    }
}
