output "address" {
  description = "What a player types into the mod's Server field."
  value       = var.port == 50100 ? "${var.host}.${var.domain}" : "${var.host}.${var.domain}:${var.port}"
}
