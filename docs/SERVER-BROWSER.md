# A server browser

A plan, not a description: none of this is built. It is written down because the research behind the
decisions is worth more than the decisions, and because three of the four things that killed the
comparable projects are design choices made on the first day.

## What a player gets

The start window keeps its address field and its CONNECT button exactly where they are, and grows a
list above them. Typing an address stays the primary way in. The list is a convenience.

That ordering is not modesty. Minecraft and Terraria have the two largest modded multiplayer scenes
in existence and neither has a master list -- discovery happens in Discord and the address bar is
the product. A browser that cannot be reached must therefore never block a player who already has an
address, and must not even look like an error: a directory that times out puts one grey line beside
a working address field, not a modal and not a blank list.

## Directories, not authorities

Several independent services, each answering one question: *what addresses might be worth pinging?*
That is an opinion, not a fact, so none of them is authoritative over anything and a hostile one can
only waste a player's time.

The read side is deliberately dumb enough that **a static file is a valid implementation**:

    GET https://<directory>/v1/servers

```json
{ "v": 1, "directory": "ksarmory",
  "servers": [ { "host": "mp.ksarmory.com", "port": 50100, "name": "KSArmory",
                 "protocol": 23, "ksa": "2026.11.10.5801", "players": 3, "max": 16,
                 "password": false, "system": "Sol", "mods": "...",
                 "seen": 1794233842 } ] }
```

So a directory can be this project's VPS with live heartbeats, or a clan's three servers in a
GitHub Pages repo, or a file on someone's Caddy. The client cannot tell, and does not need to.
Registration is the *optional* half that only a dynamic directory implements.

Plurality is nearly free when the read protocol is a static document -- a loop over URLs and a
merge. It has to be paid for on day one anyway, for the reason in the next section.

### What a directory publishes is what it saw, not what it was told

A directory lists **its own last observation**, taken by sending the game's own pre-join query to
the advertised address. It never republishes a server's self-report.

This is the load-bearing decision. In a snapshot of Luna Multiplayer's one surviving master server
taken 2026-11-09: **306 registered servers, 10 players in total, 6 servers with anybody on them**.
287 of the 306 advertise `::1` as their endpoint -- an address nobody can reach. 79 still carry a
default name and 100 a default description. The list was alive; the servers were not joinable.

Observation instead of self-report kills that whole class at once: an unreachable address never
gets listed, and a player count cannot be inflated by the thing being counted.

The client then re-probes each row itself before showing it as joinable, because a server reachable
from the directory may still be unreachable from one particular player. A registered-but-unreachable
row is greyed out and says so rather than vanishing.

## Where duplicates collapse

Several directories will carry the same server, and the same server will not always be spelled the
same way: `mp.ksarmory.com:50100` from one, `45.133.178.13:50100` from another that only ever
observed an address. Keyed as text those are two rows for one server.

It collapses in two passes, and the second one is free.

**Before probing, by `host:port` written out**, lower-cased and trimmed. Cheap, and it catches the
ordinary case where directories carry the same canonical name, which matters because it is what
stops the browser probing one server once per directory.

**After probing, by the address that answered.** A probe answer arrives from a real socket, so two
rows that are secretly one server produce two answers from one `IPEndPoint` and merge there. This is
the exact point at which identity stops being a string and becomes observable, and the browser is
probing anyway -- so the strong check costs nothing on top of the cheap one.

A merged row keeps the hostname over the bare address, because a name is what a player recognises
and it outlives the machine's address. Between two names, the one from the directory the player
listed first wins, which is a preference they expressed rather than an arbitrary tiebreak.

Disagreement between directories about a count or a world needs no rule worth arguing over: take the
more recently seen, and the browser's own probe overwrites both within the second. Directory
metadata only ever decides what to show first and what to filter out before the probes land.

### What dedupe cannot do, and what covers it

Nothing here stops a hostile directory listing a server of its own under a name like "KSArmory
Official". That is impersonation rather than duplication, and dedupe is the wrong tool.

What covers it is the rule from two sections up, doing work twice: **a row's name comes from the
server's own probe answer, never from the directory that listed it.** A directory chooses which
addresses a player sees and nothing whatsoever about what they are called, so it cannot name a
server it does not run. It can waste a player's time, which is the whole of what a directory is
trusted not to do.

