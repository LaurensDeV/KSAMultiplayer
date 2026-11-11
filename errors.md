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

**How the gap gets there: the ending makes it.** This took two goes to get right, and the first was
wrong in a way only a game could show.

Being a few hundred universe-seconds off the server *during* a 1000x warp would be a fraction of a
real second, which is healthy -- `Speed` divides by the rate for exactly that reason. But that is
not even what happens. Measured mid-warp, both games were a hundred universe-seconds **behind**.
The lead appears only at the ending: a game cannot learn the warp has stopped until the word
reaches it, and it runs at the old speed until then, so the round trip plus a frame at 1000x is
several hundred universe-seconds of overshoot. Then `ClockFollow.Speed` reads that same gap in
seconds of the rate now running -- 0.4 s at 1000x, 400 s at 1x -- and 400 is past the standstill at
three. The world stops until a 1x clock has closed a gap a 1000x warp opened.

Reproduced on loopback with `two-games.sh --test`, in a minute, on 2026-11-10:

```
A asks for 1000x, B votes yes
  A universe=84750.0  speed=1019  behind_s=+94.4    warp=1000/Running   <- behind, not ahead
  B universe=84832.6  speed=1023  behind_s=+113.8   warp=1000/Running
A ends the warp
  A universe=85496.1  speed=0     behind_s=-401.3   warp=None/None      <- the ending made this
  B universe=85475.5  speed=0     behind_s=-380.4   warp=None/None
  ... both frozen at the same universe time, behind_s closing at 1 s per second
```

A was stopped for about eight minutes and B for about six, confirmed at one second per second over
200 s with the universe time not moving at all. **The warp itself had run for about half a minute.**
Thirty seconds of warp bought eight minutes of a game that would not run. It hits **everyone in the
room**, not only whoever asked: Goddchen being alone only meant nobody else was there to report it.

**Fixed** by `Server.FollowTheRoom`: where every game says it is further past the clock than a nudge
can close, the clock keeps up, checked twice a second. It moves to the **least**-ahead game, so it
passes nobody and no game is ever asked to rewind, and one game's runaway clock cannot drag the room
-- it takes all of them. Ordinary wobble is left alone, since the threshold is the same
`CatchUpAboveSeconds` the standstill uses. The jump is checked against `SaneUniverse` like any
number off the wire, and each player's recorded lateness is corrected by what was taken on, so the
same lead is not spent twice a second later. The time is the room's to keep: the games really did
simulate those seconds, and it is the clock, carried on wall time, that is behind.

**The first attempt did nothing, and only a game showed it.** It reconciled the lead inside
`EndWarp`, on the theory that the gap existed before the rate fell. It does not -- there is nothing
to reconcile at that instant, which is why both games froze exactly as before on a server carrying
the "fix". Its unit test passed, because the test set the lead by hand and so tested the theory
rather than the game. `A behaviour fix is not a fix until it has been seen in game` earned its place
in `CLAUDE.md` here.

Three tests hold the real one: `TheClockKeepsUpWhenTheWholeRoomHasRunPastIt`,
`TheClockIgnoresOneGameThatHasRunAhead` and `TheClockLeavesAnOrdinaryWobbleAlone`. The first times
out on the old code waiting for a clock that never moves.

What it costs: the world's time steps forward, by up to the least-ahead game's lead. That is safe
where it was feared -- `Prune` is given `_world.Universe`, the last snapshot's time, and never the
clock, so nothing is pruned early by a jump; and `Track` keeps a window and interpolates, so puppets
sit at their newest state for a moment, which is already what the end of a warp looks like.

**What makes the lead was misread here, and 2026-11-10 below has it measured.** Not the round trip
and a frame, which on loopback is a fortieth of a second: a game eased its idea of the server's rate
towards the new one at three tenths a word, five words a second, and so ran on at hundreds of times
real time for more than a second after the warp had ended. And the clock keeping up was half a fix:
it goes to the least-ahead game, and the others stand still for the difference.

Worth fixing in passing either way: the mod tells the player nothing useful while this happens.
`Sync.Now`'s line is `Math.Abs(b) > CatchUpAboveSeconds ? $"catching up: {b:F0} s behind"`, so a
player who is ahead reads "catching up: -1000 s behind" over a world that is not moving.

## 2026-11-10 -- one kitten is drawn torn open, in one game only

Seen in every run of `tools/two-games.sh --test`: the kitten `Niric`, which stands on the ground in
the save both games load, is drawn in game A with its helmet whole, its body gone and long dark
blades standing out behind it. Game B draws the same kitten clean. Both count one part, both call it
`Niric`, and nothing `tools/breakup.sh` compares differs.

It is not the kitten and it is not the wire. Ruled out by looking, each with a picture:

- **what the save says of it** -- the same save loaded a second time in the same game draws it clean;
- **who flies it** -- torn in the game that owns it, and still torn after that game disconnects;
- **its life support** -- both worlds write the same roster entry for it, with no condition recorded;
- **writing a save** -- a clean kitten is still clean after one;
- **how long the world has been up** -- a load three seconds after Start is clean, and so is one a
  frame after.

What decides it is when the save was read. From the configuration popup at start, the bridge's
`load` presses Start, waits for KSA to say a world is up, and reads the save. It read it on the very
pass the world first said so, which is still `Popup.DrawAll`'s own -- `Program.FrameNumber` was 0,
and StarMap's frame hook had not run once:

| the save was read | runs | the kitten |
| --- | --- | --- |
| at frame 0, from the popup's pass | 6 | torn open, all 6 |
| at frame 1, from the frame hook | 1 | clean |
| three seconds after Start, as a second command | 1 | clean |
| a minute after Start | 1 | clean |
| again, in a game that had drawn it torn | 1 | clean |
| by `two-games.sh` itself, on the build that waits | 1 | clean |

A kitten built before KSA has drawn a frame keeps a mangled skinned mesh for as long as it lives;
its helmet, which is a plain mesh hung off a bone, is untouched. What inside KSA's renderer goes
wrong has not been found, and one drawn frame is also the difference between the popup's pass and
the frame hook, so which of the two matters is not separated either.

Why one game and not the other: A loads the save, gives its world to the server and never loads
another, so it keeps the kitten it built at frame 0. B loads the same save the same way, then joins
and is handed A's world, which builds every craft again. The handoff's "both games show it now" was
not reproduced and is not explained by this.

Fixed in the bridge: `load` waits until KSA has drawn ten frames of the started world before it
reads the save, and the last row is game A as the rig brings it up. No player could reach it. A hand gets to Load frames later, and a world from a
server is loaded by `Sync.TakeWorld` out of the frame hook, which does not run until frames are
being drawn.

