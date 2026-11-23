using System.Text;
using KSA;
using StarMap.API.Events;
using StarMap.API.Services;

namespace KSAMultiplayer;

/// <summary>
/// What other mods say of the craft they add to, and what they are told, over StarMap's event bus.
///
/// <para>A mod that adds cars keeps their steering and their wheels in its own memory, and nothing
/// this mod sends says a word of them. So such a mod says what it has to say of a craft to whoever
/// is listening, and is told what was said of it elsewhere, and whether a craft is being driven in
/// another game. It does not know this mod is there, and this mod does not know what a car is:
/// the names below are all either of them has. A bus event is matched by its name and by the type
/// it carries, and a type of a mod's own is another type in every other mod, so everything here
/// is a dictionary of names to plain values and KSA's own.</para>
///
/// <list type="bullet">
/// <item><c>craftsync.hello</c>, from a mod, with <c>mod</c>. Answered in place, since publishing is
/// done before it returns: <c>provider</c> is set true, and <c>session</c> to whether this game is
/// in a shared world now. A mod that hears nothing back is on its own.</item>
/// <item><c>craftsync.session</c>, to mods: <c>active</c>, when this game enters or leaves a shared
/// world.</item>
/// <item><c>craftsync.authority</c>, to mods: <c>craft</c> (a <see cref="Vehicle"/>) and
/// <c>remote</c>. A remote craft is where another game says it is, at every physics step: a mod
/// must not move it, and may go on animating it.</item>
/// <item><c>craftsync.state</c>, from a mod: <c>craft</c>, <c>channel</c> and <c>data</c> (bytes, at
/// most <see cref="Wire.ExtraBytes"/>). The newest word about a craft this game's player has, said
/// to every other game with the craft's own state. Said again each frame it is still true; one
/// not said for a second is no longer sent.</item>
/// <item><c>craftsync.state.remote</c>, to mods: the same three, as another game said them of a
/// craft that is remote here.</item>
/// <item><c>craftsync.event</c>, from a mod, on any thread: <c>channel</c>, <c>data</c> (bytes, at most
/// <see cref="ModEvents.EventBytes"/>), and if it likes <c>craft</c> and <c>to</c>, a player's
/// number or name. Something said once, which arrives in the order it was said or is told lost:
/// to every other game in the world, or to the one named. Answered in place: <c>queued</c>, and
/// with it <c>id</c>, this game's number for it, or <c>why</c> not. Queued is taken, not arrived.</item>
/// <item><c>craftsync.event.remote</c>, to mods: <c>channel</c>, <c>data</c>, <c>from</c> and
/// <c>fromName</c> as the server heard it, and for one about a craft its <c>key</c>, the
/// <c>craft</c> itself where it stands here, and its <c>owner</c> where it has one. Nothing stops
/// a game saying an event of a craft that is not its own, so a mod that cares compares the two.</item>
/// <item><c>craftsync.event.lost</c>, to mods, in the game that said it: <c>id</c>, <c>channel</c>,
/// <c>to</c> and <c>toName</c>, and <c>why</c>. Once for each game one did not reach.</item>
/// </list>
///
/// <para>A mod is told of an event on the frame, with KSA's physics running: it must not add,
/// remove or stage a craft from there.</para>
/// </summary>
internal static class Guests
{
    public const string Hello = "craftsync.hello", Session = "craftsync.session", Authority = "craftsync.authority",
                        State = "craftsync.state", RemoteState = "craftsync.state.remote",
                        Event = "craftsync.event", RemoteEvent = "craftsync.event.remote", LostEvent = "craftsync.event.lost";

    // How many of the last things the mods were told of events are kept for the bridge to show.
    private const int MostHeard = 32;

    // How long a mod's word about a craft goes on being sent with nothing new said.
    private const double SaidForSeconds = 1.0;

    private static IEventPublisher<Dictionary<string, object?>>? _session, _authority, _remoteState;
    private static readonly List<IDisposable> Listening = [];
    private static readonly HashSet<Vehicle> Remote = [];
    private static readonly Dictionary<Vehicle, Dictionary<string, (byte[] Data, double At)>> Said = [];
    private static volatile bool _inSession;

    private static IEventPublisher<Dictionary<string, object?>>? _event, _remoteEvent, _lostEvent;

