# How KSA Multiplayer works

KSA has no multiplayer to build on: its `KSA.Networking` connects and chats over RakNet and replicates
nothing. So every player runs a whole world of their own, and the mod keeps those worlds alike. The
server runs no world at all, which is why it needs no game and runs anywhere .NET does.

## The pieces

```
  game A ── UDP ──┐                      ┌── UDP ── game C
                  ├── ksamp-server ──────┤
  game B ── UDP ──┘  (no KSA, no world)  └── ...
```

- **`src/Protocol`** is all both sides share: `Wire` (the datagrams), `Link` (anything too big for one),
  `UniverseClock`, `ClockFollow` and `Track` (time), `Udp` (a socket read on its own thread) and `Client`
  (a game's end of it). It has nothing of KSA, so the tests drive a real server and real clients with it.
  The mod compiles these files into its own DLL, so a player installs one assembly.
- **`src/Server`** is `GameServer` and a console program round it.
- **`src/Mod`** is the StarMap mod. `World` is every call into KSA; `Sync` is what a game does each frame;
  `Hooks` are the Harmony prefixes; `Panel` is the window. `Bridge` reads commands from a folder and
  answers in another, so the tools can drive a game; only a developer's install starts it.

Every datagram starts with `KSMP` and the protocol version, so anything else is dropped, and a game of
another version is refused by name rather than misread.

## What the server keeps

- **Universe time.** A clock of its own (`UniverseClock`), set from the world it hands out, running
  while anyone is in the world and held while nobody is. With nobody in it the clock is put back to
  the world's own time: what the last player simulated past the last save went with them, so a clock
  that kept it would make the next player warp through time whose results nobody has, and a session
  that ended before catching up would leave the next one further behind still. `--keep-time` is the
  operator asking for that time anyway. Five times a second it tells every game the time and its rate.
- **The world.** The latest save, zipped, kept on disk in `--data`. It comes from an operator
  (`--world`), from the first player to join an empty server, or as a fresh save asked of the player
  who has been in longest: every `--snapshot-every` seconds, and whenever a player joins a world more
  than 30 s old, the joiner waiting up to 15 s for it.
- **Every player's ping.** A game measures its own round trip to the server and says it with each
  `Ping`; the server passes them all on in the roster, so each game can show what everyone's line is
  like. Held Tab shows that list in game.
- **A save before a player is gone.** What a player did since the last save is in no world the
  server has once they leave, so whoever is still in it is asked for a fresh one. Nothing can be
  asked of the last player to leave: that much is lost, and the clock waits at the world rather than
  pretending otherwise.
- **Who flies what.** A table of craft Id to player. The first player to take a craft nobody holds
  keeps it until they leave, when everything they owned is let go; while they hold it everyone else
  only watches, whether or not they are flying it this second.
- **A name for every craft.** Craft Ids are KSA's own, so two games that make a craft the same way
  make one Id: two players who each send a kitten out of the same capsule both call it what KSA calls
  it. A game says, with each claim, whether it made the craft or found it in the world everyone
  loaded. One it made whose name another player already holds is a second craft, not that one, and
  the server gives it a name of its own and tells the game. The game says how it is built again under
  that name, and keeps flying it under its own.
- **How each new craft is built**: the definition of every craft made since the world was saved, handed
  to every game in it and to each game that loads that world later; and which craft have gone since.
  A new save drops what it already holds.

It passes every state on to every other game in the world, checks a state comes from the craft's
owner, and passes chat on. It refuses a player with another KSA build, one playing a different celestial system (a world of Sol
entire is not a world of Earth and its Moon alone), or one without a mod the world's giver had
enabled, since a save holding a part of a missing mod does not load. A world given by a player
carries that player's system; one an operator imports carries whatever its own `meta.toml` says.

## What a game does

`Sync.Update`, from the frame hook, steps through:

1. **Joining.** A hello, until welcomed or refused.
2. **Seeding**, if the server has no world: when it asks, this world is saved (`GameSaves.MakeUncompressedSave`)
   and sent, and the game plays on in it.
3. **Waiting** for the world, then **Loading** it: written into `saves/Multiplayer` and loaded with
   `GameSaves.LoadSaveGame`, as Fast & Purrious's online race already does. After 30 frames in flight
   it says it has loaded, with its universe time.
4. **Playing**, each frame:
   - **The flown craft is claimed**, when it changes and every two seconds, unless another player
     holds it: then this player is watching it, is told so once, and claims nothing. A game that
     joins loads the world still flying whoever gave it that world, so two players begin on one
     craft every time, and watching is what the second one does until they fly something else.
   - **Craft this game made are adopted.** Anything that appears that was not in the world it loaded
     and was not built from someone's definition is this player's if it is the flown craft or nearer
     this player's craft than anyone else's: a launch, a dropped stage, a kitten out of the hatch. Its
     definition is sent: `Vehicle.SerializeSave()`, written with KSA's own serializer and gzipped.
   - **Owned craft are sent**: the flown one 20 times a second, the rest twice. A state is where the
     craft is in its body's fixed frame, off the physics state (`PhysicsStates.GetStatesCcf`), and in
     its inertial frame, off the ecliptic, with its attitude, spin and whether it is on rails or on the
     ground. A craft whose parts change, as one that stages does, is described again.
   - **Every other player's craft is a puppet.** Built from its definition if this world has none
     (`Vehicle.CreateVehicleFromSaveGameData`, or `KittenEva.CreateKittenFromSaveData` for a kitten),
     one a frame. With physics here it is pulled to its owner's state in `Vehicle.PrepareWorker`, the
     one window where a write to a craft's state survives the frame: put there outright from more than
     5 m, drawn there over 0.15 s nearer. Without physics it is placed once a second at most, and only
     if 25 m out: set down on the ground with `TeleportToLocation`, or given its owner's orbit.
     `Track` draws each 0.15 s in the past between the two states either side, since every world keeps
     the same universe time. Each carries its owner's name over it, within 2,000 km and in front of
     the camera, so whose craft is whose can be seen rather than looked up.
   - **Time is kept** (`ClockFollow`): a gap under 3 s is closed at up to a tenth faster or slower, one
     larger by warping up to 10x, and a world well ahead waits. KSA steps a world by no more than its
     33 ms a frame, so the speed asked makes up a slower frame. The warp keys and pause are held to that
     speed by prefixes on both `Universe.SetSimulationSpeed`.

What nobody owns is left to each world: a craft on rails goes the same way in every one, and a craft
in physics that nobody flies drifts apart from world to world.

Another player's craft cannot be taken here at all: prefixes on the setter of
`Program.ControlledVehicle`, on `KittenEva.BoardVehicle` and on `EVADoor.PerformEvaForSeat` leave
control where it is, turn a kitten away before it climbs in, and refuse to empty it. Without them a
player can still point at someone else's craft and press keys; nothing they did would reach anyone,
but it would happen in their own world, and a kitten pulled out of someone else's capsule would
become a craft of their own.

A puppet is never destroyed by a crash in this world (a prefix on `Universe.DestroyVehicleFromEvent`),
only when its owner's game says it is gone. Otherwise one game's collision would wreck a craft that is
fine in its owner's world, and the pieces would be taken for this player's.

## Transfers

A world or a definition goes as a `Link` transfer: cut into 1,100-byte pieces, each whole in a datagram
under the 1,280 bytes any path carries, sent at 400 KB/s after a first burst of eight. The receiver asks
four times a second for what it is missing below the highest piece it has, and for everything after a
second with nothing; a sender that has heard nothing two seconds after sending everything sends it all
again. Both give up after two minutes. What it carries says what it is in front: a world or a craft,
the craft's Id, its owner and the universe time. A zip from another machine is unpacked only into its
own folder: a name that climbs out of it is refused.

## What has been seen in a game

Two games on one machine, each with a profile of its own, joined to one server: `tools/two-games.sh
--test` sets that up and `tools/ksa-mcp` drives both through the mod's bridge (`src/Mod/Bridge.cs`),
so a run is repeatable and what each game thinks is readable rather than guessed at. Seen working
against KSA `2026.10.25.5707`:

- **Joining, seeding and the handover.** The first game's world became the server's, 31 KB, and the
  second received it, loaded it and reached the shared world.
- **Who flies what.** Claims relayed both ways; each game held the other's craft as a puppet and its
  own as its own.
- **Building a craft from another game's definition.** A kitten the second game sent out on EVA was
  built in the first from its definition and stood where its owner put it.
- **Adopting a new craft.** That kitten appeared as a new craft, was taken as its player's and
  described to the other game, unprompted.
- **Keeping the server's time.** Both the ±10% nudge and the catch-up warp: a game 15 s behind after
  a load was run at 8.4x until it caught up, and both then settled at 1.1x.
- **Two games making one name.** A kitten sent out of the same capsule in both: the second was named
  `Nasley 2` by the server, each game kept flying its own, and each built the other's and stood it
  where its owner put it. The second game holds the other player's under a name of its own
  (`Nasley (2)`), since KSA looks a craft up by its Id and this world now has two of them.

## What has not been tried

- **Placing a puppet without physics.** `SetFlightPlan` on a railed craft and `TeleportToLocation` on
  a moving one are reached in a run, but neither has been checked against where the craft should be.
- **Pulling a puppet in physics.** Two craft were close enough to share a bubble; that the pull holds
  one steady there has not been measured.
- **A craft of many parts.** What has been built from a definition is a one-part kitten, not a rocket,
  and nothing has staged or dropped a stage while another game watched.
- **Catching up by warp with craft in physics.** The catch-up seen was of craft at rest on a pad.
- **Two games across the internet.** Both games and the server were on one machine. WSL in its default
  NAT mode cannot be reached from a Windows game at all, which is why `two-games.sh` publishes a
  Windows server unless WSL is in mirrored mode.

## What it does not do

- **No docking, no shared craft.** A craft is flown by one player at a time. Docking two players' craft
  is untried and probably wrong.
- **Engines are not shown firing** on another player's craft: the throttle is sent and not yet applied.
- **One world, one time.** Nobody warps; a player who wants to wait for a transfer window waits.
- **No authority over physics.** Each game is trusted for its own craft; the server only checks a state
  comes from the craft's owner.
- **Ownership is not kept across a server restart**: it starts afresh, and the first to take control of a craft has it.
