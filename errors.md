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

## 2026-11-05 -- three players' crash logs from mp.ksarmory.com on 0.5.0

Seven unhandled exceptions across three logs, every one of them inside
`VehicleUpdateTask.ContactIsland.Execute` -> `PhysicsBubble`:

- six `InvalidOperationException: Collection was modified` out of
  `PopulateBepuStatesFromKinematic` and `FullPhysicsConstrainedStep`
- one `ArgumentOutOfRangeException: Leaf index must be a valid index in the tree's leaf array`
  out of `BepuPhysics.Trees.Tree.RemoveAt`, reached through `ConstraintSim.RemoveTerrain` ->
  `Statics.Remove`

Nothing else: no other exception type, no error or warning of any other kind, and only `Core` and
`KSAMultiplayer` loaded in all three.

The leaf-index one is new as a signature but reads as the same fault one step downstream: a
vehicle taken out from under a bubble mid-step leaves its terrain statics holding handles that no
longer point anywhere, and the next `RemoveTerrain` walks off the end of Bepu's tree. It is in the
same call stack as the other six.

So: one bug, not four. 0.5.1 moves the removals into the frame's quiet window alongside the
additions, which is the fix these point at -- still to be confirmed by a session that would
otherwise have produced them.

## 2026-11-05 -- "Attempted to update vehicle ... with origin at time" is two faults, not one

`PhysicsStates.TryUpdateKinematicOrigin` refuses any origin that moves backwards in time, logs this
line, and leaves the craft where it was. Two different things reach it.

**Placing a craft.** `PlaceAnalytic` built the orbit at the state's own time, and a state is
interpolated `ClockFollow.DelaySeconds` (0.15 s) behind this game's clock, so the epoch was always
in the past. A craft whose origin had been carried forward -- by a bubble it had just left -- then
stood still for about nine frames and jumped when the delay was made up. That is the jitter, and
0.5.1 fixed it by carrying the state forward to the craft's own origin time.

**Joining a bubble.** `Vehicle.AddToBubble` hands the craft the bubble's origin, and
`BubbleOrigin.Time` is simply the state time it was made from. A craft whose own origin is ahead of
the bubble's is refused, so it is never rebased into the bubble's frame and its position is read
against the wrong origin. Still happening on 0.6.1, by 0.26-0.38 s, a whole bubble of fragments at
once; it was 2.7 s on 0.5.0, which is likelier to be the clock being further out that session than
anything the fix did.

The second one is the catch-up warp, which the log below works out: the bubble's origin falls
behind the universe clock because the mod is running the clock at up to ten times speed, so a craft
placed at the clock's time is in the bubble's future. Nothing is wrong with the epoch.

## 2026-11-05 -- everybody crashed at once on 0.7.0

The server's side, in order: a debris storm with Laurens and Onyx each describing the *same*
fragments of a third player's rocket (`NewRocket_2_10_2_3_1`, `NewRocket_2_12_1_1_1_1`); a sweep
that took 96 craft; then `reload` handing a 16 KB world to four players at once; then
`averageksp 2` and `MrJeranimo 2` joining, the " 2" meaning the server had not yet let go of the
sessions they had just lost.

One player's log: 117 destructions of 62 distinct craft, one of them 27 times; 113 refused origin
updates; four world loads, and the log stops in the middle of the fourth with no exception.

So the cascade is not one machine. The engine of it is that a game treats the wreckage of another
player's craft as craft of its own: the copy explodes locally, `Adopt` finds fragments nothing has
seen, claims them, and describes them to everybody, who build them and explode them in turn. The
server shows two players naming the same fragments, which is that loop seen from outside.

Rate limits bound how fast it runs and do not stop it. What has to change is which craft a game is
allowed to call its own, and that wants a reproduction here -- two games, a many-part rocket, a
deliberate breakup -- rather than another guess against a live server.

## 2026-11-05 -- the 20-second origin gaps are the mod warping the clock to catch up

A fourth log, Beik's, on 0.7.0 at `mp.ksarmory.com`: 331 errors, no exception anywhere, and the
line that explains the other three.

```
ERROR Called SnapToLeader with body time 169992,129844643 but origin time 169969,068541585
```

Body time is the world's universe time; origin time is the bubble's. The bubble is **23 seconds
behind the world it is in**. Measured across the session, the refused origins are not a drift that
grows -- they are a spike that comes and goes with the breakups:

| minute | refusals | median gap | worst |
| --- | --- | --- | --- |
| 22:23 | 16 | 2.5 s | 2.8 s |
| 22:25 | 242 | 8.2 s | 19.9 s |
| 22:26 | 22 | 0.4 s | 2.5 s |
| 22:34 | 41 | 21.0 s | 24.9 s |
| 22:35 | 6 | 0.7 s | 2.4 s |

Quiet minutes sit under a second. The two minutes with a hundred-odd fragments in the air sit at
eight and twenty-one.

