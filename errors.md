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

## 2026-11-06 -- the kittens jittering, and the players called "name 2"

Two things players reported that a review of the whole mod finally explained.

**The jittering kitten.** The speed another player's craft is drawn to its place at is the speed KSA
measures it moving at. `World.Pull` writes `states.Kinematic.VelocityPhys`, and
`PhysicsBubble.BuildKittenLocomotionFacts` reads that very field back as `TangentialSpeedPhys`,
which `KittenLocomotion` uses to pick the walk clip, set its playback rate and decide whether the
kitten has tripped. Over `PullSeconds` of 0.15 s, the correction is the position error times 16:

| offset | correction | what KSA does with it |
| --- | --- | --- |
| 3 cm | 0.2 m/s | crosses `IdleSpeedThreshold`, so the idle and walk clips swap -- and `AnimBlendTime` is also 0.15 s, so the blend never finishes before they swap back |
| 24 cm | 1.6 m/s | the walk clip saturates at `PlaybackRateMax` |
| 1.05 m | 7 m/s | `TumbleSpeedGate`: the kitten trips |
| 5 m | 33 m/s | `SnapM`, above which it is put there instead |

And it holds itself open. A puppet kitten is asked for no movement at all -- the mod writes only the
engine, and KSA zeroes `ThrusterCommandFlags` for a kitten that is not an Mmu -- so the traction
servo brakes whatever the pull writes back towards nothing, about 0.08 m/s a substep. For a few
centimetres the servo wins, the gap never closes, the same correction goes in again next frame, and
the kitten walks on the spot for as long as anybody watches. No packet loss, no clock error and no
warp needed: three centimetres is enough.

Fixed by holding a kitten's correction below the walking threshold and leaving it alone over a wider
gap. Rocket tolerances were being applied to a half-metre animal.

**"averageksp 2" and "MrJeranimo 2".** A game's socket takes a fresh port every time it connects,
and a session is held under its address -- so a reconnect never matched the session it was coming
back to and was admitted beside its own ghost. Named "name 2" for the rest of the session, in the
roster, in the chat and in `admins.json`; refused every claim on its own craft, because the ghost
still answered for the owner; and told that a player of its own name was flying its rocket, with
control taken away every frame. Ten seconds of it, or two minutes when the drop was mid-load, which
is what `reload` leaves behind -- which is exactly the sequence in the write-up above. Fixed by
letting go of a session that has been silent for three seconds when a game joins under its name.

The rest of that review, including what was investigated and found not to be true, is in
`docs/REVIEW-2026-11-06.md`.


## 2026-11-06 -- the catch-up ceiling made a slow game fall behind for ever

Two players on 0.9.1: *"I'm constantly like 200s behind, it's really annoying."* Constant is the
diagnostic word -- a gap from joining is transient by construction, and a gap that sits still or
grows is a broken controller.

It was the ceiling added earlier the same day. `MostCatchUpInHand` read a frame's length against
KSA's step cap and called the ratio headroom, with a floor of one. Three things were wrong with it:

- **A frame's length is not spare capacity.** A game held at 60 Hz by its display measures a frame
  of 16.7 ms whether it spent 2 ms of it working or 16.
- **KSA's per-frame ceiling is not the step cap.** `Universe.GetJobSimStep` caps the advance at
  `max(_maxStepSimSeconds, dtPlayer)`, and `_maxStepSimSeconds` is up to twice the step before it --
  a ramp. `DESIGN.md` records a game running at 8.4x to catch up, which the ceiling said was
  impossible below 252 frames a second.
- **The floor of one was the bug.** At exactly the minimum frame rate the ratio is one, so a world
  ran at precisely the server's rate and the gap was frozen wherever it stood. Below the minimum
  frame rate it was worse: `Asked`'s own ceiling then clamped away the make-up for a long frame, so
  a game at 20 fps needed 1.5x to hold station, was allowed 1.1, and lost 16 seconds of universe a
  minute. A gap of 200 s is twelve minutes of that.

Reverted to the flat ceiling. The `Asked` clamp is fixed as well -- it carries the make-up with it
now -- which was a latent bug of its own, hidden for as long as any gap over three seconds pushed
the asked speed to ten and took the ceiling up with it.

This restores the warp that the write-up above blames for bubble origins 23 s behind the clock. That
is a fair trade and the blame was probably misplaced: a bubble's step is not budgeted and cannot lag
the clock by design, but a step that *fails* does not advance the bubble at all -- and those
sessions were full of `Update task failed`, from this mod building and destroying craft while KSA's
worker walked the vehicle list. The warp multiplied the cost of each failure rather than causing it.
The failures themselves are what the quiet-window work fixed.

