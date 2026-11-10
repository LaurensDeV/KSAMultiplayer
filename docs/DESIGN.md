# How KSA Multiplayer works

KSA has no multiplayer to build on: its `KSA.Networking` connects and chats over RakNet and replicates
nothing. So every player runs a whole world of their own, and the mod keeps those worlds alike. The
server runs no world at all, which is why it needs no game and runs anywhere .NET does.

## The pieces

```
  game A ── UDP ──┐                      ┌── UDP ── game C
                  ├── ksamp-server ──────┤
  game B ── UDP ──┘  (no KSA, no world)  └── ...

  game A ── HTTPS ── ksamp-directory ── UDP ─> any server, to check that it answers
```

- **`src/Protocol`** is all both sides share: `Wire` (the datagrams), `Link` (anything too big for one),
  `UniverseClock`, `ClockFollow` and `Track` (time), `Udp` (a socket read on its own thread), `Client`
  (a game's end of it), `Srv` (the record that carries a port), and for the server list `Probes` (one
  socket asking many addresses at once), `Listings` (what a directory answers) and `ServerFacts`
  (what a server said of itself). It has nothing of KSA, so the tests drive a real server and real
  clients with it. The mod compiles these files into its own DLL, so a player installs one assembly.
- **`src/Server`** is `GameServer` and a console program round it.
- **`src/Directory`** is the list of servers the mod's browser reads: `Roster` (who is listed, and
  for how long), `Watch` (the probe that checks an announcement before believing a word of it), and
  an HTTP program round them. Nothing of KSA and nothing of the game server either -- it runs on its
  own machine or none, and a world needs no directory. `docs/DIRECTORY.md` is the specification.
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
  than 30 s old, the joiner waiting up to 15 s for it. Only ever from the player it was asked of: one
  arriving unasked is left where it came from, since the world is written to disk with its sender's
  build and mods and everybody else is then held to those, so a single client could otherwise lock
  every other player out of the server and out of each restart of it.
- **Who is in the world.** A game says so once it has loaded, and only a game the world was handed to
  is believed: a player in the world claims craft, counts in every warp vote and, on a server whose
  world came off disk, sets the clock the whole room then follows, and none of that is anyone's to do
  from outside the world. A universe time that is not a number is not a time either.
- **Who may run commands.** `admins.json` in `--data`: a name and the SHA-256 of a token, one line
  per admin. The token itself exists in two places and neither is that file -- the operator's console
  for the moment it is said, and the machine it was said to. So the list is enough to take an admin
  away and not enough to become one, and a server whose data directory leaks has leaked no admin. A
  name has to be connected to be opped, since a token that reaches nobody is only a row in a file;
  the name is then kept up to date from whoever last joined with that token, so revoking admin names
  the player an operator can see rather than the one who was opped months ago.
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
- **The world's speed.** One world has one time, so nobody changes it alone. A game asks, everyone
  else is asked, and it runs only if they all agree; saying nothing is not agreeing, and the ask runs
  out after `--warp-answer` seconds, thirty by default. A pause is a warp like any other, since stopping the world stops it for
  everyone. The warp is then the asker's to steer and theirs to stop, ends if they leave, and another
  cannot be asked for within five seconds of one ending. What makes the games warp together is that
  the server warps *its own clock*, which is the thing they all follow anyway. It gets to the speed
  agreed by climbing, half as much again every second from a step above normal, and comes down
  from it at once; the clock says its speed the moment it changes, and a game takes that as said.
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
entire is not a world of Earth and its Moon alone), or one whose mods are not the world's
exactly. Without one the world's giver had enabled, a save holding a part of it does not load; with
one they had not, that player builds craft nobody else can put together, and all the others see is
a craft arriving that their game refuses, which reads as the mod being broken. A world given by a player
carries that player's system; one an operator imports carries whatever its own `meta.toml` says.

## What a game does

`Sync.Update`, from the frame hook, steps through:

1. **Joining.** A hello, until welcomed or refused.
2. **Seeding**, if the server has no world: when it asks, this world is saved (`GameSaves.MakeUncompressedSave`)
   and sent, and the game plays on in it. One whose world is not fit to hand over says so and waits
   for somebody else's instead.
3. **Waiting** for the world, then **Loading** it: written into `saves/Multiplayer` and loaded with
   `GameSaves.LoadSaveGame`, as Fast & Purrious's online race already does. After 30 frames in flight
   it says it has loaded, with its universe time. A world that arrives while the game is already
   playing puts it back here: that is the operator handing everybody the world again, and a game
   that carried on would be alone in the one the others have left.

KSA saves and loads nothing at all while the vehicle editor is open: both calls begin by asking, and
the answer is a line in its own log and a return, with nothing said to whoever called. So the ask for
a save is held until it can be answered and the world that has come is held until it can be loaded --
the server sends it once -- and the save folder is emptied before a save and read only if something
was written. Otherwise the handover save left there from last time is zipped and handed on as the
world now, and a game counts its thirty frames over a world it never loaded and says it is in the
shared one.
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
     ground. A craft nearer the middle of its body than half its radius is not sent at all and not
     placed from one that arrives: KSA has lost such a craft, no orbit can be made from where it
     says it is, and a game handed a world holding one aborts on its first physics step with
     "cannot construct a UniverseTime from NaN". Finite is not enough -- the centre of the Earth is
     a finite place to be. Nor is such a world handed over: the game asked for it says no and names
     the craft, and the server asks somebody else rather than keeping a world nobody can open. A craft whose parts change, as one that stages does, is described again.
   - **Every other player's craft is a puppet.** Built from its definition if this world has none
     (`Vehicle.CreateVehicleFromSaveGameData`, or `KittenEva.CreateKittenFromSaveData` for a kitten),
     one a frame, and then remembered by name: KSA does not list a craft the moment it is made, so a
     world asked whether it holds one says no for a frame or two and would otherwise be told to build
     it again. A definition whose bytes are the ones already held is ignored rather than acted on, for
     the same reason: tearing the craft down to build it back is how a world comes to hold two of it,
     and KSA throws when a physics step meets one craft twice. The name it is built under has to be
     one nothing in this world answers to, craft or body alike: KSA keeps everything in one lookup
     under a hash of its name and refuses a second thing under one hash with a line in its own log
     and nothing else, handing back a craft that is in no lookup -- never listed, never stepped,
     never swept, and another player's puppet standing here for ever. With physics here it is pulled to its owner's state in `Vehicle.PrepareWorker`, the
     one window where a write to a craft's state survives the frame: put there outright from more than
     5 m, drawn there over 0.15 s nearer. A kitten on the ground is drawn there no faster than
     0.15 m/s while its owner has it standing, because the speed it is drawn at is the speed KSA
     animates its legs at, and by as much as half the speed its owner says while it is being
     walked: its legs are moving already, and held to the standing correction its copy fell 0.18 m
     further behind every second of a walk. A copy is put where it is told at its first step
     after being built, before it is drawn, having been built where it stood when it was described. That takes only on a craft something is pushing. One that
     is coasting is stepped along its own orbit by KSA whatever is written into it, which is right to
     the millimetre while that orbit is its owner's; where it is not, the craft is given its owner's
     orbit through KSA's own mover, in the frame's quiet window, and at once when its owner stops
     pushing it or when the pull has had to put it anywhere outright. Without physics it is placed once a second at most, and only if 25 m out.
     `Track` says where each is at this world's own universe time, since every world keeps the same
     one: carried on from its newest state by the speed it had and the way that speed was changing.
     Not a little in the past, which is smooth and at orbital speed is a kilometre away from every
     craft this game steps for itself; `errors.md`, 2026-11-10. The one a player is in carries their name, within 2,000 km and in front
     of the camera; the rest of what they own carries a faint `Alice's Lander` within 20 km, which
     is also why it will not let you aboard. A name over the world says where somebody is, and a
     player is in one place: ownership is sticky and a player is not, so naming every hull somebody
     has flown this session states as many falsehoods as it takes to tell one truth. What they are
     in comes from the roster's own word, the same the scoreboard reads, so the two cannot differ.
   - **Time is kept** (`ClockFollow`): a gap under 3 s is closed at up to a tenth faster or slower, one
     larger by warping up to 10x, and a world well ahead waits. KSA steps a world by no more than its
     33 ms a frame, so the speed asked makes up a slower frame -- and that make-up is carried by the
     ceiling, or a game slower than 30 frames a second loses universe time every second it runs. The warp keys and pause are held to that
     speed by prefixes on both `Universe.SetSimulationSpeed`, and a speed asked for that is not that
     one is put to the others instead of taken -- but only when the player asked for it. **Almost nothing
     that writes the speed is a player.** KSA puts it to 1 when the editor closes, when the flight
     computer finishes, when a space weather alert fires and when a craft is wrecked; to 0 for every
     menu, and back again on the way out, back to whatever this mod last wrote. Another mod writes it
     too: a weapons mod holding the world down while a round is in the air writes it every frame of
     every shot. So the one path that carries the player's own word is the one that counts --
     `InputEvents.SimSpeedData.Apply`, where the pause and a speed bound to a key arrive -- and a
     write that did not follow it is held quietly and told to nobody. The warp keys never reach a
     setter at all: `Increase`, `Decrease` and `Reset` are taken whole. **A menu does
     not stop this world**: Escape, the settings and the save lists all ask KSA to stop it, and that
     ask is answered with the shared speed instead and told to nobody -- a craft that stops when its
     pilot opens a menu stops for nobody else, so the player would come back behind the room and
     every other game would have had their craft frozen at one universe time and then let go of it.
     The editor is the exception, because nothing is being flown there: the clock is let go of at
     real time and not at whatever this world was last run at, or the catch-up warp from a join goes
     on running in the editor with nothing holding it to the server, and the world comes back further
     ahead than it went. **Being at no craft is not an exception.** A game whose camera is on a
     planet, or whose kitten has died, flies and watches nothing and still draws everybody else's
     craft at its own universe time, so it is held to the server's like any other: let go of there,
     a warp of fifty for twelve seconds left it 229 s behind the room for good. A warp key
     pressed from a menu that has stopped this world is taken and ignored, the same as one at either
     end of the ladder: nobody is in this player's menu, and asking the room to warp while not
     running at it oneself is not a question worth putting.

