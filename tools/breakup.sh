#!/usr/bin/env bash
#
# Two games, one rocket and a deliberate breakup: the cascade that filled mp.ksarmory.com with
# exploding debris, small enough to watch on one machine and counted, so a fix can be seen to work
# rather than argued about.
#
#     ./tools/two-games.sh --test     # a server and two games, both in one world
#     ./tools/breakup.sh              # fly A's biggest rocket apart; count what it did to both
#     ./tools/breakup.sh --count      # count the logs again without flying anything
#
# The number that matters is the last one: **craft a game described that it had itself been told
# about**. A game is allowed to describe its own craft and nobody else's, so a name that appears
# both in `built <name>` (it was built here from somebody else's word) and in `sent the server how
# <name> is built` (this game told everybody how it is built) is this game claiming another
# player's wreckage. That is the engine of the cascade, and in a healthy run it is zero. The one
# honest reason for it to be above zero is a handover -- a game does take over craft whose owner has
# gone -- so both games stay joined for the whole run and nobody leaves.
#
# The rest is how bad it got: builds against distinct names, rebuilds, destructions, the origins KSA
# refused and by how much, and the ceilings KSA itself hit. A reproduction, not a test -- KSA's
# physics is not deterministic and two runs differ -- so read orders of magnitude, not digits.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=ksa-user-dir.sh
source "$HERE/tools/ksa-user-dir.sh"
USER_DIR="$(ksa_user_dir)"
# The same two profiles two-games.sh makes, since this drives the rig it sets up.
TEST_ROOT="${KSAMP_TWO_GAMES:-${USER_DIR%%/Documents/*}/KSAMultiplayerTest}"
WATCH_SECONDS="${KSAMP_BREAKUP_SECONDS:-75}"

call() {
    local name=$1
    shift
    KSA_USER_DIR="$TEST_ROOT/$name" timeout 150 python3 "$HERE/tools/ksa-mcp/server.py" cli "$@"
}

