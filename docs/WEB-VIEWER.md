# Web viewer — implementation plan

A page served by `ksamp-server` itself, showing what is happening in the server's world: status, players,
chat, a 3D map of the whole system, and admin controls behind a password.
Meant to go upstream as an opt-in feature any operator can turn on.

**Built, on the branch `feat-web-viewer`; not yet run with a game in the world.** All six phases are
in, and "Where it went" below says what was decided along the way and what was measured. What
needs a game is in `docs/DESIGN.md` under "What has not been tried".

## Where it went

- **Phase 0** is answered below, under "How KSA models the system".
- **Web stack:** ASP.NET Core inside the server, as planned. The linux-x64 single file went from
  37.5 MB (39,357,653 bytes) to 49.6 MB (51,992,970 bytes): **+12.0 MB**, three.js included. Everything web is in
  `src/Server/Web`, and only `WebHost.cs` knows Kestrel, so the `HttpListener` fallback touches
  that file.
- **Tick cost:** a view of 16 players and 200 craft takes **0.068 ms** to build
  (`WebCostTests`), once every 0.2 s. Events cost a `TryWrite` each, and nothing on the tick
  thread waits on a socket.
- **Bodies are asked for with a new message, `Describe`** (open question 3), rather than a welcome
  flag: a welcome is said once, and a world that changes system mid-session has to be asked again.
  `Wire.Version` is 36. Only a server whose viewer is on ever asks, so with `web` off nothing
  on the wire changes.
- **The description carries what the game computed,** not the elements as authored: each orbit's
  periapsis, eccentricity, time at periapsis, period, μ and the quaternion into its parent's
  ecliptic axes, and each body's `Cci2Cce` and spin rate and phase. The XML's units and frames
  differ from body to body, and a viewer that re-derived them would be a second place to get
  them wrong.
- **The 3D view is three.js r170**, vendored as `src/Server/Web/Page/three.module.min.js` (MIT,
  licence header kept; 692 KB). It came from the npm package, whose sha512 was checked against
  the registry's. A first version on a 2D canvas could not get depth right: a canvas paints whole
  objects in order, so the half of an orbit in front of a planet was painted over, and craft behind
  one showed through it. three.js draws with a logarithmic depth buffer, everything is placed
  relative to the focus from doubles (the floating origin), and labels are drawn over it on a 2D
  canvas, hidden where a body blocks the line of sight to them.
- **`WebPort` in `Facts`** is said only for a viewer bound to an address the asker can reach. One
  on loopback is behind a proxy whose port is not this one.
- **Web admin may not run** `op`, `deop`, `set`, `settings`, `fresh` or `quit`. Such a line is
  refused before it is logged, so a password typed into one never reaches the log.

## How KSA models the system (Phase 0, build 2026.11.10.5801)