**It is the catch-up warp.** `ClockFollow.Speed` runs a world that is more than
`CatchUpAboveSeconds` (3 s) behind the server at up to `MostCatchUp` (10) times speed, and
`Sync.cs` applies that whenever anything of this game's own is in physics. A breakup makes frames
long, long frames fall behind the server, and the mod answers by running the world ten times
faster. Universe time then advances ten times a second while a bubble can step at most
`World.StepCap` of it a frame, so the bubble's origin falls tens of seconds behind the clock, and
every craft placed at the clock's time is refused by `TryUpdateKinematicOrigin` as being in the
future. That is the whole of the "joining a bubble" fault left open above: nothing is wrong with
the epoch the mod chooses, the clock it chooses it against has been warped out from under the
bubbles.

And it closes a loop. The breakup slows the game; the slow game falls behind; the mod warps to
catch up; the warp breaks the bubbles' origins so craft are read against the wrong frame, put in
the wrong place, and collide; the collisions make more breakup. Catching up by running faster can
only work where there is headroom, and the one moment it is reached for is the moment there is
none.

The same log confirms the rebuild loop from the outside, in KSA's own words:

```
22:34:52.738  WARN Vehicle 'superheavystock' destroyed - 138 parts exceeded their crash tolerance at once
22:34:53.098  WARN Vehicle 'superheavystock' destroyed - 127 parts exceeded their crash tolerance at once
22:34:53.260  WARN Vehicle 'superheavystock' destroyed - 123 parts exceeded their crash tolerance at once
22:34:53.431  WARN Vehicle 'superheavystock' destroyed - 135 parts exceeded their crash tolerance at once
```

One name, four destructions in 0.7 s, a different part count each time: built, exploded, built
again from the owner's next word, exploded again. `Octo Smoke Monster` did it twice at 741 and 681
parts. `NewRocket_3_13` was destroyed 17 times. 88 destructions of 36 distinct craft. The build
limit that counts every build rather than only rebuilds is not in 0.7.0, and this is what it is for.

KSA also has a ceiling of its own, which a shared world reaches far sooner than one player can:

```
WARN ExplosionSystem: more than 32 destruction requests this frame, keeping the strongest
WARN Freed only 27 of 33 slots - 'NewRocket_1' is breaking up past 30 vehicles
```

Thirty vehicles a breakup and thirty-two destructions a frame. One player's rocket coming apart
does not reach that; four players' copies of four players' rockets do, and past it the engine
starts dropping pieces -- so the two games stop agreeing on what exists even before the mod does
anything wrong.

The log ends as the others do: `started loading save game 'Multiplayer'` at 22:35:56, nothing
after it, and the crash-dump monitor gone with exit code 143 thirty seconds later. No exception.
The game did not throw; it stopped, mid world load, while being handed a world.

## 2026-11-05 -- the cascade reproduced on this machine, and it is not the explosions

`tools/breakup.sh` stages it: A takes the GOLF save's 89-part `Rocket`, lights it and stages it
until it comes apart, B only watches, and then both worlds are read as each game has them. The
first run:

```
  A    22 craft --   17 its own,    1 other players',    4 neither
      MADE UP, the other game has never heard of them     0   none
  B    37 craft --    1 its own,    8 other players',   28 neither
      MADE UP, the other game has never heard of them    15   Rocket_2_1, Rocket_2_11, ...
```

B's world holds fifteen to twenty craft **A has never heard of** -- `Rocket_2_5` through
`Rocket_2_11`, `Rocket_2_3_10` through `Rocket_2_3_15`, `Rocket_3`, `Rocket_4` -- and twenty-eight
of its thirty-seven craft are orphans: neither its own nor anybody else's, so nothing drives them,
no owner's sweep reaches them, and they sit in physics waiting to hit something. A, which owns the
rocket, has no such craft at all. The asymmetry is the whole thing.

**Nothing exploded.** No destruction in either log, no refused origin worth the name, no physics
step failed. The divergence does not need a breakup: it comes from *rebuilding*. Staged once more
with nothing else happening, B's builds went from 15 to 18 and its made-up craft from 15 to 16 --
they accumulate per build, not per impact.

It was neither a build spawning them nor `Adopt` claiming them. `Adopt` was right: the shed pieces
are nearer a puppet than to anything of B's own, so it refused to claim them -- and then **left them
standing**, marked seen and owned by nobody. Nothing drives them, the owner's sweep does not name
them, and they are in physics. That is the whole of the leak, and it is two lines of omission.

The other half is the same rule read the other way. Distance decides whose a new craft is, and where
it goes the wrong way -- two players at one launch site, which is how everybody plays -- the pieces
of their rocket *are* nearer this player's craft, so this game claims them and sends them to
everybody as craft of its own. That is the server log's two players naming the same fragments.

Distance cannot tell the two apart, because both are next to somebody. The name can: KSA names a
piece after the stack it left, so `Rocket_2_5` came off `Rocket_2`, which came off `Rocket`. Walk
that up and the first name anybody owns says whose the piece is. `Wire.ShedFrom` does the walking;
`Adopt` takes away what belongs to somebody else and keeps claiming the rest.

With the fix in, the same run: B took away 34 pieces its copy of Alpha's rocket had shed, A took
away none, and the two worlds came out holding the same 25 craft with nothing made up on either
side. The four the GOLF save leaves unowned are the only craft neither game claims, which is right.

The rig's two numbers are the before and after: craft a game made up, and craft it neither owns nor
drives. 15 and 28 before, 0 and 4 after.
