#!/usr/bin/env bash
#
# The game with this mod and nothing else, in a profile of its own.
#
#     ./tools/play.sh              # deploy, refresh the profile, launch
#     ./tools/play.sh --no-build   # launch what is already there
#     ./tools/play.sh --dir        # print the profile's folder
#
# StarMap gives KSA a profile of its own with -InstancePath, which is what a Borea instance is: its
# own mods, saves and log. Another mod in the player's own profile is another mod in the world they
# hand the server, and every other player is then refused for not having it -- so a session worth
# trusting runs from here rather than from a profile with everything enabled.
#
# The player's own settings and saves are copied in, so it looks and plays like their game.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=ksa-user-dir.sh
source "$HERE/tools/ksa-user-dir.sh"
USER_DIR="$(ksa_user_dir)"
PLAY_DIR="${KSAMP_PLAY_DIR:-${USER_DIR%%/Documents/*}/KSAMultiplayerPlay}"

if [[ "${1:-}" == "--dir" ]]; then
    echo "$PLAY_DIR"
    exit 0
fi

if [[ "${1:-}" != "--no-build" ]]; then
    "$HERE/tools/deploy.sh" >/dev/null
    echo "deployed"
fi

STARMAP_EXE="$(find "${USER_DIR%%/Documents/*}" -maxdepth 2 -iname 'StarMap.exe' 2>/dev/null | head -1 || true)"
[[ -n "${STARMAP_DIR:-}" ]] && STARMAP_EXE="$STARMAP_DIR/StarMap.exe"
if [[ ! -f "$STARMAP_EXE" ]]; then
    echo "error: StarMap.exe not found; set STARMAP_DIR" >&2
    exit 1
fi

mkdir -p "$PLAY_DIR/mods" "$PLAY_DIR/saves" "$PLAY_DIR/vehicles" "$PLAY_DIR/Logs"

# The mod, fresh every launch, so this profile is never a build behind.
rm -rf "$PLAY_DIR/mods/KSAMultiplayer"
cp -r "$USER_DIR/mods/KSAMultiplayer" "$PLAY_DIR/mods/"

# Core and this mod: nothing else, which is the point.
printf '[[mods]]\nid = "Core"\nenabled = true\n\n[[mods]]\nid = "KSAMultiplayer"\nenabled = true\n' > "$PLAY_DIR/manifest.toml"

# The player's own settings, untouched: this is their game, not a test rig.
[[ -f "$USER_DIR/settings.toml" && ! -f "$PLAY_DIR/settings.toml" ]] && cp "$USER_DIR/settings.toml" "$PLAY_DIR/"

# Saves and craft, copied once. Anything of theirs that needs a mod this profile lacks simply will
# not load, which is the same answer the server would give.
if [[ -z "$(ls -A "$PLAY_DIR/saves" 2>/dev/null)" ]]; then
    echo "copying saves and vehicles (once)..."
    cp -r "$USER_DIR/saves/." "$PLAY_DIR/saves/" 2>/dev/null || true
    cp -r "$USER_DIR/vehicles/." "$PLAY_DIR/vehicles/" 2>/dev/null || true
fi

echo "profile: $PLAY_DIR"
echo "log:     $PLAY_DIR/Logs/KSAMultiplayer.log"
cd "$(dirname "$STARMAP_EXE")"
exec ./StarMap.exe -InstancePath "$(wslpath -w "$PLAY_DIR")"