Four of those runs were made with a wait that never reached the game: `tools/ksa-mcp`'s `load`
passes the save's name on and drops every other argument, so "one frame", "ten" and "a hundred and
twenty" were all read at frame 0 and all came out torn, which for a quarter of an hour said the wait
did not matter. A log line with the frame number in it is what showed it.

## 2026-11-10 -- a warp asked at 1000x runs for one second, then at 1x

Found by the first walk of `tools/warp-trace.py`, with both games on rails, on loopback. A asks for
1000x and B agrees. What the server did, from its own trace, in seconds after the ask:

```
 0.26  rate 1000     universe 76162
 1.27  rate    4.17  universe 77174    "the world falls to 4.17x: the room can fly 4.17x"
 2.27  rate    2.5
 3.28  rate    1.5                     "the world falls to 1.5x: the room can fly 59.28x"
 4.29  rate    1                       and there it stayed, for the 16 s until A ended it
```

And the two games, sampled against that clock:

```
 1.5 s   A at universe 76246, 986 s behind    B at 76257, 1023 s behind
 4.5 s   A 700 s behind, running at 355x      B 685 s behind
 9.5 s   A 333 s behind                       B 297 s behind
19.7 s   A 3.4 s behind                       B 2.9 s behind
```

In one second the clock went 1011 universe-seconds and the games went about ninety. They then took
nineteen seconds to make it up, while the warp they had agreed sat at 1x. A thousand times real
time was asked for; the room got about fifty, as a lurch and a wait, and after that a warp still
running at normal speed. The second run of two, with a craft in physics, went the same way:
1000, 143, 73, 44, 26, 16, 9.4, 5.6, 3.4, 2, 1.2, 1, one a second, each of the last six logged as
somebody being between 18 and 21 s behind.

Three things, read off the trace and then off `Server.Govern`:

- **A warp starts at what was asked.** `Flyable` holds an ask to what the room says it can fly, and
  a game comfortable at the speed it is running says nothing -- `can` is 0 at 1x, always -- so
  every ask is agreed in full and the clock runs at it until the governor's first look a second
  later. At 1000x that second is the whole gap.
- **The first thing a game says it can fly is measured across the start.** `Sync.Sustains` is
  universe seconds over real seconds across two seconds, and the two seconds that end just after a
  warp begins are mostly 1x. The games said 4.17x and 5.04x; two seconds on they said 59x and 66x.
  The same thing is why a warp asked at 10x fell to 1x in its first second in one run and to 1.32x
  in the other, and why it then topped out at 8x in both: the fall sets the ceiling at four fifths
  of what it fell from.
- **Backing off for lateness makes the lateness worse.** `Losing` counts how far behind a game is in
  seconds of the rate now running, which is the right way to read a gap -- but the governor's cure
  for a late game is a lower rate, and the same gap is more seconds at a lower rate. 700
  universe-seconds is 168 s at 4.17x and 700 s at 1x. So once a gap is open every look backs off
  again, down to the floor, each fall lowering the ceiling as it goes. In the run with a craft in
  physics the last fall was from 1.22x, which leaves the ceiling at 1x, and a game comfortable at
  1x says nothing that would lift it.

Nothing was stopped, nothing went backwards and the two games never disagreed with the server about
the rate, so this is the claim the trace calls LATE and no other.

**Fixed in the server**, and seen in the same walk. Each has a test in `ServerTests` that fails on
the governor as it was, with the number in brackets where it wanted the one after:

- **A warp put to a room that has said nothing starts a step above normal and climbs**, half as
  much again every second, the way it already climbed back from a fall. Only a room in which every
  watching machine has said what it flies is run at the ask at once. [1000x, wanted 1.5x]
- **Lateness is backed off for once, and not again while the game behind is catching up.** The
  server works out from a game's last two words what share of the clock's advance it failed to
  keep, which reads the same at every speed and through a change of one, and a game that will be
  level again within fifteen seconds at that pace is left to it. [2.3x, wanted 30x]
- **A warp climbs no further while a game is being left**, which the server sees in two pings and
  a game takes two seconds to measure. [100x, wanted 1.5x]
- **A climb is never a fall.** A machine saying it flies nine while the world ran at ten was read
  as a ceiling at eight that was nobody's to lift, and each look after it stepped down to the
  ceiling the last had left. [8x, wanted 10x and then on to 20x]

What the same walk shows now, both games on rails:

```
asked 10x     1.5, 2.25, 3.38, 5.06, 7.59, 10      and it stays at 10, not 8
asked 1000x   1.5 ... 656.84, 985.26, 1000          seventeen seconds, then held for 27 s
              both games within 0.02 s of the clock throughout
```

And in a world with a rocket's wreckage in the sea, where the machines fly about seventy: up to
129.75x in eleven seconds, `the world falls to 61.32x: the room can fly 61.32x`, back up to 72x and
held there for twenty-two seconds. The same ask in the same world was 1x before.

What it costs is the climb: seventeen seconds to reach 1000x, where a room that could fly it used
to be there at once. What a game says it can fly is still measured across the start of any change
(`Sync.Sustains`); a warp no longer jumps, so that no longer has anything to mislead.

The warp keys had to follow. A key steps from the speed the world is running at, so a second press
a moment after the first found the world still climbing to the first rung, stepped from there, and
asked for the rung already asked for: `asked: 2`, twice. Faster now steps from what this player
last asked for, and three presses in a second are 2x, 5x, 10x.

## 2026-11-10 -- the end of a warp at 1000x, reached at last, stands one game still

With the governor fixed a warp held 1000x for the first time in any run here, and the trace's first
claim failed on the way out of it: **A stood still for 42 s**, and would have for about a hundred
had the walk not ended. The clock keeping up with the room, from the day before, was working as
written.

```
 s after the end    A: lead   speed      B: lead   speed
       0.2            -49 s   1031x       -161 s    740x     <- the warp has ended; both run on
       0.8           +264 s    309x        +80 s    332x
       1.2           +359 s    152x       +201 s    152x
       1.7           +240 s      0x       +120 s     53x     clock goes on 129 s, then 115, 17, 4
       2.7           +123 s      0x        +17 s     14x
       7.5            +97 s      0x         +1 s    0.9x     <- A still, a second for a second
```

Two things, and neither is the round trip, which was a third of a millisecond:

- **Each game ran on at hundreds of times real time for a second and a half after it was told.**
  `Client` took the server's rate from its clock words as a measure eased three tenths of the way a
  word, and a word is said five times a second: 1000, 700, 490, 343, 240. The speed the server says
  it runs at was in every one of those words. This is the lead the entry of 2026-11-09 put down to
  the wire, and it is the same easing that left a game a third of a second behind at every step of
  a climb.
