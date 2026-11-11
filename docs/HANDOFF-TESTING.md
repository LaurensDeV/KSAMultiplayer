# Handoff: testing multiplayer

Orientation for picking this up cold. It does not repeat what is already written down -- read these
three first, in this order:

- `docs/TWO-GAMES.md` -- what can be driven with two games, what is worth testing and in what order,
  and how to assert on a world that is never the same twice. The ranked list lives there.
- `docs/WARP-AND-WIRE.md` -- the clock, and testing over a wire that is not loopback. Why timewarp
  wants a trace rather than a frozen snapshot, and what the latency relay is and is not for.
- `errors.md` -- what has actually gone wrong, with measurements. The last eight entries are from
  2026-11-09 and 2026-11-10.

`docs/DESIGN.md` is how the mod works and, in its second half, **what has not been tried in game**,
which is the real backlog. Keep it true in the same change that makes it untrue.

## Where it stands, 2026-11-10

192 tests green (`./tools/test.sh`, no KSA needed).

**Seen working in game, with two games on one machine:**

- The whole editor-launch path, for the first time. A design written out of the world, loaded into a
  blank editor, launched onto a pad, described once, and built once in the other game with matching
  part counts. This is the path that shipped broken in 0.11.0, 0.11.1 and 0.12.0.
- The guard on another player's craft being stripped by this world's physics: a part failure aimed
  at a puppet left it at 89 parts, while the same call on the owner's own craft took it to 75 in
  both worlds. The control is what makes the first number mean anything.
- A crash shedding 16 pieces, all 16 claimed by the owner and all 16 built in the other game.
- The clock keeping up with a room that has run past it, after a warp ends.
- A game at no craft held to the server's time through a warp, where it used to be left 229 s
  behind; a walking kitten's copy 5 cm behind it over 38 m, where it used to fall behind for as
  long as the kitten walked.
- A rocket burning off a pad, coming down on its engine, and hopping to 200 m and landing, each
  followed in the other game to centimetres.
- A docking flown on thrusters, port onto port, and undone again, with the same two craft in both
  worlds after each; a docking with the craft another player is flying refused; a craft one
  player built and another took over followed in its maker's world.
- A rocket falling over into thirty pieces: the same thirty in both worlds, none stepping a metre
  between two frames, and the hull never gone from the other world while it changed shape.

**Fixed, built, tested, and *not* seen in game:**

- Debris off a craft this world had to rename (`Sync.Whose` reading ancestry back through
  `Renamed`). The unit test covers the name walk; nothing reaches `src/Mod`. A breakup makes renames
  the common case, so `tools/breakup.sh` is the run that would show it.

**Explained, and it was the rig:**

- A kitten out of a save drawn torn open in one game. The bridge's `load` read the save before KSA
  had drawn a frame of the world it had just started, and a kitten built then keeps a mangled mesh.
  `load` waits for the world to be drawn now. It was not a part and not a name, and so invisible to
  every check here; `errors.md` has the runs.

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

`status` `craft` `mp` `goto` `watch` `eva` `stage` `setorbit` `engine` `attitude` `land` `dock` `approach` `move` `put`
`design` `fail` `motion` `save` `load` `start` `step` `capture` `pause` `resume` `speed` `modspeed` `pickspeed` `warpkey` `log`.

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

Four more for flying, which no test could do before:

- **`attitude`** -- `{"track":"Up"}` presses the flight computer's button of that name, any of
  KSA's `FlightComputerAttitudeTrackTarget`; `none` hands the craft back.
- **`land`** -- brings a craft down on its engine by lighting and shutting it, since KSA holds a
  throttle to the engine's minimum and that is full for every engine the rig has. `{"climb":120}`
  flies it up that far first; with nothing it answers how the descent is going. It needs a
  thrust-to-weight above about 1.3 for the default `slow` of 1.5 m/s^2; the Rocket has 1.7.
- **`dock`** -- `{"craft":"X","to":"Y"}` docks two craft with a free port each wherever they are,
  through the `DockingEvent` KSA's contact solver raises. KSA moves the lighter onto the port.
  `{"craft":"X","undo":true}` undocks, through the input the port's own menu puts in.
- **`approach`** -- `{"craft":"X","to":"Y"}` flies X onto Y's port on its thrusters and docks
  nothing: the ports touch and KSA does. It targets the port, controls from X's own, has the
  flight computer line them up, tries each translation key to learn what it does, and flies.
  With nothing it answers how far along and across the line it is, how fast, and what the flight
  computer is doing. `put {"port":true}` starts a craft in front of the other's port.
- **`attitude`** with nothing answers what the flight computer is tracking, by which of thrusters
  and gimbals, and how far off it is.
- **`watch {"body":"Earth"}`** -- the camera on a planet, which is a game at no craft.

