# Infrastructure

`mp.ksarmory.com` and the dedicated server behind it, as code. OpenTofu, two layers, the same R2
bucket for state that the domain's other infrastructure uses.

| Layer | State key | What it owns |
| --- | --- | --- |
| `infra/dns` | `ksamultiplayer/dns.tfstate` | the one Cloudflare record |
| `infra/server` | `ksamultiplayer/server.tfstate` | the container and the world's volume |

The split is by change cadence: the record never changes, the image changes on every release. It
also means a deploy never sources a token that could rewrite the zone.

## It shares a machine, and owns none of it

That machine already serves `ksarmory.com`, from a different repository and a different state. This
layer touches nothing of it:

- **Its own state keys.** `ksamultiplayer/*`, never `ksarmory/*`. Nothing is shared but the bucket.
- **Its own record.** `mp` is declared here as an address record. It is deliberately *not* in the
  zone's `subdomains` set, which is CNAMEs for names Caddy serves — this one is UDP and Caddy
  serves nothing on it.
- **Its own container.** Not a Swarm service, not on the `ksarmory` overlay network, reached by
  nothing else on the machine.
- **Its own volume.** `ksamp-world`, marked `prevent_destroy`.

A `tofu destroy` here removes one DNS record, one container and nothing else.

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

1. Take the digest from the run's summary into `infra/server/.env`:
   `export TF_VAR_server_image=ghcr.io/<owner>/ksamp-server@sha256:...`
2. `tofu apply` in `infra/server`.

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
