using KSA;
using StarMap.API;

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

    [StarMapImmediateLoad]
    public void OnImmediateLoad(Mod mod)
    {
        Log.Info($"KSA Multiplayer {typeof(MultiplayerMod).Assembly.GetName().Version} loading (mod id {mod.Id}), protocol {Wire.Version}, KSA {World.KsaBuild}");
    }

    [StarMapAllModsLoaded]
    public void OnFullyLoaded()
    {
        Hooks.Install();
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
            if (World.InFlight) Panel.Draw();
        }
        catch (Exception e)
        {
            Fault("gui", e);
        }
    }

    [StarMapUnload]
    public void Unload()
    {
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
