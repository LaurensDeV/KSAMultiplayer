# Why another player's craft pops, and what to do about it

Three analyses of craft synchronisation — the interpolation that decides where a craft should be
drawn, the write that puts it there, and what goes on the wire — each then attacked by a reviewer.
Read at `ce4b0fb`. Nothing here had been seen in a game when it was written: it is all source
reading and arithmetic, which is exactly the weakness that made today's catch-up ceiling a
regression.

**Measured since, on 2026-11-10, and the largest thing here was not in it.** `tools/motion.py` now
records where every craft is in both games at every physics step, and `tests/TrackTests.cs` flies
a known path through a simulated wire. A craft was drawn 150 ms in the past and a coasting craft is
stepped by KSA in the present, a kilometre apart at orbital speed; `Track` draws at the present
now, and what follows about `Track.DelaySeconds` and about the pull on a craft in orbit describes
code that is gone. Of the kitten sections one thing has been measured: a kitten walking at 1.8 m/s is
drawn half a metre behind itself in a sawtooth and was not put anywhere outright in the six seconds
the rig can walk one, where this predicts five metres and a teleport after twenty-one. `errors.md`.

**Partly acted on since.** Of "What to ship, in order" below, 1, 2, 4 and 6 are in the code --
`Sync.cs` jitters `SendNext` on construction and accumulates it rather than resetting it, `World.cs`
takes a craft off rails only when `Pull` writes something, and `Clock.cs` carries attitude forward
by `Spin` and extrapolates `VelocityCcf`. Check any other item against the line it names before
treating it as outstanding, and `docs/DESIGN.md` for what has since been watched in a game.

## The correction that matters most: tonight's kitten fix is inert after one frame

`Pull` calls `TakeOffRails()` on every frame it writes anything (`src/Mod/World.cs:622`), and a
craft in contact can never get back on rails: `ConstraintSim.UpdateSimFromVehicle` forces
`Awake = true` for anything off rails (`ConstraintSim.cs:340-343`), the only route back needs the
body asleep (`:411-415`, `:439-443`), Bepu needs `|v| < 0.0707` for 32 steps to sleep (`:349`,
`:376`), and `PhysicsStates.UpdateActivity` returns early for any craft with contact
(`:772-775`) so `TryToPutOnRails` can never fire for a landed craft at all.

So the `KittenRestM = 0.25` deadband at `World.cs:605` is reachable **once**, on the first frame,
and never again. Half of the kitten fix released as 0.9.2 does nothing.

**And the whole clamp approach is provably dead.** To follow an owner who is walking, the
correction has to exceed what KSA removes from the written velocity each frame — the traction servo
takes `min(|v|·0.341, 0.327)` m/s per 1/60 s chunk (`PoseIntegratorCallbacks.cs:111-121`,
`KittenServoPrecomp.cs:71-81`) and the contact friction another ~0.065
(`NarrowPhaseCallbacks.cs:160-182`), so the gap opens at **~0.39 m/s** whenever the owner walks. To
stay out of KSA's locomotion state machine the correction has to stay under
`IdleSpeedThreshold = 0.2`. **0.39 > 0.2: no clamp value satisfies both.** The correction has to
leave the velocity channel entirely.

That also means 0.9.2 traded a 60 Hz animation flutter for a 5 m teleport, and it arrives in about
**21 seconds of walking**, not the half-hour one analysis estimated.

## What actually reads as "teleport"

The frozen-attitude bug is real — `Track.At`'s extrapolation branch rebuilds position but carries
`Body2Ccf` and `VelocityCcf` from the newest state untouched (`src/Protocol/Clock.cs:206-215`),
while `Pull` writes attitude absolutely every frame — but it is **not** the reported complaint. The
craft a player watches is the one its owner is *flying*, and that is sent at 20 Hz
(`controlled = ReferenceEquals(v, flown)`, `src/Mod/Sync.cs:1105`), where the attitude step is
about 6°. The 0.5 s rate applies to spent stages, parked hulls and debris. Two analyses converged on
this mechanism, and that convergence was not evidence: both did the same multiplication from the
same source read.

The real teleports, in order:

| artefact | magnitude | when |
| --- | --- | --- |
| `MostAheadSeconds` cap, then `SnapM` | the target freezes while the craft flies on; `off` then grows at the craft's full speed and the pose is teleported back **~130 m every frame** | 1.15 s to 5 s of silence at 1x — and **1.15 s to 49 s** behind a 10x warp |
| `SnapM = 5.0` in orbit | 5 m is 0.64 ms of clock disagreement at 7.8 km/s, so it fires continuously once any bias exists | any clock bias, and on every bubble entry (`PlaceNearEnoughM = 25`) |
| `t <= _states[0].Universe` | every puppet pinned to the oldest held state, `Kept × interval` behind — **1.6 s at 20 Hz** | a world behind the server, which `errors.md` records at 200 s |
| `LostSeconds` drop | the puppet leaves `Puppets`, drifts free, is re-adopted and snapped from wherever it got to | 5 s of silence |

