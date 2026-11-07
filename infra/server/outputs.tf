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

# The other worlds, as an operator needs them: what a player types, which container holds the
# console, and which volume holds the save.
output "worlds" {
  description = "Each other world: its address, its container and its volume."
  value = {
    for name, world in var.worlds :
    name => {
      port      = world.port
      container = "ksamp-${name}"
      console   = "docker attach ksamp-${name}"
      volume    = "ksamp-world-${name}"
    }
  }
}
