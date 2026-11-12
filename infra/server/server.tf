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

  # The host's journal, not the container's own file. Docker throws a json-file log away with the
  # container that wrote it, and a release replaces this container every time: every line since the
  # last deploy went with it, which is exactly when the lines before it are worth reading. A question
  # as ordinary as who joined and left yesterday could not be answered the morning after a release.
  #
  # `docker logs` reads a journald container as it always did. `journalctl CONTAINER_TAG=ksamp` reads
  # it without Docker, and reads what the container before this one said. How much is kept is the
  # host journal's business now (SystemMaxUse), so there is no per-container cap here -- and if that
  # journal is volatile, the lines still last until the machine reboots rather than until the next
  # release.
  log_driver = "journald"
  log_opts = {
    tag = "ksamp"
  }
}


# Every other world on this machine. One container and one volume each, deliberately: a world that
# fills its memory or needs restarting takes nothing else with it, which is the whole reason these
# are not one process hosting several worlds.
#
# The image is the release's, the same one the first server runs, so a release moves every world
# together. Separate containers mean they need not -- a world can be held on an older image by hand
# when that is wanted -- which is the property this shape buys and one process would not have.

# The image of a world that names one of its own. Immutable like the release's, and for the same
# reason: the machine runs what was built, and a tag can be moved.
resource "docker_image" "other_world" {
  for_each = var.server_image == "" ? {} : { for name, world in var.worlds : name => world.image if world.image != null }

  name         = each.value
  keep_locally = false
  force_remove = true
}

# Losing this is losing that world's save. The lifecycle block means removing a world from the map
# fails the plan rather than deleting its world: take it out of the state and remove the volume by
# hand if that is really what is meant.
resource "docker_volume" "other_world" {
  for_each = var.worlds

  name = "ksamp-world-${each.key}"

  lifecycle {
    prevent_destroy = true
  }
}

resource "docker_container" "other_world" {
  # Empty while nothing is deployed, which is also what keeps the image reference below unevaluated
  # until there is an image to refer to.
  for_each = var.server_image == "" ? {} : var.worlds

  name  = "ksamp-${each.key}"
  image = each.value.image != null ? docker_image.other_world[each.key].repo_digest : docker_image.server[0].repo_digest

  restart  = "unless-stopped"
  must_run = true

  # Its own console, reached as `docker attach ksamp-<name>`; see the first server for why.
  stdin_open = true
  tty        = true

  ports {
    internal = 50100
    external = each.value.port
    protocol = "udp"
  }

  volumes {
    volume_name    = docker_volume.other_world[each.key].name
    container_path = "/data"
  }

  # Only what a world says of itself: a setting left out is the server's own default, and the mods
  # of a world that names none are those of whoever brings it one.
  env = concat(
    [
      "KSAMP_NAME=${coalesce(each.value.server_name, each.key)}",
      "KSAMP_MAX_PLAYERS=${coalesce(each.value.max_players, var.max_players)}",
      "KSAMP_SNAPSHOT_EVERY=${coalesce(each.value.snapshot_every_seconds, var.snapshot_every_seconds)}",
    ],
    each.value.mods == null ? [] : ["KSAMP_MODS=${each.value.mods}"],
    each.value.ksa_build == null ? [] : ["KSAMP_KSA_BUILD=${each.value.ksa_build}"],
    each.value.empty_world == null ? [] : ["KSAMP_EMPTY_WORLD=${each.value.empty_world}"],
  )

  memory = coalesce(each.value.memory_mb, var.memory_mb)

  stop_timeout = 20

  # The host's journal, for the reason the first server's is: a release replaces these containers too.
  log_driver = "journald"
  log_opts = {
    tag = "ksamp-${each.key}"
  }
}
