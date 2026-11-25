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
  `Everywhere` merges this directory's list with every other's into one document, and
  `Page/index.html` is the web page that reads it: a browser may not read the directories for
  itself, so the one program that is already on the internet does. `Card` writes the title and
  the counts into the page's head for each request, and each server has an address of its own
  (`/s/<address>`), because what draws a preview of a pasted link runs no script and is sent no
  fragment. The page makes the same
  `borea://` link `ModLink` does, in its own script, from the listing and the Borea mod index;
  a mod the server leaves open is given the newest stable release in that index, where a game
  gives the one it has.
- **`src/Mod`** is the StarMap mod. `World` is every call into KSA; `Sync` is what a game does each frame;
  `Hooks` are the Harmony prefixes; `Panel` is the window. `Bridge` reads commands from a folder and
  answers in another, so the tools can drive a game; only a developer's install starts it.

Every datagram starts with `KSMP` and the protocol version, so anything else is dropped, and a game of
another version is refused by name rather than misread. Two are read whatever the version: `Ask`, which
a list of servers sends an address to learn what is there, and `Facts`, which answers it. They carry
`Wire.ProbeVersion` instead, because `Wire.Version` moves whenever the shared world changes shape and a
directory is somebody else's machine: read through the join's own hello and welcome, every release
emptied every directory built before it, though neither datagram had changed since protocol 23. An ask
is padded to 1,200 bytes and never answered with more, so a server sends a stranger no more than the
stranger sent it.

## What the server keeps

- **Universe time.** A clock of its own (`UniverseClock`), set from the world it hands out, running
  while anyone is in the world and held while nobody is. With nobody in it the clock is put back to
  the world's own time: what the last player simulated past the last save went with them, so a clock
  that kept it would make the next player warp through time whose results nobody has, and a session
  that ended before catching up would leave the next one further behind still. `--keep-time` is the
  operator asking for that time anyway. Five times a second it tells every game the time and its rate.
- **The world.** The latest save, zipped, kept on disk in `--data`. It comes from an operator
  (`--world`), from the first player to join an empty server, or as a fresh save asked of the player
  who has been in longest and is not in the vehicle editor, which the craft they fly says: every `--snapshot-every` seconds, and whenever a player joins a world more
  than 30 s old, the joiner waiting up to 15 s for it. A save up to 5 s behind the ask counts as that
  fresh one, since the game giving it is often a second or two behind the server. A game that has
  sent nothing 5 s after being asked, as one with the vehicle editor open does, is not waited on
  alone: the next in longest is asked too, and whichever save comes first is taken. Only ever from the player it was asked of: one
  arriving unasked is left where it came from, since the world is written to disk with its sender's
  build and mods and everybody else is then held to those, so a single client could otherwise lock
  every other player out of the server and out of each restart of it.
- **Nothing kept is simply gone.** A named save deleted, saved over, or let go as the oldest of
  five kept before a load is moved to `saves/.deleted` under its name and when it went, and taken
  for good after 30 days, judged by when it went (`SaveShelf.Binned`, `Restore`). And `audit.log`
  beside the world has a line for every command that changes the world, a save or a role, and for
  a role given by a Discord login: a file, because `docker logs` is lost with the container. For
  the same reason everything the console says goes to `logs/<date>.log` there too (`ServerLog`), the
  newest 14 days kept and older ones deleted as each day's first line is written, and a crash that
  escapes the tick loop is said there before the process goes.
- **An operator, and the log in the game.** `Wire.Role.Operator`, above admin, from the
  `discord-operators` setting and a Discord login and nothing else: it is never written to
  `admins.json`, the token it hands a machine is an admin's, and `op`, `trust` and `deop` leave a
  player who is one as they are. Listed in `discord-admins` as well, an id is an operator, and the
  server names every id listed twice at its start. `logs [days]` is the operator's alone
  (`Permissions`): the server sends `audit.log` and up to 14 days in one `BlobKind.Log`, gzipped,
  every address in it replaced (`LogText.Unaddressed`), since only the console is shown where
  players connect from. A game takes one only while it is an operator, unpacks no more than 32 MB
  of it from a server it cannot trust, writes it under `KSAMultiplayer/server-logs` in KSA's documents folder
  and opens it with the shell (`ServerLogs`). `infra/server` gives every world the list from the
  `KSAMP_DISCORD_OPERATORS` repository variable.
- **The last save before stopping.** A stop asked for (SIGTERM, Ctrl+C, `quit [why]`) with players
  in the world is counted down on their screens for `--stop-notice` seconds (15) by a `Closing`,
  said every second with the session's word; joins are refused meanwhile. Then every game not in the
  vehicle editor is asked for a save at once, and the first is waited for up to 5 s, since there is no
  time to go round them: asked of the one in longest, who was in the editor, `ksamp.gg` stopped
  without one and its world went back four and a half minutes. The goodbye carries the reason, and the
  directories are withdrawn from last, since that can take 4 s. A second stop ends it at once. A
  release replacing the container gave it no time at all, and the two minutes since the last save
  went with it. A game that does not know `Closing` drops it and gets the goodbye all the same, so
  the kind came without a new protocol version.
- **Worlds under names.** `saves/` in `--data`, a zip and a `.json` each (`SaveShelf`): what an admin
  keeps to come back to and loads over the world for everybody. Keeping one asks the admin's own
  game for a fresh save and files it when it comes, or files the world as held after twenty seconds
  without. Only that game's answer is taken: the server's own periodic save is asked of whoever has
  been in longest, and what goes into a save an admin named is not theirs to choose. Saves still
  waiting on an answer count towards the hundred and the gigabyte that are kept, or a burst of asks
  would all pass a limit none had reached.
  Loading one is `fresh` with a world that is not empty -- craft forgotten, the clock forgotten and
  set from the save, everybody handed it where they stand -- after two things `fresh` does not do.
  Both forget what every player flies, which only a word of a craft would otherwise clear: a game
  flying nothing in the new world says none, and on `ksamp.gg` a player tabbed out through a fresh
  start was listed as flying the old world's station, which spectating could not find. A game
  flying something claims it again within two seconds.
  The world it replaces is kept as `before load <when>`, so a load is the one command here that
  can be taken back -- the newest five, none when the world being replaced is already on the shelf
  (`_worldKept`: nobody has played since it was loaded or saved), and never written over, since
  no save is taken under such a name. One slot written over by every load lost the world everybody
  had been in to the second load in a row. An admin can delete them as any save: for a release
  only the console could, on the reasoning that `load` and `delete` together are the `fresh` an
  admin is refused, and on a server run without a console that left them nobody's to tidy. An
  admin is trusted with `load` and `clear` already. And every outstanding ask for a save is withdrawn: one already on its way in is
  of the world that is going, and written down as the server's it would undo the load for everybody
  who joined after. It is refused while a player here is in another system, lacks a mod the save
  was made with, or runs one it was made without: their game would be handed a world it cannot
  open, or be turned away for its mods at the next join of a world it had been playing in. A warp
  that is running is ended first. A name is a file name on
  whichever system the server runs, so it is held to what is one on all of them
  (`SaveList.Name`; no `#` either, two of which end a label in the game's windows). A save holds
  the list of protected craft as it was, and a load puts it back: the list is names, and the names
  are the ones that world's craft have, so left where it stood it would protect whatever in the
  loaded world happened to share one and nothing that was protected in it. A save with no such
  word in it leaves the list alone. Changing the list makes the world as held one that is no
  longer already kept, or a load straight after a `protect` would lose it. A zip copied in by hand is listed under its file's name with its time left
  to whoever loads it first, as an imported world's is.
- **Which craft are not debris.** `protected.json` in `--data`, names alone (`ProtectedCraft`).
  `clear` leaves a craft on it whatever the name swept by, its own in full included
  (`ProtectedList.Spares`): taking one out is unprotecting it first, and since every game sweeps its own world, every game is sent the list:
  whole, as a `Blob` of a fourth kind, at the join and whenever it changes. A `Swept` carries which
  state of the list it was made against and a game holds the sweep until it has that state, five
  seconds at most -- the list goes by a transfer and the sweep by a datagram, and swept in the wrong
  order a craft protected a moment before goes with the debris. A game that asks to hold a swept
  craft is told it is `Gone` along with the refusal: the server forgets a swept name at the next save,
  and on `ksamp.gg` 25 craft one game had kept through a clear were asked for again then and became
  that player's, in no other world. A craft that docks with a protected
  one is protected (`OnDock`), and the name that went stays on the list, being the name KSA gives
  the half that comes away again. `fresh` empties it: a name left behind would protect whatever was
  next called it -- which is also why a protected craft its owner says is `Gone` comes off the
  list: deleting and recovering one are refused, so gone is destroyed. A named save carries the list and a load restores it (above). In a game, a protected craft is not removed by Abandon, Recover or the manifest's
  Delete either (`BeforeDelete`, `BeforeRecover`). That half is the mod's alone: the server cannot
  tell a craft deleted from one that crashed, and one that crashed has to go from every world.
- **KSA's rules.** Whether life support and radiation are on is the `life-support` and `radiation`
  settings, both on unless the operator says otherwise, and said in every `Clock`
  (`Wire.WorldRules`): five times a second to everybody, so a change is every game's within the
  second and a lost datagram costs nothing. A game sets `Universe.Rules` to match each frame it is
  in the world (`Sync.FollowRules`), which also undoes KSA's own menu and whatever a loaded save
  was written with. The third flag, `ClearPads` (`clear-pads`), is the mod's rule and not KSA's;
  a game of an earlier protocol masks it off.
- **Launchpads cleared of what was left on them.** Every pad at a site taken refuses every launch
  from it (`Pads.Room`), and a busy room filled them with spent stages. Each game looks every five
  seconds (`Sync.ClearThePads`) at what stands on a pad -- within its footprint by the test
  `Pads.Free` uses, and under 200 m up, since that test is by direction alone -- that is its own to
  remove: its own craft or one nobody holds. Not one anybody flies, nor a protected one, nor --
  debris aside -- one within 2 km of a craft somebody flies: that keeps a player's rocket while
  they walk round it, and asked of debris it kept the pads full for as long as the player waiting
  for one stood beside them. Five minutes unbroken of that, on `Wall`, and `IsDebris`
  goes, four a pass; a craft that is not debris goes only when `Pads.Free` finds no pad free at its
  site, the longest left first and one a site a pass, and never in a pass that cleared debris. Its
  crew go home (`World.SendCrewHome`) and the craft goes by the manifest's delete, not by
  `Vehicle.Recover`: a recovery also destroys every piece of debris in the bubble through
  `Universe.DestroyVehicle`, past every refusal here. The going is shared as any other is.
- **Who is in the world.** A game says so once it has loaded, and only a game the world was handed to
  is believed: a player in the world claims craft, counts in every warp vote and, on a server whose
  world came off disk, sets the clock the whole room then follows, and none of that is anyone's to do
  from outside the world. A universe time that is not a number is not a time either.
- **Who may run commands.** `admins.json` in `--data`: a name and the SHA-256 of a token, one row
  per token, and each row's role, `Admin` or `Trusted` (a row from before roles is an admin's).
  `Permissions` is the one table of what each command needs, and a command missing from it is an
  admin's. Trusted is everything that keeps a world tidy and nothing that cannot be put back:
  saving over a save, deleting one, reloading the world with its time, and giving roles are an
  admin's. Speaking as the server (`say`) is an operator's and the console's: every player reads it
  as the server's own word. An `Op` ends with the role, which a game of 0.26.0 neither sends nor reads; an `Op`
  said back is matched without it (`SaysBack`), or a game of 0.26.0 would never answer one. The token itself exists in two places and neither is that file -- the server's memory
  for as long as it takes the game to answer for it, and the machine it was said to. So the list is
  enough to take an admin away and not enough to become one, and a server whose data directory leaks
  has leaked no admin. A name has to be connected to be opped, since a token that reaches nobody is
  only a row in a file; the name is then kept up to date from whoever last joined with that token, so
  revoking admin names the player an operator can see rather than the one who was opped months ago.
  A row is `Offered` from when its token is sent until the token is shown back, and a name's other
  rows are removed only then, so a name being opped again has two rows for a moment and either token
  is an admin's. A file from before the field reads as every token having arrived.
