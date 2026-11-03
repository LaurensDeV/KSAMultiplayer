#!/usr/bin/env bash
#
# Two copies of the game on this machine, so one person is both players in a shared world.
#
#     ./tools/two-games.sh            # deploy, make or refresh both profiles, launch both
#     ./tools/two-games.sh --test     # ...then run a server, join A and B, and say what each sees
#     ./tools/two-games.sh --dir A    # print a profile's folder
#     ./tools/two-games.sh --stop     # close both games and the server
#
# StarMap gives KSA a profile of its own with -InstancePath, so each copy has its own log, saves,
# mods and bridge. Then drive either through its bridge:
#
#     KSA_USER_DIR="$(./tools/two-games.sh --dir A)" python3 tools/ksa-mcp/server.py cli status '{}'
#
# Both copies share one graphics card, and at the player's settings two of them fill its memory;
# the profiles are made with the graphics turned well down and vsync off.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=ksa-user-dir.sh
source "$HERE/tools/ksa-user-dir.sh"
# The server is net10.0 and a distro dotnet 8 cannot run it.
# shellcheck source=env.sh
source "$HERE/tools/env.sh"
USER_DIR="$(ksa_user_dir)"
# Beside the player's own Windows home, which is where a path for -InstancePath has to be.
TEST_ROOT="${KSAMP_TWO_GAMES:-${USER_DIR%%/Documents/*}/KSAMultiplayerTest}"
# A save clear of every other mod's parts: one a profile with only this mod enabled can load.
SAVE="${KSAMP_TWO_GAMES_SAVE:-GOLF}"
PORT="${KSAMP_TEST_PORT:-50100}"
# Where the server runs: "linux" is the real article, "windows" a published .exe beside the profiles.
# Auto unless KSAMP_SERVER_ON says. See the server section below for why it is ever not linux.
SERVER_ON="${KSAMP_SERVER_ON:-auto}"
DATA="$HERE/.two-games-data"

win() { wslpath -w "$1"; }

if [[ "${1:-}" == "--dir" ]]; then
    echo "$TEST_ROOT/${2:?A or B}"
    exit 0
fi

if [[ "${1:-}" == "--stop" ]]; then
    taskkill.exe /F /IM StarMap.exe >/dev/null 2>&1 || true
    taskkill.exe /F /IM ksamp-server.exe >/dev/null 2>&1 || true
    pkill -x ksamp-server 2>/dev/null || true
    echo "stopped"
    exit 0
fi

