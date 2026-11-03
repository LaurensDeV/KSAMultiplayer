# One record: where the dedicated server answers.
#
# An address record rather than a CNAME to vps.ksarmory.com, and never proxied. Cloudflare's proxy
# carries HTTP; a game server speaks UDP, which it does not forward, so a proxied name would resolve
# to Cloudflare and the server would never be reached. A CNAME would work today and break the day
# that name is proxied for the site's sake, which is a change nobody would connect to this.
#
# Declared here rather than added to the zone's own subdomains: that set is CNAMEs for names Caddy
# serves, and every one of them needs a site block in its Caddyfile. Nothing here is served by Caddy.

resource "cloudflare_dns_record" "server" {
  zone_id = var.zone_id
  name    = "${var.host}.${var.domain}"
  type    = "A"
  content = var.vps_ipv4
  ttl     = var.ttl
  proxied = false

  comment = "KSA Multiplayer dedicated server, UDP ${var.port}. Not served by Caddy; never proxy this."
}
