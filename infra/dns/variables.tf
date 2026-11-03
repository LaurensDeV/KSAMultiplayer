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
