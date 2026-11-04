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
            Patch(AccessTools.Method(typeof(Universe), nameof(Universe.DestroyVehicleFromEvent)), nameof(BeforeDestroy),
                  "another player's craft can be wrecked in this world alone");
            Patch(AccessTools.PropertySetter(typeof(Program), nameof(Program.ControlledVehicle)), nameof(BeforeControl),
                  "another player's craft can be taken over here");
            Patch(AccessTools.Method(typeof(KittenEva), nameof(KittenEva.BoardVehicle), [typeof(Vehicle), typeof(EVADoor)]), nameof(BeforeBoard),
                  "a kitten can climb into another player's craft");
            Patch(AccessTools.Method(typeof(EVADoor), nameof(EVADoor.PerformEvaForSeat), [typeof(Vehicle), typeof(IVASeat)]), nameof(BeforeEva),
                  "a kitten can be let out of another player's craft");
            // Where every way out meets: the hatch's own EVA button fills the spawn buffer itself
            // rather than going through PerformEvaForSeat, and this is what drains it.
            Patch(AccessTools.Method(typeof(EVADoor), nameof(EVADoor.SpawnEva),
                                     [typeof(Vehicle), typeof(IVASeat), typeof(KittenRosterEntryData), typeof(Camera)]), nameof(BeforeSpawnEva),
                  "a kitten can be let out of another player's craft by its hatch");
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
        if (!Others(vehicle)) return true;
        Sync.Say($"{vehicle.Id} is {Sync.OwnerOf(vehicle)}'s: its crew is theirs to send out");
        return false;
    }

    private static bool BeforeBoard(Vehicle host, ref bool __result)
    {
        if (!Others(host)) return true;
        __result = false;
        return false;
    }

    private static bool BeforeEva(Vehicle vehicle, ref bool __result)
    {
        if (!Others(vehicle)) return true;
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
    private static void PinTheSignatures(Vehicle v, SimStep step, SimSpeed named, VehicleDestructionEvent e,
                                        KittenEva kitten, EVADoor door, IVASeat seat)
    {
        v.PrepareWorker(step);
        Universe.SetSimulationSpeed(1.0, true);
        Universe.SetSimulationSpeed(named);
        Universe.DestroyVehicleFromEvent(v, e);
        Program.ControlledVehicle = v;
        kitten.BoardVehicle(v, door);
        EVADoor.PerformEvaForSeat(v, seat);
    }
}
