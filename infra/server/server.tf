# The dedicated server on a machine that is already running other things, and must go on running
# them. Nothing here is shared with them: its own container, its own volume, its own port.
#
# A plain container rather than a Swarm service, though the machine runs Swarm, for two reasons.
# Swarm's routing mesh gives every packet the ingress gateway's source address, and the server tells
# players apart by the address a datagram came from, so behind the mesh they would all look like one
# player arriving from one place. And a container is reached by nothing on that machine: a mistake
# here cannot roll a service that is serving the site.

resource "docker_image" "server" {
  count = var.server_image == "" ? 0 : 1

  name         = var.server_image
  keep_locally = false
  force_remove = true
}

# The world lives here. Losing it is losing the shared save: the server hands this out to everyone
# who joins, and a new one starts empty and takes its world from the first player to arrive.
resource "docker_volume" "world" {
  name = "ksamp-world"

  lifecycle {
    prevent_destroy = true
  }
}

resource "docker_container" "server" {
  count = var.server_image == "" ? 0 : 1

  name  = "ksamp-server"
  image = docker_image.server[0].repo_digest

  restart  = "unless-stopped"
  must_run = true

  # The server's commands -- op, clear, reload -- are read from standard input, and a container with
  # no terminal has none to read: without these there is no way to make anybody an admin on this
  # machine at all. With them, `docker attach ksamp-server` over SSH is the console, and Ctrl+P
  # Ctrl+Q leaves it running. Changing either replaces the container; the world is in its volume.
  stdin_open = true
  tty        = true

  # Published, not host-networked: publishing is a destination rewrite and leaves the sender's
  # address alone, which is the one thing the server needs from the network.
  ports {
    internal = 50100
    external = var.port
    protocol = "udp"
  }

  volumes {
    volume_name    = docker_volume.world.name
    container_path = "/data"
  }

  # The image's entrypoint already passes --data /data; everything else is read from the environment.
  env = [
    "KSAMP_NAME=${var.server_name}",
    "KSAMP_MAX_PLAYERS=${var.max_players}",
    "KSAMP_SNAPSHOT_EVERY=${var.snapshot_every_seconds}",
  ]

  # Two cores and 8 GB are shared with the site. A relay of craft states needs very little of
  # either, and a bound means a server that misbehaves cannot take the rest of the machine with it.
  memory = var.memory_mb

  # Long enough for SIGTERM to be answered: the server says goodbye to every player before it closes.
  stop_timeout = 20

  # The default is unbounded, and a busy server writes a line per join, leave and chat.
  log_driver = "json-file"
  log_opts = {
    max-size = "10m"
    max-file = "3"
  }
}