STARMAP_EXE="$(find "${USER_DIR%%/Documents/*}" -maxdepth 2 -iname 'StarMap.exe' 2>/dev/null | head -1 || true)"
[[ -n "${STARMAP_DIR:-}" ]] && STARMAP_EXE="$STARMAP_DIR/StarMap.exe"
if [[ ! -f "$STARMAP_EXE" ]]; then
    echo "error: StarMap.exe not found; set STARMAP_DIR" >&2
    exit 1
fi
if tasklist.exe 2>/dev/null | grep -qi starmap; then
    echo "error: a game is running; close it first (./tools/two-games.sh --stop)" >&2
    exit 1
fi

"$HERE/tools/deploy.sh" >/dev/null
echo "deployed"

for name in A B; do
    dir="$TEST_ROOT/$name"
    mkdir -p "$dir/mods" "$dir/saves" "$dir/Logs"
    rm -rf "$dir/mods/KSAMultiplayer"
    cp -r "$USER_DIR/mods/KSAMultiplayer" "$dir/mods/"
    [[ -d "$USER_DIR/saves/$SAVE" ]] && cp -r "$USER_DIR/saves/$SAVE" "$dir/saves/"
    # Only this mod: the server refuses a player without a mod the world's giver had enabled, and a
    # save holding another mod's parts would not load in a profile without it.
    printf '[[mods]]\nid = "Core"\nenabled = true\n\n[[mods]]\nid = "KSAMultiplayer"\nenabled = true\n' > "$dir/manifest.toml"
    python3 - "$USER_DIR/settings.toml" "$dir/settings.toml" "$name" <<'PY'
import re, sys
source, target, name = sys.argv[1:]
s = open(source).read()
light = {
    "width": "800", "height": "500", "posX": "0" if name == "A" else "820", "posY": "40",
    "vsync": "false", "fpslimit": "40", "antialiasing": '"None"',
    "terrainShadowMapSize": "1024", "cascadedShadowMapSize": "1024", "LightAtlasShadowMapSize": "1024",
    "cascadedShadowMapFilter": '"Pcf"', "lightSystemShadowSlots": '"Low"', "cloudShadows": '"Off"',
    "clouds": "false", "cloudUpscale": '"Low"', "oceanSimQuality": '"Low"', "ocean": "false",
    "ambientOcclusion": "false", "terrainGodrays": "false", "planetaryRings": "false",
    "planetaryRingMeshes": "false", "planetaryRingsVolumetricDust": "false",
    "groundClutterShadowCasting": "false", "textureStreamingBudgetMb": "1536",
    "celestialTextureCap": "2048", "vesselTextureCap": "1024", "kittenFurQuality": '"Low"',
    "explosionQuality": '"Low"', "exhaustUpscale": '"Low"', "reflections": "false",
    "terrainTessellation": "false", "stars": "false", "lensFlare": "false", "globalBloom": "false",
    "thresholdBloom": "false", "plumeTrails": "false", "plumeTrailShadows": "false",
    "particleCountQuality": '"Low"', "vesselDeformation": "false", "terrainShadows": "false",
    "skyboxShadows": "false", "milkyWayQuality": '"Low"', "vesselCascadeShadows": "false",
    "thinFilmInterference": "false", "specularAntiAliasing": "false", "showDistantGlints": "false",
    "crewPortraitCameras": "false", "masterVolume": "0",
}
for key, value in light.items():
    s = re.sub(rf"(?m)^{key} = .*$", f"{key} = {value}", s, count=1)
open(target, "w").write(s)
PY
done
echo "profiles in $TEST_ROOT"

cd "$(dirname "$STARMAP_EXE")"
for name in A B; do
    ./StarMap.exe -InstancePath "$(win "$TEST_ROOT/$name")" >/dev/null 2>&1 &
    disown
    echo "launched $name"
    # One at a time: two loading at once fight over the disk and the card.
    sleep 25
done

# Windows gives the window in front the processor and throttles the one behind it, and both copies
# have to keep a clock.
# shellcheck disable=SC2016  # the dollars are PowerShell's
powershell.exe -NoProfile -Command '
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class Throttle {
  [StructLayout(LayoutKind.Sequential)] public struct State { public uint Version; public uint ControlMask; public uint StateMask; }
  [DllImport("kernel32.dll")] public static extern bool SetProcessInformation(IntPtr h, int kind, ref State s, int size);
  public static bool Off(IntPtr h) { var s = new State { Version = 1, ControlMask = 1, StateMask = 0 }; return SetProcessInformation(h, 4, ref s, Marshal.SizeOf(s)); }
}
"@
Get-Process StarMap | ForEach-Object { $_.PriorityClass = "High"; [void][Throttle]::Off($_.Handle) }' >/dev/null 2>&1 || true
echo "both at high priority, unthrottled"

[[ "${1:-}" == "--test" ]] || exit 0

# --- a server and two players ------------------------------------------------

call() {
    local name=$1
    shift
    KSA_USER_DIR="$TEST_ROOT/$name" timeout 150 python3 "$HERE/tools/ksa-mcp/server.py" cli "$@"
}

# Where the server can run and still be reached. WSL2's default NAT mode gives Linux a network stack
# of its own, and a Windows process cannot reach a UDP socket bound inside it on 127.0.0.1: a server in
# WSL is then invisible to the games. Mirrored mode shares the Windows stack and localhost works both
# ways, so the Linux server -- the one this repository is actually for -- serves the games as it would
# a real player. Without it the server is published for Windows instead, which tests the same code over
# the same protocol but not the Linux build.
if [[ "$SERVER_ON" == "auto" ]]; then
    if [[ "$(wslinfo --networking-mode 2>/dev/null)" == "mirrored" ]]; then SERVER_ON="linux"; else SERVER_ON="windows"; fi
fi

rm -rf "$DATA"
mkdir -p "$DATA"
if [[ "$SERVER_ON" == "linux" ]]; then
    "$HERE/tools/build.sh" >/dev/null
    echo "starting the Linux server on port $PORT"
    dotnet "$HERE/src/Server/bin/Release/net10.0/ksamp-server.dll" --port "$PORT" --data "$DATA" --name "two-games" \
        > "$HERE/.two-games-server.log" 2>&1 &
else
    echo "note: WSL is in $(wslinfo --networking-mode 2>/dev/null || echo nat) mode, where the games cannot reach a server inside WSL."
    echo "      Running a Windows build instead. For the Linux server, put networkingMode=mirrored in"
    echo "      %USERPROFILE%\\.wslconfig under [wsl2], run 'wsl --shutdown', and try again."
    "$HERE/tools/publish-server.sh" win-x64 >/dev/null
    SERVER_EXE="$TEST_ROOT/ksamp-server.exe"
    cp "$HERE/dist/ksamp-server-win-x64/ksamp-server.exe" "$SERVER_EXE"
    echo "starting the Windows server on port $PORT"
    "$SERVER_EXE" --port "$PORT" --data "$(win "$DATA")" --name "two-games" \
        > "$HERE/.two-games-server.log" 2>&1 &
fi
SERVER_PID=$!
disown
sleep 3

for name in A B; do
    echo "waiting for $name's bridge..."
    for _ in $(seq 1 60); do
        call "$name" status '{}' 2>/dev/null | grep -q '"ksa"' && break
        sleep 5
    done
    call "$name" load "{\"save\":\"$SAVE\"}"
done

# A first, which has no world to wait for: the server takes A's and hands it to B.
call A mp "{\"connect\":\"127.0.0.1:$PORT\",\"name\":\"Alpha\"}"
sleep 20
call B mp "{\"connect\":\"127.0.0.1:$PORT\",\"name\":\"Bravo\"}"
echo "both joining; the server log is $HERE/.two-games-server.log (pid $SERVER_PID)"