What nobody owns is left to each world: a craft on rails goes the same way in every one, and a craft
in physics that nobody flies drifts apart from world to world.

Another player's craft cannot be taken here at all: prefixes on the setter of
`Program.ControlledVehicle`, on `KittenEva.BoardVehicle` and on `EVADoor.PerformEvaForSeat` leave
control where it is, turn a kitten away before it climbs in, and refuse to empty it. Without them a
player can still point at someone else's craft and press keys; nothing they did would reach anyone,
but it would happen in their own world, and a kitten pulled out of someone else's capsule would
become a craft of their own.

A puppet is spared when a crash in this world would wreck it -- a prefix on
`Universe.DestroyVehicleFromEvent` refuses the destroy along with the explosion and the twelve pieces
that go with it -- and is taken away only when its owner's game says it is gone. Otherwise one game's
collision would wreck a craft that is fine in its owner's world, and the pieces would be taken for
this player's.

A craft waiting to be taken out of this world is spared the same way. One its owner says has
crashed is no puppet for the frame it is told in, and the crash this world's own physics had ready
for it went through in that frame: its pieces were shed here under the names its owner's pieces
have, and stood in for them.

Docking is the same rule. KSA docks two craft in the world whose physics saw their ports touch, by
taking the lighter into the heavier, or from a port's own menu when two are within 2 m; both end in
`DockingPort.Dock`, and a prefix there refuses it where either is another player's, as one on
`DockingPort.Undock` refuses to take another player's craft apart: let through, one player's rocket went into a copy of the other's and was gone
from every world, while its owner flew on with the parts they had. A craft docked with one nobody
flies is docked, and the craft that went into it is claimed so it can be said gone -- only its
owner may say so, and it had none.

A craft a game made is never taken on another player's word while its name is unanswered, because
another player's craft can be standing under that name; once the server has said the craft is this
player's, the name is the craft's, and whoever is given it next is followed like any other owner.
And the server gives a name that has gone to a craft a game has just made, and to nothing else:
KSA hands a destroyed piece's name to the next piece shed.

That prefix covers one of KSA's two ways to wreck a craft, and not the one a crash takes, so there
is a second on `PartFailureEvent.Apply`. Parts past their crash tolerance on contact fail there, and
`PhysicsBubble` stands the G-load path down in its favour wherever a craft is touching terrain,
ocean or another craft -- so a crash raises no `VehicleDestructionEvent` and the first prefix is
never reached. The two are not the same wreck, either: under half a craft's part count that path
explodes and isolates the failed parts one at a time and the hull flies on without them, which a
destruction event cannot express, and only at half or more does it take the whole craft. Unguarded
it blew parts off a rocket that was whole in the world that owned it and told nobody, a game having
nothing to say about a craft it does not own, so the two players watched one rocket and disagreed.
Refusing `Apply` wedges nothing: the caller nulls the event on its next line whatever the prefix
answers, so the next frame raises another and has it refused too.

## Transfers

A world or a definition goes as a `Link` transfer: cut into 1,100-byte pieces, each whole in a datagram
under the 1,280 bytes any path carries, sent at 400 KB/s after a first burst of eight. The receiver asks
four times a second for what it is missing below the highest piece it has, and for everything after a
second with nothing; a sender that has heard nothing two seconds after sending everything sends it all
again. Both give up after two minutes. What it carries says what it is in front: a world or a craft,
the craft's Id, its owner and the universe time. A zip from another machine is unpacked only into its
own folder: a name that climbs out of it is refused.

## Commands from inside a game

A line typed into the chat behind a `/` goes to the server as a `Command` rather than to everyone as
a `Chat`, so it is said to nobody whether or not it runs. The server runs it only for a player whose
`Hello` carried a token on the admin list, and hands it to the console program through `Asked` -- the
same dispatcher the console's own standard input feeds, so there is one list of commands and one place
each is implemented. The answer goes back as a `Chat` from the server to that player alone, which their
game shows in the chat window they typed into.

`quit` is the exception the dispatcher makes for itself: an admin asking for it is told it is the
console's, because a server nobody can start again is not something to hand out to somebody who meant
to leave the world rather than end it.

Being an admin is a machine's, not a name's. `op` makes 32 random bytes, keeps their hash and sends the
token itself once; the game writes it beside that server's address and offers it in every `Hello` after.
Nothing is asked of the player, and nothing they could be shoulder-surfed typing. Revoking it removes
the hash, so the token stops working at once even for a game that still has it, and tells a game that is
connected to throw it away -- which is said by `Op(Admin: false)` and needs nothing else on the wire.

None of it is secret from somebody watching the line. A token goes out in the clear in every `Hello`,
as the password does, so it is worth what the network between the game and the server is worth: enough
to keep an admin from being typed in by a stranger who guessed a name, not enough to stand up to
somebody reading the datagrams. Taking one back is a `deop` away, which is the answer to a token that
has got out.

**A name can be taken.** `op` and `deop` name a player by what they chose to be called, and whoever
joins first holds a name: a stranger who joins as the name an operator is about to type gets the
token, and the operator's admin becomes "name 2". A roster holds only one of a name however it is
capitalised, so the verbs are at least unambiguous about which player they mean -- but they mean
whoever is holding the name, not whoever deserves it. On a server where that matters, set a password.

**The server carries empty worlds it cannot make.** A world is a KSA save -- a `meta.toml` and a
`universe.xml` -- and this half of the project never references KSA, on a machine with no game
installed, so there is nothing here that could write one. One empty save per system was made once in
the game instead and is carried as bytes: `src/Server/worlds/<System>/`, embedded, zipped on demand
into the shape a player's handover has and sent out the same way. Nothing reads inside them. Under a
kilobyte each, because an empty universe is a camera, a system id and some ground clutter. What it
costs is a system and a version: the system a world started from one is in for ever, which is why
there is one per system and a setting to pick rather than one assumed, and the save says which KSA
build wrote it, so each goes stale when the format moves on. `fresh` puts one in place of the world
there is -- every craft dropped, the universe clock forgotten so the next load says the time, and
everybody handed the new world where they stand.

**A world is called what the folder it is in is called.** KSA registers a save under the name in its
`meta.toml`, not the folder it was read from (`UncompressedSave.FromDirectory` registers
`SaveMetaData.Name`), so a world unpacked into the handover folder and loaded by that folder's name
is only found if its meta happens to agree. One handed over by another player agrees, because the
game that wrote it wrote it under this same name; one imported by an operator or carried by the
server agrees by luck. The name is rewritten to match the folder as the world is unpacked.

**A stalled game is still a game.** Everything a client sends goes out of `Update`, on KSA's own
thread, so a frame that runs long -- a rocket being rebuilt, a scene loading, a machine thrashing --
sends nothing at all, and ten seconds of that was a player the server dropped out of a world they
had never left. The stall is one-sided: datagrams coming the other way are stamped by the reader
thread and found waiting afterwards, so a game never gave up on a server, only a server on a game.
A thread of the client's own now sends `Alive` -- empty, carrying nothing a stalled frame loop could
not vouch for -- once two seconds have passed with the frame loop silent. It speaks for a stall of
up to a minute and then stops, so a process that has hung does not hold a seat and a rocket in the
world for as long as it exists; past that the server's own silence takes over, which is what used to
happen the moment a frame ran long. `Udp.Send` takes a lock for this, since two threads shared one
scratch buffer.

