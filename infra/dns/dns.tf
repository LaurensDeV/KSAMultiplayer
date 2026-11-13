# One record: where the dedicated server answers.
#
# An address record rather than a CNAME to vps.ksarmory.com, and never proxied. Cloudflare's proxy
# carries HTTP; a game server speaks UDP, which it does not forward, so a proxied name would resolve
# to Cloudflare and the server would never be reached. A CNAME would work today and break the day
# that name is proxied for the site's sake, which is a change nobody would connect to this.
#
# Declared here rather than added to the zone's own subdomains: that set is CNAMEs for names Caddy
# serves, and every one of them needs a site block in its Caddyfile. Nothing here is served by Caddy.

locals {
  # The first server's whole name: the domain itself where no host is given.
  server_name = var.host == "" ? var.domain : "${var.host}.${var.domain}"
}

resource "cloudflare_dns_record" "server" {
  zone_id = var.zone_id
  name    = local.server_name
  type    = "A"
  content = var.vps_ipv4
  ttl     = var.ttl
  proxied = false

  comment = "KSA Multiplayer dedicated server, UDP ${var.port}. Not served by Caddy; never proxy this."
}

# Every other world on this machine: an address record so the name resolves at all, and an SRV
# record so a client that asks is told which port to use.
#
# Both are needed and neither is enough. The address record alone leaves a player typing the port,
# because an A record cannot carry one. The SRV record alone leaves the name unresolvable to
# everything that does not ask for SRV -- which is every ordinary tool, so the name would look
# broken to whoever is debugging it.

resource "cloudflare_dns_record" "world" {
  for_each = var.worlds

  zone_id = var.zone_id
  name    = "${each.key}.${var.domain}"
  type    = "A"
  content = var.vps_ipv4
  ttl     = var.ttl
  proxied = false

  comment = "KSA Multiplayer world '${each.key}', UDP ${each.value.port}. Not served by Caddy; never proxy this."
}

# The service name is this mod's own, and the protocol label is udp because the game speaks udp.
#
# Priority and weight are both zero: there is one target, so there is nothing to order and nothing
# to share load between.
#
# The target is the world's own address record above, deliberately and not a CNAME to another name:
# RFC 2782 says an SRV target must not be an alias, and a target that resolved to Cloudflare's proxy
# would hand out addresses that carry no UDP at all -- a name that resolves perfectly and never
# connects. Cloudflare will not proxy an SRV record itself, so there is nothing to set here.
resource "cloudflare_dns_record" "world_srv" {
  for_each = var.worlds

  zone_id = var.zone_id
  name    = "_ksamp._udp.${each.key}.${var.domain}"
  type    = "SRV"
  ttl     = var.ttl

  data = {
    priority = 0
    weight   = 0
    port     = each.value.port
    target   = "${each.key}.${var.domain}"
  }

  comment = "Where ${each.key}.${var.domain} answers: UDP ${each.value.port}. Read by the mod, by nothing else."

  # Cloudflare answers with the priority beside the record as well as inside it, and a priority
  # set nowhere but inside is one every plan since offers to take away again.
  lifecycle {
    ignore_changes = [priority]
  }
}

# The directory of servers, which is HTTPS and so is Caddy's to serve on the same machine. An
# address record and unproxied like the rest: Caddy gets its own certificate for the name, and a
# zone whose every record is the machine itself has no reason to send one of them round by
# Cloudflare. The site block that answers at this name is in the layer that owns the machine's
# Caddy, not here.
resource "cloudflare_dns_record" "directory" {
  count = var.directory_host == null ? 0 : 1

  zone_id = var.zone_id
  name    = "${var.directory_host}.${var.domain}"
  type    = "A"
  content = var.vps_ipv4
  ttl     = var.ttl
  proxied = false

  comment = "KSA Multiplayer server directory, HTTPS through the machine's Caddy. Not proxied."
}

