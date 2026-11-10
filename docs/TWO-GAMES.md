# Testing with two games

Three passes over the same question — what can be driven, what should be tested, and how to assert
on a world that is never the same twice — then a review whose job was to knock the findings down.
A record, not a plan: some of it is already true, some is a defect found on the way, and the parts
that are neither say so.

## What changed under this

The rig could not start itself until 2026-11-10. The bridge was built in `MultiplayerMod.OnFullyLoaded`
(`[StarMapAllModsLoaded]`), which StarMap does not fire until KSA has finished starting — and the
mod's own start window is what holds that up, so the bridge did not exist at the one window a tool
had to get past. `two-games.sh --test` waited for a bridge only a hand on the mouse could bring
about.

It is now opened in `OnImmediateLoad` beside `Hooks.InstallEarly` and served from `Hooks.BeforePopups`,
the only hook alive at the start window, giving way to the frame hook once there is one. Two layers
sat behind it: `status` threw `[ViewportRegistry] Main viewport is not set yet` because it asked
flight questions with no world, and `load` could not read a save from the configuration popup at all,
because KSA builds the system and the particle pools on the way out of it. `load` now presses Start
with a system chosen, waits for `World.Loaded`, and only then reads the save; `start` does the first
half alone.

So `docs/DESIGN.md`'s "nothing can press those buttons but a person" is stale for the start window.

**And it is stale for the vehicle editor too, which is the more valuable half.** The first draft of
this document claimed the editor could never make a craft from the bridge, because
`mp {"editor":true}` opens it on the craft being flown and `Dispose` only builds when
`ExistingVehicle == null && EditingSpace.Parts != null`. The review refuted it, and the refutation
checks out: the constructor branches on `existingVehicle == null || newVehicle`
(`VehicleEditor.cs:1479`), so with **nothing controlled** `EditorFlag` opens the blank new-vehicle
editor — and the rig already arrives in that state, because the game that is not flying has had
`Program.ControlledVehicle` set to null by its own `ClaimFlown`/`LetGo`. Filling it needs no mouse
either: `VehicleEditor.LoadVehicle(PartTree)` is public (`VehicleEditor.cs:8628`),
`VehicleSave.Load(IViewport)` is public and ignores the viewport entirely
(`UncompressedVehicleSave.cs:67`), and `VehicleSaves.MakeUncompressedSave` is public static
(`VehicleSaves.cs:273`), so a design can be written straight out of the live world and loaded back.

**Those two commands exist now**: `design {"save":"<craft>"}` writes a craft standing in the world
out as a vehicle save and `design {"load":"<name>"}` puts one into the open editor, so with
`mp {"editor":true}` and `mp {"launch":true}` a craft is built and launched without a mouse. That
unblocks the one regression this project has shipped three times. `VehicleEditingSpace.Id = GenerateNewVehicleId()` picks the first id free *in the local
system*, so two identical worlds hand both games the same name — the two-launch collision can be
forced rather than raced. What stays undrivable is narrower than claimed: no part can be *placed*,
so part placement, symmetry, staging edits and the editor's own interface remain out of reach.

A fresh game is still the cheapest world: `start` with no save lands in one holding `Rocket` at
89 parts and `Gemini7` at 21, and `start` then `save` makes a multi-stage test world.

## Defects in the measurement itself

Found while surveying, and worth more than any scenario below, because they make the one existing
end-to-end test lie.

All four are fixed; what each said is kept, because the reasoning is what makes the new numbers
readable.

**`breakup.sh` joined the two worlds on the wrong field.** It built `{c['id']: c for c in craft}`
(`tools/breakup.sh:122`) and calls the set difference MADE UP. But `id` is KSA's *local* name; the
name everyone shares is `key ?? id`, and `Bridge.CraftList` returns `key` separately for exactly that
reason (`src/Mod/Bridge.cs:300-302`). Renames are routine — the server renames on collision
(`src/Server/Server.cs:733`) and a receiving game renames again when the wire key is taken locally
(`src/Mod/Sync.cs:1641`). A renamed craft therefore appears under two different `id`s and is counted
MADE UP **in both directions**. The headline number of the only end-to-end test has a built-in false
positive. Note that `owned` in the same reply is already computed against `key`, so the dump is
inconsistent with its own consumer.