- **Who is an admin on Discord.** A row of `admins.json` with a `Discord` user id and no hash, made
  by `op discord:<id>` or by `op` on a player who has logged in; or a user id in the
  `discord-admins` setting, which no `deop` changes and no lost data folder takes away, and which
  infra/server gives every world from the `KSAMP_DISCORD_ADMINS` repository variable. The first
  Discord admin either way is what turns logins on: until then the server asks the login service nothing and gives no nonce. With one,
  each player is given a nonce in the welcome that admits them, and anybody already in is welcomed
  again with theirs. A game logs in once per machine through the login service in the directory
  (`Login`), the only holder of the Discord application's secret: it makes a P-256 key, sends the
  public half through the browser to `/v1/discord/login` in the state with a random part of its
  own, on to Discord with `identify` alone, and back through `/v1/discord/callback`, where the code
  is traded, Discord's token revoked, and a `DiscordCert` binding that key to the Discord user for
  30 days is signed and caught on `127.0.0.1` (`DiscordCallback`, a bare socket, since
  `HttpListener` on Windows wants a URL reservation), on a port the system picks, which the game says in its
  state (RFC 8252's way for a program's login): the service sends the browser to that port of the
  loopback address where it is 1024 or above, and to 50180 for a state that names none, which is
  where a game from before listened. That one port was another program's on some machines. Key and certificate are kept in
  `%LOCALAPPDATA%\KSAMultiplayer\discord.json` (`DiscordIdentity`), the Windows account's and not
  one game instance's, so every instance is logged in at once. At every join the game signs the
  nonce and the address the player typed and says the certificate and signature until answered
  (`DiscordProof`, `DiscordUser`). The server checks the certificate against `/v1/discord/key`,
  fetched once and again after an hour or a certificate that does not check, then its expiry, the
  signature over its own nonce, and that the address resolves to one of its own interfaces or to
  where the service saw it from. The certificate is public; the key is what proves it, and a
  signature is good for one join of one server, so a server a player joined cannot relay what they
  sent: it would need its own nonce signed, and the address it names leads to the other. A Discord
  admin who proves it is handed a token as `op` does; taking the Discord row away takes every token
  of the name it was under with it. The page the game opens is always `Discord.Service`, never one
  a server names.
- **Every player's ping.** A game measures its own round trip to the server and says it with each
  `Ping`; the server passes them all on in the roster, so each game can show what everyone's line is
  like. Held Tab shows that list in game.
- **Another player's RCS.** A state says which of the craft's RCS thrusters are firing, a bit
  each in the order its nozzles come in, which every game building it from one definition shares;
  the others light those and no others, as they light its engines. An RCS thruster is a rocket
  nozzle too, one a `ThrusterController` fires: lighting every nozzle for a craft said to be
  burning puffed every thruster it has in every direction at once, and a pilot nudging sideways on
  RCS alone was said to be burning, which lit their main engines everywhere else. Tried in two
  games: Bravo fired Rocket's thrusters up, Alpha drew the same nine, engines dark, and both went
  out together; Bravo's main engines drew in Alpha with no thruster lit.
- **Who is spectating whom, and giving a craft to them.** The craft a game's camera is on goes to
  the server with each `Ping` already; the roster passes it on, sooner than its own round when it
  changes, so every game can say on a player's row that they are spectating you: their camera on
  the craft you fly while they fly something else or nothing. **Give control** on that row sends a
  `Give`. The server hands the craft over only from its pilot, to a player still looking at it and
  not more than a second behind. A give to a game still catching up waits up to a minute for it and
  is made the moment it is level, with its pilot told so; it is dropped, and its pilot told why, if
  the pilot stops flying the craft or the taker looks away or leaves first. Told instead to give
  it in a moment, a player on `ksamp.gg` pressed the button fifty-five times in twenty seconds.
  Once given, the server tells that player's game, which writes it down as its own at once and
  takes the controls; the giver's game learns from the owners that it is somebody else's, and lets
  go of it with the camera left on it, as for any craft another player flies.
  Spectating lets go of the craft this player was flying: a spectator flies nothing. Left at
  the controls, their craft went on taking keys nobody could see it answer, and the room was told
  they were flying it. Spectating puts both of KSA's cameras on the craft, the flight view's and
  the map's, which each follow something of their own: with only the one showing moved, pressing M
  showed the map still on the spectator's own craft.
- **A save before a player is gone.** What a player did since the last save is in no world the
  server has once they leave, so whoever is still in it is asked for a fresh one. Nothing can be
  asked of the last player to leave: that much is lost, and the clock waits at the world rather than
  pretending otherwise.
- **Who flies what.** A table of craft Id to player. The first player to take a craft nobody holds
  keeps it until they leave; while they hold it everyone else only watches, whether or not they are
  flying it this second. What they owned is kept for them for `--kept-away` seconds, two minutes by
  default, under `Wire.Away` (255, an owner no player is given), and is theirs again the moment they
  are back in the world under the same Discord account, or the same name if they had none; after
  that it is nobody's. Let go of at once, a rocket mid-docking went on `ksamp.gg` to the player
  watching it in the second its pilot had dropped, and he came back two seconds later to find it
  taken and its RCS propellant spent. Not after a kick. Protocol 36, since a game of 35 would look
  255 up as a player, find nobody and take the craft.
- **The world's speed.** One world has one time, so nobody changes it alone. A game asks, everyone
  else is asked, and it runs only if they all agree; saying nothing is not agreeing, and the ask runs
  out after `--warp-answer` seconds, thirty by default. An ask above normal that some machine here cannot fly at
  1.25x or more is refused, and the asker told whose machine it is: held to what the room can fly it
  was put to everyone as "warp to 1x" and "warp to 1.02x". The answers already given stand when the
  asker changes the speed, except from slower than normal to faster, since a game agrees by itself
  to anything that slows the world and that yes stood for the 30x its asker went on to. A pause is a warp like any other, since stopping the world stops it for
  everyone. The warp is then the asker's to steer and theirs to stop, ends if they leave, and another
  cannot be asked for within five seconds of one ending. What makes the games warp together is that
  the server warps *its own clock*, which is the thing they all follow anyway. It gets to the speed
  agreed by climbing, half as much again every second from a step above normal, and comes down
  from it at once; the clock says its speed the moment it changes, and a game takes that as said.
  A game may answer for its player, who chooses under *Agree to warps* (`Sync.AgreeForThePlayer`,
  kept in `settings.json`): *Ask me*, the default; *When not flying*, which agrees while the vehicle
  editor is open or no craft is controlled, and to anything slower than normal; or *Always*. It is
  the same vote a player's button sends and the server cannot tell them apart, so nothing about
  what a warp needs has changed: a player in the editor, or only watching, held up every warp
  until they noticed it was asked. The game says in its log each warp it agreed to and why.
  Seen in two games: *Always* agreed in the second it was asked; *When not flying* left an ask
  alone while its player flew, and agreed once they flew nothing.
- **A name for every craft.** Craft Ids are KSA's own, so two games that make a craft the same way
  make one Id: two players who each send a kitten out of the same capsule both call it what KSA calls
  it. A game says, with each claim, whether it made the craft or found it in the world everyone
  loaded. One it made whose name another player already holds is a second craft, not that one, and
  the server gives it a name of its own and tells the game. The game says how it is built again under
  that name, and keeps flying it under its own.
- **How each new craft is built**: the definition of every craft made since the world was saved, handed
  to every game in it and to each game that loads that world later; and which craft have gone since.
  A new save drops what it already holds. A game that loads the world later is handed only the
  definitions of craft another player has: one described by somebody who has since left is nobody's,
  and its shape after the last save went with them.

It passes every state on to every other game in the world, checks a state comes from the craft's
owner, and passes chat on. It refuses a player with another KSA build than it was built for (`KsaBuild` in `Directory.Build.props`, written into the server when it is built and checked against the assemblies whenever the mod is; the operator's `ksa-build` says another), one playing a different celestial system (a world of Sol
entire is not a world of Earth and its Moon alone), or one whose mods are not the world's
exactly. Without one the world's giver had enabled, a save holding a part of it does not load; with
one they had not, that player builds craft nobody else can put together, and all the others see is
a craft arriving that their game refuses, which reads as the mod being broken. A world given by a player
carries that player's system; one an operator imports carries whatever its own `meta.toml` says.

### What it does not take on trust

A datagram says where it is from and nothing shows it, and a game that is in says what it likes.

- **A game's commands and its goodbye carry its session's word**, which the welcome that admitted
  it gave: a command with an admin's address written on it is otherwise the admin's. A game says
  in its hello that it says the word back, and one from before does not and is held to none, so
  the two share a server. A game holds the server's goodbye to the same word.
- **A game of another version is answered with seven bytes**, off which it reads the server's
  version; the sentence it shows is its own. A game already in a world never acts on one, since
  anybody can write the server's address on a datagram, but it remembers it: when the server it
  was in comes back updated, the silence that ends the session is put down to that, and the
  player is told to install the newest mod rather than that nothing came for ten seconds.
- **A wrong password counts against its address**: five in a minute and the address is refused for
  the rest of it, with the right one too. Compared as hashes. It still crosses the network as typed.
- **A world asked for is looked at before it is kept**: a zip, of names a save has, unpacking to
  under 512 MB by its own account. One that is not is treated as a world its game would not hand
  over, and somebody else is asked.
- **Room for a world is kept only for a player asked for one**; any other transfer may say 4,096
  pieces (4.5 MB, a craft). 256 MB of craft definitions are kept from one player and 1 GB from all.
- **One craft passes on 60 states a second and 120 of mods' words**, and a player 2 chat lines and
  5 commands a second after a burst of 10 and of 60; a player holds `--max-craft` craft. What is
  over is dropped, and the log says `is over a limit` once a minute. The limits are for a craft and
  for how many, never for a player's whole traffic: a salvo of a hundred craft is play.
- **Who holds what is told at most twenty times a second**, and less often the more pages it runs to.
- **The roster is said with the names of craft cut short** when thirty players flying long-named
  craft would not fit a datagram, which had it go to nobody.
- **Nobody is called `server`**, which is how the server's own words are shown, nor by a name with
  characters in it that are not seen. A line of the log is one line whatever a craft is called.
- **The login service's key is fetched again at most once a minute** for a certificate the key held
  did not sign: any game can send one nobody signed.