1. **Orbits are fixed two-body Kepler orbits around the parent** (patched conics), with no
   ephemeris and no n-body. `CelestialUpdateTask` only evaluates `Orbit.GetStateVectorsAt(t)`, and
   nothing changes a celestial's orbit after it is made. An orbit is `OrbitData`: periapsis,
   eccentricity, inclination, longitude of ascending node, argument of periapsis, time at
   periapsis (UT s), and the rotation `Orb2ParentCci`. The semi-major axis is derived as
   rp/(1 − e). μ is `Orbit.Mu`, which is the parent's GM unless the template overrides the
   central mass (`CentralMass`, `TwoBody`, `Barycentric`: Alpha Centauri's stars), so a viewer
   must take μ from the orbit and not from the parent. Position at t: M = n·(t − tp) wrapped to
   (−π, π], Newton on Kepler's equation, the perifocal position, turned by `GetOrb2ParentCce()`.
2. **The frames.** Ecl is the root inertial frame: ecliptic J2000-like, right-handed, +Z ecliptic
   north. CCE is a body-centred frame with Ecl's axes, and **positions add up only there**:
   posEcl(child) = posEcl(parent) + posCce(child). CCI is body-centred and non-rotating with +Z
   on the body's spin axis, so **CCI frames do not share axes**; `GetCci2Cce()` turns one into
   ecliptic axes. It is constant, since nothing precesses. Stars and barycentres have
   CCI = CCE = CCF. `Wire.State.PositionCci` is in the craft's body's CCI.
3. **Rotation** is about CCI +Z: θ(t) = ω·t + R0, with CCF→CCI a rotation by θ about z.
   ω = 2π/sidereal period (negative if retrograde), 2π/orbital period if tidally locked, 0 if
   neither. R0 is the private `Celestial.InitialRotation`, set once at boot (UT 0) from the
   longitude meant to face the parent. Latitude and longitude off CCF are asin(z/r) and atan2(y, x).
4. **Per body:** id, parent id, class (stars, barycentres, planets, terrestrial and minor bodies;
   a moon is one whose parent is not a star), mean radius, mass (μ = 6.6743e-11·mass), the orbit as
   above, `GetCci2Cce()`, `GetAngularVelocity()` and `InitialRotation`, and the atmosphere radius
   from `GetAtmosphereRadius()`, which is computed at load and so is only to be had from a game.
   A root (`FixedStar`, `Barycenter`) stays at its `GetPositionEcl()`.



| | |
| --- | --- |
| Where it runs | Inside `ksamp-server`, on its own TCP port. No extra hosting. |
| Who it is for | Every operator: opt-in, off by default, upstream PR. |
| What it shows | Status, roster and chat; a 3D map of the whole system; admin commands. |
| Privacy | Not a per-player concern. The operator turning the viewer on makes everything on it public. |
| Admin auth | A separate web password (`web-admin-password`), **not** the in-game admin tokens. |
| Planet positions | Reported by a game in the world (the server must never reference KSA), cached by the server. |

Before writing code, open an issue or discussion on `LaurensDeV/KSAMultiplayer` describing this plan.
It adds a dependency to the server, bumps `Wire.Version`, and touches the mod, so the maintainer should
agree to the shape first.

## What the server has today (read before changing anything)

- **UDP only.** `src/Server/Program.cs` builds a `GameServer` and ticks it every 5 ms on one thread
  (`TickMs`). Datagrams are read on the socket's thread and queued. `GameServer` is **not thread-safe**,
  and nothing outside the tick thread may touch it.
- **Console commands** go through `ConcurrentQueue<string> commands` and `Command(line, who)` in
  `Program.cs`. `who == null` means the console, and replies go to `Say`. In-game admins come in through
  `server.Asked`.
- **Craft state** is `GameServer.Craft.Said` (`Wire.State`): owner, key, parent `Body`, position and
  velocity in the body-fixed frame (`PositionCcf`/`VelocityCcf`) and inertial frame
  (`PositionCci`/`VelocityCci`), `Body2Ccf` attitude, `Spin`, `Throttle`, `Flags` (`Rails`, `Landed`,
  `EngineOn`, `Controlled`, `Near`) and `Parts`. It is only relayed to players with `Loaded`.
- **Do not build a "spectator client".** A `Loaded` player votes in warps, holds the room's speed down
  as the slowest machine, can set the clock, and can be asked for snapshots. A headless watcher
  would change the game it is watching.
- **Settings** live in `src/Server/Settings.cs` (`Settings.All`). Each key is also a `--flag` and a
  `KSAMP_` environment variable, and `Live` says whether a running server picks it up without a restart.
  `password` is the one value hidden from the log. Do the same for the new password.
- **Prior art for HTTP** is `src/Directory` (ASP.NET Core minimal API, rate limiting, forwarded headers,
  a strict CSP, a page embedded as a resource). Copy its patterns, not its code.

## Architecture

```
 UDP tick thread (GameServer, single-threaded)            Kestrel thread pool (web)
 ─────────────────────────────────────────────            ─────────────────────────
 Tick() ──every 200 ms──▶ builds ServerView (immutable) ──▶ Volatile slot ──▶ GET /api/v1/*
        ──each event────▶ ViewEvents (bounded Channel)  ──▶ fan-out ───────▶ WS /api/v1/live
 commands queue ◀──────── admin POST (line + reply sink) ◀─ session cookie
```

- **One writer, many readers.** The tick thread is the only thing that reads `GameServer`. It
  publishes a fresh immutable `ServerView` at 5 Hz, plus discrete events (chat lines, joins and leaves,
  craft gone, dock/undock, warp phase changes) into a bounded `System.Threading.Channels` channel.
  The web side only ever reads those.
- **Commands from the web** go into the same `commands` queue as the console, carrying a reply sink.
  Generalise `Command(line, who)` so a reply can go to the console, to a player, or to a web request
  that waits on a `TaskCompletionSource` (with a timeout of about 2 s, since commands such as `save`
  answer later through `Said`). Every web command is also logged: `web admin from <ip>: <line>`.
- **Web stack:** add `<FrameworkReference Include="Microsoft.AspNetCore.App" />` to the server csproj
  and host Kestrel inside the existing process. Do not switch the SDK, since `Program.cs` keeps its own
  loop. **Measure** the size of `./tools/publish-server.sh` output before and after, and put both numbers
  in the PR. If the maintainer will not take the size increase, the fallback is `System.Net.HttpListener`
  (in the BCL, WebSockets supported). It is cruder but adds nothing. Write the endpoint layer so this
  swap touches one file.
- **New files:** `src/Server/Web/WebHost.cs` (start/stop, routes), `src/Server/Web/ServerView.cs`
  (snapshot records + builder run on the tick thread), `src/Server/Web/ViewFeed.cs` (WebSocket fan-out,
  per-client throttling), `src/Server/Web/WebAdmin.cs` (password, sessions, rate limit),
  `src/Server/Web/Page/*` (embedded `index.html`, `viewer.js`, `orbits.js`, `map3d.js`, and a vendored
  `three.module.min.js`, which is MIT: keep its licence header, and load nothing from a CDN).

### Settings to add (`Settings.All`)

| Key | Kind | Default | Live | Says |
| --- | --- | --- | --- | --- |
| `web` | Flag | `false` | no | serve the web viewer |
| `web-port` | Whole | `0` | no | TCP port for the web viewer; 0 for the same number as `port` |
| `web-bind` | Text | `127.0.0.1` | no | address the viewer listens on; loopback by default so a reverse proxy is the way out, empty for every address |
| `web-admin-password` | Text | `""` | yes | password for admin commands on the viewer; empty turns web admin off |
| `web-trust-proxy` | Text | `""` | no | addresses of reverse proxies whose `X-Forwarded-*` headers are believed, comma-separated |

Log `web-admin-password` as `(set)`/`(none)`, as `password` is. Say at every start whether the viewer
is on and where, the same way `announcer.Standing()` is always said.

### HTTP surface

| Route | Returns |
| --- | --- |
| `GET /` | the page (CSP: `default-src 'self'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'`) |
| `GET /api/v1/server` | name, system, KSA build, mods, players/max, has world, password needed, protocol version |
| `GET /api/v1/view` | the whole current `ServerView` (the same JSON the socket opens with) |
| `GET /api/v1/bodies` | the cached system description (see Phase 4), with an `ETag`; 404 until a game has reported one |
| `GET /api/v1/chat?after=<seq>` | chat lines after a sequence number, from a ring buffer of the last 200 |
| `WS /api/v1/live` | opens with a full view, then deltas (see below) |
| `POST /api/v1/admin/login` | `{password}` → HttpOnly, SameSite=Strict session cookie (Secure when the request came in over HTTPS, per trusted proxy); 5 tries a minute per address |
| `POST /api/v1/admin/command` | `{line}` → `{lines:[...]}`; needs the session; also needs a custom header (`X-KSAMP: 1`) as a CSRF guard |
| `POST /api/v1/admin/logout` | drops the session |

Compare the password in constant time (`CryptographicOperations.FixedTimeEquals` over SHA-256 of
both). Sessions are random 32-byte tokens kept in memory with an idle expiry (12 h). Changing
`web-admin-password` at runtime ends every session. Read endpoints are rate-limited per address (as
in the Directory project), and the socket has a cap on concurrent connections (e.g. 64).

### `ServerView` (JSON, camelCase, doubles kept as doubles)

```text
ServerView {
  seq, wallTime,                      // monotonic, for clients to order frames
  server:  { name, system, ksaBuild, mods[], maxPlayers, hasWorld },
  clock:   { universe, rate, serverTime },        // client extrapolates universe = universe + rate*(now-serverTime)
  warp:    { phase, who, speed, waiting[] },
  players: [{ id, name, loaded, controlling, rtt, can, watching }],
  crafts:  [{ key, owner, body, universe, posCcf, velCcf, posCci, velCci, rotCcf, spin, throttle,
              flags: { rails, landed, engineOn, lights, controlled, near }, parts }],
  bodiesEtag                          // null until known
}
```

Leave out a craft that has no `Said` yet. Never put anything from `Hello` that is not already public
(no tokens, cookies or passwords) into the view.

**Socket deltas:** every 200 ms, send the changed `players`/`warp`/`clock` and the crafts whose
`Sequence` moved. Throttle per client so a `Controlled` craft goes at 5 Hz and every other craft at
1 Hz. Events (`chat`, `joined`, `left`, `gone`, `dock`, `rename`) are pushed as they happen. A client
that falls behind its buffer is dropped, not waited for. The tick thread must never block on a web
client.

## Phases

### Phase 0: find out how KSA models the system (investigation, no code)

Phases 4 and 5 depend on the answers. Read `../ksa-game-assemblies/current/src` and
`../KSACars/docs/KSA-MODDING-NOTES.md`, then write the answers into this file:

1. Are celestials on fixed Keplerian orbits (patched conics) or on an ephemeris/n-body? Which class
   and fields (`Celestial`, `Orbit`, elements, epoch, GM/μ)?
2. Do all CCI frames share axes, so that a child's position plus its parent's position gives a
   system-frame position? If not, what does `GetCce2Cci()` and its relatives give?
3. How is body rotation modelled (pole, period, angle at epoch), so `posCcf` of a landed craft can be
   placed at any universe time?
4. Which fields a viewer needs per body: id, parent, radius, μ, orbit, rotation, atmosphere height,
   and a type (star, planet, moon) for drawing.

Do not copy KSA textures, meshes or code anywhere. Draw bodies as coloured spheres.

### Phase 1: web host, read model, and the status/roster/chat page

- Settings rows and validation (`SettingsTests`).
- `ServerView` builder on the tick thread, plus a chat ring buffer fed from every `Wire.Chat` the
  server sends or relays (look for `Broadcast(new Wire.Chat(...))` and the `case Wire.Chat` relay in
  `Server.cs`).
- `WebHost` starts after the UDP socket and stops on shutdown, and a port in use is a clear `could not
  start` error.
- Page: server header, a player table (name, ping, flying, speed managed), the chat log, universe time
  and warp state.
- Tests (`tests/WebTests.cs`): a real Kestrel on port 0 over loopback, as the other tests use real UDP.
  Cover the view before and after a loopback client joins and loads, chat arriving over the socket,
  that the view never carries tokens or passwords, and that the viewer is off when `web` is off.

### Phase 2: admin

- `WebAdmin` (password, sessions, login rate limit), and the reply-sink generalisation of `Command`.
- Page: a login box, and a command line with the output under it. Buttons for the common commands
  (`players`, `say`, `kick`, `save`, `status`, `clear`) build the same line a person would type.
- Leave `op`/`deop` and `set password`/`set web-admin-password` out of the web console for now. Say why
  on the page.
- Tests: wrong password, rate limit, expired session, missing CSRF header, a command reaching the
  queue and its reply coming back, and the password never in the log.

### Phase 3: craft feed and the 2D map per body

There is no 2D map in what is built: the 3D map shows everything this one would, and one map is
one thing to keep right. The orbit maths below is `orbits.js`, which the 3D map draws from.

- Crafts in `ServerView` and the socket deltas with per-client throttling.
- Client-side motion between frames: on `rails`, propagate a Kepler orbit from `posCci`/`velCci` at
  `universe` (the 2D view only needs the body's μ, so carry a small built-in μ table until Phase 4,
  or draw only the last fix plus linear extrapolation); `landed`, hold `posCcf`; otherwise, linear
  extrapolation capped at 2 s.
- 2D map: pick a body. Draw a top-down view of its CCI equatorial plane with craft markers, their
  orbits as ellipses, owner colours and names. Add an equirectangular lat/lon strip for landed craft.
  Use plain canvas, with no library.
- Tests: throttling rates, that a slow client gets dropped without stalling `Tick`, and delta ordering.

### Phase 4: bodies reported by a game (protocol and mod change)

- `Wire`: add `BlobKind.Bodies` and send it through the existing `Transfer` chunking, since a system is
  more than a datagram. Add a `WelcomeFlags` bit (or a small new `Kind`) for the server to say "send me
  your system". **Bump `Wire.Version`.** `ProbeVersion` stays.
- Mod (`src/Mod/World.cs`, sent from `Sync`): once `Loaded`, and only when the server asked, describe
  every celestial with the fields Phase 0 found, as finite doubles.
- Server: accept it only from a `Loaded` player whose build is `RequiredBuild` and whose system is the
  world's, as with worlds (see "What the server keeps" in `DESIGN.md`: an unasked blob is dropped).
  Validate it: everything finite, radius > 0, every parent known, no cycles, at most 256 bodies, and a
  size cap. Keep the first good one per `(system, build)` in `--data` as
  `bodies-<system>-<build>.json`, so the viewer works with nobody online. Ask again only when the build
  or system changes.
- Tests: validation rejects, poisoning (a second player with a different description does not replace
  the first), and persistence across a restart.
- **This phase needs a game test** (`./tools/two-games.sh`). Per `CLAUDE.md`, nothing in the mod counts as
  working until it has been seen in game. Record the result in `DESIGN.md`'s "what has not been tried".

### Phase 5: 3D whole-system view

- three.js (vendored), one scene. Keep positions in doubles on the CPU, and give the GPU positions
  relative to the focused object (a floating origin), with a logarithmic depth buffer. At
  solar-system scale, float32 world coordinates jitter by kilometres.
- Bodies placed from `/api/v1/bodies` at the extrapolated universe time; orbits as lines; craft as
  billboards with CSS2D labels. Focus by clicking or from a list, with smooth zoom from system to body
  to craft.
- Toggles: orbits, labels, debris (non-`controlled`), and a "follow player" camera.

### Phase 6: tie-ins and docs

- Append `WebPort` (ushort, 0 for none) to the end of `Wire.Facts`. `CLAUDE.md` says a field added at the
  end does not bump `ProbeVersion`. ksamp.gg and the in-game list can then offer "watch live" (that
  part belongs to the Directory and mod, and can be a later PR).
- `README.md` "Running a server": turning the viewer on, `web-bind` and a reverse proxy (Traefik/Caddy
  example with WebSocket upgrade), the admin password, and opening the TCP port.
- `DESIGN.md`: the view, the bodies store, and the threading rule.
- `deploy/Dockerfile`: `EXPOSE` the web port; document it, do not enable it by default.

## Acceptance

- With `web` off the server behaves exactly as before: same tests and same log lines, apart from
  the "viewer off" line.
- `./tools/test.sh` passes at every phase, and the new tests use real sockets over loopback.
- Load on the tick thread stays within budget: building the view and fanning out events adds at most
  0.2 ms to a tick with 16 players and 200 craft. Measure it, for example with `ClockTrace` or a
  benchmark test.
- Commits are Conventional Commits. A mod-side change is `feat`, and nothing is called a `fix` until it
  has been seen in game.

## Open questions for the maintainer

1. Is the ASP.NET Core size increase to the single-file server acceptable, or HttpListener?
2. Should `web-bind` default to loopback (this plan) or every address?
3. Bodies: a `WelcomeFlags` bit, or a new message kind?
4. Should the Directory card (ksamp.gg) link to a server's viewer when `WebPort` is set?
