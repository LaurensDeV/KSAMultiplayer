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
    private readonly IModMenuService _menu;

    private IDisposable? _menuItem;

    /// <summary>
    /// StarMap builds this with <c>ActivatorUtilities</c> and hands it the services it asks for, which
    /// is why this mod wants StarMap 0.5.3 or later: nothing before 0.5 has any to hand, and 0.5.3 is
    /// the first that runs on KSA 2026.11.10.5801.
    /// </summary>
    public MultiplayerMod(IModMenuService menu) => _menu = menu;

    [StarMapImmediateLoad]
    public void OnImmediateLoad(Mod mod)
    {
        Log.Info($"KSA Multiplayer {typeof(MultiplayerMod).Assembly.GetName().Version} loading (mod id {mod.Id}), protocol {Wire.Version}, KSA {World.KsaBuild}");
        // So a directory's log says which build asked it for a list.
        Listings.Agent = $"KSAMultiplayer/{typeof(MultiplayerMod).Assembly.GetName().Version?.ToString(3) ?? "unknown"}";
        // Here and not with the rest: KSA's configuration at start comes before StarMap's AllModsLoaded.
        Hooks.InstallEarly();
        // Beside them, and for the same reason: the start window those patches draw is up before
        // AllModsLoaded, and a tool that cannot reach the bridge there cannot get the game past it.
        Bridge.Open();
    }

    [StarMapAllModsLoaded]
    public void OnFullyLoaded()
    {
        Hooks.Install();
        // KSA's own Mods menu, where a player looks for a mod's window before they look anywhere else,
        // and the only way back to a window that has been shut: nothing of this mod is drawn over the
        // game once it is.
        try
        {
            _menuItem = _menu.AddModMenuItem("Multiplayer", Panel.Show);
            Log.Info("added Multiplayer to KSA's Mods menu");
        }
        catch (Exception e)
        {
            Log.Warn($"could not add a Mods menu entry: {e.Message}");
        }
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
        // After the session, so a command reads the state this frame left and never half of it. From
        // here on this hook serves the bridge and the start window's does not.
        try
        {
            Bridge.ServeFrame(dtPlayer);
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
        try { Browser.Stop(); } catch (Exception e) { Log.Warn($"could not put the server list down: {e.Message}"); }
        try { Sync.Shutdown(); } catch (Exception e) { Log.Error("leaving the server", e); }
        Bridge.Close();
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