**A game that drops and comes back** does not match the session it left, because its socket takes a
fresh port each time it connects and a session is held under its address. A session that has not been
heard from for three seconds is let go of when a game joins under its name, which is what makes a
reconnect get its own name and its own craft back rather than becoming "name 2" and watching its own
rocket for ten seconds -- or two minutes, when the drop was mid-load.

**What an admin is told is not what the console is told.** `players` names everybody, what they are
flying and which KSA build they have, but where they are from only in the server's own console: an
admin is a player like any other, and being trusted to sweep debris is not being trusted with
everybody's address.

**An address with no port is asked about before it is dialled.** `Srv` queries
`_ksamp._udp.<name>` and takes the host and port the lowest-priority record gives, which is how one
machine holds several worlds under names of their own: an address record cannot carry a port, and an
SRV record is the only one that can. Written by hand, because .NET resolves A and AAAA and has no
SRV at all, and the library everyone else uses is a second assembly this mod will not ship. It is a
hint and never a requirement -- no resolver, no record, a mangled packet, or a target of `.` all end
as the name and port 50100, which is what happened before any of it existed. A port typed after the
name skips the question: that is the player being specific, and somebody else's zone does not get to
overrule them.

**A hello from an address the server has not heard from is answered with a cookie and nothing else.**
Only a hello that says it back holds a place, since a datagram's address is whatever its sender wrote
there.

**What the debug menus can and cannot do to somebody else's craft.** The universe manifest (F11)
deletes, recovers and clears debris by putting a craft in `InputEvents.VehicleDestroyBuffer`, and
that is refused for another player's craft -- a prefix on `VehicleDestroyData.Apply`, beside the two
on `Universe.DestroyVehicleFromEvent` and `PartFailureEvent.Apply` that spare it from being wrecked.
The buffer and not
`Universe.DestroyVehicle` which it ends in: that call is also how KSA frees breakup slots and expires
debris, and how this mod takes a craft away, so refusing it wholesale would leave a game unable to
clean up after a breakup.

A craft of a player's own is theirs to delete, and that *is* shared: the sending round sees the craft
has gone and says `Gone`, the same as any other way of losing one. Deleting another player's craft
was not shared and never could be -- the owner's game knows nothing of it -- which is why it is
refused rather than relayed.

**The debug teleport cannot move somebody else's craft, and does not need its own refusal.**
`Vehicle.DrawPhysicsDebugWindow` offers "Teleport To CCSFS LC-39A", a few other fixed places, and a
"Teleport To..." window: a pan-and-zoom heightmap of any body, which reads as a map view and
teleports the craft on a double click. It draws under
`if (Program.ControlledVehicle == this && Program.ShowPhysicsDebug)`, and the prefix on the
`Program.ControlledVehicle` setter refuses another player's craft -- so the window never appears for
one, and neither its buttons nor its map have anything of theirs to act on.

That double click is the only one in the game that moves a craft. Of KSA's eleven, the rest select a
row in a save, layout or kitten list, zoom a spline editor, place a terrain decal's point of
interest, or -- in the universe manifest -- point the camera at a body with
`camera.SetFollow`, which is watching and needs no refusal.

Teleporting a craft of this player's own *within* a body is shared like any other way of moving it:
this game's states are what every other game follows, so it simply arrives somewhere else.

Teleporting it to **another body** was not, and this is worth knowing because it is the one thing
nothing else in the mod could follow. The state says which body a craft is going round, and the pull
stands aside when this game's copy says a different one -- it has no coherent frame to write in --
while the analytic placing writes a flight plan without rebasing the craft's physics origin, which a
bubble reverts on its next step or reads as the old body's coordinates against the new one. So the
copy stayed where it was, for ever: a craft on a launchpad is inside the Earth's physics radius,
where KSA's own SOI check gives up on its first line, so it could never follow by itself either. A
change of body is now handed to `Vehicle.Teleport`, which is what KSA uses for the same job: it
takes the craft out of its bubble, rebases the origin, and leaves it for the solver to pick up round
the right body.

The same held for an ordinary flight to the Moon, for anyone who joined afterwards. A definition
carries the body it was written at, and it was only sent again when a craft's part count changed --
so a late joiner built the craft back at the Earth and nothing moved it. The body is part of what
counts as a change of shape now.

The one gap left is a frame's worth: a craft this game is controlling before the roster says who
owns it is not yet a puppet, so a teleport then would move it here alone -- and the pull drags it
back within the second. Not worth a flag, given that this mod places landed craft through
`Vehicle.TeleportToLocation` itself and a refusal would have to let its own calls through.

## What has been seen in a game

Two games on one machine, each with a profile of its own, joined to one server: `tools/two-games.sh
--test` sets that up and `tools/ksa-mcp` drives both through the mod's bridge (`src/Mod/Bridge.cs`),
so a run is repeatable and what each game thinks is readable rather than guessed at. Seen working
against KSA `2026.11.10.5801` with StarMap 0.5.3:

- **Joining, seeding and the handover.** The first game's world became the server's, 31 KB, and the
  second received it, loaded it and reached the shared world.
- **An empty world the server carries.** A server started with `empty-world = "SolLite"` handed its
  own 977-byte world to a game that had asked nobody for one, and the game loaded it and reached the
  shared world in about a second. Two things had to be fixed before it would, and neither was about
  the save: KSA lists a save under the name its `meta.toml` gives rather than the folder it is in,
  so a world whose meta said anything else was written to the handover folder and then not found in
  it; and the loading stage waited for a craft to fly, which a world with nothing in it never has.
- **Who flies what.** Claims relayed both ways; each game held the other's craft as a puppet and its
  own as its own.
- **Building a craft from another game's definition.** A kitten the second game sent out on EVA was
  built in the first from its definition and stood where its owner put it.
- **Adopting a new craft.** That kitten appeared as a new craft, was taken as its player's and
  described to the other game, unprompted.
- **One game's rocket coming apart, and what the other is left holding.** `tools/breakup.sh` stages
  it and reads both worlds. A staged the 89-part `Rocket` to pieces; B, which only watched, came out
  of it holding fifteen craft A had never heard of and twenty-eight of its thirty-seven craft
  neither its own nor anybody else's, while A had none. Nothing exploded: the pieces are shed as the
  stack is remade under B, not by any impact.
- **Reading a name as saying what the craft came off, and taking the other player's pieces away.**
  The same run with the fix in: B took away 34 pieces its copy of Alpha's rocket had shed and A took
  away none, and the two worlds came out holding the same 25 craft, nothing made up on either side,
  with the four the GOLF save itself leaves unowned the only craft neither game claims. Over a
  second, longer round it held, and no craft thrashed between being taken away and built again.
- **Keeping the server's time.** Both the ±10% nudge and the catch-up warp: a game 15 s behind after
  a load was run at 8.4x until it caught up, and both then settled at 1.1x.
- **Catching up by warp with craft in physics, and what it costs.** Four players on 0.7.0: while
  rockets were coming apart the warp ran worlds ahead of their own physics bubbles by up to 23 s,
  and KSA refused 327 origin updates across 130 craft for being in their bubbles' future. The
  headroom ceiling that answers it has not been in a game yet.
- **Warping together.** A vote asked of both games, the other player's prompt, agreement, and two
  worlds running at 5x and keeping within a twentieth of a second of the server's time; a warp
  refused by the player who did not start it, ended by the one who did; and a pause voted through,
  with both worlds stopped at the same universe time and still there five seconds later. Coming out
  of a warp overshoots by about two seconds and is nudged off at 0.9x.
- **Where another player's craft is, against where its owner has it.** `tools/motion.py` has each
  game write down every craft as each frame is drawn and at every physics step, and lays the two
  accounts side by side on universe time. On loopback: a craft in orbit at 7.2 km/s is drawn 3 mm
  from where its owner has it while coasting and within 5 cm while burning; a kitten out of its
  hatch is 3.64 m from the capsule in both games, to 4 mm; two craft put 40 m apart are 40 m apart
  in both, to 8 mm; a kitten falling 150 m is followed to 3 mm and one landing from 3 m to 81 cm
  for a moment; a game that has just joined draws it all to 3 mm while its clock catches up; and
  nothing is put anywhere outright and nothing steps a metre between two frames. Two craft meeting
  with one of them burning agree in the two games to 30 cm at 50 ms each way and to 1.7 m at 150.
  Every one of those was a kilometre before, or two. A kitten walking 38 m is followed to 5 cm.
  A rocket burning 35 s off a pad to 214 m/s is followed to 2 mm, and to 4 cm coming down from
  6 km on an engine lit and shut; one hopped to 200 m and landed on its engine, to 15 cm over a
  wire of 50 ms each way that loses one datagram in fifty. The same wire carries the meeting, to
  12 mm.
