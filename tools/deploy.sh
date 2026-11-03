#!/usr/bin/env bash
#
# Builds the mod and installs it where StarMap looks for mods, as a developer's install: with a
# `developer` marker beside the DLL, which starts the bridge (src/Mod/Bridge.cs) that tools/ksa-mcp
# drives. tools/package.sh stages from the build output instead, so a release never carries it.
#
#   ./tools/deploy.sh              # Release, into the user mods folder
#   ./tools/deploy.sh Debug        # Debug
#
# KSA_MODS_DIR overrides where it installs; KSA_USER_DIR overrides the profile it is found from.
#
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIG="${1:-Release}"
# shellcheck source=env.sh
source "$HERE/tools/env.sh"
# shellcheck source=ksa-user-dir.sh
source "$HERE/tools/ksa-user-dir.sh"

if [[ -n "${KSA_MODS_DIR:-}" ]]; then
    MODS_DIR="$KSA_MODS_DIR"
elif USER_DIR="$(ksa_user_dir)"; then
    MODS_DIR="$USER_DIR/mods"
else
    echo "error: could not locate the KSA user folder on this machine." >&2
    echo "       set KSA_MODS_DIR to <user dir>/Kitten Space Agency/mods and retry" >&2
    exit 1
fi

TARGET="$MODS_DIR/KSAMultiplayer"
echo "building ($CONFIG)..."
dotnet build "$HERE/src/Mod/KSAMultiplayer.csproj" -c "$CONFIG" --nologo
OUT="$HERE/src/Mod/bin/$CONFIG/net10.0"

mkdir -p "$TARGET"

# KSA holds the assembly open while it runs, so the copy fails with a bare "Permission denied"
# that reads as a filesystem problem rather than "close the game".
if ! cp "$OUT/KSAMultiplayer.dll" "$TARGET/" 2>/dev/null; then
    echo "error: could not write KSAMultiplayer.dll to the mods folder." >&2
    if tasklist.exe 2>/dev/null | grep -qiE 'starmap|kittenspaceagency'; then
        echo "       KSA is still running and has the file locked -- close the game and retry." >&2
    else
        echo "       check permissions on $TARGET" >&2
    fi
    exit 1
fi
cp "$OUT/mod.toml" "$TARGET/"
[[ -f "$OUT/KSAMultiplayer.pdb" ]] && cp "$OUT/KSAMultiplayer.pdb" "$TARGET/"
: > "$TARGET/developer"
echo "installed to $TARGET"

# KSA discovers mods through manifest.toml and StarMap walks the same list for code mods. Dropping
# the folder in is not enough: without an entry here nothing loads.
MANIFEST="$(dirname "$MODS_DIR")/manifest.toml"
if [[ ! -f "$MANIFEST" ]]; then
    echo "warning: no manifest at $MANIFEST -- register the mod by hand" >&2
elif grep -q '^[[:space:]]*id[[:space:]]*=[[:space:]]*"KSAMultiplayer"' "$MANIFEST"; then
    echo "already registered in $(basename "$MANIFEST")"
else
    cp "$MANIFEST" "$MANIFEST.bak"
    printf '\n[[mods]]\nid = "KSAMultiplayer"\nenabled = true\n' >> "$MANIFEST"
    echo "registered in $(basename "$MANIFEST") (backup: $(basename "$MANIFEST").bak)"
fi
