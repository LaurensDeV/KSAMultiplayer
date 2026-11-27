# Operators who look after the servers without being given the machine: `ssh ops@ksamp.gg status`,
# `logs`, `follow` and `restart`, for the KSA server containers alone (ops/ksamp-ops-root).
#
# This machine runs other repositories' services too, and the usual ways in to a container --
# the docker group, the Docker socket, the journal -- are each root on all of it, so an operator is
# given none of them: their key runs one command, which asks root to do one of four things.
#
# The user, its sshd block and its sudo rule are this repository's, written by this and taken away
# by it when ops_keys is empty. Removing this file instead leaves them on the machine: empty the
# map and apply first.

locals {
  # What ksamp-ops-root may name. Never the directory: its environment holds the Discord signing key.
  ops_containers = concat(["ksamp-server"], [for name in sort(keys(var.worlds)) : "ksamp-${name}"])

  ops_install = templatefile("${path.module}/ops/install.sh.tftpl", {
    keys       = keys(var.ops_keys)
    gate       = file("${path.module}/ops/ksamp-ops")
    root       = file("${path.module}/ops/ksamp-ops-root")
    sshd       = file("${path.module}/ops/90-ksamp-ops.conf")
    sudoers    = file("${path.module}/ops/sudoers")
    containers = join("", [for c in local.ops_containers : "${c}\n"])
    names      = join("", [for name, k in var.ops_keys : "${name} ${k.key}\n"])
    # `restrict` takes forwarding, the terminal and ~/.ssh/rc away from the key itself, so that
    # sshd's block is the second thing keeping an operator in and not the only one.
    authorized = join("", [
      for name, k in var.ops_keys :
      "restrict,expiry-time=\"${k.expires}\",command=\"/usr/local/sbin/ksamp-ops\" ${k.key} ${name}\n"
    ])
  })
}

resource "terraform_data" "ops" {
  triggers_replace = [sha256(local.ops_install)]

  connection {
    type        = "ssh"
    host        = var.vps_host
    user        = var.ssh_user
    private_key = sensitive(file(pathexpand(var.ssh_private_key)))
  }

  # A file run by bash, since remote-exec's inline commands go to sh.
  provisioner "file" {
    content     = local.ops_install
    destination = "/root/.ksamp-ops-install.sh"
  }

  provisioner "remote-exec" {
    inline = ["bash /root/.ksamp-ops-install.sh; s=$?; rm -f /root/.ksamp-ops-install.sh; exit $s"]
  }
}