- **The clock goes to the least-ahead game**, which is right when nothing says why a game is past
  it and wrong when the clock's own speed has just fallen: then every game is past it, each by as
  long as it took to hear, and those differ. A was 158 s further on than B, and that difference is
  what it stood still for.

Fixed, in three places, each seen in the trace by itself before the next went in:

| at the end of 1000x | games past the clock | stood still | past 6 s for |
| --- | --- | --- | --- |
| as it was | 359 s and 201 s | A, 42 s and counting | 43 s |
| the clock goes on to the furthest game, where its own speed has just fallen | about 400 s each | nobody | 2.4 s |
| and a game takes a changed speed as said | 89 s, and one not past it | 0.9 s | 6.8 s, catching up |
| and the clock's word is said the moment its speed changes | 20 s and 17 s | nobody | one sample |

The first is the server's alone and holds for a game that has not been updated. It is held to what
the fall explains, two seconds of the speed lost, so it is still not one game's word that moves a
room; the game passed has that to catch up, which on rails is seconds. The second is in
`Protocol/Client.cs`, so in the mod, and changes what no datagram means. The third is the server
again: the speed went out with the clock's next word in its turn, up to a fifth of a second late,
which from 1000x is two hundred universe-seconds every game ran on for.

With all three, a warp ended at 10x or 8x leaves both games 0.17 s past the clock where it left
them two to four, and through a warp they sit within 0.02 s of it where they sat a third of a
second behind.

Tests: `AfterAWarpEndsTheClockGoesOnToTheGameFurthestPastIt`, which stops at 201 s on the clock as
it was, and `AGameTakesAChangeOfTheServersSpeedAsSaid`, which reads 574x half a second into a
warp at 1000x.

Not seen: any of this off loopback, where the round trip is a real part of the lead; and the end
of a warp with a craft in physics, where the game the clock passes catches up at ten times and not
a thousand.

## 2026-11-10 -- off loopback: a lead followed twice, and three things said once

The walk again with the relay between the games and the server (`KSAMP_LAG_MS`, through
`two-games.sh --server`, so the same two games throughout). Held alone, a datagram's delay changes
one thing in proportion and breaks nothing until it is long:

| each way | round trip, as the games measured it | games past the clock at the end of 1000x | the clock went on | from the clock during a warp | past it after ending 10x |
| --- | --- | --- | --- | --- | --- |
| 0 | 0.3 ms | 20 s and 17 s | 21 s | 0.02 s | 0.17 s |
| 25 ms | 53 ms | 48 s and 50 s | 48 s | 0.04 s | 0.48 s |
| 50 ms | 104 ms | 84 s | 84 s, then 6 | 0.13 s | 0.63 s |
| 150 ms | 305 ms | 177 s and 183 s | 177 s, **then 182 more** | 0.34 s | 1.38 s |

A game's lead at the end of a warp is the trip there and a frame, at the speed that ended: twenty
seconds on no wire, and a second more for every millisecond of one. That is what the clock going
on to the furthest game is for, and through 50 ms each way it did it and every claim held.

**At 150 ms each way the clock followed one lead twice.** From the server's own trace, in seconds
after the warp ended:

```
 1.13  A says it is 177 s past the clock
 1.34  the clock goes on 177 s                        A is level, and B's lead is 5 s now
 1.60  B says it is 182 s past the clock              <- said a quarter of a second after the
 1.85  the clock goes on 182 s                           clock moved, and written before B knew
```

A word of lateness is about the clock as the game last heard it, and it takes a round trip for a
ping that left just before the clock's own word arrived to be read as stale: the word out, the
ping in. On loopback that window is nothing. At 300 ms it is a third of the time between pings.
Both games were then 180 s behind a clock that had passed them, and seven seconds catching up.

Fixed in the server: each time the clock goes on it is noted against every player, and a word that
reaches the server sooner than a round trip after it -- the round trip that game itself reports --
is read as being that much less far past. The same run after: `175 s`, then `5 s`. This one has no
test of its own, because nothing in the suite can hold a datagram back; the walk at 150 ms is its
test, and it failed before and held after.

**And a wire that loses and reorders -- 50 ms give or take 20, one datagram in fifty dropped -- found
what was only ever said once.** The protocol repeats a craft's state twenty times a second and asks
again for every missing piece of a world; what it did not repeat was the handful of words a session
turns on.

- **The server's ask for the first player's world.** One datagram, sent on the heels of the
  welcome. Lost, or arriving ahead of that welcome at a game that is not yet anybody, it is gone:
  the game sits at "sending your world to the server" with nothing asking it to, the server has no
  world and no clock, and everybody who joins after waits behind it. This happened on the first
  join the rough wire was tried on, and both games sat there for three minutes until the walk gave
  up. Jitter does it more readily than loss: two datagrams sent together and held 30 to 70 ms each
  swap places about half the time. The server now asks again every three seconds while nothing of
  the world has come.
- **The end of a warp.** A running warp is said once a second, and its ending was said once. One
  game missed it and went on showing `Running 8x` for the quarter of a minute until the next warp,
  which the trace's RATE claim caught in 25 samples; its faster key then stepped from 8x, a speed
  the world was not at, and asked for 30x where it meant 2x. The server now goes on saying a warp has
  ended for the five seconds nobody may start another.
- **A game's word that it is in the world.** Not seen, found by looking for the same shape: said
  once, and a game whose word is lost is in a world the server does not count it in, sent nothing
  and asked nothing. It now says so again every two seconds until the roster shows the server
  knows. In `Protocol/Client.cs`, so in the mod.

Each has a test that loses the datagram and fails without the repeat:
`AServerAsksAgainForAWorldItWasNeverSent`, `TheEndOfAWarpIsSaidMoreThanOnce`,
`AGameSaysItIsInTheWorldUntilTheServerKnows`.

Still said once, and not changed: that a craft has gone, to a game as it loads; a line of chat; an
ask for a warp; and a vote on one. The last two fail in front of the player who made them -- nothing
happens, or they are still being asked -- and are theirs to make again; one walk failed on exactly
that, a warp that never ran, and the walk now presses again as a player would and says that it had
to. The first is a craft standing in one game that is in nobody else's.

Six walks over that wire in all: nobody got into the world; every claim held, with the server
asking again; RATE, the end of a warp unheard; a warp that never ran, an ask or a vote lost; and
every claim held, twice. A wire that fails one walk in three is what found these, and two clean
walks after are two clean walks.

In passing, the relay's own account of the delay it applied said `held 0 ms on average` in most
lines. It counted per interval and was asked for the count on every pass of its loop.

## 2026-11-10 -- another player's craft in orbit is drawn a kilometre from where it is