A game does not take the server or the other players on trust either. What it unpacks is counted as
it unpacks (512 MB for a world, 64 MB for a craft), and a name is asked of the file system before
anything is written under it.

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
the server sends it once, and said over the editor until it is: said once in the log, it was missed
by a player put back in a restarted `ksamp.gg` from the editor, who sat there waiting and gave up --
and the save folder is emptied before a save and read only if something
was written. Otherwise the handover save left there from last time is zipped and handed on as the
world now, and a game counts its thirty frames over a world it never loaded and says it is in the
shared one.
A craft launched from the vehicle editor is named again as the editor closes, where something
here or another player's craft has taken its name since the editor opened: KSA names it then and
launches it without looking again. A duplicate is refused by KSA's lookup and never simulated, and
taking it away removes the other craft's entry and the Sun's slot, after which the Universe
Manifest is empty and the next vehicle editor crashes the game. Its seated kittens are moved to the
new name with it. `World.Destroy` also leaves alone any craft the lookup does not hold under its own
name, and a lost Sun is logged, told to the server and shown to the player once.

4. **Playing**, each frame:
   - **The flown craft is claimed**, when it changes and every two seconds, unless another player
     holds it: then this player is watching it, is told so once, and claims nothing. A game that
     joins loads the world still flying whoever gave it that world, so two players begin on one
     craft every time, and watching is what the second one does until they fly something else.
     One nobody holds is claimed only once it has been flown for three seconds, as long as a
     watched craft has to be watched before it is taken: flicking through the vessel list claimed
     every craft in it, nine in four seconds on `ksamp.gg`, each then that player's to lapse and
     spared by a clear. One this game made, or holds outright already, is claimed at once.
     Nor is a joiner flying the craft the world was saved flying when nobody holds it: that is
     whoever saved it's, and on `ksamp.gg` a player joining an empty room was put at the controls
     of the shared station, took it and undocked it in half within the minute. The game lets go of
     it two seconds in, once the owners have arrived, leaves the camera on it and says so, and does
     not take it for being watched; choosing it, by clicking it or switching to it, flies it.
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
     the craft, and the server asks somebody else rather than keeping a world nobody can open. A craft whose parts change, as one that stages does, is described again. A craft taken back whose
     definition the server holds is this game's, and which has not changed shape since, is not:
     every game built it from that, and a station handed back and forth was otherwise sent whole,
     160 to 240 KB, on each return and built again in every game. Nor is new debris described twice:
     a claim the server has not answered is held to for five seconds, where it used to be let go of
     on a roster older than the claim and taken, and described, again. A name the server gave a
     craft this game made, when that craft has gone by the time the rename comes, is said gone, so
     the server holds no record of a craft that does not exist. And a craft a game has just made
     under the name of a craft nobody holds that another game described is given a name of its own,
     as one under a held craft's name is: KSA numbers the pieces each game sheds by itself, and the
     second game's piece took the first's record and replaced it in every world. A game making its
     own craft again, or one undocked under its own name, keeps the name.
   - **Every other player's craft is a puppet.** Built from its definition if this world has none
     (`Vehicle.CreateVehicleFromSaveGameData`, or `KittenEva.CreateKittenFromSaveData` for a kitten),
     one a frame, and then remembered by name: KSA does not list a craft the moment it is made, so a
     world asked whether it holds one says no for a frame or two and would otherwise be told to build
     it again. A definition of a craft standing here that no other player has is held, not acted on,
     until somebody has it: only another player's craft is built from one, so the craft would be
     taken down and nothing put back. A definition whose bytes are the ones already held is ignored rather than acted on, for
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
     after being built, before it is drawn, having been built where it stood when it was described;
     put there from its owner's newest word even when that word is seconds old, because built from a
     game behind the clock KSA carries a piece along an orbit to this world's time, through the
     ground if it was falling. That takes only on a craft something is pushing. One that
     is coasting is stepped along its own orbit by KSA whatever is written into it, which is right to
     the millimetre while that orbit is its owner's; where it is not, the craft is given its owner's
     orbit through KSA's own mover, in the frame's quiet window, and at once when its owner stops
     pushing it or when the pull has had to put it anywhere outright. How far out it is, is measured
     against that same orbit carried to this world's time, so a game behind the clock still
     corrects what coasts, and a craft put on its orbit measures nothing: under 5 cm it is not put
     on at all, since each put-on takes it out of physics. A word off rails re-arms the put-on
     only when its engine was on or it lasted half a second, since KSA flickers a coasting craft
     that shares its owner's bubble between freefall and manoeuvring by itself. While its owner
     burns and the copy is on rails here, which throws away what the pull writes, it is put on its
     owner's orbit every half second it is over 25 m out, and what the pull did is written down once
     a second. Without physics it is placed once a second at most, and only if 25 m out. A warp's
     step is too long to draw a craft on over, so through a warp the pull only turns it as its
     owner has it, in its physics state and in the craft's own attitude, which is what KSA's step
     on rails starts from when there is no spin to carry on. Left alone on rails, KSA carries a
     craft on from the spin it last had: on `ksamp.gg` a rocket being spectated was drawn sideways
     through its pilot's warp, and swung back the moment the warp ended. Seen with two games, a
     rocket held prograde in a 207 km orbit through an 80 to 100x warp: its copy within the degree
     the bridge rounds to of its owner's, read in both games at once, through the warp and after it.
     Not seen: the sideways rocket itself in the rig, to set against.
     `Track` says where each is at this world's own universe time, since every world keeps the same
     one: carried on from its newest state by the speed it had and the way that speed was changing.
     Where it can say nothing of now -- a second and more past its newest word, or a world behind
     every word it keeps, as one catching up after a load is -- the pull only turns the craft and
     leaves its place and its speed to KSA. Writing in the speed of another time stopped gravity
     bending its path: on `ksamp.gg` a copy of a craft in orbit, watched by a game catching up from
     minutes behind, flew straight down into the ocean and was 1,229 km off its orbit when it was
     put back on it. A craft its owner has asleep on the ground is the exception, being at the same
     place in its body's fixed frame at every time, so it is pulled there however old the word; and
     no craft is put on an orbit that has it under the ground by now. A game 11 s behind said its
     rocket was dropping the last metre onto its pad, that carried on as an orbit put the copy 642 m
     under the pad, and held from there it stayed under it until its pilot launched.
     Not a little in the past, which is smooth and at orbital speed is a kilometre away from every
     craft this game steps for itself; `errors.md`, 2026-11-10. The one a player is in carries their name, within 2,000 km and in front
     of the camera; the rest of what they own carries a faint `Alice's Lander` within 20 km, which
     is also why it will not let you aboard. In the map the craft each player is in carries their
     name however far off, with its distance from this player's craft, and nothing else of theirs
     is named: the map's camera is further than either range from every craft, so by range it
     named nobody. Seen with two games 13,000 km apart, each naming the other in the map and not
     in the orbit camera; how the names sit among KSA's own labels has not been looked at closely. A name over the world says where somebody is, and a
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
     **Nor does the vehicle editor**, which KSA stops the world for on every frame it is open
     (`Program.PrepareFrame`): the shared speed is held there too, and KSA, which lets go of the
     craft being flown as the editor opens, goes on stepping it with nobody at the controls. So the
     craft flown into the editor is still this player's flown craft -- claimed, said to be flown,
     and nobody else's to take -- and where it was in the air, close enough to its body to be
     stepped through physics and touching nothing, it is **held where it was**: put back in its
     body's fixed frame on every physics step and stopped against the surface with KSA's own
     `StopRelativeToSurface`, so it turns with the ground under it, with its engine cut. It is let
     go of on the first step after the editor closes, moving, turning and burning as it was when it
     was held, so the editor is a pause for that craft alone and never a way out of a crash.
     That is the editor opened on the craft. Opened on a new vehicle, the craft is left as any craft
     is when its pilot looks elsewhere -- theirs for ten seconds, then anybody's, never tinted or said
     to be in the editor -- unless it is in the air and held, when it stays theirs as above. On
     `ksamp.gg` a player went to a new vehicle to keep out of a warp's way, and the craft another
     player had just given them was drawn in every game as theirs, in the editor, the whole time. Held
     or standing, its states carry `Editing` while its pilot is in the editor, and every other game tints it with KSA's own purple picking tint and
     writes **IN THE VEHICLE EDITOR** under its pilot's name; the scoreboard says so as well. The
     tint comes off as the craft stops being that player's, too: KSA never clears a tint it did not
     put on, and a craft taken over when its pilot left from the editor stayed purple for the player
     flying it. Held,
     it is still solid: anything flown into it stops against it, so nobody can take its place
     before it is let go of. **Being at no craft is not an exception.** A game whose camera is on a
     planet, or whose kitten has died, flies and watches nothing and still draws everybody else's
     craft at its own universe time, so it is held to the server's like any other: let go of there,
     a warp of fifty for twelve seconds left it 229 s behind the room for good. A warp key
     pressed from a menu that has stopped this world is taken and ignored, the same as one at either
     end of the ladder: nobody is in this player's menu, and asking the room to warp while not
     running at it oneself is not a question worth putting.

**What nobody owns and somebody is near is held by that somebody's game.** A craft nobody owns is
stepped by every game near it, each in its own way: a rocket nobody flew was 3.4 m from itself in
two games after a walk of warps, and a parked craft one player drove into moved in that player's
world alone. A game asks for every such craft within 10 km of what its player flies, or watches
while flying nothing, and for any that is moving under its physics wherever it is (`Claim.Near`,
once a second); the first to ask holds it, says where it is
like any craft of its own, and every other game draws it there. The hold keeps nobody out: the
roster marks it (`Owner.Near`), it carries no name over the craft, and the server gives it at once
to whoever asks to fly the craft, send its crew out or take it by watching. It is kept by being
said -- each state of such a craft carries `StateFlags.Near` while the craft is still within reach
-- and the first state without it, or three seconds of silence, lets the craft go to whoever has
it near next. Not by KSA's own physics alone, which steps every craft in a world wherever it is:
held by that, the first game into a world held all of it. A craft held this way is not written
down for the others until it changes shape, since it is the craft they already have.

What nobody owns, nobody is near and nothing is moving is left to each world, and goes the same
way in every one: at rest, or on rails. A game that is only watching another player's craft asks
for nothing: that player's game is there, and holds what is near. When KSA takes a craft out of one world that nobody owns,
or that another game holds only for being near it -- wreckage expired or cleared to make room for
a new breakup, a craft recovered from a menu -- that game claims it and says it gone, so it goes
from every world. A game that holds a craft for being near never wrote it down, so it could not
say how to build it again. Not where more than eight go in one frame and they are most of what
there was, which is a world being unloaded.

**A piece is built when the craft it came off is built without it.** A piece that breaks off is
described at once, and the hull it left is built again up to two seconds later; built at once,
the piece's copy stood inside a hull that still had the piece on it and was held there by KSA's
collisions. Every definition carries the time the server was given it, and a piece waits until
the nearest craft in its name's ancestry that stands here was built from a word written after
its own, for at most five seconds. Until then it is still to be seen where it was, on the hull.
A hull a piece is waiting on is built again after one second and not two. Built late, a piece
is put where its owner has it before it is first drawn: on its orbit, or where it lies.

**A craft nobody flies is said early when it leaves its path.** Craft under way share two hundred
states a second, a fifth of a second each for forty pieces, and most of them spend that falling
as their last word said. A game carries each on from its last word as the others will, and one
that is a quarter of a metre from where that puts it takes its turn at once, out of two hundred
more a second. `Track` carries a change of speed on only where three states agree on it: two
say it changed, not that it goes on changing.

**How a craft is built is taken in the order it was written.** A definition carries the universe
time its owner's game wrote it at. The server drops one written before the one it already has
from the same game, if that one was heard in the last thirty seconds: on a wire that loses
datagrams the first of two can finish arriving after the second, and the last to arrive was what
every other game built and what everybody who joined later was given.

