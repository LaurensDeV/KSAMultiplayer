using System.Reflection;
using HarmonyLib;
using KSA;

namespace KSAMultiplayer;

/// <summary>
/// Where this mod patches the game, all prefixes on public members:
///
/// <list type="bullet">
/// <item><see cref="Vehicle.PrepareWorker"/>, the one window in which a write to a craft's physics
/// state survives the frame: KSA double-buffers it, and anything written from a StarMap hook is
/// overwritten before it is read. Other players' craft are pulled to their states here.</item>
/// <item>Both of <see cref="Universe"/>'s <c>SetSimulationSpeed</c>, which every warp key, auto-warp
/// and the pause go through: while connected, the speed is the one that keeps the server's time.</item>
/// <item><see cref="Universe.DestroyVehicleFromEvent"/>: another player's craft is never destroyed
/// here by a crash this world sees, only when its owner's game says it is gone.</item>
/// <item><see cref="Vehicle.AddVolumetricExhaustInstances"/> and
/// <see cref="Vehicle.UpdatePlumeTrailEmitters"/>, where the game turns a craft's engines into a
/// plume, an exhaust and a light: all three off a number only a physics step leaves behind, so
/// another player's craft is given that number here, in the one place that is read after the last
/// step and before the frame is drawn.</item>
/// <item>The setter of <see cref="Program.ControlledVehicle"/>, which clicking a craft, the map and
/// boarding all go through; <see cref="KittenEva.BoardVehicle"/>, since by the time control is handed
/// over the kitten is already inside; and <see cref="EVADoor.PerformEvaForSeat"/>: a craft another
/// player flies is theirs to fly, and nobody else takes it, climbs into it or empties it.</item>
/// </list>
///
/// <para>Nothing in any of them may throw: they run inside KSA's own loop.</para>
/// </summary>
internal static class Hooks
{
    private const string HarmonyId = "com.ksamultiplayer";

    private static Harmony? _harmony;
    private static bool _complained;

