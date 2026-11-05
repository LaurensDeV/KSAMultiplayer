# KSA Multiplayer

A shared world for **Kitten Space Agency**: several players, each in their own game, flying in one
universe kept by a **dedicated server that runs on Linux** with no game installed.

Any craft syncs: rockets, rovers, kittens on EVA. The mod's folder and
id are `KSAMultiplayer`, and the server is one executable, `ksamp-server`.

> **Status: played, not finished.** Two games on one machine have flown in one world through it,
> against KSA `2026.10.25.5707`, and the server and the protocol are tested headlessly besides
> (74 tests, real UDP over loopback, a lossy line included). An 89-part rocket has been flown in a
> shared world and staged while the other game watched, which rebuilt the dropped stage as its own
> craft. What two games across the internet do is not known.
> See [docs/DESIGN.md](docs/DESIGN.md#what-has-not-been-tried).

## Playing

1. Install [StarMap](https://github.com/StarMapLoader/StarMap), then take
   `KSAMultiplayer-<version>.zip` from the [releases](https://github.com/LaurensDeV/KSAMultiplayer/releases), unzip it into KSA's `mods` folder and
   enable it, as with any StarMap mod.
2. Everyone needs **the same KSA build** and **every mod the server's world uses**. The server refuses
   anyone who differs and says what is missing.
3. The **Multiplayer** window is there from the moment the game opens, before any world is loaded.
   Enter the server's address (`host:port`, or just `host` for port 50100), your name and the
   password if there is one, and press **Connect**. There is a server at **`mp.ksarmory.com`** to
   try it on.
4. Connecting **replaces your open world with the server's**: save first. If the server has no world
   yet, yours becomes the server's world instead, and nothing is replaced.
5. Hold **Tab** for who is playing and what their ping is. **Enter** opens a chat line, **Enter** sends
   it; a line behind a `/` goes to the server as a command instead, and is said to nobody. Another player's craft carries their name, and is theirs: you can watch it, not fly it.

Once you're in:

- **Fly anything nobody else is flying.** Take control of a craft and it's yours while you are in it
  or looking at it, and for ten seconds after. A craft someone else is flying moves where their
  game says: ask for it and the camera goes there anyway, so you ride along and watch rather than
  fly. Their name is over the craft they're in; their other craft carry a faint `Alice's Lander`.
- **Launch as usual.** A craft you launch, a stage you drop and a kitten you send on EVA show up for
  everyone else.
- **Time is shared.** Every world keeps the server's time, so changing speed is put to everyone
  first: a warp runs only if all agree, and only whoever asked can end it. Pause is a speed like any
  other. The world is never warped past what the slowest machine in it is managing, and slows to
  that machine rather than leaving it behind. After joining, your game may run fast for a few
  seconds to catch up.
- **Chat** from the window.

Two craft with the same name can't both be seen: the panel tells you to rename yours if that happens.

## Running a server

The server is a single self-contained file. Take `ksamp-server-linux-x64.tar.gz` from the
[releases](https://github.com/LaurensDeV/KSAMultiplayer/releases), or build it on any machine with the .NET 10 SDK; it needs no KSA files:

```bash
./tools/publish-server.sh              # linux-x64
```

```bash
./tools/publish-server.sh linux-arm64  # a Raspberry Pi or an ARM VPS
```

Copy `dist/ksamp-server-linux-x64/ksamp-server` to the server and run it:

```bash
./ksamp-server --name "Moon base" --password secret
```

Open **UDP port 50100** in the firewall (and on the router, if the server is at home). Players connect
to the server's address.

### Giving it a world

- **Let the first player bring one.** With no world, the first player to connect gives the server
  theirs.
- **Or copy a save in.** Copy a save folder from `Documents/My Games/Kitten Space Agency/saves/<name>`
  (or a zip of it) to the server and start with `--world <path>`.

The server keeps the world in `--data` (default `./data`) and carries on from it after a restart. Every
five minutes, and whenever someone joins a world that's more than 30 seconds old, it asks the player who
has been in longest for a fresh save, so a player joining starts close to now. Universe time stops while
nobody is connected, unless `--keep-time` is given.

### Options

| Option | Environment | Default | |
| --- | --- | --- | --- |
| `--port` | `KSAMP_PORT` | `50100` | UDP port |
| `--bind` | `KSAMP_BIND` | every address | address to listen on |
| `--name` | `KSAMP_NAME` | `KSA server` | shown to players |
| `--password` | `KSAMP_PASSWORD` | none | |
| `--max-players` | `KSAMP_MAX_PLAYERS` | `16` | |
| `--data` | `KSAMP_DATA` | `./data` | where the world is kept |
| `--world` | `KSAMP_WORLD` | | a save folder or zip to start from, replacing the kept one |
| `--ksa-build` | `KSAMP_KSA_BUILD` | the world's | the KSA build players must run |
| `--snapshot-every` | `KSAMP_SNAPSHOT_EVERY` | `300` | seconds between fresh saves |
| `--keep-time` | `KSAMP_KEEP_TIME` | off | let time run with nobody connected |
| `--warp-answer` | `KSAMP_WARP_ANSWER` | `30` | seconds everyone has to answer a warp |
| `--lapse-within` | `KSAMP_LAPSE_WITHIN` | `10` | seconds a craft stays its owner's once another player is watching it and they are neither in it nor looking at it |
| `--speed` | `KSAMP_SPEED` | `1` | how fast universe time runs |

Commands typed into the server's console: `players`, `say <text>`, `kick <name> [why]`,
`op <name>`, `deop <name>`, `admins`, `clear [name]`, `reload` (or `reset`), `save`, `status`,
`version`, `quit`.
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

### Admins

An admin types those same commands into the game's chat with a `/` in front -- `/clear Rocket_*`,
`/reload`, `/players` -- and the answer comes back in their own chat. `quit` is the console's alone.
A line behind a `/` is said to nobody, so a player who types one where they are no admin has told the
room nothing and is told so themselves.

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

`reload` is also the only way universe time is ever put back, which is why it is a command of its
own rather than something `clear` does on the way past. A game's own
time comes down by loading a world and by no other means -- a game more than three seconds ahead of
the server stops dead until the server catches up -- and while they are all loading there is nobody
in the world, so the server's clock returns to the world's own time at the same moment theirs do.

### As a service

[`deploy/ksamp-server.service`](deploy/ksamp-server.service) is a systemd unit with the install steps
in its header. [`deploy/Dockerfile`](deploy/Dockerfile) builds a container from the repository root:

```bash
docker build -f deploy/Dockerfile -t ksamp-server .
```

```bash
docker run -dit --name ksamp -p 50100:50100/udp -v ksamp-data:/data -e KSAMP_NAME="Moon base" ksamp-server
```

The commands above are read from standard input, so a container wants `-it` to have a console at
all: `docker attach ksamp` then types into it, and Ctrl+P Ctrl+Q leaves it running. Started with
plain `-d`, or run as a systemd service, the server has no console and `clear` and `reload` cannot
be reached -- `tmux` or `screen` is the way to keep one for a service.

## Building

| Command | |
| --- | --- |
| `./tools/test.sh` | the protocol and the server over loopback; needs nothing of KSA |
| `./tools/build.sh` | the server and the mod; the mod needs KSA's assemblies, found by `Directory.Build.props` (set `KSA_DLL_DIR`) |
| `./tools/publish-server.sh [rid]` | the server as one self-contained executable |
| `./tools/package.sh` | the mod as a player installs it, `dist/KSAMultiplayer-<version>.zip` |
| `./tools/deploy.sh` | the mod into this machine's KSA, as a developer's install: it starts the bridge |
| `./tools/run.sh` | deploy and launch the game, showing the mod's log |
| `./tools/play.sh` | the game with this mod and nothing else, in a profile of its own |
| `./tools/two-games.sh --test` | two games and a server on this machine, joined to one world |

| Path | |
| --- | --- |
| `src/Protocol/` | what the server and the mod both speak: the wire format, transfers, the clocks, a client session. Nothing of KSA |
| `src/Server/` | the dedicated server: `GameServer`, the world kept on disk, and the console program |
| `src/Mod/` | the StarMap mod: `Sync` runs the session each frame, `World` is every contact with KSA, `Hooks` the patches, `Panel` the window |
| `tests/` | the protocol and the server driven over loopback |
| `tools/ksa-mcp/` | commands into a running game through the mod's bridge, for driving a test without a hand on the keyboard |
| `deploy/` | a systemd unit and a Dockerfile |
| `infra/` | `mp.ksarmory.com` and the server behind it, as OpenTofu |
| `docs/DESIGN.md` | how it works, and what it cannot do yet |
