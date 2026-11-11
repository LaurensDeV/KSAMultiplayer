#!/usr/bin/env python3
"""Is another player's craft where its owner says it is, and does it get there smoothly?

    ./tools/two-games.sh --test           # a server and two games, both in one world
    ./tools/motion.py                     # record both for twenty seconds and lay them side by side
    ./tools/motion.py --seconds 40
    ./tools/motion.py --at 5 A engine '{"on":true}'   # do something to a craft part-way through
    ./tools/motion.py --scene orbit       # A flies a craft in orbit and burns; B watches it
    ./tools/motion.py --scene eva         # a kitten out of a craft in orbit
    ./tools/motion.py --scene meet        # each game flies a craft, forty metres apart in one orbit
    ./tools/motion.py --scene walk        # a kitten walks, turns, runs and stops
    ./tools/motion.py --scene drop        # a craft lifted 150 m and let go; --scene land, 3 m
    ./tools/motion.py --scene nocraft     # B flies, and A's camera is on the planet; nocraft-warp, through a warp
    ./tools/motion.py --scene launch      # a rocket on the ground burns for 35 s; --scene hop, up and down on its engine
    ./tools/motion.py --scene dock        # A's craft docks with one nobody flies; dock-theirs, with the one B flies
    ./tools/motion.py --scene fly-dock    # A flies onto a port on thrusters and undocks; fly-dock-theirs, onto B's craft
    ./tools/motion.py --scene fly-dock    # A flies one craft onto another's port on its thrusters, docks, and undocks
    ./tools/motion.py --check <run>       # judge a recording already taken, without a game

Exits 0 when no craft was put anywhere outright and none jumped, 1 when one was, and 2 when there
was nothing to judge.

Each game writes down, every frame, where each craft it owns is, and at every physics step, where
each craft of another player's stood and where it had been told to stand (`motion` on the bridge,
src/Mod/Motion.cs). Universe time is the one clock the two games share, so one game's account of a
craft it owns is the truth for the other's copy of it, at the universe time the copy was being
drawn for. Nothing here is timed on this side.

For each craft one game owns and the other holds:

  * **drawn** -- how far the copy stood, as each frame was drawn, from where its owner had the craft
    at that universe time. This is what a player sees.
  * **step** -- how much that error changed between one frame and the next. A craft a steady metre
    out is in the wrong place; one whose error moves is shaking, and that is what is noticed.
  * **told-to-stand** and **the pull** -- its two causes, which are different faults: how far the
    place it was told to stand was from the truth, which is what was said and what was worked out
    from it, and how far the craft stood from that place at the start of each physics step, which
    is the pull through KSA's physics. Not the same instant as a frame: KSA puts a coasting craft
    back on its own orbit every step whatever was written into it the step before.
  * **put there** -- how many times it was more than the snap distance out and was moved outright.
  * **past its newest word** -- the share of steps it was being carried on rather than drawn between
    two states.

And for every two craft within five kilometres of each other, whoever's they are: how far apart
they stand as each game has them, and how far the two accounts differ. That is the one that says
whether two players can meet.

Seven claims are zero-expected and decide the exit: nothing put anywhere outright, no step of a
metre or more, no copy drawn a metre from where its owner has the craft, no two craft a metre
differently apart in the two games, no copy left three seconds in a shape its owner no longer has,
no craft in one world that is not in the other with as many parts when the recording ends, and
the two games within half a second of one universe time by then. Where a copy has not yet the
parts its owner has, it is counted as that and not measured: its centre says how its weight is
spread, not where it stands. The rest are measurements, because what is good enough depends on how close anybody is
standing.

The last is there because everything above is laid side by side on universe time, and a game
that is four minutes in the past agrees with all of it: each row carries the time of day as well,
which the two games share, so their clocks can be compared at one instant.

A copy was once drawn 150 ms in the past, on purpose, and at 7.2 km/s that was 1,083 m from
everything the game that drew it stepped for itself. This is the tool that showed it.
"""

from __future__ import annotations

import argparse
import bisect
import json
import math
import shutil
import statistics
import subprocess
import sys
import threading
import time
from pathlib import Path

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE / "tools" / "ksa-mcp"))
from server import Refused, bridge_of, send_to, wsl  # noqa: E402

GAMES = ("A", "B")
# A step this large in how far a craft is from the truth is one a player calls a teleport.
JUMP_M = 1.0
# And this is one they call a judder, counted and not judged.
SHAKE_M = 0.05
# A copy this long in a shape its owner has left has been left there: the mod builds a standing
# craft again no sooner than two seconds after it last did, and the word has a wire to cross, s.
WRONG_SHAPE_S = 3.0
# Two craft this close in one game are asked about in the other.
NEAR_M = 5000.0