**A server can be held to a build of a mod.** A game says what it has enabled with the version each
mod's own assembly carries (`World.Mods`, read off the file of the assembly each mod's `mod.toml`
names, found from the mod's name where StarMap finds it, since KSA knows a mod's name and nothing
of its version), and an operator's `mods` may name a version
after an `@`: a release, or a release and the beginning of the commit it was built from
(`ModList`). Two builds of one mod are one name and two mods -- a car mod that says how its cars
are steering and one that does not. The list a world's first player brought is still ids alone.
A player with another version is told the two releases, and the commits only where one release
was built twice and then seven characters of each: forty of a commit, in the line a player acts
on, were read as noise and hid the two numbers that mattered. And it is said apart from a mod
that is off (`Sync.ModsAgainst`), because "turn it on" about a mod that is on sends a player to
a switch that is already where it should be.

**Debris is swept by the games.** `debris` sends a `Swept` marked as debris alone, and each game
takes from its world what matches and `World.IsDebris` holds: what KSA marks as debris
(`PartFailure` sheds it so) and any craft whose parts carry no `Control` module, a spent stage --
not `Vehicle.IsControllable`, which a probe out of power is not. The server drops nothing for it,
since a name does not say whether a craft is debris; the owner's game says gone what it swept of its
own, and the save asked for after is from a game that has swept. A game of `Wire.Version` 31 would
take the sweep for one of everything nobody flies, hence 33 (with `Spared`). Seen in two games: four pieces shed by a rocket went from both worlds,
and the rocket, its kittens and every other craft stayed.

**A mod may be optional.** An operator's `optional-mods` names mods a player may run or not:
the join check leaves them out of what a game has (`ModList.Without`), a world a player saves is not
said to want them, a world saved before they were made optional stops wanting them, and a welcome
and a `Facts` end with them (`Optional`, after every field a reader of before knows) so a game does
not tell its player to turn one off. Meant for mods that change one player's game and nothing it
writes that another cannot read: KSA holds part scale to its limits in the editor alone
(`VehicleEditor.ScaleBoundsFor`), and a part's x, y and z scale load as saved, though mass and what
a part's modules scale by follow the largest of the three (`ScaleFactors(double3)`), the same in
every game. Whether that is fair is the operator's call, and why the list is theirs rather than a
mod's word about itself. Not yet seen with such a mod in a game.

**Players' logs go to the developer's test server and nowhere else.** As a game joins it sends its
logs of the sessions before this one, the newest three of the mod's and the newest three of KSA's
own not sent before -- KSA's says what it did to its own world, a name registered twice or an
exception in the editor, which the mod's cannot; one KSA names `.abnormal-exit` is a crash, and
the session being written now is told by its process id and left out, every file of each KSA
session going together, since one that ended badly leaves an `.abnormal-exit` summary of its last
lines beside the full log. `playerlogs [name]` at the server's console, and only there -- a game
that types it is told there is no such command -- asks every game, or one, for its logs
now (`Wire.LogsWanted`, for which the protocol is 34, so no game on the server can leave it unanswered), this session's as it stands among them,
for something going wrong while people are still in it; each ask lets a game send a full set again
(`src/Mod/PlayerLogs.cs`), each cleaned of addresses and of the user's name in its paths
(`LogText.Scrubbed`), as a `BlobKind.Log` the server has always been able to read. Which server is
the game's decision, not the server's: only when the address it joined is what `ksamp.gg` resolves
to, on port 50100, so another operator cannot have players' logs by saying anything, and the
setting a server has, `collect-logs`, only decides whether that server keeps what it is sent --
on, in `infra/server/server.tf`, for that one container. There is no switch to stop it in the
game; the chat says when it has happened. A previous session is what is worth having: a crash or
a mod that switched itself off is written there, and a player cannot be asked for it once two
more launches have written over it. The server cleans each again, keeps them
under `player-logs/<day>/`, at most eight a session, and takes away days older than two weeks. Each is filed as
`discord-<id>.player-<n>.<session>.<time>.log.gz`, or `no-discord.` for a player not logged in
with Discord: by the account the server has checked and their number on it, never by name, which
a player types, another can type too, and would land in a path. The name heads the text instead,
on one line with the number and the Discord account. The number is this join's, so a player not
logged in with Discord is told apart within a session and not across them. `KSAMP_LOGS_TO=host:port` in
a game's own environment points it elsewhere, for trying it with two games on one machine.

**A game turned away for its mods is handed a link to them.** `ModLink` writes the world's list
as Borea's `borea://modlist?mods=Id@1.2.3,...&name=...&id=...`, and the connect window puts a
button for it where `CONNECT` would be greyed. Borea knows nothing of this mod and this mod knows
that one line of Borea. A link has to say an exact release of every mod, which a world's list
often does not, so a mod the world leaves open is given the version this game has, KSA's own
`Core` is left out, a commit after a `+` is dropped, and where a mod has no version anybody here
knows there is no link and no button. The `id` is how Borea knows a list it has made a game from
before, to offer bringing that one up to date, so it has to outlast a server's name. It is
`ksamp-` and the operator's `id` setting, which a server says in its `Facts` and in the welcome
that carries a cookie. It is a setting and not something the server makes up and keeps, because
all a server keeps is its world, and a world is thrown away and begun again far more often than
a server becomes another server. One with no `id` is known by the address the player reaches it
by, so under two addresses it is two lists, and at an address that moves a new one each time.
What is handed to the shell is only ever a line `ModLink.Is` knows as its own, since a server
had a hand in it.

**What another mod knows of a craft goes with the craft.** A car's steering and its wheels are in
the car mod's memory and in nothing KSA saves. `Guests` listens on StarMap's event bus for what a
mod says of a craft this player has and sends it with the craft's state (`Wire.Extra`, opaque
bytes under the mod's own channel name), hands what arrives to this game's copy of that mod, and
tells mods which craft are held here for another game, so that a mod with physics of its own
leaves those alone. Neither mod refers to the other; `docs/MOD-API.md` has the five names they
share.

**A docking is done in one world and told to the rest.** KSA docks two craft where its contact
solver or its port menu says so, in whichever game sees it first, and takes one into the other. That
game tells the server (`Wire.Dock`, the craft that went and the craft that is left), and the
server's rule is one line: the craft that is left is that player's and the other is out of the
world, whoever had either, provided one of the two was theirs. Where the other is another player's
in earnest, both must have been said by the players who have them to be within 500 m of each
other in the last ten seconds: taken on trust, a game's word that two craft docked took any craft
in the world from anybody. Everybody else is told the same
datagram back with the new owner in it. A game takes the craft that went out of its world,
whoever's it was, and gives up the craft that is left if it was its own; a player who was flying
either is left watching the craft that is left, and is told whose it is. The game that docked holds
the craft as its own from the frame it docked in, without waiting for the server: pulled to where
its last owner had it, the stack would be pulled by the length of what had just joined it. It says
the docking before it says anything else of the craft that went, since said gone first that craft
is nobody's when the server hears it docked. Two games may both dock the same two craft a few
frames apart; the first is the one the server takes, and the other is told like anybody else and
drops what it made. `DockingPort.Dock` is refused where both craft are other players', and where
one is and the other is this game's only for being near it: that docking is the other player's
game's to do. `DockingPort.Undock` is refused where the craft is another player's.

Undocking is KSA's own split in the owner's game: the half that comes away has its old name back,
is a craft that game has just made, and is described to everybody. The game's claim on it says it
came away at a port (`Wire.Claim.Undocked`, set from a postfix on `DockingPort.Undock`), and the
server's log names who did it (`undocked`) by that alone: KSA gives a freed name to the next piece
shed in a crash as well, and taken by the name, a station breaking up was logged as its owner
undocking a module. The server keeps what each craft a docking took went into until that name is a
craft again, to say what it undocked from; after a restart it names the undocking without that. Where the half KSA leaves its
player flying was another player's before the docking, this game flies the other half instead. The
player who rode along is left watching their own craft again as soon as it stands in their world,
and it is theirs by the rule every craft follows: its owner has not been near it for ten seconds
and somebody flying nothing has been watching it for three.

Another player's craft cannot be taken here at all: prefixes on the setter of
`Program.ControlledVehicle`, on `KittenEva.BoardVehicle` and on `EVADoor.PerformEvaForSeat` leave
control where it is, turn a kitten away before it climbs in, and refuse to empty it. Without them a
player can still point at someone else's craft and press keys; nothing they did would reach anyone,
but it would happen in their own world, and a kitten pulled out of someone else's capsule would
become a craft of their own.

Refused control leaves this game controlling the craft it had, and KSA sends keys and fills the
gauges from that craft whatever the camera is on: a player watching somebody else read their own
speed as the other's, and could throttle or stage their own rocket off screen. So while the camera
is on another player's craft (`Sync.Observed`), prefixes on `Vehicle.OnKey` and `KittenEva.OnKey`
leave the controlled craft's keys unhandled, the one on `PrepareWorker` clears what was held as the
camera moved away, as KSA does when its own control is switched off, and prefixes on
`Program.UpdateGauges` and `NavBallRenderer`'s two per-craft methods hand them the watched craft
instead. The player still flies their own craft as far as everybody else is concerned, so it is not
let go of meanwhile; the mission clock is still theirs.

KSA's debug Editor Toggle opens the editor on the controlled craft, so while the camera is on
another player's craft it would open on the player's own, out of sight, with the one they are looking
at nowhere in it. The toggle is taken back the frame it is asked for and the player told whose the
watched craft is and to ask for it (`Sync.KeepTheEditorOffWhatIsWatched`). A new vehicle opens as it
always did.

A puppet is spared when a crash in this world would wreck it -- a prefix on
`Universe.DestroyVehicleFromEvent` refuses the destroy along with the explosion and the twelve pieces
that go with it -- and is taken away only when its owner's game says it is gone. Otherwise one game's
collision would wreck a craft that is fine in its owner's world, and the pieces would be taken for
this player's.

A craft waiting to be taken out of this world is spared the same way. One its owner says has
crashed is no puppet for the frame it is told in, and the crash this world's own physics had ready
for it went through in that frame: its pieces were shed here under the names its owner's pieces
have, and stood in for them.

So is a craft that was another player's moments ago, for thirty seconds after it stops being a
puppet -- every craft of a player who leaves stops at once -- unless this player is flying it.
Whatever this world's physics had been trying to do to it was refused while it was theirs, and went
off the instant it was not: on 2026-11-23 a protected station was wrecked in one game nine seconds
after its owner left, into KSA's twelve pieces, and that game, holding it for being near, said it
gone. Each refusal is written down once a minute a craft, with the cause and the G-load, which is
what would have said something was wrecking it all along. And the server does not take the word
of a game that took a protected craft over less than a minute ago that it is gone: it stays in
every other world and on the list, and is nobody's.

Docking is the same rule. KSA docks two craft in the world whose physics saw their ports touch, by
taking the lighter into the heavier, or from a port's own menu when two are within 2 m; both end in
`DockingPort.Dock`, and a prefix there refuses it where either is another player's, as one on
`DockingPort.Undock` refuses to take another player's craft apart: let through, one player's rocket went into a copy of the other's and was gone
from every world, while its owner flew on with the parts they had. A craft docked with one nobody
flies is docked, and the craft that went into it is claimed so it can be said gone -- only its
owner may say so, and it had none.

**Leaving a server is said on the screen.** `Sync.Disconnect` is every way out of a session --
the button, a connection gone quiet for ten seconds, a server that closed or refused, a world
that did not load -- and from a game that was in one it calls `Panel.Disconnected`: a red
*Disconnected* on ImGui's foreground list, over every window, for a second and a half and faded
over one more, and the Multiplayer window opened again. A connection lost while that window was
shut used to be a world that simply stopped answering. Leaving one server to join another is
`Disconnect(quietly: true)` and says nothing.

