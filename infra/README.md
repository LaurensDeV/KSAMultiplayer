# Infrastructure

`mp.ksarmory.com`, the dedicated server behind it and the server directory at
`servers.ksarmory.com`, as code. OpenTofu, two layers, the same R2 bucket for state that the
domain's other infrastructure uses.

| Layer | State key | What it owns |
| --- | --- | --- |
| `infra/dns` | `ksamultiplayer/dns.tfstate` | the Cloudflare records |
| `infra/server` | `ksamultiplayer/server.tfstate` | the game containers, the world's volume, the directory container |

The split is by change cadence: the record never changes, the image changes on every release. It
also means a deploy never sources a token that could rewrite the zone.

## It shares a machine, and owns none of it

That machine already serves `ksarmory.com`, from a different repository and a different state. This
layer touches nothing of it:

- **Its own state keys.** `ksamultiplayer/*`, never `ksarmory/*`. Nothing is shared but the bucket.
- **Its own record.** `mp` is declared here as an address record. It is deliberately *not* in the
  zone's `subdomains` set, which is CNAMEs for names Caddy serves — this one is UDP and Caddy
  serves nothing on it.
- **Its own containers.** Not Swarm services. The game server is on no shared network and is
  reached by nothing else on the machine.
- **Its own volume.** `ksamp-world`, marked `prevent_destroy`.

The one exception is the directory, which has to be reachable over HTTPS and so has to be reachable
by that machine's Caddy. It joins the `ksarmory` network **by name**, which is a reference and not
ownership: this layer never creates or destroys that network, so the repository that does is
unaffected. Set `directory_network` to `""` to publish a loopback port instead and share nothing.

A `tofu destroy` here removes this project's DNS records, its containers and nothing else.

## The directory

`servers.ksarmory.com` is the list of servers the mod's browser reads, and it is a container of this
layer's like any other: `directory_image` by digest, `directory_name` for what it calls itself on a
row. Empty `directory_image` means not deployed, which is also how it is taken away.

**Two halves, in two repositories.** This layer owns the container. The name and the TLS in front of
it belong to KSArmory, which owns this machine's Caddy, so standing the directory up took one line
in that repository's Caddyfile and one entry in its `subdomains`:

    servers.ksarmory.com -> reverse_proxy ksamp-directory:8080

Nothing here can make that change, because nothing here owns that state. With `directory_network`
set, Caddy reaches the container by name on the shared network, exactly as it reaches feedback; with
it empty, the container publishes `directory_port` on the loopback and Caddy reaches it at the bridge
gateway instead.

**Changing anything in KSArmory's services layer is a CI job, not a local apply.** A local
`tofu apply` there writes live settings back from whatever the local environment holds, which blanks
the ones it does not know; and targeting Caddy alone does not help, because `depends_on` pulls the
other services in with it. Make the change through that repository's pipeline.

The directory needs **outbound UDP**, since probing is the whole of how it learns anything, and
inbound HTTP from Caddy. It keeps nothing on disk and has no volume: every entry is listed only
while a probe answered recently, so a restart refills from the next round of heartbeats and is wrong
for one heartbeat interval.

## More than one world on the machine

One address carries one UDP port once, so a second world needs a port of its own. An address record
cannot carry a port, so each world past the first also gets an **SRV record**, which can:

    _ksamp._udp.mpcars.ksarmory.com.  300  IN  SRV  0 0 50101 mpcars.ksarmory.com.

Set `worlds` and both records are made for each:

    export TF_VAR_worlds='{ mpcars = { port = 50101 } }'

The mod reads SRV, so a player types `mpcars.ksarmory.com` and nothing else. It asks only when no
port was typed: `mpcars.ksarmory.com:50101` is the player being specific and is dialled as written,
which is also the way in when a resolver mangles SRV or drops it. The address record is there so
that fallback works and so the name resolves for everything else.

Two things to know when it looks broken. Nothing but the mod consults an SRV record -- `ping` and a
browser never will -- so check it with `dig +short SRV _ksamp._udp.mpcars.ksarmory.com` and not by
pinging the name. And a world's record must stay unproxied: a proxied name resolves to Cloudflare,
which carries no UDP, so the name would resolve perfectly and never connect.

