terraform {
  required_version = ">= 1.6"

  required_providers {
    cloudflare = {
      source  = "cloudflare/cloudflare"
      version = "~> 5.22"
    }
  }

  # The same R2 bucket the zone's other records are kept in, under a key of this project's own.
  # Nothing is shared between the two but the bucket: ksarmory/dns.tfstate owns the site and the
  # machine's own names, and this owns one record that happens to live in the same zone.
  backend "s3" {
    key    = "ksamultiplayer/dns.tfstate"
    region = "auto"

    use_lockfile = true

    skip_credentials_validation = true
    skip_metadata_api_check     = true
    skip_region_validation      = true
    skip_requesting_account_id  = true
    skip_s3_checksum            = true
  }
}

provider "cloudflare" {
  api_token = var.cloudflare_api_token
}