The first thing `tools/motion.py` recorded. Each game writes down where every craft in its world is
as each frame is drawn, and at every physics step where each craft of another player's stood and
where it had been told to stand; the two accounts are laid side by side on universe time. Nothing
had measured this before: the bridge is asked twice a second and reports no position.

Game A flew a craft in orbit at 7.2 km/s and game B put its camera on it.

```
 drawn, from where its owner had it     1082.6 m, coasting or burning, to the centimetre
 put there outright                     every physics step, 120 a second
 a kitten out of its hatch              3.64 m from the capsule in both games, until the capsule
                                        changed hands: then 1,083 m from it in one of them
 a craft nobody owns, 177 m away        177 m away in A and 1,260 m away in B
 two craft put 40 m apart by B          40 m apart in B and 2,211 m apart in A
```

**A craft was drawn 150 ms in the past, on purpose**, between two of its states, which is how a
thing that walks is drawn smoothly. At 7.2 km/s it is 1,083 m. Everything a game steps for itself
-- its own craft, every craft nobody owns -- is in the present, so another player's craft was a
kilometre from all of it: nothing two players flew was ever beside anything else, and a craft
stepped a kilometre each time it changed hands.

And the mod was fighting KSA to keep it there. KSA puts a craft nothing is pushing back on its own
orbit at the start of every physics step, in the present and within 4 mm of where its owner has
it; the pull then found it 1,083 m from where it was told to stand and put it there outright, and
the frame was drawn. Every step, for every coasting craft another player owns.

This entry first said the opposite of part of that -- that a coasting craft stood in the present
and stepped back a kilometre when its engine lit. It read the craft where the pull finds it, at the
start of the step, and not where the frame draws it. The recorder writes both down now.

Fixed, in the mod:

- **A craft is drawn where it is at this world's own time**, carried on from its newest state
  rather than held back behind it (`Track.At`).
- **Carried on by the speed it had and the way that speed was changing**, in position as well as
  in the speed it reports, and for a quarter of a second where its states come closer than that;
  and **between two states, along the curve** that leaves one at its speed and reaches the next at
  its own. Without those, drawing in the present is a guess a round trip long made badly.
- **A coasting craft that is out of place is given its owner's orbit** through KSA's own mover, in
  the frame's quiet window, and at once when its owner stops pushing it: what the pull writes into
  such a craft is gone by the next step, so one left a little off after a burn drifted away for
  good. The orbit is made as of now and not as of its owner's last word, or the craft is drawn for
  one frame where it was then -- 350 to 650 m back -- every time it is put right.
- **A craft built here for another player is put where its owner has it before it is first drawn.**
  A kitten out of a hatch was drawn for its first two frames a kilometre and a half away.

The same scenes after, on loopback, with what each was before:

| | before | after |
| --- | --- | --- |
| a craft in orbit, coasting: drawn from where its owner has it | 1082.6 m | 0.003 m, 0.017 m at worst |
| and burning | 1082.6 m | 0.008 m at the 95th frame in a hundred, 0.049 m at worst |
| a kitten out of its hatch, from the capsule: the two games differ by | up to 2,165 m | 0.004 m at worst |
| two craft 40 m apart: the two games differ by | 2,210 m | 0.008 m at worst |
| put there outright, in a 22 s recording | 4,173 times | never |
| steps of a metre or more between two frames | 21, to 236 m | none |

And what could not be driven at all until the bridge learned to hold a craft's movement keys
(`move`) and to put one craft beside another or lift one off the ground (`put`):

| | drawn from where its owner has it |
| --- | --- |
| a kitten walking at 1.8 m/s | 0.5 m behind at the 95th frame, 0.77 m at worst, in a sawtooth; never put there outright |
| a kitten lifted 3 m and let go | 0.015 m at the 95th frame, 0.81 m for a moment as it lands |
| a kitten falling 150 m | 0.003 m at the 95th frame, all the way down |

Over the relay, two craft in one orbit with one of them burning, and how far the two games'
accounts of the distance between them differ:

| each way | median | 95th | worst |
| --- | --- | --- | --- |
| 50 ms | 0.000 m | 0.016 m | 0.30 m |
| 150 ms | 0.48 m | 0.87 m | 1.68 m |

What it costs is what the wire simulation says it costs (`tests/TrackTests.cs`, a known path said
twenty times a second through delay, jitter and loss, with no game at either end): a guess as long
as the trip from one game to the other. Millimetres for anything coasting or burning steadily on a
line up to 50 ms each way; a hand's width for a craft thrown about at 12 m/s2; and one step as an
engine cuts, 4 cm on loopback and 27 cm at 50 ms each way.

**The ten seconds after a join, explained.** A game that has just joined is two or three seconds
behind the game it joined and gives a tenth of a second of that back every second. `Track` kept the
last 32 states of a craft, which at twenty a second is a second and a half, so a game further behind
than that asked about a time older than anything kept and was answered with the oldest state there
was -- up to a second ahead of what it had asked -- as though it were the answer:

```
 s after joining   behind by   told the craft was    put there outright
        0           2.70 s      7,935 m from it        every step
        5           2.17 s      4,085 m
       10           1.68 s        612 m
       11           1.58 s          0 m                 never again
```

873 times in eleven seconds, and it is what the "330 m off its owner's orbit" in the log was
measured against. States are kept by how long now, four seconds, and a craft told of a time that
is not now is neither pulled nor measured. The same eleven seconds after: 3 mm, and nothing put
anywhere.

Not explained and not fixed then, and all of it taken up in the next entry:

- **A game that is at no craft keeps no clock.** When the falling kitten died its game was left
  flying and watching nothing, which is where the mod stopped holding a game to the server's time;
  it ran free, and within a few seconds another player's craft in orbit was drawn 5 km out and put
  back 26 times.
- **A walking kitten trails half a metre**, and only six seconds of walking were measured.
- The meeting was not recorded over the wire that loses and reorders: the two games would not
  settle on the craft in two minutes there.
- Not measured: a craft in the air under its own power, a landing on engines, and a docking.

## 2026-11-10 -- no craft, a long walk, engines in the air, and what a docking leaves behind

Four things the entry above left open, each recorded with `tools/motion.py` in two games before
anything was changed and again after. The bridge grew what they needed: `watch {"body":...}` puts
the camera on a planet as picking one in the map does, `attitude` presses the flight computer's
buttons, `land` brings a craft down by lighting and shutting an engine KSA will not throttle, and
`dock` raises the event KSA's contact solver raises when two ports touch.

**A game at no craft kept no clock.** `Sync.HoldTheClock` let go of the server's time whenever a
game was flying and watching nothing, forced 1x, and reported being late by nothing. KSA's camera
takes control of whatever it follows, so a player who picks a planet in the map is there, as is
one whose kitten has died. Recorded with one game's camera on Earth while the other flew a rocket:

| | its universe time less the other game's |
| --- | --- |
| forty seconds, no warp | -0.21 s at the median and drifting, 0.3% slow |
| a warp of 50 for twelve seconds | **-229 s** when it ended, and it stayed there |
| the same, held to the clock | -0.009 s when it ended, 0.55 s at worst during |
| the same, 50 ms each way and 2% lost | -0.009 s, 0.15 s at worst |

229 s behind, the rocket it drew was 1,661 km from where its owner had it. The reason it was let
go is in the commit that did it: before that the clock was simply not held out of flight, so the
catch-up warp from a join ran on in the editor. Forcing 1x cured that and the editor is the only
place it was needed, because it is the only place KSA stops the world. Held everywhere else now.
The judge compares the two games' clocks at one instant of the time of day, which it did not
before: joined on universe time alone, a game four minutes in the past agrees with everything.

**A walking kitten's copy fell behind for as long as it walked.** Not half a metre: the gap grew
0.18 m every second, a tenth of the walk, to 1.57 m after nine seconds, and closed only when the
copy left the ground for a moment. The six seconds measured before had not been long enough to
see it was a slope and not a level. A kitten's correction is held to 0.15 m/s so a standing kitten
is not animated walking on the spot (2026-11-06), and KSA's traction servo brakes a copy that is
asked for no movement by more than that. While its owner walks it, the correction may now be half
the speed its owner says, short of the 7 m/s at which KSA trips a kitten. A walk of 38 m in 24 s
after: 4 to 5 cm throughout, 19 cm at worst. Over the lossy wire, 8 cm at the 95th where it had
been 1.1 m. How far the kitten gets before it falls over depends on where it is pointed -- 10 m,
30 m, and once not at all.

**The meeting over the lossy wire ran.** Twice, the second time straight after the relay was put
under the games, which is when it had refused: 12 mm between the two accounts of how far apart the
craft were. What stopped it the first time was not found. The scene before it that day had left one
game at no craft, which is the state fixed above, and that is a guess.

**Engines in the air.** A copy of the Rocket put on a pad through the editor. Its flight computer
was asked to hold it pointing up and, as the next entry found, never heard: it flew straight
because nothing turned it.

- Burning 35 s off the pad, to 214 m/s and 3.8 km: its copy within 2 mm at the 95th, 5 cm at
  worst, which was the frame the engine shut.
- Coming down from 6 km with the engine lit whenever it fell faster than its height allowed:
  4 cm at the 95th, 9 cm at worst. It ran out of propellant at 900 m and hit the ground at 66 m/s.
- Hopped to 200 m and brought down again, touching at 2.5 m/s: 2.5 cm, and 15 cm over the lossy
  wire. Then it fell over, having stood on its engine bell, and broke into thirty pieces.

**What the crashes showed.** Neither was looked for.

- A piece's copy in its first seconds stood up to 10 m from where its owner had it, stepped up to
  19 m between two frames and was put where it was told a few times; at rest every piece agrees to
  millimetres. A craft that is not the one flown is said twice a second, and what is worked out
  from two words half a second apart about a thing that is bouncing is wrong by metres. Not fixed.
- The hull's copy stood 2 to 3 m out for 5 to 8 s after it lost parts: it waits to be built again
  in its new shape, and the old shape is held against a centre of mass that has moved. Not fixed.
- **Pieces that were nobody's, in one world only.** Three of 28 after one fall, six of 50 after
  another: standing in their owner's world, unowned, and in no other world at all. Each was a piece
  of a piece. KSA hands a destroyed piece's name to the next piece shed; the owner had said the
  first was gone, and the server would not give a gone name to a claim that was not a player
  taking control, so the new piece was claimed, refused, and let go of. A claim for a craft its
  game has just made is given the name now. Thirty pieces in both worlds after, and none nobody's,
  in the one fall recorded since. A wait of half a second before saying a craft gone was tried
  first, on the theory that KSA builds the piece again a frame late; it left one of 28 and was
  taken out.

**Docking.** KSA docks two craft by taking the lighter into the heavier, in the world whose
physics saw the ports touch.

- *With a craft nobody flies.* The Rocket took Gemini7 in and was 110 parts in both worlds, and
  Gemini7 stood on in the other world, 21 parts inside the craft it had become part of: only a
  craft's owner may say it is gone, and it had none. It is claimed now, to be said gone when the
  server answers. 110 parts in both worlds and the capsule in neither after.
- *With the craft another player is flying.* One player's rocket went into their copy of the
  other's and was gone from every world; its owner flew on with 89 parts, the first player's
  world held 178 under that name, 14 m from where its owner had it, and put it back 3,670 times
  in half a minute. Refused now, as a part failure on another player's craft is, and the player
  is told. The control is the same bridge call, which docks when nobody flies the other.
- *A craft one player built and another took over* was never followed in its maker's world: a
  craft a game made is kept out of other players' hands so that a name two games both gave
  something is not taken for one craft, and that was kept for ever. It ends once the server has
  said the craft is its maker's. Built again as its new owner's after, and followed through a
  burn to 3 cm.

No docking was flown here. The next entry flies one.

**And the rig.** `two-games.sh` asks whether a game is running with `tasklist.exe | grep -qi`,
under `pipefail`: grep stops at the first match, tasklist dies of the closed pipe, and the
pipeline has failed -- so two running games read as none, and the launch went on to fail at
replacing a DLL the games held open. It reads all of tasklist now.

## 2026-11-10 -- a docking flown, and what was left of both craft the first time

The five things the entry above ended on, taken in turn. Each number is from `tools/motion.py` in
two games; "the lossy wire" is 50 ms each way, give or take 20, losing one datagram in fifty.

**A docking, flown.** The bridge's `approach` sets a craft up as a player does -- the other's port
targeted, the craft controlled from its own, the flight computer told to `Align` -- then holds each
translation key for 0.4 s to learn what it does, and flies: onto the line out of the port and held
4 m off it, then down the line. The Gemini's keys push it at 1.6, 0.9 and 0.8 m/s^2. From 80 m it
took two and a half minutes, arrived 6 cm off the line and 0.4 degrees off the axis at 0.32 m/s,
and KSA docked the two on contact. A minute later it was undocked through the port's own menu
input, and KSA gave the capsule its name back: 21 and 89 parts in both worlds. The other game drew
the capsule within 22 cm of where its owner had it through the approach, 8 cm over the lossy wire.

Three things had to be found first.

