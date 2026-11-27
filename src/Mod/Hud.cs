using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KSA;

namespace KSAMultiplayer;

/// <summary>
/// KSA's own flight HUD -- its gauges, navball and markers, the mission clock -- for the craft a
/// spectator watches. KSA draws and fills all of it from <c>Program.ControlledVehicle</c> and from
/// nothing else, and a spectator flies nothing, so watching another player's rocket showed no
/// number about it at all.
///
/// <para>Where KSA reads that craft to show something, it is handed <see cref="Craft"/> instead:
/// the craft flown, or while nothing is, the one the camera is on. Where it reads it to act -- a
/// flight-computer mode, the navball's frame, a warp to the next apsis -- the click is refused
/// while the player watches (<see cref="Watching"/>), since the craft shown is another player's,
/// or their own out of sight. The resources, staging and crew panels are left to say nothing: they
/// would show the copy's own tanks and staging, which are not its owner's, and staging edits would
/// act on it.</para>
/// </summary>
internal static class Hud
{
    /// <summary>The craft KSA's HUD shows: the one flown, or the one watched while nothing is.</summary>
    public static Vehicle? Craft() =>
        Program.ControlledVehicle ?? (Program.IsEditorOpen ? null : Sync.Spectated is { IsDisposed: false } v ? v : null);

    /// <summary>Whether the HUD shows a craft its clicks must not reach: one watched, not flown.</summary>
    public static bool Watching => Program.ControlledVehicle is null ? Craft() is not null : Sync.Observed is not null;

    private static readonly MethodInfo? Controlled = AccessTools.PropertyGetter(typeof(Program), nameof(Program.ControlledVehicle));
    private static readonly FieldInfo? Burn = AccessTools.Field(typeof(Program), nameof(Program.ActiveBurn));
    private static readonly MethodInfo Ours = AccessTools.Method(typeof(Hud), nameof(Craft));

    private static Action<Vehicle, double>? _updateGauges;

    public static void Install(Harmony harmony)
    {
        const string shown = "a spectator sees none of KSA's gauges for the craft they watch";
        HarmonyMethod swap = new(AccessTools.Method(typeof(Hud), nameof(SwapControlled)));
        (Type Type, string Name)[] reads =
        [
            (typeof(Program), nameof(Program.UpdateShaderData)),
            (typeof(Program), nameof(Program.ShouldDrawVehicleCanvas)),
            (typeof(Program), "UpdateGauges"),
            (typeof(GaugeCanvas), nameof(GaugeCanvas.IsContextVisible)),
            (typeof(GaugeCanvas), "RegisterOpaqueCoverage"),
            (typeof(GaugeCanvas), nameof(GaugeCanvas.CanvasesRender)),
            (typeof(GaugeButtonBinding), nameof(GaugeButtonBinding.PackData)),
            (typeof(GaugeButtonFlightComputer), nameof(GaugeButtonFlightComputer.IsDisabled)),
            (typeof(GaugeButtonFlightComputer), nameof(GaugeButtonFlightComputer.PackData)),
            (typeof(GaugeButtonNavBallMode), nameof(GaugeButtonNavBallMode.IsDisabled)),
            (typeof(GaugeButtonNavBallMode), nameof(GaugeButtonNavBallMode.PackData)),
            (typeof(GaugeButtonWarpTo), nameof(GaugeButtonWarpTo.IsDisabled)),
            (typeof(GaugeButtonWarpTo), nameof(GaugeButtonWarpTo.PackData)),
        ];
        foreach ((Type type, string name) in reads) Transpile(harmony, AccessTools.DeclaredMethod(type, name), swap, $"{type.Name}.{name}", shown);
        Transpile(harmony, AccessTools.DeclaredPropertyGetter(typeof(GaugeDigitBinding), "SourceMissing"), swap, "GaugeDigitBinding.SourceMissing", shown);
        Transpile(harmony, AccessTools.DeclaredMethod(typeof(Program), "RenderGame"), new HarmonyMethod(AccessTools.Method(typeof(Hud), nameof(SwapTheCanvasGate))),
                  "Program.RenderGame", shown);

        HarmonyMethod refuse = new(AccessTools.Method(typeof(Hud), nameof(BeforeClick)));
        (Type Type, string Name)[] clicks =
        [
            (typeof(GaugeButtonFlightComputer), "OnReleased"),
            (typeof(GaugeButtonFlightComputer), "OnHovered"),
            (typeof(GaugeButtonNavBallMode), "OnReleased"),
            (typeof(GaugeButtonWarpTo), "OnReleased"),
        ];
        foreach ((Type type, string name) in clicks)
        {
            if (AccessTools.DeclaredMethod(type, name) is not { } click)
            {
                Log.Warn($"KSA has moved {type.Name}.{name}: its button acts on the player's own craft while they watch another");
                continue;
            }
            try { harmony.Patch(click, prefix: refuse); }
            catch (Exception e) { Log.Error($"could not patch {type.Name}.{name}", e); }
        }

        try
        {
            if (AccessTools.DeclaredMethod(typeof(Program), "PrepareFrame") is { } prepare)
                harmony.Patch(prepare, postfix: new HarmonyMethod(AccessTools.Method(typeof(Hud), nameof(AfterPrepareFrame))));
            else Log.Warn($"KSA has moved Program.PrepareFrame: {shown}");
            if (AccessTools.DeclaredMethod(typeof(Program), "UpdateGauges") is { } update)
            {
                harmony.Patch(update, postfix: new HarmonyMethod(AccessTools.Method(typeof(Hud), nameof(AfterUpdateGauges))));
                _updateGauges = update.CreateDelegate<Action<Vehicle, double>>();
            }
            else Log.Warn($"KSA has moved Program.UpdateGauges: {shown}");
        }
        catch (Exception e)
        {
            Log.Error("could not patch KSA's gauges for a spectator", e);
        }
    }