And a rocket on a pad out of a craft in orbit, which is the only rocket with an engine the save
has to stand on the ground: `design {"save":"Rocket"}`, `mp {"editor":true,"blank":true}`,
`design {"load":"Rocket"}`, `mp {"launch":true}`. `setorbit` puts it back in orbit with its port
free, which is how a world gets a third craft to dock with. A craft the other game owns is taken
by watching it for ten seconds after its owner has gone to another.

**Still out of reach:** placing a part (so part placement, symmetry and staging edits), throttling
an engine below what KSA allows it, a third game (GPU memory), and two machines. Where anything is has an answer now -- `motion` on the bridge and `tools/motion.py`,
with scenes for an orbit, an EVA, two craft meeting, a walk, a drop, a game at no craft with and
without a warp, a launch, a hop, the two dockings and the flown one. It lays the two games' clocks
side by side, compares what each world is left holding, and counts the frames a copy is not the
shape its owner has it, so a craft in one world only, or left in an old shape, fails a run. `move`
holds a craft's translation keys, which walks a kitten; `put` puts a craft beside another or lifts
it off the ground.

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

**What is written to a craft's flight computer from a frame hook is lost.** KSA hands each
physics step a copy and takes it back. `World.Track` keeps the order for `Vehicle.PrepareWorker`,
which is the only place it holds; `attitude {}` reads back whether it did. The launch and the hop
of 2026-11-10 were recorded believing a flight computer was steering them.

**A watcher takes what nobody owns**, so who is watching what decides who owns a craft a scene
left behind. `tools/motion.py` has A fly and B watch; a craft is handed the other way by its
owner going elsewhere and the taker watching it until the server log says it is theirs to take.

**A scene leaves its world behind.** A hop ends with thirty pieces of rocket rolling on the ground
in physics, and a world holding those warps at 1.6x and runs the game that did not make them a
fifth of a second behind. A dock uses up the only two ports the save has. Relaunch before a warp
trace, and before reading any number as the mod's rather than the debris's.

**Relaunching over running games used to fail without saying so**, and may again in some other
script: `tasklist.exe | grep -q` under `pipefail` reads a running game as none. `--stop` first.

**A warp climbs to what was asked.** Asking for 1000x and reading `warp.speed` back as `1.5` is the
first second of seventeen, and reading it back as `70` a minute later is what the room can fly with
what is in it. Always assert on `warp.speed` from `status`, never on what you asked for. And
`status.can` reads `0` whenever a game is comfortable at the speed it is running, which at 1x is
always, so never gate a wait on it.

**Time nothing on the Linux side.** WSL's clock ran two percent fast against Windows's and was
stepped back six tenths of a second every half minute, which is enough to lay one step of a walk
over the next. A game's `status.wall` and the server's trace are the same clock; `time.time()` is
not.

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

1. **A rider in a docked craft.** Two players' craft dock (`--scene dock-theirs`, and flown,
   `--scene fly-dock-theirs`), the craft is whoever docked's, and the other player watches it
   until its owner undocks. They cannot undock or leave. What they should be able to do is a
   design question; undocking asked of the owner's game is the smallest answer.
2. **A breakup takes several crashes to judge.** No two are alike: thirty to forty pieces, and
   the worst of them anywhere from 0.5 to 3 m out depending on what hit what. `--scene hop` four
   times is a measurement and once is an anecdote. What is left is a metre-sized step or three a
   crash, and a hull that is an outdated shape for seconds while it goes on coming apart.
   `--scene fly-dock` and `fly-dock-theirs` rest on the bridge's `approach`, which sat 4 m from
   the port for as long as it was left until its sideways correction was made unconditional: a
   scene that exits 0 with no "docked" line in the server's log has shown nothing.
3. **More than 2% loss.** Everything here has been walked at one datagram in fifty and nothing at
   one in ten. Chat, an ask for a warp and a vote on one are still said once.
4. **The end of a warp with a craft in physics.** The game the clock passes then catches up at ten
   times and not a thousand. `land` and `attitude` make a craft that stays in physics for a
   minute, which the trace could not have before.
5. **A breakup with renames**, the one fix in the tree not seen in game. `motion.py` notices a
   craft in one world only now, which `breakup.sh` could not.
6. **The rest of the walk.** `tools/warp-trace.py` walks nine steps. Not walked: leaving a craft
   mid-warp, which is a different thing now that a game at no craft keeps the clock; joining or
   leaving mid-warp; a save loaded mid-warp; a machine made to look slow.
7. **`breakup.sh` over the wire that loses and reorders**, and `admins.json` planted so a harness
   can sweep between scenes with `mp {"chat":"/sweep *"}` instead of relaunching.

## Open questions

- **What two players docking should do.** It is refused. One craft with two owners is not
  something the mod has: a craft is one player's.
- **Why the meeting once would not settle over the lossy wire.** It has run twice since.
- **Nothing a craft carries that is not a part and not a name is checked at all**, which is how the
  kitten got through. Worth deciding what else falls in that gap.