`infra/server` takes the same map and gives each world a container and a volume of its own:

    export TF_VAR_worlds='{ mpcars = { port = 50101 } }'

Anything left out of a world's entry is the first server's: its player limit, its snapshot interval,
its memory ceiling. `server_name` defaults to the world's own name, which is what the panel shows.

Then the firewall rule, for the reason the first port's is there — Docker reaches the port without
it, and the rule is how a reader knows that was meant:

```bash
ufw allow 50101/udp comment 'KSA Multiplayer: mpcars'
```

Each world has a console of its own, `docker attach ksamp-mpcars`, and admins of its own: `admins.json`
lives in the volume, so being an admin of one world is nothing in another. `tofu output worlds` in
either layer prints what a player types, which container holds which console, and which volume holds
which save.

**A world is its own container on purpose.** One of them filling its memory, wedging or being
restarted leaves the others running, which is the whole reason these are not one process hosting
several worlds. They share the machine, and the release unless a world says otherwise.

### A world on a build and a set of mods of its own

A world's entry may name an `image`, and then it runs that build until the entry changes, whatever
is released meanwhile; and `mods`, which is what that server turns a player away for not having.
That is a second server for a mod that is not released yet, or for players who run another mod
beside this one:

    export TF_VAR_worlds='{ mpcars = {
      port        = 50101
      server_name = "Cars"
      image       = "ghcr.io/<owner>/ksamp-server@sha256:..."
      mods        = "Core,KSAMultiplayer,FastAndPurrious@0.1.12+8b152a3"
    } }'

**The image** is any build of the server. A release has one, and `gh workflow run "Server image"
--ref <branch>` builds one from a branch and prints the line with its digest. A game and a server
that speak different protocol versions refuse each other by name, so the server's build is already
what holds players to a build of this mod that speaks its protocol: a version on `KSAMultiplayer`
in the list below turns away every player at each release until somebody changes it, for nothing
the protocol number does not already say.

**The mods** are ids separated by commas, as the `mods` setting takes them, and each may be held
to a version after an `@`. A game says what it has with the version each mod's own assembly
carries, which for a mod built from a repository ends in the commit: `0.1.12+8b152a3f...`. So
`FastAndPurrious` wants the mod at any version, `FastAndPurrious@0.1.12` wants that release
whatever commit built it, and `FastAndPurrious@0.1.12+8b152a3` wants that build and no other. A
player with another is told which one the server wants. The version a build carries is in its log's
first lines, and in the refusal of a server that wants a different one. `ksa_build` and
`empty_world` are the settings of the same names, and so are `advertise` and `advertise_as`:
`advertise = true` with `advertise_as = "mp.ksarmory.com"` puts the world in a game's server list
under the machine's name and the world's own port.

**Without a release**, `.github/workflows/worlds.yml` applies the other worlds alone. It reads
them from the repository variable `KSAMP_WORLDS`, which is the same text as `TF_VAR_worlds`, and
reads what the first server and the directory run now out of the state, so neither is touched:

    gh variable set KSAMP_WORLDS --body '{ mpcars = { port = 50101, image = "...", mods = "..." } }'
    gh workflow run Worlds                 # shows the plan
    gh workflow run Worlds -f apply=true

A release's deploy passes the same variable, so a release leaves these worlds as they are: taken
out of it, a release would plan to remove their containers and stop at their volumes. The
firewall rule and the DNS layer's records are still typed by hand, as above.

**Removing a world is deliberately awkward.** Its volume is `prevent_destroy`, so taking the entry
out of the map fails the plan instead of deleting a world's save. Take it out of the state and remove
the volume by hand if that is really what is meant.

## Applying

Each layer takes the same two files, both gitignored and neither belonging in a commit:

```bash
cd infra/<layer>
cp .env.example .env               # what this layer needs
cp backend.hcl.example backend.hcl # R2 credentials for the state
chmod 600 .env backend.hcl

set -a && source .env && set +a
tofu init -backend-config=backend.hcl
tofu plan
```

The Cloudflare token needs **Zone:Read** and **DNS:Edit** on this zone alone. The server layer needs
no token at all beyond SSH to the machine: the image is public.

## Releasing