**A craft is renamed by the server, and every game follows.** KSA lets a craft be named only
before it launches, in the editor and the launch menu; `CelestialSystem.Rename` exists and nothing
in the game calls it. A craft's name is what every game and the server know it by, so a rename is
asked of the server (`Wire.Name` with nobody in it, from `/rename New name` for the craft flown or
`/rename Old -> New`), which refuses a craft that is not the player's own -- held for being near is
not their own -- a protected one unless an admin asks, and a name a craft already has. It then
moves everything it keeps under the old name to the new, keeps a protected craft protected, and
says `Wire.Name` with who asked to every game, twice again over the next second, to any game that
speaks of the craft by its old name in the next thirty seconds, and to a game that loads a world
saved before it; it refuses the old name to a claim for those thirty seconds, made or not, since a
game that has not heard would otherwise make it a second craft. A game moves every record of its
own from the old name to the new at once, knows the craft by the new name through `Renamed` from
that frame, and renames it in KSA in the quiet window, the crew's assignment with it as the launch
menu moves them, unless KSA has something else by that name. How it is built stays as its owner
said it, under the old name inside: a game building it from that knows it by the new one as it
does a craft renamed on a clash. A body's name is refused by the game, which knows the bodies; the
server does not. Protocol 29.

**A touch of two ports that docks nothing is written down, here and on the server.** KSA docks on
a contact only where the two ports are under half a metre apart, within ten degrees of facing each
other and closing with the port's latching energy, and says nothing of a contact that fell short:
the craft bounce, in one world, and nobody else is told. `World.PortContact` reads KSA's own flag
for a hull that met another in the step just solved and, where a free port of each craft is
within a metre and a half of the other, says which ports, how far along and across the line out
of the port they were, the tilt and the roll between them, how fast they were closing, both
masses, and which of KSA's conditions was not met -- or that all of them were, which is the case
worth being sent. `Sync.MayDock` says why when it refuses a docking. Both go to the game's log and
to the server's (`Wire.Note`, a line of text a game says for the operator's log): whoever runs a
server is who is asked why a docking failed, and the only account of it was in a log on a player's
machine that the next start of the game threw away. The server writes a note down under the
player's name, clipped and with nothing in it that is not print, from a game that is in the
world, and five at once and one every five seconds after: it is a line in the operator's log on a
player's word. `Wire.Note` is a kind a server of the same version that has never heard of it
drops unread, so `Wire.Version` did not move for it.

The roll in a note is how far the two ports are turned from where their guides slot together, not
the angle between them. KSA's androgynous port docks every 120 degrees, at 60 and 180 degrees
between the two ports' second axes; Align with roll Up holds 180 (it aims at the target port turned
half a turn about its Z), and roll Down, by KSA's source and not yet seen, holds 0, which is guide
tip on guide tip. Sent as the raw
angle, a perfect Align read "180 of roll" and a player asked how it could be half a turn out. Where
the ports are 30 degrees or more from a docking roll the note says the guides met and to hold Align
with roll Up: 30 has bounced off, 11 has docked, and nothing between has been seen.

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

**A craft read from saved data keeps what its tanks held, which KSA `2026.11.16.5901` and
`2026.11.25.5949` do not manage by themselves. This is a workaround for a bug in KSA and comes out
when KSA fixes it.**
`Vehicle.DeserializeSave` puts each tank's contents back and then applies the craft's fuel lines,
and with one line or more that runs `PartTree.ReconfigureTanks` before the craft's resource managers
exist: no tank has a consumer yet, so every tank not manually assigned is given no mix and emptied.
Every craft a game builds from another player's definition is read that way, and so is every craft
in the world a player is handed on joining, so a station with a fuel line was dry in every game but
its owner's and dry for everybody once another player's game had held it. `Hooks.BeforeReconfigureTanks`
answers for KSA while a craft is being read (`BeforeReadingCraft`, counted down in a finalizer) and
lets it through at every other time, the editor's included. Reported on KSA's forum on 2026-11-21
as "Loading a save empties every auto-assigned tank on a craft that has a fuel line". The test for
taking it out is KSA with no mods: load a save holding a fuelled craft with a fuel line, and if the
tanks are still full the three patches and this paragraph go.
Seen in a game on both paths, with a station of 143 tanks and three fuel lines holding 591,720 kg:
a world loaded in no server kept all of it where KSA alone kept none, and with two games on one
server Bravo took the station from Alpha, Alpha's game said `rebuilding RCS Tanker Deux` and `built
RCS Tanker Deux, Bravo's`, and the copy it built held all of it. Not tried: a craft with a manually
assigned tank, which KSA's pass would have left alone anyway.

## Transfers

A world or a definition goes as a `Link` transfer: cut into 1,100-byte pieces, each whole in a datagram
under the 1,280 bytes any path carries, sent at 400 KB/s after a first burst of eight. The receiver asks
four times a second for what it is missing below the highest piece it has, and for everything after a
second with nothing; a sender that has heard nothing two seconds after sending everything sends it all
again. The receiver gives up two minutes after the last piece, the sender two minutes after it last
sent a piece that was due or asked for or heard anything. What it carries says what it is in front: a world or a craft,
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
token itself; the game writes it down and offers it in every `Hello` after. Nothing is asked of the
player, and nothing they could be shoulder-surfed typing. Revoking it removes the hash, so the token
stops working at once even for a game that still has it, and tells a game that is connected to throw
it away -- which is said by `Op(Admin: false)` and needs nothing else on the wire.

**An `Op` is said until it is said back.** The server sends it twice a second until the game returns
the same datagram: that it is an admin, recognised at the join; that it is one no longer; or a token to
keep. The first two are answered on arrival. A token is answered by `Client.Kept`, which the mod calls
once `tokens.json` is written, because a token that could not be written is gone when the game closes
and the one still on disk has to be worth something then. Only when a token comes back does the server
remove what that name was an admin by before (`Admins.Check`, which is also what a `Hello` goes
through, so a token shown at a join counts the same). Said once, with the old hash already replaced, a
single lost datagram left a name on the list that no machine held the token of. A player who leaves
before their game answered is an admin by either token, and the one they come back with stays; `deop`
removes both. The answer is the token itself in the clear, as every `Hello` already carries it. No byte
of the datagram changed and `Wire.Version` moved to 27 all the same: a game from before answers
nothing, so it would be handed the same token twice a second, write it down each time, and never have
the old one retired.

**Where a game files a token** is `TokenBook`, in the protocol so the tests can drive it: under the
address as typed, and under the IP address and port the join's datagrams went to. The typed address is
looked under first and the other only when nothing is there. A token the server takes at a join is
filed again under both, which is how one kept by an earlier mod, or found by its second key, comes to
be under the first. Filed by the typed address alone, every admin's game looked under a name nothing
was filed under the day the public server went from `mp.ksarmory.com` to `ksamp.gg`, and joined as a
player; the server's list had not changed. A list of servers does the same in miniature each time an
SRV record turns `host:port` into `host` (`Browser.Row.Where`).

It is never filed under anything the server says of itself -- its name, its `LinkId`. The token is a
bearer secret that goes out in the first `Hello`, before the far end has shown anything, so a key the
far end chooses is a way to ask for somebody else's token: a server claiming to be another would be
handed that one's admin. Where the datagrams go is the one thing in a join a server does not get to
say. That makes the second key exactly as strong as the first: an IP address that has passed to
another machine is offered the token once, as a name that has passed to another machine always was,
and a private address means a different machine on every network it is typed on. A refusal throws away
only the token refused, and only where it is filed under those two addresses, so a machine that has
taken over an address cannot make a game forget what it holds for the same server under another name.

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
up to ten minutes and then stops, so a process that has hung does not hold a seat and a rocket in the
world for as long as it exists. A minute was too short: on `ksamp.gg` a game in the vehicle editor
stood still for two minutes at a time and was put out of the world three times in a day; past that the server's own silence takes over, which is what used to
happen the moment a frame ran long. `Udp.Send` takes a lock for this, since two threads shared one
scratch buffer.

**A game that drops and comes back** does not match the session it left, because its socket takes a
fresh port each time it connects and a session is held under its address. A session that has not been
heard from for three seconds is let go of when a game joins under its name, which is what makes a
reconnect get its own name and its own craft back rather than becoming "name 2" and watching its own
rocket for ten seconds -- or two minutes, when the drop was mid-load. A session from the same
address that has sent nothing at all since it was let in, for a second, is let go of the same way:
a game answers its welcome within a frame or two, so one that never has gave up on the join while
its hello was on the way. On `ksamp.gg` a player who pressed connect twice in a tenth of a second
was in the room as himself, never loading, and as "name 2", which was him. Driven over loopback;
not seen in a game. The press itself is answered in the window: DISCONNECT is drawn where CONNECT
was, so the second click of a double-click on CONNECT left the join it had just started, and it
stays greyed for a second after a connect.

**A game whose frame loop stands still says so afterwards**, in its log and the server's: how long,
whether its window was in front, and how many times the keepalive spoke meanwhile. Minimising KSA
in a borderless or full-screen window with vsync on stops its frame loop outright -- presenting
throws `ErrorOutOfDateKHR` and `GlfwWindow.WaitForWindow` waits in `glfwWaitEvents` until the window
has a size again -- and a player on `ksamp.gg` was let go of about twenty seconds into one of those,
inside what the keepalive covers. With the rig, a game minimised that way for forty seconds stayed
in the world, its keepalive speaking once every two seconds; what stopped it on that player's
machine is not known, and the next such line will say whether it spoke.

**An admin's game is told which worlds are kept without asking.** The list goes to every admin
when they join or are opped and whenever it changes, as a `Blob` of a third kind (`BlobKind.Saves`,
`SaveList`) over the same `Link` a world takes: it has to arrive, and a hundred rows do not fit a
datagram. `Client` keeps the latest and hands it to nothing else, forgets it when told it is no
admin, and drops any row whose name it could not have asked for: the rows are drawn in a window and
sent back as commands. `Wire.Version` did not move for it: no datagram changed, a game from before reads a blob of a
kind it does not know as nothing, and a server from before answers `saves` as it answers any command
it does not have.

