---
name: upgrade-ksa
description: Move this mod onto a new Kitten Space Agency build. Use when KSA has updated, when the mod's log warns that KSA has moved a method it patches, when a server refuses joins over the KSA build, or when the user asks to upgrade/retarget KSA. Refreshes the assemblies and the decompiled corpus, finds the breaking changes, fixes them, re-makes the worlds the server carries, and updates every place the build is written down.
---

# Upgrading to a new KSA build

KSA is pre-release and has **no official code-modding API**. This mod binds to it in three ways,
and only the first is one a compiler can check:

1. **Compiled members** — `Universe`, `Vehicle`, `GameSaves`, the UI helpers. A rename is a build
   error. Loud, and the easy half.
2. **Harmony patch targets**, thirty-eight of them in `src/Mod/Hooks.cs`. A moved method is **a line in
   the log and nothing else**: the mod loads, the window opens, and the thing that patch was for
   silently stops happening. Private targets named by string, and overloads named by parameter
   list, are the ones that go first.
3. **KSA's save folder**, which is this mod's whole payload. A world travels between games as a
   zipped `meta.toml` + `universe.xml`. Nothing compiles against that, so a format change does not
   fail — it hands every joiner a world their game cannot open, and strands the stock worlds the
   server carries.

Read CLAUDE.md and `docs/DESIGN.md` before starting. `../KSACars/docs/KSA-MODDING-NOTES.md` and
`KSA-FRAME-ORDER.md` explain the engine and why craft state is written where it is.

## What this repo does *not* have

Unlike `../KSArmory`, there is **no `ksa-assemblies.lock`, no `ksa-api-diff.sh`, no
`api-surface.sh`, and no `check-docs.sh`**. Nothing fails CI when a build number goes stale, and
nothing enumerates the bound surface for you. The surface is small enough to read instead: all of
it is in `src/Mod`, and `src/Mod/World.cs` and `src/Mod/Hooks.cs` hold nearly all of it.

The mod does not pin a build either. It reads one at runtime —

```csharp
public static string KsaBuild => typeof(Vehicle).Assembly.GetName().Version?.ToString() ?? "";
```

— so it compiles against whatever assemblies it finds and **retargets itself silently**. Nothing
stamps the build it was compiled against: `src/Mod/Build.cs` records only whether this is a
developer install, and the load line in `MultiplayerMod` reports the build it is *running* on. So
unlike `../KSArmory`, which notices the mismatch and turns reporting off, there is no signal here at
all — the docs can say one build while the assembly binds to another. Step 1 is worth doing before
anything else for that reason.

If a check script would help, the honest place for one is a `tools/check-patches.sh` that loads the
mod's assembly and reports which `AccessTools` lookups resolve to null. Step 3 does it by hand.

## What you need

- The new KSA build **installed** (`/mnt/c/Program Files/Kitten Space Agency`).
- A checkout of the private **`LaurensDeV/ksa-game-assemblies`** repository, beside
  `~/projects/laurens/`, holding `current/dll` and `current/src`.
- `ilspycmd` if the corpus has to be regenerated. **Do not upgrade it as part of an upgrade** — a
  different decompiler version rewrites the whole corpus and buries the real diff.

`source tools/env.sh` first, or use the `tools/*.sh` wrappers. A distro `dotnet` is 8.0 and cannot
build this.

### Which assemblies a build actually used

`Directory.Build.props` takes the first of: `KSA_DLL_DIR`, `Import/`,
`../ksa-game-assemblies/current/dll`, then a game install. Those are resolved **relative to the
props file**, so in a worktree under `.claude/worktrees/<name>/` the mirror is not beside the repo
and the build falls through to the installed game. That is usually what you want and is never what
you assumed, so be deliberate:

```bash
KSA_DLL_DIR=/home/laurens/projects/laurens/ksa-game-assemblies/current/dll ./tools/build.sh
```

The corpus and the DLLs must be the same build or step 3 compares the wrong things.

## 1. Confirm the update is real, and find what the repo thinks

There is no version endpoint check here. Ask the three sources directly:

```bash
# what is installed
powershell.exe -NoProfile -Command '(Get-Item "C:\Program Files\Kitten Space Agency\KSA.dll").VersionInfo.FileVersion'
# what the mirror holds
cat ../ksa-game-assemblies/current/KSA_BUILD
# what this repo says it was flown against
grep -rn '20[0-9][0-9]\.[0-9]*\.[0-9]*\.[0-9]*' README.md docs/ errors.md tests/ src/Server/worlds/
```

If all three agree, stop — there is nothing to upgrade.

Write the new build number down. It is needed in five places in step 7.

**Then check StarMap**, because a KSA update usually breaks the loader too and a mod cannot be
tested through a loader that will not start. Its releases name the build they were made for:

```bash
curl -s 'https://api.github.com/repos/StarMapLoader/StarMap/releases?per_page=5' \
    | grep -oP '"(tag_name|body)": "[^"]*' | head
curl -s 'https://api.nuget.org/v3-flatcontainer/starmap.api/index.json'   # what can be compiled against
```

0.5.3 was released the same day as 2026.11.10.5801 and says only "Made StarMap compatible with KSA
2026.11.10.5801". Its `StarMap.API` assembly was **byte-identical in public API** to 0.5.2, so the
mod needed no source change and the `PackageReference` bump was bookkeeping — but the loader beside
the game had to be the new one, and `tools/two-games.sh` takes the folder named exactly `StarMap`
under the Windows home, so that is the copy that has to be replaced. Decompile both and diff before
assuming, since the version alone says nothing:

```bash
ilspycmd -o /tmp/a <old>/StarMap.API.dll && ilspycmd -o /tmp/b <new>/StarMap.API.dll && diff -ru /tmp/a /tmp/b
```

## 2. Refresh the binaries and the corpus

This repo has no sync scripts; `../KSArmory/tools/sync-import.sh`,
`sync-assemblies.sh` and `decompile-assemblies.sh` are the ones to run, against the mirror. Then,
**in the private repository**, set `current/KSA_BUILD` to the new build and commit the DLLs and the
sources **together** in one commit — committing them apart makes the next diff straddle two
commits for no reason.

Do not push yet if you want to read the diff first.

## 3. Find what actually broke

### The patches, which fail with a warning and nothing else

This is the step that matters most here and the one no tool does. Every target in
`src/Mod/Hooks.cs` must still exist, and `AccessTools` answers `null` rather than throwing:

```csharp
if (target is null) { Log.Warn($"KSA has moved a method this mod patches: {otherwise}"); return; }
```

So the mod starts, the panel draws, and — say — nobody's craft is ever destroyed on its owner's
word, with one `WARN` line in `KSAMultiplayer.log` to show it. **Nothing counts the patches or
reports a shortfall to the player.** Read the log after the first launch and grep it:

```bash
grep -E 'KSA has moved|could not patch' "$(./tools/ksa-user-dir.sh)/Logs/KSAMultiplayer.log"
```

List what is patched and check each against the new corpus. There are two install points —
`Hooks.InstallEarly`, which must run before KSA's configuration at start, and `Hooks.Install` —
and each catches around the lot as well as per target, so read for both messages:

```bash
grep -nP 'AccessTools\.\w+\(' src/Mod/Hooks.cs src/Mod/Pads.cs src/Mod/World.cs src/Mod/Bridge.cs
```

Three groups, in order of how quietly they go:

- **Private methods named by string.** `AccessTools` cannot be helped by the compiler at all here,
  so a rename is invisible until runtime: `ConfigOnStartPopup.OnDrawUi` and `SetLocations`,
  `VehicleEditor.SetLocations` and `FinalizeExit`, `VehicleLaunchMenu.SetLocations` and
  `LaunchVehicle`. `src/Mod/Bridge.cs` reaches `FinalizeExit` too, and
  `Program._screenshotCapture` by name.
- **Overloads named by parameter list.** `Universe.SetSimulationSpeed(double, bool)` and
  `(SimSpeed)`, `Celestial.GetLocation(string)`, `KittenEva.BoardVehicle`, `EVADoor.ShowContextMenu`,
  `PerformEvaForSeat`, `SpawnEva`, `Vehicle.AddVolumetricExhaustInstances`,
  `UpdatePlumeTrailEmitters`, and `ImGuiHelper.BeginRegion(ImString, ImGuiTreeNodeFlags, float)` —
  a default-argument change is enough to move that last one. A **new or changed parameter** makes the explicit type list miss, and
  removing one makes an unqualified lookup ambiguous — which throws, which is why `Patch` and
  `PatchAfter` catch per target rather than once around the lot.
