# KSA Multiplayer

A shared world for **Kitten Space Agency**: several players, each in their own game, flying in one
universe kept by a **dedicated server that runs on Linux** with no game installed.

Any craft syncs: rockets, rovers, kittens on EVA. The mod's folder and
id are `KSAMultiplayer`, and the server is one executable, `ksamp-server`.

> **Status: played, not finished.** Two games on one machine have flown in one world through it,
> against KSA `2026.11.10.5801`, and the server and the protocol are tested headlessly besides
> (391 tests, real UDP over loopback, a lossy line included). An 89-part rocket has been flown in a
> shared world and staged while the other game watched, which rebuilt the dropped stage as its own
> craft. What two games across the internet do is not known.
> This release is built for KSA `2026.11.25.5949`, and two games on one machine have flown on it
> through StarMap 0.5.3: a rocket staged apart with both worlds agreeing on every piece, and both
> games joining a world in another system by swapping theirs, Sol to Earth and its Moon and back.
> See [docs/DESIGN.md](docs/DESIGN.md#what-has-not-been-tried).

## Playing

1. Install [StarMap](https://github.com/StarMapLoader/StarMap) **0.5.3 or later**, then take
   `KSAMultiplayer-<version>.zip` from the [releases](https://github.com/LaurensDeV/KSAMultiplayer/releases), unzip it into KSA's `mods` folder and
   enable it, as with any StarMap mod. StarMap before 0.5 hands a mod no services at all, so an old
   StarMap with this mod installed stops the game starting rather than letting the mod quietly fail
   to load.
2. Everyone needs **the same KSA build** and **every mod the server's world uses**. The server refuses
   anyone who differs and says what is missing.
3. **Join a server.** The **PLAY MULTIPLAYER** window is the first thing the game shows, in place
   of KSA's configuration screen (or, with that screen turned off, over the game once it has
   loaded), with KSA's welcome held until you have joined or pressed **PLAY ALONE**, which hands the
   configuration back. Pick a server from the list it shows, or type an address -- a host on its
   own, or `host:port` -- then put in your name and the password if there is one, and press
   **CONNECT**. There is a server at **`ksamp.gg`** to try it on.

   Your name starts as something like `Kitten 4821` rather than your Windows account name, so
   nothing of yours reaches a server's player list unless you put it there. Change it to whatever
   you want to be called and it is kept for next time.

   **CONNECT** asks the server which system its world is in and which mods it wants, starts KSA in
   that system with the rest of your configuration as you last left it, and joins once the game has
   loaded. Tick **Display settings before starting** for KSA's configuration screen first, with the
   server's system already chosen, and KSA's own **Start** then joins. **BACK TO MULTIPLAYER** in
   that screen's footer brings this window back, as does an entry in KSA's **Mods** menu once you
   are in a game.

   From a game already in a system, a world in another one is joined by swapping your game into
   that system, which the window says before you connect. If your graphics card cannot hold the
   new system beside the old one, KSA is set to start in the server's system instead: restart it
   and connect again.
4. Connecting **replaces your open world with the server's**: save first. If the server has no world
   yet, yours becomes the server's world instead, and nothing is replaced: joined straight from
   the start, that is the new game KSA opens on. A game set to start in sandbox, at no craft, waits
   until there is one to hand over.
5. Hold **Tab** for who is playing, what their ping is, and who is spectating you; **Spectate** on
   a player's row (or `/spectate <name>`) puts your camera on what they fly and lets go of your own
   craft, which stays yours for ten seconds as any craft you leave does, and **Give control** on the row of somebody
   spectating you hands them the craft you are flying, which you go on watching. **Enter** opens a chat line, **Enter** sends
   it; a line behind a `/` goes to the server as a command instead, and is said to nobody. Another player's craft carries their name, and is theirs: you can watch it, not fly it. While you watch it the gauges are its, and your keys do nothing until you go back to your own craft. One its owner has left for ten seconds is yours if you have watched it for three while flying nothing.

Once you're in:

- **The server's name at the top right of the menu bar** opens and shuts the Multiplayer window: who
  is here, your ping, and **SERVERS**, the list from the start screen, to move to another server
  with **JOIN**. Each player's row says whether they are spectating you, with **Spectate** or
  **Give control** as on the Tab list. Hold **Tab** for who is flying what.
- **Fly anything nobody else is flying.** Take control of a craft and it's yours while you are in it
  or looking at it, and for ten seconds after. A craft someone else is flying moves where their
  game says: ask for it and the camera goes there anyway, so you ride along and watch rather than
  fly. Their name is over the craft they're in; their other craft carry a faint `Alice's Lander`, and
  the one you are flying a fainter `You`.
  In the map, each player's name is over the craft they're in, however far away.
- **Launch as usual.** A craft you launch, a stage you drop and a kitten you send on EVA show up for
  everyone else.
- **Other mods' craft.** A mod that adds to a craft -- a car's steering and wheels -- can share
  that with every other game through StarMap, without either mod knowing of the other. KSACars
  does. `docs/MOD-API.md` is what a mod has to say and listen for.
- **Dock.** Your craft and another become one craft, for everyone, and two again when you undock.
  Docked with another player's craft, the one craft belongs to whoever's game docked them first:
  that player flies it and the other rides along, watching. Only its owner can undock it. When
  they do, each half goes back to whoever had it, the rider's about ten seconds after its owner
  has flown the other half away. Ports latch only within half a metre, so come in slow and centred,
  with Align on roll Up: the ports' guides stop two ports turned a sixth of a turn apart 0.7 m short,
  as roll Down should. Docking from a port's menu takes that port's craft into the other, under the
  other's name, so dock from the visiting craft's port, not the station's.
- **Time is shared.** Every world keeps the server's time, so changing speed is put to everyone
  first: a warp runs only if all agree, and only whoever asked can end it. Pause is a speed like any
  other. Under *Agree to warps* in the Multiplayer window you can have your game agree for you: *When
  not flying* agrees while you're in the vehicle editor or not flying a craft, and to any pause or
  slow-down; *Always* agrees to everything. *Ask me* is the default. The world is never warped past what the slowest machine in it is managing, and slows to
  that machine rather than leaving it behind. After joining, your game may run fast for a few
  seconds to catch up.
- **Chat** from the window.
- **On the developer's test server at `ksamp.gg`**, and on no other, your game sends the server its
  logs of your last few sessions when you join, the mod's and KSA's own, with addresses and your user name taken out,
  so what went wrong can be found. The chat says when it has.
- **Rename a craft.** Type `/rename New name` in the chat to rename the craft you're flying, if it's
  yours. It's renamed in every game, crew and all. A protected craft can only be renamed by an admin,
  who can rename any craft with `/rename Old name -> New name`.

Two craft with the same name are still two craft: the server gives the second one a name of its own,
your game goes on calling it what you called it, and the player list says *other players see your craft as*
with the name everybody else sees. Two players with the same name are told apart the same way.

### The server list

The window lists every server it can find, a row each:

| Column | |
| --- | --- |
| **Server** | its name, `(password)` where it wants one, and a pill at the right where this game cannot join -- `wrong KSA build`, `turn mods on`, `turn mods off`, `other mod version`, `full`, `update your mod`, `server is older`, `no answer` -- or a quieter `recent` for an address you have been to before |
| **Players** | how many are on it, against the most it takes |
| **World** | the system its world is in, or that it has none yet |
| **Latency** | the round trip, measured by the asking |

The same servers are on the web at **<https://ksamp.gg>**, with the mods each one needs
and a button that has Borea install them.

Servers with players on them come first, then the ones you could join. **REFRESH** asks again, and
servers you have connected to before are in the list whether or not a directory carries them, so you
can see who is on them. Picking a row fills the address in; the **Password** field becomes the words
*none needed* for a server that has said it wants none, and **CONNECT** is greyed for a row this game
would be refused from, with the reason beside it.

Where the reason is your mods, the button is **GET THESE MODS WITH BOREA** instead. It hands
[Borea](https://github.com/KSAModding/Borea), a mod manager for KSA, the list of what the server
wants; Borea shows it, and on your say-so makes a separate instance of the game holding exactly
those mods, leaving the one you play in as it was. The link is in the mod's log as well, for
pasting. There is no button where a link could not say what the server wants: a mod it names
without a version that this game has not got either, since Borea takes only exact versions. It
needs a Borea that reads `borea://modlist` links, which is 0.6.20 or later. When a server's mods
change, Borea offers to bring the instance it made for that server up to date: it knows the
server by the `id` its operator set, or by its address where they set none.

Nothing about the list is on the way in: typing an address and pressing **CONNECT** works whether or
not the list ever came up. The list is in this window only -- the **Multiplayer** panel inside a game
takes an address and not a list, so changing servers from there is disconnect, type, connect.

Leaving a server, whether by **DISCONNECT**, a lost connection or the server closing, puts a red
**Disconnected** in the middle of the screen for a couple of seconds and opens the **Multiplayer**
window if it was closed, so rejoining is one click away.

What the list costs you: the mod fetches `directories.txt` from this repository and then each
directory's list over HTTPS, and asks every server on it what it is. So the operators of the servers listed see a request from your address, as they would if
you typed the address yourself -- a few dozen of them rather than one. Nothing about you goes with
it: no name, no account, nothing but the request.

Put a `directories.json` beside your settings in `Documents/KSAMultiplayer` to use different
directories:

```json
{ "use": ["https://a.directory.example/v1/servers"] }
```

`use` replaces the list, `add` extends it, and `remove` drops one by URL. An empty `use` falls back
to the built-in list rather than standing for none, so going without means `remove`-ing the ones you
have.

## Running a server

The server is a single self-contained file, and runs on a machine with no game on it. Take
`ksamp-server-linux-x64.tar.gz`, `ksamp-server-linux-arm64.tar.gz` or `ksamp-server-win-x64.zip`
from the [releases](https://github.com/LaurensDeV/KSAMultiplayer/releases), unpack it on the machine that is to run it, and start it:

```bash
./ksamp-server --name "Moon base" --password secret
```

To build it instead, on any machine with the .NET 10 SDK and no KSA files on it:

```bash
./tools/publish-server.sh              # linux-x64
./tools/publish-server.sh linux-arm64  # a Raspberry Pi or an ARM VPS
./tools/publish-server.sh win-x64      # a Windows box, including the one you play on
```

That writes `dist/ksamp-server-<rid>/ksamp-server`; copy it across and run it as above.

Open **UDP port 28500** in the firewall (and on the router, if the server is at home). Players connect
to the server's address.

### Giving it a world

- **Let the first player bring one.** With no world, the first player to connect gives the server
  theirs.
- **Or start empty.** Set `empty-world` to a system -- `SolLite` or `Sol` -- and the server starts
  with an empty world of its own, so nobody has to hand one over and the first player joins into a
  world that is already there. These are saves KSA itself wrote, carried in the server; it cannot
  make one, since a world is KSA's own format and the server has no game. They pin the system a
  world started from them is in, and go stale when KSA's save format moves on.

  Either way the server holds every player to **the KSA build it was built for**, which is the
  one its release of the mod was compiled against and is on the release's page. It says so on a
  list of servers before anybody has joined, and it does not go by which build saved its world:
  when KSA updates, install the server release made for the new build and the world is kept.
  `ksa-build` says another build instead.
- **Or copy a save in.** Copy a save folder from `Documents/My Games/Kitten Space Agency/saves/<name>`
  (or a zip of it) to the server and start with `--world <path>`.

The server keeps the world in `--data` (default `./data`) and carries on from it after a restart. Every
five minutes, and whenever someone joins a world that's more than 30 seconds old, it asks the player who
has been in longest for a fresh save, so a player joining starts close to now. Universe time stops while
nobody is connected, unless `--keep-time` is given.

### Letting players find it

Off by default. Turning it on publishes the address this server is reached at, which for a machine
at home is a household's address, so it is a decision for whoever runs the machine:

```toml
# list this server in the public directories so players can find it; its address becomes public
advertise = true
```

The server then says so at every start -- listed, or not listed and why, in words you can act on.
Nothing is listed on being asked: a directory asks the address what it is, as a game's own list
does, and lists the server only once it answers, so a port that is closed or forwarded wrongly reads
as exactly that in the log rather than as a server nobody joins. A directory need not be on the
server's release to list it, as long as both speak protocol 26 or later. Announcing stops the moment
`advertise` goes back to `false`, without a restart.

A server behind carrier-grade NAT cannot be listed, and is told so.

`--advertise-as` is for a server with a name of its own: the name has to have an address record
pointing at the server, since a directory checks that the name it is given is the machine that gave
it. With no name the server is listed at the address its announcement arrived from.

A server sharing a machine with a directory has to give a name, and is told so. Its announcement
never gets out to the internet and back, so what the directory sees is an address inside the
machine, and the name is the only thing left to go on.

`--directories` points somewhere else -- a group's own directory, or one for testing. With none
given, the server reads the shared list in `directories.txt` once an hour and announces to every
directory in it, so a directory retired from that file stops being asked within the hour and a new
one is picked up without anybody installing anything. The last list read is kept in `--data`, so a
restart with no internet still knows yesterday's directories.

That read happens only while `advertise` is on. A server nobody opted in fetches nothing and asks
nobody, which is the same decision as the setting itself. `docs/DIRECTORY.md` is what running a
directory takes.

On another port, players have to type `host:50101` -- unless the zone holds an SRV record, which the
mod reads when no port is typed:

    _ksamp._udp.moon.example.com.  300  IN  SRV  0 0 50101 moon.example.com.

Keep an address record on the name beside it, both so the name resolves for everything that does not
ask about SRV and so a player whose resolver drops the record can still get in by typing the port.
`infra/` makes both from one line of configuration. Advertising carries the port for you, so this is
for a server players reach by name without one.

### Options

Each option is a line in the settings file, a `KSAMP_` environment variable, or a flag, and a flag
wins over the environment, which wins over the file. The file is `ksamp-server.toml` beside the
server, or wherever `--config <path>` says; with none there, the server writes one with every option
at its default and what it does:

```toml
# the server's name, shown to players
name = "KSA server"

# UDP port to listen on (a restart takes it up)
port = 28500
```

At the console, `set` lists every setting and where its value came from; `set <key> <value>`
changes one and writes it to the file, and `settings` reads the file again after it has been edited.
Either takes effect at once, except `port`, `bind` and `data`, which wait for a restart; a setting a
flag or the environment gives stays theirs, and the console says so. Both are the console's alone,
like `quit`.

| Option | Environment | Default | |
| --- | --- | --- | --- |
| `--port` | `KSAMP_PORT` | `28500` | UDP port; a server that already had a world when it first ran a release with this default stays on 50100, which is written into its settings |
| `--bind` | `KSAMP_BIND` | every address | address to listen on |
| `--name` | `KSAMP_NAME` | `KSA server` | shown to players |
| `--id` | `KSAMP_ID` | none | a short name that never changes though the name and the address may, in letters, digits, dots, dashes and underscores (`my-clan-cars`). Borea knows the mods it installed for this server by it, and offers to update that instance when the server's mods change. Without one the server is known by the address a player reaches it at |
| `--password` | `KSAMP_PASSWORD` | none | |
| `--max-players` | `KSAMP_MAX_PLAYERS` | `16` | |
| `--max-craft` | `KSAMP_MAX_CRAFT` | `2048` | the most craft one player holds at once |
| `--data` | `KSAMP_DATA` | `./data` | where the world is kept |
| `--world` | `KSAMP_WORLD` | | a save folder or zip to start from, replacing the kept one; not a setting in the file, since it is something to do once |
| `--ksa-build` | `KSAMP_KSA_BUILD` | the build this server was built for | the KSA build players must run |
| `--mods` | `KSAMP_MODS` | the world's | the mods players must have, comma separated, each held to a version after an `@` where one is given: `FastAndPurrious@0.1.13` for that release, `FastAndPurrious@0.1.13+8b152a3` for one build of it. The whole list, not a floor: a player running anything else is refused, except the optional mods below. Worth setting, because the world's own list follows whoever last handed a save over |
| `--optional-mods` | `KSAMP_OPTIONAL_MODS` | none | mods a player may have enabled or not, comma separated, at any version: ones that change only that player's own game and write nothing another game cannot read, such as an editor without its limits on part scale. Never required, whoever saves the world, unless `--mods` names one too. A mod that adds parts or part modules does not belong here: a craft built with it does not load in a game without it |
| `--snapshot-every` | `KSAMP_SNAPSHOT_EVERY` | `300` | seconds between fresh saves |
| `--keep-time` | `KSAMP_KEEP_TIME` | off | let time run with nobody connected |
| `--life-support` | `KSAMP_LIFE_SUPPORT` | `true` | whether kittens need food, water and air, for every player |
| `--radiation` | `KSAMP_RADIATION` | `true` | whether kittens take a dose from radiation, for every player |
| `--clear-pads` | `KSAMP_CLEAR_PADS` | `true` | clear away debris left on a launchpad for five minutes, and a craft only when its site is full |
| `--warp-answer` | `KSAMP_WARP_ANSWER` | `30` | seconds everyone has to answer a warp |
| `--stop-notice` | `KSAMP_STOP_NOTICE` | `15` | seconds the players are shown a countdown before the server stops; `0` for none |
| `--lapse-within` | `KSAMP_LAPSE_WITHIN` | `10` | seconds a craft stays its owner's once another player flying nothing has watched it for 3 s and they are neither in it, looking at it, nor flying something within 10 km of it |
| `--kept-away` | `KSAMP_KEPT_AWAY` | `120` | seconds a player's craft are kept for them after they leave, nobody else's, and theirs again if they rejoin under the same Discord account (or name); `0` lets them go at once. Not after a kick |
| `--speed` | `KSAMP_SPEED` | `1` | how fast universe time runs |
| `--empty-world` | `KSAMP_EMPTY_WORLD` | | start with an empty world of this system (`SolLite` or `Sol`) instead of waiting for a player; also what `fresh` puts in place |
| `--collect-logs` | `KSAMP_COLLECT_LOGS` | off | keep the mod's and KSA's logs players' games send, each line once: as they join, every ten minutes while they play, and whenever `playerlogs [name]` is typed at the server's console, which no game can type. Under `player-logs/` in the data folder, for two weeks. Games send them only to the developer's test server at `ksamp.gg`, so on any other server this does nothing |
| `--advertise` | `KSAMP_ADVERTISE` | off | list this server publicly so players can find it, which publishes its address |
| `--advertise-as` | `KSAMP_ADVERTISE_AS` | the address seen | the hostname to be listed under |
| `--directories` | `KSAMP_DIRECTORIES` | the shared list | where to announce, separated by commas; the shared list in `directories.txt` otherwise, read hourly while advertising |
| `--discord-admins` | `KSAMP_DISCORD_ADMINS` | none | Discord user ids, comma separated, who are admins whenever they log in with Discord, whatever `admins.json` says; `deop` cannot take it away |
| `--discord-operators` | `KSAMP_DISCORD_OPERATORS` | none | Discord user ids, comma separated, who are [operators](#operators-and-the-servers-log): admins who may also download the server's log |
| `--discord-trusted` | `KSAMP_DISCORD_TRUSTED` | none | Discord user ids, comma separated, who are [trusted](#trusted-players) whenever they log in with Discord |
| `--discord-login` | `KSAMP_DISCORD_LOGIN` | `https://servers.ksamp.gg/v1/discord` | the service that vouches for who a player is on Discord, [asked only once there is a Discord admin](#admins-by-discord); empty to take no Discord logins |

Commands typed into the server's console: `help`, `players`, `say <text>`, `kick <name> [why]` (or `kick #<id>` by the number `players` shows; a name may have spaces in it), `op <name>`, `trust <name>`, `deop <name>`, `admins`,
`clear [name]`, `debris [name]`, `remove <craft>`, `protect <craft>`, `unprotect <craft>`, `protected`, `rename <craft> -> <new name>`, `reload` (or `reset`), `save [name]`,
`saves`, `load <name>`, `delete <name>`, `deleted`, `restore <name>`, `status`, `version`, `fresh`, `logs`, `playerlogs [name]`, `set [key value]`, `settings`, `quit`.

`life-support` and `radiation` are KSA's two rules, which a game on its own takes from its start
screen. In a server they are the server's, for everybody: `set life-support false` turns life support
off in every game within the second and says so in the chat, and a player who changes one in KSA's
own menu has it put back.

`clear-pads` keeps launch sites usable in a busy world, since a site with something on every pad
refuses every launch from it. Debris left on a launchpad for five minutes is cleared away, whoever is
standing around. A craft that isn't debris goes only when every pad at its site is taken and nobody
has flown it or been within 2 km of it for five minutes, one at a time, the one left longest first,
and its crew are sent home rather than lost. A protected craft is never cleared, and anybody
standing next to their rocket keeps it. The chat says what was cleared and why.

`fresh` throws the world away and puts an empty one in its place -- the one `empty-world` names:
every craft gone, universe time started over, and everybody handed the new world where they stand.
The world it throws away is kept first, as a `before fresh` save in SERVER SAVES, so `load` brings
it back, and the line that says so gives its size and universe time, as SERVER SAVES does. It
refuses to start over a world that is still an empty one nobody has played in. An admin can type it
in chat as `/fresh`; a trusted player cannot.
It shuts down cleanly on Ctrl+C, on SIGTERM and on `quit [why]`: with players in the world, it
counts them down on screen for `stop-notice` seconds, refuses new joins, asks for a last save and
waits up to 5 s for it, says goodbye with the reason, and only then leaves the directories. A second
Ctrl+C or SIGTERM stops it at once. Whatever stops the process has to give it that long: the
containers here are given 40 s (`infra/server/server.tf`).

`version` says what the server is, the protocol it speaks and the KSA build it holds players to --
the protocol being the number that decides who can join, and the one to ask for when somebody is
refused by name. It is said at the top of the log too, so reading `docker logs` answers it without
attaching to anything.

`clear` is the broom for a world filling up with spent stages. Every craft nobody is flying is taken
out of every game that is in the world; a name narrows it, with `*` and `?` for wildcards --
`clear Rocket_*` -- and a craft somebody is flying is never taken, whatever is asked for. Nor is a
craft a connected player holds and has flown: their parked ship is not debris, though what it shed
is. `remove <craft>` takes one of those by name. A game still loading the world when the clear is
made is told of it once it is in. The name
goes to the games and each sweeps its own world by it, because the server only hears of a craft when
somebody claims it: the fragments a spent stage breaks into belong to nobody, and they are most of
what there is to sweep. The world on disk still holds them, so a fresh save is asked for -- from a game
that has already swept, so what lands on disk is clean. Nobody is moved: everybody is standing in a
world they swept themselves, so clearing costs no loading screen and leaves the clock alone.

`debris` is the same broom for debris alone, for when the world holds stations and rovers worth
keeping: what KSA's own *Clear Debris* counts, pieces broken off a craft, and any craft with no
command pod or probe core on it, which is what a spent stage is. A kitten, a craft somebody is
flying and a protected craft are left; a name narrows it as it does `clear`. Trusted players and up
can type it in a game as `/debris`. Each game tells debris from its own copy of the parts, so a craft
is debris in every game or in none.

`remove <craft>` takes one craft out of every game by its exact name, whoever owns it, and says so
in the chat; it refuses a craft somebody is flying and one that is protected.

`rename <craft> -> <new name>` gives a craft another name in every game, whoever owns it, and keeps
it protected if it was. A player renames their own craft from the chat with `/rename <new name>`; a
protected craft only an admin can rename. It refuses a name another craft has.

`protect <craft>` is for what is not debris. A protected craft is left by every `clear`, even one
that names it in full: `clear`, `clear Station*` and `clear Station` all leave `Station` standing, and
taking it out is `unprotect Station` first. Nor can a player remove it from their own game: Abandon,
Recover and the universe manifest's Delete all say that it is protected. Two craft that dock are one craft, and protected if either was.
`unprotect <craft>` takes it off the list and `protected` lists them. The list is `protected.json` in
`--data`; `fresh` empties it, a loaded save brings back its own, and a craft that crashes is gone
whether it was protected or not, and taken off the list when it is.

`reload`, or `reset`, is the other half and is asked for on its own: the world as it stands goes out
to everybody again, and each game loads it and comes back in a few seconds. Nobody reconnects, and
nobody loses what they are flying.

It is also the only way universe time is ever put back, which is why it is a command of its own
rather than something `clear` does on the way past. A game's own time comes down by loading a world
and by no other means -- a game more than three seconds ahead of the server stops dead until the
server catches up -- and while they are all loading there is nobody in the world, so the server's
clock returns to the world's own time at the same moment theirs do.

### Saves

`save <name>` keeps the world under a name, and `load <name>` puts it back in place of the world for
everybody where they stand: every craft as it was, and universe time back to when it was kept. Nobody
reconnects. `saves` lists what is kept and `delete <name>` throws one away.

A save is of now, and of the game that asked: an admin's own game is asked for a fresh save first, or
the player who has been in longest when the console asks. Nobody else's answer is taken for it. If
none comes within twenty seconds -- KSA writes no save while the vehicle editor is open -- it is made
of the world as the server last had it, and says so. The server's own saves, and the last one before it stops, are not
asked of a player in the vehicle editor while anybody else can give one.

The world a load replaces is kept first, as `before load <date and time>`, so loading that takes a
load back. It is the world as last saved, which is up to `snapshot-every` seconds behind what the
players were looking at. The newest five are held on to, nothing is kept when the world being
replaced is already a save -- so loading one save after another does not push out the world
everybody was playing in. An admin can delete them like any other save, and the window says first
that the load it came from can't be undone afterwards. Nothing can be saved under such a name.

A load is refused while somebody playing could not follow: a game whose mods are not the save's.
A save in another system is loaded, and every game swaps into that system to open it. It ends a
warp that is running.

A save holds which craft were protected when it was made, and a load puts that list back with the
world -- the world a load replaces included, so taking a load back takes the list back too. A save
made by 0.21.0, or a zip copied in, says nothing of them and leaves the list as it is.

They are zips in `saves/` under `--data`, each with a `.json` beside it, and they outlive a restart
and a redeploy as the world does. A KSA save zipped and copied into that folder is one of them --
its folder or what is in it, as `--world` takes -- named for the file. A hundred are kept at most,
and a gigabyte of them. A name is letters, digits, spaces and ``-_.()'+,&!``, 48 characters at most.

In a game they are an admin's: see below.

### Admins

An admin types those same commands into the game's chat with a `/` in front -- `/clear Rocket_*`,
`/reload`, `/players` -- and the answer comes back in their own chat. All but `playerlogs`, which is
the console's alone and does not exist as far as a game is concerned.
KSA's universe manifest works for an admin too. Its Delete on another player's craft is a `remove`,
taken out of every game, owner's included; a player who is no admin is told only an admin can. Each
craft's row has a padlock before its name: shut for a protected craft, which everybody sees, and open
and faint for the rest, which only an admin sees. An admin's click on it is `protect` or `unprotect`.
A line behind a `/` is said to nobody, so a player who types one where they are no admin has told the
room nothing and is told so themselves. `quit`, `settings` and `set` are the console's alone, each
saying so when an admin tries it.

Who is one starts at the console, since nobody is one on a server that has just come up:

```bash
op Jeb
```

That hands Jeb's game a random token, which it keeps in `Documents/KSAMultiplayer/tokens.json` and
offers at every join after, so Jeb is an admin whenever he comes back with nothing to type and no
password to lose. `deop Jeb` takes it back, whether or not he is playing, and his game throws the
token away when it next hears from the server.

`op` on somebody who is already an admin replaces their token, which is how one that was lost on a
reinstalled machine is replaced. The token they had goes on working until their game says the new one
is written down, so a replacement lost on its way costs nothing. `deop` waits for nothing, and is what
to type when a token has got out.

The game files the token under the server's address as it was typed and under the IP address and port
that led to, and looks under the second when there is nothing under the first. So Jeb is still an
admin when the server takes a new name, and when he reaches it as `host` one day and `host:port` the
next. Two things do lose it, and `op` again is the answer to both:

- **A new name and a new machine at once.** Nothing the game kept points at the server any more.
  Change one at a time, with each admin joining in between, and nobody is lost.
- **A new game instance.** `tokens.json` is in KSA's documents folder, and each StarMap or Borea
  instance has its own. Copying the file across carries every admin it holds.

The list is `admins.json` in `--data`, beside the world, and holds only each token's **hash**: enough
to take an admin away, not enough to become one. It is not touched by an update or a restart. A player
has to be connected to be opped -- the token is said to their game until it answers, and is never
written down where it could be said to anybody else -- and the name it is remembered under follows
whoever last joined with it, so `deop` names the player you can see.

#### Trusted players

Between a player and an admin is **trusted**: somebody who keeps the world tidy and running when no
admin is about, and can lose nothing that cannot be put back.

| | Anybody | Trusted | Admin |
| --- | --- | --- | --- |
| `players`, `admins`, `protected`, `saves`, `status`, `version`, `help` | yes | yes | yes |
| `clear`, `remove`, `protect`, `unprotect`, `rename` any craft | | yes | yes |
| `save` under a new name, `load` | | yes | yes |
| `kick` | | yes | yes |
| `save` over a save, `delete`, `deleted`, `restore`, `reload`, `fresh` | | | yes |
| `op`, `trust`, `deop` | | | yes |
| `logs`: download the server's log, for [operators](#operators-and-the-servers-log) only | | | |
| `say`: chat as the server, for operators and the server's console only | | | |

```bash
trust Jeb                           # Jeb is playing
```

```bash
trust discord:80351110224678912     # or by Discord account, as op does
```

`op` on somebody trusted makes them an admin, `trust` on an admin makes them trusted, and `deop`
takes either away. In a game, `/help` lists only what that player may run, and the **SERVER SAVES**
window shows a trusted player Load and New and no Delete or Overwrite.

#### Nothing is lost for 30 days

A save that is deleted, saved over, or let go as the oldest of the five worlds kept before a load
goes to `saves/.deleted` in `--data` for 30 days. `deleted` lists them, and `restore <name>` puts
one back under the name it had (or that name with "restored" after it, where it is taken).

#### Who did what

Every command that changes the world, a save or a role -- `clear`, `remove`, `protect`, `rename`,
`save`, `load`, `delete`, `restore`, `reload`, `kick`, `op`, `trust`, `deop`, and the console's own
`set` and `quit`, and `fresh` -- is written to `audit.log` in `--data`: when, who (with their role and
Discord user), what they typed and what came of it. So is a role given by a Discord login. It is a
file beside the world and not the console's log, so it outlives a restart and a redeploy.

#### Operators and the server's log

Everything the console says is also kept in `logs` in `--data`, a file a day named by its date, the
newest 14 days of them. A redeploy that throws the container's own log away leaves these, and they
are where to look when somebody asks what happened.

An **operator** is an admin who may also download that log from inside the game, and `say` something
in the server's own name. It is given only by
`discord-operators` and a Discord login: there is no command for it, `op`, `trust` and `deop` leave an
operator as they are, and a machine's token makes them no more than an admin until they have logged
in. An id listed in `discord-admins` too is an operator, and the server says at every start which ids
are listed twice so they can be taken out of the other list.

In a game an operator has a **SERVER LOG** button beside SERVER SAVES, or types `/logs` (today) or
`/logs 3` (the last three days, up to 14). The server sends `audit.log` and the days asked for in one
file, with every IP address said as `(address)`, and the game saves it to
`KSAMultiplayer\server-logs` in KSA's documents folder (`Documents\My Games\Kitten Space Agency`) and opens it in whatever opens text files. A busy day is a
few hundred kilobytes, and gzipped on the way about a tenth of that. At the console, `logs` says where
the files are.

#### Admins by Discord

A token belongs to one machine. To make somebody an admin as **their Discord account** -- on any
machine, any game instance, after any reinstall -- all a server needs is their Discord user id
(Discord's **Copy User ID**, with Developer Mode on):

```bash
op discord:80351110224678912
```

or, once they are playing and have logged in, simply `op Jeb`. For admins on every server you run,
list them in the `discord-admins` setting instead: it is read at every start, so a world reset or
a lost data folder takes nobody's admin with it.

Players log in **once per computer**: **Log in with Discord** under the name in the Multiplayer
window. Their browser goes through the login service at `servers.ksamp.gg` to Discord, which asks
only to know who they are, and comes back to the game on their own machine, on a port the system picks for that login. If the
browser that opens is not the one they use Discord in, **Copy link** puts the login's address on the
clipboard. The login lasts 30 days, and **Log out** forgets it. It is kept in
`%LOCALAPPDATA%\KSAMultiplayer\discord.json`, for the Windows account rather than for one StarMap or
Borea instance, so every instance on the computer is logged in at once. Logging in puts the Discord name in
**Nickname**, which can be changed after as ever.

From then on every server they join is told who they are, with nothing to press: a server with
Discord admins makes one of them an admin on the spot, and hands that machine a token as `op` does.
Everybody's player list shows a blue dot before the name of each player whose login the server
checked, and nothing of who they are on Discord. `deop Jeb` or `deop discord:80351110224678912` takes it back, along with every token handed out for
it, and `admins` shows the Discord user beside each name it applies to.

There is nothing to set up on a server. One with no Discord admin is told nothing and asks the
service nothing; `--discord-login ""` turns Discord logins off altogether.

The login is a key made by the game, which never leaves the computer, and a certificate from the
service saying whose it is. At each join the game signs that join's nonce and the address the player
typed with the key, and a server takes it only for its own nonce and an address that leads to itself
-- its own interfaces, or where the login service saw it from. So a server that has your admin join
it cannot hand what they sent on to yours.

In a server, the key and the menu entries that open KSA's **GAME SAVES** open **SERVER SAVES**
instead, for an admin, and so does a button in the Multiplayer window. It is the same window with the
server's saves in it: **New** keeps the world under a name, a double click loads one for everybody,
and a right click offers Load, Delete and Overwrite, each asking first. Anybody else who presses the
key is told that only admins can save or load the world.

Nobody loads a save of their own while in a server, admin or not: it would put that one game in a
world the others are not in. KSA says so where the save was picked, and **DISCONNECT** is the way to
one's own saves.

### As a service

Every release publishes a container image, so there is nothing to build or clone:

```bash
docker run -dit --name ksamp -p 28500:28500/udp -v ksamp-data:/data ghcr.io/laurensdev/ksamp-server:latest
```

A server set up before the default port was 28500 is on 50100, and stays there: its settings in
the volume say so, so keep publishing `-p 50100:50100/udp` for it. 50100 is in the range Windows and
Linux hand out for their own use, where another program can have been given it first; players reach
a server on either by its name alone.

`latest` is the newest release, which is the one players who keep the mod up to date are on. A
container keeps the build it was started with, so a release reaches your server when you fetch it
and not before; the world is in the volume and is still there afterwards:

```bash
docker pull ghcr.io/laurensdev/ksamp-server:latest
docker rm -f ksamp
docker run -dit --name ksamp -p 28500:28500/udp -v ksamp-data:/data ghcr.io/laurensdev/ksamp-server:latest
```

A server and its players have to speak the same protocol, and a release's notes say when it
changes that, so the time to update is when your players do. To stay on one release whatever
comes out after it, name it in place of `latest`: `ksamp-server:v0.38.0`. A release's notes also
carry the line pinned to that build's digest, which is what to run on a machine you care about:
a tag moves and a digest does not.
[`deploy/Dockerfile`](deploy/Dockerfile) builds one yourself from the repository root, and
[`deploy/ksamp-server.service`](deploy/ksamp-server.service) is a systemd unit with the install
steps in its header:

```bash
docker build -f deploy/Dockerfile -t ksamp-server .
```

Its settings file is `/data/ksamp-server.toml`, in the volume, and the systemd unit's is
`/var/lib/ksamp/ksamp-server.toml`: beside the executable is read-only in both. Set the name and the
password in there, or with `set` at the console.

A `-e KSAMP_NAME`, or an `Environment=` line in the unit, wins over the file for good -- `set name`
at the console then writes a name into the file that players never see. So put it in the environment
where the machine's configuration is the source of truth and the console is not meant to override it,
which is what `infra/` does for `ksamp.gg`, and in the file where you want to change it from
the console.

The commands above are read from standard input, so a container wants `-it` to have a console at
all: `docker attach ksamp` then types into it, and Ctrl+P Ctrl+Q leaves it running. Started with
plain `-d`, or run as a systemd service, the server has no console and `clear` and `reload` cannot
be reached -- `tmux` or `screen` is the way to keep one for a service.

## Building

| Command | |
| --- | --- |
| `./tools/test.sh` | the protocol, the server and the directory over loopback; needs nothing of KSA |
| `./tools/build.sh` | the server and the mod; the mod needs KSA's assemblies, found by `Directory.Build.props` (set `KSA_DLL_DIR`) |
| `./tools/publish-server.sh [rid]` | the server as one self-contained executable |
| `./tools/package.sh` | the mod as a player installs it, `dist/KSAMultiplayer-<version>.zip` |
| `./tools/deploy.sh` | the mod into this machine's KSA, as a developer's install: it starts the bridge |
| `./tools/run.sh` | deploy and launch the game, showing the mod's log |
| `./tools/play.sh` | the game with this mod and nothing else, in a profile of its own |
| `./tools/two-games.sh --test` | two games and a server on this machine, joined to one world |
| `./tools/two-games.sh --server` | this tree's server put under those two games as they stand |
| `./tools/breakup.sh` | fly one game's rocket apart and count what it did to both worlds |
| `./tools/motion.py` | record where every craft is in both games as each frame is drawn, and lay the two accounts side by side; `--scene` sets up an orbit, an EVA, a meeting, a walk or a drop |
| `./tools/warp-trace.py` | walk both games through a script of warps, sampling them and the server together, and judge the series |

| Path | |
| --- | --- |
| `src/Protocol/` | what the server and the mod both speak: the wire format, transfers, the clocks, a client session. Nothing of KSA |
| `src/Server/` | the dedicated server: `GameServer`, the world kept on disk, and the console program |
| `src/Directory/` | the directory service: the roster, the probe that checks an announcement, the HTTP endpoints, and the page of every server at its root. Nothing of KSA |
| `src/Mod/` | the StarMap mod: `Sync` runs the session each frame, `World` is every contact with KSA, `Hooks` the patches, `Panel` the window |
| `tests/` | the protocol and the server driven over loopback |
| `tools/ksa-mcp/` | commands into a running game through the mod's bridge, for driving a test without a hand on the keyboard |
| `deploy/` | a systemd unit and a Dockerfile |
| `infra/` | `ksamp.gg` and the server behind it, as OpenTofu |
| `docs/DESIGN.md` | how it works, and what it cannot do yet |
| `docs/DIRECTORY.md` | what a directory answers, and what running one takes |
| `docs/SERVER-BROWSER.md` | the research the server list came out of |

## Thanks

The Multiplayer window is laid out to a design by **Beik**, who drew the wireframe it follows and
made the point the whole thing now turns on: every column of the server list is a fact about the
server, and whether your game can join one is the state of the row rather than a value written over
the top of a column. The things either side of it -- what you are on the left, where you are going
on the right -- are his too.