- **A game at no craft through a warp.** Its camera on the planet while the other game flew and
  warped at fifty: 9 ms from the other game's universe time when it ended, and never more than
  half a second through it. `tools/motion.py --scene nocraft-warp`.
- **A docking, flown and undone.** A capsule flown onto a rocket's port on its thrusters, lined
  up by its flight computer, docks on contact: 110 parts in both worlds and the capsule in
  neither. Undocked, it is 21 and 89 in both, under the names they had. The other game draws the
  capsule within 22 cm through the approach, 8 cm over a wire of 50 ms each way that loses one
  datagram in fifty. Docked with the capsule the other game was flying, nothing happens in either
  world and the player is told why; the bridge's own `dock` is the control, and docks when nobody
  flies it. `tools/motion.py --scene fly-dock`.
- **A craft whose shape changes** -- a stage dropped, a docking -- is the new shape in every
  other world within a frame or two of its being described, and not gone for five seconds first.
- **A craft one player launched and another took over** is built again in its maker's world as
  its new owner's and follows it there, through a burn to 3 cm.
- **A rocket falling over into thirty pieces** leaves the same thirty in both worlds, each drawn
  within 0.8 m of where its owner has it while it tumbles, with no step of a metre between two
  frames and nothing put anywhere outright; 1.9 m and one step of 1.5 m over the lossy wire. The
  hull is in a shape its owner has left for two seconds at the longest.
- **A walk of warps, sampled.** `tools/warp-trace.py` asks both games what they are doing twice a
  second while the server writes its own clock down, and judges the whole series: nine steps from
  nobody asking to 1000x and back. Seen holding on loopback, every claim: no game stopped while
  the clock ran, no time went backwards, the games and the server never named different rates, an
  ask refused or unanswered never ran, the other player could neither steer nor end a warp, a speed
  written by a program never reached the room and a speed picked by a player did, three presses of
  the faster key in a second raised what was asked three times, a warp asked at 1000x climbed
  there in seventeen seconds and held it with both games within 0.02 s of the clock, and at its
  end both were about twenty seconds past a clock that went on to them, and neither stood still.
  It took a day of that walk failing to get there; `errors.md` has each failure and
  `docs/WARP-AND-WIRE.md` what is walked and what is not. The same walk held at 25, 50 and 150 ms
  each way through the server's own relay, and over a wire holding datagrams 50 ms give or take
  20 and dropping one in fifty, once what each of those found had been put right.
- **Another player's engines, burning.** Alpha lit an 89-part rocket in orbit, 7,576 km from the
  craft Bravo was standing by. Bravo's game was not stepping it in physics and KSA gave it no duty
  cycle at all, so the mod wrote one: Bravo's bridge reported `engine_duty 1` with
  `engine_drawn_here true`, and the picture from Bravo's camera has the plume, the exhaust and the
  light on a craft it is not simulating. Shut the engine in Alpha and the same craft in the same
  camera was dark within the second, `engine_duty 0`. Alpha's own copy of it read `engine_duty 1`
  with `engine_drawn_here false` throughout -- the mod leaving a craft KSA is stepping to KSA's own
  numbers, which is the other half of the rule.

  It also corrected the mod. A craft whose ignition was on with nothing behind it -- no propellant,
  nothing staged -- was reported to the others as burning, because what was sent was the ignition
  and not the burn. It is the nozzles' own duty cycle now: that craft reports its engines cold, and
  one that is really firing reports them lit whoever opened the valve, which is what a flight
  computer running a manoeuvre node does without ever setting the flag.
- **The broom, and the world handed out again.** One game, a server and a rocket staged twice: the
  spent stages broke into fragments on their own, and `clear Rocket_*` took the game from 11 craft
  to 7 with none of the debris left, while the craft being flown stayed. The server swept the four
  it knew of, asked for a save, was handed a 33 KB world without them and gave it straight back;
  the game said "the world has been handed out again: loading it" and was in the shared world a
  fifth of a second later. Nobody reconnected and nothing was logged by either side.

  `reload` on its own then put universe time back, which is the only way it ever moves: 72783.5 s
  in the game before, 72747.8 s after, with the server's own clock going to the world's time while
  nobody was in it -- "universe time waits at 72740 s, 44 s back".
- **Joining from the main menu, and being made an admin.** One game, opened on nothing -- the
  sandbox view, no world, no craft -- with the Multiplayer window drawn over it, which is where a
  player starts and where the window was not drawn before. Connect from there and the server handed
  over its 31 KB world, the game loaded it and was flying `Niric` half a minute later, having never
  opened a save by hand.

  Then `op Alpha` at the server's console. The game wrote
  `{"127.0.0.1:50100": "Tc9kzx..."}` into its `tokens.json` and the server kept
  `f777349e...`, which is that token's SHA-256 and nothing else; `/status`, `/players` and
  `/admins` typed into the chat were answered in the same chat -- `1 Alpha playing, admin, flying
  Niric` -- and `/quit` was refused as the console's alone. `/reload` put universe time back from
  72688 s to 72630 s from inside the game, as it does from the console, with the player still
  flying and still an admin. `deop Alpha` left `tokens.json` holding `{}` and `admins.json` holding
  `[]`, the game knew within the second, and the next `/status` was refused. The one line asked for
  before any of it, as a nobody, had been refused too.
- **Menus, which ask nobody to stop.** KSA stops the world for Escape, the settings, and the save
  and vehicle lists as well as for the editor (`Program.UpdateMenuPause`), and the mod's speed
  prefix turned each into a speed put to the whole room -- pressing Escape asked everybody else to
  pause. Nothing is held while any of them is up. Watched for the editor; the rest are the same
  code path and the same flag.
- **The start window in KSA's own chrome, and the dot in its menu bar.** The window is drawn with
  `ConsoleStyle.BeginModal`, `ImGuiHelper.BeginRegion` and `ConsoleWidgets.PrimaryButton` -- the same
  calls KSA's configuration makes, so it follows the game's theme and interface scale rather than
  imitating them. Seen: the titlebar, the regions, the rows and the yellow button, in place of that
  configuration. A region lays its rows out in two columns and sizes the label one to the region, so
  anything longer than a word or two is cut; the checkbox sits outside them, where KSA puts its own.
  The dot and the server's address go in KSA's menu bar from `Program.DrawProgramMenusHook`, which
  the game leaves empty inside that bar for a mod to draw in, just before it writes its version.
  Seen amber while a world was being handed over; green is for a game that is in the world.
- **Closing a menu, which asks nobody to warp.** KSA keeps the simulation speed when a menu opens
  and sets it again when it shuts (`Program.UpdateMenuPause`), and what it keeps is whatever the mod
  last wrote: a tenth either side of normal, or several times normal mid-catch-up. The restore
  arrived with the menu already shut, so the prefix read it as this player asking the room to run at
  1.08 or 0.94. Three times into the editor and out again put no ask on the server at all. It was
  one of five things writing the speed that were read as the player, and the rest were found by
  looking for the other callers rather than by being reported.
- **A launch landing on a free pad.** Two craft launched one after the other from CCSFS LC-39A
  went to `MP2` and then, with one more craft on the ground, to `MP3`, each time because the pad
  before it was taken. The pad is chosen inside
  `Vehicle.GetInitialKinematicStateForLocation`, not at the `Celestial.GetLocation` that reads the
  site: that one is a single line forwarding to the body template, the JIT inlines it into the
  editor's exit, and a prefix on it never ran -- which is why every craft landed on the middle pad
  while the picker was choosing the right one and being ignored. Those two landed on `MP2` and `MP3`
  because every game then tried the site's own pad first and the ring after it; a game in a world
  with others now starts at a pad of its own, so the names in a run like that one would differ.
- **The vehicle editor, which asks nobody to stop.** KSA pauses the world to open the editor and
  runs it again on the way out, and the mod's speed prefix turned each into a speed put to the
  whole room: opening the VAB asked everybody else to pause. Watched working: with the editor open
  the game sat at speed 0 holding nothing, the server heard not one warp ask, and on the way out
  the game was 1.76 s behind and ran at 1.1x until it caught up.
- **Two games making one name.** A kitten sent out of the same capsule in both: the second was named
  `Nasley 2` by the server, each game kept flying its own, and each built the other's and stood it
  where its owner put it. The second game holds the other player's under a name of its own
  (`Nasley (2)`), since KSA looks a craft up by its Id and this world now has two of them.
