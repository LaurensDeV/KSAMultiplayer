terraform {
  required_version = ">= 1.9"

  required_providers {
    docker = {
      source  = "kreuzwerker/docker"
      version = "~> 3.0"
    }
  }

  # Same bucket as the DNS layer, different key. That layer knows the name; this one knows the
  # machine, and shares no resource with anything else running on it.
  backend "s3" {
    key    = "ksamultiplayer/server.tfstate"
    region = "auto"

    skip_credentials_validation = true
    skip_metadata_api_check     = true
    skip_region_validation      = true
    skip_requesting_account_id  = true
    skip_s3_checksum            = true
    use_path_style              = true
  }
}

# SSH is the transport, not the deploy mechanism: the provider tunnels the Docker Engine API over it.
provider "docker" {
  host = "ssh://${var.ssh_user}@${var.vps_host}"

  ssh_opts = [
    "-o", "StrictHostKeyChecking=accept-new",
    "-o", "IdentitiesOnly=yes",
    "-i", pathexpand(var.ssh_private_key),
  ]
}
