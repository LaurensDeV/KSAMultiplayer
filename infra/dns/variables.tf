variable "cloudflare_api_token" {
  description = "Cloudflare token with Zone:Read and DNS:Edit on the zone below."
  type        = string
  sensitive   = true
}

variable "zone_id" {
  description = "Cloudflare zone ID, shown on the domain's overview page."
  type        = string
}

variable "domain" {
  description = "Apex domain this record lives under."
  type        = string
  default     = "ksarmory.com"
}

variable "host" {
  description = "The name under that domain."
  type        = string
  default     = "mp"
}

# Given rather than read from the machine's own state, as the zone's other records are: the two
# layers share a bucket and nothing else. The address is written down twice, which is the cost of
# not coupling them.
variable "vps_ipv4" {
  description = "Public IPv4 of the machine running the server."
  type        = string
}

variable "ttl" {
  description = "Record TTL in seconds."
  type        = number
  default     = 300
}

# Only for the record's comment, so whoever reads the zone knows which port is meant.
variable "port" {
  description = "UDP port the server answers on."
  type        = number
  default     = 50100
}

# Every other world on the same machine, by the name it answers to.
#
# One address carries one UDP port once, so a second world needs a port of its own -- and a port is
# the one thing an address record cannot carry. Hence an SRV record beside each name: a client that
# looks it up is told the port, and one that does not falls back to this name and the mod's default,
# which is the first world. A world on the default port needs no entry here; that is `host` above.
variable "worlds" {
  description = "Other servers on this machine: the name each answers to, and the UDP port it is on."
  type = map(object({
    port = number
  }))
  default = {}

  validation {
    condition     = alltrue([for w in var.worlds : w.port > 0 && w.port < 65536])
    error_message = "Every world's port must be between 1 and 65535."
  }

  validation {
    condition     = !contains(keys(var.worlds), var.host)
    error_message = "A world cannot be named ${var.host}: that is the first server, declared by host and port."
  }
}
