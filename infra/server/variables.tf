variable "vps_host" {
  description = "Address of the machine this runs on. The DNS layer points the name at it."
  type        = string
  default     = "45.133.178.13"
}

variable "ssh_user" {
  description = "User the Docker provider reaches the daemon as."
  type        = string
  default     = "root"
}

variable "ssh_private_key" {
  description = "Path to the private key authorised on that machine."
  type        = string
  default     = "~/.ssh/id_rsa"
}

# Immutable, so the machine runs what was built rather than whatever the tag means today. The
# Server image workflow prints the line to paste.
variable "server_image" {
  description = "Image by digest, ghcr.io/<owner>/ksamp-server@sha256:... Empty means not deployed."
  type        = string
  default     = ""
}

variable "port" {
  description = "UDP port on the machine. The mod's default, so players need only the hostname."
  type        = number
  default     = 50100
}

# Free text, not an address: the panel shows this where a player might expect what they typed, so a
# name that is almost the hostname reads as a broken one. Either match the hostname exactly or do
# not look like it at all.
variable "server_name" {
  description = "What the server calls itself in the mod's panel and its player list."
  type        = string
  default     = "Multiplayer test server"
}

variable "max_players" {
  description = "How many players may be in the world at once."
  type        = number
  default     = 16
}

variable "snapshot_every_seconds" {
  description = "Seconds between asking a player for a fresh save, so someone joining starts near now."
  type        = number
  default     = 300
}

variable "memory_mb" {
  description = "Memory ceiling for the container, MB."
  type        = number
  default     = 512
}

# Every other world on this machine, by the name it answers to.
#
# One address carries one UDP port once, so each needs a port of its own; the DNS layer's `worlds`
# map must agree with this one, since that is where the name and its SRV record come from. Anything
# left out is the same as the first server's.
#
# Each world is its own container and its own volume, so one of them filling its memory, wedging or
# being restarted leaves the others alone. They share the machine, and the release unless a world
# names an `image` of its own: that world runs that build until the entry is changed, whatever is
# released meanwhile, which is how a world is kept for a build that is not a release -- a branch
# that speaks a newer protocol, say -- or held back on an old one.
#
# `mods` is what the server turns a player away for not having, as the `mods` setting is: ids
# separated by commas, each held to a version after an @ where one is given. Left out, the world
# wants whatever the first player to bring it a world had enabled.
#
# `advertise` lists the world in the public directories, which is what puts it in a game's server
# list, and `advertise_as` is the name it is listed under: the machine's own, since a directory
# otherwise lists the address it saw the announcement come from.
variable "worlds" {
  description = "Other servers on this machine: the name each answers to, its UDP port, and what it does not share."
  type = map(object({
    port                   = number
    image                  = optional(string)
    mods                   = optional(string)
    ksa_build              = optional(string)
    empty_world            = optional(string)
    advertise              = optional(bool)
    advertise_as           = optional(string)
    server_name            = optional(string)
    id                     = optional(string)
    max_players            = optional(number)
    snapshot_every_seconds = optional(number)
    memory_mb              = optional(number)
  }))
  default = {}

  validation {
    condition     = alltrue([for w in var.worlds : w.port > 0 && w.port < 65536])
    error_message = "Every world's port must be between 1 and 65535."
  }

  # Two worlds on one port is one world that will not start, and the one that loses is whichever
  # Docker gets to second.
  validation {
    condition     = length(distinct([for w in var.worlds : w.port])) == length(var.worlds)
    error_message = "Two worlds share a port; each needs one of its own."
  }

  validation {
    condition     = alltrue([for w in var.worlds : w.port != var.port])
    error_message = "A world is on ${var.port}, which is the first server's port."
  }
}

# ------------------------------------------------------------------ the directory

# Immutable, like the server's, so the machine runs what was built. The Directory image workflow
# prints the line to paste.
variable "directory_image" {
  description = "Image by digest, ghcr.io/<owner>/ksamp-directory@sha256:... Empty means not deployed."
  type        = string
  default     = ""
}

# The first server in the public directories, and the name it is listed under: the domain itself,
# which is all a player types. A directory lists a server at the name it says and only once it
# has answered there, so a name that does not resolve yet is a server on no list.
variable "advertise" {
  description = "Whether the first server lists itself in the public directories."
  type        = bool
  default     = true
}

variable "advertise_as" {
  description = "The hostname the first server is listed under. Empty to be listed at the address a directory sees."
  type        = string
  default     = "ksamp.gg"
}

# What a mod manager knows the first server's mods by, which has to outlast its name and its
# address: it has had two addresses already.
variable "server_id" {
  description = "The first server's id setting. Empty to be known by address."
  type        = string
  default     = "mp"
}

# Shown on a row of the mod's server list, so a player who does not like what a directory carries
# knows which one to take out of their own list.
variable "directory_name" {
  description = "What the directory calls itself in a listing."
  type        = string
  default     = "ksamp.gg"
}

# This machine's Caddy belongs to the KSArmory repository and nothing here can edit its config.
# Named: the container joins this network and Caddy reaches it as ksamp-directory:8080, which needs
# the network to be attachable. Empty: the container publishes directory_port on the loopback and
# Caddy reaches it at the bridge gateway instead.
variable "directory_network" {
  description = "An existing Docker network for Caddy to reach the directory by name on. Empty to publish a port instead."
  type        = string
  default     = "ksarmory"
}

# On the loopback only, because the firewall allows 22, 80 and 443 and a published port would go
# round it through Docker's own DNAT.
variable "directory_port" {
  description = "Host port on 127.0.0.1, when directory_network is empty."
  type        = number
  default     = 8090
}

# A header is the sender's to choose, so this is believed only from a hop that cannot be a stranger.
# Caddy is the one hop in front of this; the defaults are the private ranges Docker puts it on.
variable "directory_trust_proxy" {
  description = "Networks whose X-Forwarded-For the directory believes, comma separated."
  type        = string
  default     = "127.0.0.0/8,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16"
}

variable "directory_memory_mb" {
  description = "Memory ceiling for the directory container, MB. Kilobytes of state and one socket."
  type        = number
  default     = 128
}