- **A KSA update, flown.** The move from `2026.10.25.5707` to `2026.11.10.5801`. Three breaks the
  compiler found: a location is keyed by `Key` rather than `Id` and names its concrete with a
  `DataReference` rather than a string, the list of systems moved from `SelectSystem.Systems` to
  `SystemLibrary.PickerSystems()`, and letting a kitten out answers with an `EvaRequest` rather than
  a bool. Two more it could not: that same `EvaRequest`, and boarding's `CrewSeatResult`, are what a
  prefix's `__result` has to be, and a prefix still declaring `bool` is refused by Harmony at
  install -- the patch logs and the refusal it carries quietly stops happening. Then
  `tools/breakup.sh` on the new build: both worlds came out holding the same 13 craft, nothing made
  up on either side, nothing claimed that a game had only been told about, and no physics step
  refused. The worlds the server carries were written by the older build and still load, because
  every element added since -- a world's `Rules`, a kitten's `Condition` and `CauseOfDeath` -- has a
  default, and the default rules are a fresh game's.
- **A craft built in the editor and launched, with another game watching.** The path three releases
  shipped broken, flown for the first time on 2026-11-10. B, holding only puppets and so controlling
  nothing, had `design {"save":"Rocket"}` write the 89-part starting rocket out as a vehicle save,
  `mp {"editor":true}` open the blank editor on it, `design {"load":"Rocket"}` put the part tree in
  -- 89 parts, under `NewRocket_2`, the name `GenerateNewVehicleId` picks -- and `mp {"launch":true}`
  press Launch. The site's own pad was taken, so the mod put it on `CCSFS LC-39A MP3`, which is this
  mod's own third pad there. B owned and described it once, A built it once as a puppet with the same
  89 parts, and neither log held a warning or an error. So `Sync.Made`, the launchpad choice and the
  definition of a launched craft are all watched now, and `mp {"editor":true,"blank":true}` opens an
  empty editor even while flying, the way KSA's Build New Vehicle does.
- **The bridge losing a whole reply over one unmeasurable number.** Found by accident with the rig
  up. `World.Between` and `World.RangeFromCamera` both say "cannot tell" with infinity, and
  `JsonSerializer` refuses to write one: it throws partway through, so `status` answered nothing at
  all and timed out, the whole instrument dead, the moment one nametag's range could not be worked
  out. The log said so in a line about `JsonNumberHandling` that reads as a warning about
  formatting. Non-finite numbers are written as null now, at the boundary rather than at each
  sentinel, so a field that cannot be measured is empty and the reply still arrives.

- **Flat ground under all eight launchpads, and nothing to walk in under.** KSA levels a launch site
  with a terrain decal 550 m across holding the ground at one absolute height -- 16.97 m at the Cape
  against 11.09 m of natural Florida, 180 m at Vandenberg against 127.62 m of hillside -- dead flat
  only over a core 170 m across and feathering back to the planet's own by the rim. The pads laid
  out here stood 150 m from the site, out on that feather, each a rigid disc placed flush with the
  ground sampled at its own centre: the side facing the site buried, the side facing away hanging
  over a drop. 1.6 m and 2.8 m at the Cape; 5.8 m and 10.5 m at Vandenberg, where they also stood at
  visibly different heights, each centre landing on its own part of the hillside, and where a kitten
  could walk in under a pad and not get out again. `errors.md` has the measurements.

  A decal of its own for each pad is how the game would do it, and was tried: the modifier count is
  read by the per-celestial render data, by the planet renderer's buffer and by the ground clutter
  renderer at three different moments, and there is no window before all three. It died on the first
  rendered frame in `GroundClutterRenderer.GenerateBatch`, and there was no room for it anyway -- a
  celestial has sixteen modifier textures and Earth already spends nine.

  What is in is the other way round: the site's own decal widened until its flat core takes the whole
  complex in. One number changed rather than a modifier added, and the one change that answers the
  burial, the overhang and the differing heights together, since inside the core the decal's weight
  is exactly one and the ground is flat under every pad. `Pads.Level` does it from a prefix on
  `Universe.SetupRenderData`, the last moment before KSA copies a decal's radius into the parameters
  the GPU reads and fills that buffer, once, in the planet renderer's constructor. Levelled before it
  every reader agrees and nothing is rebuilt; levelled after, the ground the physics finds and the
  ground a player sees would be different ground. The core is worked out from the site's own decal
  and this mod's own ring rather than written down, so moving the pads moves the levelling with them.

  Flat ground then showed what the slope had been hiding. A pad lays 82 m of grass over the ground
  around it and the pads were 130 m apart, so those discs had always overlapped by 35 m a side:
  invisible while the inward half of every pad was buried, and a patchwork of each showing through
  the other once the two were exactly coplanar with their depths interpolated from different
  matrices. So the pads moved apart as well -- 169 m between centres, four metres of grass between
  aprons, a ring 195 m out, and the levelling followed it to 303 m without being told to.

  Flown at Vandenberg, the hard case: 52 m of lift, and where the kitten got stuck. All eight pads
  stand at one height on flat ground, every ring road is whole, there is grass between the aprons
  rather than a patchwork, and the hillside still rises behind the site instead of the mountain
  being levelled with it.
- **A craft launched from the editor, built by a player already in the world.** Both games launched
  from the VAB nineteen seconds apart, and each had the other's rocket standing on its pad within
  three tenths of a second:

  ```
  B 10:57:24.022  launching from CCSFS LC-39A MP3 instead: the site's own pad is taken
  B 10:57:24.071  sent the server how NewRocket_2 is built, 3681 bytes
  A 10:57:24.338  built NewRocket_2, Bravo's
  ```

  Before this, neither definition was sent at all. A craft the editor made was marked as one the
  world had loaded, which is the whole of how a craft made here is recognised, so it was claimed as
  though it had always been there and nothing ever wrote it down: the name was in everybody's
  roster, the states were relayed to everybody, and no other game could build it. Only `MadeHere` is
  marked now. `errors.md` has the mechanism, and why three releases went out with it. A kitten on
  EVA (`Babean`) crossed in the same run, so the adoption path that the old marking also sat on
  still answers.
- **Two games choosing different launchpads.** Player 1 took `CCSFS LC-39A MP2` and player 2 took
  `MP3`, each the pad its own id picks out of the eight. The site's own pad used to be every game's
  first choice whatever else was turned, which left the turning doing nothing. Not the whole of it:
  those two launches were nineteen seconds apart, so the craft and states `Pads.Free` counts would
  have kept them apart anyway. What was watched is that each game's *first* choice is now its own.
  The moment the change exists for -- two launches inside the window before either craft has crossed
  the wire -- has still not been made to happen on purpose.

- **The server browser.** Seen in a game 2026-11-09, against the real directory at
  `servers.ksarmory.com`. The window listed `mp.ksarmory.com` from the directory and `127.0.0.1`
  from this player's own remembered addresses, probed both from one socket, and marked the second
  `[wrong version]` -- a genuine mismatch, since that was a server left running from an earlier
  build, with the whole of it in the log as `protocol 22, you have 23`. The mod's log, from a start
  at 15:32:46: `1 of 1 directories answered (ksarmory), 1 servers listed, 1 of this player's own`,
  then 45 ms later `1 of 2 can be joined`. The whole of it -- fetching the list of directories,
  reading the directory, merging, resolving, probing and judging -- took under a second from the mod
  loading, on a window drawn before KSA has a universe.

  **Joining from a row has been seen too.** A row picked, `CONNECT` pressed, the server's world
  handed over and loaded, and a craft launched into it with its definition relayed. That is the
  whole path the window exists for, and it carries `Wire.Version` 23 end to end: the cookie welcome
  now holds the player count, the most players, a flags byte for whether a password is wanted and
  the KSA build the world wants, about twenty bytes that take the answer from roughly 450 to 470
  against a 140-byte hello -- an amplification factor of about 3.4 rather than 3.2.

  `docs/DIRECTORY.md` is what a directory answers and `docs/SERVER-BROWSER.md` is the research the
  design came out of. What follows is only what watching it taught.

  It took two tries to look right. At 440-560 px wide and 460 tall, which is the shape KSA's own
  configuration is, the third column cut every reason in half and the address field sat below the
  bottom edge -- the one control that has to work when nothing else does. A table wants width and a
  column of labelled fields wants none, and the window had been built for the second.

  The shape it settled on is Beik's, and it is a different answer rather than a wider version of the
  same one: 820-1180 px against 0.62 of the screen, up to 900 tall, the list filling a scrolling box
  of its own, and beneath it two blocks splitting *what you are* from *where you are going* -- the
  nickname and the start-settings box on the left, the address, the password and `CONNECT` on the
  right. `PLAY ALONE` sits in the titlebar, away from the yellow button a player actually wants, and
  `BACK TO MULTIPLAYER` in KSA's own configuration footer is the way back from it.

  **Whether this game can join a server is the state of a row, not a value in a column.** The marker
  beside the name carries it -- `[wrong KSA build]`, `[turn mods on]`, `[turn mods off]`, `[full]`,
  `[wrong version]`, `[no answer]`, `[recent]` -- and `CONNECT` is greyed, with the reason beside
  it, for a row this game would be refused from. Rows were dimmed for this before there was a
  marker, and dimming fought the colour ImGui fills a row with when it is chosen or merely under the
  pointer, so a row could disappear as the mouse crossed it.

  A row also says the round trip, which cost nothing to know: the probe is a question and its
  answer, so the time between them was already being measured and simply not written down. It is
  taken from the hello that was answered rather than the first one sent, so a server that needed
  asking twice is not charged for the lost datagram.

  A row carries what a server said of itself rather than what a directory claimed of it, which is
  why one server reached through two directories collapses onto the address that answered. The
  password field is replaced by the words *none needed* where a server has said it wants none, which
  it has said in every welcome since protocol 23 -- an empty box a player cannot type in looks far
  too much like an empty box they have not typed in yet. Servers with players on them sort first,
  then the ones that can be joined.

  The mod list under the fields is the one thing here a player has to act on *before* KSA starts,
  since KSA reads its own mods once and a player told after a world has loaded is a player who has
  to restart. It is drawn from the row's own answer rather than from a second probe of a server this
  game has just finished probing.

  One fault worth recording because the screen would never have shown it: a row still being looked
  up in DNS is in no state at all -- not answered, not given up on, and not among the probes
  outstanding -- so the count of what could be joined was taken while a server was still on its way
  to being asked, and said `0 of 2` with one row already good. Names being resolved are counted as
  outstanding now.

  The columns are headed `Server`, `Players`, `World` and `Latency`, and the count of what can be
  joined runs along the box's floor beside how many directories answered, rather than inside a
  heading where it would change a column's identity every time a server replied. Latency sits last
  because it is the least of what decides whether to join -- the build, the mods and whether anybody
  is on it all come first -- and that is where every server browser worth copying puts it.

  **A label that says the opposite of its meaning was the fault that kept coming back.** `Mods (1)`
  beside "1 to turn off" reads as *the server wants one*. "Extra mods" was read as mods to get
  rather than mods to drop. Each time the fix was the same: put a number next to the thing it counts,
  and let the label say only what the row is about.

