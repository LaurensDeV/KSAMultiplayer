using System.Net;
using System.Net.Sockets;

namespace KSAMultiplayer;

/// <summary>
/// Which port a server is on, for an address that names none.
///
/// <para>There are two it may be. <see cref="Wire.DefaultPort"/> is where a server listens unless it
/// is told otherwise; <see cref="Wire.LegacyPort"/> is where every server listened before, and where
/// every one set up before still does, since its port is written in its settings and in somebody's
/// firewall. So both are asked, with a <see cref="Wire.Ask"/>, which a server answers to anybody,
/// and the one that answers is the server.</para>
/// </summary>
public static class Ports
{
    // Asked again this often while nothing has answered: a datagram can be lost.
    private const double AskEverySeconds = 0.3;

    // How long the port asked first is waited for once the other has answered. A server on both is
    // the one on the first, whichever datagram arrived sooner.
    private const double FirstWithinSeconds = 0.15;

    /// <summary>
    /// The port of the two that a server answers on at an address, or <paramref name="first"/> when
    /// neither does within <paramref name="within"/>: a join that fails then says the port a server
    /// is on by default.
    /// </summary>
    public static async Task<int> AnsweringAsync(IPAddress at, TimeSpan within, int first = Wire.DefaultPort, int second = Wire.LegacyPort)
    {
        try
        {
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // Windows reports an ICMP refusal of an earlier send as a failure of the next receive,
            // and a refusal is what the port nobody is on answers with.
            if (OperatingSystem.IsWindows()) socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            byte[] ask = new byte[Wire.BufferBytes];
            int n = Wire.Write(new Wire.Ask(), ask);
            byte[] heard = new byte[Wire.BufferBytes];
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            double askNext = 0.0, secondAt = double.PositiveInfinity;
            while (clock.Elapsed < within)
            {
                double now = clock.Elapsed.TotalSeconds;
                if (now >= secondAt + FirstWithinSeconds) return second;
                if (now >= askNext)
                {
                    foreach (int port in (int[])[first, second])
                    {
                        try { socket.SendTo(ask.AsSpan(0, n), SocketFlags.None, new IPEndPoint(at, port)); }
                        catch (SocketException) { }
                    }
                    askNext = now + AskEverySeconds;
                }
                using CancellationTokenSource wait = new(TimeSpan.FromMilliseconds(50));
                try
                {
                    SocketReceiveFromResult got = await socket.ReceiveFromAsync(heard, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), wait.Token).ConfigureAwait(false);
                    if (got.RemoteEndPoint is not IPEndPoint from || !from.Address.Equals(at)) continue;
                    // Anything at all from it: a server from before the ask says its version, and is there.
                    if (from.Port == first) return first;
                    if (from.Port == second && double.IsPositiveInfinity(secondAt)) secondAt = clock.Elapsed.TotalSeconds;
                }
                catch (OperationCanceledException) { }
                catch (SocketException) { }
            }
            return double.IsPositiveInfinity(secondAt) ? first : second;
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            return first;
        }
    }
}
