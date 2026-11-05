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
# The two numbers that matter come from the worlds rather than the logs, because the cascade starts
# before anything is written down:
#
#   * **craft one game holds that the other has never heard of.** Every craft in a shared world
#     belongs to somebody, and a name only one game knows is a craft that game made up: its copy of
#     the other player's rocket came apart differently and shed pieces of its own. Zero in a healthy
#     run. The first run of this measured twenty.
#   * **craft a game neither owns nor treats as another player's.** Orphans: nothing drives them,
#     the owner's sweep does not reach them, and they are in physics and will hit things. Zero in a
#     healthy run; the same run had thirty-five of forty-six.
#
# From the logs, then, how bad it got, and whether the orphans were claimed: a game may describe its
# own craft and nobody else's, so a name in both `built <name>` and `sent the server how <name> is
# built` is this game telling everybody about wreckage it was told about. The one honest reason for
# that is a handover, so both games stay joined for the whole run and nobody leaves.
#
# A reproduction, not a test -- KSA's physics is not deterministic and two runs differ -- so read
# orders of magnitude, not digits.
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
    # The most parts A can take over: the one worth breaking. Not a puppet, since a game may only
    # fly what is not somebody else's, and flying it is what claims it.
    flown=$(call A craft '{}' | python3 -I -c '
import json, sys
try:
    craft = json.load(sys.stdin).get("craft", [])
except Exception:
    craft = []
mine = [c for c in craft if not c.get("puppet") and (c.get("parts") or 0) > 1]
best = max(mine, key=lambda c: c.get("parts") or 0, default=None)
print(best["id"] if best else "")
')
    if [[ -z "$flown" ]]; then
        echo "A has nothing of more than one part to break; load a save with a rocket in it" >&2
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

# Both worlds as each game sees them, while they are still up: the logs cannot show a craft that
# was never written down, and the ones that matter most never are.
WORLDS="$(mktemp -d)"
trap 'rm -rf "$WORLDS"' EXIT
for name in A B; do
    call "$name" craft '{}' > "$WORLDS/$name.json" 2>/dev/null || true
done

python3 -I - "$TEST_ROOT" "$HERE/.two-games-server.log" "$WORLDS" <<'PY'
import glob, json, os, re, statistics, sys

root, server_log, worlds = sys.argv[1], sys.argv[2], sys.argv[3]
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


def world(name):
    path = os.path.join(worlds, name + '.json')
    if not os.path.isfile(path):
        return None
    try:
        with open(path, encoding='utf-8') as f:
            return {c['id']: c for c in json.load(f).get('craft', [])}
    except Exception:
        return None


made_up = {}
seen = {name: world(name) for name in ('A', 'B')}
if seen['A'] is not None and seen['B'] is not None:
    print('the worlds, as each game has them')
    for name, other in (('A', 'B'), ('B', 'A')):
        mine = seen[name]
        owned = sum(1 for c in mine.values() if c.get('owned'))
        puppets = sum(1 for c in mine.values() if c.get('puppet'))
        orphans = [k for k, c in mine.items() if not c.get('owned') and not c.get('puppet')]
        alone = sorted(set(mine) - set(seen[other]))
        made_up[name] = len(alone)
        print(f'  {name}  {len(mine):4d} craft -- {owned:4d} its own, {puppets:4d} other players\','
              f' {len(orphans):4d} neither')
        print(f'      MADE UP, the other game has never heard of them  {len(alone):4d}'
              + (f'   {", ".join(alone[:6])}{" ..." if len(alone) > 6 else ""}' if alone else '   none'))
    print()
else:
    print('(no live worlds read -- the games are not up, so only the logs below)\n')

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

if made_up:
    bad = [n for n, c in made_up.items() if c]
    print('craft a game made up is where the cascade starts: '
          + (f'{" and ".join(bad)} did, ' + ', '.join(f'{n} by {made_up[n]}' for n in bad)
             if bad else 'neither did'))
if verdict:
    bad = [n for n, c in verdict if c]
    print('claiming craft it was told about is where it spreads: '
          + (f'{" and ".join(bad)} did' if bad else 'neither did'))
PY
