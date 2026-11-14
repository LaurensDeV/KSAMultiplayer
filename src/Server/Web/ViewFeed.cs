using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace KSAMultiplayer.Server;

/// <summary>
/// The web viewer's live half: every socket open on <c>/api/v1/live</c>, each told the whole view
/// when it opens and what changed every <see cref="EverySeconds"/> after, and every event as it
/// happens. The tick thread hands it views and events and never waits on it; a socket that cannot
/// keep up is dropped rather than waited for.
/// </summary>
public sealed class ViewFeed
{
    public const double EverySeconds = 0.2;

    /// <summary>How often a craft is sent: the one somebody is flying at the view's own rate, the rest -- parked, debris -- far less.</summary>
    public const double FlownEvery = 0.2, ParkedEvery = 1.0;

    /// <summary>How many messages may wait for one socket before it is taken to have fallen behind.</summary>
    public const int Behind = 64;

    /// <summary>How many sockets at once.</summary>
    public const int MostSockets = 64;

    /// <summary>How many chat lines are kept for a page that opens late.</summary>
    public const int ChatKept = 200;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // A craft's numbers come from somebody's game: a NaN said there is a NaN shown here, and
        // not a view that cannot be written at all.
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public sealed record ChatLine(long Seq, double WallTime, string Kind, string Who, string Text);

    private sealed class Socket(WebSocket ws)
    {
        public readonly WebSocket Ws = ws;
        public Channel<byte[]> Out = null!;
        public readonly Dictionary<string, (uint Seq, double At)> Sent = new(StringComparer.Ordinal);
        public ServerView? Last;
    }

    private readonly ConcurrentDictionary<Socket, byte> _sockets = new();
    private readonly Channel<ViewEvent> _events = Channel.CreateBounded<ViewEvent>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly LinkedList<ChatLine> _chat = new();
    private readonly Lock _chatLock = new();
    private long _eventSeq;
    private ServerView? _view;
    private readonly Func<double> _wall;

    private readonly int _behind;

    /// <param name="behind">How many messages may wait for one socket; see <see cref="Behind"/>.</param>
    public ViewFeed(Func<double> wall, int behind = Behind) => (_wall, _behind) = (wall, behind);

    /// <summary>The newest view, for a request that wants all of it.</summary>
    public ServerView? View => Volatile.Read(ref _view);

    public int Sockets => _sockets.Count;

    private volatile Tuple<byte[], string>? _bodies;

    /// <summary>The system's description and its tag, set from the tick thread.</summary>
    public (byte[] Json, string Etag)? Bodies
    {
        get => _bodies is { } b ? (b.Item1, b.Item2) : null;
        set => _bodies = value is { } v ? Tuple.Create(v.Json, v.Etag) : null;
    }

    /// <summary>From the tick thread: the newest view. Nothing else is done here, so the tick never waits on a socket.</summary>
    public void Publish(ServerView view) => Volatile.Write(ref _view, view);

    /// <summary>From the tick thread, as it happens. Never blocks: past what is held, the oldest is dropped.</summary>
    public void Event(ViewEvent e) => _events.Writer.TryWrite(e);

    /// <summary>Chat and comings and goings after a sequence number, from those kept.</summary>
    public ChatLine[] ChatAfter(long after)
    {
        lock (_chatLock) return [.. _chat.Where(c => c.Seq > after)];
    }

    /// <summary>One round of what changed, to every socket. On the feed's own timer, never the tick thread.</summary>
    public void Pump()
    {
        double wall = _wall();
        List<ChatLine> happened = [];
        while (_events.Reader.TryRead(out ViewEvent? e))
        {
            ChatLine line = new(++_eventSeq, wall, e.Kind, e.Who, e.Text);
            happened.Add(line);
            lock (_chatLock)
            {
                _chat.AddLast(line);
                while (_chat.Count > ChatKept) _chat.RemoveFirst();
            }
        }
        ServerView? view = View;
        foreach (Socket s in _sockets.Keys)
        {
            foreach (ChatLine line in happened) Send(s, new { type = "event", @event = line });
            if (view is not null && !ReferenceEquals(view, s.Last)) Send(s, Delta(s, view, wall));
        }
    }

