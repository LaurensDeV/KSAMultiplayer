# Handoff: testing multiplayer

Orientation for picking this up cold. It does not repeat what is already written down -- read these
three first, in this order:

- `docs/TWO-GAMES.md` -- what can be driven with two games, what is worth testing and in what order,
  and how to assert on a world that is never the same twice. The ranked list lives there.
- `docs/WARP-AND-WIRE.md` -- the clock, and testing over a wire that is not loopback. Why timewarp
  wants a trace rather than a frozen snapshot, and what the latency relay is and is not for.
- `errors.md` -- what has actually gone wrong, with measurements. The last three entries are from
  2026-11-09 and 2026-11-10.

`docs/DESIGN.md` is how the mod works and, in its second half, **what has not been tried in game**,
which is the real backlog. Keep it true in the same change that makes it untrue.

## Where it stands, 2026-11-10

Branch `claude/ksa-multiplayer-handoff-786f03`, pushed to `origin/dev` at `ea17ae8`. Tree clean,
169 tests green (`./tools/test.sh`, no KSA needed).

**Seen working in game, with two games on one machine:**

- The whole editor-launch path, for the first time. A design written out of the world, loaded into a
  blank editor, launched onto a pad, described once, and built once in the other game with matching
  part counts. This is the path that shipped broken in 0.11.0, 0.11.1 and 0.12.0.
- The guard on another player's craft being stripped by this world's physics: a part failure aimed
  at a puppet left it at 89 parts, while the same call on the owner's own craft took it to 75 in
  both worlds. The control is what makes the first number mean anything.
- A crash shedding 16 pieces, all 16 claimed by the owner and all 16 built in the other game.
- The clock keeping up with a room that has run past it, after a warp ends.

**Fixed, built, tested, and *not* seen in game:**

- Debris off a craft this world had to rename (`Sync.Whose` reading ancestry back through
  `Renamed`). The unit test covers the name walk; nothing reaches `src/Mod`. A breakup makes renames
  the common case, so `tools/breakup.sh` is the run that would show it.

**Known broken, not fixed:**

- A kitten out of a save is drawn in one game with hardware standing out of it. Not a part -- both
  games report one -- not a name, and so invisible to every check here. `docs/DESIGN.md` has what
  was ruled out. Unexplained.

## The rig

```bash
./tools/two-games.sh --test       # deploy, two games, a server, both joined. 3-4 minutes.
./tools/two-games.sh --stop       # kill both games, the server and the relay
./tools/two-games.sh --priority   # ask Windows again not to throttle the window behind
./tools/two-games.sh --dir A      # print a profile's folder
./tools/breakup.sh --gate         # is the rig fit to measure? exits 2 if not
./tools/breakup.sh                # fly a rocket apart and count both worlds
./tools/breakup.sh --count        # measure again without flying anything
```

`breakup.sh` exits **0** when every claim held, **1** when one did not, and **2** when the rig never
came up -- which is a different thing and used to be reported as a failure. Artifacts go to
`.two-games-runs/<timestamp>/`: both games' `craft` and `status` dumps, their mod logs, the server
log, and the report.

Settings, all environment variables: `KSAMP_LAG_MS` / `KSAMP_LAG_JITTER_MS` / `KSAMP_LAG_DROP` put a
relay between the games and the server; `KSAMP_TWO_GAMES_SAVE` picks the save (default `GOLF`);
`KSAMP_TWO_GAMES_PLUMES=1` turns plume trails on; `KSAMP_SERVER_ON` forces `linux` or `windows`;
`KSAMP_BREAKUP_QUIET` / `KSAMP_BREAKUP_SETTLE` tune the quiescence wait.

```bash
KSAMP_LAG_MS=25 KSAMP_LAG_JITTER_MS=5 ./tools/two-games.sh --test   # a 50 ms round trip
```

## What a tool can drive

One command into a running game, by profile:

```bash
KSA_USER_DIR=<profile> python3 tools/ksa-mcp/server.py cli <command> '<json>'
```

`status` `craft` `mp` `goto` `watch` `eva` `stage` `setorbit` `engine` `design` `fail` `save` `load`
`start` `step` `capture` `pause` `resume` `speed` `modspeed` `pickspeed` `warpkey` `log`.

`mp` carries the rest as flags: `connect` `disconnect` `name` `password` `chat` `editor` `blank`
`launch` `pads` `warp` `vote` `board`. With no flag it answers as `status` does.

Three worth knowing about because they are new and change what is reachable:

- **`design`** -- `{"save":"<craft>"}` writes a craft standing in the world out as a vehicle save,
  `{"load":"<name>"}` puts one into the open editor. With `mp {"editor":true,"blank":true}` and
  `mp {"launch":true}` that is a craft built and launched with no hand on the mouse, from a game
  that is already flying something.
- **`fail`** -- `{"craft":"X","parts":3}` blows parts off through the same `PartFailureEvent` KSA's
  own G-load path raises; `{"whole":true}` is the crash, shedding debris and destroying the craft in
  one call. This is the **only** way to reach a part failure on the ground, because `PhysicsBubble`
  stands KSA's path down wherever a craft touches terrain, ocean or another craft -- which is
  exactly where the reported failure happened. Aimed at a puppet it is the guard's own test.
- **`start`** -- presses Start on KSA's configuration at start, giving a fresh world holding the
  game's own `Rocket` (89 parts) and `Gemini7` (21). `load` does that first if needed, so it works
  on a game that has only just opened.