    private static void Transpile(Harmony harmony, MethodBase? target, HarmonyMethod transpiler, string what, string otherwise)
    {
        if (target is null)
        {
            Log.Warn($"KSA has moved {what}: {otherwise}");
            return;
        }
        try
        {
            harmony.Patch(target, transpiler: transpiler);
        }
        catch (Exception e)
        {
            Log.Error($"could not patch {what}: {otherwise}", e);
        }
    }

    // Every read of the controlled craft in a method that only shows it.
    private static IEnumerable<CodeInstruction> SwapControlled(IEnumerable<CodeInstruction> code, MethodBase original)
    {
        int swapped = 0;
        foreach (CodeInstruction i in code)
        {
            if (Controlled is not null && i.Calls(Controlled))
            {
                i.operand = Ours;
                swapped++;
            }
            yield return i;
        }
        if (swapped == 0) Log.Warn($"KSA's {original.DeclaringType?.Name}.{original.Name} no longer reads the controlled craft the way this mod looks for");
    }

    // RenderGame draws everything, and reads the controlled craft for the crew seated in it and its
    // ground track as well: only the read that decides whether the gauges are drawn at all, which
    // is the one followed by the burn being edited, is swapped.
    private static IEnumerable<CodeInstruction> SwapTheCanvasGate(IEnumerable<CodeInstruction> code)
    {
        List<CodeInstruction> all = [.. code];
        bool swapped = false;
        for (int n = 0; n + 2 < all.Count; n++)
        {
            if (Controlled is null || Burn is null || !all[n].Calls(Controlled) || !all[n + 1].Branches(out _) || !all[n + 2].LoadsField(Burn)) continue;
            all[n].operand = Ours;
            swapped = true;
            break;
        }
        if (!swapped) Log.Warn("KSA no longer decides whether to draw its gauges the way this mod looks for: a spectator sees none");
        return all;
    }

    // Before the frame's UI, where KSA steps its canvases for a craft it flies.
    private static void AfterPrepareFrame()
    {
        try
        {
            // Not while a burn is being edited, when KSA steps the burn's own canvas itself.
            if (Program.ControlledVehicle is null && (object?)Program.ActiveBurn is null && Craft() is not null) GaugeCanvas.UpdateCanvasesOnFrame();
        }
        catch (Exception e)
        {
            Once("stepping KSA's gauges for the watched craft", e);
        }
    }

    /// <summary>
    /// After KSA's frame, where it fills its gauges for a craft it flies: filled for the watched
    /// one while nothing is flown, and animated as KSA animates them.
    /// </summary>
    public static void AfterFrame(double dt)
    {
        try
        {
            if (Program.ControlledVehicle is not null || (object?)Program.ActiveBurn is not null || Craft() is not { } watched || _updateGauges is null) return;
            _updateGauges(watched, dt);
            GaugeData.AnimateAll(dt * Math.Max(1.0, KSA.Universe.SimulationSpeed));
        }
        catch (Exception e)
        {
            Once("filling KSA's gauges for the watched craft", e);
        }
    }

    // What a copy cannot say of itself: it never burns, so its own tanks, delta-v and thrust are not
    // its owner's. The owner's game sends them (Wire.State), and the digits show those.
    private static void AfterUpdateGauges(Vehicle vehicle)
    {
        try
        {
            if (Sync.OwnerSays(vehicle) is not { Said: var s }) return;
            const UnitMask speeds = UnitMask.Meter | UnitMask.Kilometer | UnitMask.Gigameter | UnitMask.AU | UnitMask.LightYear;
            if (float.IsFinite(s.DeltaV)) Program.DeltaVRoller.PackTarget(Math.Max(0f, s.DeltaV), 1, speeds);
            if (float.IsFinite(s.Propellant)) Program.PropellantMassRoller.SetTarget(DigitRoller.PackValue(Math.Max(0f, s.Propellant) * 1000f, 5, 100000));
            if (float.IsFinite(s.ThrustWeight)) Program.ThrustWeightRatioRoller.SetTarget((int)(Math.Max(0f, s.ThrustWeight) * 10f));
        }
        catch (Exception e)
        {
            Once("showing the owner's delta-v and propellant", e);
        }
    }

    // A click on a gauge acts on the controlled craft: refused while what the gauges show is a
    // craft watched, which is another player's, or the player's own out of sight.
    private static bool BeforeClick() => !Watching;

    private static readonly HashSet<string> Failed = [];

    private static void Once(string what, Exception e)
    {
        if (Failed.Add(what)) Log.Warn($"{what} failed, said once a session: {e.GetBaseException().GetType().Name}: {e.GetBaseException().Message}");
    }
}