if [[ "${1:-}" != "--count" ]]; then
    for name in A B; do
        [[ -S "$TEST_ROOT/$name/mp-bridge.sock" || -e "$TEST_ROOT/$name/mp-bridge" ]] ||
            [[ -f "$TEST_ROOT/$name/Logs/KSAMultiplayer.log" ]] ||
            { echo "no $name profile in $TEST_ROOT; run ./tools/two-games.sh --test first" >&2; exit 1; }
    done

    echo "A flies a rocket apart; B only watches."
    # Whatever A has standing with the most parts: the one worth breaking, and its own rather than a
    # puppet, since a game may only fly what it owns.
    flown=$(call A craft '{}' | python3 -I -c '
import json, sys
try:
    craft = json.load(sys.stdin).get("craft", [])
except Exception:
    craft = []
mine = [c for c in craft if not c.get("puppet") and c.get("situation") == "Landed"]
best = max(mine, key=lambda c: c.get("parts") or 0, default=None)
print(best["id"] if best else "")
')
    if [[ -z "$flown" ]]; then
        echo "nothing of A's is standing on the ground; load a save with a rocket on a pad" >&2
        exit 1
    fi
    echo "  A takes $flown"
    call A goto "{\"craft\":\"$flown\"}" >/dev/null
    # Lit and staged over and over. Each stage dropped is a craft of its own and what falls back on
    # the site breaks up, which is the cheapest breakup that wants no hand on the stick.
    call A engine '{"on":true,"throttle":1}' >/dev/null || true
    for _ in $(seq 1 6); do
        call A stage '{}' >/dev/null || true
        sleep 4
    done
    echo "  watching for ${WATCH_SECONDS}s while it comes apart..."
    sleep "$WATCH_SECONDS"
    echo
fi

python3 -I - "$TEST_ROOT" "$HERE/.two-games-server.log" <<'PY'
import glob, os, re, statistics, sys

root, server_log = sys.argv[1], sys.argv[2]
BUILT = re.compile(r' INFO built (.+?), ')
REBUILT = re.compile(r' INFO rebuilding (.+?): ')
DESCRIBED = re.compile(r' INFO sent the server how (.+?) is built')
DESTROYED = re.compile(r" WARN Vehicle '(.+?)' destroyed - (\d+) parts")
REFUSED = re.compile(r' ERROR Attempted to update vehicle (.+?) with origin at time ([\d.,]+) with new origin at time ([\d.,]+)')
CEILING = re.compile(r' WARN (ExplosionSystem: more than|Freed only)')
FAILED = re.compile(r'Update task failed')


def newest_ksa_log(folder):
    logs = [p for p in glob.glob(os.path.join(folder, 'Logs', 'KittenSpaceAgency.*.log'))
            if not p.endswith('.ring')]
    return max(logs, key=os.path.getmtime) if logs else None


def read(path):
    if not path or not os.path.isfile(path):
        return []
    with open(path, encoding='utf-8', errors='replace') as f:
        return f.readlines()


verdict = []
for name in ('A', 'B'):
    folder = os.path.join(root, name)
    mod = read(os.path.join(folder, 'Logs', 'KSAMultiplayer.log'))
    ksa = read(newest_ksa_log(folder))
    if not mod:
        print(f'{name}: no mod log in {folder} -- was the game running?')
        continue

    built = [m.group(1) for line in mod if (m := BUILT.search(line))]
    rebuilt = [m.group(1) for line in mod if (m := REBUILT.search(line))]
    described = [m.group(1) for line in mod if (m := DESCRIBED.search(line))]
    # The whole bug in one set: described here, and built here from somebody else's word.
    claimed = sorted(set(described) & set(built))

    destroyed = [m.group(1) for line in ksa if (m := DESTROYED.search(line))]
    gaps = []
    for line in ksa:
        if (m := REFUSED.search(line)):
            own = float(m.group(2).replace(',', '.'))
            new = float(m.group(3).replace(',', '.'))
            gaps.append(own - new)
    ceilings = sum(1 for line in ksa if CEILING.search(line))
    failed = sum(1 for line in ksa if FAILED.search(line))

    print(f'{name}')
    print(f'  built        {len(built):5d} of {len(set(built)):4d} names'
          f'   rebuilt {len(rebuilt):4d}')
    print(f'  described    {len(described):5d} of {len(set(described)):4d} names')
    print(f'  destroyed    {len(destroyed):5d} of {len(set(destroyed)):4d} craft')
    if gaps:
        print(f'  origins refused {len(gaps):4d}   median {statistics.median(gaps):6.2f} s'
              f'   worst {max(gaps):6.2f} s')
    else:
        print('  origins refused    0')
    print(f"  physics steps failed {failed:3d}   KSA's own ceilings hit {ceilings:3d}")
    print(f'  CLAIMED WHAT IT WAS TOLD  {len(claimed):4d}'
          + (f'   {", ".join(claimed[:4])}{" ..." if len(claimed) > 4 else ""}' if claimed else '   none'))
    verdict.append((name, len(claimed)))

    worst = [(n, built.count(n)) for n in set(built)]
    worst.sort(key=lambda p: -p[1])
    if worst and worst[0][1] > 3:
        print('  most-built names: ' + ', '.join(f'{n} x{c}' for n, c in worst[:3]))
    print()

if os.path.isfile(server_log):
    lines = read(server_log)
    known = [m.group(1) for line in lines if (m := re.search(r'(\d+) craft known', line))]
    print('server')
    print(f'  craft known at the end {known[-1] if known else "?":>6}'
          f'   definitions sent {sum("says how" in l for l in lines):5d}'
          f'   of them repeats {sum("times now" in l for l in lines):5d}')
    print()

if verdict:
    bad = [n for n, c in verdict if c]
    print('either game claiming craft it was told about is the cascade: '
          + (f'{" and ".join(bad)} did' if bad else 'neither did'))
PY