**Still out of reach:** placing a part (so part placement, symmetry and staging edits), where
anything *is* (neither `status` nor `craft` reports a position; the closest is `from_flown_m`), a
third game (GPU memory), and two machines.

## Traps

Each of these cost real time. They are not written down anywhere else.

**A Windows game cannot reach a UDP socket bound inside WSL.** In WSL's default NAT mode this kills
any hand-rolled Linux-side server, relay or `tc netem`. `two-games.sh` handles it by publishing
Windows builds; anything you write yourself will not. I lost a round trip to this *after* writing it
down, by running the new relay under WSL.

**`KSA_DLL_DIR` must be set in a worktree**, and `two-games.sh` sends deploy's stdout to
`/dev/null` while MSBuild prints errors to stdout -- so a failed build is completely silent and you
get an old DLL. Prefix with
`KSA_DLL_DIR=/home/laurens/projects/laurens/ksa-game-assemblies/current/dll`.

**`pkill -f <pattern>` matches your own shell's command line** and kills the session (exit 144).
Use `pkill -x -f 'exact command'`, or find PIDs and kill by PID.

**A screenshot can never show a popup.** `capture` hides the interface for 24 frames by design, so a
game blocked behind a dialog photographs clean. Read `status.popups`.

**An orphaned `ksamp-server.exe` holds its own file**, so `cp` over it fails with Permission denied.
`taskkill.exe /F /IM <name>.exe` first, and give the relay a different filename from the server.

**Before any warp test, put both games at a craft.** A game that is not at one is forced to 1x by
`HoldTheClock` and reports its lateness as zero, so it does not take part in a warp at all and
nothing you measure means anything. Check `status.in_flight` and `status.craft`.

**The governor clamps a warp hard.** Asking for 1000x and reading `warp.speed` back as `1` or `2.7`
is normal -- it holds the room to what the slowest machine says it can fly. Always assert on
`warp.speed` from `status`, never on what you asked for. And `status.can` reads `0` until the game
has measured itself, so never gate a wait on it.

**`breakup.sh`'s referee needs both authorities.** The sibling game alone cannot tell "A invented it"
from "B has not received it yet"; the server's roster alone cannot either, because it holds only
*claimed* craft, so every craft of the handed-over world that nobody flies is absent from it in both
worlds and perfectly healthy. Measuring on either one by itself reported six of seven craft in a
clean world as orphans.

## Method that earned its place

**A behaviour fix is not a `fix` until it has been seen in game.** This is in `CLAUDE.md` and it
caught a shipped-looking fix this session: the first warp fix reconciled a gap at the moment the rate
fell, which is not when the gap exists. Both games froze exactly as before on a server carrying it.

**A unit test that sets up the condition by hand tests your theory, not the game.** That same wrong
fix had a green test throughout, because the test wrote the lead in rather than letting a warp make
one. When a test and a game disagree, the game is right.

**Show the test failing without the fix.** Takes one minute: revert the call, run the filter, read
the message. `"the clock gained 0.3 s, wanted at least 399"` is worth more than a green tick.

**Prefer a claim whose healthy value is zero or "both agree"** over any claim whose healthy value is
a number. A zero-expected invariant needs no baseline, no tolerance and no statistics. This is the
rule that organises `docs/TWO-GAMES.md`.

**Say which measurements are controls.** "The puppet stayed at 89 parts" means nothing without "and
the owner's own went to 75".

## What to do next, ranked

1. **The warp trace.** Every fault in the clock family lives in a transition, and `breakup.sh`'s
   freeze-and-compare is blind to all of them. Sample both games and the server at 2 Hz through a
   scripted walk of transitions and assert over the series. `docs/WARP-AND-WIRE.md` has the eight
   assertions and the transitions; the first assertion is written so the bug just fixed fails the
   run.
2. **Reproduce the full 1000x warp case against the fix.** What was verified was a 2.7x warp and a
   51-second overshoot, because the governor clamped it. Same mechanism, a tenth the drama. Needs
   both games settled and reporting a high `can` before the ask.
3. **A breakup with renames**, which is the one fix in the tree that has not been seen in game.
   `tools/breakup.sh` produces the conditions unaided; what it cannot currently do is *notice*,
   because the orphan it would leave is neither a part count nor a missing name.
4. **The `modspeed` invariant** -- the most-repeated failure in this project's history, five separate
   `fix(mod)` commits, and a pure zero-expected claim: a `modspeed` write either reaches the room or
   is refused, never silently sticks. Cheapest high-value item here.
5. **Run everything at 0, 25, 50 and 150 ms.** A claim that holds at 0 and fails at 50 is the
   interesting result.
6. **Plant `admins.json`** so a harness can sweep between scenarios with `mp {"chat":"/sweep *"}`
   rather than relaunching. The rig owns the server's data folder and wipes it per run.

## Open questions

- **What is standing out of that kitten.** Both games show it now, which contradicts the first
  explanation. Ruled out: parts (both report one), the ownership path, the puppet path, and that
  game's rendering (a fresh `eva` kitten beside it is clean).
- **One number per craft for its distance from its owner's last reported state.** The largest blind
  spot here: `docs/DESIGN.md` admits "is the puppet where its owner says it is" has never been
  measured, and today no test can ask.
- **Nothing a craft carries that is not a part and not a name is checked at all**, which is how the
  kitten got through. Worth deciding what else falls in that gap.
