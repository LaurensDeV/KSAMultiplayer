# A directory on this machine: the list of servers the mod's browser reads.
#
# Separate from the game server on purpose. It speaks HTTP rather than UDP, it keeps nothing, and it
# is the one part of this that strangers POST to -- so it gets its own container, its own memory
# ceiling and its own restart, and a directory being hammered or wedged leaves the world alone.
#
# It has to be reachable over HTTPS, and this machine's Caddy belongs to the KSArmory repository.
# Two ways to join them, and `directory_network` chooses:
#
#   set    the container joins that network and publishes no port, so Caddy reaches it by name --
#          `reverse_proxy ksamp-directory:8080`, exactly as it reaches feedback. Needs the network
#          to be attachable; tofu says so plainly if it is not.
#   empty  the container publishes `directory_port` on the loopback, and Caddy reaches it at the
#          bridge gateway -- `reverse_proxy 172.17.0.1:8090`. Uglier, and needs nothing of anybody.
#
# Either way one line goes into KSArmory's Caddyfile and one label into its `subdomains`. See
# infra/README.md; nothing here can do it, because nothing here owns that state.

locals {
  directory = var.directory_image == "" ? 0 : 1
  # Joining a network by name is a reference and not ownership: this layer never creates or destroys
  # it, so the repository that does is unaffected.
  directory_networked = var.directory_network == "" ? 0 : 1
}

resource "docker_image" "directory" {
  count = local.directory

  name         = var.directory_image
  keep_locally = false
  force_remove = true
}

resource "docker_container" "directory" {
  count    = local.directory
  name     = "ksamp-directory"
  image    = docker_image.directory[0].repo_digest
  hostname = "ksamp-directory"

  # No stdin and no tty: it has no console, unlike the game server, and nothing to type at.
  restart  = "unless-stopped"
  must_run = true

  dynamic "networks_advanced" {
    for_each = local.directory_networked == 1 ? [var.directory_network] : []
    content {
      name = networks_advanced.value
    }
  }

  # Only when it is not on a network Caddy can already reach it by. On the loopback, because the
  # firewall allows 22, 80 and 443 and nothing else, and a port published on every address would
  # bypass that through Docker's own DNAT.
  dynamic "ports" {
    for_each = local.directory_networked == 1 ? [] : [var.directory_port]
    content {
      internal = 8080
      external = ports.value
      ip       = "127.0.0.1"
      protocol = "tcp"
    }
  }

  env = [
    "KSAMP_DIRECTORY_NAME=${var.directory_name}",
    "KSAMP_TRUST_PROXY=${var.directory_trust_proxy}",
    # A line per request, with a health check every thirty seconds, is tens of thousands of lines a
    # day saying this is still up.
    "Logging__LogLevel__Microsoft.AspNetCore=Warning",
  ]

  # Kilobytes of state and one socket. The ceiling is here to bound a mistake, not to fit anything.
  memory = var.directory_memory_mb

  # The host's journal, as the world's log is: a deploy replaces the container, and a log inside one
  # goes with it.
  log_driver = "journald"
  log_opts = {
    tag = "ksamp-directory"
  }
}

output "directory" {
  description = "Where the directory answers, before Caddy puts a name and a certificate on it."
  value = local.directory == 0 ? "not deployed" : (
    local.directory_networked == 1
    ? "ksamp-directory:8080 on the ${var.directory_network} network"
    : "127.0.0.1:${var.directory_port} on the host"
  )
}
