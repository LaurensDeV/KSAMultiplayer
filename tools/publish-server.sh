#!/usr/bin/env bash
#
# The dedicated server as one self-contained executable: copy it to a Linux box and run it, nothing
# else to install. Needs no KSA assemblies.
#
#     ./tools/publish-server.sh                # linux-x64
#     ./tools/publish-server.sh linux-arm64    # a Raspberry Pi, an ARM VPS
#
# Out: dist/ksamp-server-<rid>/ksamp-server
#
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=env.sh
source "$HERE/tools/env.sh"
RID="${1:-linux-x64}"
OUT="$HERE/dist/ksamp-server-$RID"
rm -rf "$OUT"
dotnet publish "$HERE/src/Server/KSAMultiplayer.Server.csproj" -c Release -r "$RID" --self-contained \
    -p:PublishSingleFile=true -p:DebugType=none -p:EnableCompressionInSingleFile=true --nologo -o "$OUT"
cp "$HERE/deploy/ksamp-server.service" "$OUT/"
echo "published: $OUT/ksamp-server"