A `v*` tag goes through **Release**, which builds the image, the mod's zip and the server's
executables, publishes them together, and then applies this layer with the digest it just built. So
a release puts itself on the machine: nothing is copied by hand and no digest is pasted anywhere.

It needs five repository secrets, and without them the deploy is skipped with a notice rather than
failing -- the release itself still happens, and `tofu apply` by hand still works:

| Secret | What |
| --- | --- |
| `DEPLOY_SSH_KEY` | a private key authorised for `ssh_user` on the machine; the provider tunnels the Docker API over it |
| `TF_STATE_BUCKET` | as in `backend.hcl` |
| `TF_STATE_ENDPOINT` | as in `backend.hcl` |
| `TF_STATE_ACCESS_KEY` | as in `backend.hcl` |
| `TF_STATE_SECRET_KEY` | as in `backend.hcl` |

An apply replaces the container, so anybody playing is dropped for the few seconds it takes to come
back. The world is in the `ksamp-world` volume and is not touched. A release that changes
`Wire.Version` locks every player out until they install that release's mod, which is the point of
the version and not a fault of the deploy.

A push to `main` that touches the server still builds an image through **Server image**, but
deploys nothing: what the machine runs is a release.

To deploy by hand anyway -- an image from `main`, or a release whose deploy failed:

1. Take both digests from the runs' summaries into `infra/server/.env`:
   `export TF_VAR_server_image=ghcr.io/<owner>/ksamp-server@sha256:...`
   `export TF_VAR_directory_image=ghcr.io/<owner>/ksamp-directory@sha256:...`
2. `tofu apply` in `infra/server`.

**Set both, every time.** `directory_image` defaults to empty and empty means *not deployed*, so an
apply with only `TF_VAR_server_image` in the environment plans the directory's container away and
takes `servers.ksarmory.com` down. The release workflow exports both for this reason. Read the plan
before confirming it: a destroy you did not mean to ask for is in there in black and white.

By digest rather than by tag, so the machine runs the image that was built. A run whose summary has
scrolled away can be asked of the registry instead, by the commit that built it:

```bash
tag=sha-$(git rev-parse HEAD) owner=<owner>
token=$(curl -sS "https://ghcr.io/token?scope=repository:$owner/ksamp-server:pull" | jq -r .token)
curl -sSI -H "Authorization: Bearer $token" -H 'Accept: application/vnd.oci.image.index.v1+json' \
    "https://ghcr.io/v2/$owner/ksamp-server/manifests/$tag" | grep -i docker-content-digest
```

**A release is the mod and the server together.** `Wire.Version` is what a server and a game must
agree on, and a game of another version is refused by name rather than misread — so a server
deployed ahead of the mod zip is a server nobody can join.

## The port

UDP **50100**, the mod's default, so a player types `mp.ksarmory.com` and nothing else.

`ufw` on that machine defaults to deny and lists 22, 80 and 443. Docker publishes a port by
rewriting the destination before the firewall's input chain sees it, so the server is reachable
without a rule — but that is a quirk of where Docker inserts its rules, not a decision anybody
made. The rule is there to say it on purpose:

```bash
ufw allow 50100/udp comment 'KSA Multiplayer'
```

## The console, and the first admin

The server reads its commands from standard input, so on this machine the console is whatever is
attached to the container. Over SSH:

```bash
docker attach ksamp-server
```

Type `players` to see who is on, then `op <name>` to make one of them an admin. Ctrl+P Ctrl+Q leaves
the container running; Ctrl+C would stop it, and the restart policy would bring it back up empty of
anybody attached. Nobody is an admin until somebody is opped here, and an admin needs to be connected
to be opped, since what makes them one is a token said once to their game.

From then on they type the same commands into the game's chat behind a `/`, so this is a thing done
once rather than the way the server is run. `admins.json` in the `ksamp-world` volume is the list.

## The world

The server has no world until a player gives it one, and hands that out to everyone who joins
after. It keeps the latest save in the `ksamp-world` volume, so a restart carries on rather than
starting empty.

```bash
docker run --rm -v ksamp-world:/data -v "$PWD":/backup alpine \
    tar czf /backup/ksamp-world.tar.gz -C /data .
```

Ownership is not kept across a restart: the first player to take control of a craft after one has it.