def profiles() -> dict[str, Path]:
    out = subprocess.run([str(HERE / "tools" / "two-games.sh"), "--dir", "A"], capture_output=True, text=True)
    root = Path(out.stdout.strip()).parent
    return {name: root / name for name in GAMES}


def call(folder: Path, cmd: str, timeout: float = 30.0, **args) -> dict:
    return send_to(bridge_of(folder), cmd, timeout, **args)


# ---- recording -----------------------------------------------------------------------------

def record(seconds: float, events: list[tuple[float, str, str, dict]]) -> Path | None:
    games = profiles()
    run = HERE / ".two-games-runs" / time.strftime("%Y%m%dT%H%M%SZ-motion", time.gmtime())
    run.mkdir(parents=True)
    about: dict = {"seconds": seconds, "events": [], "status": {}}
    try:
        for name, folder in games.items():
            s = call(folder, "status", timeout=10)
            about["status"][name] = {k: s.get(k) for k in ("craft", "watching", "stage", "rtt_ms", "speed", "universe",
                                                         "owned_here", "puppets_here", "analytic")}
            if not s.get("server"):
                print(f"VOID: {name} is not joined to a server", file=sys.stderr)
                return None
    except Refused as e:
        print(f"VOID: a game did not answer -- is the rig up? ({e})", file=sys.stderr)
        return None

    # Both at once, near enough: each account carries its own universe times, so nothing rests on it.
    started = [threading.Thread(target=call, args=(folder, "motion"), kwargs={"start": True}) for folder in games.values()]
    for t in started:
        t.start()
    for t in started:
        t.join()
    print(f"recording both games for {seconds:g} s...", flush=True)
    began = time.monotonic()
    for after, name, cmd, args in sorted(events, key=lambda e: e[0]):
        time.sleep(max(0.0, after - (time.monotonic() - began)))
        try:
            answer = call(games[name], cmd, **args)
        except Refused as e:
            answer = {"refused": str(e)}
        about["events"].append({"after": after, "game": name, "cmd": cmd, "args": args, "answer": answer})
        print(f"  {after:g} s: {name} {cmd} {json.dumps(args)} -> {json.dumps(answer)[:120]}", flush=True)
    time.sleep(max(0.0, seconds - (time.monotonic() - began)))

    for name, folder in games.items():
        try:
            done = call(folder, "motion", timeout=120, stop=True)
        except Refused as e:
            print(f"VOID: {name} would not hand over its recording ({e})", file=sys.stderr)
            return None
        shutil.copyfile(wsl(done["file"]), run / f"{name}.jsonl")
        about.setdefault("lines", {})[name] = done.get("lines")
    # What each world is left holding, which is the whole of what a docking or a breakup is judged on.
    about["craft"] = {}
    for name, folder in games.items():
        try:
            about["craft"][name] = [{k: c.get(k) for k in ("id", "key", "parts", "situation", "puppet", "owned")}
                                    for c in call(folder, "craft").get("craft", [])]
        except Refused:
            about["craft"][name] = None
    for extra in (".two-games-lag.ms", ".two-games-server.on"):
        try:
            about[extra] = (HERE / extra).read_text().strip()
        except OSError:
            pass
    try:
        about["relay"] = (HERE / ".two-games-relay.log").read_text().splitlines()[0] if about.get(".two-games-lag.ms", "0") != "0" else ""
    except (OSError, IndexError):
        about["relay"] = ""
    (run / "about.json").write_text(json.dumps(about, indent=1))
    return run


# ---- judging -------------------------------------------------------------------------------

def sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def length(a) -> float:
    return math.sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2])