## What has not been tried

- **A server reading the shared list of directories.** An advertising server reads
  `directories.txt` once an hour, announces to every directory in it, and withdraws from any that
  has left the list -- so retiring one is a commented-out line for servers and not only for players.
  A server with `advertise` off reads nothing and asks nobody, which is the half worth testing and
  is tested: the read is pointed at a loopback server and a day of ticking leaves it never asked.
  All of it over loopback, so what has not been seen is an hour going by on a real machine and the
  real file being picked up.
- **A server being told its port is shut.** `advertise` is off by default, and a server that is
  advertising says every sixty seconds that it is still there and withdraws when it stops or when
  the setting goes back to `false`. That much has been watched end to end: `mp.ksarmory.com`
  announces to `servers.ksarmory.com` on another machine and is listed there only once a probe
  reached it, and what a directory answers goes into the log whatever it says. What has not been
  seen is the case that matters most to somebody standing a server up -- a port closed or forwarded
  wrongly, and an operator reading the refusal and acting on it.
- **A directory somebody else runs.** `servers.ksarmory.com` is deployed and listing, and the split
  it needed holds: this repository owns the container, the KSArmory repository owns the hostname and
  the Caddy site block in front of it. What the whole design turns on and nobody has done is a
  second directory, on different hardware and under a different person -- which is the case that
  decides whether retiring this one ever costs a player anything.
- **Grass and rocks kept off the extra launchpads.** KSA keeps four circles of ground clear of
  ground clutter for a whole planet -- the cap is in the clutter compute shader as much as in the
  struct handed to it, so a mod cannot raise it -- and spends one on each of the first four
  launchpads in the celestial's list. Earth ships five launch sites, so the fifth grows grass
  through its concrete in a game with no mods at all, and the pads added here never stood a chance.
  A postfix now spends those four on whole complexes instead: one circle of the ring plus a pad's
  clearance, which is not an approximation of the eight it replaces but exactly their union, since a
  ring pad's own circle already reaches past the site's centre. Four sites are covered and the fifth
  is no worse off than the game leaves it. The pads also have to exist before KSA asks, which is why
  they are laid out from the same early hook and not only on the frame: KSA decides it while the
  world is still loading, so on the frame they were never in the list it walked. **Not looked
  for.** The pads are clear of scrub in every picture taken since, but nobody has stood at the
  fifth site, and there is no before to compare against.
- **The keepalive under a real stall.** Driven over loopback by stopping a client's `Update` and
  putting the clock forward: sixteen seconds of a frozen frame loop keeps its place, eighty lets it
  go. What has not been watched is KSA itself hitching -- a rebuild, a scene load -- and the session
  surviving it, which is the thing it was written for.
- **Another mod writing what this one writes.** The prefixes on both `SetSimulationSpeed` and on
  `Vehicle.PrepareWorker` are given `Priority.Last`, so they are the word that stands when another
  mod is patching the same method: a craft somebody else is flying goes where its owner says, and
  the clock stays the server's. None of them returns false, so the other mod's prefix still runs.
  Read against KSArmory, whose warp policy writes the speed, and KSACars, whose own hook writes it
  during one of its races and whose car physics writes a craft's state in the same window as `Pull`.
  Not yet watched with either of them installed.
