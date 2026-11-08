#!/usr/bin/env bash
#
# The dedicated server as one self-contained executable: copy it to a machine and run it, nothing
# else to install. Needs no KSA assemblies, and no game on the machine that runs it.
#
#     ./tools/publish-server.sh                # linux-x64
#     ./tools/publish-server.sh linux-arm64    # a Raspberry Pi, an ARM VPS
#     ./tools/publish-server.sh win-x64        # a Windows box, and what two-games.sh runs
#
# Out: dist/ksamp-server-<rid>/ksamp-server, or ksamp-server.exe on Windows. The systemd unit goes
# beside a Linux build only: it is what starts one on a server, and means nothing to Windows.
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
EXE="ksamp-server"
case "$RID" in
    win-*) EXE="ksamp-server.exe" ;;
    *) cp "$HERE/deploy/ksamp-server.service" "$OUT/" ;;
esac
echo "published: $OUT/$EXE"