class Truth:
    """Where a craft was at any universe time, from its owner's own frames."""

    def __init__(self, samples: list[dict]):
        # One a universe time: a game paused or held still says the same instant many times over.
        seen: dict[float, dict] = {}
        for s in samples:
            seen[s["u"]] = s
        self.samples = [seen[u] for u in sorted(seen)]
        self.times = [s["u"] for s in self.samples]

    def at(self, u: float):
        i = bisect.bisect_right(self.times, u) - 1
        if i < 0 or i + 1 >= len(self.samples):
            return None
        a, b = self.samples[i], self.samples[i + 1]
        span = b["u"] - a["u"]
        # Across a gap in the owner's own account there is no saying where the craft was.
        if span <= 0 or span > 1.0 or a["b"] != b["b"]:
            return None
        f = (u - a["u"]) / span
        f2, f3 = f * f, f * f * f
        # Along the curve that leaves one frame at its speed and reaches the next at its own.
        return tuple(a["p"][k] * (2 * f3 - 3 * f2 + 1) + a["v"][k] * (f3 - 2 * f2 + f) * span
                     + b["p"][k] * (3 * f2 - 2 * f3) + b["v"][k] * (f3 - f2) * span for k in range(3)), a["b"], bool(a.get("rails"))

    def moved_outright(self) -> list[tuple[float, float]]:
        """When the owner's own frames moved the craft other than at the speed they gave, and how far: it was put somewhere."""
        out = []
        for a, b in zip(self.samples, self.samples[1:]):
            span = b["u"] - a["u"]
            if 0 < span < 0.5 and a["b"] == b["b"]:
                mean = [(a["v"][k] + b["v"][k]) / 2 for k in range(3)]
                by = length([b["p"][k] - a["p"][k] - mean[k] * span for k in range(3)])
                if by > 0.5:
                    out.append((b["u"], by))
        return out


def between(seen: dict[str, dict[str, list[dict]]]) -> dict[tuple[str, str], tuple[list[float], list[float], list[float]]]:
    """For every two craft near each other in A's world: how far apart they are in each, and how the two accounts differ."""
    tracks = {g: {k: Truth(rows) for k, rows in seen[g].items() if len(rows) > 10} for g in GAMES}
    both = sorted(set(tracks["A"]) & set(tracks["B"]))
    # How many parts each game had each craft at, by universe time. Two games that mean two shapes
    # by one name are asked nothing about where it is: its centre is somewhere else in each.
    shapes = {g: {k: sorted((r["u"], r["n"]) for r in rows if "n" in r) for k, rows in seen[g].items()} for g in GAMES}

    def parts(game: str, key: str, u: float) -> int | None:
        had = shapes[game].get(key) or []
        i = bisect.bisect_right(had, (u, float("inf"))) - 1
        return had[max(i, 0)][1] if had else None

    def one_shape(key: str, u: float) -> bool:
        return parts("A", key, u) == parts("B", key, u)

    out = {}
    for i, one in enumerate(both):
        for other in both[i + 1:]:
            in_a, in_b, differ = [], [], []
            # At A's own frames of the first craft, a few a second: each game is asked where both
            # were at that universe time.
            for u in tracks["A"][one].times[::6]:
                a1, a2 = tracks["A"][one].at(u), tracks["A"][other].at(u)
                if a1 is None or a2 is None or a1[1] != a2[1]:
                    continue
                gap_a = sub(a1[0], a2[0])
                if length(gap_a) > NEAR_M:
                    break
                b1, b2 = tracks["B"][one].at(u), tracks["B"][other].at(u)
                if b1 is None or b2 is None or not one_shape(one, u) or not one_shape(other, u):
                    continue
                gap_b = sub(b1[0], b2[0])
                in_a.append(length(gap_a))
                in_b.append(length(gap_b))
                differ.append(length(sub(gap_a, gap_b)))
            if len(differ) > 5:
                out[(one, other)] = (in_a, in_b, differ)
    return out


def pct(values: list[float], q: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, int(q * len(ordered)))]


