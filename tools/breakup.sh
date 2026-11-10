#!/usr/bin/env bash
#
# Two games, one rocket and a deliberate breakup: the cascade that filled mp.ksarmory.com with
# exploding debris, small enough to watch on one machine and counted, so a fix can be seen to work
# rather than argued about.
#
#     ./tools/two-games.sh --test     # a server and two games, both in one world
#     ./tools/breakup.sh              # fly A's biggest rocket apart; count what it did to both
#     ./tools/breakup.sh --count      # measure the worlds again without flying anything
#     ./tools/breakup.sh --gate       # only say whether the rig is fit to measure
#
# Exits 0 when every claim held, 1 when one did not, and 2 when the rig never came up -- which is a
# different thing from a failure and was previously reported as one.
#
# Every claim here is one whose healthy value is zero or "both agree", because KSA's physics is not
# deterministic and no two runs of this make the same wreckage. A count would need a baseline; these
# do not.
#
#   * INVENTED -- a craft only one world has, that the server has never heard of, and that the game
#     holding it does not own. Its copy of somebody else's rocket came apart in its own physics and
#     it kept the pieces: nothing drives them, no sweep reaches them, and they are in physics and
#     will hit things. All three conditions are needed. The sibling game alone cannot referee -- a
#     name only one game knows is as likely one the other has not received yet -- and the server
#     alone cannot either, because the roster holds only *claimed* craft, so every craft of the
#     handed-over world that nobody flies is absent from it in both worlds and perfectly healthy.
#     Measuring this on either referee by itself is what the old script got wrong.
#   * NOT A PUPPET HERE -- the server says another player owns it, this world has a craft of that
#     name, and this game is not treating it as that player's. The craft it should be following is
#     standing in its own physics instead.
#   * TWO UNDER ONE NAME -- one world holding two craft under one shared name. A breakup makes this
#     near-certain, both games shedding their own copy's pieces under the same names, and a join that
#     was a plain dictionary silently dropped one of the two.
#   * OWNED TWICE -- a craft both games claim.
#   * OWNED AND A PUPPET -- a craft one game treats as both.
#   * PARTS DISAGREE -- a craft one game owns and the other holds as a puppet, with different part
#     counts. The exact shape of a puppet being stripped by the watcher's own physics.
#   * CLAIMED WHAT IT WAS TOLD -- from the logs: a name in both `built` and `sent the server how ...
#     is built` is a game telling everybody about wreckage it was told about. A handover is the one
#     honest reason, so nobody leaves for the whole run.
#
# The worlds are frozen before they are compared, by asking for warp 0 and voting it through, which
# turns "diff two worlds that are both still moving" into "diff two frozen worlds". Four things that
# costs, all of which the output states: a game that was behind does not catch up first, since the
# clock short-circuits at a zero rate, so `behind_s` is sampled before the freeze and not asserted
# on after; a game not at a craft is not frozen at all; a missing vote leaves the world running; and
# a thaw has a five-second lockout in which a second ask is discarded. It is also circular for
# anything about warping or about speed writes.
#
# What it cannot catch: load (the failures that shipped were found with three or four players),
# the network (loopback has no loss, reordering, jitter or MTU), two machines, the vehicle editor's
# part placement, and where anything is -- neither `status` nor `craft` reports a position, so "is
# the puppet where its owner says it is" cannot be asked here at all. A clean run shows nothing
# broken, not a race cured.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=ksa-user-dir.sh
source "$HERE/tools/ksa-user-dir.sh"
USER_DIR="$(ksa_user_dir)"
# The same two profiles two-games.sh makes, since this drives the rig it sets up.
TEST_ROOT="${KSAMP_TWO_GAMES:-${USER_DIR%%/Documents/*}/KSAMultiplayerTest}"
# How long the world must stop changing before it is measured, and how long to wait for that.
QUIET_SECONDS="${KSAMP_BREAKUP_QUIET:-15}"
SETTLE_SECONDS="${KSAMP_BREAKUP_SETTLE:-150}"
RUN="$HERE/.two-games-runs/$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$RUN"

MODE="${1:-}"