## Compatibility is a query, not a column

`Wire.Version`, the KSA build and the mod set all have to match, so most rows in an unfiltered list
are unjoinable. The browser defaults to **only servers this game can actually join**, with an
explicit opt-in to see the rest and a reason on every excluded row ("needs KSA 2026.11.10.5801",
"protocol 23, you have 22").

Filter in the directory *and* in the client. Luna Multiplayer's `SendServerLists` carries a doc
comment promising it returns only matching versions, and loops over every entry with no version
comparison at all; its one live list mixes six mutually incompatible versions.

## Listing is opt-in, and says so

Three rows in `src/Server/Settings.cs`, which is also the `--flag` list and the `KSAMP_*` list and
the TOML schema:

```csharp
new("advertise", Kind.Flag, "false", "list this server in the public directories so players can find it; its address becomes public", true),
new("advertise-as", Kind.Text, "", "the hostname to be listed under; empty to be listed at the address a directory sees", true),
new("directories", Kind.Text, "", "where to announce, separated by commas; empty for the built-in list", true),
```

Off by default, because a tarball that calls home publishes its operator's address, and a lot of
these are somebody's home machine behind a port forward. An address tied to a household is personal
data under GDPR; `advertise = false` on line one is a better place to be than the alternative.

The cold-start worry that argues for the other default is answered without it. The server already
writes a settings file with defaults on first run, so the comment above arrives for free and the
decision costs one word. The baked-in directory list has this project's own server in it, so the
browser is never empty on day one. And a server that is not listed says so at every start:
`not listed publicly (advertise = false in ksamp-server.toml)`.

`Live: true` means the console's `set advertise true` takes effect without a restart -- so turning
it *off* has to send an explicit delist, rather than leaving the server in the list until its entry
expires.

DarkMultiPlayer's server list lives behind a disclaimer dialog whose source comment records why:
*"This disclaimer exists because I was contacted by a moderator pointing me to the addon posting
rules."* Phoning home is a policy problem before it is a technical one. State plainly what is sent,
keep the off switch visible, and publish a policy page with an abuse address.

## Never silent

The worst failure in the whole survey is Factorio's: the matchmaker returned the right public
address, the server published it, the reverse connection never arrived, and the game said nothing.
Valheim's equivalent boots fine and simply never prints a join code.

So the answer to an announce carries what the directory observed and whether it listed the server,
and the server logs it in words:

    the directory at servers.ksarmory.com could not reach this server at 86.82.x.x:50100:
    players will not be able to connect

## The bootstrap list must be data, not code

DarkMultiPlayer compiled three master addresses into its client. Probed 2026-11-09: one resolves and
answers, one does not resolve at all, one refuses the connection. The project stopped shipping
releases, so the list is frozen and unfixable, and one volunteer's box carries the whole mod.

Luna Multiplayer put the same list in a text file fetched from the repository at runtime every 15
minutes. When the lead author's own master went NXDOMAIN in September 2025, somebody commented out
one line and every client recovered. That is the single best idea in the survey, and it is the one
to copy exactly.

Three sources, unioned:

1. `directories.txt` in this repository, fetched over HTTPS, refreshed every 15 minutes, with the
   last good copy cached on disk. Commenting out a line is a sufficient fix for a dead directory.
2. `Documents/KSAMultiplayer/directories.json`, beside the `tokens.json` already kept there, so a
   player can add a clan's directory or strike one out.
3. A compiled-in default, as the last resort only.

A DNS `TXT` record would do the same job and reuse the hand-rolled resolver in `src/Protocol/Srv.cs`
plus this repository's own Cloudflare layer. It is a fine second mechanism and not needed first.

## Plan for this project's own directory to die first

Both comparable projects lost the *author-operated* master while a volunteer's survived. Luna's wiki
asked for three for that reason and is down to one.

So the directory ships as a container image and a one-page specification, deliberately small enough
that a second operator can stand one up in an afternoon: no state worth protecting, one config file.
Two independent operators before the browser is described to players as reliable, and then a test
that everything still works with this project's own instance switched off.

## Where it runs

On the VPS already paid for, behind the Caddy already running, unproxied.

