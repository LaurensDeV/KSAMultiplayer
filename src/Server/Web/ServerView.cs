namespace KSAMultiplayer.Server;

/// <summary>Something that happened, for the web viewer to push as it happens rather than at the next view.</summary>
/// <param name="Kind">chat, joined, left, gone, dock or rename.</param>
/// <param name="Who">The player it is about or by; empty for the server itself.</param>
public sealed record ViewEvent(string Kind, string Who, string Text);

/// <summary>
/// What the web viewer shows of a server, all of it, as of one tick: built on the tick thread and
/// never changed after, so the web's threads may read it while the next is being built. Everything
/// in it is already public to anybody in the world; nothing from a hello that is not -- no tokens,
/// cookies, passwords or addresses -- is ever put in it.
/// </summary>
public sealed record ServerView(long Seq, double WallTime, ViewServer Server, ViewClock Clock, ViewWarp Warp,
                                ViewPlayer[] Players, ViewCraft[] Crafts, string? BodiesEtag);

public sealed record ViewServer(string Name, string System, string KsaBuild, string[] Mods, int Players, int MaxPlayers,
                                bool HasWorld, bool Password, int Protocol);

/// <summary>Universe time as of <paramref name="ServerTime"/> (Unix seconds): a reader runs it on at <paramref name="Rate"/>.</summary>
public sealed record ViewClock(double Universe, double Rate, double ServerTime, bool Known);

public sealed record ViewWarp(string Phase, string Who, double Speed, string[] Waiting);

public sealed record ViewPlayer(byte Id, string Name, bool Loaded, string Controlling, float Rtt, double Can, string Watching);

/// <summary>A craft where its owner last said it was, in both of the frames the wire carries.</summary>
public sealed record ViewCraft(string Key, byte Owner, string OwnerName, string Body, uint Sequence, double Universe,
                               Vec3 PosCcf, Vec3 VelCcf, Vec3 PosCci, Vec3 VelCci, Quat RotCcf, Vec3 Spin, float Throttle,
                               ViewFlags Flags, ushort Parts);

public sealed record ViewFlags(bool Rails, bool Landed, bool EngineOn, bool Lights, bool Controlled, bool Near);

public sealed partial class GameServer
{
    /// <summary>
    /// Told of each <see cref="ViewEvent"/> on the tick thread as it happens. Null while there is no
    /// viewer, which costs a server without one nothing. Whatever is set here must not block.
    /// </summary>
    public Action<ViewEvent>? Viewed { get; set; }

    private long _viewSeq;

    private void View(ViewEvent e) => Viewed?.Invoke(e);

    /// <summary>The view as of now. On the tick thread only, as everything else here is.</summary>
    /// <param name="wall">The time of day in Unix seconds, which is the clock a browser has too.</param>
    public ServerView BuildView(double now, double wall, string? bodiesEtag = null)
    {
        ViewCraft[] crafts =
        [
            .. _crafts.Where(c => c.Value.Said is not null).OrderBy(c => c.Key, StringComparer.Ordinal).Select(c =>
            {
                Wire.State s = c.Value.Said!;
                Wire.StateFlags f = s.Flags;
                return new ViewCraft(c.Key, c.Value.Owner, c.Value.Owner == 0 ? "" : Named(c.Value.Owner), s.Body, s.Sequence, s.Universe,
                                     s.PositionCcf, s.VelocityCcf, s.PositionCci, s.VelocityCci, s.Body2Ccf, s.Spin, s.Throttle,
                                     new ViewFlags((f & Wire.StateFlags.Rails) != 0, (f & Wire.StateFlags.Landed) != 0,
                                                   (f & Wire.StateFlags.EngineOn) != 0, (f & Wire.StateFlags.Lights) != 0,
                                                   (f & Wire.StateFlags.Controlled) != 0, (f & Wire.StateFlags.Near) != 0),
                                     s.Parts);
            }),
        ];
        Wire.Warp warp = WarpState();
        return new ServerView(
            ++_viewSeq, wall,
            new ViewServer(Options.Name, _worldSystem, RequiredBuild, WorldMods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                           _players.Count, Options.MaxPlayers, _world is not null, Options.Password.Length > 0, Wire.Version),
            new ViewClock(UniverseClock.Now(now), UniverseClock.Rate, wall, UniverseClock.Known),
            new ViewWarp(warp.Phase.ToString(), warp.Phase == Wire.WarpPhase.None ? "" : Named(warp.Who), warp.Speed,
                         [.. warp.Waiting.Select(Named)]),
            [.. _players.Values.OrderBy(p => p.Id).Select(p => new ViewPlayer(p.Id, p.Name, p.Loaded, p.Controlling, p.Rtt, p.Can, p.Watching))],
            crafts,
            bodiesEtag);
    }
}