## Dead code, and a finding struck

**`PlaceAnalytic` can never run.** Its first line is `if (v.HasPhysicsBubble) return false;`
(`World.cs:515`) and `Settle` re-checks the same thing before calling it (`Sync.cs:1327`) — while
`VehicleUpdateTask.AssignOrphanVehicles` gives *every* orphan vehicle a bubble every frame
(`:303-341`), and the mod enumerates the same collection KSA walks. So both of its branches, 41
lines with the longest comment in the file, are unreachable.

Consequently **`docs/REVIEW-2026-11-06.md` finding 2 should be struck**: "`PlaceAnalytic` mutates a
list KSA's workers are walking" cannot happen. The `Pads.LayOut()` half of that finding stands and
its fix was worth shipping; the placement half guards a function that never executes.

`Pull` is the only mover. `World.cs:547`'s comment does not merely mislead — it describes dead code.

## What to ship, in order

Everything in the first group is one or two lines, needs no `Wire.Version` bump, and was confirmed
by a reviewer.

1. **Jitter `Mine.SendNext` on construction** (`Sync.cs:71`). It defaults to 0 and `now` is taken
   once for the whole loop (`:1056`), so every craft adopted in one frame is phase-locked for the
   session: a 62-fragment breakup sends as one cohort, twice a second, for ever. Spreading it turns
   a periodic burst into a stream, which cuts *correlated* loss — and correlated loss is what drives
   the freeze-and-teleport at the top of the table above.
2. **Accumulate `SendNext` rather than resetting it** (`Sync.cs:1106`). `m.SendNext = now + interval`
   gives an effective period of `ceil(interval/frame)·frame`, so today's 20 Hz is **15 Hz at 30 and
   45 fps** and exact only at 60. It also means any higher rate would silently land below the rate
   at which interpolation works at all.
3. **Suppress the correction when the extrapolation was capped.** `Track.At` rewrites `Universe` on
   every branch, and `World.Universe - Track.DelaySeconds - s.Universe > 0` is true **if and only
   if** `MostAheadSeconds` clipped it — an exact detector, no signature change. Removes the
   130 m/frame pose teleport, which lasts up to 49 s behind a warp.
4. **Do not call `TakeOffRails()` when `Pull` writes nothing** (`World.cs:622`), so the deadband at
   `:605` is reachable after the first frame. Without this, item 4 of the 0.9.2 release is inert.
5. **Key the kitten clamp on contact rather than on type** (`World.cs:602`). Off the ground there is
   no traction servo to fight — `KittenServoImpulseBudget` is zeroed outside a fresh terrain contact
   (`PhysicsBubble.cs:2070-2077`) and `StepAirborne`/`StepMmu`/`StepTumbling` all set
   `Active = false` — so an EVA kitten gets a 0.15 m/s correction budget against metres per second
   of relative motion, for nothing.
6. **Carry attitude forward by `Spin`, and extrapolate `VelocityCcf`** (`Clock.cs:206-215`). `Spin`
   is already on the wire and already in body coordinates. The velocity omission is in the same
   `return` and nobody had flagged it: the puppet is commanded at a velocity up to 0.4 s stale —
   4 m/s in freefall — which `Pull` then writes absolutely.

Then, needing the two-game rig before a live server:

7. **Split the correction out of the velocity channel.** Write `VelocityPhys = s.VelocityCcf` and
   apply the position error to `PositionPhys` at a bounded rate. This is the only thing that fixes
   the walking case, per the impossibility above. Safe for kittens without qualification —
   `PartFailure.Detect` returns immediately for a kitten (`PartFailure.cs:47`), so there is no
   crash-tolerance cascade to reintroduce. For a rocket the budget must be **contact-aware and
   ≤1-2 m/s**, not the 20 m/s first proposed: 20 m/s is 333 mm a frame, and the contact solver's
   1.5 m/s recovery cap (`NarrowPhaseCallbacks.cs:182`) against a 100 t stack on a 0.1 m² footpad
   comes to ~130 MPa against a 9 MPa default tolerance. Project the correction one-sidedly along the
   true contact normal from `Vehicle.TryGetTerrainContactNormalBody` (`Vehicle.cs:1409-1420`),
   falling back to `World.DirectionOf`; one-sided because KSA's own precedent
   (`KittenServoPrecomp.ComputeLadderFollow`) removes only the component *into* the surface.

   Worth recording that this is a safety *improvement* for rockets as well: today's
   `draw = off/0.15` writes 6.7 m/s for a one-metre disagreement, which permanently arms the crash
   accumulator (`|v| > 2` is sticky, `PartContactLoad.cs:107-111`) and hands the solver a real
   6.7 m/s to arrest rather than 1.5.