If a ceiling of this kind is wanted again, the signal is KSA's own `_achievedSpeedFraction` -- what
share of the asked speed the solver is actually paying for, already read as `World.Achieved` -- and
its floor has to be above one, so a gap is always repaid even on a drowning machine.

## 2026-11-07 -- one player sees parts another player's craft has already shed

Two reports from `mp.ksarmory.com` on 0.10.0: *"Onyx sees craft parts that I don't"*, and *"when I go
onto a ladder on a craft he controls, and he starts it, for me it immediately disappears for a
bit"*. Both logs have it, and both are the same mechanism seen from two sides.

The server heard Onyx describe one craft five times:

```
23:21:39 Onyx says how Laurens death machine is built, 5 KB
23:22:07 Onyx says how Laurens death machine is built, 5 KB -- 2 times now
23:25:30 Onyx says how Laurens death machine is built, 5 KB -- 2 times now
23:25:32 Onyx says how Laurens death machine is built, 5 KB -- 3 times now
23:25:34 Onyx says how Laurens death machine is built, 4 KB -- 4 times now
```

The watching game rebuilt it three times and then stopped:

```
00:21:39 rebuilding Laurens death machine: 5868 bytes became 5879
00:22:07 rebuilding Laurens death machine: 5879 bytes became 5950
00:25:30 rebuilding Laurens death machine: 5869 bytes became 5879
```

Nothing at 23:25:32 or 23:25:34, because `RebuildEverySeconds` is 5 and both fell inside the window
that opened at 23:25:30. The craft lost parts in those two words -- 5 KB became 4 KB -- and the
watching game is still showing the 5,879-byte hull.

**And it never catches up.** The throttle dropped the definition outright, on the reasoning written
into the comment: that this shape "or whatever it has become by then, is built once the wait is
over". That holds only if the craft changes shape *again*, because a definition is only sent on a
change of shape -- and a craft that settles into its final shape inside the window never sends
another. So the divergence is permanent, for the rest of the session, which is what the report says.

Fixed by keeping the word the wait pushed back instead of dropping it, and taking it up when the
window opens. Forgotten on a sweep, on a `Gone`, and when a definition cannot be built here, so the
wait cannot resurrect a craft that has been taken away.

The ladder half is the same rebuild seen from on board: a rebuild destroys the puppet and builds it
again, so a player standing on its ladder watches it go and come back, and is dropped. That part is
not fixed. Rebuilding is how a craft's shape is followed at all, and the craft genuinely is a new
object afterwards -- what would have to change is following a change of shape without tearing the
hull down, which is a larger thing than this.

## 2026-11-08 -- a kitten walks in under a launchpad at Vandenberg and is stuck

Reported from a game, with a screenshot of CCSFS LC-39A beside it: the seven pads this mod rings a
launch site with draw only part of their circular access road, a wedge of it missing on the side
facing the middle pad every time, while the middle pad's road is whole. At VSFB LC-4 the same pads
stand at visibly different heights from one another, and a kitten can walk in under the edge of one
and not get out again.

None of it is a drawing fault. The roads are drawn in full, every frame, for all eight pads: KSA has
no per-object clipping or bounds culling in that path at all, only a horizon test. What is wrong is
the ground.

A launch site is levelled by a `Decal` terrain modifier in `Astronomicals.xml` which holds the
ground at one absolute altitude over a disc, dead flat only within `(1 - SmoothFactor) * Radius` and
feathering back to the planet's own height by `Radius`. The lift is real and it is the planet's own
heightmap it is lifted from -- read out of `Earth_Height.ktx2`, which is uncompressed R16:

| site | flat core | natural | held at | lift |
| --- | --- | --- | --- | --- |
| CCSFS LC-39A | 85.3 m | 11.09 m | 16.97 m | 5.88 m |
| VSFB LC-4 | 67.5 m | 127.62 m | 180 m | 52.38 m |
| Starbase OLM A | 93.0 m | 8.52 m | 17.27 m | 8.75 m |
| GSC ELA-4 | 77.5 m | 21.29 m | 29.11 m | 7.82 m |
| Māhia LC-1A | 93.0 m | 15.95 m | 16.02 m | 0.07 m |

