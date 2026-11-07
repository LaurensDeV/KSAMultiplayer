using KSA;
using StarMap.API;
using StarMap.API.Services;

namespace KSAMultiplayer;

/// <summary>
/// StarMap entry point. The frame hook runs the session; the GUI pass draws the window; the physics
/// window, reached through <see cref="Hooks"/>, moves other players' craft. A fault in a frame is
/// logged and the mod carries on; ten switch it off rather than fill the log.
/// </summary>
[StarMapMod]
public sealed class MultiplayerMod
{
    private const int FaultLimit = 10;
    private int _faults;
    private bool _disabled;
    private Bridge? _bridge;
    private readonly IModMenuService _menu;

    private IDisposable? _menuItem;

    /// <summary>
    /// StarMap builds this with <c>ActivatorUtilities</c> and hands it the services it asks for, which
    /// is why this mod wants StarMap 0.5.2 or later: nothing before 0.5 has any to hand.
    /// </summary>
    public MultiplayerMod(IModMenuService menu) => _menu = menu;

    [StarMapImmediateLoad]
    public void OnImmediateLoad(Mod mod)
    {
        Log.Info($"KSA Multiplayer {typeof(MultiplayerMod).Assembly.GetName().Version} loading (mod id {mod.Id}), protocol {Wire.Version}, KSA {World.KsaBuild}");
    }

    [StarMapAllModsLoaded]
    public void OnFullyLoaded()
    {
        Hooks.Install();
        // KSA's own Mods menu, where a player looks for a mod's window before they look anywhere else.
        // The panel's own MP button is still there for a player who has shut the window.
        try
        {
            _menuItem = _menu.AddModMenuItem("Multiplayer", Panel.Show);
            Log.Info("added Multiplayer to KSA's Mods menu");
        }
        catch (Exception e)
        {
            Log.Warn($"could not add a Mods menu entry: {e.Message}");
        }
        // A developer's install only: the bridge is how the tools drive two games against one
        // server, and a released mod has no business watching a folder for commands.
        if (Build.Developer) _bridge = new Bridge();
        if (_bridge is not null) Log.Info("developer install: the bridge is listening");
    }

    [StarMapAfterOnFrame]
    public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
    {
        if (_disabled) return;
        try
        {
            Sync.Update();
        }
        catch (Exception e)
        {
            Fault("frame", e);
        }
        // Last of all: a speed the player asked for is spent by the write that follows it in the same
        // breath, and anything still standing here was an input event that never reached a setter.
        try
        {
            Hooks.EndOfFrame();
        }
        catch (Exception e)
        {
            Fault("closing the frame", e);
        }
        // After the session, so a command reads the state this frame left and never half of it.
        try
        {
            _bridge?.Update(dtPlayer);
        }
        catch (Exception e)
        {
            Fault("bridge", e);
        }
    }

    [StarMapAfterGui]
    public void OnAfterGui(double dt)
    {
        if (_disabled) return;
        try
        {
            // Not InFlight: an observer flies nothing, and the window, the nametags, the scoreboard
            // and the chat are exactly what it has instead. The window itself is drawn wherever the
            // player is, the main menu included: a server is joined before there is a craft, and a
            // player who has to find flight before they can find the mod does not find the mod.
            Panel.Draw(World.AtACraft);
            // Last, over everything the mod has drawn.
            if (World.AtACraft) Panel.DrawTags();
        }
        catch (Exception e)
        {
            Fault("gui", e);
        }
    }

    [StarMapUnload]
    public void Unload()
    {
        try { _menuItem?.Dispose(); } catch (Exception e) { Log.Warn($"could not take the Mods menu entry away: {e.Message}"); }
        try { Panel.Save(); } catch (Exception e) { Log.Warn($"could not keep the panel's settings: {e.Message}"); }
        try { Sync.Shutdown(); } catch (Exception e) { Log.Error("leaving the server", e); }
        Hooks.Remove();
        Log.Info("unloaded");
        Log.Shutdown();
    }

    private void Fault(string where, Exception e)
    {
        Log.Error($"{where} hook failed", e);
        if (++_faults < FaultLimit) return;
        _disabled = true;
        Log.Warn($"{FaultLimit} faults: KSA Multiplayer is switched off for the rest of the session");
        try { Sync.Disconnect(); } catch (Exception) { }
    }
}