- **Private fields read by reflection.** `Popup.Popups`, `Universe._simulationQuickSpeeds`,
  `VehicleEditor._selectedCelestial` and `_selectedLocation`. `src/Mod/Pads.cs` already tries
  `_locationObjects` then `_locations` because KSA renamed that once — a precedent, not a one-off.
- **The `__result` of every prefix that writes one**, which is the one class the compiler cannot see
  at all: `__result` has to be the target's return type exactly, and Harmony refuses the whole patch
  at install when it is not — so the refusal that prefix existed to make quietly stops happening.
  Check each against the new corpus by hand. Going to 2026.11.10.5801 moved two at once:
  `EVADoor.PerformEvaForSeat` went from `bool` to an `EvaRequest`, and `KittenEva.BoardVehicle` to a
  `CrewSeatResult`. Only the first produced a build error, and only because `src/Mod/Bridge.cs`
  happened to call it as well; the second was invisible. Grep for them:

  ```bash
  grep -nP '__result' src/Mod/Hooks.cs
  ```

### What the compiler sees

```bash
./tools/build.sh
```

Fix compile errors against the decompiled sources rather than by guessing. The definitive answer
to "what is this now" is `../ksa-game-assemblies/current/src`:

```bash
git -C ../ksa-game-assemblies diff HEAD~1 --stat -- current/src/KSA/KSA/
git -C ../ksa-game-assemblies diff HEAD~1 -- current/src/KSA/KSA/Vehicle.cs
```

Read the diff for every file defining a type this mod uses, even where it still compiles. A method
that kept its name, its parameters and its return type and changed what it *means* builds
perfectly and is wrong in flight — and in this mod wrong in flight means every other player sees
it.

Pay particular attention to:

- **Reference frames** — `Ecl`, `Ego`, `Asmb`. Craft state goes on the wire; a changed origin makes
  every remote craft land somewhere else, silently and consistently, so it looks like a sync bug.
- **Units** — metres vs kilometres, radians vs degrees, seconds vs ticks.
- **Frame order** — the whole protocol is built on when a write survives. See the contracts below.
- **`Universe`'s save and vehicle lifecycle** — `DeserializeSave`, `DestroyVehicleFromEvent`,
  `GameSaves.MakeUncompressedSave`, `LoadSaveGame`, `Refresh`.

### The named contracts

These are behaviours the mod is built on that **nothing can check** — facts about what the engine
does rather than what it declares. The build succeeds, the 120 tests pass (they load no KSA at
all), and the mod is broken. Read the cited file when the diff touches it and confirm the claim
still holds.