- **The Multiplayer window before KSA's configuration and its welcome.** KSA's configuration at
  start comes before StarMap's `AllModsLoaded`, so a prefix on `Popup.DrawAll` goes in at
  `ImmediateLoad` (`Mod.PrepareSystems`, inside `ModLibrary.PrepareAll`) and draws the window instead
  of KSA's popups while the `ConfigOnStartPopup` is up. Connect there joins nothing: a hello with no
  cookie is answered by the server with one, and since protocol 19 that welcome says which system
  the world is in, so the game starts in it (as choosing it from the configuration's list would)
  and joins once loaded; with "Change settings before starting" ticked, KSA's configuration is
  given back with that system chosen instead, and the join waits for its Start. Play alone gives the
  configuration back. `WelcomePopup` is made inactive as
  it is constructed, which takes it out of KSA's list before that is drawn, and is made again for a
  player who plays alone. A server with no world is given the new game KSA starts in, which in its
  default start type is at the craft `Rocket`. Seen in a game only this far: with
  `selectSystemOnStart` on, the window is drawn in place of KSA's configuration and Connect asks the
  server, and with it off the game loads as it always did and the window is drawn over it. Play
  alone, the settings-first path, the welcome being held back and the join after loading have not
  been watched. They can be now: the bridge is opened at `ImmediateLoad` and served from the popup
  prefix, so `mp`, `start` and `load` all answer at the start window, and `status`'s `popups` says
  what is standing over the game -- a picture cannot, KSA hiding the interface to take one.
- **Placing a puppet without physics never happens.** `PlaceAnalytic`'s own first line and `Settle`'s
  check before it both stand it down for a craft with a physics bubble, and KSA's
  `AssignOrphanVehicle` gives every vehicle one in the first thing it does each frame. So both of its
  branches -- `SetFlightPlan` on a railed craft, `TeleportToLocation` on a moving one -- are
  unreachable, and `Pull` is the only thing that moves a puppet. `Rebase` is the one analytic path
  that does run. Written up in `docs/SYNC-SMOOTHNESS.md`.
- **Pulling a puppet in physics.** Two craft were close enough to share a bubble; that the pull holds
  one steady there has not been measured.
- **Holding the catch-up warp to the frame rate's headroom.** The arithmetic is tested and the
  session that calls for it is written up in `errors.md`; a world that stays behind rather than
  warping into its own bubbles has not been flown.
- **Everything a review turned up on 2026-11-06**, written up in `docs/REVIEW-2026-11-06.md` and
  fixed the same day: placing another player's craft from the frame's quiet window, leaving the
  shared world when a save is loaded here, holding a kitten's correction below a walk, a reconnect
  taking its own name and craft back, a roster round taken whole or not at all, the clocks'
  difference measured again on a line that got slower, and the three ways a stranger could stop the
  server. The tests cover the arithmetic and the server over loopback, and the two-game rig says the
  mod still shares a world -- but none of it has been in front of several players, which is where
  every one of these was found.
- **Two players at one launch site, both shedding pieces.** A name says what a craft came off, and
  that answer is taken for both players -- their pieces left alone, this player's claimed -- so the
  pieces go to the right player wherever they are standing; what is untested is the case the
  distance rule used to get wrong, which is the one that put another player's debris on the wire.
- **A puppet held against the ground, and the parts this world would fail on it.** Both ways KSA
  wrecks a craft are refused for another player's now, where only the destruction event was before.
  The one that was missing is the one a crash takes, and it is also the one that strips parts off a
  craft that lives -- so this world blew parts off rockets that were whole in the worlds owning
  them, which is how it was reported. What the guard stops is read off the game's source and the
  frame order around it. It can be reached on demand now, which it could not before: `PartFailure`
  on the ground is unreachable by flying, because `PhysicsBubble` stands KSA's G-load path down
  wherever a craft touches terrain, ocean or another craft -- so the bridge raises the same event
  itself through `fail`, and aiming it at a puppet is the guard's own test. What has still not been
  watched is the realistic version: two games holding one rocket on the ground with the same parts on
  it, a minute of divergent physics doing the work rather than a tool.
- **A rocket that crashes with nothing else of its owner's alive.** The pieces of a crash were
  claimed by nobody. `Adopt` settled a craft whose name said nothing by distance, and a crash leaves
  its owner nothing alive to be near, so every piece measured further from its owner than from
  anybody, none was claimed, none was described, and the others watched the rocket vanish whole and
  never saw it come apart. A name now answers for a player's own debris as it already did for
  everybody else's. The names are the live server's own (`LS Test MK I_1` off `LS Test MK I`) and the
  walk over them is covered in `WireTests`, but nothing in the suite reaches `src/Mod`, so the frame
  where a crash sheds twelve pieces has not been watched.
- **A kitten drawn torn open was the rig's own doing.** Seen on 2026-11-10 with two games: the
  kitten `Niric`, out of the save `two-games.sh` loads, drawn in one game with its helmet whole and
  its body in long dark blades, and clean in the other, while both count one part and agree on its
  name. It is not anything a kitten carries and not anything this mod sends. The bridge's `load`
  read the save on the pass KSA's fresh world first said it was up, which is still the start
  popup's own pass and before KSA has drawn a frame of that world, and a kitten built then keeps a
  mangled skinned mesh for as long as it lives. The game that showed it is the one that never loads
  another world; the other reloads on joining and builds the kitten again. `load` now waits for the
  world to be drawn first, and `errors.md` has the runs. A player cannot reach it: a hand gets to
  Load frames later, and a server's world is loaded from the frame hook, which does not run until
  frames are being drawn. What stands from it is the gap it fell through -- nothing here compares
  anything about a craft but its part count and its name, and only a picture found this.
- **Debris off a craft this world had to rename.** The name rule above reads a piece's ancestry in
  the names the room shares, while KSA builds a shed piece's name out of its parent's local
  `Vehicle.Id`. Those differ for every craft this world renamed -- one built here under a free name
  because the one on the wire was taken, or one the server renamed on a collision -- so the ancestry
  matched nothing in either roster, the name rule was skipped, and the piece fell through to the
  distance test standing right beside the puppet it came off. A permanent orphan, and the case the
  name rule exists for. `Whose` reads each ancestor back through the renames now. Worth knowing that
  a breakup makes this the common case rather than the odd one: both games shed their own copy's
  pieces under the same names, so whoever receives the other's definition second has to rename.
  Never watched -- `tools/breakup.sh` is the run that would show it.
- **A piece lying still that is struck.** A craft that is under way and not flown is said ten
  times a second; one at rest, twice, and it stays twice until its owner's game has it moving. A
  piece of an earlier crash that a rocket fell on was drawn 9 m out for the last second and a half
  before it was destroyed. Not fixed.
- **A game building thirty pieces at once runs behind.** Half a second at worst and a fifth of
  one for the half minute after, measured against the game that owns them. Not fixed.
- **A craft nobody owns, docked with.** What nobody owns is each world's own, and after a walk of
  warps a rocket nobody flew was 3.4 m from itself in the two games: one saw the capsule dock that
  far from the port. Targeting a port could claim the craft; it does not. Not fixed.
- **Debris nobody owns.** Its owner's game says when a piece has gone; a piece whose owner has
  left, or every piece after the rig swaps the server under two games, is expired by each world
  when that world likes. Thirteen stood on in one world of two. Not fixed.
- **Two games on different machines.** The *server* has been remote -- `mp.ksarmory.com` on a VPS,
  reached across the internet, with a craft described to two other players in its world -- so that
  path is exercised. What has not happened is two games behind two different connections, where each
  player's own line, NAT and jitter are in play rather than one machine's loopback. WSL in its
  default NAT mode cannot be reached from a Windows game at all, which is why `two-games.sh`
  publishes a Windows server unless WSL is in mirrored mode.
- **A puppet's engines where this game is stepping the craft.** The plume the mod writes for a craft
  out of physics has been watched; the one KSA draws for a puppet close enough to be in this game's
  own physics has not, so the rule that hands that case back to KSA is read off the source alone.
- **An engine lit by a flight computer rather than by a hand.** What is sent is the nozzles' own duty
  cycle, which is what a node burn leaves behind, but no node burn has been watched from another game.
- **Spectating, and finishing a name with Tab.** Both are the game's own, answered without the
  server hearing of them, and both are driven only by hand so far: no run has watched a camera land
  on another player's craft, nor a half-typed name fill itself in.
- **Whether building in the quiet window cures the physics failures.** Two games on one machine,
  one kitten sent out: it was built as the other's puppet from the new window, no patch warning, no
  `Update task failed` in either game. That shows nothing broken, not the race cured -- the failures
  were seen with three players and two rockets in pieces, which is a load no rig here can make.
- **A craft the server hands over, written down.** A craft this game is given control of without
  having claimed it reaches `Owned` by a path of its own, and that path used to record the shape it
  would next be described at as the shape it already had -- so the re-describe never fired and a
  craft taken this way was never written down. It starts at a shape no craft has now, as the rebuild
  path beside it already did. The editor launch that shared this fault has been flown; this one has
  not. It can be driven, though, which it could not before: A claims a craft and looks away, B
  watches it with the bridge's `watch`, and the lapse rule hands it over.
- **A full site refusing a launch.** Nobody has filled all eight pads to see the refusal and the
  line drawn over the editor. What has been watched is the other side of it: the warning rightly
  absent and nothing refused while pads are free. The launch menu for a craft already built refuses
  one too, which is newer still: it had no refusal at all and stacked onto the site's own pad, and it
  answers with KSA's own timed alert rather than with the mod's line, which is drawn over the editor
  and not over that menu.
- **A craft described over and over while it comes apart.** A game adopts a craft it has not seen,
  and `Seen` holds Vehicle objects: when KSA destroys and remakes a stack under the same Id, as it
  does while a rocket breaks up, the new object is unseen and the craft is described again. Watched
  on `mp.ksarmory.com`: `NewRocket_3` described 23 times in six seconds, each definition smaller
  than the last as parts fell off, every one of them relayed to both other players to rebuild. What
  that costs the games receiving it has not been measured, and no fix is in yet.
- **A world handed over with the vehicle editor open.** That KSA refuses to save or load then is
  read off its own source; that the mod waits rather than handing on a stale world has not been
  watched happen. Opening the editor has been, and so has filling it: `design` writes a craft
  standing in the world out as a vehicle save and puts one back into the open editor, which with
  `mp {"editor":true}` and `mp {"launch":true}` is a craft built and launched with no hand on the
  mouse. No part can be *placed* from the bridge, so part placement, symmetry and staging edits stay
  out of reach. The bridge's `save` used to answer `ok` while KSA wrote nothing, and asks
  `World.Refuses()` first now.

## What it does not do

- **No docking between two players, no shared craft.** A craft is flown by one player at a time, and
  two players' craft that touch port to port are not docked. A craft docks with one nobody flies,
  and undocks.
- **No authority over physics.** Each game is trusted for its own craft; the server only checks a state
  comes from the craft's owner.
- **The world is never warped past what the slowest machine can fly.** Every game measures what it
  is managing -- universe seconds a real second, over a couple of seconds so a hitch does not speak
  for it, floored by what KSA says it achieved of what it asked -- and tells the server with each
  ping. A game comfortable at the speed it is running at says nothing at all, which at normal speed
  is every game there is, and silence is not room. So a warp is put to a room that has said
  nothing a step above normal and climbs, half as much again each second: the only way a machine's
  limit is found is by asking it for a little more. Run at the ask at once, the first second of
  1000x was a thousand universe-seconds no game was near, and the room spent nineteen seconds
  making them up at 1x. Only where every watching machine has said what it flies is a warp run
  there directly.
  It falls to what a machine says it can fly within a second of one giving out, and climbs back a
  step at a time -- but never again to a speed a machine has been found not to fly, or the world
  climbs into the same ceiling and falls out of it once a second for as long as the warp lasts.
  The ceiling is that one machine's, and lifts when that machine says it can fly past it or leaves.
  A climb is never a fall: a machine saying it flies a little less than the world is running at
  holds the world where it is and sets no ceiling.
  The server also watches each game's lateness from one ping to the next, as a share of what the
  clock ran in between, which reads the same at every speed. A game losing a quarter of it while
  behind stops the climb before that game has measured anything. And a game behind is backed off
  for once and then left alone while it is catching up fast enough to be level within fifteen
  seconds: its gap is longer in the seconds of a slower clock, so backing off again for the same
  gap makes the case for backing off a third time, and a room that could fly sixty times real
  time ended at one. `errors.md`, 2026-11-10.
  A window nobody is looking at is left out of the floor: Windows throttles it, so what it manages
  measures Windows and not the machine, and that player is an absent one rather than a slow one. A
  pause or a world in slow motion asks nothing of anybody and is never held back.
- **The clock is never left behind the room.** A game cannot rewind, and one more than three of its
  own seconds past the clock stands still until the clock reaches it. So where every watching game
  is past the clock by more than a nudge closes, the clock goes on to the nearest of them; and
  where the clock's own speed has just fallen, which leaves every game past it by however long
  each took to hear, it goes on to the furthest, by no more than two seconds of the speed lost.
  The games it passes have that to catch up, which is what a game does best, where the other way
  round the furthest stood still for the difference, a second for a second. What a game says of
  its lateness for a round trip after the clock has gone on is about the clock as it was, and is
  read as that much less: taken as said, the same lead was followed twice at 150 ms each way.
- **What a session turns on is said until it is heard.** A craft's state goes out twenty times a
  second and every missing piece of a world is asked for again, so loss costs those nothing. Three
  words were said once and cost a session when lost: the server's ask for the first player's world,
  which it now repeats every three seconds while none of it has come; the end of a warp, which it
  now goes on saying for five seconds; and a game's word that it is in the world, which it now
  repeats until the roster shows the server knows. That a craft has gone is said three times by
  the server while its name stays gone, and again by its owner's game for as long as the roster
  still gives it the craft. Still said once: a line of chat, an ask for a warp and a vote on one.
- **A world with nothing in physics closes a gap in a moment.** KSA works a craft on rails or
  sitting still out in closed form, so a step of an hour costs what a step of a frame costs, while a
  craft being stepped through physics costs an hour of stepping. So how far a world may be warped to
  catch up is a thousand times its speed when everything here is analytic: a player who was not
  looking comes back in a moment instead of running fast for minutes.
- **A ceiling worked out from the frame rate was tried and taken out again.** It read a frame's
  length against the step cap as headroom, which it is not, and its floor of one meant a game at
  exactly its minimum frame rate could never catch up while a game below it fell behind for ever:
  two players sat 200 s behind and stayed there. Written up in `errors.md`. What a world may be
  warped to is the flat ceiling again.
- **How long a craft may go unheard is counted at the world's speed.** A state is sent on a real
  clock and its age read on the world's, and the two are the same thing only at normal speed: at ten
  times, half a second of sending is five seconds of world. Letting go at a fixed five dropped every
  craft nobody was flying as soon as anyone warped, and with it the pull, the placing and the name.
- **A warp step is taken from the shared warp, not this world's speed.** The game works a key's
  speed out from the speed the world is running at, which while connected is the server's warp
  nudged off a round number to hold its clock; the mod takes the key by which way it points and
  steps the game's own ladder from what the server says instead.
- **Engines are shown firing, and only where this game is stepping the craft.** Whether a craft's
  engine is lit and how far its throttle is open live in a private field of a public type, read
  through Harmony: the throttle alone can be read without it and neither can be written. A build that
  moves that field leaves engines dark and says so in the log. What is sent is not that field alone,
  because KSA sets it false every frame while the flight computer is running the burn, so a craft
  burning a manoeuvre node said its engines were cold: the nozzles' own duty cycle counts as lit too.
  A puppet's engines are set from its owner's state, which changes nothing that lasts, since the
  craft is pulled back to where its owner says it is every frame regardless.

  What the plume, the exhaust light and the volumetric exhaust are all drawn from is
  `RocketNozzleState.DutyCycle`, and that is accumulated only inside a full physics step, from the
  chamber pressure the rocket model worked out. A craft of this game's own in its own bubble, in
  freefall or sitting still, is stepped down a cheaper path that clears the duty cycle every frame
  and never accumulates one. So another player's craft is burning *here* only when it is in this
  game's physics -- near this player, or anywhere in an atmosphere -- and out there it draws no
  propellant, makes no thrust and leaves nothing to draw a plume from, however hard its owner says
  it is burning.

  So out there the mod writes what the step that never ran would have left: the chamber conditions
  that engine's own design gives at the throttle its owner reports (`RocketCore.ComputeConditions`),
  the exhaust its nozzles make of those (`RocketNozzle.ComputePerformance`, which is what KSA's own
  engine window calls from the main thread), and a full duty cycle. It goes in through
  `GetModuleAndAllMutableStatesForInitialization`, in a prefix on the two passes that read it, which
  is the one place after the last physics step and before the frame is drawn -- `[StarMapAfterOnFrame]`
  is after the render, and anything written there for this frame is a frame late and cleared by the
  next step first. A craft KSA is burning for real is left alone, since its own numbers are the
  better ones, and a craft that stops burning is written dark once.

  None of it changes what the craft does. KSA works a core's conditions out from its throttle and a
  nozzle's thrust fraction out from those conditions on every step it runs, and the nozzles that
  thrust and draw propellant are counted from that thrust fraction: neither the throttle nor the
  thrust fraction is written here. What is written is read by one frame's renderers and overwritten
  by the next step from numbers the mod never touched.
- **A craft is let go of ten seconds after its owner walks away from it.** Sticky ownership is
  what stops two games pulling one hull in two directions, but sticky for ever meant the first
  player to touch a thing held it until they left: in a session here one player came to own every
  craft in the world and the other could fly none of them. A craft its owner is neither in nor
  looking at goes to a player who is watching it after ten seconds, `--lapse-within`, and a player's camera counts as
  being near it so stepping out to watch something is not walking away from it. Only a craft somebody
  else is waiting on changes hands: a world is mostly parked craft nobody is looking at, and letting
  those go lapsed ownership 873 times in 70 minutes, each one moving the camera of whoever was
  watching a craft when it came free.
- **The operator's broom sweeps by name, and every game sweeps its own world.** A world fills up
  with spent stages, and a `Gone` cannot clear them: that datagram is one game's word about a craft
  of its own, which the game that owns it is right to ignore, so the one game holding the debris
  keeps it, goes on sending it, and hands it back to everybody within the second. `clear` on the
  console sends `Swept` instead, which is the world being told what is no longer in it.

  It carries a name with `*` and `?` in it rather than a list of craft, because the server cannot
  name most of what is out there: it hears of a craft when somebody claims it or says how it is
  built, and the fragments a spent stage breaks into are claimed by nobody. Measured in a game: a
  world of 27 craft, of which the server knew 3. So each game matches the name against its own
  world and takes away what it finds, bar what anybody is flying -- a world that pulls the hull out
  from under a player is worse than a world with debris in it. A fresh save is asked for straight
  after, since the world on disk still holds them all. It comes from a game that has already swept
  -- a game with a sweep still queued does not answer a save that frame, or it would hand back the
  very thing it is about to take away -- so what lands on disk is clean and nobody is moved. A
  clear therefore costs no loading screen and leaves universe time where it is; `reload` is the
  command for that, and is asked for on its own.
- **The world is handed out again rather than the clock put back.** Universe time cannot be moved
  backwards by telling anyone: each game keeps its own, one more than three seconds ahead of the
  server holds at a standstill until the server catches up (`ClockFollow.Speed`), and the only thing
  that brings a game's own time down is loading a world. So `reload` hands the stored world to every
  player again -- the same datagrams a player joining gets, and the mod takes a world while it is
  playing by going round its own loading once more. While they all load, nobody is in the world, so
  the server's clock goes back to the world's own time by the rule that was already there for an
  empty server, and every game comes back on that time. A sweep arms the same thing for the save it
  asks for, since the world the players are standing in still holds what was swept out of the
  server's tables. Nobody reconnects, and nobody loses the craft they are flying.
- **Ownership is not kept across a server restart**: it starts afresh, and the first to take control of a craft has it.
- **A hatch is claimed before it opens.** A craft another player holds refuses to let its crew out,
  but a craft nobody holds was nobody's to refuse: two games looking at the same capsule each sent
  the same kitten out of it and the world got two. Opening a hatch on an unheld craft takes it
  first, so the second game finds it held within the tick.
- **The refusal guards taking a craft, not keeping one.** A game that joins while already controlling
  a craft the server gives to somebody else goes on controlling it: it becomes a puppet, overwritten
  by its owner's states every frame, while the game that joined still believes it is flying it.