def judge(run: Path) -> int:
    about = json.loads((run / "about.json").read_text()) if (run / "about.json").exists() else {}
    owned: dict[str, dict[str, list[dict]]] = {g: {} for g in GAMES}
    held: dict[str, dict[str, list[dict]]] = {g: {} for g in GAMES}
    seen: dict[str, dict[str, list[dict]]] = {g: {} for g in GAMES}
    for game in GAMES:
        path = run / f"{game}.jsonl"
        if not path.exists():
            continue
        for text in path.read_text().splitlines():
            try:
                row = json.loads(text)
            except json.JSONDecodeError:
                continue
            {"o": owned, "p": held, "w": seen}.get(row.get("r"), seen)[game].setdefault(row["k"], []).append(row)

    out: list[str] = []
    fails: list[str] = []
    notes: list[str] = []

    def say(text: str = "") -> None:
        out.append(text)

    lag = about.get(".two-games-lag.ms", "0")
    say(f"{about.get('seconds', '?')} s of both games, " + ("on loopback." if lag in ("0", "") else f"{lag} ms from the server each way."))
    if about.get("relay"):
        say(f"  the relay: {about['relay']}")
    for name in GAMES:
        s = (about.get("status") or {}).get(name) or {}
        say(f"  {name} flies {s.get('craft')}, watches {s.get('watching')}, round trip {s.get('rtt_ms')} ms, at {s.get('speed')}x")
    for e in about.get("events", []):
        say(f"  {e['after']:g} s in: {e['game']} {e['cmd']} {json.dumps(e['args'])}")
    say()

    pairs = 0
    say("craft                     owner>copy            frames   drawn, from where its owner has it: median  95%   worst | "
        "step: 95%  worst  shakes  jumps | told-to-stand  the pull  put there  past newest  word age")
    for owner, viewer in (("A", "B"), ("B", "A")):
        for key in sorted(set(owned[owner]) & set(seen[viewer])):
            truth = Truth(owned[owner][key])
            pulled = held[viewer].get(key, [])
            # A craft its owner put somewhere outright is one its copy has to be put after, and
            # that is not the copy's fault: the half second either side of it is left out.
            outright = truth.moved_outright()

            def excused(u: float) -> bool:
                return any(abs(u - at) < 0.5 for at, _ in outright)

            # Apart, because they are moved by different things: a craft nothing is pushing is
            # stepped along its orbit by the game that holds it, and one under way is pulled.
            phases: dict[str, dict] = {}

            def phase_of(rails: bool) -> dict:
                return phases.setdefault("coasting" if rails else "under way",
                                         {"drawn": [], "moved": [], "told": [], "pull": [], "ages": [], "steps": []})

            # Where the copy stood as each frame was drawn, which is after the pull has had its say
            # and is what a player sees. At the start of the physics step that follows it may be
            # somewhere else: KSA puts a coasting craft back on its own orbit every step, and the
            # first version of this read the craft there and called a kilometre four millimetres.
            was = None
            # How many parts its owner had it at, by universe time: a copy that is not yet the
            # shape its owner has is not the same thing in another place, and where its centre is
            # says how its weight is spread and not where it stands. Counted, and not measured.
            shaped = sorted((r["u"], r["n"]) for r in owned[owner][key] if "n" in r)
            shape_times = [u for u, _ in shaped]
            other_shape: list[float] = []
            for f in sorted(seen[viewer].get(key, []), key=lambda r: r["u"]):
                there = truth.at(f["u"])
                if there is None or there[1] != f["b"] or excused(f["u"]):
                    was = None
                    continue
                if shaped and "n" in f:
                    i = min(len(shaped) - 1, max(0, bisect.bisect_right(shape_times, f["u"]) - 1))
                    if shaped[i][1] != f["n"]:
                        other_shape.append(f["u"])
                        was = None
                        continue
                ph = phase_of(there[2])
                error = sub(f["p"], there[0])
                ph["drawn"].append(length(error))
                # Only across frames of a clock that moved: two readings of one instant are one.
                if was is not None and f["u"] > was[0]:
                    ph["moved"].append(length(sub(error, was[1])))
                was = (f["u"], error)
            for s in pulled:
                there = truth.at(s["tu"]) if s.get("tu") is not None else None
                if there is None or there[1] != s["b"] or excused(s["u"]):
                    continue
                ph = phase_of(there[2])
                ph["told"].append(length(sub(s["tp"], there[0])))
                ph["pull"].append(s["off"])
                ph["ages"].append(s["u"] - s["nu"])
                ph["steps"].append(s)
            if not any(ph["drawn"] for ph in phases.values()):
                continue
            pairs += 1
            flown = any(s.get("flown") for s in pulled)
            if not pulled:
                notes.append(f"{key} is {owner}'s and {viewer} never pulled it: it is drawn there off its own orbit, or off the ground")
            for name, ph in phases.items():
                drawn, moved, mine = ph["drawn"], ph["moved"], ph["steps"]
                if not drawn:
                    continue
                snaps = sum(1 for s in mine if s.get("snap"))
                past = (sum(1 for s in mine if s["tu"] > s["nu"] + 1e-9) / len(mine)) if mine else 0.0
                rest = (sum(1 for s in mine if s.get("rest")) / len(mine)) if mine else 0.0
                jumps = sum(1 for m in moved if m >= JUMP_M)
                shakes = sum(1 for m in moved if m >= SHAKE_M)
                say(f"{key[:24]:25s} {owner}>{viewer} {'flown' if flown else '     '} {name:9s} {len(drawn):6d}   "
                    f"{statistics.median(drawn):28.3f} {pct(drawn, 0.95):7.3f} {max(drawn):7.3f} m | "
                    f"{pct(moved, 0.95):8.3f} {max(moved, default=0.0):6.3f} {shakes:6d} {jumps:6d} | "
                    f"{pct(ph['told'], 0.95):10.3f} m {pct(ph['pull'], 0.95):8.3f} m {snaps:8d} {past:11.0%} "
                    f"{(statistics.median(ph['ages']) if ph['ages'] else 0.0):8.3f} s"
                    + (f"   at rest {rest:.0%}" if rest > 0.5 else ""))
                if snaps:
                    fails.append(f"{key}, as {viewer} holds it {name}, was put where it was told {snaps} time{'s' if snaps > 1 else ''}")
                if jumps:
                    fails.append(f"{key}, as {viewer} draws it {name}, stepped a metre or more from one frame to the next {jumps} "
                                 f"time{'s' if jumps > 1 else ''}, {max(moved):.1f} m at worst")
                if pct(drawn, 0.95) >= JUMP_M:
                    fails.append(f"{key}, as {viewer} draws it {name}, stood {pct(drawn, 0.95):.1f} m from where {owner} had it")
            for at, by in outright[:3]:
                notes.append(f"{owner} put {key} {by:.0f} m from where it was, outright: the second around that is not judged")
            if other_shape:
                # Runs of frames, since a craft that changes shape twice is the wrong one twice.
                runs, began, last = [], other_shape[0], other_shape[0]
                for u in other_shape[1:]:
                    if u - last > 0.5:
                        runs.append(last - began)
                        began = u
                    last = u
                runs.append(last - began)
                notes.append(f"{key}, as {viewer} draws it, was not the shape {owner} had it for {len(other_shape)} frames in "
                             f"{len(runs)} stretch{'es' if len(runs) > 1 else ''}, {max(runs):.2f} s the longest: not measured while it was not")
                if max(runs) >= WRONG_SHAPE_S:
                    fails.append(f"{key}, as {viewer} draws it, went {max(runs):.1f} s as a shape {owner} no longer had it")
        for key in sorted(set(owned[owner]) - set(seen[viewer])):
            notes.append(f"{key} is {owner}'s and {viewer} never had it: not built there")
    # How far apart two craft stand, as each game has it. One craft a steady kilometre out is in
    # the wrong place and nobody near it can tell; two craft that are ten metres apart in one game
    # and a kilometre apart in the other is a kitten that cannot get back in its capsule.
    apart = between(seen)
    differing: list[tuple[float, str]] = []
    if apart:
        say()
        say("two craft                                    apart as A has it   as B has it | the two differ by: median     95%   worst")
        for (one, other), (in_a, in_b, differ) in sorted(apart.items(), key=lambda kv: statistics.median(kv[1][0])):
            say(f"{(one + ' and ' + other)[:43]:44s} {statistics.median(in_a):14.2f} m {statistics.median(in_b):13.2f} m | "
                f"{statistics.median(differ):24.3f} {pct(differ, 0.95):7.3f} {max(differ):7.3f} m")
            if pct(differ, 0.95) >= JUMP_M:
                differing.append((pct(differ, 0.95), f"{one} and {other} are {statistics.median(in_a):.1f} m apart as A has them and "
                                  f"{statistics.median(in_b):.1f} m as B has them: {pct(differ, 0.95):.1f} m between the two accounts"))
        # A rocket in forty pieces is eight hundred pairs, and the three worst say what the rest would.
        differing.sort(reverse=True)
        fails.extend(line for _, line in differing[:3])
        if len(differing) > 3:
            fails.append(f"and {len(differing) - 3} more pairs of craft a metre or more differently apart in the two games")
        pairs += len(apart)
    left = about.get("craft") or {}
    if left.get("A") is not None and left.get("B") is not None:
        worlds = {g: {c.get("key") or c["id"]: c.get("parts") for c in left[g]} for g in GAMES}
        say()
        say("left in each world when it ended: " + ", ".join(
            f"{k} {worlds['A'].get(k, '-')}/{worlds['B'].get(k, '-')}" for k in sorted(set(worlds["A"]) | set(worlds["B"]))) + "  (parts in A/B)")
        for k in sorted(set(worlds["A"]) | set(worlds["B"])):
            if worlds["A"].get(k) != worlds["B"].get(k):
                def has(g: str) -> str:
                    return f"{worlds[g][k]} parts" if k in worlds[g] else "not there"
                fails.append(f"{k} is {has('A')} in A's world and {has('B')} in B's")
        pairs += 1
    # One world has one time. Each row carries the time of day it was written at, which the two
    # games share, so where each game's clock stood at one instant can be laid beside the other's.
    clocks = {}
    for g in GAMES:
        at = sorted({(r["w"], r["u"]) for rows in seen[g].values() for r in rows[::10] if "w" in r})
        if len(at) > 10:
            clocks[g] = ([w for w, _ in at], [u for _, u in at])
    if len(clocks) == 2:
        def clock(g: str, w: float) -> float | None:
            walls, universes = clocks[g]
            i = bisect.bisect_left(walls, w)
            if i <= 0 or i >= len(walls):
                return None
            f = (w - walls[i - 1]) / max(walls[i] - walls[i - 1], 1e-9)
            return universes[i - 1] + f * (universes[i] - universes[i - 1])
        gaps = [(w, clock("A", w) - clock("B", w)) for w in clocks["A"][0][::20]
                if clock("A", w) is not None and clock("B", w) is not None]
        if gaps:
            worst = max(gaps, key=lambda g: abs(g[1]))
            say()
            say(f"A's universe time less B's, at one instant: {statistics.median(g for _, g in gaps):+.3f} s at the median, "
                f"{worst[1]:+.3f} s at worst ({worst[0] - gaps[0][0]:.0f} s in), {gaps[-1][1]:+.3f} s at the end")
            if abs(gaps[-1][1]) >= 0.5:
                fails.append(f"the two games ended {abs(gaps[-1][1]):.1f} s apart in universe time, A {'behind' if gaps[-1][1] < 0 else 'ahead'}")
    say()
    say("drawn is how far the copy stood, as each frame was drawn, from where its owner had the craft at that")
    say("universe time; a step is how much that moved between two frames, a shake one of "
        f"{SHAKE_M * 100:g} cm or more and a jump")
    say(f"one of {JUMP_M:g} m. told-to-stand is how far the place it was told to stand was from the truth, and the pull")
    say("how far it stood from that place at the start of each physics step, both at the 95th in a hundred.")
    say()
    say("-" * 78)
    if pairs == 0:
        say("VOID  no craft was owned by one game and pulled by the other while this was recording")
    for line in fails:
        say(f"FAIL  {line}")
    for line in dict.fromkeys(notes):
        say(f"NOTE  {line}")
    if pairs and not fails:
        say("nothing was put anywhere outright and nothing jumped.")
    say(f"\nthe recordings are in {run}")
    text = "\n".join(out)
    print(text)
    (run / "report.txt").write_text(text + "\n")
    return 2 if pairs == 0 else (1 if fails else 0)