    /// <summary>What changed for one socket since the last view it was sent, crafts held to how often each is wanted.</summary>
    private static object Delta(Socket s, ServerView view, double wall)
    {
        (ViewCraft[] crafts, string[] removed) = Due(s.Sent, view, wall);
        s.Last = view;
        return new { type = "delta", view.Seq, view.WallTime, view.Server, view.Clock, view.Warp, view.Players, crafts, removed, view.BodiesEtag };
    }

    /// <summary>
    /// The crafts one reader is due, and those gone from under it, given what it was last sent:
    /// a craft that has said nothing new is never sent again, and one that has is held to its rate.
    /// Notes what it returns in <paramref name="sent"/>.
    /// </summary>
    internal static (ViewCraft[] Crafts, string[] Removed) Due(Dictionary<string, (uint Seq, double At)> sent, ServerView view, double wall)
    {
        List<ViewCraft> crafts = [];
        foreach (ViewCraft c in view.Crafts)
        {
            double every = c.Flags.Controlled ? FlownEvery : ParkedEvery;
            // A little under the rate, so a craft wanted every 0.2 s is not put off a whole round by
            // a round that came a millisecond early.
            if (sent.TryGetValue(c.Key, out (uint Seq, double At) was) && (was.Seq == c.Sequence || wall - was.At < every - 0.05)) continue;
            sent[c.Key] = (c.Sequence, wall);
            crafts.Add(c);
        }
        HashSet<string> standing = [.. view.Crafts.Select(c => c.Key)];
        string[] removed = [.. sent.Keys.Where(k => !standing.Contains(k))];
        foreach (string k in removed) sent.Remove(k);
        return ([.. crafts], removed);
    }

    private void Send(Socket s, object message)
    {
        // Full is fallen behind: the socket is closed under it rather than the round waiting for it.
        if (!s.Out.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(message, Json))) Drop(s);
    }

    // Fallen behind, or gone: either way nothing more is written for it, and one that fell behind
    // is cut off rather than waited on for a polite close.
    private void Drop(Socket s, bool abort = true)
    {
        if (!_sockets.TryRemove(s, out _)) return;
        s.Out.Writer.TryComplete();
        if (abort) s.Ws.Abort();
    }

    /// <summary>Serves one socket until it closes or is dropped: the whole view first, then whatever <see cref="Pump"/> gives it.</summary>
    /// <returns>False when there are already <see cref="MostSockets"/>, and nothing was served.</returns>
    public async Task<bool> Serve(WebSocket ws, CancellationToken stop)
    {
        Socket s = new(ws) { Out = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(_behind) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait }) };
        if (_sockets.Count >= MostSockets) return false;
        if (View is { } view)
        {
            foreach (ViewCraft c in view.Crafts) s.Sent[c.Key] = (c.Sequence, _wall());
            s.Last = view;
            s.Out.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(new { type = "view", view }, Json));
        }
        _sockets[s] = 0;
        // Read only to notice a close: the page says nothing over the socket.
        Task reading = Task.Run(async () =>
        {
            byte[] sink = new byte[256];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    if ((await ws.ReceiveAsync(sink, stop).ConfigureAwait(false)).MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
            // The page closed it: what is queued still goes, and the close is answered in kind.
            Drop(s, abort: false);
        }, stop);
        try
        {
            await foreach (byte[] message in s.Out.Reader.ReadAllAsync(stop).ConfigureAwait(false))
            {
                await ws.SendAsync(message, WebSocketMessageType.Text, true, stop).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        Drop(s);
        try
        {
            if (ws.State is WebSocketState.CloseReceived or WebSocketState.Open)
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException) { }
        await reading.ConfigureAwait(false);
        return true;
    }
}
