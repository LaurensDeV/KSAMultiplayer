using System.Reflection;
using Brutal.ImGuiApi;
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
/// and the pause go through: while connected, the speed is the one that keeps the server's time.
/// <see cref="InputEvents.SimSpeedData.Apply"/> beside them, which is the only one of those that is
/// the player's own word, and so the only one put to the room.</item>
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
/// <item>The constructor of <see cref="WelcomePopup"/>, after, and <see cref="Popup.DrawAll"/>: the
/// Multiplayer window comes before KSA's configuration and its welcome.</item>
/// </list>
///
/// <para>Nothing in any of them may throw: they run inside KSA's own loop.</para>
/// </summary>
internal static class Hooks
{
    private const string HarmonyId = "com.ksamultiplayer";

    private static Harmony? _harmony;
    private static bool _complained;

    /// <summary>
    /// What has to be in place before KSA's configuration at start, which comes before every other
    /// hook StarMap offers: <see cref="Popup.DrawAll"/>, so the Multiplayer window is drawn in its
    /// place. DrawAll and not <c>SelectSystem.DrawUi</c>, which is three lines and as likely to be
    /// inlined into its frame as Celestial.GetLocation was into the editor.
    /// </summary>
    public static void InstallEarly()
    {
        try
        {
            _harmony ??= new Harmony(HarmonyId);
            Patch(AccessTools.Method(typeof(Popup), nameof(Popup.DrawAll)), nameof(BeforePopups),
                  "the Multiplayer window does not come before KSA's configuration at start");
        }
        catch (Exception e)
        {
            Log.Error("could not patch the game's start", e);
        }
    }

    // False leaves KSA's popups undrawn this frame: the configuration, which the window stands in for.
    private static void BeforeEndConsoleBody(Popup __instance)
    {
        try
        {
            if (__instance is ConfigOnStartPopup) Panel.EndOfConfigurationBody();
        }
        catch (Exception e)
        {
            Complain("drawing a way back out of KSA's configuration", e);
        }
    }

    private static void BeforeConfiguration()
    {
        try
        {
            Panel.StartOfConfiguration();
        }
        catch (Exception e)
        {
            Complain("counting KSA's configuration from the top", e);
        }
    }

    // __result is false for a region the player has collapsed, which KSA then does not end.
    private static void AfterBeginRegion(bool __result)
    {
        try
        {
            Panel.RegionBegun(__result);
        }
        catch (Exception e)
        {
            Complain("holding the server's choices still", e);
        }
    }

    private static void BeforeEndRegion()
    {
        try
        {
            Panel.RegionEnding();
        }
        catch (Exception e)
        {
            Complain("letting go of the server's choices", e);
        }
    }

    private static void AfterProgramMenus()
    {
        try
        {
            Panel.DrawMenuBarStatus();
        }
        catch (Exception e)
        {
            Complain("saying in the menu bar that this game is joined", e);
        }
    }

    private static bool BeforePopups()
    {
        try
        {
            return !Panel.InsteadOfConfiguration();
        }
        catch (Exception e)
        {
            Complain("drawing the Multiplayer window at start", e);
            return true;
        }
    }