8. **A sender-side relevance rule, then a higher rate for near craft.** The rate is what multiplies
   nearly every artefact above, and 10 Hz with the current 0.15 s delay interpolates throughout.
   But a flat 10 Hz on a 60-craft breakup is 105 kB/s up and 1.6 MB/s out of the server at 16
   players — the load profile that produced the 0.7.0 session. The rule must come first, and it
   belongs on the **sender**: a game knows where its own craft are and, from the tracks it already
   receives, where everyone else's are. The server cannot do it — it keeps no craft position, and it
   could only thin a stream, never raise it.

## What not to do

- **Do not make `SnapM` larger or speed-aware.** The snap is the *good* branch: it sets `draw = 0`,
  so it already writes the owner's velocity honestly. Making it larger hands those frames back to
  the fake-velocity branch — at 390 m in low orbit that is up to **2,600 m/s** of invented velocity,
  which propagates into the orbit every other player sees on the map. If anything it should be
  smaller and contact-aware: a 5 m pose teleport during a docking approach can put one hull inside
  another, and that write is unbounded today.
- **Do not add a bounded slerp with a deadband to the attitude write.** It fights the `Spin` carry:
  once the target rotates at the craft's real rate, any bounded correction lags for ever and never
  catches up — the same trap as the kitten servo. And a 2° deadband against a stale facing servo
  gives a 2° limit cycle at ~10 Hz where today there is an exact pin.
- **Do not make the interpolation delay per-track.** Two fragments of one rocket share a physics
  bubble and collide with each other; drawn 0.35 s apart they generate nonsense contacts. If an
  adaptive delay is wanted for warp or sustained loss it must be one delay for all tracks, and it
  must step rather than slew on a warp change.
- **Do not pin a landed puppet on rails** before the velocity split has been tried. It makes the mod
  the authority on a flag KSA re-derives every step, a wrong pin propagates onto the wire as
  `Landed` (`FullPhysicsEndFrame` only clears the contact flag off rails), and a pinned craft is
  crash-immune while its owner's copy is not.
- **Do not lower the kitten clamp** to get under Bepu's sleep threshold. The body cannot sleep while
  `Pull` takes it off rails every frame, and a lower clamp makes the walking case worse.

## Numbers that did not survive review

| claimed | actual | why |
| --- | --- | --- |
| 57° attitude pop | 46° | the interpolation window absorbs the last ~20%: the pop is `ω × (interval − delay + latency)` |
| interpolation floor 8.7 Hz / 17.5 Hz | 6.7 / 13.3 Hz from the code; **13.3 / 26.7 Hz on a 150 ms link** | the first figures embed an undeclared ~30 ms latency. On a real link **today's 20 Hz does not survive one loss** |
| 930 server sends per burst | ~190 at the player counts in the logs | worst case at `MaxPlayers = 16` presented as observed |
| sub-pixel beyond 10.3 km | 12.4 km at 1080p, 16.5 km at 1440p | KSA's field of view is 50° vertical, not 60° |
| traction servo authority 0.08 m/s | 0.327 m/s per frame as a cap, but **0.051 m/s** at the 0.15 m/s the clamp writes — and the cap only binds above 0.96 m/s | the review assumed 4 substeps; the solver runs one. Neither figure is the one that binds |
| `Rebase` epoch costing 30-650 m | 30-150 m typical | the upper end needs 4.3 km/s of parent-relative speed |
| 5 m kitten gap in 30 s to 8 min | **~21 s of walking** | the gap opens from the servo eating the owner's velocity, not from a correction error |

## What must be seen in the rig before any of this reaches a server

- Two kittens in one bubble, one walking a straight line for a minute, logging `off`, the measured
  tangential speed, the locomotion mode and `Situation` each frame on the watching game. The run
  before the change must show the ~0.24 m/s drift and the 5 m snap; if it does not, the clamp
  arithmetic above is wrong and wants redoing.
- Whether a landed puppet's `IsOnRails()` ever returns true after the first `Pull`. If it never
  does, the latch is confirmed and item 4 is mandatory. If it does, a route has been missed.
- A landed rocket puppet with the two games deliberately disagreeing by 0.5-3 m, watching both logs
  for `destroyed - N parts exceeded their crash tolerance`. This is the only way to settle whether
  the 1.5 m/s recovery cap is survivable for a heavy stack, and it must be clean before the rocket
  branch of item 7 ships.
- A puppet in orbit with its sender deliberately stalled for two seconds, to confirm item 3
  suppresses the teleport and that the drop at `LostSeconds` still happens.
- A docking approach between two games, which is the one case where `SnapM = 5.0` is actively
  dangerous and which nobody has looked at.

`Vehicle`'s own debug window shows Situation, sleep energy and motionless time directly, and
`PartContactLoadDebug` shows the arming state and per-part pressures — better than inferring from
explosions.