| Contract | Where | What breaks if it moves |
| --- | --- | --- |
| `Vehicle.PrepareWorker` is the **only** window in which a write to a craft's physics state survives the frame — KSA double-buffers it, and a write from a StarMap hook is overwritten before it is read | `Vehicle.cs`, `../KSACars/docs/KSA-FRAME-ORDER.md` | Every other player's craft stops being pulled to its state. The mod looks like it is syncing (it sends, it receives) and nothing moves. This is the single load-bearing fact of the whole mod. |
| `UncompressedSave.FromDirectory` registers a save under **`SaveMetaData.Name`**, not the folder it was read from | `UncompressedSave.cs`, `GameSaves.cs` | `LoadSaveGame("Multiplayer")` logs `save 'Multiplayer' not found` and a join sits on "loading the world" for ever. `World.CallItTheHandover` rewrites the name for exactly this. |
| `Program.OnGameLoaded` runs after `Universe.DeserializeSave`, once a save has gone in | `Program.cs` | The one signal that a world with **no craft in it** has loaded. Without it the loading stage waits for a craft to fly and an empty world never finishes joining. `World.AtACraft` still counts, so a lost patch here only strands empty worlds. |
| `Program.IsLoaded` is set once at boot and **never cleared** | `Program.cs` | It is not a signal that a world is up, however much it reads like one. Do not reach for it when `OnGameLoaded` moves. |
| `Vehicle.AddVolumetricExhaustInstances` and `UpdatePlumeTrailEmitters` are read after the last physics step and before the frame is drawn | `Vehicle.cs` | The one place a puppet can be given the engine numbers a physics step would have left behind. Another player's rocket burns with no plume, no exhaust and no light. |
| `GameSaves.MakeUncompressedSave` writes **nothing** while the editor is open, and nothing again if the folder cannot be replaced | `GameSaves.cs` | `World.SaveWorld` empties the folder first and treats an empty folder as failure, because otherwise last time's handover save is zipped and handed out as the world now — correcting every game to a world minutes out of date. |
| A save folder is `meta.toml` + `universe.xml`, and `meta.toml` records **no mods list** | `SaveMetaData.cs`, `UncompressedSave.cs` | The server cannot learn a world's mods from the world; it takes them from the hello of whoever seeded it. An imported or stock world records `[]` and so refuses nobody. |
| KSA **parses but does not gate on** a save's `version` — it is shown and sorted on only | `UncompressedSave.cs:42`, `GameSaves.cs` | A stock world whose meta names an older build still loads. If a gate appears, every embedded world in `src/Server/worlds/` locks the server out of its own payload. |
| `Popup.Popups` is a private static list; a popup enters it as it is made and leaves once inactive, before the list is next drawn. `SelectSystem.Current` is set only **after** its first frame | `Popup.cs`, `ConfigOnStartPopup.cs` | `World.Configuration` finds KSA's start configuration through the list for this reason. Through `Current` it is a frame late, which is a frame after the mod needed to replace the window. |
| `Universe.SetSimulationSpeed` is reached by every warp key, auto-warp and the pause, and `InputEvents.SimSpeedData.Apply` is the only one of them that is the **player's own word** | `Universe.cs`, `InputEvents.cs` | Either the server's time stops being kept, or this game's own correction is put to the room as a warp request and everybody warps. |
| `ImGuiHelper.BeginRegion`/`EndRegion` and `ConsoleStyle.BeginFooter` bracket the regions and the footer of KSA's start configuration, from inside it | `ImGuiHelper.cs`, `ConsoleStyle.cs` | These are how the mod reaches into a window it does not own: without them the configuration lets a player change the system and game type the server has already settled, and then refuses the join, with no way back out of the window. `BeginRegion` is also overload-sensitive — it is taken by an explicit `(ImString, ImGuiTreeNodeFlags, float)`. |
| The five patches in `Hooks.InstallEarly` must be registered **before** KSA's configuration at start is shown, which is before every hook StarMap offers | `src/Mod/Hooks.cs`, StarMap's load order | Registered in `Install` instead they bind to the right methods and **never run** — the configuration is drawn and dismissed long before a mod is loaded. A StarMap change to when a mod gets control reintroduces this, and it looks exactly like the patches failing. |
| A method short enough to be **inlined** cannot be patched at all, whatever Harmony reports | `Popup.cs`, `Celestial.cs` | `Popup.DrawAll` is patched rather than the three-line `SelectSystem.DrawUi` for this reason, and `Celestial.GetLocation` was already inlined into the editor once. A patch that installs cleanly and never fires is this, not a missing target — so it leaves no warning in the log either. |
| `typeof(Vehicle).Assembly.GetName().Version` is what the mod calls the KSA build, and the server refuses a join whose build differs from the world's | `World.cs:109`, `src/Server/Server.cs:562` | See step 9: this is why a KSA update locks every running server. |

Add a row whenever a fix depends on the engine *doing* something rather than *declaring* it.

## 4. The save format, because nothing compiles against it

The handover is the mod's payload, and it is KSA's own serialisation of a universe. A change there
produces no build error and no test failure: it produces a joiner whose game will not open the
world, or opens it with craft missing.

The cheapest check is a real round trip, which step 8 does. Before that, read for it:

```bash
git -C ../ksa-game-assemblies diff HEAD~1 --stat -- \
    current/src/KSA/KSA/UncompressedSave.cs current/src/KSA/KSA/SaveMetaData.cs \
    current/src/KSA/KSA/GameSaves.cs current/src/KSA/KSA/Universe.cs
```

What to look for, in order of how quietly it fails:

- **A renamed or added key in `meta.toml`.** `World.CallItTheHandover` rewrites the `name = ` line
  by hand and leaves a meta without one alone. A meta that now spells it otherwise is handed on
  unrewritten and the join hangs on "loading the world".
- **A new required file in the save folder.** `Blob.ZipFolder` takes whatever is there, so a new
  file travels for free — unless KSA writes it outside the folder, in which case the world arrives
  incomplete.
- **A `universe.xml` schema change.** The worst case, because an old save may load with craft at
  their defaults. Compare an empty save written by the new build against
  `src/Server/worlds/SolLite/universe.xml`; step 6 replaces them. Read the diff of `UniverseData`,
  `KittenRosterData`, `KittenRosterEntryData`, `CameraData` and `CelestialSystemData` and ask of
  each change whether an element the carried worlds **lack** has a sane default — an added one with
  a default costs nothing, which is why the 5707 worlds still load on 5801.
- **A change to `VehicleData`,** which is the craft definition this mod puts on the wire, built with
  `XmlHelper.AttributeOverrides` and its own `XmlSerializer` (`World.Define`). An element gained or
  lost here changes what a datagram means even though no datagram changed, so it takes a
  `Wire.Version` bump: 5801 dropped `ResourceManager`. The per-player KSA build check does **not**
  cover this, because a world the server carries records no build at all and so gates nobody.
- **A new system, or a renamed one.** `src/Server/worlds/` carries `SolLite` and `Sol`, and the
  server refuses a join whose system differs from the world's (`Server.cs`, `_worldSystem`). A new
  stock system wants a world of its own, or `empty-world` cannot name it.

## 5. Build and test

```bash
./tools/build.sh
./tools/test.sh
```

**A green suite is no evidence at all here.** `src/Protocol` and `src/Server` reference nothing of
KSA by design, and the tests drive both over loopback — so all 120 pass identically whether the
game binding works or is completely broken, which is the exact failure an upgrade produces.
Nothing up to this line has run a single line of KSA.

## 6. Re-make the worlds the server carries

`src/Server/worlds/{SolLite,Sol}/` are empty saves **written by KSA itself**, byte for byte
unedited, embedded in the server so it needs no player to seed it. They were written by one build
and are the one part of this repo that a save-format change strands.

The server must never reference KSA, so they cannot be authored — they have to be made by driving
the game, through the bridge:

```bash
./tools/deploy.sh                       # the mod into this machine's KSA, bridge on
# start a new game in SolLite with nothing launched, then:
echo '{"id":"stock1","cmd":"save","name":"stock"}' > "$(./tools/ksa-user-dir.sh)/Logs/bridge/KSAMultiplayer/in/stock1.json"
```

The command field is `cmd`, not `name`. Copy the resulting save folder's `meta.toml` and
`universe.xml` over the embedded ones, unedited — `World.CallItTheHandover` fixes the name at
load, so `name = "stock"` in the meta is correct and must not be hand-changed. Then repeat in
`Sol`. `StockWorld` finds them from `GetManifestResourceNames()`, so no project file changes.

Check the server still carries them:

```bash
./tools/test.sh    # AServerCanStartWithAnEmptyWorldOfItsOwn, FreshThrowsTheWorldAwayAndHandsOutAnEmptyOne
```

## 7. Write the new build down

Nothing enforces this, so it is the step that rots. Five places:

- `README.md` — the **Status** block, which names the build two games have flown against
- `docs/DESIGN.md` — *What has been seen in a game*, the same claim
- `src/Server/worlds/*/meta.toml` — written by KSA in step 6, not by hand
- `tests/WireTests.cs` — a build string as test data; harmless, but misleading left stale
- `errors.md` — only for a **new** entry; existing ones are dated history and must not be moved
- `CLAUDE.md` and `README.md` again, for **StarMap's** minimum version, when the loader had to move
  with the game. Nothing in `mod.toml` declares it, so that prose is the only place it is written
  down and the only warning a player gets.

`README.md` and `docs/DESIGN.md` are assertions that someone flew it against that build. **Do not
sweep the number through with `sed`**: until step 8 has actually run, the old number is the true
one. Move those two last.

`docs/REVIEW-2026-11-06.md` cites the corpus by `file:line` throughout and goes stale on every
update. It is named for the day it was written and dates itself, so leave it — but do not quote it
as current.

