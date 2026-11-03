# KSA Multiplayer

A shared world for **Kitten Space Agency**: several players, each in their own game, flying in one
universe kept by a **dedicated server that runs on Linux** with no game installed.

Any craft syncs: rockets, rovers, kittens on EVA. The mod's folder and
id are `KSAMultiplayer`, and the server is one executable, `ksamp-server`.

> **Status: untested in game.** The server and the protocol are tested headlessly (29 tests, real UDP
> over loopback, a lossy line included), and the mod compiles against KSA `2026.10.25.5707`. No two
> games have been joined through it yet. See [docs/DESIGN.md](docs/DESIGN.md#what-has-not-been-tried).

## Playing

1. Install [StarMap](https://github.com/StarMapLoader/StarMap), then unzip `KSAMultiplayer-<version>.zip`
   into KSA's `mods` folder and enable it, as with any StarMap mod.
2. Everyone needs **the same KSA build** and **every mod the server's world uses**. The server refuses
   anyone who differs and says what is missing.
3. Load any world and get into flight. The **Multiplayer** window is at the top right: enter the server's
   address (`host:port`, or just `host` for port 50100), your name and the password if there is one, then
   press **Connect**.
4. Connecting **replaces your open world with the server's**: save first. If the server has no world
   yet, yours becomes the server's world instead, and nothing is replaced.

Once you're in:

- **Fly anything nobody else is flying.** Take control of a craft and it's yours until you leave. A
  craft someone else is flying moves where their game says, and you can watch it but not fly it.
- **Launch as usual.** A craft you launch, a stage you drop and a kitten you send on EVA show up for
  everyone else.
- **Time is shared.** Time warp and pause are off while connected: every world keeps the server's time.
  After joining, your game may run fast for a few seconds to catch up.
- **Chat** from the window.

Two craft with the same name can't both be seen: the panel tells you to rename yours if that happens.

## Running a server

The server is a single self-contained file. Build it on any machine with the .NET 10 SDK; it needs no
KSA files:

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
| `--speed` | `KSAMP_SPEED` | `1` | how fast universe time runs |

Commands typed into the server's console: `players`, `say <text>`, `kick <name> [why]`, `save`,
`status`, `quit`. It shuts down cleanly on Ctrl+C and on SIGTERM.

### As a service

[`deploy/ksamp-server.service`](deploy/ksamp-server.service) is a systemd unit with the install steps
in its header. [`deploy/Dockerfile`](deploy/Dockerfile) builds a container from the repository root:

```bash
docker build -f deploy/Dockerfile -t ksamp-server .
```

```bash
docker run -d --name ksamp -p 50100:50100/udp -v ksamp-data:/data -e KSAMP_NAME="Moon base" ksamp-server
```

## Building

| Command | |
| --- | --- |
| `./tools/test.sh` | the protocol and the server over loopback; needs nothing of KSA |
| `./tools/build.sh` | the server and the mod; the mod needs KSA's assemblies, found by `Directory.Build.props` (set `KSA_DLL_DIR`) |
| `./tools/publish-server.sh [rid]` | the server as one self-contained executable |
| `./tools/package.sh` | the mod as a player installs it, `dist/KSAMultiplayer-<version>.zip` |

| Path | |
| --- | --- |
| `src/Protocol/` | what the server and the mod both speak: the wire format, transfers, the clocks, a client session. Nothing of KSA |
| `src/Server/` | the dedicated server: `GameServer`, the world kept on disk, and the console program |
| `src/Mod/` | the StarMap mod: `Sync` runs the session each frame, `World` is every contact with KSA, `Hooks` the three patches, `Panel` the window |
| `tests/` | the protocol and the server driven over loopback |
| `deploy/` | a systemd unit and a Dockerfile |
| `docs/DESIGN.md` | how it works, and what it cannot do yet |
