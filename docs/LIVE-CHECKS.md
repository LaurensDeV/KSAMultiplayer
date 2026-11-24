# To check on the live server

What a release put out that tests and the two-game rig could not settle, waiting on a session with
three or more players on `ksamp.gg`. Each item says what to look for and where. Take an item off
once it has been seen, and write what was seen into `docs/DESIGN.md`.

The players' logs are on the server, under `player-logs/<day>/` in the `ksamp-world` volume. Type
`playerlogs [name]` at the server's console for a session still running.

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
