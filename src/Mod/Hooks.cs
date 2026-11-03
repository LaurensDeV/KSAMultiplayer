using System.Reflection;
using HarmonyLib;
using KSA;

namespace KSAMultiplayer;

/// <summary>
/// The three places this mod patches the game, all prefixes on public methods:
///
/// <list type="bullet">
/// <item><see cref="Vehicle.PrepareWorker"/>, the one window in which a write to a craft's physics
/// state survives the frame: KSA double-buffers it, and anything written from a StarMap hook is
/// overwritten before it is read. Other players' craft are pulled to their states here.</item>
/// <item>Both of <see cref="Universe"/>'s <c>SetSimulationSpeed</c>, which every warp key, auto-warp
/// and the pause go through: while connected, the speed is the one that keeps the server's time.</item>
/// <item><see cref="Universe.DestroyVehicleFromEvent"/>: another player's craft is never destroyed
/// here by a crash this world sees, only when its owner's game says it is gone.</item>
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
            Patch(AccessTools.Method(typeof(Universe), nameof(Universe.DestroyVehicleFromEvent)), nameof(BeforeDestroy),
                  "another player's craft can be wrecked in this world alone");
        }
        catch (Exception e)
        {
            Log.Error("could not patch the game", e);
        }
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

    private static void BeforeSpeed(ref double simulationSpeed, ref bool alert)
    {
        if (Held() is not { } held) return;
        if (Math.Abs(held - simulationSpeed) > 1e-9) alert = false;
        simulationSpeed = held;
    }

    private static void BeforeNamedSpeed(ref SimSpeed speed)
    {
        if (Held() is { } held && Math.Abs(held - speed.Value) > 1e-9) speed = new SimSpeed(held);
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

    private static double? Held()
    {
        try { return Sync.HeldSpeed; }
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
    private static void PinTheSignatures(Vehicle v, SimStep step, SimSpeed named, VehicleDestructionEvent e)
    {
        v.PrepareWorker(step);
        Universe.SetSimulationSpeed(1.0, true);
        Universe.SetSimulationSpeed(named);
        Universe.DestroyVehicleFromEvent(v, e);
    }
}