The cause is worse than "renames happen": a breakup makes them near-certain. Both games step their
own copy of the same rocket, KSA names a shed piece `{parent.Id}_{n}`, so A's `Rocket` sheds
`Rocket_1` and B's puppet copy sheds *its own* `Rocket_1` in B's physics — and when A's definition
arrives, B builds it as `Rocket_1 (2)` and records the rename. The scenario `breakup.sh` exists to
measure is the one that guarantees the defect. **And the join must be a multimap, not a dict**: in
that same case B holds two distinct craft under one shared key, and a dict silently drops one —
possibly the very orphan the second headline number exists to count. "Two craft in this world under
one shared key" is itself a third zero-expected invariant worth printing.

**The referee was the wrong one.** "A craft one game holds that the other has never heard of" used
the *sibling game* as the authority, which cannot tell "A invented it" from "B has not received it yet".
The server's owner map is the real authority and is already on the bridge as `status.owners`, with
`craft`, `owner`, `mine`, `here`, `puppet`, `heard_s` per entry. Measure each world against that.

**The server console cannot be reached from the rig — but its commands can.** `two-games.sh` starts
the server with no stdin pipe and without `KSAMP_STDIN=1`, which is what `src/Server/Program.cs:216`
needs to start the reader thread when stdin is redirected, so no console line can be typed. The
first draft concluded that a harness wanting to sweep between scenarios must start the server itself.
It does not: `Wire.Command` runs any console line sent by an **admin player**, and the bridge already
exposes it — `mp {"chat":"/sweep *"}`, with a comment saying as much ("Through the same door as the
keyboard, / and all"). `sweep`, `reload`, `fresh` and `set` all go through that one dispatcher.
Becoming an admin without the console is within reach too, because the rig owns the server's `--data`
folder and wipes it per run: plant the `{Name, Hash, SinceUtc}` entry in `admins.json` and the
plaintext in the profile's `tokens.json`, which `Sync.Connect` sends as `Wire.Hello.Token`. That is
the better route of the two, because `KSAMP_STDIN` exercises a path no player uses.

**`save` lied with the editor open.** It did not consult `World.Refuses()`, so KSA wrote nothing and
the command still answered `ok`. It asks first now.

## What to test, worst first

Ranked by what has already happened, then by damage, then by how silent the failure is.

1. **A puppet's parts stripped off by this world's own physics.** Reported history, not theory:
   `errors.md` 2026-11-07, two reports from the live server. `PhysicsBubble` stands the
   destruction-event path down wherever a craft touches terrain, ocean or another craft, so
   `BeforeDestroy` is never reached by a crash; the guard on `PartFailureEvent.Apply` is new and
   unwatched. The pressure comes from the mod's own `Pull`: a correction of `off / 0.15` writes
   6.7 m/s for one metre of disagreement against the solver's 1.5 m/s recovery cap.
   *Drive:* **directly, which it could not be before.** `PartFailureEvent` is public and its
   `FailedParts` and `DestroyWholeVehicle` are settable, so `fail {"craft":"X","parts":3}` raises the
   same event KSA's own G-load path raises and `fail {"craft":"X","whole":true}` is the crash, which
   sheds twelve pieces and destroys the craft in one call. Aimed at a *puppet* it is the guard's own
   test; aimed at an owned craft it is the crash-debris path. Applied in `Settle`'s quiet window,
   since both halves add to and remove from the world's vehicles. Nothing is simulated around the
   guard -- the call is the one the game makes.
   The slow drive stands as the realistic version: both games stand a multi-part rocket on the ground
   in one bubble, make them disagree by a metre or two, and hold for a minute. The pressure comes from
   the mod's own `Pull`, and a minute of divergent physics is a premise the freeze cannot instrument.
   *Observe:* `parts` for the same key differs between the two `craft` dumps; the watcher's KSA log
   carries `destroyed - N parts exceeded their crash tolerance`.

2. **A renamed craft shedding debris** — which is also where the debris fix really leaks, and the
   scenario the rig produces unaided.
   The first draft of this document claimed a *temporal* hole: that a fragment appearing a frame
   after its parent's death finds no owned ancestor, because the parent leaves `Owned` in
   `SendOwned` (`src/Mod/Sync.cs:1346`) and leaves the server's map on `Gone`. **That premise never
   obtains.** `PartFailureEvent.Apply` calls `PartFailure.ShedDebris(vehicle, 12)` on the line
   immediately before `Universe.DestroyVehicle`, in the same call, so all the debris of one crash
   exists in the frame the parent dies in — and whichever side of `Play` that falls, `Adopt` still
   sees `Owned[parent]`. A test written to that drive would pass and be read as the fix working.
   **The hole is in the key space instead, and it is wider** -- and that is what was fixed:
   `Whose` reads every ancestor back through `Renamed` before looking it up. `Whose` matches against `Owned` and
   `c.Owners`, both keyed by *shared* keys, while KSA derives a fragment's name from its parent's
   *local* `Vehicle.Id`, and `KeyOf` for a brand-new fragment is just `v.Id`
   (`src/Mod/Sync.cs:354`). So for any craft whose local id differs from its shared key — per the
   defect above, most of them in a breakup — `Wire.ShedFrom` walks a chain that appears in neither
   map, `Whose` answers null, and the name rule is bypassed altogether. In the watching game the
   piece then fails the distance test, because it is right beside the puppet, and `Adopt` added it
   to `Seen` and `MadeHere` *before* that test, so it is never reconsidered. A permanent orphan —
   exactly what the fix exists to prevent.
   *Drive:* a craft that has been renamed in the watching game sheds debris there. The existing rig
   arranges this by itself.
   *Observe:* a fragment whose `ShedFrom` chain reaches a name in `status.renamed` rather than one
   in `status.owners`, left `owned: false, puppet: false`. Note that "no `sent the server how … is
   built`" is *not* specific to this — it also fires while `RedescribeEverySeconds` has simply not
   opened yet.

3. **Every guard still installed and still reached.** A prefix whose target moved logs one warning
   and is then simply absent. This has happened: the move to `2026.11.10.5801` changed
   `EVADoor.PerformEvaForSeat` to answer `EvaRequest`, and Harmony refuses a prefix with the wrong
   `__result` type at install. `PinTheSignatures` catches signature changes at build time but not
   `__result` changes and not an inlined target. Assert on the absence of the warning in both logs —
   and note that a run in which a guard was never *reached* is as suspicious as one with a warning,
   which nothing currently distinguishes.

4. **Two games launching from the editor inside the same window.** Two craft want one name and one
   pad; the name case is unrepairable — "KSA's own lookup may have refused the duplicate and left the
   craft out of the world entirely". Three releases shipped with the marking bug, and the pad guards
   in `Hooks.cs` are reached today only by a hand on the mouse. **Drivable**, per the first section,
   with two new bridge commands — and `GenerateNewVehicleId` picks the first id free in the *local*
   system, so two identical worlds hand both games the same name and the collision can be forced
   rather than raced. The cheapest high-risk item on this list once those commands exist.

5. **A save loaded in this game while in the shared world.** The whole local save is claimed as craft
   made here and uploaded into the room. Guard is a single prefix on `DeserializeSave`, never watched
   with two games. *Drive:* B, while playing, `load`s a local save. Correct behaviour is to leave.

6. **A craft described over and over while it comes apart.** `errors.md` 2026-11-07 again: one craft
   built 87 times and destroyed 62 in a session. The `Deferred` mechanism exists because the previous
   fix made the divergence permanent. The ladder half — a player standing on a puppet being rebuilt is
   dropped — is recorded as **not fixed**.

7. **Two players at one launch site, both shedding pieces.** The distance rule is still live for any
   craft whose name says nothing, and at a shared site every piece is near somebody. This is the case
   that originally put another player's debris on the wire.

8. **A craft the server hands over, and the camera that goes with it.** `docs/DESIGN.md` says there is
   no way from here to make the server hand a craft over on purpose. **That is no longer true:** the
   lapse rule plus the bridge's `watch` makes it routine — A claims a craft and looks away, B watches
   it, wait out `lapse-within`. This unlocks the one untested path in `SendOwned`, where a craft
   reaching `Owned` unclaimed recorded the shape it would next be described at as the shape it
   already had, and so was never written down.

**Speed writes that are not the player's word** belong here and not below the line, where the first
draft put them. It is the most-repeated failure in this project's history — five separate `fix(mod)`
commits — the bridge has `modspeed` built specifically to drive it while connected, and the assertion
is a pure zero-expected invariant: a `modspeed` write either reaches the room or is refused, never
silently sticks. It is the cheapest high-risk item in this document.

Below these, in order: warping with craft in physics; a craft followed to another body; holding a
walking kitten; a sender stalled for two seconds; another player's craft taken, boarded or deleted;
one hatch and two games; the broom and the reload with two games in the world; nametags; a puppet's
engines.

**A contradiction between two of our documents, settled by reading rather than by a run.**
`docs/SYNC-SMOOTHNESS.md` says `PlaceAnalytic` can never run; `docs/DESIGN.md` says its two branches
"are reached in a run". SYNC-SMOOTHNESS is right. `Universe.PrepareVehicleWorkers` walks every
vehicle and `TakeUpdateStateIfOrphaned` returns a state exactly when `!HasPhysicsBubble`, after which
`AssignOrphanVehicle` unconditionally merges it into a bubble or rents one — first thing in `Run()`,
every frame. No live vehicle lacks a bubble when the mod looks, so `Sync.Settle` `continue`s before
`PlaceAnalytic` is ever called, and its own first line would refuse it anyway. The only live analytic
branch is `Rebase`. **DESIGN.md should be corrected**, and "which placement path runs for a distant
puppet" comes off the list of things to test.

## Asserting on a world that is never the same twice

The rule that organises everything else: **prefer a claim whose healthy value is zero or "both
agree" over any claim whose healthy value is a number.** A zero-expected invariant needs no baseline,
no tolerance and no statistics. The two numbers `breakup.sh` leads with are already of that kind,
which is why they work at all.

Measure frozen. `pause`, `speed` and `step` are all refused while connected, but a pause *is* a warp:
`mp warp 0` from one game, `mp vote true` from the other, and the server stops its own clock. That
turns "diff two worlds that are both still moving" into "diff two frozen worlds", which removes the
largest class of flake. `modspeed 0` is the tempting alternative and does not work — `HoldTheClock`
overwrites it within a frame.

Four things the freeze costs, all of which the output must state:
it does **not** let a game that was behind catch up, because `ClockFollow.Speed` short-circuits on
`serverRate <= 0` regardless of `behind` — so the two worlds freeze at different universe times, and
`behind_s` must be sampled *before* freezing rather than asserted on after;
a game not at a craft is not frozen at all, since `HoldTheClock` returns early and forces 1x, so
"verify it took" means both games reporting `warp.phase == Running`, `speed == 0`, **and** a universe
time that does not move between two reads;
a missing vote costs up to `--warp-answer` seconds and leaves the world running, so the rig should
pass `--warp-answer 3`;
and thawing has a silent five-second lockout in which a second ask is discarded without an answer.
It is also circular for anything about warping or about speed writes, and it cannot instrument
scenario 1, whose premise is a minute of divergent physics — there it can only be the end point.

Settle by quiescence, never by a sleep. `WATCH_SECONDS=75` is simultaneously too long on an idle
machine and too short under load. Poll until `status.arriving` is null in both, no new
`built` / `sent the server how` / `rebuilding` lines for K seconds, and two `craft` dumps K seconds
apart have the same key set and the same per-craft part counts.

Safe claims, all on the frozen world and joined on `key or id`:

- **Accounted-for.** Every craft is owned here, a puppet here, or present in `status.owners`. The
  residue is the orphan class, refereed by the server rather than by the sibling game. Zero.
- **No double ownership.** `A.owned_here ∩ B.owned_here = ∅`.
- **Nothing is both.** `owned_here ∩ puppets_here = ∅` in each game.
- **Parts agree on a shared craft.** For each craft A owns and B holds as a puppet, `parts` must be
  equal. This is an agreement assertion, not a count, and it is the exact shape of scenario 1.
- **Nothing is invented.** A craft in A's list that is neither in `status.owners` nor owned by A.
- Monotonic: `universe` never goes backwards; `behind_s` trends to zero rather than growing.
- Eventually-within-N, with the time reported as a measurement rather than asserted.

Not safe, however tempting: "both worlds hold 14 craft" (debris count is physics output); "A's ids
equal B's ids" (renames); any fixed sleep; exact definition or destruction counts; part counts of
unshared craft or mid-flight; pixel comparison of captures; anything depending on which game won a
race — assert that *exactly one* claimed a piece, never which.

Three buckets for log lines, because the existing script mixes them. **VOID** (the rig, not the mod):
`ErrorOutOfDeviceMemory`, `KSA has moved a method this mod patches`, any bridge timeout, a non-empty
`status.popups`. **FAIL**: `N faults: KSA Multiplayer is switched off`, any mod `ERROR`, a name in
both `built` and `sent the server how … is built`. **NOTE** (baseline only): refused origins,
`Update task failed`, explosion ceilings, describe counts. Presence fails; absence proves nothing.

## Cost, and where it stops

Three to four minutes of rig before any scenario: two launches serialised by `sleep 25` because two
loading at once fight over the disk and the card, a save load each, 20 seconds between the two joins.
One launch should carry many scenarios, fenced with `mp chat` lines that timestamp the same instant in
all three logs, and swept between. A fresh launch is needed only for settings changes, for anything
that destroys the world, and for anything testing join, handover or the mod's own startup. A third
game is ruled out by GPU memory — a structural limit, not a scheduling one.

What this harness cannot catch, and should say so in its own header:

- **Load.** The failures that shipped were found with three or four players and rockets in pieces.
  A clean two-game run "shows nothing broken, not the race cured".
- **The network.** Loopback has no loss, reordering, jitter, NAT or MTU, and `Udp.Loss` is reachable
  only in-process, never from a real game. When WSL is in NAT mode the rig silently tests a *Windows*
  server rather than the Linux one this repository exists for — the harness must print which it ran.
- **Two machines**, and therefore each player's own line.
- **The vehicle editor**, per the first section.
- **Where anything is.** Neither `status` nor `craft` reports a position, velocity or attitude; the
  closest is `from_flown_m`. So "is the puppet where its owner says it is" — which `docs/DESIGN.md`
  admits has never been measured — cannot be answered by a test at all. One number per craft, its
  distance from its owner's last reported state, would close the largest blind spot here.
- **Rare interleavings**, which a harness that freezes to measure will systematically miss. At
  minutes per run, nothing with a per-run probability below roughly one in ten will ever be seen.

## The smallest honest first step, taken

`breakup.sh` now joins on `key or id` as a multimap, referees against the server's own roster,
fences the run with chat lines, settles by quiescence rather than by a sleep, freezes both worlds
with warp 0 before comparing them, splits the precondition check into a gate that exits 2 rather
than 1, sorts log lines into VOID, FAIL and NOTE, says which server build it tested, and keeps every
dump in a per-run directory. Its claims are all zero-expected or "both agree", so none of them needs
a baseline to be judgeable.

What remains, in the order this document ranks it: scenario 1, the only one with reported history
behind it, which the freeze cannot instrument because its premise is a minute of divergent physics;
the `modspeed` invariant, which is the cheapest; planting `admins.json` so a harness can sweep
between scenarios through `mp {"chat":"/sweep *"}`; and one number per craft for its distance from
its owner's last reported state, which would close the largest blind spot here.
