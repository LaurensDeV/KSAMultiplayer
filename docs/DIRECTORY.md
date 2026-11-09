# Running a directory

A directory answers one question: *which addresses are worth asking about?* That is an opinion, not
a fact, so a directory is authoritative over nothing and several of them disagreeing is the normal
case rather than a problem to solve.

This is short on purpose. Standing one up should be an afternoon, because what kills a server list
is that only one person ever ran one: the list stops working when that person loses interest, their
domain lapses or their machine goes away, and by then the mod's discovery cannot be fixed without
shipping a new build to everybody.

**So: expect this project's own directory to be the first to go, and run yours as though it will
have to carry the list alone.**

## The quickest one that works

A directory does not have to run anything. The read half below is a document, so a file served over
HTTPS by any host you already have -- a static site, an object store, a web server you keep for
something else -- is a directory, and the mod cannot tell it from this service. Write out the
servers you want to vouch for, serve it, and open a pull request to add its URL to
`directories.txt`.

What you give up is registration: a server cannot announce itself to a file, so the list is whatever
you put in it and the `players`, `world` and `latency` on each row stay whatever you last wrote. The
mod probes every address itself before showing a row as joinable, so a stale file is slow rather
than wrong -- it just cannot grow on its own.

Run the service below when you want servers to be able to add themselves.

## The read half

```
GET https://your.directory/v1/servers
```

```json
{
  "v": 1,
  "directory": "ksarmory",
  "servers": [
    {
      "host": "mp.ksarmory.com",
      "port": 50100,
      "name": "KSArmory",
      "protocol": 23,
      "ksa": "2026.11.10.5801",
      "players": 3,
      "max": 16,
      "password": false,
      "system": "Sol",
      "mods": "Core,KSAMultiplayer",
      "seen": 1794233842
    }
  ]
}
```

`v` must be 1. A reader that does not know a version ignores the whole document rather than reading
it for the fields that happen to look familiar. `host` and `port` are the only required fields on a
server; `seen` is seconds since 1970, or 0 from a directory that keeps no times. A client takes at
most 250 servers from one directory.

**A static file satisfies this.** That is the whole design. A clan's three servers can be a JSON
file on any host that serves HTTPS, and the mod cannot tell it from this service — so if you only
want to publish a fixed list of servers you trust, you do not need to run any software at all.

Everything past the address is a hint. The mod asks every server itself before showing it as
joinable, and overwrites your `players`, `name`, `system` and `mods` with what the server said to
it. Hints decide what is shown first and what is filtered out while the answers are still arriving.

## The write half, which is optional

```
POST   https://your.directory/v1/announce     {"v": 1, "port": 50100, "host": "mp.example.com"}
DELETE https://your.directory/v1/announce     {"v": 1, "port": 50100, "host": "mp.example.com"}
```

`host` may be left out, and then the server is listed at whatever address the announcement arrived
from. Both answer:

```json
{"ok": true, "seen": "45.133.178.13", "listed": false, "say": "being asked about now; it is listed once it answers"}
```

`seen` is the address you saw it come from and `say` is read by a human in a server's log, so write
it for one. A refusal is a 200 with `ok: false` and words in `say`, not a status code: a server
operator needs to know *why*, and a 403 sends them to check a firewall that is fine.

A server announces every 60 seconds and withdraws when it stops or when its operator turns
advertising off.

## The three rules that matter

**1. List what you have seen, never what you were told.** Send the game's own pre-join query to the
address and list only what answers. This is the whole difference between a directory and a text
file. An announcement is unchecked, so a server is free to announce an address that never worked, a
name it has since changed and a player count from an hour ago -- and a list that republishes such
claims fills up with servers nobody can reach while reporting that all is well.

**2. A stated hostname must resolve to the address its announcement came from.** Not because that
proves reachability — the probe does that — but because without it you have built a port scanner
with a public front door: anybody could point your prober at any host and port. With it, a server
can only ever aim your probes at itself.