The pad the game shipped fits inside the flat core at three of the five sites -- not at Kourou or
Vandenberg, whose own aprons already run past it. This mod's pads stand 150 m out, on the feather,
at every site. Each one is placed flush with the ground sampled at its own centre and is then a
rigid flat disc 82 m across, so the ground rises above its inward edge and falls away under its
outward one. At the Cape that is 1.57 m of burial and 2.78 m of overhang; at Vandenberg, 5.80 m and
10.53 m, which is the gap the kitten walked into. The differing heights are the same arithmetic: at
150 m out Vandenberg's flatten weight is 0.881, so a ninth of a 52 m hillside still comes through,
and each pad's centre lands on a different part of it.

Shrinking the ring does not fix it. Six pads at 100 m would take the Cape's burial from 1.57 m to
0.10 m and its overhang only from 2.78 m to 2.01 m, and Vandenberg's overhang from 10.53 m to
6.96 m -- the wedge would stop showing in a screenshot and the kitten would still be stuck.

Fixed by widening the site's own decal until its flat core takes the whole complex in, so the ground
under it is dead flat and every pad stands at the same height on it. `docs/DESIGN.md` has the rest,
including why it is done where it is.

**And flat ground showed up a second fault the slope had been hiding.** A pad lays a disc of grass
82 m across the ground around it -- the widest thing it draws, against 62.1 m for the circular road
-- and the pads stood 130 m apart, so those discs had always overlapped by 35 m on every side. While
the inward half of every pad was buried, so were the overlaps. Levelled, the two discs are exactly
coplanar, their depths are interpolated from different model matrices and round against each other
pixel by pixel, and it came back as a patchwork of each showing through the other, worst close up.
Nothing else could be moved: the apron belongs to the static object the ring pads share with the
pad the game shipped, so shrinking it would shrink that one too, and `GroundOffset` is on that same
shared object, so there is no per-pad height to stagger. The pads went from 130 m apart to 169 m,
which leaves four metres of grass between aprons and puts the ring 195 m out. The levelling follows
the ring, so it widened to 303 m on its own.

Both flown at VSFB LC-4, the worst case of the two: all eight pads at one height on flat ground,
every ring road whole, grass between the aprons, nothing to walk in under, and the hillside still
behind the site rather than levelled with it.

## 2026-11-09 -- a rocket launched from the editor stands in one game only

Found by reading rather than by being hit, and the symptom was never named as such: a craft launched
from the vehicle editor is claimed, its name is in everybody's roster and its states are relayed to
everybody, and no other game can build it. Players already in the world see the name flying and no
rocket. A player who joins afterwards is fine, because the world they are handed holds the craft.

`Sync.Made` is called as the editor closes, to say that the craft the editor just made is this
game's own whatever the server makes of its name. It marked the craft in two sets, and only one of
them was wanted:

- `MadeHere` is the one it needed. Two games both launch a `NewRocket_1`, because KSA steps N against
  this world alone; without `MadeHere` the second player's own rocket read as the first player's and
  control was taken off them.
- `Seen` means the craft was in the world this game loaded. Not being in it is the whole of how
  `ClaimFlown` and `Adopt` recognise a craft made here -- one that wants a name of its own and a
  definition the others can build it from.

Marked seen, the craft took the other branch everywhere. `ClaimFlown` claimed it with `made` false,
`Adopt` passed over it, and the only path left into `Owned` pre-set the shape it would be described
at to the shape it already had, so the re-describe never fired either. The craft reached everything
except `Define`.

The claim is the second half of it. A claim with `made` false and a name another player holds is
refused outright by the server, with no rename and nothing sent back -- which is the exact case the
two lines were added to fix. `ClaimFlown` then resent that same refused claim every two seconds for
the rest of the session.

Fixed by marking only `MadeHere`. The launch menu does not go through `Sync.Made` and was never
affected, which is why three releases went out with it.

## 2026-11-09 -- two games come up as one, and the card runs out of memory

`tools/two-games.sh` writes each profile a `settings.toml` with the graphics turned well down, which
is the only reason two copies fit on one card. Both copies came up at 2560x1440 instead, one exactly
on top of the other, so the pair looked like a single game that had been started at full resolution.

The script sets `width`, `height`, `posX` and `posY`, and never set `mode`. A size is only a size
while the game is in a window: the player's own `[display] mode = "Fullscreen"` was copied through
with everything else, and both profiles honoured it. Two full-resolution swapchains and their shadow
maps is the memory the whole settings block exists to stay inside of.