call() {
    local name=$1
    shift
    KSA_USER_DIR="$TEST_ROOT/$name" timeout 150 python3 "$HERE/tools/ksa-mcp/server.py" cli "$@"
}

# One line timestamped in all three logs at once, so the mod logs, the KSA logs and the server log
# can be cut at the same instant afterwards. It goes through the chat, which is how a player says it.
fence() {
    call A mp "{\"chat\":\"-- $1 --\"}" >/dev/null 2>&1 || true
}

# Every craft in a world as "name:parts", sorted: the same twice over means nothing is still arriving,
# being rebuilt or coming apart.
fingerprint() {
    python3 -I -c '
import json, sys
try:
    craft = json.load(sys.stdin).get("craft", [])
except Exception:
    print("unreadable")
    sys.exit(0)
print(" ".join(sorted((c.get("key") or c.get("id") or "?") + ":" + str(c.get("parts")) for c in craft)))
'
}

# How much the mod has said about building and describing craft. Settling by quiescence rather than
# by a sleep: a fixed wait is at once too long on an idle machine and too short under load.
chatter() {
    grep -cE ' INFO (built|rebuilding|sent the server how) ' \
        "$TEST_ROOT/$1/Logs/KSAMultiplayer.log" 2>/dev/null || echo 0
}

# --- is the rig fit to measure at all ----------------------------------------

for name in A B; do
    call "$name" status '{}' > "$RUN/$name.status.pre.json" 2>"$RUN/$name.status.pre.err" || true
done

if ! python3 -I - "$RUN" <<'PY'
import json, os, sys

run = sys.argv[1]
bad = []
for name in ('A', 'B'):
    path = os.path.join(run, name + '.status.pre.json')
    try:
        with open(path, encoding='utf-8') as f:
            s = json.load(f)
    except Exception:
        bad.append(f'{name}: the bridge did not answer -- is the game up?')
        continue
    if not s.get('loaded'):
        bad.append(f'{name}: no world is loaded')
    if s.get('popups'):
        bad.append(f'{name}: a popup is over the game ({", ".join(s["popups"])})')
    if not s.get('server'):
        bad.append(f'{name}: not joined to a server')
    if s.get('arriving') is not None:
        bad.append(f'{name}: still receiving the world ({s["arriving"]:.0%})')
for line in bad:
    print('VOID: ' + line)
sys.exit(2 if bad else 0)
PY
then
    echo "the rig did not come up; run ./tools/two-games.sh --test first" >&2
    echo "(the dumps are in $RUN)" >&2
    exit 2
fi
echo "both games are up, loaded and joined."
[[ "$MODE" == "--gate" ]] && exit 0

# --- fly it apart -------------------------------------------------------------

if [[ "$MODE" != "--count" ]]; then
    echo "A flies a rocket apart; B only watches."
    fence "breakup begins"
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
        exit 2
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
fi

# --- wait for both worlds to stop changing ------------------------------------

echo "waiting for both worlds to go quiet (${QUIET_SECONDS}s still, ${SETTLE_SECONDS}s at most)..."
settled=no
last=""
quiet_since=0
began=$SECONDS
while (( SECONDS - began < SETTLE_SECONDS )); do
    now=""
    for name in A B; do
        now+="$name[$(call "$name" craft '{}' 2>/dev/null | fingerprint)|$(chatter "$name")]"
    done
    arriving=$(for name in A B; do call "$name" status '{}' 2>/dev/null; done |
        grep -c '"arriving": [^n]' || true)
    if [[ "$now" == "$last" && "$arriving" == "0" ]]; then
        (( quiet_since == 0 )) && quiet_since=$SECONDS
        if (( SECONDS - quiet_since >= QUIET_SECONDS )); then settled=yes; break; fi
    else
        quiet_since=0
        last="$now"
    fi
    sleep 5
done
if [[ "$settled" == yes ]]; then
    echo "  quiet after $(( SECONDS - began ))s."
else
    echo "  still changing after ${SETTLE_SECONDS}s -- measured moving, so read the numbers as a floor."
fi

# --- freeze, then measure -----------------------------------------------------