**In a server the save window is the server's.** `ServerSaves` is a subclass of KSA's own
`ImGuiWindow`, so the frame, the menu strip and the footer are the game's and the table is laid out
as `GameSaves` lays out its own; every button sends the console line an admin could have typed.
`GameSaves.Toggle` is patched to open it instead while this game is in a server, and the frame hook
puts KSA's window away if it opened anyway, which is what is left if KSA moves that method. A save of
this machine's is refused in `UncompressedSave.Load` unless this mod asked for it
(`World.LoadNamed`: the server's world, and the bridge): loading one used to leave the shared world
without a word, and is now a sentence saying to disconnect first.

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
as the name and no port, which is what happened before any of it existed: the server is then on
28500, where one listens unless told otherwise, or on 50100, where every one did before and every
one set up before still does. `Ports` asks both with a `Wire.Ask` and takes the one that answers,
the first of them if both do. 50100 is in the range Windows and Linux hand out for their own use,
so another program can hold it; a server whose data folder already has a world when it first writes
its settings writes 50100 into them, since its firewall and its players' saved addresses say so
and nothing about moving by itself would fail where anybody would see. A list anybody reads leaves
a port unsaid only when it is 50100, which is what a game from before takes no port to mean; the
mod's own list leaves 28500 unsaid. A port typed after the
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
is not shared and never could be -- the owner's game knows nothing of it -- so it is refused here
either way (`Sync.RefuseDelete`), and what happens next is who asked. A player who is no admin is
told whose it is and that only an admin can delete it; an admin's is sent to the server as the
console's `remove <craft>`, which takes one craft out of every game by name, the owner's too, as a
`Swept` naming it in full. The server refuses a craft being flown, which the game says first by the
pilot's name, and a protected one. Refusals are gathered and answered once a frame, because Clear
Debris asks for every piece in the system at once; the server saves the world three seconds after a
`remove` rather than on each, for the same reason. Recover on another player's craft is refused the
same way: it sends the crew home before it asks for the craft to go.

**Clear Debris finds the debris the manifest lists while KSA's debug "Override Is Controllable" is
on, which KSA `2026.11.16.5901` and `2026.11.25.5949` do not. This is a workaround for a bug in
KSA and comes out when KSA fixes it.** The manifest calls a craft debris by its own `IsDebris` flag, and
Clear Debris counts what it takes by `PartFailure.IsExpendable`, which passes over any craft with a
control module, and KSA's debug "Override Is Controllable" (in a craft's debug window) makes every
craft have one: with it ticked, a player on `ksamp.gg` had a manifest listing a page of debris
under a greyed-out button saying there was none. The manifest's two calls to its count, in the
menu bar and in the button's confirmation, are swapped by a transpiler for the mod's
(`World.CollectDebris`), which is KSA's own while the override is off and the same rule read from
the craft's real control modules while it is on. The override itself is never set aside, not even
for a moment: the physics worker reads it while the menu is drawn. Seen with the rig on
2026-11-24: a rocket crashed whole left twelve pieces, which KSA's own count made twelve with the
override off and none with it on; the mod's made twelve both ways, and the manifest's own Clear
Debris run with the override on took all twelve out of both games. `manifest {"override":…}` and
`manifest {"clear_debris":true}` on the bridge tick it and press it.

The manifest's rows carry a padlock (`ManifestLocks`), put in by a transpiler on the row's drawing
just ahead of `ImGui.SetNextItemAllowOverlap`, which comes right before the row's name: a craft's
row has no children, so the room its tree arrow would have is empty. Drawn first, an invisible
button there takes the click, since ImGui gives the mouse to the first item under it that does not
share, and the name is put down after it, sharing. It is shut for a protected craft, for everybody,
and open and faint for the rest for an admin alone; an admin's click is `protect` or `unprotect`.

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
so a late joiner built the craft back at the Earth and nothing moved it. That is answered where a
copy is built: it is put round its owner's body from their newest state before it is first drawn,
and moved if it is found round another. So the body is not part of a craft's shape, and a change of
body sends nothing; a late joiner is sent definitions only of craft somebody owns, who is saying
where they are.

The one gap left is a frame's worth: a craft this game is controlling before the roster says who
owns it is not yet a puppet, so a teleport then would move it here alone -- and the pull drags it
back within the second. Not worth a flag, given that this mod places landed craft through
`Vehicle.TeleportToLocation` itself and a refusal would have to let its own calls through.

## What has been seen in a game

- **Six changes from the 2026-11-24 logs** (two games on one machine, 2026-11-24): the editor
  refused while Bravo flew Hunter with the camera on Alpha's Niric, saying why, and opened as before
  on his own; spectating Alpha and pressing M left the map on Niric, and Bravo still said to be
  watching it; switching through Rocket, Gemini7 and Hunter a second apart claimed Hunter alone;
  Alpha dropping and coming back eight seconds later had Niric again, with Bravo spectating it and
  flying nothing the whole time and not taking it; Alpha joining alone into a world saved flying
  Rocket, once Rocket had lapsed, was let go of it, watched it for fifteen seconds without taking
  it, and flew it on choosing it; and undocking Gemini7 from Rocket was logged as an undocking.

- **A join by an address with no port, to a server on 50100 and to one on 28500** (two games on one
  machine, 2026-11-22): `127.0.0.1` typed alone found the server on each, in about four seconds from
  disconnect to in the world.
- **A chat line of `100%s sure %n %d %x done`** drawn as typed in the other game's window, and a
  player called `Bravo %s%n%d` in the chat of both, the server's lines in their colour and a long
  one wrapped, with both games still running; a join refused by a server called `100%s%n%d server`;
  an admin's command and a goodbye taken with the session's word; and a rocket flown apart by
  `tools/breakup.sh` with every claim held and no limit met.
- **A craft built from another game's definition through the reader that refuses a document type**:
  `built NewRocket_1, Alpha's` in the other game's log.

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
  datagram in fifty. `tools/motion.py --scene fly-dock`. The rocket nobody flies is held by the
  game flying the capsule and drawn within 3 mm in the other, where it was 3.4 m out.
- **A car from another mod, driven.** KSACars' buggy driven in one game is drawn in the other
  within 2 cm with nothing put outright, and that game's copy of KSACars shows it steering 2.1
  degrees as its driver does, at the same throttle, in the same gear and within 50 rpm. On
  loopback; the two mods found each other over the bus in both games.
- **Two players' craft, docked and undocked.** A capsule flown onto the port of a rocket the other
  player is in docks in the flying game: the server gives that player the rocket, the other is
  told and left watching it, and both worlds hold 110 parts. Undocked two minutes on, the rocket
  is its first owner's again ten seconds later. No craft put anywhere outright and none more
  than 47 cm from where its owner had it, over the lossy wire. Flown, the craft taken in is the
  flier's own; through the bridge's `dock` it is the other player's, who gets it back the same
  way. `--scene fly-dock-theirs`, `--scene dock-theirs`.
- **A craft nobody owns, wrecked in one game,** is gone from the other within the second: claimed
  by the game it was wrecked in and said gone.
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
  whole room: opening the VAB asked everybody else to pause. Watched working when the editor let
  go of the clock: the game sat at speed 0, the server heard not one warp ask, and on the way out
  the game was 1.76 s behind and ran at 1.1x until it caught up. The editor now keeps the shared
  speed instead, below.
