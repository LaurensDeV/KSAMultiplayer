#!/usr/bin/env bash
#
# Builds the mod, which needs KSA's assemblies (found as Directory.Build.props finds
# them, KSA_DLL_DIR first), and the server, which needs nothing of KSA.
#
#     ./tools/build.sh            # Release
#     ./tools/build.sh Debug
#
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=env.sh
source "$HERE/tools/env.sh"
CONFIG="${1:-Release}"
case "$CONFIG" in
    Debug|Release) ;;
    *) echo "error: '$CONFIG' is not a build configuration (expected Debug or Release)" >&2; exit 2 ;;
esac
dotnet build "$HERE/src/Server/KSAMultiplayer.Server.csproj" -c "$CONFIG" --nologo
dotnet build "$HERE/src/Mod/KSAMultiplayer.csproj" -c "$CONFIG" --nologo
