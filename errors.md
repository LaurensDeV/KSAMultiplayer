# Errors seen while testing

What was posted or hit while running the game, what it turned out to be, and what was done. Kept so
the same dialog is recognised the second time rather than investigated again.

## 2026-11-05 -- `0xe0434352` dialog from StarMap.exe, second game only

> The exception unknown software exception (0xe0434352) occurred in the application at location
> 0x00007FFE240F408A.

`0xE0434352` is the CLR's code for an unhandled managed exception, so the dialog says nothing about
which one. The log named it:

```
Unhandled exception System.Reflection.TargetInvocationException
 ---> System.OutOfMemoryException
   at KSA.Rendering.Particles.ScreenspaceParticleRenderer`2..ctor
   at KSA.Rendering.Particles.ParticleSystem`2..ctor(Int32 emitterCount, Int32 maxParticlesPerEmitter)
   at KSA.Program..ctor
```

**Not the mod.** The whole stack is KSA's own startup, building its particle system before the mod
is anywhere near it; the mod's log for that profile has three lines and ends at `unloaded` as the
process dies. This machine cannot hold two copies of the game at once -- the same limit that gave
`Vma Failed to create image ... ErrorOutOfDeviceMemory` and a `PlanetRenderer..ctor`
NullReferenceException on earlier runs, each time in the second game.

Where to look: `<profile>/Logs/KittenSpaceAgency.*.abnormal-exit.log`, and
`<profile>/Logs/KSAMultiplayer.log` to rule the mod in or out.

What to do: run one game. Anything that needs two players on this machine has to be driven a piece
at a time, or over loopback by `./tools/test.sh`, which needs no game at all.

## 2026-11-05 -- `NativeList<T>.Finalize()` NullReferenceException on a desktop instance

> Unhandled exception System.NullReferenceException
>    at Brutal.Collections.NativeList`1.Dispose()
>    at Brutal.Collections.NativeList`1.Finalize()
>    at System.GC.RunFinalizers().

Borea instance `49ef9418`, KSA 2026.10.25.5707, the v0.4.0 mod from the release zip, installed
correctly (`mods/KSAMultiplayer/{KSAMultiplayer.dll,mod.toml}`, enabled in `manifest.toml`).

**A symptom, not the fault.** `GC.RunFinalizers` is the teardown pass: a finalizer ran on a
`NativeList` whose native buffer was already gone and dereferenced null. In the second of the two
runs the same log holds the thing that actually killed it, after the finalizer line:

```
System.NullReferenceException
   at KSA.PlanetRenderer..ctor(Renderer, IRenderPassInfo, Int32, GpuTextureSystem, CloudShadowsRenderer)
   at KSA.Program..ctor
```

The renderer failed to build, the process tore down, and the finalizer threw on the way out. The
first run had got as far as loading every system and acquiring fullscreen exclusive, so it died
leaving rather than starting.

**Not shown to be the mod, not cleared either.** The mod references nothing in `Brutal.Collections`
and patches `Vehicle` and GUI methods that do not exist yet when `KSA.Program..ctor` runs. Against
that: Harmony installs a MonoMod JIT hook at mod load, and a two-games run on this machine produced
an `OutOfMemoryException` whose stack ran through `MonoMod.Core...CompileMethodHook` in the same
renderer-construction region.

The same instance also crashed five times on 2026-11-04, all inside KSA's own physics
(`PhysicsBubble.PopulateBepuStatesFromKinematic`, Bepu's `ConstraintRemover`, a duplicate key in
`ConstraintSim.AddVehicle`), so it is not a machine that only crashes with this mod.

Both of the runs that failed were 2560x1440 **fullscreen exclusive** with HDR offered and the
swapchain rebuilt twice, which is the other candidate.

To settle it: `enabled = false` for `KSAMultiplayer` in that instance's `manifest.toml`, then launch
three or four times. Still crashing means KSA or the display path; never crashing means look here.

## 2026-11-05 -- "Update task failed for vehicle(s)" during a three-player session

> System.InvalidOperationException: Collection was modified; enumeration operation may not execute.
>    at System.Collections.Generic.List`1.Enumerator.MoveNext()
>    at KSA.PhysicsBubble.PopulateBepuStatesFromKinematic(Double terrainLeadTime, Double subStepDt)
>    at KSA.PhysicsBubble.RunStep()
>    at KSA.VehicleUpdateTask.ContactIsland.Execute(VehicleUpdateTask task, ContactIsland& island)

and, alternating with it:

> System.ArgumentOutOfRangeException: Index was out of range ... (Parameter 'index')
>    at KSA.IBubbleJob`1.KSA.Concurrency.IBatchJob<TPayload,KSA.PhysicsBubble>.Execute(...)

Thirty-odd craft named in each failure, repeating every second or two, on `mp.ksarmory.com` with
three players and two rockets coming apart.

**This one looks like the mod.** `KSA-FRAME-ORDER.md` says the worker that
`ExecuteNextVehicleSolvers` queues is *running* while every mod hook runs, and the mod builds and
destroys craft from `[StarMapAfterOnFrame]`: each puppet built adds a vehicle, each one swept or
gone removes one, and a bubble mid-step is walking the very list that changes. The doc records the
same shape of failure for staging a craft from a hook, and says a stage queued for a
`Vehicle.PrepareWorker` prefix cannot race it -- which is where building probably belongs too.

Two earlier crashes fit: `ConstraintSim.AddVehicle` throwing on a duplicate key (a vehicle added
twice) and `error registering duplicate 'Astronomical' id 'Keworth'`.

Not proven, and KSA catches these rather than dying of them -- but a physics step failing for
thirty craft a second is not a session anybody can fly in.

## 2026-11-05 -- adding terrain modifiers at runtime: don't

Giving the extra launchpads the same terrain decal the real site has -- which is the only way to
stop the hillside cutting their aprons -- was tried by appending a `DecalModifierReference` to
`BodyTemplate.TerrainReference.ProceduralModifiers.Modifiers` and rebuilding the sorted list with
the public `ProcessModifiers()`. It was done from a prefix on `Astronomical.SetupModifierRenderData`,
before a celestial's modifier data is made and before `PlanetRenderer` sizes its buffer from
`TerrainModifiersRenderData.NumModifiers` -- which looked like the one safe window.

It is not. A second consumer sizes itself from the same count:

```
System.ArgumentOutOfRangeException: Index was out of range ... (Parameter 'index')
   at System.Collections.Generic.List`1.get_Item(Int32 index)
   at KSA.GroundClutterRenderer.GenerateBatch(..., Int32 numModifiers, ...)
   at KSA.PlanetRenderer.OnPreRender(...)
```

The game died on the first rendered frame after a world load, every time.

The modifier count is read in at least three places at three different times -- the per-celestial
render data, the planet renderer's buffer, and the ground clutter renderer -- and there is no window
that is before all of them. Reverted. The extra pads stand on the ground as they find it, and the
far ones have their aprons cut where it is not flat. That is the cost of not crashing.