    public static void Install()
    {
        try
        {
            // Already made when the start window asked for its patches before KSA was up.
            _harmony ??= new Harmony(HarmonyId);
            // Last of whatever is patching these three, because each is a write another mod may also
            // be making and this one has to be the word that stands: a craft somebody else is flying
            // goes where its owner says, and the clock is the server's. Harmony runs a lower priority
            // later. Nothing here returns false, so another mod's prefix still runs either way.
            Patch(typeof(Vehicle).GetMethod(nameof(Vehicle.PrepareWorker), [typeof(SimStep)]), nameof(BeforePrepareWorker),
                  "other players' craft cannot be moved in physics", Priority.Last);
            Patch(AccessTools.Method(typeof(Universe), nameof(Universe.SetSimulationSpeed), [typeof(double), typeof(bool)]), nameof(BeforeSpeed),
                  "time warp is put back a frame late", Priority.Last);
            Patch(AccessTools.Method(typeof(Universe), nameof(Universe.SetSimulationSpeed), [typeof(SimSpeed)]), nameof(BeforeNamedSpeed),
                  "time warp is put back a frame late", Priority.Last);
            // Where a speed the player themselves asked for comes from, and the only place one does:
            // a pick from KSA's own simulation-speed menu, or its console's speed command.
            Patch(AccessTools.Method(typeof(InputEvents.SimSpeedData), nameof(InputEvents.SimSpeedData.Apply)),
                  nameof(BeforePlayerSpeed),
                  "a speed another mod sets is put to the room as though this player had asked for it");
            Patch(AccessTools.Method(typeof(Universe), nameof(Universe.IncreaseSimulationSpeed)), nameof(BeforeFaster),
                  "a warp key steps from this world's speed rather than the shared one");
            Patch(AccessTools.Method(typeof(Universe), nameof(Universe.DecreaseSimulationSpeed)), nameof(BeforeSlower),
                  "a warp key steps from this world's speed rather than the shared one");
            Patch(AccessTools.Method(typeof(Universe), nameof(Universe.ResetSimulationSpeed)), nameof(BeforeNormalSpeed),
                  "the key for normal speed steps from this world's speed rather than the shared one");
            // KSA leaves this empty inside its own menu bar for a mod to draw in. Whether this game is
            // in a shared world, and which one, belongs where a player can see it without opening
            // anything.
            PatchAfter(AccessTools.Method(typeof(Program), nameof(Program.DrawProgramMenusHook)), nameof(AfterProgramMenus),
                  "there is nothing in KSA's menu bar to say this game is in a shared world");
            // What the server has settled is drawn in KSA's configuration but not changed there. The
            // three are found by the order they are drawn in, so the configuration's own first frame
            // is where the counting starts.
            Patch(AccessTools.Method(typeof(ConfigOnStartPopup), "OnDrawUi"), nameof(BeforeConfiguration),
                  "the server's system and game type can be changed in KSA's configuration and then refused");
            PatchAfter(AccessTools.Method(typeof(ImGuiHelper), nameof(ImGuiHelper.BeginRegion),
                                          [typeof(ImString), typeof(ImGuiTreeNodeFlags), typeof(float)]),
                       nameof(AfterBeginRegion),
                  "the server's system and game type can be changed in KSA's configuration and then refused");
            Patch(AccessTools.Method(typeof(ImGuiHelper), nameof(ImGuiHelper.EndRegion)), nameof(BeforeEndRegion),
                  "the server's system and game type can be changed in KSA's configuration and then refused");
            // The foot of a popup's body, which is where a way back out of KSA's configuration goes.
            Patch(AccessTools.Method(typeof(Popup), "EndConsoleBody"), nameof(BeforeEndConsoleBody),
                  "there is no way back out of KSA's configuration once this mod has handed it over");
            Patch(AccessTools.Method(typeof(Universe), nameof(Universe.DestroyVehicleFromEvent)), nameof(BeforeDestroy),
                  "another player's craft can be wrecked in this world alone");
            Patch(AccessTools.Method(typeof(InputEvents.VehicleDestroyData), nameof(InputEvents.VehicleDestroyData.Apply)),
                  nameof(BeforeDelete),
                  "another player's craft can be deleted from the universe manifest in this world alone");
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
            PatchAfter(AccessTools.Method(typeof(VehicleEditingSpace), nameof(VehicleEditingSpace.GenerateNewVehicleId)),
                  nameof(AfterNewVehicleId),
                  "two games in one world launch craft under the same name");
            // The constructor and not Create, which is one line and as likely to be inlined into
            // the loading as Celestial.GetLocation was into the editor.
            PatchAfter(AccessTools.Constructor(typeof(WelcomePopup)), nameof(AfterWelcome),
                  "KSA's welcome is shown over the multiplayer window when the game starts");
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

    /// <summary>
    /// KSA's welcome, put away before it is drawn while the player chooses between a server and
    /// playing alone. A popup goes in KSA's list as it is made and leaves it once inactive, before
    /// the list is next drawn. A player who plays alone is given it then (<see cref="Panel"/>).
    /// </summary>
    private static void AfterWelcome(WelcomePopup __instance)
    {
        try
        {
            if (!Panel.Starting) return;
            __instance.Active = false;
            Panel.WelcomeHeld = true;
        }
        catch (Exception e)
        {
            Complain("holding KSA's welcome", e);
        }
    }

    private static void PatchAfter(MethodBase? target, string postfix, string otherwise)
    {
        if (target is null)
        {
            Log.Warn($"could not patch after a method KSA no longer has: {otherwise}");
            return;
        }
        try
        {
            _harmony!.Patch(target, postfix: new HarmonyMethod(typeof(Hooks).GetMethod(postfix, BindingFlags.NonPublic | BindingFlags.Static)!));
        }
        catch (Exception e)
        {
            // One at a time. A patch that threw here came out in Install's own catch instead, which
            // left every patch after it in the list unmade: one ambiguous overload took most of the
            // mod with it and the log said only that the game could not be patched.
            Log.Error($"could not patch after {target.DeclaringType?.Name}.{target.Name}: {otherwise}", e);
        }
    }

    private static void Patch(MethodInfo? target, string prefix, string otherwise, int priority = Priority.Normal)
    {
        if (target is null)
        {
            Log.Warn($"KSA has moved a method this mod patches: {otherwise}");
            return;
        }
        try
        {
            _harmony!.Patch(target, prefix: new HarmonyMethod(typeof(Hooks).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)!)
            {
                priority = priority,
            });
        }
        catch (Exception e)
        {
            Log.Error($"could not patch {target.DeclaringType?.Name}.{target.Name}: {otherwise}", e);
        }
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

    // Whether the editor is closing on a launch of something new, which is KSA's own condition for
    // making the craft: read before, because it is the field the craft is then put in.
    private static bool _launched;

    private static void BeforeEditorDispose(VehicleEditor __instance)
    {
        _launching = true;
        _launched = __instance.ExistingVehicle is null && __instance.LaunchNewVehicle;
    }

    // The other way a craft is launched: the menu for one already built, which places it the same
    // way the editor does and so wants the same pad chosen for it. Without this, everything sent up
    // from that menu went to the site's own pad however many craft were standing on it.
    private static void BeforeMenuLaunch() => _launching = true;

    private static void AfterMenuLaunch() => _launching = false;

    private static void AfterEditorDispose(VehicleEditor __instance)
    {
        _launching = false;
        if (!_launched) return;
        _launched = false;
        try
        {
            // The craft the editor just made, told to the session now rather than guessed at later:
            // KSA puts it in this field, and the session's own look at the world comes after this.
            if (__instance.ExistingVehicle is { } made) Sync.Made(made);
        }
        catch (Exception e)
        {
            Complain("taking the craft just launched as this game's own", e);
        }
    }

    /// <summary>
    /// The name KSA gives a craft launched from the editor, stepped past names other games have
    /// taken as well as this one's.
    ///
    /// <para>KSA steps N while <c>Universe.CurrentSystem</c> holds <c>NewRocket_N</c> -- this world
    /// only, and worked out when the editor opens rather than when the craft is launched. So two
    /// players building at once both get <c>NewRocket_1</c>, and a player whose editor was open
    /// across somebody else's launch gets a name that player already owns. The server does rename a
    /// craft a game says it made, but by then KSA's own lookup may have refused the duplicate and
    /// left the craft out of the world entirely, which nothing can repair.</para>
    /// </summary>
    private static void AfterNewVehicleId(ref string __result)
    {
        try
        {
            if (Sync.Now != Sync.Stage.Playing) return;
            for (int n = 1; n <= 999; n++)
            {
                string name = $"NewRocket_{n}";
                if (World.Taken(name) || Sync.SomebodyOwns(name)) continue;
                __result = name;
                return;
            }
        }
        catch (Exception e)
        {
            Complain("naming a craft the others have not taken", e);
        }
    }

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

    // The speed stays the one that keeps the server's time: this game's clock is not its own to
    // change while it shares a world. A speed that is not that one is put to the other players
    // instead -- but only when the player asked for it, which is the narrow case and not the wide one.
    //
    // Everything writes this. KSA puts the speed to 1 when the editor closes, when the flight
    // computer finishes, when a space weather alert fires and when a craft is wrecked; it puts it to
    // 0 for every menu, and back again on the way out -- back to whatever this mod last wrote, which
    // is a nudge of a few per cent or, mid catch-up, several times speed. Other mods write it too: a
    // weapons mod holding the world down while a round is in the air writes it every frame of every
    // shot. Read as asks, those came back as this player asking the room to run at 1.08x for
    // closing a menu, or to slow down for firing a gun nobody else could see.
    //
    // So nothing is an ask unless it followed the player's own input event, which is the one path
    // that carries their word: InputEvents.SimSpeedData.Apply, where the pause and a bound speed
    // arrive. The warp keys do not come through here at all -- Increase, Decrease and Reset are
    // taken whole by Step -- so this is the pause, a speed bound to a key, and nothing else.
    private static void BeforeSpeed(ref double simulationSpeed, ref bool alert)
    {
        if (Held() is not { } held) return;
        if (Math.Abs(held - simulationSpeed) > 1e-9)
        {
            if (PlayersOwnWord()) Wanted(simulationSpeed);
            alert = false;
        }
        simulationSpeed = held;
    }

    private static void BeforeNamedSpeed(ref SimSpeed speed)
    {
        if (Held() is not { } held || Math.Abs(held - speed.Value) <= 1e-9) return;
        if (PlayersOwnWord()) Wanted(speed.Value);
        speed = new SimSpeed(held);
    }

    // Set by the player's own input event and taken by the one speed write that follows it. KSA
    // applies the event and sets the speed in the same breath, so there is nothing in between for
    // this to be mistaken for.
    private static bool _playerAsked;

    // Only an event carrying a speed, because only that one reaches a setter: KSA builds this with a
    // speed for a pick from its own simulation-speed menu and for the console's speed command, and
    // with an action alone for the three warp keys, which Step takes whole and which never reach a
    // setter to spend this. Set for one of those and it would still be standing when something else
    // wrote the speed later in the same frame -- a craft wrecked, a menu shut, another mod -- and
    // that write would be read as the player's. Cleared at the end of the frame as well, for the
    // pick that is refused before it reaches a setter.
    private static void BeforePlayerSpeed(ref InputEvents.SimSpeedData __instance) =>
        _playerAsked = __instance.Speed.HasValue;

    private static bool PlayersOwnWord()
    {
        bool asked = _playerAsked;
        _playerAsked = false;
        return asked;
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

    /// <summary>
    /// False spares the craft. The other half of <see cref="BeforeDestroy"/>: wrecking a craft goes
    /// through the destruction event, but deleting one does not.
    ///
    /// <para>The universe manifest's Delete, its Recover and its Clear Debris all put the craft in
    /// <c>InputEvents.VehicleDestroyBuffer</c> -- so a player could take another player's craft out
    /// of their own world from a menu, with the owner still flying it. Nothing went to anybody else,
    /// so it came back within <c>RebuildEverySeconds</c> looking like a glitch.</para>
    ///
    /// <para>The buffer and not <c>Universe.DestroyVehicle</c>, which it ends in: that call is also
    /// how KSA frees breakup slots and expires debris, and how this mod takes a craft away. Refusing
    /// it wholesale would leave a game unable to clean up after a breakup, which is the ceiling
    /// already in the logs.</para>
    ///
    /// <para>A craft of this player's own is theirs to delete, and the sending round sees it has
    /// gone and tells everybody.</para>
    /// </summary>
    private static bool BeforeDelete(ref InputEvents.VehicleDestroyData __instance)
    {
        try
        {
            return __instance.Vehicle is not { } v || !Sync.IsPuppet(v);
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
            // Not from a menu that has stopped this world. The speed itself is already left alone
            // while paused -- see Held -- but a warp key went straight past that and put the
            // question to everybody: a player sat in the escape menu could ask the whole room to run
            // at ten times speed, see nothing of it, and not be running at it themselves until they
            // came out. Taken and ignored, as the keys are at either end of the ladder.
            if (Paused()) return true;
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


    /// <summary>Called once at the end of every frame, from the mod's own frame hook.</summary>
    public static void EndOfFrame() => _playerAsked = false;

    private static double? Held()
    {
        try
        {
            // The editor is the one place this world is not this player's flight: KSA stops it
            // there (Program.OnFrameEditor) and runs it again on the way out, nobody is flying
            // anything meanwhile, and HoldTheClock brings the world back to the shared one when
            // they come out.
            //
            // Everywhere else the world is held to the shared speed even while a menu is open.
            // Escape, the settings and the save lists all stop the world (Program.UpdateMenuPause),
            // and a craft that stops when its pilot opens a menu is a craft that stops for nobody
            // else: the shared clock runs on, so this player comes back behind it, and for as long
            // as they are in there every other game has their craft frozen at one universe time and
            // then lets go of it. One world has one time, and a menu is not a reason to leave it.
            // What this does not do is tell anybody about it -- see BeforeSpeed.
            return Program.IsEditorOpen ? null : Sync.HeldSpeed;
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