    // What mods have said once and this game has not yet taken up. Filled on whatever thread a mod
    // publishes from and emptied on the frame, so a number is given and a place in the queue taken
    // under the one lock: the numbers rise in the order things go out. Whether this game is in a
    // world is read under it too, or one said as the world was left would be taken and never sent.
    private static readonly object Gate = new();
    private static readonly Queue<(uint Id, string Channel, byte[] Data, Vehicle? Craft, object? To)> Out = new();
    private static int _outBytes;

    // Never begun again, so a number is one event for as long as the game runs.
    private static uint _ids;

    // What the session held unsent at the end of the last frame: read where a mod publishes, which is any thread.
    private static volatile int _clientWaiting;
    private static double _nobodySaid = double.NegativeInfinity;
    private static readonly List<Dictionary<string, object?>> Heard = [];

    /// <summary>Listens and gets ready to speak. From the mod's constructor, so nothing said later is missed.</summary>
    public static void Start(IEventBus bus)
    {
        try
        {
            Listening.Add(bus.Subscribe<Dictionary<string, object?>>(OnHello, Hello));
            Listening.Add(bus.Subscribe<Dictionary<string, object?>>(OnState, State));
            _session = bus.GetPublisher<Dictionary<string, object?>>(Session);
            _authority = bus.GetPublisher<Dictionary<string, object?>>(Authority);
            _remoteState = bus.GetPublisher<Dictionary<string, object?>>(RemoteState);
            Log.Info("listening for other mods on StarMap's event bus");
        }
        catch (Exception e)
        {
            Log.Warn($"other mods cannot take part in a shared world: {e.GetBaseException().Message}");
        }
        // Apart from the above, so a bus that will not take these still carries what it did before.
        try
        {
            Listening.Add(bus.Subscribe<Dictionary<string, object?>>(OnEvent, Event));
            _event = bus.GetPublisher<Dictionary<string, object?>>(Event);
            _remoteEvent = bus.GetPublisher<Dictionary<string, object?>>(RemoteEvent);
            _lostEvent = bus.GetPublisher<Dictionary<string, object?>>(LostEvent);
        }
        catch (Exception e)
        {
            Log.Warn($"other mods cannot say anything once: {e.GetBaseException().Message}");
        }
    }

