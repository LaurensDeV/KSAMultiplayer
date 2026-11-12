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
you put in it and the `players` and `system` on each row stay whatever you last wrote. The mod
probes every address itself before showing a row as joinable, so a stale file is slow rather than
wrong -- it just cannot grow on its own.

Three things such a file still has to get right: `v` is exactly 1, the URL you publish is `https://`
because an `http://` one is skipped without a word, and `seen` is a real timestamp -- a row with
`seen: 0` loses every tie to a live directory carrying the same server.

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
server; `seen` is seconds since 1970, or 0 from a directory that keeps no times. A reader takes at
most 250 servers from one directory, stops at a megabyte and gives up after eight seconds, so a
listing has to be small and quick rather than complete. A row with no `host`, or a port outside
1-65535, is dropped and the rest kept; `servers` may be absent, and fields a reader does not know
are ignored.

Everything past the address is a hint, and this mod reads almost none of it: `name` fills a row
until the server answers for itself, and `seen` breaks a tie when two directories carry the same
server. `protocol`, `ksa`, `players`, `max`, `password`, `system` and `mods` are read by nothing
today — the mod asks every server for all of it and shows what the server said. Write them anyway
where they are cheap, since another reader may want them, but `host`, `port` and `name` are the
whole of what matters now.

A listing is IPv4 only. The protocol's socket and every name this project resolves are IPv4, so an
IPv6-only server cannot be listed, and a name is followed to its A records alone.

## The write half, which is optional

```
POST   https://your.directory/v1/announce     {"v": 1, "port": 50100, "host": "mp.example.com"}
DELETE https://your.directory/v1/announce     {"v": 1, "port": 50100, "host": "mp.example.com"}
```

**Only the listing URL is ever configured; the announce URL is derived from it.** A trailing
`/servers` is replaced with `/announce`, and anything else has `/announce` appended — so a listing
at `https://d.example/list.json` takes announcements at `https://d.example/list.json/announce`.
Serve yours at a path ending `/servers` unless you want them arriving underneath it.

`host` is sent as `""` rather than left out when a server has no name to give, and is then listed at
whatever address the announcement arrived from; do not tell the two apart. The `DELETE` carries a
JSON body, which some proxies and frameworks drop — where yours does, no server can ever withdraw.
Both answer:

```json
{"ok": true, "seen": "45.133.178.13", "listed": false, "say": "being asked about now; it is listed once it answers"}
```

`seen` is the address you saw it come from and `say` is read by a human in a server's log, so write
it for one.

A refusal *about the server* — an address nobody can reach, nothing answering at it, the list full,
too many already from that address — is a 200 with `ok: false` and words in `say`, never a status
code: an operator needs to know *why*, and a 403 sends them to check a firewall that is fine. A
request you cannot act on at all is a status code: 400 for a body that will not parse or a `v` you
do not know, 415 for a body that is not JSON, 429 over a rate limit. Put the `say` body on a 400
wherever you can manage one, because a sender reads any non-2xx as "not listed, reason unknown".
`seen` is `""` when a request never got far enough for the address to matter.

A server announces every 60 seconds and withdraws when it stops or when its operator turns
advertising off. A rate limit wants to leave that room and little more: this one allows 20
announcements and 60 reads a minute from one address.

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
(10/8, 172.16/12, 192.168/16), link-local, carrier-grade NAT (100.64/10), `0/8`, the documentation
and benchmarking (198.18/15) ranges, and multicast and above. An operator behind CGNAT should be told that, plainly, rather than left
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
allows four), a cap on the list as a whole (this one, 500), and a withdrawal that only the
announcing address may make.

Turnstile and the like are no use here — they want a browser, and a heartbeat sender is a headless
Linux process.

## Getting listed

Open a pull request adding your directory to `directories.txt` in this repository. The mod re-reads
that file every time it refreshes its list, so adding one is a pull request and retiring one is a
commented-out line. Neither is a release, which is the point. The URL has to be `https://`; an
`http://` line is skipped without a word.

**Servers find you too, without anybody installing anything.** A server with `advertise` on reads
the same file once an hour and announces to every directory in it, so a merged pull request reaches
running servers within the hour, and a line commented out stops them asking within the hour as well.
A server that names its own `directories` announces to those alone.

A server reads it *only* while advertising. One nobody opted in fetches nothing and asks nobody,
which is the same reason the setting exists at all: publishing an address is a decision for whoever
runs the machine, and so is telling anybody that the machine is there.

Publish an abuse address and what you will and will not list. A directory with no stated policy
and no way to complain to it becomes an advertising board, and then the ranking becomes the
product.

## Running this one

The image is published and public, so nothing has to be built or cloned:

```bash
docker run -d --name ksamp-directory -p 8080:8080 \
    -e KSAMP_DIRECTORY_NAME="your name here" \
    ghcr.io/laurensdev/ksamp-directory:v0.19.1
```

Pin the tag to a release rather than tracking `main`. A directory reads a welcome in the protocol
version it was built against, so which build you run decides which servers you can list at all.

It serves plain HTTP, so put TLS in front of it — any reverse proxy will do. What you submit to
`directories.txt` has to be `https://`, and an `http://` URL is skipped by every reader without a
word.

To build it instead, from a checkout:

```bash
docker build -f deploy/Dockerfile.directory -t ksamp-directory .
```

| | |
|---|---|
| `KSAMP_DIRECTORY_NAME` | what it calls itself on a row, so a player knows which to switch off |
| `KSAMP_TRUST_PROXY` | networks whose `X-Forwarded-For` is believed, comma separated; the loopback and the private ranges already, which covers a proxy beside it on a host or a Docker network |
| `ASPNETCORE_URLS` | where it listens, default `http://0.0.0.0:8080`. Keep the 8080 inside the container — the image's health check asks `127.0.0.1:8080` — and publish it elsewhere with `-p` |

It needs **outbound UDP** — that is how it sees anything — and inbound HTTP and nothing else. It
keeps nothing on disk and wants no volume: every entry is listed only while a probe answered
recently, so a restart is refilled by the next round of heartbeats and is wrong for one heartbeat
interval. A database here would bring back rows for servers that stopped while it was down, which is
the one thing a list of servers must not do.

`KSAMP_TRUST_PROXY` already covers the loopback and the private ranges, which is what a proxy beside
it on a host or a Docker network needs. Narrow it to your proxy's own network if strangers can reach
you from one of those ranges as well. The address an announcement arrives from is the whole of what
this trusts, and `X-Forwarded-For` is a header the sender chooses.

`GET /health` answers `{"ok":true,"known":1,"listed":1,"protocol":23,"document":1}`. `known` counts
entries and `listed` counts the ones currently in the list; the two differing is normal and is how
many servers are announcing but not answering.

There is no conformance suite for a directory of your own. The nearest things are this one's
`/health`, `tests/RosterTests.cs` for the expiry and reachability rules, and pointing a game at your
URL through its own `directories.json` to see whether rows appear.

## A directory is tied to one protocol version

It can only read a welcome in the version it was built against, so it cannot list a server speaking
another one — and says so in that server's log, naming both versions. That is deliberate: a row a
player cannot join is noise, and `Wire.Version`, the KSA build and the mod set all have to match
before a join works. During a version change, servers disappear from lists until they update.
