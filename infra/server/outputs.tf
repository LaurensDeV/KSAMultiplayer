output "address" {
  description = "Where the server answers, as a player types it."
  value       = var.port == 50100 ? "mp.ksarmory.com" : "mp.ksarmory.com:${var.port}"
}

output "deployed_image" {
  description = "Image digest currently running, or empty when the server is not deployed."
  value       = var.server_image
}

output "world_volume" {
  description = "The volume holding the shared world. Back this up, not the container."
  value       = docker_volume.world.name
}