## 8. Fly it

The only step that tests the binding. Two games on this machine, in one world:

```bash
./tools/two-games.sh --test       # a server and two games, both joined, says what each sees
./tools/breakup.sh                # fly one rocket apart; count what it did to both worlds
```

`two-games.sh --test` proves the handover, the join and the claims. `breakup.sh` gives two numbers
that come from the worlds rather than the logs — craft one game holds that the other has never
heard of, and craft nobody owns. Both are zero in a healthy run.

Then look at it, because the counts check the outcome and not the picture. Each of these rests on a
contract above that no tool checks:

- another player's craft **moves**, smoothly, and is where this game puts it (`PrepareWorker`)
- another player's rocket **burns** with a plume and lights the ground (the exhaust hooks)
- a world handed over **loads**, including `empty-world = "SolLite"`, which has no craft at all
  (`CallItTheHandover`, `OnGameLoaded`)
- the **server rows** are in KSA's start configuration, not only in the mod's own window
  (`BeginRegion`, `BeginFooter`)
- a **warp** asked for in one game reaches the other, and a correction does not (`SimSpeedData`)
- the menu bar says the server, green in the world and red out of it

Anything that misbehaves is a finding against the new build and belongs in `errors.md` or
`docs/DESIGN.md`, not buried in the retarget commit as a silent fix.

## 9. Release — and the servers already running

Push the private repository **first**, or a build here resolves a corpus that does not exist yet.

```bash
git -C ../ksa-game-assemblies push
```

Then commit here, Conventional Commits. `build` is the right type for the retarget:

```
build(ksa): retarget 2026.11.10.5801
```

Behaviour a player would notice goes in its own `fix` or `feat` commit. And per CLAUDE.md, **a
behaviour fix is not a `fix` until it has been seen in game** — which step 8 is for.

### Every running server locks its players out

This is the consequence unique to this project, and it is not in the mod at all. The server
refuses a join whose KSA build differs from the world's:

```
the server's world is KSA <build> and you run <yours>: you need the same build
```

A world seeded by a player records **that player's** build (`Server.cs`, `SetWorld(blob, p.KsaBuild, …)`).
So the moment RocketWerkz ship a build, every existing world is stamped with the old one and
refuses everybody who updates — including `mp.ksarmory.com`. Nothing warns the operator, and the
players see only the refusal.

Three ways out, in order of preference:

- **`fresh`** at the server console — throws the world away and hands out the stock empty world,
  which records `empty-world`'s build rather than a player's. Console only — an admin in game is
  told `fresh is the console's alone`.
- **`set ksa-build <new>`**, which makes the operator's word the gate instead of the world's.
- Seed again from a game on the new build.

Say which in the release notes. An operator who does nothing has a server nobody can join, and no
way to tell from inside the game why.

Also change **`Wire.Version`** if what a datagram means changed — and remember the mod and the
server ship together: a server on the old protocol refuses the new mod with
`the server speaks version N and this mod M`, which is correct and still looks like a bug to a
player.

## If it goes wrong

- **The mod loads and one feature does nothing** — a patch target moved. Step 3, and grep the log
  for `KSA has moved` and `could not patch`. This is the default failure mode of this mod and
  nothing else will surface it.
- **Compile errors mentioning types that clearly still exist** — the corpus and the DLLs are
  different builds, or a worktree resolved the installed game instead of the mirror. Set
  `KSA_DLL_DIR` explicitly.
- **A join hangs on "loading the world"** — the save would not open. `save '…' not found` in KSA's
  own log is the meta name (step 4); nothing at all is the loading stage waiting for a craft that
  a world without craft never has.
- **Everything passes and the shared world is wrong** — expected, and exactly what steps 3 and 8
  are for. The tests load no KSA, so they cannot tell you anything about it either way.
- **A huge, unreadable corpus diff** — ilspycmd was upgraded, or the mirrored assembly set changed.
  Both rewrite everything. Redo the corpus with the pinned ilspycmd and the same set.
- **Two games will not both run** — one card, and at the player's settings two copies fill its
  memory. `two-games.sh` turns the graphics well down for this; plumes are off unless
  `KSAMP_TWO_GAMES_PLUMES=1`.