fence "measuring"
echo "freezing both worlds (warp 0, voted through)..."
frozen=no
if call A mp '{"warp":0}' >/dev/null 2>&1; then
    sleep 1
    call B mp '{"vote":true}' >/dev/null 2>&1 || true
    call A mp '{"vote":true}' >/dev/null 2>&1 || true
    for _ in $(seq 1 10); do
        sleep 2
        stopped=$(for name in A B; do call "$name" status '{}' 2>/dev/null; done |
            python3 -I -c '
import json, sys
# Two objects back to back, so they are read one at a time.
text = sys.stdin.read()
decoder = json.JSONDecoder()
at, speeds = 0, []
while at < len(text):
    while at < len(text) and text[at] in " \r\n\t":
        at += 1
    if at >= len(text):
        break
    obj, at = decoder.raw_decode(text, at)
    speeds.append(obj.get("speed"))
print("yes" if len(speeds) == 2 and all(s == 0 for s in speeds) else "no")
')
        [[ "$stopped" == yes ]] && { frozen=yes; break; }
    done
fi
if [[ "$frozen" == yes ]]; then
    # A speed of zero is not a stopped clock on its own: a game not at a craft is forced back to 1x
    # and reports what it was told, so the universe time itself has to be seen not to move.
    before=$(call A status '{}' | python3 -I -c 'import json,sys; print(json.load(sys.stdin).get("universe"))')
    sleep 3
    after=$(call A status '{}' | python3 -I -c 'import json,sys; print(json.load(sys.stdin).get("universe"))')
    [[ "$before" == "$after" ]] || { frozen=no; echo "  speed is 0 but the clock still runs ($before -> $after)"; }
fi
[[ "$frozen" == yes ]] && echo "  frozen." || echo "  not frozen -- the worlds were compared while moving."

# Both worlds as each game sees them, while they are still up: the logs cannot show a craft that
# was never written down, and the ones that matter most never are.
for name in A B; do
    call "$name" craft '{}' > "$RUN/$name.craft.json" 2>/dev/null || true
    call "$name" status '{}' > "$RUN/$name.status.json" 2>/dev/null || true
    cp -f "$TEST_ROOT/$name/Logs/KSAMultiplayer.log" "$RUN/$name.mod.log" 2>/dev/null || true
done
cp -f "$HERE/.two-games-server.log" "$RUN/server.log" 2>/dev/null || true

if [[ "$frozen" == yes ]]; then
    echo "thawing..."
    call A mp '{"warp":1}' >/dev/null 2>&1 || true
    sleep 1
    call B mp '{"vote":true}' >/dev/null 2>&1 || true
    call A mp '{"vote":true}' >/dev/null 2>&1 || true
fi
fence "breakup ends"
echo

# --- count it -----------------------------------------------------------------

set +e
python3 -I - "$TEST_ROOT" "$HERE/.two-games-server.log" "$RUN" "$frozen" "$settled" <<'PY' | tee "$RUN/report.txt"
import glob, json, os, re, statistics, sys

root, server_log, run, frozen, settled = sys.argv[1:6]
BUILT = re.compile(r' INFO built (.+?), ')
REBUILT = re.compile(r' INFO rebuilding (.+?): ')
DESCRIBED = re.compile(r' INFO sent the server how (.+?) is built')
DESTROYED = re.compile(r" WARN Vehicle '(.+?)' destroyed - (\d+) parts")
REFUSED = re.compile(r' ERROR Attempted to update vehicle (.+?) with origin at time ([\d.,]+) with new origin at time ([\d.,]+)')
CEILING = re.compile(r' WARN (ExplosionSystem: more than|Freed only)')
FAILED = re.compile(r'Update task failed')
# The rig broke, not the mod: nothing below is worth reading after one of these.
VOID = re.compile(r'ErrorOutOfDeviceMemory|KSA has moved a method this mod patches')
# The mod broke.
FAULTS = re.compile(r'faults: KSA Multiplayer is switched off| ERROR ')

fails, voids, notes = [], [], []


def newest_ksa_log(folder):
    logs = [p for p in glob.glob(os.path.join(folder, 'Logs', 'KittenSpaceAgency.*.log'))
            if not p.endswith('.ring')]
    return max(logs, key=os.path.getmtime) if logs else None


def read(path):
    if not path or not os.path.isfile(path):
        return []
    with open(path, encoding='utf-8', errors='replace') as f:
        return f.readlines()


def load(name, what):
    path = os.path.join(run, f'{name}.{what}.json')
    try:
        with open(path, encoding='utf-8') as f:
            return json.load(f)
    except Exception:
        return None


def shared(craft):
    """The name the room knows a craft by: its own unless this world had to rename it."""
    return craft.get('key') or craft.get('id') or '?'


def held(dump):
    """Every craft in one world, by shared name -- a list, because two can share one."""
    out = {}
    for c in dump.get('craft', []):
        out.setdefault(shared(c), []).append(c)
    return out


print(f'measured {"frozen" if frozen == "yes" else "MOVING"}, '
      f'{"after the worlds went quiet" if settled == "yes" else "WITHOUT the worlds going quiet"}.')
mode = 'linux'
if os.path.isfile(os.path.join(os.path.dirname(server_log), '.two-games-server.on')):
    with open(os.path.join(os.path.dirname(server_log), '.two-games-server.on')) as f:
        mode = f.read().strip() or 'linux'
print(f'the server under test was the {mode} build.')
if mode != 'linux':
    notes.append('a Windows server was tested, not the Linux one this repository is for')

# The round trip the games actually had, which decides a whole class of claim: everything
# proportional to it is unreachable on loopback. See docs/WARP-AND-WIRE.md.
lag = '0'
try:
    with open(os.path.join(os.path.dirname(server_log), '.two-games-lag.ms')) as f:
        lag = f.read().strip() or '0'
except Exception:
    pass
if lag == '0':
    print('the games were on loopback: no loss, no reordering, a round trip of about 0.05 ms.')
    notes.append('loopback, so nothing proportional to the round trip was tested')
else:
    held = ''
    try:
        with open(os.path.join(os.path.dirname(server_log), '.two-games-relay.log')) as f:
            for line in f:
                if 'held' in line:
                    held = line.strip()
    except Exception:
        pass
    print(f'the games were {lag} ms from the server each way, so a {float(lag) * 2:.0f} ms round trip.')
    if held:
        print(f'  the relay last said: {held}')
print()

worlds = {name: load(name, 'craft') for name in ('A', 'B')}
states = {name: load(name, 'status') for name in ('A', 'B')}
pre = {name: load(name, 'status.pre') for name in ('A', 'B')}

if all(worlds.values()) and all(states.values()):
    print('the worlds, as each game has them')
    for name, other in (('A', 'B'), ('B', 'A')):
        mine, state = held(worlds[name]), states[name]
        # The server's roster is the referee. The sibling game cannot be: a name only one game
        # knows is as likely one the other has not received yet as one this game made up.
        roster = {o['craft'] for o in state.get('owners') or []}
        owned = set(state.get('owned_here') or [])
        puppets = set(state.get('puppets_here') or [])
        flat = [c for group in mine.values() for c in group]

        theirs = held(worlds[other])
        alone = set(mine) - set(theirs)
        # All three conditions. The roster holds only claimed craft, so the craft of the handed-over
        # world that nobody flies are in neither game's roster and in both games' worlds: healthy,
        # and counted as a fault by either referee used on its own.
        invented = sorted(k for k in alone if k not in roster and k not in owned)
        # Somebody else's, standing here, and not being followed: it is in this world's own physics.
        elsewhere = {o['craft'] for o in state.get('owners') or [] if not o['mine']}
        unpuppeted = sorted(k for k in elsewhere if k in mine and k not in puppets)
        absent = sorted(k for k in elsewhere if k not in mine)
        twice = sorted(k for k, group in mine.items() if len(group) > 1)
        both = sorted(owned & puppets)
        nobodys = sorted(k for k in mine if k not in roster and k not in owned and k not in alone)

        print(f'  {name}  {len(flat):4d} craft -- {len(owned):4d} its own, {len(puppets):4d} other'
              f" players', {len(nobodys):4d} claimed by nobody in either world")
        for label, found, bucket in (
                ('INVENTED, only here and nobody has heard of them', invented, fails),
                ('NOT A PUPPET HERE, though the server says whose', unpuppeted, fails),
                ('TWO CRAFT UNDER ONE SHARED NAME', twice, fails),
                ('OWNED AND A PUPPET AT ONCE', both, fails),
                ("another player's, not built here yet", absent, notes),
                ('only here, but the server knows them', sorted(alone & roster), notes)):
            shown = f'   {", ".join(found[:6])}{" ..." if len(found) > 6 else ""}' if found else '   none'
            print(f'      {label}  {len(found):4d}{shown}')
            if found:
                bucket.append(f'{name}: {len(found)} {label}')
        if pre[name] and pre[name].get('behind_s') is not None:
            print(f'      behind the server by {pre[name]["behind_s"]} s before the freeze')

    a, b = states['A'], states['B']
    doubled = sorted(set(a.get('owned_here') or []) & set(b.get('owned_here') or []))
    print(f'  OWNED BY BOTH GAMES  {len(doubled):4d}'
          + (f'   {", ".join(doubled[:6])}' if doubled else '   none'))
    if doubled:
        fails.append(f'{len(doubled)} craft are claimed by both games')

    # A craft one game owns and the other holds as a puppet should be the same craft in both: this
    # is the one claim here about agreement rather than about absence, and the exact shape of a
    # puppet having its parts stripped off by the watcher's own physics.
    disagreed = []
    for name, other in (('A', 'B'), ('B', 'A')):
        mine, theirs = held(worlds[name]), held(worlds[other])
        for key, group in mine.items():
            if len(group) != 1 or not group[0].get('owned'):
                continue
            seen = theirs.get(key)
            if not seen or len(seen) != 1 or not seen[0].get('puppet'):
                continue
            if group[0].get('parts') != seen[0].get('parts'):
                disagreed.append(f'{key} {group[0].get("parts")} in {name}, {seen[0].get("parts")} in {other}')
    print(f'  PARTS DISAGREE ON A SHARED CRAFT  {len(disagreed):4d}'
          + (f'   {"; ".join(disagreed[:4])}' if disagreed else '   none'))
    if disagreed:
        fails.append(f'{len(disagreed)} shared craft have different part counts in the two worlds')
    print()
else:
    voids.append('no live worlds were read, so only the logs below mean anything')
    print('(no live worlds read -- the games are not up, so only the logs below)\n')

for name in ('A', 'B'):
    folder = os.path.join(root, name)
    mod = read(os.path.join(folder, 'Logs', 'KSAMultiplayer.log'))
    ksa = read(newest_ksa_log(folder))
    if not mod:
        voids.append(f'{name}: no mod log in {folder} -- was the game running?')
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
    moved = [line.strip() for line in mod + ksa if VOID.search(line)]
    faulted = [line.strip() for line in mod if FAULTS.search(line)]

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
    if claimed:
        fails.append(f'{name}: {len(claimed)} craft were described here after being built from somebody else\'s word')
    if moved:
        voids.append(f'{name}: {moved[0][:120]}')
    if faulted:
        fails.append(f'{name}: {faulted[0][:120]}')
    if gaps:
        notes.append(f'{name}: {len(gaps)} origin updates refused, worst {max(gaps):.1f} s')
    if ceilings or failed:
        notes.append(f"{name}: {failed} physics steps failed, {ceilings} of KSA's own ceilings hit")

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

print('-' * 78)
for label, bucket in (('VOID', voids), ('FAIL', fails), ('NOTE', notes)):
    if not bucket:
        continue
    for line in bucket:
        print(f'{label}  {line}')
if not voids and not fails:
    print('every claim held. A clean run shows nothing broken, not a race cured.')
print(f'\nthe dumps and the logs are in {run}')

sys.exit(2 if voids else (1 if fails else 0))
PY
code=${PIPESTATUS[0]}
set -e
exit "$code"
