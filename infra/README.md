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

1. Push to `main`. **Server image** builds `deploy/Dockerfile` and pushes to
   `ghcr.io/<owner>/ksamp-server`.
2. Take the line from that run's summary into `infra/server/.env`:
   `export TF_VAR_server_image=ghcr.io/<owner>/ksamp-server@sha256:...`
3. `tofu apply` in `infra/server`.

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

## The world

The server has no world until a player gives it one, and hands that out to everyone who joins
after. It keeps the latest save in the `ksamp-world` volume, so a restart carries on rather than
starting empty.

```bash
docker run --rm -v ksamp-world:/data -v "$PWD":/backup alpine \
    tar czf /backup/ksamp-world.tar.gz -C /data .
```

Ownership is not kept across a restart: the first player to take control of a craft after one has it.
