# To check on the live server

What a release put out that tests and the two-game rig could not settle, waiting on a session with
three or more players on `ksamp.gg`. Each item says what to look for and where. Take an item off
once it has been seen, and write what was seen into `docs/DESIGN.md`.

The players' logs are on the server, under `player-logs/<day>/` in the `ksamp-world` volume. Type
`playerlogs [name]` at the server's console for a session still running.

## Not yet released

- **A watched station keeps its RCS propellant.** With one player flying a station at RATE and
  another near it, the server's saved world should hold the station's RCS tanks at what its owner's
  game shows, after the watcher has saved it and after a restart. Seen in two games with a 5 t
  capsule; not with a station of hundreds of thrusters, or with a world saved by the watcher.

## From 0.41.0

KSA's HUD for a spectator and the lines about a game falling behind were seen with two games on
one machine; what is left needs a room flying something real.

- **The HUD during somebody's burn.** Spectate a player burning: the gauges' throttle, delta-v and
  propellant should follow what that player's own gauges say, a second or so behind, and clicking
  a flight-computer or navball button should do nothing.
- **A copy kept whole while its owner's game lags.** When a player's game falls well behind while
  others watch their craft up close, no watching game should break that craft up: their logs
  should say `spared <craft>, another player's, whose game has said nothing of it lately` rather
  than `no longer sparing`.
- **A game falling behind, and what it does.** When a player's game hitches or falls behind, the
  server's log should say `<name>'s game is N s behind the world` and then `level with the world
  again`, the player's own log `a frame took` or `went N s unsaid`, and each watching game's
  `lost word of <name>'s craft` and `word of <name>'s craft again`. Nothing should come more often
  than once a minute a kind. If a copy breaks up in only one game, that game's log should say
  `no longer sparing` about 30 s before it.

## From 0.36.0

The reconnect, the joiner, Editor Toggle, the map and the vessel list have been seen with two games
on one machine; what is left of them is a busy room. The rest have been driven over loopback only.

- **A quick reconnect.** A player who drops and rejoins within two minutes has their craft back:
  `<name> is back, and has … again` in the server's log, and nobody else took it meanwhile.
- **Joining into somebody's craft.** A player joining a room whose save was flying a craft nobody
  holds is told `you've joined watching …, which isn't yours` and is not put at its controls.
- **Editor Toggle while watching somebody else's craft.** Fly one craft, click a craft another
  player is flying, then Debug > Editor Toggle: the editor should not open, and the player's log
  should say `not opening the editor: <craft> is <owner>'s`. Back in their own craft the toggle
  opens on it as before.

- **Docking with a station you took earlier.** While a player docks with a craft they hold, a
  player who only watches it should not be handed it: no `<craft> is <name>'s to take` in the
  server's log until the docking player is 10 km away, and no juddering of the station in the
  docking player's game.

- **The map while spectating.** Spectate somebody and press M: the map should be centred on their
  craft, and pressing M again should come back to it in the flight view. Spectating from the map
  view should do the same the other way round.

- **A warp nobody can fly.** With a player whose machine is the limit near 1x (beside a big
  station), an ask for 10x should not be put to the room: the asker is told whose game it is.
- **Clear with a game that kept the craft.** After a `clear`, the server's `craft known` should not
  climb back by the count it swept with no `says how … is built` lines.
- **Undocking.** The server's log should say `<name> undocked <craft> from <station>` when a player
  flies away in the half that came off.

- **Flicking through the vessel list.** Switching through craft nobody holds with `[` and `]`
  should claim none of them until one has been flown for three seconds: one `claiming …` line in
  the player's log, not one for each craft passed.

## From 0.35.3

- **Copies of a craft whose owner's game is behind.** A breakup during a warp is what put a game
  10 to 27 s behind on 2026-11-24. The server now names who it is: `the world falls to …: <name> is
  N s behind`. While it lasts, nobody else should see that player's craft fall out of orbit, sit
  under a pad, or appear inside the sea. In the other players' logs, `spared …, another player's,
  from being wrecked (HydrodynamicForces, …)` from a craft just built, and `… m off its owner's
  orbit` in the kilometres, are this going wrong.
- **A copy left coasting with nothing to steer it.** `the pull held it, with no word of now` should
  be rare outside a warp, and no more than one line a second for any craft.
- **The purple editor tint coming off.** A craft whose pilot leaves the server from the vehicle
  editor, and that somebody else then takes, should lose the tint in the taker's game. The rig
  cannot read a tint, so this needs eyes.
- **New vehicle in the editor.** A player who opens a new vehicle while flying a craft should be
  shown flying nothing, without the editor tag, and the craft should be anybody's to take after ten
  seconds. A craft held in the air for the editor this way has not been seen at all.
- **Giving a craft to a player still catching up.** The giver is told it waits, and it changes hands
  by itself once the taker's game is level, with no second press.
- **Nobody in the room twice.** After a dropped connection or a double-clicked CONNECT, nobody should
  show up as `<name> 2` beside themselves. The server logs `<name> had gone quiet, and is joining
  again` when it lets an abandoned join go.
- **A game minimised or frozen.** The game now logs `the game stood still for N s, …, and the
  keepalive spoke K times for it`, which the server also writes as a note. If anybody is let go of
  as `has gone quiet` while that line says the keepalive spoke, the keepalive is not reaching the
  server and that is the next thing to chase. On 2026-11-24 one player was let go of twice inside
  what the keepalive covers, and the rig could not make it happen.
- **Clear Debris** with KSA's "Override Is Controllable" ticked in a craft's debug window: it should
  find the debris the universe manifest lists, and take another player's pieces out of every game
  for an admin.
- **The log flood.** A player's log should no longer be mostly `is under way in its owner's game`.
  Four lines in five of one were, and up to 223 in a second.