- **Debris cleared off a launchpad.** Bravo launched the 89-part Rocket onto pad MP3 and had six
  of its parts fail, which shed ten pieces of debris onto the pad, and both games let go of every
  craft. Five minutes later Bravo's game cleared the debris, and both worlds were left with the
  73-part rocket -- not debris, and its site had seven pads free -- and every other craft on a pad
  standing. Run again with clearing by delete, twelve pieces went four at a time in three passes
  five seconds apart, and neither world had any debris left. An earlier run cleared by
  `Vehicle.Recover`, whose sweep of the bubble took the rest at once ("Recovering 'NewRocket_2_1'
  destroyed 9 debris"), which is why it no longer does. A craft cleared because its site is full
  has not been tried. With Alpha flying the kitten Niric 176 m from the pad, ten pieces of
  Bravo's debris were cleared all the same five minutes on, and the two rockets Alpha stood
  beside were left untimed and standing.
- **An admin's Delete and the manifest's padlocks.** Two games on one server, Alpha an admin and
  Bravo not. Bravo deleting Alpha's Niric was refused with "Niric belongs to Alpha. Only an admin
  can delete another player's craft." Alpha deleting Bravo's NewRocket_2 while Bravo flew it was
  refused with "Bravo is flying NewRocket_2 right now, so it wasn't deleted."; once Bravo had let
  go of it, the same Delete went to the server as `remove NewRocket_2`, both games took it out
  within the frame, the chat said "Alpha removed NewRocket_2", and the world was saved three
  seconds later. With Niric protected, Alpha's manifest showed a shut padlock on it and a faint
  open one on NewRocket_1, and Bravo's the shut one alone; clicks on the padlocks by hand
  protected and unprotected both craft. `clear NewRocket_1` with it protected and nobody in it
  left it standing in both games.
- **Watching a craft that stages.** The other game's copy of a craft is built again whenever its
  owner changes its shape, and KSA hands a camera whose craft is destroyed to the heaviest craft
  near it or else to the body it goes round (`Universe.HandOffCameras`): watching the 89-part
  Rocket stage in orbit put the watcher's camera on the whole Earth, following no craft at all.
  The camera is now put back on the copy built in its place, by name, with the azimuth, elevation
  and distance the watcher had -- a copy arrives with its owner's view of it, from its definition --
  unless the player has looked at something else since, or nothing of that name comes back within
  ten seconds. Seen: after the stage the watcher was following the 21-part Rocket in the frame it
  was built, looking at it from where it had been.
  That rocket kept its name through the stage because its root part was above the decoupler. KSA's
  split leaves the name with the half holding the root (`Vehicle.Split`), so a rocket built from
  its engine up drops a booster that keeps the name while its pilot flies on in a new craft, and a
  camera kept on the name rode the booster down. The camera now follows the player who was flying
  what was watched onto whatever they fly next, once it is built here (`Sync.FollowPilot`). Built;
  not yet seen in a game.
- **The vehicle editor with the world running, and a craft held for it.** A 2-part capsule lifted
  400 m and falling at 56.8 m/s had the editor opened on it: the game ran on at 1.00x with the
  editor up, its universe time kept with the server's, and the capsule stood 80.5 m above the pad
  in both games for the fifty seconds it was open -- KSA put it on its rails as landed, which the
  hold allows for. The other game drew it tinted purple and named it with `editing` on its tag.
  Closing the editor let it go at 56.8 m/s, said by both games within a second, and it fell on
  into the ground. Afterwards the two games' universe times were a tenth of a second apart, read
  a second apart, and the server had heard no warp asked for.
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
  beside the name carries it, as a pill against the right of the row (`Panel.PillAtRight`) --
  `wrong KSA build`, `turn mods on`, `turn mods off`, `other mod version`, `full`, `update your
  mod`, `server is older`, `no answer`, and a muted `recent` -- and `CONNECT` is greyed, with the
  reason beside it, for a row this game would be refused from. Rows were dimmed for this before there was a
  marker, and dimming fought the colour ImGui fills a row with when it is chosen or merely under the
  pointer, so a row could disappear as the mouse crossed it.

  A row also says the round trip, which cost nothing to know: the probe is a question and its
  answer, so the time between them was already being measured and simply not written down. It is
  taken from the ask that was answered rather than the first one sent, so a server that needed
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

- **A touch that docked nothing, in the server's log, and what stopped it.** One game alone on a
  server, the 94 t `Water and Air Delivery` flown onto the 12 t `RCS Depot` at 0.32 m/s, under
  0.1 m off the line and 0.4 degrees out of true, with the roll between the two ports set by
  `approach {"roll":N}`. At 0 degrees and at 120 the ports stopped 0.67 m apart and stayed there,
  and at 30 they stopped 0.64 m apart and bounced off:
  the game's log said so every two seconds and the server's every five, under the player's name,
  with the ports, both masses and "the ports were 0.68 m apart and KSA latches under 0.5 m". At 60
  degrees they went straight in and docked at first contact, as they had in three earlier runs
  whose roll nobody wrote down, at 0.29 m/s and 0.53 m/s and up to 0.27 m off the line. So it is
  KSA's own port, in a game with or without this mod: its guides meet where the two ports are
  turned alike and pass each other where one is turned a sixth of a turn, and KSA's rule for a
  docking asks nothing about the turn. A flight computer left at roll ANY arrives at whatever
  turn it had, which is what a player who lined up to the centimetre and bounced had done. A
  docking the mod refuses has not been seen noted, having not been made to happen, and one
  rocket flies one approach: 1.7 t of thruster propellant is gone after it.

- **A craft renamed, and a server lost.** Two games on one server, Alpha flying `Water and Air
  Delivery` and Bravo watching it. Bravo's `/rename Water and Air Delivery -> Hijacked` was
  answered "isn't yours to rename". Alpha's `/rename Hauler One` renamed it in both worlds, KSA's
  own name included, 49 parts and Alpha's still in each, said once in each chat and built again
  in neither; twenty seconds on it was still `Hauler One` everywhere. The server was then killed
  with Alpha's Multiplayer window shut: ten seconds later both games said they had lost the
  connection, put *Disconnected* up and had the window open. Not tried in a game: an admin's
  rename of somebody else's craft, a protected craft, a game joining after a rename, and the crew
  roster following the name -- the server's half of the first three is in its tests.

## What has not been tried

What is waiting on a session with several players on the live server is listed, as things to look
for, in `docs/LIVE-CHECKS.md`.

- **A copy left to coast while there is no word of now, and the editor's tint taken off a craft
  taken over.** Both from one session on `ksamp.gg` on 2026-11-24: a game that joined behind,
  watching a craft in orbit whose pilot was in the vehicle editor, drew it falling into the ocean
  for 20 s, and once that pilot left and the craft was taken over it stayed purple. Later in the
  same session a rocket whose pilot's game was 4 to 11 s behind stood under its pad until it
  launched, and pieces of his rockets were built inside the sea at up to 11,000 g. Seen with the
  rig on 2026-11-24, one game held 6 to 7 s behind by suspending its process: its craft in a
  393 km orbit, watched in physics by the other game while the owner caught up, read the same
  altitude in both games to half a metre at equal universe times, with no put-on needed; a
  rocket crashed whole on its pad by the game 6.5 s behind was built in the other as twelve
  pieces lying on the pad, none spared from any force; a craft on its pad held its 2.44 m; and a
  craft moved 6,965 km by its owner was put on its new orbit. The editor opened on a new vehicle
  left the craft its pilot was in, shown flying nothing and untagged, and a watcher flying
  nothing took it; opened on the craft, it stayed theirs and tagged for as long as it was open.
  Not seen: the purple tint coming off a craft taken over, a craft held in the air through a new
  vehicle, and anything of it with three or more players. Suspending a game's whole process
  also stops its keepalive, so past ten seconds the server lets it go whatever the keepalive's
  own limit is.

- **Players' logs to the test server, on `ksamp.gg` itself.** Seen with two games pointed at the
  rig's server by `KSAMP_LOGS_TO`: each sent its last three sessions as it joined, the server kept
  all six, cleaned, and a rejoin sent none again. KSA's own logs went with them, a crashed session's
  `.abnormal-exit` among them, and `playerlogs` at the console had both games send the session in
  progress, the mod's and KSA's, within the second. Not yet seen: a deploy with `collect-logs` on,
  and a game deciding by `ksamp.gg`'s own address.

- **The 2026-11-22 review's work, in two games on 2026-11-23.** Seen with the rig, on loopback and
  a Windows server: a launch renamed as its editor closed, both editors having opened on
  `NewRocket_2`, the same three craft in both worlds after; a craft let go of only after its owner
  had been away 10 s and a watcher flying nothing had watched it 3 s, and sixteen looks of about
  1.5 s each through two craft letting go of nothing; the gauges filled from the watched craft and
  both halves of a key kept from the controlled one, with the same key reaching it once the camera
  was back on it; four pieces of debris described once each; `debris` taking the four and nothing
  else, and `clear` naming and sparing the four craft their players held and had flown; the 15 s
  countdown said in both games, the last save, and the goodbye's reason; both games told the
  server they lost was updated, by one put in its place that spoke 34; `fresh` refused over its
  own empty world 24 s later; `players` with ping and loss; a coasting copy put on its orbit
  three times all afternoon, at 0.3 to 0.4 m, and none at 0.0; a burn and a meeting 40 m apart
  drawn within 2 cm; a breakup with every claim of `breakup.sh` holding; and the mod's previous
  log kept under the time it began. **Not seen:** the countdown drawn on screen (a capture hides
  the interface), a crew moved with a renamed launch (the design had none), a lost Sun's warning,
  a craft kept for its watcher against another game's ask, a joiner's save asked of a third game,
  a copy under thrust on rails here, a game behind the clock correcting a copy, a copy wrecked
  before it was a puppet, a release's 40 s of grace answered by a deploy on the VPS, the camera
  following a pilot onto what they fly next, and optional mods.

- **A Discord login on a port the system picks.** The listener and the service's redirect are
  driven in the tests; no browser has come back to a game on one.

- **The window's text outside the chat through `ImGui.Text`.** The binding's `TextDisabled`, `TextWrapped`
  and `TextColored` hand their text to ImGui as a printf format, and names, chat and what a server
  calls itself were shown through them; `Shown` shows them through `ImGui.Text` with the colour or
  the wrapping pushed. The chat has been looked at in a game; the menu's list of players, the list
  of servers and what a refusal says take a mouse to open, and have not.

- **The server's log in the game: the button.** Seen on 2026-11-22 with two games and the Windows
  build of the server on one machine, in `discord-operators` alone: a game that joined as an admin by
  its token became an operator when its Discord login (a certificate from `servers.ksamp.gg`,
  checked against the live key) came through; `/logs` typed in it brought back `audit.log` and the
  day's log as one file, every address in it said as `(address)`, written with CRLF lines and
  opened in Notepad. Written to `%LOCALAPPDATA%` first, it was not: a game started from a packaged
  app writes there into the package's own copy, and Notepad, outside it, said the path did not exist. Not seen: the **SERVER LOG** button itself, which takes a mouse; the Linux
  build's `logs` folder; more than one day; and a game that is no operator being refused, since both
  games were the same Discord user.

- **Logging in with Discord.** Driven over loopback with the login service's key made in the test,
  and not yet run in a game against the directory at `servers.ksamp.gg`. A server behind NAT that
  players reach over IPv6 while it fetched the key over IPv4 alone would refuse their passes.

- **The rest of the vehicle editor with the world running.** Seen only opened on the flown craft
  and closed again: not opened as Build New Vehicle and launched from, which lets the held craft go
  as the new one reaches the pad; not with an engine lit as it opened, which is cut and lit again
  on the way out, or one a flight computer is burning, which may light it while it is held; not
  for long enough to see whether the place it is held drifts against the ground; and the tag's
  line was read off the bridge, since a capture hides the interface. A craft in orbit is not held,
  and goes round with nobody at the controls.
- **KSA `2026.11.25.5949`.** The server holds players to it. Every patch target and private field
  the mod reaches has the signature it had on 5901, and `PrepareWorker` and the exhaust hooks are as
  they were. Three things did move:
  - **A rocket has several cores.** A puppet's plume takes its conditions from the nozzle's own
    core, which for a gas generator's turbine exhaust is the gas generator, where 5901 had one core
    a rocket. Seen: another player's Gemini 7 burned with the glow its own game drew. No
    gas-generator engine has been watched burning from the other game.
  - **A save is loaded into its own system.** `UncompressedSave.Load` swaps the running system for
    the save's when it lacks one of its bodies, which may put off `DeserializeSave` and
    `Program.OnGameLoaded` past the frame the load was asked for, and loads nothing at all, with a
    yellow alert, when the save's system is not installed. The server refuses a player in another
    system than the world's, so a join does not reach it.
  - **A craft definition may carry where it has arrived** (`<At>`), so the protocol moved to 37.
    The worlds the server carries lack the new elements (`At`, `FirstVisit`, `BodyKnowledge`,
    `Visits`), which KSA leaves empty, so they are still those of 5801.

  StarMap 0.5.3, the copy beside the game, starts on 5949 and installs every patch: no `KSA has
  moved` or `could not patch` in either game's log. `tools/breakup.sh` held every claim, flying
  a rocket apart on one game while the other watched.
- **Two rows for one server of another version.** A server that answers in another protocol
  version now keeps where it answered from, so a directory's row for it and the player's own
  collapse into one as they do for a server this game can join. Seen the other way in a game of
  0.20.1 the hour every server moved to 27: `Cars` and `cars.ksamp.gg`, one server, two rows. The
  fix has compiled and has not been run against a server of another version.
- **Protected craft and the server's rules, in a game.** The list reaching every game, the sweep
  waiting for it, docking and the rules on the clock are driven over loopback by `ProtectTests`.
  Unseen: a sweep in a game leaving a protected craft standing, the alert on Abandon, Recover and
  Delete, and a kitten's needs stopping or starting when `life-support` is changed under it.
- **The server's saves, in a game.** Keeping, loading, deleting and the list reaching an admin are
  driven over loopback by `SaveTests`. Nothing of the mod's side has been run: the SERVER SAVES
  window has compiled and never been drawn, so its layout, its sorting and whether KSA's popups
  call back into it are unseen; so are both patches -- whether `GameSaves.Toggle` is reached or has
  been inlined into the key handler, and whether refusing `UncompressedSave.Load` leaves KSA's
  window in a state it minds. A load with players in the room rides on `reload`, which has been
  seen, but with a world whose craft are not the ones their games hold.
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
- **A directory somebody else runs, listing a server newer than itself.** There is one:
  `ksamp.safeshows.dev`, on other hardware and under another person, and in `directories.txt`. On
  2026-11-12 its `/health` said `"known":2,"listed":0,"protocol":23` while both KSArmory servers
  spoke 25 and were listed at `servers.ksarmory.com`; each server's log had the directory's answer,
  `it answered at 45.133.178.13:50100 in protocol 25; this directory lists protocol 23`. Nothing a
  directory reads had changed between 23 and 25. `Ask` and `Facts` are the answer to that, and are
  driven over loopback only: a server answering an asker three versions either side of it, a prober
  reading a server stamped 99, and a roster listing it under that number. What has not been seen is
  a real directory of one release listing a real server of the next, which cannot be until both are
  past protocol 26.
- **The list in a game, of a server of another version.** A server that says what it is in another
  version is drawn as the rows `update your mod` and `server is older` were before, from the version
  alone; `Browser.Update` throws the rest of the answer away. Not seen in a game.
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
  putting the clock forward: two minutes of a frozen frame loop keeps its place, ten and a third
  minutes let it go. What has not been watched is KSA itself hitching -- a rebuild, a scene load -- and the session
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
- **A breakup, piece by piece.** Seven crashes over the lossy wire since pieces wait for their
  hull and are said early: no piece put anywhere outright, the worst piece 0.4 to 3.3 m out at
  its 95th and under a metre and a half in six of them, and none to three steps of more than a
  metre in a crash. Before, two pieces put 4.9 m and the worst 4.6 m out. What a step is: a piece
  built late is drawn for one frame where it was built, before it is first put anywhere. A hull
  that goes on coming apart is an outdated shape for up to four and a half seconds at a stretch,
  and its pieces are seen that much late. A craft a piece has just come off is written down
  again after one second and not two; with that, it was over three seconds in one crash of three.
- **Two copies built against each other.** A craft and the craft that has just undocked from it
  are built port to port in every other game, and KSA's collisions can knock one away at 4 m/s.
  A coasting copy a metre out is put back on its orbit within half a second, so it is one or two
  steps of about a metre and not 13 m of drift. The knock itself is not prevented.
- **Other mods, past one word about a craft.** Nothing said once and in order, nothing to
  everybody, nothing kept for a late joiner; KSACars' races, its circuits and its opponents are not
  shared, and its hold on time warp during a race has not met this mod's vote. Not tried over the
  lossy wire or with more than one mod speaking.
- **A docking is still a game's word.** The server holds it against where the two players' own
  games last said their craft were, which stops a craft being taken from across a world and
  does not stop a game that lies about where its own craft is. Each game is trusted for its own
  craft everywhere else in this design as well.
- **A docking the server will not have.** The game that docked has two craft fused that every
  other world has apart. Where the craft that is left is another player's it is built again as
  they have it, if they ever wrote it down; where it is this player's own, nothing undoes it.
  It takes a server that refuses, which an honest docking has not been seen to meet.
- **A hull held for being near that is torn down to be built again** is not built if the hold
  lapses in the few frames between. Not seen.
- **Copies strike each other where their craft did not.** Each piece of a breakup is a craft in
  KSA's physics in every game, and two copies a few centimetres from where their owner has them
  collide differently. A piece told to fall at 11 m/s moved at 2 to 3 in the other game until it
  was 4.5 m out and was put right: one piece, in one crash of the last nine. Not fixed.
- **A game in the background at the end of a warp.** The server moves the clock on only to games
  whose window has the player's attention, since KSA throttles the rest. One in the background
  that is past the clock when a warp ends waits for it: 22 s after a warp at 1000x, in a run where
  the game in front was itself a minute behind. Meant, and `tools/warp-trace.py` now says so
  rather than failing. Two games on one machine cannot both be in front, so what the game in the
  background shows there -- this, and running half a second behind while it builds thirty pieces
  -- has not been separated from being in the background.
- **A game building thirty pieces at once runs behind.** Half a second at worst and a fifth of
  one for the half minute after, measured against the game that owns them. Not fixed.
- **A craft nobody owns, at rest, with nobody near** is where each world has it. Anything that
  moves is held by the game it moves in, so two worlds can only differ by what they differed by
  when it came to rest. Not seen to differ since; the 2.6 m it was seen at was wreckage the rig's
  server swap had left nobody's.
- **A player riding in a docked craft cannot leave it.** Only the craft's owner undocks, and a
  rider has no hatch. If the owner leaves the craft for ten seconds it is the rider's.
- **A game that hears nothing of a docking it lost a craft to.** The server tells it again each
  time it speaks of that craft, for thirty seconds. Past that it would go on flying a craft
  nobody else has. Not seen.
- **The link to a world's mods, past Borea's dialog.** Seen 2026-11-12 against a local server
  with an `id` and a mod the game had not: the button stood where `CONNECT` does, and pressed
  (through the bridge, by the call the button makes) it opened Borea 0.6.20 on "Create an
  instance from a link" with both mods at the versions wanted, the server's name, and Create
  live. Create was not pressed. So an instance made this way has not been played from, Borea
  offering to update one when a server's mods change has not been seen, and neither has what an
  older Borea says of a link it does not know.
- **A mod at another version said apart from one that is off.** The sentence beside `CONNECT`
  that names two things to do at once, and `other mod version` on a row, have only been read in
  the source: it takes a server held to a version this game has not got. The pill itself was seen
  2026-11-12, amber `turn mods on` and `no answer` on a plain row and on the chosen one; the muted
  `recent` was not on screen.
- **The web page of servers, in a browser a person was holding.** It is deployed at
  `https://ksamp.gg` and was looked at there on 2026-11-13 in headless Chrome: the list, a
  server's page, the mods' names, pictures and links, and the cars server's link, which carries
  the `ksamp-mpcars` a game's own does. Before that it was run on this machine against the live
  directories in both colour schemes and at 520 px. The tags a preview is drawn from were read
  back from the live addresses as Discord's crawler asks for them, for the list and for a server.
  Not clicked through to Borea from the page, not seen on a phone, and no link has been pasted
  into Discord.
- **A server joined by name, in the list afterwards.** A name remembered without a port was
  asked at the default one, which on a machine with two servers is the other server
  (`Browser.Place`, `Row.Serial`, and `errors.md` for 2026-11-14). Read out of a game's log and
  built; the list has not been looked at since.
- **An admin kept through a server's change of name, and a token handed over a line that loses
  it.** All of the mod's side is a few lines of `Sync.Update` and `Tokens`, which only says where
  the file is; the rest is `TokenBook`, `Client` and the server, driven over loopback with every
  datagram one way lost and with a third of them lost both ways. Built against KSA, and no game
  has been started on it. Not seen: a `tokens.json` written by 0.20.2 read by this mod in a real
  documents folder and filed under where the server is at the next join; a game opped as
  `mp.ksarmory.com` being an admin as `ksamp.gg`, or as `host` after `host:port`; a token that
  could not be written leaving the one before it good; and a server's own `admins.json` of eight
  admins read by a server that writes `Offered`. A game instance made afresh has a documents
  folder of its own and no tokens, which is not changed and only said in the README.
- **A mod's version, where a mod manager installed the mod.** It was looked up by the folder
  its loaded assembly said it came from, which need not be the folder KSA knows the mod by
  (`World.VersionOf`, and `errors.md` for 2026-11-15). Read out of Borea's source and one
  player's account; the file's version was read by hand off both mods as Borea stores them, and
  no game has been started on it.
- **A world in another system, from a game started without KSA's configuration.** The window
  offers to have KSA start in the world's system next time (`World.StartNextIn`,
  `Panel.PressStartThere`, the bridge's `mp {"start_there": true}`). Not pressed, and no game
  has been restarted on it.
- **CONNECT pressed once after a name is typed.** A look no longer greys the button and a new
  name no longer starts one (`Sync.AskingAhead`, and `errors.md` for 2026-11-12). Read out of two
  logs and not pressed: the bridge has no mouse.
- **A game on a Mac, closed.** It hung on exit, for a reason read out of .NET's own source and
  not seen: a UDP socket closed under a thread reading it, which macOS cannot wake (`Udp.Read`,
  and `errors.md` for 2026-11-12). The reader no longer waits in the socket. Nobody here has a
  Mac, so that it now closes, and that joining and leaving a server on one do not stall, are
  unseen.
- **A launch on Mars.** A place with no launchpad is launched from as KSA always did
  (`Pads.Room`), seen from `Apollo11` on Luna in a game played alone on 2026-11-12 after every
  launch off Earth had been refused as a full site. Venus and Mars are the same kind of place in
  the same list and have not been launched from, and neither has any of them in a shared world.
- **A player on another KSA build than a server was built for.** The server turns them away
  before it has a world or a player (`RequiredBuild`), and lists its build to anybody who asks,
  under test over loopback. It takes an install of KSA at another build to see, and a KSA update
  to see the other half: a world saved by the old build handed out by a server brought up to the
  new one.
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
- **Spectating and giving between more than two players, or off loopback.** In two games on one
  machine the rows were drawn and Give control was clicked by hand, handing a kitten and a rocket
  over; through the bridge, Bravo in Gemini7 spectated Alpha in Rocket, let go of Gemini7, and
  stayed on Rocket through three stagings, the camera put back each time. The "You" over the flown
  craft and the Tab list held to a narrow window have been seen. A kitten given to a game still
  2.9 s behind the world stood still in the giver's until it was put 28 m on, which is why such a
  game is now refused; any craft flown in a player's first seconds after joining does the same.
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
- **A craft nobody has, kept by the next player to join.** On `ksamp.gg` a player stood beside three
  protected craft, said how they were built, and left before any save; the next to join was handed
  those definitions, logged `rebuilding NewRocket_3: nothing was known of it` and the two others,
  built none of them, and saved a world without them. The server no longer hands such a definition
  to a joiner, and the game no longer takes a craft down for one that no other player has. The
  server half is driven over loopback; neither has been seen in a game.
- **A docking after the last save, by a player who then leaves.** The consumed craft is said gone to
  whoever joins next, but the survivor's new shape went with the player, so the joiner has the
  survivor as it was saved and neither craft's parts together. Seen on `ksamp.gg` with `RCS Depot`
  docked into another player's craft two minutes after a save. Not fixed.

## What it does not do

- **No shared craft.** A craft is flown by one player at a time. Two players' craft dock into one
  that is one player's, and the other rides in it.
- **No authority over physics.** Each game is trusted for its own craft; the server only checks a state
  comes from the craft's owner.
- **Nothing is encrypted**, the password and an admin's token included, and only a command and a
  goodbye are tied to a session: somebody who can write a player's address and port on a datagram
  can still send states and chat as them.
- **The password the window keeps is one**, and is offered to whichever server is joined next.
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
  The line the server writes when the world falls names the player whose game it fell for, which
  is the game whose own log says why.
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
  still gives it the craft. What a game is told of being an admin, a new token among it, is said
  until the game says it back. Still said once: a line of chat, an ask for a warp and a vote on one.
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
  being near it so stepping out to watch something is not walking away from it. So does flying
  something else within 10 km of it, by where the server last heard each was: on `ksamp.gg` a player
  docking with a station they had taken earlier lost it to a player watching it while the two craft
  were metres apart, and the contact became one game's craft against another's copy in both their
  worlds, each pushed by the touch and put back by the pull. Driven over loopback; not seen in a
  game. Only a craft somebody
  else is waiting on changes hands: a world is mostly parked craft nobody is looking at, and letting
  those go lapsed ownership 873 times in 70 minutes, each one moving the camera of whoever was
  watching a craft when it came free. Waiting means flying nothing, which is the only game that
  takes a watched craft, and having watched it for three seconds, so the camera passing over it is
  not waiting: one player flicking through the vessel list freed thirty craft in a minute, and of
  330 craft let go of in an evening 220 went to nobody. Let go of, a craft is kept for that player
  for five seconds against any other game's ask that is not to fly it, since it is still in its last
  owner's physics and their game held it again for being near before the watcher's had heard. The
  game takes a craft nobody has by the same three seconds of watching.
- **A game far behind is handed nothing.** More than 30 s behind the server's time, by its last
  ping or, until it has sent one since loading, by the universe time it said it loaded at, a game
  keeps the craft it has and the ones it makes, and `Grant` refuses it anything else; a craft only
  such a game is watching is not let go of. As owner, a game's world is the one every other is
  pulled to, and one behind is flying the past. On `ksamp.gg` a game came in 5,500 s behind, was
  handed the protected station 14 s later by this lapse rule, and the station came apart in its
  world 45 s after that: its owner's word that it was destroyed took it off the protected list,
  and the operator loaded a three-hour-old backup. The server writes down once a minute who is
  refused for it. Driven over loopback; not seen in a game.
- **A world is told up to date from the one a game was sent.** A game holds a world it cannot load
  yet, as it does while the vehicle editor is open, and loads it when it can. The server sends a
  game that says it is in the craft described, gone and renamed since the world *it was sent*, not
  since the server's last save, and a save keeps those back to the oldest world still loading
  (`Prune`). A game that loads more than three minutes after the world was sent, where it has been
  saved since, is sent it again and goes round the loading once more, as for an operator's reload;
  its word that it was in, repeated every two seconds until a world comes, is not taken of a world
  older than the one sent. Before this a game that sat in the editor for an hour came in with
  none of that hour: two sweeps of the sky and a station moved to a polar orbit. Driven over
  loopback; not seen in a game.
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

  Two things it leaves. A craft a connected player holds outright and has flown, which is their
  craft and not their debris: the server names those in the `Swept` (`Spared`, at most 48), since a
  game cannot tell from its own world which of a player's craft they have been in, and every game
  has to leave the same ones. What a player's game shed, or holds for being near it, was never
  flown, and goes. And a game still loading a world from before the clear is sent the `Swept`
  once it says it is in, ahead of anything made since: the world it loaded still has everything
  the clear took, and the server can say gone only what it knew of.
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
