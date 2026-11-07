#!/usr/bin/env bash
#
# The mod as a player installs it: a KSAMultiplayer folder with the DLL and its manifest, zipped,
# into dist/. Release, with no symbols and no game DLL in it.
#
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=env.sh
source "$HERE/tools/env.sh"
dotnet build "$HERE/src/Mod/KSAMultiplayer.csproj" -c Release --nologo
BIN="$HERE/src/Mod/bin/Release/net10.0"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$HERE/src/Mod/KSAMultiplayer.csproj")"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/KSAMultiplayer"
cp "$BIN/KSAMultiplayer.dll" "$BIN/mod.toml" "$STAGE/KSAMultiplayer/"
# Discord's library and its notices, which the build put beside the mod when it found the SDK.
for f in discord_partner_sdk.dll libdiscord_partner_sdk.so discord-license-notices.txt; do
    if [[ -f "$BIN/$f" ]]; then cp "$BIN/$f" "$STAGE/KSAMultiplayer/"; fi
done
if [[ ! -f "$BIN/discord_partner_sdk.dll" && ! -f "$BIN/libdiscord_partner_sdk.so" ]]; then
    echo "note: built without Discord's Social SDK (set DISCORD_SDK_DIR) -- the mod has no Discord" >&2
fi
mkdir -p "$HERE/dist"
ZIP="$HERE/dist/KSAMultiplayer-$VERSION.zip"
rm -f "$ZIP"
(cd "$STAGE" && python3 -c "import shutil,sys; shutil.make_archive(sys.argv[1], 'zip', '.', 'KSAMultiplayer')" "${ZIP%.zip}")
echo "packaged: $ZIP"
