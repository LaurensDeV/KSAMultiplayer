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

    private readonly Socket _socket;
    private readonly Thread _reader;
    private readonly ConcurrentQueue<(byte[] Data, IPEndPoint From, double Arrived)> _in = new();
    private readonly Func<double> _clock;
    private readonly byte[] _out = new byte[Wire.BufferBytes];
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

    public void Send(Wire.Message message, IPEndPoint? to)
    {
        if (to is null || _closed) return;
        int n = Wire.Write(message, _out);
        if (Loss > 0 && _chance.NextDouble() < Loss) return;
        try
        {
            _socket.SendTo(_out.AsSpan(0, n), SocketFlags.None, to);
            Sent++;
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or ArgumentException)
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
        _socket.Dispose();
        _reader.Join(500);
    }
}