def of_a(games, want) -> list[dict]:
    return [c for c in call(games["A"], "craft").get("craft", []) if not c.get("puppet") and want(c)]


def both_on(games, craft: str, watch: bool = True) -> bool:
    """A flies a craft and B puts its camera on it, and the two clocks are let settle."""
    call(games["A"], "goto", craft=craft)
    until = time.monotonic() + 120
    while time.monotonic() < until:
        time.sleep(2)
        try:
            # Refused for a moment while B builds it again as A's.
            if watch:
                call(games["B"], "watch", craft=craft)
        except Refused:
            continue
        both = [call(folder, "status") for folder in games.values()]
        if all(s.get("behind_s") is not None and abs(s["behind_s"]) < 0.5 for s in both):
            time.sleep(3)
            return True
    print("VOID: the two games never settled on the craft", file=sys.stderr)
    return False


# What KSA calls a craft with something under it: standing, and what a kitten is after it has walked.
ON_THE_GROUND = ("Landed", "Rolling", "Sliding")


def in_flight(c: dict) -> bool:
    return c.get("situation") in ("Freefall", "Maneuvering") and (c.get("parts") or 0) > 1


def scene(name: str, games) -> tuple[float, list[tuple[float, str, str, dict]]] | None:
    """Sets the two games up for one of the things worth recording, and says what is done while it is."""
    if name == "orbit":
        # The craft coasts, burns for eight seconds, and coasts.
        flying = of_a(games, in_flight)
        if not flying:
            print("VOID: A has nothing in free flight to fly", file=sys.stderr)
            return None
        pick = max(flying, key=lambda c: c.get("parts") or 0)["id"]
        print(f"A flies {pick}, B watches it", flush=True)
        return (26.0, [(6.0, "A", "engine", {"on": True}), (14.0, "A", "engine", {"on": False})]) if both_on(games, pick) else None
    if name == "eva":
        # A kitten out of the hatch of a craft in free flight: one is pulled and the other stepped.
        flying = of_a(games, in_flight)
        if not flying:
            print("VOID: A has nothing in free flight to send a kitten out of", file=sys.stderr)
            return None
        pick = min(flying, key=lambda c: c.get("parts") or 0)["id"]
        print(f"A flies {pick} and sends a kitten out of it, B watches {pick}", flush=True)
        return (22.0, [(4.0, "A", "eva", {"craft": pick})]) if both_on(games, pick) else None
    if name == "meet":
        # Each flies a craft of its own, forty metres apart in one orbit.
        flying = of_a(games, in_flight)
        if not flying:
            print("VOID: A has nothing in free flight to fly", file=sys.stderr)
            return None
        pick = max(flying, key=lambda c: c.get("parts") or 0)["id"]
        # Whatever B owns or nobody does, the smallest of them: it is about to be put somewhere else.
        mine = [c for c in call(games["B"], "craft").get("craft", [])
                if not c.get("puppet") and (c.get("parts") or 0) > 1 and c["id"] != pick]
        if not mine:
            print("VOID: B has no craft of its own to bring", file=sys.stderr)
            return None
        other = min(mine, key=lambda c: c.get("parts") or 0)["id"]
        if not both_on(games, pick):
            return None
        call(games["B"], "goto", craft=other)
        time.sleep(3)
        print(f"A flies {pick}; B flies {other} and is put 40 m from it: "
              f"{json.dumps(call(games['B'], 'put', craft=other, beside=pick, metres=40))}", flush=True)
        time.sleep(8)
        return 24.0, []
    if name == "walk":
        # A kitten on the ground walks forward, then sideways, then runs, and stops.
        kittens = of_a(games, lambda c: c.get("situation") in ON_THE_GROUND and c.get("parts") == 1)
        if not kittens:
            print("VOID: A has no kitten standing on the ground", file=sys.stderr)
            return None
        pick = kittens[0]["id"]
        print(f"A walks {pick}, B watches", flush=True)
        if not both_on(games, pick):
            return None
        return 28.0, [(4.0, "A", "move", {"forward": 1}), (10.0, "A", "move", {"right": 1}),
                      (14.0, "A", "move", {"forward": 1, "sprint": True}), (21.0, "A", "move", {"stop": True})]
    if name in ("nocraft", "nocraft-warp"):
        # B flies a craft, and A is at none: its camera is on the planet, as picking one in the map puts it.
        flying = [c for c in call(games["B"], "craft").get("craft", []) if not c.get("puppet") and in_flight(c)]
        if not flying:
            print("VOID: B has nothing in free flight to fly", file=sys.stderr)
            return None
        pick = max(flying, key=lambda c: c.get("parts") or 0)
        call(games["B"], "goto", craft=pick["id"])
        print(f"B flies {pick['id']}; A is at no craft: {json.dumps(call(games['A'], 'watch', body=pick['parent']))}", flush=True)
        time.sleep(10)
        if name == "nocraft":
            return 40.0, []
        # A warp of fifty for twelve seconds, asked by the game that is flying and agreed by the other.
        return 50.0, [(8.0, "B", "mp", {"warp": 50}), (9.0, "A", "mp", {"vote": True}), (9.5, "B", "mp", {"vote": True}),
                      (21.0, "B", "mp", {"warp": 1}), (22.0, "A", "mp", {"vote": True}), (22.5, "B", "mp", {"vote": True})]
    if name in ("launch", "hop"):
        # A rocket standing on the ground is lit and flown up; for a hop it is brought down again on its engine.
        standing = of_a(games, lambda c: c.get("situation") in ON_THE_GROUND and (c.get("parts") or 0) > 5)
        if not standing:
            print("VOID: A has no rocket standing on the ground. One is put on a pad out of a craft in the world with\n"
                  "      design {\"save\":\"Rocket\"}, mp {\"editor\":true,\"blank\":true}, design {\"load\":\"Rocket\"}, mp {\"launch\":true}",
                  file=sys.stderr)
            return None
        pick = max(standing, key=lambda c: c.get("parts") or 0)["id"]
        print(f"A flies {pick} off the ground, B watches", flush=True)
        if not both_on(games, pick):
            return None
        if name == "launch":
            return 50.0, [(3.0, "A", "attitude", {"craft": pick, "track": "Up"}), (5.0, "A", "engine", {"on": True}),
                          (40.0, "A", "engine", {"on": False})]
        return 70.0, [(3.0, "A", "attitude", {"craft": pick, "track": "Up"}), (5.0, "A", "land", {"craft": pick, "climb": 120.0, "slow": 3.0})]
    if name in ("fly-dock", "fly-dock-theirs"):
        # A flies the lighter of two craft onto the other's port on its thrusters, and the ports touch.
        # A minute after that would have docked them, it undocks. B watches the craft being flown,
        # which is the craft that goes.
        ported = [c for c in call(games["A"], "craft").get("craft", []) if in_flight(c) and (c.get("ports") or 0) > 0]
        if len(ported) < 2:
            print("VOID: the world has no two craft in free flight with a docking port free", file=sys.stderr)
            return None
        ported.sort(key=lambda c: c.get("parts") or 0)
        mine, other = ported[0]["id"], ported[-1]["id"]
        if name == "fly-dock-theirs":
            # B is in the craft that is flown onto, and is still in it when it is somebody else's.
            call(games["B"], "goto", craft=other)
            time.sleep(4)
        if not both_on(games, mine, watch=name == "fly-dock"):
            return None
        print(f"A flies {mine} and is put 25 m in front of {other}'s port: "
              f"{json.dumps(call(games['A'], 'put', craft=mine, beside=other, metres=25, port=True))}", flush=True)
        # A craft put eleven thousand kilometres from where it was takes the other game a while to believe.
        time.sleep(20)
        return 300.0, [(2.0, "A", "approach", {"craft": mine, "to": other}), (200.0, "A", "approach", {}),
                       (240.0, "A", "dock", {"craft": other, "undo": True})]
    if name in ("dock", "dock-theirs"):
        # A flies a craft with a docking port beside another that has one, and docks with it. The
        # other is nobody's, or it is the one B is flying.
        ported = [c for c in call(games["A"], "craft").get("craft", []) if in_flight(c) and (c.get("ports") or 0) > 0]
        if len(ported) < 2:
            print("VOID: the world has no two craft in free flight with a docking port free", file=sys.stderr)
            return None
        ported.sort(key=lambda c: c.get("parts") or 0)
        mine, other = ported[-1]["id"], ported[0]["id"]
        if name == "dock-theirs":
            call(games["B"], "goto", craft=other)
            time.sleep(4)
        if not both_on(games, mine, watch=name == "dock"):
            return None
        print(f"A flies {mine} and is put 30 m from {other}: "
              f"{json.dumps(call(games['A'], 'put', craft=mine, beside=other, metres=30))}", flush=True)
        time.sleep(10)
        return 34.0, [(6.0, "A", "dock", {"craft": mine, "to": other})]
    if name in ("drop", "land"):
        # Lifted off the ground and let go: through the air and onto the ground, from 150 m or from 3.
        standing = of_a(games, lambda c: c.get("situation") in ON_THE_GROUND)
        if not standing:
            print("VOID: A has nothing standing on the ground", file=sys.stderr)
            return None
        # A craft before a kitten, which lands on its feet.
        pick = max(standing, key=lambda c: c.get("parts") or 0)["id"]
        print(f"A lifts {pick} and lets it go, B watches", flush=True)
        if not both_on(games, pick):
            return None
        return 24.0, [(4.0, "A", "put", {"craft": pick, "metres": 150.0 if name == "drop" else 3.0})]
    return None


