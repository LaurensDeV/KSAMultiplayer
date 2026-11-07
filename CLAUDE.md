# CLAUDE.md

**KSA Multiplayer**: a shared world for Kitten Space Agency, as a StarMap mod (`KSAMultiplayer`) and a
dedicated server (`ksamp-server`) that runs on Linux with no game installed.

- `README.md` is how to play and run a server; `docs/DESIGN.md` is how it works and **what has not been
  tried in game**, which is all of the mod. Keep both true in the same change that makes them untrue.
- `docs/MOD-API.md` is how another mod takes part without either knowing of the other: five names
  on StarMap's event bus, and `src/Mod/Guests.cs` is all of this mod's side of them.
- Testing it with two games starts at `docs/HANDOFF-TESTING.md`: what the rig and the bridge can
  drive, the traps that cost hours, and what to do next. `docs/TWO-GAMES.md` ranks what is worth
  testing, `docs/WARP-AND-WIRE.md` covers the clock and testing off loopback, and `errors.md` is what
  has actually gone wrong, with measurements.
- `src/Protocol` and `src/Server` must never reference KSA: the server runs without the game, and the
  tests drive both over loopback. Only `src/Mod` touches KSA, and `src/Mod/World.cs` holds nearly all of it.
- The mod compiles `src/Protocol/*.cs` into its own DLL, so a player installs one assembly.
- Change `Wire.Version` whenever what a datagram means changes. `Wire.ProbeVersion` is `Ask` and
  `Facts` alone, which directories of other versions read: it changes only when a field already in
  a `Facts` does, never for one added at the end.
- KSA's assemblies are copyrighted and never committed. `Directory.Build.props` finds them: `KSA_DLL_DIR`,
  `Import/`, `../ksa-game-assemblies/current/dll`, then a game install, which is enough on its own:
  `StarMap.API` and `Lib.Harmony` come from NuGet, compile-time only so one assembly still ships.
- The mod needs **StarMap 0.5.3 or later**: its entry class takes `IModMenuService`, StarMap before
  0.5 passes no services at all, and 0.5.3 is the first that runs on KSA 2026.11.10.5801.
- Discord's Social SDK is never committed either: its terms forbid redistributing it. The build finds it
  (`DISCORD_SDK_DIR`, `Import/discord_social_sdk/`, `../discord_social_sdk/discord_social_sdk`) and puts
  its library beside the mod; without it the mod has no Discord. Nor is the Discord application id, which
  Discord's Developer Terms count a credential: the build takes it from `DISCORD_APPLICATION_ID` or
  `Import/discord-application-id.txt`.
- The decompiled game is in `../ksa-game-assemblies/current/src`; `../KSACars/docs/KSA-MODDING-NOTES.md`
  and `KSA-FRAME-ORDER.md` explain the engine, and why craft state is written in `Vehicle.PrepareWorker`.
- A KSA update is the `upgrade-ksa` skill. `KsaBuild` in `Directory.Build.props` is the build a
  server holds players to, and the mod's build fails where the assemblies it found are another's.
  The mod itself pins no build and retargets itself silently, and
  a patch in `src/Mod/Hooks.cs` whose target has moved warns once in the log and is otherwise lost.
- Comment why, never what; state facts, not history.
- Commit messages are Conventional Commits. A behaviour fix is not a `fix` until it has been seen in game.

```bash
./tools/test.sh                     # protocol and server, no KSA needed
./tools/build.sh                    # server and mod
./tools/publish-server.sh [rid]     # one self-contained server executable
./tools/package.sh                  # the mod's zip
./tools/deploy.sh                   # the mod into this machine's KSA, with the bridge on
./tools/two-games.sh --test         # two games and a server on this machine, in one world
./tools/breakup.sh                  # fly one game's rocket apart, and count both worlds
```

The mod has been run in a game, through `src/Mod/Bridge.cs` and `tools/ksa-mcp`. `two-games.sh` runs
a Windows server unless WSL is in mirrored networking mode, because a Windows game cannot reach a UDP
socket bound inside WSL's own network stack.
