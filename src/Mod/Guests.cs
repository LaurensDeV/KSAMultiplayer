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
/// </list>
/// </summary>
internal static class Guests
{
    public const string Hello = "craftsync.hello", Session = "craftsync.session", Authority = "craftsync.authority",
                        State = "craftsync.state", RemoteState = "craftsync.state.remote";

    // How long a mod's word about a craft goes on being sent with nothing new said.
    private const double SaidForSeconds = 1.0;

    private static IEventPublisher<Dictionary<string, object?>>? _session, _authority, _remoteState;
    private static readonly List<IDisposable> Listening = [];
    private static readonly HashSet<Vehicle> Remote = [];
    private static readonly Dictionary<Vehicle, Dictionary<string, (byte[] Data, double At)>> Said = [];
    private static bool _inSession;

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
        _inSession = active;
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
