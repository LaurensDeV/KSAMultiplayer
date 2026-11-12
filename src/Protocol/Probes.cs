using System.Net;

namespace KSAMultiplayer;

/// <summary>
/// Asks many servers at once what they are, and nothing else: one socket, one thread, no session, no
/// place held on anybody's roster.
///
/// <para><see cref="Client.Probe"/> asks one, and does it by being a client that stops after the
/// answer -- which means a socket and a keepalive thread of its own, since that is what a client
/// needs to be a client. A list of thirty servers built that way is thirty sockets and thirty
/// threads for thirty datagrams, and the keepalive in every one of them has nothing to keep
/// alive.</para>
///
/// <para>So the question goes out from one socket to every address at once and the answers are
/// told apart by where they came from, which is the only thing about a datagram that is not the
/// sender's to choose.</para>
///
/// <para>The question is a <see cref="Wire.Ask"/> and not the hello a join begins with, because a
/// server answers one whatever version it or the asker speaks: what it says of itself is read by a
/// directory built several releases ago, and by a game deciding which of the two is out of date.
/// A server from before the ask existed answers it as it answers anything of another version, with
/// a refusal written in its own, which cannot be read as a refusal -- only as a sighting. It is
/// remembered as that rather than as silent: a row that says which version it speaks is a row a
/// player can act on.</para>
/// </summary>
public sealed class Probes : IDisposable
{
    /// <summary>How often an unanswered probe is sent again, s. Two or three go out inside <see cref="WaitSeconds"/>, so one lost datagram is not a dead server.</summary>
    private const double AskEverySeconds = 0.4;

    /// <summary>How long a server is waited for, s, before it is taken as unreachable from here.</summary>
    public const double WaitSeconds = 2.0;

    private static readonly Wire.Ask Question = new();

    private readonly Udp _udp;
    private readonly Func<double> _clock;
    private readonly Dictionary<IPEndPoint, (double Next, double Until, double Sent)> _asking = [];
    private readonly Dictionary<IPEndPoint, double> _rtt = [];
    private readonly Dictionary<IPEndPoint, ServerFacts> _answered = [];
    private readonly Dictionary<IPEndPoint, ushort> _elsewhere = [];
    private readonly HashSet<IPEndPoint> _silent = [];

    public Probes(Func<double> clock, int port = 0)
    {
        _clock = clock;
        _udp = new Udp(port, clock, "KSAMultiplayer probes");
    }

    /// <summary>
    /// What answered, by the address it answered from, which is what two listings of one server
    /// have in common when nothing else about them does. Not all of it can be joined:
    /// <see cref="ServerFacts.Protocol"/> says which version each speaks.
    /// </summary>
    public IReadOnlyDictionary<IPEndPoint, ServerFacts> Answered => _answered;

    /// <summary>
    /// How long each answer took to come back, s. Measured from the ask that was answered rather
    /// than the first one sent, so a server that needed asking twice is not charged for the wait.
    ///
    /// <para>One sample, because one is what a question and its answer give. It is the round trip to
    /// the server and not the time to join it, and it costs nothing to know: the probe was a round
    /// trip already.</para>
    /// </summary>
    public IReadOnlyDictionary<IPEndPoint, double> Rtt => _rtt;

    /// <summary>What answered without saying what it is, and which version it answered in: a server from before <see cref="Wire.Ask"/>.</summary>
    public IReadOnlyDictionary<IPEndPoint, ushort> Elsewhere => _elsewhere;

    /// <summary>What never answered. Unreachable from here, which is not the same as down.</summary>
    public IReadOnlyCollection<IPEndPoint> Silent => _silent;

    /// <summary>How many are still being waited for.</summary>
    public int Waiting => _asking.Count;

    /// <summary>
    /// Asks one server, unless it has already been asked or already answered. Sends at once rather
    /// than on the next <see cref="Update"/>, so a window that asks about thirty servers in one
    /// frame has thirty datagrams on the wire before it draws.
    /// </summary>
    public void Ask(IPEndPoint at)
    {
        if (_asking.ContainsKey(at) || _answered.ContainsKey(at) || _elsewhere.ContainsKey(at) || _silent.Contains(at)) return;
        double now = _clock();
        _asking[at] = (now + AskEverySeconds, now + WaitSeconds, now);
        _udp.Send(Question, at);
    }

    /// <summary>Asks again about a server already settled, for a list being refreshed rather than built.</summary>
    public void AskAgain(IPEndPoint at)
    {
        _answered.Remove(at);
        _elsewhere.Remove(at);
        _silent.Remove(at);
        _asking.Remove(at);
        Ask(at);
    }

    public void Forget()
    {
        _asking.Clear();
        _answered.Clear();
        _elsewhere.Clear();
        _silent.Clear();
        _rtt.Clear();
    }

    /// <summary>Drained by whoever runs the window or the tick: nothing here is touched from two threads but the socket's own reader.</summary>
    public void Update()
    {
        double now = _clock();
        while (_udp.TryReceive(out (byte[] Data, IPEndPoint From, double Arrived) d))
        {
            // Only what was asked about, and only once: anything else arriving at this socket is
            // either an answer already counted or a datagram nobody here sent for.
            if (!_asking.ContainsKey(d.From)) continue;
            Wire.Message? m = Wire.Read(d.Data, out ushort version);
            if (m is Wire.Facts f)
            {
                _answered[d.From] = ServerFacts.Of(f);
                // Never negative: an answer to the ask before last arrives after the last one went
                // out, and a round trip measured through that is a number from nowhere.
                _rtt[d.From] = Math.Max(0.0, d.Arrived - _asking[d.From].Sent);
                _asking.Remove(d.From);
            }
            else if (m is null && version != 0 && version != Wire.Version)
            {
                _elsewhere[d.From] = version;
                _asking.Remove(d.From);
            }
            // Anything else -- a datagram cut short, or one of this version that is not an answer --
            // is left outstanding, and the next ask covers it.
        }
        foreach (IPEndPoint at in _asking.Keys.ToArray())
        {
            (double next, double until, _) = _asking[at];
            if (now > until)
            {
                _silent.Add(at);
                _asking.Remove(at);
                continue;
            }
            if (now < next) continue;
            _udp.Send(Question, at);
            _asking[at] = (now + AskEverySeconds, until, now);
        }
    }

    public void Dispose() => _udp.Dispose();
}