    // On whatever thread a mod publishes from, which may be one of KSA's workers: nothing of the
    // session or the world is touched here, only what is said and what is under the lock.
    private static void OnEvent(Dictionary<string, object?> said)
    {
        try
        {
            object? to = said.GetValueOrDefault("to");
            string? why =
                !_inSession ? "not in a shared world"
                : said.GetValueOrDefault("channel") is not string { Length: > 0 and <= Wire.ChannelChars } ? $"channel must be a string of 1 to {Wire.ChannelChars} characters"
                : said.GetValueOrDefault("data") is not byte[] { Length: <= ModEvents.EventBytes } ? $"data must be bytes, {ModEvents.EventBytes} at most"
                : said.GetValueOrDefault("craft") is not (null or Vehicle) ? "craft must be a Vehicle"
                : to is not (null or int and > 0 and <= 255 or byte and > 0 or string { Length: > 0 }) ? "to must be a player's id or name"
                : null;
            uint id = 0;
            if (why is null)
            {
                string channel = (string)said["channel"]!;
                // The mod's own bytes are its own to change once this returns.
                byte[] copy = (byte[])((byte[])said["data"]!).Clone();
                // As it will weigh without the craft's name, which is not known off the frame. Under
                // what it comes to is safe: the session counts it exactly and what it refuses is told lost.
                int size = 10 + Encoding.UTF8.GetByteCount(channel) + copy.Length;
                lock (Gate)
                {
                    if (!_inSession) why = "not in a shared world";
                    else if (_outBytes + _clientWaiting + size > ModEvents.WaitingBytes) why = "too many events are waiting to be sent";
                    else
                    {
                        id = ++_ids;
                        Out.Enqueue((id, channel, copy, said.GetValueOrDefault("craft") as Vehicle, to));
                        _outBytes += size;
                    }
                }
            }
            if (why is null)
            {
                said["queued"] = true;
                said["id"] = (int)id;
                said.Remove("why");
            }
            else
            {
                said["queued"] = false;
                said["why"] = why;
                // A mod that says each with the dictionary it said the last with is not left the last one's number.
                said.Remove("id");
            }
        }
        // Never into whoever published: it is another mod's code, on a thread of KSA's.
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// What other games' mods said once handed to this game's, what of this game's did not arrive,
    /// and what this game's mods have said since the last frame sent on its way. On the frame.
    ///
    /// <para>Three rounds and in this order, so a mod that says something from inside what it is
    /// told only ever adds to what goes out next frame.</para>
    /// </summary>
    public static void Events(Client c, Dictionary<string, Vehicle> here)
    {
        while (c.Events.TryDequeue(out ModEvent e))
        {
            Dictionary<string, object?> told = new() { ["channel"] = e.Channel, ["data"] = e.Data, ["from"] = (int)e.From, ["fromName"] = e.FromName };
            // Whatever craft it is of: this player's own, nobody's, or one that is not here at all.
            // A mod's newest word about a craft is only wanted where the craft is another's; an
            // event is whatever its mod means by it.
            if (e.Craft.Length > 0)
            {
                told["key"] = e.Craft;
                if (here.TryGetValue(e.Craft, out Vehicle? craft) && World.IsAlive(craft)) told["craft"] = craft;
                if (c.OwnerOf(e.Craft) is var owner and not 0) told["owner"] = (int)owner;
            }
            Tell(_remoteEvent, "remote", told);
        }
        while (c.EventsLost.TryDequeue(out Wire.EventLost? l)) Lost(l.Id, l.Channel, l.To, l.ToName, l.Why);

        (uint Id, string Channel, byte[] Data, Vehicle? Craft, object? To)[] saying;
        lock (Gate)
        {
            saying = [.. Out];
            Out.Clear();
            _outBytes = 0;
        }
        foreach ((uint id, string channel, byte[] data, Vehicle? craft, object? given) in saying)
        {
            byte to = given switch
            {
                int n => (byte)n,
                byte n => n,
                string name => c.Players.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Id,
                _ => 0,
            };
            if (given is string nobody && to == 0)
            {
                if (Sync.Wall() - _nobodySaid > 60.0)
                {
                    _nobodySaid = Sync.Wall();
                    Log.Warn($"a mod's event on {channel} was for {nobody}, who is not here");
                }
                Lost(id, channel, 0, nobody, Wire.EventLoss.Nobody);
                continue;
            }
            // By the name the world knows it by, as its state carries it. One destroyed since it
            // was said still has its name, and that it blew up is a fair thing to say of it.
            string key = craft is null ? "" : Wire.Clip(Sync.KeyOf(craft), Wire.KeyChars);
            if (!c.SendEvent(id, channel, data, key, to)) Lost(id, channel, to, c.OnRoster(to), Wire.EventLoss.Unsent);
        }
        _clientWaiting = c.EventsWaiting;
    }

    private static void Lost(uint id, string channel, byte to, string toName, Wire.EventLoss why) =>
        Tell(_lostEvent, "lost", new()
        {
            ["id"] = (int)id,
            ["channel"] = channel,
            ["to"] = (int)to,
            ["toName"] = toName,
            ["why"] = why switch
            {
                Wire.EventLoss.Full => "full",
                Wire.EventLoss.GaveUp => "gave up",
                Wire.EventLoss.Nobody => "nobody",
                Wire.EventLoss.Left => "left",
                Wire.EventLoss.Rate => "rate",
                Wire.EventLoss.Unsent => "unsent",
                _ => "lost",
            },
        });

    // Kept for the bridge before the mods are told, so what a mod does to what it is handed is not
    // what is shown. Plain values only: a craft or bytes would lose the bridge its whole answer.
    private static void Tell(IEventPublisher<Dictionary<string, object?>>? to, string what, Dictionary<string, object?> told)
    {
        if (Build.Developer)
        {
            Dictionary<string, object?> kept = new() { ["name"] = what, ["wall"] = Sync.Wall() };
            foreach ((string k, object? v) in told)
            {
                switch (v)
                {
                    case byte[] bytes:
                        kept["bytes"] = bytes.Length;
                        kept["text"] = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 200));
                        break;
                    case Vehicle:
                        kept[k] = true;
                        break;
                    default:
                        kept[k] = v;
                        break;
                }
            }
            lock (Heard)
            {
                Heard.Add(kept);
                if (Heard.Count > MostHeard) Heard.RemoveAt(0);
            }
        }
        Publish(to, told);
    }

    /// <summary>The last things the mods were told of events, oldest first, for the bridge: a developer's install only.</summary>
    public static List<object?> HeardNow()
    {
        lock (Heard) return [.. Heard];
    }

    /// <summary>
    /// Says an event as a mod would, for the bridge: on the bus, and to this mod's own listener
    /// if the bus does not hand a mod what it published itself. <c>bus</c> in the answer is which.
    /// </summary>
    public static Dictionary<string, object?> Say(Dictionary<string, object?> said)
    {
        Publish(_event, said);
        bool bus = said.ContainsKey("queued");
        if (!bus) OnEvent(said);
        said["bus"] = bus;
        return said;
    }

    private static void OnHello(Dictionary<string, object?> said)
    {
        said["provider"] = true;
        said["session"] = _inSession;
        Log.Info($"{said.GetValueOrDefault("mod") ?? "a mod"} asked whether craft are shared here");
    }

    private static void OnState(Dictionary<string, object?> said)
    {
        if (said.GetValueOrDefault("craft") is not Vehicle craft || said.GetValueOrDefault("channel") is not string channel
            || said.GetValueOrDefault("data") is not byte[] data) return;
        if (channel.Length == 0 || channel.Length > Wire.ChannelChars || data.Length > Wire.ExtraBytes) return;
        if (!Said.TryGetValue(craft, out Dictionary<string, (byte[] Data, double At)>? of)) Said[craft] = of = [];
        of[channel] = (data, Sync.Wall());
    }

    /// <summary>Tells the mods whether this game is in a shared world, when that changes.</summary>
    public static void InSession(bool active)
    {
        if (active == _inSession) return;
        // What mods said in the world this game was in is not said in the next, nor in none. With
        // the flag and under the lock, so one said as this happens is refused or kept, never taken
        // and then emptied out.
        lock (Gate)
        {
            _inSession = active;
            Out.Clear();
            _outBytes = 0;
        }
        _clientWaiting = 0;
        Publish(_session, new() { ["active"] = active });
    }

    /// <summary>Tells the mods which craft are driven in another game, as that changes.</summary>
    public static void Remotes(IReadOnlyCollection<Vehicle> now)
    {
        if (now.Count == 0 && Remote.Count == 0) return;
        foreach (Vehicle was in Remote.Where(v => !now.Contains(v)).ToArray())
        {
            Remote.Remove(was);
            if (World.IsAlive(was)) Publish(_authority, new() { ["craft"] = was, ["remote"] = false });
        }
        foreach (Vehicle craft in now)
        {
            if (Remote.Add(craft)) Publish(_authority, new() { ["craft"] = craft, ["remote"] = true });
        }
        foreach (Vehicle gone in Said.Keys.Where(v => !World.IsAlive(v)).ToArray()) Said.Remove(gone);
    }

    /// <summary>What the mods are saying of a craft of this player's, to go out with its state.</summary>
    public static IEnumerable<(string Channel, byte[] Data)> Of(Vehicle craft)
    {
        if (Said.Count == 0 || !Said.TryGetValue(craft, out Dictionary<string, (byte[] Data, double At)>? of)) yield break;
        double now = Sync.Wall();
        foreach ((string channel, (byte[] data, double at)) in of)
        {
            if (now - at <= SaidForSeconds) yield return (channel, data);
        }
    }

    /// <summary>What another game's mod said of a craft that is remote here, handed to this game's.</summary>
    public static void Arrived(Vehicle craft, string channel, byte[] data) =>
        Publish(_remoteState, new() { ["craft"] = craft, ["channel"] = channel, ["data"] = data });

    // A mod that throws in its handler is StarMap's to catch, and it does; this is for the bus itself.
    private static void Publish(IEventPublisher<Dictionary<string, object?>>? to, Dictionary<string, object?> what)
    {
        try { to?.Publish(what); }
        catch (Exception e) { Log.Warn($"could not tell the other mods: {e.GetBaseException().Message}"); }
    }
}