The arithmetic is not close. Fifty servers on a 60-second heartbeat is ~73,000 requests a day
against Cloudflare Workers Free's 100,000 -- 73% of the budget, and a 30-second heartbeat does not
fit at all. A Durable Object has its own separate 100,000/day line, also at ~73%, and one that never
goes idle spends ~83% of the 13,000 GB-s/day allowance just existing. Three budgets in the eighties,
failing as an opaque Error 1027. Workers KV is wrong three times over: 1,000 writes/day free,
60-second global write propagation, and $5 per million writes on a write-heavy load. D1 probably
breaks its 100,000 rows/day because a primary-key index makes each upsert write two rows.

Against that, the marginal cost here is zero and the marginal infrastructure is one CNAME and one
Caddy site block.

Three things decide it beyond price:

- **The directory has to send UDP**, because observing beats trusting. Cloudflare offers arbitrary
  UDP only on Enterprise, as a paid add-on, and the Workers runtime has no UDP socket API at all.
- **It stays in the language that owns the wire format.** `src/Protocol` already defines what a
  server is and how to ask one; a TypeScript directory would be a second implementation and a second
  place to remember `Wire.Version`.
- **No firewall change.** ufw allows 22, 80 and 443 and denies the rest inbound; a service behind
  Caddy on 443 needs nothing opened.

### Which repository

**This one**, as `src/Directory/`, and not KSArmory as this plan first said.

The reason is the probe. A directory's whole job is to ask servers what they are *in the version
they would be joined in*, so it has to speak the protocol. Here it compiles against
`src/Protocol/*.cs` exactly as the mod and the server do, and cannot drift from `Wire.Version`. In
KSArmory it would need that protocol vendored or packaged, and a second place to remember the
version is what this design is most careful to avoid everywhere else.

What does belong to KSArmory is the part this repository cannot reach: that repository owns the
machine's Caddy, since only one service can hold 80/443 on a single-node Swarm. So the container is
made by `infra/server/directory.tf` here, and the hostname and one `reverse_proxy` line go there.
`feedback` is still the model for how the service is built and deployed -- a minimal API, built to
GHCR by a workflow on pushes touching its own directory, deployed by digest with a `/health` smoke
test -- it is just not the neighbour.

### No datastore

`feedback` keeps its counters in process memory on purpose -- its README says durable counters mean
a database, and the ceiling exists to bound a catastrophe rather than to be exact. The same logic
applies harder here: entries expire after a few minutes, so a restart costs one heartbeat interval
and nothing else. A dictionary behind a lock, no volume, nothing to back up.

Expire by comparing timestamps when the list is assembled, never by a TTL mechanism or a scheduled
sweep. Exact, free, and no extra writes.

### Two things to be careful of on the way in

Behind Caddy the announcing server's address arrives as Caddy's, so the real one comes from
`X-Forwarded-For` -- trustworthy only because Caddy is the single hop and proxying is deliberately
off for this machine's names. `feedback` already configures forwarded headers.

Caddy is one replica with `stop-first` and no working rollback: a bad Caddyfile takes
`api.ksarmory.com` down with it rather than reverting. Render it with
`tofu console <<<'local.caddyfile'` before applying. The two layers also apply separately, and a DNS
record that exists without a matching site block fails the TLS handshake and reads as a broken site
rather than an absent one.

## Abuse

Probe-before-list is the defence that matters, because answering the game's own protocol at the
address you claim is the one thing an attacker cannot forge cheaply. Everything else is secondary:
a per-source-IP limit on both endpoints, a cap on new registrations per address per day, and a deny
list fetched from the repository and applied before a request is handled -- Luna's, which is free
and reviewable by pull request.

Refuse at registration rather than storing and filtering later: loopback, RFC1918, link-local,
CGNAT (100.64/10), multicast.

Turnstile is irrelevant -- it needs a browser and a heartbeat sender is a headless Linux process.
Cloudflare's free plan gives one rate-limiting rule with a 10-second window and a 10-second timeout,
which a patient attacker sits under indefinitely; it is not a defence to rely on.

**The list is served over HTTPS and never as unconnected UDP replies.** Luna's master server carries
the comment explaining why: a small spoofed request producing *n* × 200 bytes of reply is an
`(n * 200)`-times UDP amplification service. This project's own probe answer is already held near
450 bytes against a ~140-byte hello for the same reason.

