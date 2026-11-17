#!/usr/bin/env python3
"""Which release the live servers should run: the newest one that speaks the protocol of the newest
release a player can install from the content index.

A release that changes Wire.Version refuses every game on the one before it, and most players install
through a mod manager reading the content index, which can take hours to list a release. Put on the
servers before then, it refuses everybody with nothing for them to update to. A release that keeps
the protocol refuses nobody, so it goes out at once.

    tools/deploy-pick.py v0.23.1 v0.23.0 v0.22.0 ...   # the published releases, any order
    tools/deploy-pick.py --index index.json v0.24.0 v0.23.0

Prints the tag to deploy on the last line of stdout, or nothing when no listed release is among them;
what it weighed goes to stderr. Each release's protocol is read from src/Protocol/Wire.cs at its tag,
so this runs in a clone that has the tags.
"""

import argparse
import json
import re
import subprocess
import sys
import urllib.request

INDEX = "https://ksamodding.github.io/content-index-releases/v1/index.json"
MOD = "KSAMultiplayer"


def semver(v: str) -> tuple[int, ...]:
    return tuple(int(p) for p in re.findall(r"\d+", v.lstrip("v").split("-")[0]))


def protocol(tag: str) -> int | None:
    try:
        wire = subprocess.run(["git", "show", f"{tag}:src/Protocol/Wire.cs"], capture_output=True, text=True, check=True).stdout
    except subprocess.CalledProcessError:
        return None
    found = re.search(r"public const ushort Version = (\d+);", wire)
    return int(found.group(1)) if found else None


def listed(source: str) -> set[str]:
    if source.startswith("http"):
        with urllib.request.urlopen(urllib.request.Request(source, headers={"User-Agent": "ksamp-deploy"}), timeout=60) as r:
            index = json.load(r)
    else:
        with open(source) as f:
            index = json.load(f)
    for listing in index.get("listings", []):
        if listing.get("id") == MOD:
            return {r["version"] for r in listing.get("releases", []) if r.get("version")}
    return set()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--index", default=INDEX, help="the content index, a URL or a file")
    ap.add_argument("releases", nargs="+", help="the published release tags")
    a = ap.parse_args()

    say = lambda s: print(s, file=sys.stderr)
    releases = sorted({t for t in a.releases if re.fullmatch(r"v\d+\.\d+\.\d+", t)}, key=semver, reverse=True)
    on_index = listed(a.index)
    installable = [t for t in releases if t.lstrip("v") in on_index]
    if not installable:
        say(f"no published release of {MOD} is in the content index, so nothing is chosen")
        return 0
    newest_listed = installable[0]
    speaks = protocol(newest_listed)
    if speaks is None:
        say(f"could not read the protocol of {newest_listed}, so nothing is chosen")
        return 0
    say(f"the newest release in the content index is {newest_listed}, speaking protocol {speaks}")

    for tag in releases:
        if semver(tag) < semver(newest_listed):
            break
        p = protocol(tag)
        if p == speaks:
            say(f"{tag} speaks protocol {p} too" if tag != newest_listed else f"{tag} is the one to run")
            print(tag)
            return 0
        say(f"{tag} speaks protocol {p}: waiting for the content index to list it")
    print(newest_listed)
    return 0


if __name__ == "__main__":
    sys.exit(main())