    public static void Install()
    {
        try
        {
            _harmony = new Harmony(HarmonyId);
            Patch(typeof(Vehicle).GetMethod(nameof(Vehicle.PrepareWorker), [typeof(SimStep)]), nameof(BeforePrepareWorker),
                  "other players' craft cannot be moved in physics");
            Patch(AccessTools.Method(typeof(Universe), nameof(Universe.SetSimulationSpeed), [typeof(double), typeof(bool)]), nameof(BeforeSpeed),
                  "time warp is put back a frame late");
            Patch(AccessTools.Method(typeof(Universe), nameof(Universe.SetSimulationSpeed), [typeof(SimSpeed)]), nameof(BeforeNamedSpeed),
                  "time warp is put back a frame late");
            Patch(AccessTools.Method(typeof(Universe), nameof(Universe.IncreaseSimulationSpeed)), nameof(BeforeFaster),
                  "a warp key steps from this world's speed rather than the shared one");
            Patch(AccessTools.Method(typeof(Universe), nameof(Universe.DecreaseSimulationSpeed)), nameof(BeforeSlower),
                  "a warp key steps from this world's speed rather than the shared one");
            Patch(AccessTools.Method(typeof(Universe), nameof(Universe.ResetSimulationSpeed)), nameof(BeforeNormalSpeed),
                  "the key for normal speed steps from this world's speed rather than the shared one");
            Patch(AccessTools.Method(typeof(Universe), nameof(Universe.DestroyVehicleFromEvent)), nameof(BeforeDestroy),
                  "another player's craft can be wrecked in this world alone");
            Patch(AccessTools.Method(typeof(Program), nameof(Program.RefreshVehiclesInFrame)), nameof(BeforeRefreshVehicles),
                  "another player's craft is built while KSA is stepping the world, which fails its physics");
            Patch(AccessTools.Method(typeof(Universe), nameof(Universe.DeserializeSave)), nameof(BeforeDeserializeSave),
                  "a save loaded in this game would have every craft in it claimed and sent to everybody");
            PatchAfter(AccessTools.Method(typeof(VehicleEditor), "SetLocations"), nameof(AfterSetLocations),
                  "the editor offers this mod's extra launchpads as places to launch from");
            PatchAfter(AccessTools.Method(typeof(VehicleLaunchMenu), "SetLocations"), nameof(AfterLaunchMenuLocations),
                  "launching a vehicle already built offers this mod's extra launchpads");
            PatchAfter(AccessTools.Method(typeof(ConfigOnStartPopup), "SetLocations"), nameof(AfterStartLocations),
                  "starting a game offers this mod's extra launchpads");
            Patch(AccessTools.Method(typeof(VehicleEditor), nameof(VehicleEditor.RequestExit)), nameof(BeforeRequestExit),
                  "a launch onto a full site is refused only after the editor has asked about unsaved work");
            Patch(AccessTools.Method(typeof(VehicleEditor), "FinalizeExit"), nameof(BeforeFinalizeExit),
                  "a craft is launched onto a pad somebody is already standing on");
            Patch(AccessTools.Method(typeof(VehicleEditor), nameof(VehicleEditor.Dispose)), nameof(BeforeEditorDispose),
                  "a craft launched from the editor goes to the pad the site was given, taken or not");
            PatchAfter(AccessTools.Method(typeof(VehicleEditor), nameof(VehicleEditor.Dispose)), nameof(AfterEditorDispose),
                  "every location lookup after the editor closes is answered with a launchpad");
            Patch(AccessTools.Method(typeof(Celestial), nameof(Celestial.GetLocation), [typeof(string)]), nameof(BeforeGetLocation),
                  "a craft launched from the editor goes to the pad the site was given, taken or not");
            Patch(AccessTools.Method(typeof(Vehicle), nameof(Vehicle.GetInitialKinematicStateForLocation)), nameof(BeforeInitialState),
                  "a craft launched from the editor goes to the pad the site was given, taken or not");
            Patch(AccessTools.Method(typeof(VehicleLaunchMenu), "LaunchVehicle"), nameof(BeforeMenuLaunch),
                  "a vehicle launched from the launch menu goes to the pad the site was given, taken or not");
            PatchAfter(AccessTools.Method(typeof(VehicleLaunchMenu), "LaunchVehicle"), nameof(AfterMenuLaunch),
                  "every location lookup after the launch menu is answered with a launchpad");
            Patch(AccessTools.PropertySetter(typeof(Program), nameof(Program.ControlledVehicle)), nameof(BeforeControl),
                  "another player's craft can be taken over here");
            Patch(AccessTools.Method(typeof(KittenEva), nameof(KittenEva.BoardVehicle), [typeof(Vehicle), typeof(EVADoor)]), nameof(BeforeBoard),
                  "a kitten can climb into another player's craft");
            // The hatch's own button, so a craft that is somebody else's does not offer a way out of
            // it that will be refused: an offer nobody can take is worse than no offer.
            Patch(AccessTools.Method(typeof(EVADoor), nameof(EVADoor.ShowContextMenu), [typeof(Vehicle)]), nameof(BeforeHatchMenu),
                  "a hatch of another player's craft offers a way out that is then refused");
            Patch(AccessTools.Method(typeof(EVADoor), nameof(EVADoor.PerformEvaForSeat), [typeof(Vehicle), typeof(IVASeat)]), nameof(BeforeEva),
                  "a kitten can be let out of another player's craft");
            // Where every way out meets: the hatch's own EVA button fills the spawn buffer itself
            // rather than going through PerformEvaForSeat, and this is what drains it.
            Patch(AccessTools.Method(typeof(EVADoor), nameof(EVADoor.SpawnEva),
                                     [typeof(Vehicle), typeof(IVASeat), typeof(KittenRosterEntryData), typeof(Camera)]), nameof(BeforeSpawnEva),
                  "a kitten can be let out of another player's craft by its hatch");
            // Two of the three passes the engine visuals are drawn in, since nothing between them
            // touches the state again and either one alone leaves the others with nothing to draw.
            Patch(AccessTools.Method(typeof(Vehicle), nameof(Vehicle.AddVolumetricExhaustInstances),
                                     [typeof(Camera), typeof(VolumetricExhaustRenderer), typeof(double)]), nameof(BeforeExhaust),
                  "other players' engines are not seen firing");
            Patch(AccessTools.Method(typeof(Vehicle), nameof(Vehicle.UpdatePlumeTrailEmitters),
                                     [typeof(VolumetricTrailRenderer)]), nameof(BeforePlumeTrail),
                  "other players' engine plumes are not seen");
        }
        catch (Exception e)
        {
            Log.Error("could not patch the game", e);
        }
    }

    private static void PatchAfter(MethodInfo? target, string postfix, string otherwise)
    {
        if (target is null)
        {
            Log.Warn($"could not patch after a method KSA no longer has: {otherwise}");
            return;
        }
        _harmony!.Patch(target, postfix: new HarmonyMethod(typeof(Hooks).GetMethod(postfix, BindingFlags.NonPublic | BindingFlags.Static)!));
    }