One exception, and you need it: **a server sharing a machine with your directory has no address of
its own to compare.** Its announcement is turned back by the host before it is given one, so what
arrives is a bridge address like `172.19.0.1`. Such a server gives a name and is held to that
instead, and the name must resolve to something a player could reach. Nothing is lost, because an
announcement from an address no player can reach is one only your own machine could have sent — a
stranger's arrives from a public address, and `X-Forwarded-For` is believed from nowhere but your
proxy's network.

**3. Expire by comparing timestamps when you build the list.** No sweep to schedule, no TTL to get
wrong. This project's directory lists a server while a probe answered within 180 s *and* it
announced itself within 300 s. The second condition matters because a withdrawal can be lost, and
without it a server whose operator opted out stays listed for as long as it keeps answering.

Refuse unreachable addresses at the announcement rather than filtering them later: loopback, private
(10/8, 172.16/12, 192.168/16), link-local, carrier-grade NAT (100.64/10), the documentation ranges,
and multicast and above. An operator behind CGNAT should be told that, plainly, rather than left
wondering why nobody joins.

## Serve the list over HTTPS, never over UDP

A list answered over UDP turns one small datagram into one unconnected reply per registered server.
At a couple of hundred bytes a row that is a few hundred times amplification, aimed at whatever
address the request claimed to come from, and the sender does not have to be able to receive the
answer. A connection that has to be established cannot be aimed at a third party.

## Abuse

The probe is the only check an attacker cannot forge cheaply, because passing it means actually
running the protocol at the address claimed. Everything else is secondary, and worth having anyway:
a per-address rate limit on both endpoints, a cap on how many servers one address may list (this one
allows four), a cap on the list as a whole, and a withdrawal that only the announcing address may
make.

Turnstile and the like are no use here — they want a browser, and a heartbeat sender is a headless
Linux process.

## Getting listed

Open a pull request adding your directory to `directories.txt` in this repository. The mod and every
advertising server read that file at startup, so adding one is a pull request and retiring one is a
commented-out line. Neither is a release, which is the point.

Publish an abuse address and what you will and will not list. A directory with no stated policy
and no way to complain to it becomes an advertising board, and then the ranking becomes the
product.

## Running this one

The image is published and public, so nothing has to be built or cloned:

```bash
docker run -d --name ksamp-directory -p 8080:8080 \
    -e KSAMP_DIRECTORY_NAME="your name here" \
    ghcr.io/laurensdev/ksamp-directory:v0.16.0
```

Pin the tag to a release rather than tracking `main`. A directory reads a welcome in the protocol
version it was built against, so which build you run decides which servers you can list at all.

To build it instead, from a checkout:

```bash
docker build -f deploy/Dockerfile.directory -t ksamp-directory .
```

| | |
|---|---|
| `KSAMP_DIRECTORY_NAME` | what it calls itself on a row, so a player knows which to switch off |
| `KSAMP_TRUST_PROXY` | networks whose `X-Forwarded-For` is believed, comma separated |
| `ASPNETCORE_URLS` | where it listens, default `http://0.0.0.0:8080` |

It needs **outbound UDP** — that is how it sees anything — and inbound HTTP and nothing else. It
keeps nothing on disk and wants no volume: every entry is listed only while a probe answered
recently, so a restart is refilled by the next round of heartbeats and is wrong for one heartbeat
interval. A database here would bring back rows for servers that stopped while it was down, which is
the one thing a list of servers must not do.

Behind a reverse proxy, set `KSAMP_TRUST_PROXY` to the proxy's network and nothing wider. The address
an announcement arrives from is the whole of what this trusts, and `X-Forwarded-For` is a header the
sender chooses.

`GET /health` answers `{"ok":true,"known":1,"listed":1,"protocol":23,"document":1}`. `known` counts
entries and `listed` counts the ones currently in the list; the two differing is normal and is how
many servers are announcing but not answering.

## A directory is tied to one protocol version

It can only read a welcome in the version it was built against, so it cannot list a server speaking
another one — and says so in that server's log, naming both versions. That is deliberate: a row a
player cannot join is noise, and `Wire.Version`, the KSA build and the mod set all have to match
before a join works. During a version change, servers disappear from lists until they update.
