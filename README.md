# KSA Multiplayer

A shared world for **Kitten Space Agency**: several players, each in their own game, flying in one
universe kept by a **dedicated server that runs on Linux** with no game installed.

Any craft syncs: rockets, rovers, kittens on EVA. The mod's folder and
id are `KSAMultiplayer`, and the server is one executable, `ksamp-server`.

> **Status: played, not finished.** Two games on one machine have flown in one world through it,
> against KSA `2026.11.10.5801`, and the server and the protocol are tested headlessly besides
> (166 tests, real UDP over loopback, a lossy line included). An 89-part rocket has been flown in a
> shared world and staged while the other game watched, which rebuilt the dropped stage as its own
> craft. What two games across the internet do is not known.
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
4. Connecting **replaces your open world with the server's**: save first. If the server has no world
   yet, yours becomes the server's world instead, and nothing is replaced: joined straight from
   the start, that is the new game KSA opens on. A game set to start in sandbox, at no craft, waits
   until there is one to hand over.
5. Hold **Tab** for who is playing and what their ping is. **Enter** opens a chat line, **Enter** sends
   it; a line behind a `/` goes to the server as a command instead, and is said to nobody. Another player's craft carries their name, and is theirs: you can watch it, not fly it.

Once you're in:

- **Fly anything nobody else is flying.** Take control of a craft and it's yours while you are in it
  or looking at it, and for ten seconds after. A craft someone else is flying moves where their
  game says: ask for it and the camera goes there anyway, so you ride along and watch rather than
  fly. Their name is over the craft they're in; their other craft carry a faint `Alice's Lander`.
- **Launch as usual.** A craft you launch, a stage you drop and a kitten you send on EVA show up for
  everyone else.
- **Other mods' craft.** A mod that adds to a craft -- a car's steering and wheels -- can share
  that with every other game through StarMap, without either mod knowing of the other. KSACars
  does. `docs/MOD-API.md` is what a mod has to say and listen for.
- **Dock.** Your craft and another become one craft, for everyone, and two again when you undock.
  Docked with another player's craft, the one craft belongs to whoever's game docked them first:
  that player flies it and the other rides along, watching. Only its owner can undock it. When
  they do, each half goes back to whoever had it, the rider's about ten seconds after its owner
  has flown the other half away.
- **Time is shared.** Every world keeps the server's time, so changing speed is put to everyone
  first: a warp runs only if all agree, and only whoever asked can end it. Pause is a speed like any
  other. The world is never warped past what the slowest machine in it is managing, and slows to
  that machine rather than leaving it behind. After joining, your game may run fast for a few
  seconds to catch up.
- **Chat** from the window.

Two craft with the same name are still two craft: the server gives the second one a name of its own,
your game goes on calling it what you called it, and the player list says *known to the others as*
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

Open **UDP port 50100** in the firewall (and on the router, if the server is at home). Players connect
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
port = 50100
```

At the console, `set` lists every setting and where its value came from; `set <key> <value>`
changes one and writes it to the file, and `settings` reads the file again after it has been edited.
Either takes effect at once, except `port`, `bind` and `data`, which wait for a restart; a setting a
flag or the environment gives stays theirs, and the console says so. Both are the console's alone,
like `quit`.

| Option | Environment | Default | |
| --- | --- | --- | --- |
| `--port` | `KSAMP_PORT` | `50100` | UDP port |
| `--bind` | `KSAMP_BIND` | every address | address to listen on |
| `--name` | `KSAMP_NAME` | `KSA server` | shown to players |
| `--id` | `KSAMP_ID` | none | a short name that never changes though the name and the address may, in letters, digits, dots, dashes and underscores (`my-clan-cars`). Borea knows the mods it installed for this server by it, and offers to update that instance when the server's mods change. Without one the server is known by the address a player reaches it at |
| `--password` | `KSAMP_PASSWORD` | none | |
| `--max-players` | `KSAMP_MAX_PLAYERS` | `16` | |
| `--data` | `KSAMP_DATA` | `./data` | where the world is kept |
| `--world` | `KSAMP_WORLD` | | a save folder or zip to start from, replacing the kept one; not a setting in the file, since it is something to do once |
| `--ksa-build` | `KSAMP_KSA_BUILD` | the build this server was built for | the KSA build players must run |
| `--mods` | `KSAMP_MODS` | the world's | the mods players must have, comma separated, each held to a version after an `@` where one is given: `FastAndPurrious@0.1.13` for that release, `FastAndPurrious@0.1.13+8b152a3` for one build of it. The whole list, not a floor: a player running anything else is refused. Worth setting, because the world's own list follows whoever last handed a save over |
| `--snapshot-every` | `KSAMP_SNAPSHOT_EVERY` | `300` | seconds between fresh saves |
| `--keep-time` | `KSAMP_KEEP_TIME` | off | let time run with nobody connected |
| `--warp-answer` | `KSAMP_WARP_ANSWER` | `30` | seconds everyone has to answer a warp |
| `--lapse-within` | `KSAMP_LAPSE_WITHIN` | `10` | seconds a craft stays its owner's once another player is watching it and they are neither in it nor looking at it |
| `--speed` | `KSAMP_SPEED` | `1` | how fast universe time runs |
| `--empty-world` | `KSAMP_EMPTY_WORLD` | | start with an empty world of this system (`SolLite` or `Sol`) instead of waiting for a player; also what `fresh` puts in place |
| `--advertise` | `KSAMP_ADVERTISE` | off | list this server publicly so players can find it, which publishes its address |
| `--advertise-as` | `KSAMP_ADVERTISE_AS` | the address seen | the hostname to be listed under |
| `--directories` | `KSAMP_DIRECTORIES` | the shared list | where to announce, separated by commas; the shared list in `directories.txt` otherwise, read hourly while advertising |

