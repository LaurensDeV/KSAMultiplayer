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
mkdir -p "$HERE/dist"
ZIP="$HERE/dist/KSAMultiplayer-$VERSION.zip"
rm -f "$ZIP"
(cd "$STAGE" && python3 -c "import shutil,sys; shutil.make_archive(sys.argv[1], 'zip', '.', 'KSAMultiplayer')" "${ZIP%.zip}")
echo "packaged: $ZIP"
