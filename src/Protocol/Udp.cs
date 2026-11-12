using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace KSAMultiplayer;

/// <summary>
/// One UDP socket, read on a thread of its own into a queue that whoever runs the session drains once
/// a frame or a tick, so nothing else is touched from two threads. The clock is the caller's.
/// </summary>
public sealed class Udp : IDisposable
{
    private const int MostQueued = 4096;

    // How long the reader waits to be told there is something to read before it looks at whether
    // the socket has been closed under it, microseconds.
    private const int LookEveryMicroseconds = 100_000;

    private readonly Socket _socket;
    private readonly Thread _reader;
    private readonly ConcurrentQueue<(byte[] Data, IPEndPoint From, double Arrived)> _in = new();
    private readonly Func<double> _clock;
    private readonly byte[] _out = new byte[Wire.BufferBytes];
    private readonly Lock _sending = new();
    private readonly Random _chance = new();
    private volatile bool _closed;

    public int Port { get; }

    /// <summary>For tests: the share of datagrams this end sends that are lost on purpose.</summary>
    public double Loss { get; set; }

    public long Sent { get; private set; }
    public long Received { get; private set; }

    public Udp(int port, Func<double> clock, string name, IPAddress? bind = null)
    {
        _clock = clock;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Windows reports an ICMP refusal of an earlier send as a failure of the next receive.
        if (OperatingSystem.IsWindows()) _socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
        _socket.Bind(new IPEndPoint(bind ?? IPAddress.Any, port));
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        _reader = new Thread(Read) { IsBackground = true, Name = name };
        _reader.Start();
    }

    public bool TryReceive(out (byte[] Data, IPEndPoint From, double Arrived) datagram) => _in.TryDequeue(out datagram);

    /// <remarks>
    /// Locked because the keepalive is sent from a thread of its own while the game's frame loop
    /// sends everything else: two senders sharing one scratch buffer wrote over each other's bytes
    /// mid-datagram. Uncontended almost always -- the keepalive only fires while the frame loop is
    /// not sending at all -- and a socket send is orders of magnitude dearer than taking a lock.
    /// </remarks>
    public void Send(Wire.Message message, IPEndPoint? to)
    {
        if (to is null || _closed) return;
        if (Loss > 0 && _chance.NextDouble() < Loss) return;
        lock (_sending)
        try
        {
            // Inside the guard, not in front of it: a message that will not fit the buffer throws
            // here, and thrown on it took the caller's bookkeeping with it -- the server stopped
            // saying when to send the next roster and threw in the same place on every tick
            // afterwards. One datagram lost is what the rest of this protocol is built for.
            int n = Wire.Write(message, _out);
            _socket.SendTo(_out.AsSpan(0, n), SocketFlags.None, to);
            Sent++;
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or ArgumentException
                                      or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
        }
    }

    private void Read()
    {
        byte[] buffer = new byte[Wire.BufferBytes];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_closed)
        {
            try
            {
                // Never waiting in the receive itself. Closing a socket waits for whoever is in
                // it to come out, and makes them: on Windows and Linux. On macOS a UDP receive
                // cannot be made to return, so closing a socket nobody was sending to waited for
                // ever -- the list of servers' own, when the game was closed, and a game with
                // this mod in it never finished exiting. Asked first, for a tenth of a second at
                // a time, the reader is in the receive only while there is a datagram to take.
                if (!_socket.Poll(LookEveryMicroseconds, SelectMode.SelectRead)) continue;
                int n = _socket.ReceiveFrom(buffer, ref any);
                Received++;
                if (_in.Count < MostQueued) _in.Enqueue((buffer[..n], (IPEndPoint)any, _clock()));
            }
            // Inside a game, anything thrown on any thread ends it.
            catch (Exception)
            {
                if (_closed) return;
                Thread.Sleep(10);
            }
        }
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        // The reader out of the socket first, which it is within one look, and only then the
        // socket: see Read for what closing it under a reader costs.
        _reader.Join(1000);
        _socket.Dispose();
    }
}
