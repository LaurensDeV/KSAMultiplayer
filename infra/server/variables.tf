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
  default     = "mp.ksarmory.com"
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
