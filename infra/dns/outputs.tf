output "address" {
  description = "What a player types into the mod's Server field."
  value       = var.port == 50100 ? "${var.host}.${var.domain}" : "${var.host}.${var.domain}:${var.port}"
}

# What a player types for each of the other worlds. A port is shown because a mod that does not read
# SRV records needs it; one that does needs only the name, and both are true at once.
output "worlds" {
  description = "Each other world's name, and what a player types to reach it."
  value = {
    for name, world in var.worlds :
    name => {
      address    = "${name}.${var.domain}:${world.port}"
      bare       = "${name}.${var.domain}"
      srv        = "_ksamp._udp.${name}.${var.domain}"
      checked_by = "dig +short SRV _ksamp._udp.${name}.${var.domain}"
    }
  }
}