## The protocol change

`Wire.Welcome` carries name, world system, mod list and has-world. A browser row needs more, and all
of it is already known to the server and already assembled for the console's `status` command:

| field | wire | why |
|---|---|---|
| players | `U8` | the row is half a row without it |
| max players | `U8` | leaks today only in the full-server refusal text |
| flags | `U8` | password required, and room for the next one |
| KSA build | `Text(24)` | the compatibility axis a player cannot otherwise see |

About twenty bytes, taking the cookie welcome from ~450 to ~470 against a ~140-byte hello, so the
amplification factor goes from roughly 3.2 to 3.4. Small, but it belongs in the comment that sets
the cap rather than slipping past it.

`Wire.Version` 22 -> 23. Everybody updates together, so this is worth batching with any other
protocol change that is waiting.

## Probing many servers at once

`Client` opens a UDP socket *and* starts a keepalive thread in its constructor, and `Sync` holds one
probe at a time. Pointing a browser at thirty rows that way is thirty sockets and thirty threads.

So: one socket, fan the hellos out, collect answers by source address, a one-second budget, no
keepalive. New, small, belongs in `src/Protocol`, and testable over loopback by `tools/test.sh`
without KSA.

## Drawing it

The start window is KSA's own console chrome rather than a window of this mod's making, and that
pays off here: `ConsoleStyle.BeginBody()` is an `ImGui.BeginChild` with KSA's scrollbar style already
pushed, so **a long list scrolls for free and looks native**. `ConsoleWidgets` has a
`(label, id, size)` button overload, which is what a per-row JOIN button needs for unique ids.

Use `ImGui.BeginTable`, not `ImGuiHelper.BeginRegion`: a region sizes its label column to the region
and clips anything longer than a word, which already ate "Change settings before starting" down to
"Settings fir". No `BeginTable` exists anywhere in the mod yet.

Fetch directories off the frame thread and poll with `IsCompleted`, as `Sync._finding` already does
for address resolution.

## Phases

**0. Nothing to build.** Manual entry works, and `Sync.Look` already shows a server's name, world
and mods before anybody joins. A remembered recents/favourites list extending the `Settings` record
is local-only, needs no infrastructure, and is worth shipping on its own.

**1. Protocol.** Grow `Welcome`, bump `Wire.Version`, add the multi-probe. Covered by `tools/test.sh`
over loopback with no KSA.

**2. Read-only browser.** The directory service, its DNS record and its Caddy block, serving a
static curated list with one entry. The browser in the mod reads it, probes it, filters it. **This is
a working server browser with no announce path and therefore no abuse surface at all.**

**3. The announce path.** The three settings, the heartbeat on the existing tick loop, probe
verification, delist on shutdown and on `advertise = false`.

**4. Plurality.** The runtime-fetched directory list, the on-disk cache, the player-editable file,
the published spec and image, and a second operator.

**5. Probably never.** NAT punchthrough. Factorio, with a paid team, reached about 70% and a class
of silent carrier-grade-NAT failures. Not until everything above is boring.

## The ecosystem question

There is no official or community server-listing infrastructure to join. KSA's FAQ says only
"Multiplayer is planned"; StarMap has no networking facility; none of KSAModding's 27 RFCs touches
servers or matchmaking. KSA is not on Steam and the FAQ says it will not be, so Steamworks
matchmaking -- free infrastructure for an appid's owner -- is not available at any price.

`ksamods.gg` hosts vehicles and saves, not servers, and has no matchmaking or registry API. What it
does have is the useful precedent: RFC 0094 created a typed, registered, policy-bound host role,
`kind = "share"`, with a specification, conformance rules and a `hosts.toml` entry carrying an API
base URL, byte limits, a per-day cap, a signing key and a policy URL. A `kind = "servers"` role
would be the same shape of thing.

But the charter is explicit that anything not on its list is out of scope until an RFC puts it
there, and servers are not on that list. So: open a pre-RFC discussion, and build independently of
whether it ever concludes.

## Kept true in the same change

`README.md` gains the browser and the `advertise` settings when phase 3 lands. `docs/DESIGN.md`
gains each piece under what has not been tried in game, and loses it only once it has been.