    private static void Patch(MethodInfo? target, string prefix, string otherwise)
    {
        if (target is null)
        {
            Log.Warn($"KSA has moved a method this mod patches: {otherwise}");
            return;
        }
        _harmony!.Patch(target, prefix: new HarmonyMethod(typeof(Hooks).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)!));
    }

    public static void Remove()
    {
        try { _harmony?.UnpatchAll(HarmonyId); }
        catch (Exception e) { Log.Warn($"could not remove the patches: {e.Message}"); }
        _harmony = null;
    }

    // Read, not written, in the same frame: this is the last word before the plume, the exhaust and
    // the light are all three drawn from it.
    private static void BeforeExhaust(Vehicle __instance) => ShowEngines(__instance);

    private static void BeforePlumeTrail(Vehicle __instance) => ShowEngines(__instance);

    private static void ShowEngines(Vehicle v)
    {
        try
        {
            Sync.ShowEngines(v);
        }
        catch (Exception e)
        {
            Complain("showing another player's engines", e);
        }
    }

    // The editor hands a new craft to the world as it closes, and looks its launch site up by name
    // to do it. While that is happening -- and only then -- a site's name is answered with whichever
    // of its pads is free, so a player who picked "CCSFS LC-39A" lands on one of the eight there.
    private static bool _launching;

    // Every way out of the editor that launches goes through RequestExit, the crewless confirmation
    // included, so one refusal here covers the lot. The player is told why by the panel, which draws
    // over the editor for as long as the site is full.
    private static bool BeforeRequestExit(VehicleEditor __instance, bool launchNewVehicle)
    {
        if (!launchNewVehicle) return true;
        if (Pads.Room(__instance)) return true;
        Log.Info("refused a launch: every pad at the chosen site is taken");
        return false;
    }

    // The one place a launch is actually settled. RequestExit is refused above so that a player is
    // told before the editor asks them about unsaved work, but the answer to that question comes
    // back here on its own, and a refusal that only covered RequestExit would let it straight past.
    private static bool BeforeFinalizeExit(VehicleEditor __instance, bool launchNewVehicle)
    {
        if (!launchNewVehicle) return true;
        if (Pads.Room(__instance)) return true;
        Log.Info("refused a launch at the last gate: every pad at the chosen site is taken");
        return false;
    }

    private static void BeforeEditorDispose() => _launching = true;

    // The other way a craft is launched: the menu for one already built, which places it the same
    // way the editor does and so wants the same pad chosen for it. Without this, everything sent up
    // from that menu went to the site's own pad however many craft were standing on it.
    private static void BeforeMenuLaunch() => _launching = true;

    private static void AfterMenuLaunch() => _launching = false;

    private static void AfterEditorDispose() => _launching = false;

    // Where a launched craft is actually put. Celestial.GetLocation is patched as well, but it is
    // one line -- return BodyTemplate.GetLocation(id) -- and the JIT inlines it into the editor's
    // exit, so the prefix on it never ran and every craft landed on the site's own pad. This one
    // builds orbits and is far too big to inline, and it is handed the very numbers that matter.
    private static void BeforeInitialState(Celestial celestial, ref double latitude, ref double longitude)
    {
        if (!_launching) return;
        try
        {
            if (Pads.FreeFor(celestial, latitude, longitude) is not { } free) return;
            double lat = free.Latitude.GetRadians() * 180.0 / Math.PI;
            double lon = free.Longitude.GetRadians() * 180.0 / Math.PI;
            if (Math.Abs(lat - latitude) < 1e-12 && Math.Abs(lon - longitude) < 1e-12) return;
            Log.Info($"launching from {free.Id} instead: the site's own pad is taken");
            (latitude, longitude) = (lat, lon);
        }
        catch (Exception e)
        {
            Complain("choosing a launchpad", e);
        }
    }

    private static bool BeforeGetLocation(Celestial __instance, string id, ref LocationReference? __result)
    {
        if (!_launching) return true;
        try
        {
            // Nothing free: left to KSA, which puts the craft on the site's own pad as it always
            // did. Refusing the launch is the editor's job and is not done here.
            if (Pads.Free(__instance, id) is not { } free) return true;
            __result = free;
            if (!string.Equals(free.Id, id, StringComparison.Ordinal)) Log.Info($"launching from {free.Id}: {id} is taken");
            return false;
        }
        catch (Exception e)
        {
            Complain("choosing a launchpad", e);
            return true;
        }
    }

    // The editor lists every landmark it can find as a place to launch from, so this mod's pads are
    // taken back out of the list it has just built. Out of its list, never out of the world: the
    // celestial's Locations are walked by ConstraintSim.UpdateLaunchPads on the physics worker, and
    // taking pads out of that and putting them back as the editor opened was a list modified under
    // that enumerator -- "Collection was modified", for every craft in the bubble, every time
    // somebody opened the VAB.
    private static void AfterSetLocations(VehicleEditor __instance) => Unlist(__instance);

    private static void AfterLaunchMenuLocations(VehicleLaunchMenu __instance) => Unlist(__instance);

    private static void AfterStartLocations(ConfigOnStartPopup __instance) => Unlist(__instance);

    private static void Unlist(object menu)
    {
        try
        {
            Pads.HideFromEditor(menu);
        }
        catch (Exception e)
        {
            Complain("keeping the extra launchpads out of a menu", e);
        }
    }

    /// <summary>
    /// The frame's quiet window: PrepareFrame has waited for last frame's solver jobs and has not
    /// queued the next, so nothing is walking the world's vehicles and a craft may be added to it.
    /// Every StarMap hook runs later than this, with the worker running, which is why the session
    /// asks for a craft there and it is built here.
    /// </summary>
    private static void BeforeRefreshVehicles()
    {
        try
        {
            // Here rather than in the frame hook, and before the session so a craft placed this
            // frame has every pad to stand on: laying them out adds to the celestial's own list of
            // locations, which ConstraintSim.ResolveLaunchPads walks on the solver from
            // PopulateBepuStatesFromKinematic -- the method in the crash logs. It runs once a
            // launch, so it was only ever a narrow window, but it is the same list that failed the
            // physics of every craft in the bubble when pads were taken out of it.
            Pads.LayOut();
            Sync.Settle();
        }
        catch (Exception e)
        {
            // Thrown on into KSA's own frame, this would take the game down with it.
            Log.Error("putting another player's craft into this world", e);
        }
    }

    /// <summary>
    /// Before a world is deserialized into this game. KSA destroys every vehicle and builds new
    /// objects, so a session that carried on here would claim the whole save as craft of its own:
    /// see <see cref="Sync.WorldLoadedHere"/>, which leaves the shared world instead.
    ///
    /// <para>Patched rather than hooked at the save menu because a world is loaded from several
    /// places -- the menu, the bridge, a tool -- and this is the one they share.</para>
    /// </summary>
    private static void BeforeDeserializeSave()
    {
        try
        {
            Sync.WorldLoadedHere();
        }
        catch (Exception e)
        {
            Log.Error("leaving the shared world for a save loaded here", e);
        }
    }

    private static void BeforePrepareWorker(Vehicle __instance, SimStep simStep)
    {
        try
        {
            Sync.Physics(__instance, simStep.DeltaTime);
        }
        catch (Exception e)
        {
            Complain("moving another player's craft", e);
        }
    }

    // The speed stays the one that keeps the server's time. A speed asked for that is not that one
    // came from the player reaching for a warp key or the pause, and is put to the other players
    // instead: this game's own clock is not its own to change while it shares a world.
    private static void BeforeSpeed(ref double simulationSpeed, ref bool alert)
    {
        if (Held() is not { } held) return;
        if (Math.Abs(held - simulationSpeed) > 1e-9)
        {
            Wanted(simulationSpeed);
            alert = false;
        }
        simulationSpeed = held;
    }

    private static void BeforeNamedSpeed(ref SimSpeed speed)
    {
        if (Held() is not { } held || Math.Abs(held - speed.Value) <= 1e-9) return;
        Wanted(speed.Value);
        speed = new SimSpeed(held);
    }

    private static void Wanted(double speed)
    {
        try
        {
            Sync.WantSpeed(speed);
        }
        catch (Exception e)
        {
            Complain("asking the others for a warp", e);
        }
    }

    private static bool BeforeDestroy(Vehicle vehicle)
    {
        try
        {
            return !Sync.IsPuppet(vehicle);
        }
        catch (Exception e)
        {
            Complain("sparing another player's craft", e);
            return true;
        }
    }

    // False leaves control where it is. A craft another player flies is watched, never taken: without
    // this the player can still point at it and press keys, and what they do to it happens here alone.
    // The warp keys, by which way they point: the speed the game works these out from is this
    // world's, nudged off the round number to hold the server's clock, and a step from there is a
    // step from the wrong rung.
    private static bool BeforeFaster() => !Step(1);

    private static bool BeforeSlower() => !Step(-1);

    private static bool BeforeNormalSpeed() => !Step(0);

    private static bool Step(int direction)
    {
        try
        {
            return Sync.StepSpeed(direction);
        }
        catch (Exception e)
        {
            Complain("taking a warp key", e);
            return false;
        }
    }

    // The camera is already on it by the time this runs: KSA follows first and takes control after,
    // so a refusal leaves the player watching rather than nowhere. Said once a craft, since every
    // click on it comes back through here.
    private static Vehicle? _refused;

    private static bool BeforeControl(Vehicle? value)
    {
        if (!Others(value)) return true;
        if (!ReferenceEquals(_refused, value))
        {
            _refused = value;
            string whose = Sync.OwnerOf(value!) is { Length: > 0 } name ? $"{name}'s" : "another player's";
            Sync.Say($"{value!.Id} is {whose}: watching it, not flying it");
        }
        return false;
    }

    // The hatch's own EVA button never calls PerformEvaForSeat: it queues the spawn straight off, so
    // the refusal has to sit where the queue is emptied, which is here.
    private static bool BeforeSpawnEva(Vehicle vehicle)
    {
        try
        {
            // Taken first, if nobody holds it: an unclaimed craft is nobody's to refuse, so two
            // games looking at one capsule would each send the same kitten out of it.
            if (Sync.TakeBeforeEva(vehicle)) return true;
        }
        catch (Exception e)
        {
            Complain("taking a craft before its crew is sent out", e);
            return true;
        }
        Sync.Say($"{vehicle.Id} is {Sync.OwnerOf(vehicle)}'s: its crew is theirs to send out");
        return false;
    }

    private static bool BeforeBoard(Vehicle host, ref bool __result)
    {
        if (!Others(host)) return true;
        __result = false;
        return false;
    }

    // Shown on a craft this game may not empty? Then not shown. The refusal still stands behind it;
    // this is only so nobody presses a button that was never going to work.
    private static bool BeforeHatchMenu(Vehicle vehicle, ref bool __result)
    {
        if (!Others(vehicle)) return true;
        __result = false;
        return false;
    }

    private static bool BeforeEva(Vehicle vehicle, ref bool __result)
    {
        try
        {
            if (Sync.TakeBeforeEva(vehicle)) return true;
        }
        catch (Exception e)
        {
            Complain("taking a craft before its crew is sent out", e);
            return true;
        }
        __result = false;
        return false;
    }

    private static bool Others(Vehicle? craft)
    {
        try
        {
            return craft is not null && Sync.IsPuppet(craft);
        }
        catch (Exception e)
        {
            Complain("telling another player's craft from this game's", e);
            return false;
        }
    }

    /// <summary>Whether KSA has stopped this player's world for a menu of its own.</summary>
    private static bool Paused() =>
        Program.IsEditorOpen || EscMenu.Open || GameSettings.Show || GameSaves.IsOpen || VehicleSaves.IsOpen;

    private static double? Held()
    {
        try
        {
            // Nothing is held while this player is in a menu. KSA stops the world for its own
            // reasons -- the vehicle editor (Program.OnFrameEditor), and Escape, the settings and
            // the save and vehicle lists (Program.UpdateMenuPause) -- and runs it again on the way
            // out, and this prefix would turn every one of those into a speed put to the whole
            // room. Pressing Escape asked everybody else to stop.
            //
            // None of it is anybody else's business: nobody is in this player's menu, the shared
            // clock runs on without them, and HoldTheClock brings their world back to it when they
            // come out. A player who means to change the speed does it from the world, not from a
            // menu that has stopped it.
            return Paused() ? null : Sync.HeldSpeed;
        }
        catch (Exception e)
        {
            Complain("holding the simulation speed", e);
            return null;
        }
    }

    private static void Complain(string what, Exception e)
    {
        if (_complained) return;
        _complained = true;
        Log.Error($"{what} failed", e);
    }

    // Never called: puts the patched methods in this assembly's metadata, so a KSA change to one is a build error.
    private static void PinTheSignatures(Vehicle v, SimStep step, SimSpeed named, VehicleDestructionEvent e,
                                        KittenEva kitten, EVADoor door, IVASeat seat,
                                        Camera camera, VolumetricExhaustRenderer exhaust, VolumetricTrailRenderer trails)
    {
        v.PrepareWorker(step);
        v.AddVolumetricExhaustInstances(camera, exhaust, 0.0);
        v.UpdatePlumeTrailEmitters(trails);
        Universe.SetSimulationSpeed(1.0, true);
        Universe.SetSimulationSpeed(named);
        Universe.DestroyVehicleFromEvent(v, e);
        Program.ControlledVehicle = v;
        kitten.BoardVehicle(v, door);
        EVADoor.PerformEvaForSeat(v, seat);
    }
}