def main() -> int:
    parser = argparse.ArgumentParser(description="Record where craft are in two games and lay the accounts side by side.")
    parser.add_argument("--seconds", type=float, default=20.0)
    parser.add_argument("--at", nargs=4, action="append", metavar=("SECONDS", "GAME", "COMMAND", "JSON"), default=[],
                        help="a bridge command sent to one game part-way through")
    parser.add_argument("--scene", choices=("orbit", "eva", "meet", "walk", "drop", "land", "nocraft", "nocraft-warp",
                                            "launch", "hop", "dock", "dock-theirs", "fly-dock", "fly-dock-theirs"),
                        help="set the two games up first, and do what the scene does")
    parser.add_argument("--check", metavar="RUN", help="judge a recording already taken")
    args = parser.parse_args()
    if args.check:
        return judge(Path(args.check))
    events = [(float(after), game, cmd, json.loads(text)) for after, game, cmd, text in args.at]
    seconds = args.seconds
    if args.scene:
        try:
            set_up = scene(args.scene, profiles())
        except Refused as e:
            print(f"VOID: a game would not do what the scene needs ({e})", file=sys.stderr)
            return 2
        if set_up is None:
            return 2
        seconds, events = max(seconds, set_up[0]), events + set_up[1]
    run = record(seconds, events)
    return 2 if run is None else judge(run)


if __name__ == "__main__":
    sys.exit(main())