`antialiasing` was wrong in the same place and had never once applied: the script wrote `"None"` and
KSA's `AntiAliasing` is `{Disabled, Cmaa2, MultisampleX2, MultisampleX4, MultisampleX8}`. Both games
had been antialiasing at whatever the player plays at for as long as the script has existed.

What let both hide is that the rewrite used `re.sub(..., count=1)` and never looked at the result. A
key KSA has renamed, or one that was never right, matches nothing, is written nowhere, and the
profile silently runs at the player's own setting. It counts with `re.subn` now and exits naming
every key it could not place, which is checked against the game's own `GameSettings` fields.

## 2026-11-09 -- ending a timewarp stops every game in the room, for minutes

Watched, not reported: Goddchen alone on `mp.ksarmory.com`, time-warping, ended up more than 1000
seconds *ahead* of the server's universe time, and his game then paused for a long time.

**Why it stopped is certain, and is one line.** `ClockFollow.Speed` (`src/Protocol/Clock.cs:135`):

```csharp
if (late < -CatchUpAboveSeconds) return 0.0;
```

More than three seconds ahead of the server, in its own seconds, and a world is held at a standstill
-- no proportional slow-down, no floor, and nothing bounding how long. The gap closes only as the
server's own clock advances, so at 1x a world 1000 s ahead is stopped for about seventeen minutes.
That is the "long time".

The two directions are not symmetric, and that is the shape of the fault. A world that is *behind*
is warped at up to `MostCatchUp = 10` with anything of its own in physics and up to
`MostCatchUpOnRails = 1000` without, so a gap can be opened at a thousand times real time and is
then repaid at one. And the server's clock never follows a player forward once it is known --
`UniverseClock.Set` is called from a world off disk, from the first world handed over, from
`OnLoaded` only while `!Known`, and by `HoldTimeAtTheWorld`, which runs only when nobody is loaded.
So nothing moves the clock a game ahead of the server is measured against.

**How he got ahead is confirmed, and it is not latency, not a lone player and not an overshoot.**
Being a few hundred universe-seconds off the server *during a 1000x warp* is being a fraction of a
real second off, which is healthy and expected -- `Speed` divides by the rate for exactly that
reason, so `late` is in real seconds and a 400-second gap at 1000x reads as 0.4. Ending the warp
drops the server's clock to 1x **without reconciling the gap**, and the same 400 universe-seconds is
now 400 *real* seconds ahead. `late < -3` and the world stops, for as long as the server's 1x clock
takes to close a gap a 1000x warp opened: up to a thousand times longer to undo than to make.

Reproduced on loopback with `two-games.sh --test`, in a minute, on 2026-11-10:

```
A asks for 1000x, B votes yes
  A universe=83500.6  speed=1026  behind_s=+132.6   warp=1000/Running
  B universe=83700.4  speed=1026  behind_s=+129.3   warp=1000/Running     <- 0.13 real seconds. healthy.
A ends the warp
  A universe=101911.8 speed=0     behind_s=-469.4   warp=None/None
  B universe=101810.1 speed=0     behind_s=-367.6   warp=None/None
  ... 24 s later, both still frozen at the same universe time, behind_s closing at 1 s per second
```

A was stopped for about eight minutes and B for about six. Note that it hits **everyone in the
room**, not only whoever asked: Goddchen being alone only meant nobody else was there to report it.
Note also that the two games ended the warp 100 universe-seconds apart, so the size of each player's
stop is whatever their own frame timing happened to leave them with.

**The fix the server already has the parts for.** Every player sends its own `Late` on every ping
and `Losing()` already divides it by the rate to read it in real seconds, so at `EndWarp` the server
knows how far ahead the furthest-ahead player is. Advancing the clock by that lead before dropping
the rate leaves nobody ahead, and is what the room honestly simulated: the games did that time, and
the server's wall-clock-driven time is the thing that is behind. The alternative -- keeping the
server's clock and holding every game at a standstill -- is what happens today.

What that costs, and the reason this is a choice rather than a one-liner: the world's time would
jump forward at the end of every warp, by up to the whole lead. Craft states already timestamped in
the past stay valid, since `Track` keeps a window and interpolates, but anything keyed to the
server's clock being monotonic *and* smooth would see a step. Worth checking `SaveMarginSeconds` and
the definition-pruning in `Prune` against a jump before shipping it.

Worth fixing in passing either way: the mod tells the player nothing useful while this happens.
`Sync.Now`'s line is `Math.Abs(b) > CatchUpAboveSeconds ? $"catching up: {b:F0} s behind"`, so a
player who is ahead reads "catching up: -1000 s behind" over a world that is not moving.