- *The flight computer never heard.* KSA hands each physics step a copy of a craft's flight
  computer and takes the copy back, so `SetEnum` from a frame hook was written over every time:
  twenty seconds of asking left it in Manual with nothing to track, and the launch and the hop of
  the entry before had flown with nobody steering. It is told where a step is prepared now, and
  lined the capsule up from 75 degrees in twenty seconds.
- *The first docking that worked destroyed both craft.* The other game was watching the capsule.
  A game that watches a craft nobody owns and flies nothing takes it, and a craft its owner has
  just said is gone is nobody's for the frame it takes to leave: so the watcher took the capsule
  that had become part of a rocket, said how it was built, and every other world built it again
  inside that rocket. Eleven parts past their crash tolerance at once, 33 pieces. A craft on its
  way out is not taken now.
- *KSA docks two ways*, by contact and from the port's menu when two ports are within 2 m, and
  only the first goes through `DockingEvent`. The refusal and the word that follows a docking are
  on `DockingPort.Dock`, which both call; `DockingPort.Undock` is refused on another player's
  craft, whose port menu is on everybody's screen.

**A copy was in no world for five seconds whenever its shape changed.** The wait that keeps a name
from being built over and over was counted from when the old copy was taken away, so the new one
was built five seconds later: a rocket that dropped a stage or docked vanished from every other
world for that long, and the hull of one breaking up was gone, then back in a shape it no longer
had, then gone again, fifteen seconds in all. The old copy and the new change places in one quiet
window now -- 16 ms in the log -- and a craft still changing is built again after two seconds, not
five. The hull of a rocket falling over is in a shape its owner has left for 2.1 s at the longest,
and the judge counts frames of that separately: a copy with other parts has its centre somewhere
else, which says how its weight is spread and not where it stands.

**The pieces of a crash.** A rocket falling over into thirty, on loopback:

| | before | after |
| --- | --- | --- |
| the worst piece, at the 95th in a hundred | 2.3 to 2.5 m | 0.8 m |
| the worst any piece was drawn | 13 to 26 m | 2.0 m |
| the worst step between two frames | 13 to 26 m | 0.8 m |
| steps of a metre or more | 10 to 22 | 0 |
| put where it was told outright | 2 | 0 |
| pieces a metre out at the 95th | 7 to 8 | 0 |

Three changes. A craft that is under way and not flown is said ten times a second and not twice,
all of them together no more than two hundred times. A copy is put where it is told at its first
physics step, before it is drawn: it is built where it stood when it was described, thirty are
built one a frame, and each began with a step everybody saw. And the third was a fault: a copy
of a rocket that crashes was no puppet for the frame it was told to go in, the crash this world's
own physics had ready for it went through in that frame, and its pieces were shed here under the
names its owner's pieces have, nobody's, standing in for them. Near by they lay a few metres
from the real ones. Far off, outside this game's physics, one fell through the ground at 65 m/s
for two minutes, 7.4 km below a piece its owner had lying on a pad. A craft on its way out is
spared this world's physics as a puppet is.

Over the lossy wire the same fall is 1.9 m at the worst piece's 95th and one step of 1.5 m. Not
zero, and it will not be: a piece that bounces is somewhere its last two words did not say.
A piece lying still that is struck is still said twice a second until its owner's game has it
moving, and one struck and destroyed within two seconds was drawn 9 m out for the last of them.

**A craft its owner moves to another orbit** while it is in the other game's physics was put back
11,000 km at every step for seventeen seconds and never given the orbit: looked at when the frame
hook runs it stands where the pull has just written it. A coasting craft the pull had to put back
is given its owner's orbit now. Only the rig moves a craft like that, and it is how every docking
scene starts.

**Gone, said more than once.** The server says it again 0.3 s and 1 s later while the name stays
gone, and a game says it again for as long as the roster goes on giving it the craft. Two tests.

**On the final build**, the warp trace holds every claim; on loopback the flown docking and
undocking, a launch, and a hop and its breakup; over the lossy wire the hop. Recorded over the
lossy wire one and two builds before it, with nothing in their path changed since: both bridge
dockings, the flown one, a craft handed from its maker to another player, the meeting, the long
walk, the game at no craft through a warp, and a launch. Every one exits 0 but the breakups.

Left as they are:

- **A craft nobody owns is each world's own**, and a docking is flown at one. After a walk of
  warps the Rocket nobody flew was 3.4 m from itself in the two games, so one saw the capsule dock
  3.4 m from the port. And the rig's server swap leaves every piece of debris nobody's: thirteen
  expired in one world and stood on in the other.
- **Two players cannot dock**, and what that should do is a decision.


## 2026-11-11 -- what nobody owned, two players at one port, and a hull in the way of its own wreckage

Five things left open the day before, taken in turn with two games and `tools/motion.py`. The wire
is version 24 for it: a claim and a roster entry say whether a craft is held only for being near,
a state says how many parts its craft has, and there is a datagram for a docking.

**A craft nobody owns was in as many places as there were games.** The rocket a capsule docked
with was nobody's, and stood 3.4 m from itself in the two worlds. A game now asks for every craft
nobody has within 10 km of what its player flies or watches, the server gives each to the first to
ask, and the rest draw it where that game has it. First written as "every craft this game's
physics is stepping", from `Vehicle.HasPhysicsBubble`: KSA steps every craft in a world wherever
it is, so the first game in held all seven craft in the save, two of them in orbit with its player
standing on the ground. With the distance in, it held the one beside its player. Such a hold keeps
nobody out -- flown, the craft is the flier's at once -- and lapses when its holder goes away. The
rocket is 3 mm from itself now, over the lossy wire.

**A craft nobody owns, destroyed in one world, stood on in the others.** The game KSA destroys it
in claims it and says it gone. Seen with a two-part rocket nobody was near: wrecked through the
bridge in one game, gone from both. Not where more than eight go at once.

**Two players' craft dock.** Refused the day before, on my call. They dock in whichever game sees
the ports touch first; that player has the one craft and the other rides in it. What went wrong on
the way:

- *The server refused the first one flown.* A capsule flown onto the other player's rocket is the
  craft that is taken in. The game found its own capsule dead, said it gone as it says any craft of
  its own gone, and then said the two had docked: by then the capsule was nobody's, the rocket was
  the other player's, and a game may not dock two craft neither of which is its own. The flying
  game was left with 110 parts under the other player's name and the other with 89. Through the
  bridge it had worked, because there the flying craft was the heavier and the one left. The
  docking is said first now, and the craft it took is not said gone separately.
- *After undocking, the player who docked was left flying the other player's craft.* KSA puts its
  player in the half that comes away. Asked where it splits them, the game was still flying the
  stack; a moment later it was in the capsule that had been the other player's, who then waited
  for a craft nobody was going to leave. Looked at for a second after the split now, and the game
  flies the other half.