Commands typed into the server's console: `help`, `players`, `say <text>`, `kick <name> [why]`, `op <name>`, `deop <name>`, `admins`,
`clear [name]`, `reload` (or `reset`), `save`, `status`, `version`, `fresh`, `set [key value]`,
`settings`, `quit`.

`fresh` throws the world away and puts an empty one in its place -- the one `empty-world` names:
every craft gone, universe time started over, and everybody handed the new world where they stand.
Like `quit`, `settings` and `set`, it is the console's alone and an admin cannot type it in chat.
There is no undoing it.
It shuts down cleanly on Ctrl+C and on SIGTERM.

`version` says what the server is, the protocol it speaks and the KSA build it holds players to --
the protocol being the number that decides who can join, and the one to ask for when somebody is
refused by name. It is said at the top of the log too, so reading `docker logs` answers it without
attaching to anything.

`clear` is the broom for a world filling up with spent stages. Every craft nobody is flying is taken
out of every game that is in the world; a name narrows it, with `*` and `?` for wildcards --
`clear Rocket_*` -- and a craft somebody is flying is never taken, whatever is asked for. The name
goes to the games and each sweeps its own world by it, because the server only hears of a craft when
somebody claims it: the fragments a spent stage breaks into belong to nobody, and they are most of
what there is to sweep. The world on disk still holds them, so a fresh save is asked for -- from a game
that has already swept, so what lands on disk is clean. Nobody is moved: everybody is standing in a
world they swept themselves, so clearing costs no loading screen and leaves the clock alone.

`reload`, or `reset`, is the other half and is asked for on its own: the world as it stands goes out
to everybody again, and each game loads it and comes back in a few seconds. Nobody reconnects, and
nobody loses what they are flying.

It is also the only way universe time is ever put back, which is why it is a command of its own
rather than something `clear` does on the way past. A game's own time comes down by loading a world
and by no other means -- a game more than three seconds ahead of the server stops dead until the
server catches up -- and while they are all loading there is nobody in the world, so the server's
clock returns to the world's own time at the same moment theirs do.

### Admins

An admin types those same commands into the game's chat with a `/` in front -- `/clear Rocket_*`,
`/reload`, `/players` -- and the answer comes back in their own chat.
A line behind a `/` is said to nobody, so a player who types one where they are no admin has told the
room nothing and is told so themselves. `quit`, `fresh`, `settings` and `set` are the console's
alone, each saying so when an admin tries it.

Who is one starts at the console, since nobody is one on a server that has just come up:

```bash
op Jeb
```

That hands Jeb's game a random token, which it keeps in `Documents/KSAMultiplayer/tokens.json` beside
the server's address and offers at every join after, so Jeb is an admin whenever he comes back with
nothing to type and no password to lose. `deop Jeb` takes it back, whether or not he is playing, and
his game throws the token away when it next hears from the server. `op` on somebody who is already an
admin replaces their token, which is how one that was lost on a reinstalled machine is replaced.

The list is `admins.json` in `--data`, beside the world, and holds only each token's **hash**: enough
to take an admin away, not enough to become one. A player has to be connected to be opped -- the token
is said once, to a game, and never stored anywhere it could be said again -- and the name it is
remembered under follows whoever last joined with it, so `deop` names the player you can see.

### As a service

Every release publishes a container image, so there is nothing to build or clone:

```bash
docker run -dit --name ksamp -p 50100:50100/udp -v ksamp-data:/data ghcr.io/laurensdev/ksamp-server:v0.20.1
```

A release's notes carry the same line pinned to that build's digest, which is what to run on a
machine you care about: a tag moves and a digest does not.
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