- *The other game's copy was put nine metres along the rocket, twenty-four steps running.* A
  craft's place is its centre, and the centre of a rocket with a capsule on it is not where the
  rocket's was. The copy was the old shape until it was built again and was put where the new one
  stood. A state says how many parts its craft has, and a copy of another shape is never put
  anywhere outright.
- *And that, done bluntly, broke the breakups.* First the copy of another shape was not moved at
  all. A hull coming apart on the ground is another shape most of the time, and standing still
  while its owner's fell away it stood in the way of its own wreckage: a piece told to fall at
  10 m/s fell 3 m behind in a second with the pull on, its copy's speed knocked back to one at
  every step. Three variants and four crashes to find, since no two crashes are alike. The rule
  is only for a craft its owner has coasting, which is where a docking or a stage leaves one.

Flown over the lossy wire: docked, undocked, the rocket back with its first owner ten seconds
after. Exit 0, the rocket's copy within 18 mm and the capsule's within 47 cm.

**A piece lying still that is struck** was written down as said twice a second for a second and a
half. The recording does not say that: the piece was out for eleven frames. Two smaller things
were true and are fixed. A change of speed is carried forward because an engine goes on burning,
and a blow does not go on: a piece that stopped dead was drawn leaving the ground again at the
speed it arrived. `Track` carries a change only where the change before it agrees, which costs a
burn its first interval; `tests/TrackTests.cs` counts a third fewer frames out by 0.3 m after a
blow. And a craft set moving is said at once, not at the next turn it was given while it lay
still. What is left of that 9 m is in two entries under what has not been tried in
`docs/DESIGN.md`: a piece that breaks off a hull is built inside the hull's copy, which still has
that piece in it, and is held there until the hull is built again; and forty pieces under way
share two hundred states a second.

**Every scene over the lossy wire on this build**, 50 ms give or take 20 with 2% lost: the flown
docking with a craft nobody flies and with one the other player is in, both dockings through the
bridge, the meeting, the walk, the warp at no craft, a launch and a hop. Exit 0 for the
two-player flown docking, the meeting, the walk, the warp and the launch. The other dockings fail
only on wreckage the rig's server swap had left nobody's, lying 2.6 m from itself with both
players in orbit. The hop fails on two pieces of thirty-eight stuck in their hull's copy and put
where they belonged once, 4.9 m, and on the hull being an outdated shape for 4.2 s while it came
apart. On loopback a hop has no piece put anywhere and no step over 0.76 m.

Not run again: the warp trace, since nothing in the clock changed, and a craft handed from its
maker to another player as a recording of its own, though every docking above hands one over.

## 2026-11-11 -- a piece inside its own hull, a docking that was refused and counted, and three words to carry a speed on

What was left open that morning, bar the rider who cannot leave a docked craft.

**A piece that broke off a hull was built inside the hull's copy.** The piece is described at
once and the hull up to two seconds later, so the other game built the piece inside a hull that
still had it on, and KSA's collisions held it there until it was 5 m out and was put where it
belonged. A piece now waits to be built until the hull standing here was built from a word its
owner wrote after the piece's own, and a hull a piece is waiting on is built again after one
second, not two. That moved the fault before it removed it: a piece built seconds late is built
where it was described, in the air where it broke off, and was drawn there for a frame -- five
put 8 to 13 m in the first crash. Put on its orbit at build where it is still falling, as a
kitten out of a hatch already was, and where it lies otherwise. KSA's own `TeleportToLocation`
does that a frame later and onto the ground at that spot, which left one piece 1.75 m from where
it lay for that frame, so the place is written into the craft as the pull writes it.

**Forty pieces sharing two hundred states a second.** A craft nobody flies takes its turn early
when it is a quarter of a metre from where its last word would have it drawn. Median piece at its
95th: 5 cm on loopback where it was 17.

**A craft nobody owns with nobody near.** One that is moving is held by the game it moves in,
wherever it is. And a hole in the morning's work: a game that holds a craft only for being near
never writes it down, so when KSA cleared old wreckage out of the other game to make room for a
new breakup, seven pieces stood on in one world with nothing to build them from in the other. A
game KSA takes such a craft out of claims it and says it gone, as it does for one nobody holds.

**Then the dockings again, which is where the time went.** Three faults, each found by the run
meant to show nothing had changed:

- *Both games docked the pair a tenth of a second apart, and the rider was not a rider.* The
  roster is told of a docking as the datagram arrives and the game acts on it a frame later. By
  then it had seen its craft was somebody else's and let go of the controls, so asked what its
  player was flying it said nothing: no word to the player, nothing watching the craft, and no
  way for their rocket to come back. Asked now of the last claim made, a moment before.
- *The rocket was put 5 m back five steps running, half a second after docking.* Two states of
  the docked craft a twentieth of a second apart, differing by the nudge the two gave each
  other, then nothing for three quarters of a second: a lost datagram. That nudge was carried on
  as though it were a burn. `Track` carries a change of speed on only where three states agree.
- *After undocking, the other game docked them again by itself.* Twice in two runs, and it took
  two fixes because the first was to the wrong thing. The two copies are built port to port a
  tenth of a second after they part. I took it for the frame in which a copy is not yet held
  for anybody, and had the refusal read the roster as well; it docked again all the same. The
  refusal had been working throughout. What runs after KSA's docking call runs after a refused
  one too, and I had dropped the line that asks whether anything docked when I rewrote it the
  day before: a refused docking was counted as done, the rocket was this game's own for the six
  seconds the server took not to answer, and the next touch docked it in earnest. The rider then
  took back a rocket with a capsule on it that every other world had as two craft. A game that
  docks what the server will not have now builds both again as their owners have them.

**On the build with all of it**, over 50 ms give or take 20 with 2% lost: the flown docking onto
the other player's rocket, with both games docking at once, the undocking, and the rocket back
with its first owner, failing only on one step of 1.1 m in the capsule's copy; and a hop, with
nothing put outright, the worst piece 1.0 m out at its 95th and three steps of 2.9 m. On
earlier builds of the day, before the three docking faults were fixed and before a change of
speed needed three states to be carried on: a craft handed from its maker to another player and
launched, exit 0 and 2.6 cm; two more hops, one onto the wreckage of the other; and the warp
trace on loopback, every claim held.

## 2026-11-11 -- every scene on one build, and what running them all turned up

Asked whether three leftovers were worth fixing. One was only coverage: run everything on the
build that would ship. Doing that found four more things, one of them older than the week's work.

**The steps were mostly not faults of the copy.** From the recordings: the 2.9 m steps in a hop
are each a piece that was built late, drawn for exactly one frame where it was built. The 1.1 m
step at a docking is KSA setting the capsule onto the port in its owner's game. Left as they are.
One was real: a capsule's copy stepped 4.1 m, 3.2 s after undocking. It is built as the two part
and put on the orbit of its owner's newest word, which is from before the push that parts them;
a coasting copy is looked at every three seconds, by which time it was 4 m out. A copy just built
is looked at again after half a second.

**That fix made the next run worse, and showed something else.** The two copies are built port to
port, and in one run KSA's collisions knocked the rocket's copy away at 4 m/s. Having just been
looked at, it was not looked at again for three seconds: 13 m out, and put back 250 times to no
effect, since a write into a coasting craft does not hold. A coasting copy more than a metre out
is now put on its orbit within half a second, however lately it was looked at.

**The rig's autopilot had a dead zone.** `approach` holds 5 m out until it is on the line. Inside
4 m and off the line it neither corrected sideways nor backed out by more than it thrusts for,
and sat at 3.9 m and 0.9 m off for as long as it was left: three scenes that exited 0 having
docked nothing. It had docked before only when it arrived lined up. The correction is
unconditional. A scene is checked against the server's log, not its exit code.

**An older word of how a craft is built could replace a newer one.** A piece written down twice
two seconds apart, 11 parts and then 10: over the lossy wire the first finished arriving at the
server after the second. The server kept and handed on the last to arrive, so the other game had
11 parts for good and so would anybody who joined. Nothing from this week: it needs loss and a
craft that changes shape twice in quick succession, which is a breakup. The server keeps the time
each was written and drops the older. `AnOlderWordOfHowACraftIsBuiltDoesNotReplaceANewerOne`,
failing first.

**The warp trace failed once.** Started seconds after both games had joined, one game stood still
for 22 s at the end of a 10x warp, 26 s ahead of the server. Run again at once it held every
claim, as it had twice earlier in the day. Nothing in the clock changed. Not found, and written
down under what has not been tried.

**On that build, over 50 ms give or take 20 with 2% lost.** The flown docking onto the rocket the
other player is in, undocked and returned: two steps of 1.2 m in the capsule's copy as it is put
right after undocking, and nothing else. The bridge's docking of two players' craft, the
meeting, the walk, the warp at no craft and a launch: exit 0. A rocket handed over and launched:
1.4 cm at its 95th, failing only on the 11-part piece above. Three hops: nothing put
outright, no craft in one world only, worst piece 0.4, 0.7 and 3.3 m at its 95th, one step over
a metre in the three. Not on that build: the bridge docking of two craft of one player's, which
the scene could not set up, and the flown docking onto a rocket nobody is in, which ran with
the other player still in it.

## 2026-11-11 -- the warp trace's one failure, and a watcher that docked two craft it was not flying

**The warp trace that failed once was the judge, not the clock.** At the end of the warp at 1000x
one game was 25 s past the server and stood still for 22 s. The server moves the clock on only to
games whose window has the player's attention -- KSA throttles the rest, and what they say of
their lateness is about that -- and the server's own trace has that game down as in the
background for every one of its 487 samples. So it was in the run that passed. What differed was
the game in front: a minute behind the server when the warp ended, so there was no lead of its
own for the clock to follow that would have covered the other's. Working as written. The judge
reads the window's state now and notes a game in the background that waits, where it failed it.

**A game that was only watching docked the pair.** The last scene not yet run on the build: a
capsule flown onto a rocket nobody is in. It passed, and the server's log said Bravo had docked
them and been given the rocket -- Bravo, whose player was watching. Two holes. A watcher asks
for nothing near another player's craft, but the craft it watches is not held for anybody in
the frame after it is built again, and in that frame the watcher took the rocket; the roster is
asked as well now. And a game could dock another player's craft with one it held only for being
near, which is a hold that exists so the craft is in one place: that docking is refused, and
is the other player's game's to do. Run again, Alpha's game docks them. Found only because the
log was read; the recording was clean both times.

**Wreckage held by the watcher.** A piece of old wreckage that a rocket fell on was put 5 m in
one game: the watcher held it, the flier's rocket struck it, and each game worked out its own
half. The rule above is what stops the watcher holding it.

**Run on that build, over the lossy wire:** the flown docking onto a rocket nobody is in and the
bridge docking of two craft of one player's, both exit 0; two hops, nothing in one world only,
one with a piece held back by the copies around it and put 5 m once. The warp trace on loopback,
every claim held, on the server that drops an older word of how a craft is built.

## 2026-11-11 -- a second pair of eyes on two days' work

Before building mod support on it: the work since 0.17.0 committed, read by two reviewers who had
not written it, and a late joiner tried.

**Joining late.** With two players' craft docked and one riding, the rider's game left and came
back: 110 parts in both worlds, a kitten the other player held for being near it drawn 4 mm from
where it was, and the undocking after it the same in both.

**What the reviews found, and all of it was mine.** On the server:

- A docking was taken on a game's word, so a player could remove or take any craft in the world
  by saying it had docked with one of their own. Where the other craft is another player's in
  earnest, both must now have been said by their own players to be within 500 m.
- Saying how a craft is built took a craft another player held for being near, and overwrote it.
- A game that was flying the craft a docking took asks for it every two seconds until it hears,
  and one such ask arriving after the docking brought the craft back with no world holding it.
- The same two craft docked twice inside half a minute were answered as the first docking said
  again, and nothing was changed.
- A craft held for being near stayed so while its holder flew it, if the claim was lost.
- `Track` wanted three states to carry a change of speed on and kept only two under a warp.

In the mod:

- A player who flew onto another player's craft had the craft and was at no controls. KSA
  hands over the craft that is left in the same call that docks them, while it was still held
  here for its last owner. It was there in every recording as a row that said "coasting" where
  it should have said "flown", and I had read past it.
- A name said gone for a craft nobody owned went on being said gone for ten seconds, and KSA
  hands a freed name to the next piece shed.
- A piece whose wait for its hull ran out began it again unless it was the one built that frame.
- A craft held only for being near was not taken outright before its crew was sent out of it.
- The half left behind at an undocking was this player's only if it was nearer their craft
  than anybody else's.
- A rider's camera was put back on the craft they rode in every frame.

Four tests for the server's; the mod's seen in game again over the lossy wire: the flown
docking with the flier now at the controls of the stack, the undocking, each player back in
their own craft.

**And one more from the runs after.** A piece of a crashed rocket stood in the watching game
alone: that game's own breaking of a copy, under a name nobody answered for any more. A piece,
by its name, that came off nothing of this player's and is nearer somebody else's craft is taken
away. Two hops of four in these runs, each the first after the games were started, had the
watching game a sixth and a quarter of a second behind for the whole run, and every number in
the worse of them was worse for that; the runs after them were within 9 ms.
