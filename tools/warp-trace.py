#!/usr/bin/env python3
"""Two games and the server sampled together through a scripted walk of warps, and judged as a series.

    ./tools/two-games.sh --test          # a server and two games, both in one world
    ./tools/warp-trace.py                # walk every transition and judge the trace
    ./tools/warp-trace.py --only rails   # some of the walk; --list names the steps
    ./tools/warp-trace.py --check <run>  # judge a trace already taken, without a game

Exits 0 when every claim held, 1 when one did not, and 2 when the rig was not fit to measure.

tools/breakup.sh freezes both worlds and compares them, which is right for what a world holds and
blind to the clock: every fault there lives in a transition -- a rate going up, coming down, being
governed, ending -- and a snapshot taken after one is a snapshot of the recovery. So this samples
instead, twice a second, and its claims are made over the whole series. docs/WARP-AND-WIRE.md has
the reasoning and the list this walks.

Every time here is Windows's time of day and none of them is this script's own. Each game answers
`status` with the time of day it answered at beside its universe time, and the server writes its
clock to a file with the time of day on every line (--trace, which two-games.sh passes), on both
sides of every change. Games and server are processes on one machine, so that is one clock in all
three, and where the server's universe time was at the instant a game answered is worked out from
the two lines either side of it. A game's own `behind_s` is reported beside that and never judged
on: it is the thing under test.

This side's clock is no part of it because under WSL it is not that clock. Measured on the machine
this was written on, it ran two percent fast and was stepped back six tenths of a second every half
minute, which laid one step's warp over the step before it. So where a step begins is asked of a
game, and the waits are counted on a clock that only has to go forwards.

Every claim is one whose healthy value is zero:

  * STOPPED -- a game whose universe time does not move while the server's does, for longer than
    a nudge takes. The bug of 2026-11-09, stated so that it fails the run.
  * LATE -- a game further from the server's clock, in seconds of the rate then running, than a
    few, for longer than it takes a healthy one to come back: three seconds ahead, and behind,
    three more than the catch-up it is meant to make takes. Through every transition and not only in the
    steady state.
  * RATE -- a game that says the room is running at a speed the server has not said lately.
  * BACKWARDS -- universe time going down, in a game or on the server.
  * and what each step says of itself: a warp that ran without everyone's yes or outlived a no, a
    warp steered or ended by somebody who did not ask for it, a speed above what its asker asked,
    a speed written by a program reaching the room, a speed picked by a player not reaching it.

Whether a step ran with this game's world on rails or in physics is reported and not arranged:
it decides how fast a game may catch up, a thousand times against ten, and the only thing here that
could put a craft in physics on purpose is an engine, which the bridge can light and cannot
throttle.

What this does not walk: a player leaving their craft mid-warp, joining or leaving mid-warp, a
save loaded mid-warp, and a machine made to look slow so the governor has something to do. And
what no run on one machine reaches: load, two machines, and a third player.
"""

from __future__ import annotations

import argparse
import bisect
import json
import math
import os
import re
import shutil
import statistics
import subprocess
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE / "tools" / "ksa-mcp"))
from server import Refused, bridge_of, send_to  # noqa: E402

SERVER_TRACE = HERE / ".two-games-server.trace"
SERVER_LOG = HERE / ".two-games-server.log"

# The names two-games.sh joins the two profiles under.
PLAYERS = {"A": "Alpha", "B": "Bravo"}

# What two-games.sh starts the server with: how long an ask waits for its answers, s.
ANSWER_SECONDS = 3.0
# The server's own: how long after a warp ends before anybody may ask for another, s.
QUIET_SECONDS = 5.0

# The bounds, which are the part of this to argue about. A game more than three seconds ahead is
# held still by design until the clock reaches it, and one behind is nudged back over a few, so a
# healthy room is inside both of these with room to spare and the bug they exist for was outside
# them by two orders of magnitude.
STOPPED_SECONDS = float(os.environ.get("KSAMP_TRACE_STOPPED_S", "5"))
LATE_SECONDS = float(os.environ.get("KSAMP_TRACE_LATE_S", "6"))
# And for how long. Ahead, a game stands still for exactly as long as it is ahead, so the two are
# one number. Behind they are not: the end of a warp turns a tenth of a second at 1000x into a
# hundred seconds at 1x on the instant, and what matters is whether a game then makes them up as
# fast as it is meant to. ClockFollow runs a world a gap behind at one and half the gap times the
# server's speed, which halves the gap every 1.4 s, so a healthy game is allowed that long to come
# inside the bound from the worst it was and this much over.
LATE_FOR_SECONDS = float(os.environ.get("KSAMP_TRACE_LATE_FOR_S", "3"))
CATCH_UP_SECONDS = 2.0
# How long a game may go on saying what the server said a moment ago. The server repeats the warp
# once a second, so this is one repeat lost and the next one late.
AGREE_SECONDS = float(os.environ.get("KSAMP_TRACE_AGREE_S", "2.5"))

KEPT = ("wall", "loaded", "stage", "popups", "in_flight", "craft", "watching", "here", "can", "achieved",
        "analytic", "speed", "held_speed", "universe", "behind_s", "rtt_ms", "arriving", "warp")


class Game:
    def __init__(self, name: str, folder: Path):
        self.name, self.player, self.folder = name, PLAYERS[name], folder

    def call(self, cmd: str, timeout: float = 15.0, **args) -> dict:
        return send_to(bridge_of(self.folder), cmd, timeout, **args)

    def tried(self, cmd: str, **args) -> dict:
        """A command whose refusal is something to write down rather than a reason to stop."""
        try:
            return self.call(cmd, **args)
        except Refused as e:
            return {"refused": str(e)}


def profiles() -> dict[str, Game]:
    out = subprocess.run([str(HERE / "tools" / "two-games.sh"), "--dir", "A"], capture_output=True, text=True)
    root = Path(out.stdout.strip()).parent
    return {name: Game(name, root / name) for name in PLAYERS}


def last_server_line() -> dict | None:
    """What the server last wrote of itself: the phase a step waits on before it goes on."""
    try:
        with open(SERVER_TRACE, "rb") as f:
            f.seek(0, os.SEEK_END)
            f.seek(max(0, f.tell() - 8192))
            lines = f.read().decode("utf-8", "replace").splitlines()
    except OSError:
        return None
    for line in reversed(lines):
        try:
            return json.loads(line)
        except json.JSONDecodeError:
            continue
    return None


# ---- sampling ------------------------------------------------------------------------------

class Sampler(threading.Thread):
    """Both games asked what they are doing, together, twice a second, for as long as the walk lasts."""

    def __init__(self, games: dict[str, Game], out: Path, every: float = 0.5):
        super().__init__(daemon=True)
        self.games, self.every, self.rows = games, every, []
        self.done = threading.Event()
        self.file = open(out, "w", encoding="utf-8")

    def read(self, game: Game) -> dict:
        sent = time.monotonic()
        try:
            status = game.call("status", timeout=4.0)
        except Refused as e:
            return {"error": str(e), "took": round(time.monotonic() - sent, 3)}
        row = {k: status.get(k) for k in KEPT}
        row["took"] = round(time.monotonic() - sent, 3)
        return row

    def run(self) -> None:
        due = time.monotonic()
        with ThreadPoolExecutor(len(self.games)) as pool:
            while not self.done.is_set():
                asked = {name: pool.submit(self.read, game) for name, game in self.games.items()}
                row = {name: f.result() for name, f in asked.items()}
                self.rows.append(row)
                self.file.write(json.dumps(row) + "\n")
                self.file.flush()
                due += self.every
                # Never a burst to make up for a slow answer: a late sample is a late sample.
                if due < time.monotonic():
                    due = time.monotonic()
                self.done.wait(max(0.0, due - time.monotonic()))

    def stop(self) -> None:
        self.done.set()
        self.join(timeout=10)
        self.file.close()

    def latest(self, name: str) -> dict:
        return self.rows[-1][name] if self.rows else {}


# ---- the walk ------------------------------------------------------------------------------

class Void(Exception):
    """The rig is not fit to measure: nothing after this is worth reading."""


class Walk:
    def __init__(self, games: dict[str, Game], sampler: Sampler):
        self.games, self.sampler = games, sampler
        self.a, self.b = games["A"], games["B"]
        self.segments: list[dict] = []
        self.said: list[str] = []

    def now(self) -> float:
        """The time of day as a game has it, which is as the server has it and not as this side does."""
        for game in self.games.values():
            if (wall := game.tried("status").get("wall")) is not None:
                return wall
        raise Void("neither game says what time it is")

    def begin(self, name: str, claims: bool = True, **expect) -> None:
        """Everything from here to the next one is this segment, and is held to what it expects."""
        # One line in all three logs at the same instant, said the way a player says it.
        self.a.tried("mp", chat=f"-- {name} --")
        # Asked after the line and before whatever the step does next, so nothing a step does can
        # fall in the step before it.
        now = self.now()
        if self.segments:
            self.segments[-1]["w1"] = now
        self.segments.append({"name": name, "w0": now, "w1": now, "claims": claims, "expect": expect})
        print(f"  {name}", flush=True)

    def end(self) -> None:
        if self.segments:
            self.segments[-1]["w1"] = self.now()

    def note(self, text: str) -> None:
        self.said.append(text)
        print(f"      {text}", flush=True)

    def hold(self, seconds: float) -> None:
        time.sleep(seconds)

    def phase(self, wanted: str, within: float) -> bool:
        """Waits for the server itself to say it, since a game's word for it is what is under test."""
        until = time.monotonic() + within
        while time.monotonic() < until:
            line = last_server_line()
            if line and line.get("phase") == wanted:
                return True
            time.sleep(0.1)
        return False

    def ask(self, game: Game, speed: float) -> None:
        game.tried("mp", warp=speed)

    def agree(self, asker: Game, voter: Game, speed: float) -> bool:
        # An ask and a vote are each one datagram, said once, and a wire that loses one in fifty
        # loses these too. A player sees nothing happen and presses again, so this does, and says
        # that it had to: it is the walk that is being kept honest here, not the wire.
        for again in range(3):
            self.ask(asker, speed)
            if self.phase("Asking", 3.0) or self.phase("Running", 0.5):
                break
            self.note(f"{asker.name}'s ask for {speed:g}x was not heard, and is made again")
        for again in range(3):
            if (last_server_line() or {}).get("phase") != "Asking":
                break
            voter.tried("mp", vote=True)
            if self.phase("Running", 2.0):
                break
            self.note(f"{voter.name}'s yes was not heard, and is said again")
        return self.phase("Running", 3.0)

    # -- before anything is claimed --

    def prepare(self) -> None:
        self.begin("getting both games to a craft", claims=False)
        taken: set[str] = set()
        for game in self.games.values():
            status = game.call("status")
            if status.get("craft"):
                taken.add(status["craft"])
                continue
            # A game that is not at a craft is forced to 1x and reports no lateness at all, so it
            # takes no part in a warp and nothing measured of it means anything.
            theirs = {o["craft"] for o in status.get("owners") or [] if not o.get("mine")}
            free = [c for c in game.call("craft").get("craft", [])
                    if not c.get("puppet") and (c.get("key") or c["id"]) not in theirs and c["id"] not in taken]
            if not free:
                raise Void(f"{game.name} has no craft it could fly")
            # Standing on something before flying: a craft on the ground is one KSA works out in
            # closed form, and it is still there after an hour of warp. The rocket this once took
            # instead was on its way down, and a thousand times real time put it in the sea.
            pick = max(free, key=lambda c: (c.get("situation") == "Landed", c.get("parts") or 0))
            game.call("goto", craft=pick["id"])
            taken.add(pick["id"])
            self.note(f"{game.name} takes {pick['id']}")

        still, until = 0, time.monotonic() + 120
        while time.monotonic() < until and still < 8:
            time.sleep(0.5)
            now = [self.sampler.latest(name) for name in self.games]
            good = all(s.get("craft") and s.get("stage") == "Playing" and s.get("behind_s") is not None
                       and abs(s["behind_s"]) < 2.0 and not s.get("popups") for s in now)
            still = still + 1 if good else 0
        if still < 8:
            raise Void("the two games never settled at a craft, playing and within two seconds of the server")
        for name in self.games:
            s = self.sampler.latest(name)
            self.note(f"{name} flies {s.get('craft')}, round trip {s.get('rtt_ms')} ms")

    # -- the steps, each of which leaves the room at normal speed and past the quiet --

    def baseline(self) -> None:
        self.begin("baseline: nobody asks for anything", phase="None")
        self.hold(10)

    def modspeed(self) -> None:
        # The way another mod writes a speed: straight at KSA. It is not this player's word, so the
        # room must not hear of it, and the clock must have this world back within the segment.
        self.begin("B's speed is written by a program, at 50x", phase="None", back={"B": 2.0})
        self.note(f"the write answered {self.b.tried('modspeed', x=50)}")
        self.hold(8)

    def pickspeed(self) -> None:
        # The way the player picks one from KSA's own menu, which is theirs to ask the room for.
        self.begin("B picks 5x from the game's own menu", sees=("Asking", "Bravo"), never=("Running",))
        self.note(f"the pick answered {self.b.tried('pickspeed', x=5)}")
        self.phase("Asking", 3.0)
        self.a.tried("mp", vote=False)
        self.begin("A says no to it", phase="None", settle=2.0, never=("Running",))
        self.hold(QUIET_SECONDS + 2)

    def ten(self) -> None:
        self.begin("A asks for 10x, B says yes", phase="Running", settle=2.0, who="Alpha", ceiling=10)
        if not self.agree(self.a, self.b, 10):
            self.note("the warp never ran")
        self.hold(15)
        self.begin("A ends it", phase="None", settle=2.0)
        self.ask(self.a, 1)
        self.hold(20)

    def no(self) -> None:
        self.begin("A asks for 5x, B says no", phase="None", settle=2.0, never=("Running",))
        self.ask(self.a, 5)
        self.phase("Asking", 3.0)
        self.b.tried("mp", vote=False)
        self.hold(QUIET_SECONDS + 3)

    def silence(self) -> None:
        self.begin("A asks for 5x, B says nothing", phase="None", settle=ANSWER_SECONDS + 2.0, never=("Running",))
        self.ask(self.a, 5)
        self.hold(ANSWER_SECONDS + QUIET_SECONDS + 3)

    def steer(self) -> None:
        self.begin("A asks for 4x, B says yes", phase="Running", settle=2.0, who="Alpha", ceiling=4)
        if not self.agree(self.a, self.b, 4):
            self.note("the warp never ran")
        self.hold(8)
        self.begin("A changes it to 8x", phase="Running", who="Alpha", ceiling=8)
        self.ask(self.a, 8)
        self.hold(8)
        self.begin("B, who did not ask for it, asks for 50x", phase="Running", who="Alpha", ceiling=8)
        self.ask(self.b, 50)
        self.hold(4)
        self.begin("B asks for it to end", phase="Running", who="Alpha", ceiling=8)
        self.ask(self.b, 1)
        self.hold(4)
        # Written down to normal speed, which from the asker's own hand would end the warp.
        self.begin("A's speed is written by a program, at 1x", phase="Running", who="Alpha", ceiling=8)
        self.note(f"the write answered {self.a.tried('modspeed', x=1)}")
        self.hold(4)
        self.begin("B's speed is written by a program, at 100x", phase="Running", who="Alpha", ceiling=8)
        self.note(f"the write answered {self.b.tried('modspeed', x=100)}")
        self.hold(4)
        self.begin("A ends it", phase="None", settle=2.0)
        self.ask(self.a, 1)
        self.hold(1)
        # Not judged, because the server's own comment and its own code say different things about
        # whoever had the warp asking again inside the quiet. Written down as it happened.
        self.begin("A asks again a second later, inside the quiet", watch=True)
        self.ask(self.a, 4)
        self.hold(4)
        line = last_server_line() or {}
        if line.get("phase") == "Asking":
            self.b.tried("mp", vote=False)
        elif line.get("phase") == "Running":
            self.ask(self.a, 1)
        self.begin("and the room is left alone", phase="None", settle=ANSWER_SECONDS + 1.0)
        self.hold(QUIET_SECONDS + 4)

    def keys(self) -> None:
        # The keys and not an ask for a number: a key says which way, and the mod works the number
        # out. A warp starts below what was asked and climbs, so a second press a moment after the
        # first is pressed while the world is still on its way to the first.
        self.begin("A presses faster once, B says yes", phase="Running", settle=2.0, who="Alpha")
        self.note(f"the key answered {self.a.tried('warpkey', by=1)}")
        self.phase("Asking", 3.0)
        self.b.tried("mp", vote=True)
        self.phase("Running", 5.0)
        self.begin("A presses faster twice more, at once", phase="Running", who="Alpha", rises=2)
        self.a.tried("warpkey", by=1)
        time.sleep(0.3)
        self.note(f"the last key answered {self.a.tried('warpkey', by=1)}")
        self.hold(8)
        self.begin("A presses the key for normal speed", phase="None", settle=2.0)
        self.a.tried("warpkey", by=0)
        self.hold(QUIET_SECONDS + 2)

    def thousand(self) -> None:
        self.begin("A asks for 1000x, B says yes", phase="Running", settle=2.0, who="Alpha", ceiling=1000)
        if not self.agree(self.a, self.b, 1000):
            self.note("the warp never ran")
        # Long enough to climb there a step at a time and then be seen to stay.
        self.hold(45)
        # Where the bug of 2026-11-09 lived: the ending makes a lead of the round trip at the old
        # rate, and at the new one that lead was minutes of a game that would not run.
        self.begin("A ends it", phase="None", settle=2.0)
        self.ask(self.a, 1)
        self.hold(45)


STEPS = ("baseline", "modspeed", "pickspeed", "ten", "no", "silence", "steer", "keys", "thousand")


# ---- judging -------------------------------------------------------------------------------

class Server:
    """The server's own account of its clock, and where that clock was at any instant in it."""

    def __init__(self, lines: list[dict]):
        self.lines = sorted(lines, key=lambda l: l["t"])
        self.times = [l["t"] for l in self.lines]

    def between(self, t0: float, t1: float) -> list[dict]:
        return self.lines[bisect.bisect_left(self.times, t0):bisect.bisect_left(self.times, t1)]

    def at(self, t: float) -> dict | None:
        """The line in force at an instant: a change is written from both sides, so one always is."""
        i = bisect.bisect_right(self.times, t) - 1
        return self.lines[i] if 0 <= i < len(self.lines) else None

    def universe(self, t: float) -> float | None:
        i = bisect.bisect_right(self.times, t) - 1
        if i < 0 or i + 1 >= len(self.lines):
            return None
        a, b = self.lines[i], self.lines[i + 1]
        span = b["t"] - a["t"]
        return a["universe"] if span <= 0 else a["universe"] + (b["universe"] - a["universe"]) * (t - a["t"]) / span

    def jumps(self, t0: float, t1: float) -> list[float]:
        """Where the clock moved by more than its own rate carried it: the clock keeping up with the room."""
        out, lines = [], self.between(t0, t1)
        for a, b in zip(lines, lines[1:]):
            moved = b["universe"] - a["universe"] - a["rate"] * (b["t"] - a["t"])
            if abs(moved) > 0.5 + 0.02 * max(a["rate"], b["rate"]):
                out.append(moved)
        return out


def judge(run: Path) -> int:
    rows = [json.loads(l) for l in (run / "games.jsonl").read_text().splitlines() if l.strip()]
    server = Server([json.loads(l) for l in (run / "server.jsonl").read_text().splitlines() if l.strip()])
    about = json.loads((run / "walk.json").read_text())
    segments, voids, fails, notes = about["segments"], list(about.get("voids", [])), [], list(about.get("notes", []))
    out: list[str] = []

    def say(text: str = "") -> None:
        out.append(text)

    def segment_of(wall: float) -> dict | None:
        return next((s for s in segments if s["w0"] <= wall < s["w1"]), None)

    if not server.lines:
        voids.append("the server wrote no trace: start the rig from this tree, which passes it --trace")
    timeouts = sum(1 for row in rows for n in PLAYERS if "error" in row[n])
    if timeouts:
        # Not the mod's failure on its own, but a game that does not answer was not measured.
        (voids if timeouts > len(rows) * 0.1 else notes).append(f"{timeouts} of {len(rows) * 2} samples went unanswered")

    say(f"{len(rows)} samples of each game and {len(server.lines)} lines from the server, "
        f"over {(segments[-1]['w1'] - segments[0]['w0']) if segments else 0:.0f} s.")
    say(f"the server under test was the {about.get('server_on', '?')} build; the games were "
        + ("on loopback." if about.get("lag_ms", "0") == "0" else f"{about['lag_ms']} ms from it each way."))
    if about.get("lag_ms", "0") != "0" and about.get("relay"):
        say(f"  the relay: {about['relay']}")
    if about.get("server_on") != "linux":
        notes.append("a Windows server was tested, not the Linux one this repository is for")
    if about.get("lag_ms", "0") == "0":
        notes.append("loopback, so nothing proportional to the round trip was tested")
    say()

    # Every sample of a game against where the server's clock was at the instant it was taken.
    series: dict[str, list[dict]] = {n: [] for n in PLAYERS}
    for row in rows:
        for name in PLAYERS:
            g = row[name]
            if "error" in g or g.get("wall") is None or g.get("universe") is None:
                continue
            line, there = server.at(g["wall"]), server.universe(g["wall"])
            if line is None or there is None:
                continue
            behind = there - g["universe"]
            series[name].append({
                "wall": g["wall"], "universe": g["universe"], "speed": g.get("speed"), "rate": line["rate"],
                "there": there, "behind": behind, "late": behind / max(line["rate"], 1.0),
                "own": g.get("behind_s"), "playing": g.get("stage") == "Playing", "craft": g.get("craft"),
                "warp": g.get("warp"), "rails": bool(g.get("analytic")), "segment": segment_of(g["wall"]),
            })

    # -- STOPPED --
    stopped: dict[str, list[tuple[float, float, str]]] = {n: [] for n in PLAYERS}
    for name, samples in series.items():
        since = None
        for a, b in zip(samples, samples[1:]):
            claimed = a["segment"] and a["segment"]["claims"] and a["playing"] and b["playing"]
            still = (claimed and b["universe"] - a["universe"] < 0.005 and a["rate"] > 0 and b["rate"] > 0
                     and b["there"] - a["there"] > 0.05)
            if still and since is None:
                since = a
            if since is not None and (not still or b is samples[-1]):
                end = b if still else a
                stopped[name].append((end["wall"] - since["wall"], since["wall"], since["segment"]["name"]))
                since = None
        for seconds, _, where in stopped[name]:
            if seconds > STOPPED_SECONDS:
                fails.append(f"STOPPED  {name} stood still for {seconds:.0f} s while the server's clock ran, in '{where}'")

    # -- LATE --
    longest_late: dict[str, float] = {n: 0.0 for n in PLAYERS}
    for name, samples in series.items():
        judged = [s for s in samples if s["segment"] and s["segment"]["claims"] and s["playing"] and s["craft"]]
        worst, since, stretch = None, None, None
        for s in judged + [None]:
            past = s is not None and abs(s["late"]) > LATE_SECONDS
            if past:
                since = since or s
                if worst is None or abs(s["late"]) > abs(worst["late"]):
                    worst = s
                if stretch is None or s["wall"] - since["wall"] > stretch[0]:
                    stretch = (s["wall"] - since["wall"], since)
            else:
                since = None
        if worst is None:
            continue
        longest_late[name] = stretch[0]
        allowed = LATE_FOR_SECONDS
        if worst["late"] > 0:
            allowed += CATCH_UP_SECONDS * math.log(worst["late"] / LATE_SECONDS)
        text = (f"{name} was {abs(worst['late']):.0f} s {'behind' if worst['late'] > 0 else 'ahead of'} the server at its "
                f"worst, in '{worst['segment']['name']}', and past {LATE_SECONDS:.0f} s for {stretch[0]:.1f} s at a stretch")
        if stretch[0] > allowed:
            fails.append(f"LATE  {text}, against {allowed:.1f} s allowed")
        else:
            notes.append(text)

    # -- RATE --
    lag = float(about.get("lag_ms", "0") or 0) / 1000.0
    for name, samples in series.items():
        wrong = []
        for s in samples:
            if not (s["segment"] and s["segment"]["claims"] and s["playing"]):
                continue
            warp = s["warp"] or {}
            mine = (warp.get("phase") or "None", warp.get("speed"))
            lately = server.between(s["wall"] - AGREE_SECONDS - 2 * lag, s["wall"] + 0.1)
            if (start := server.at(s["wall"] - AGREE_SECONDS - 2 * lag)) is not None:
                lately = [start] + lately
            if not any(l["phase"] == mine[0] and (mine[0] == "None" or mine[1] is None
                                                  or abs(l["speed"] - mine[1]) <= 0.002 * max(1.0, l["speed"]) + 0.002)
                       for l in lately):
                wrong.append(s)
        if wrong:
            first = wrong[0]
            had = server.at(first["wall"])
            fails.append(f"RATE  {name} said the room was at {(first['warp'] or {}).get('phase') or 'None'} "
                         f"{(first['warp'] or {}).get('speed')}x when the server had {had['phase']} {had['speed']}x, "
                         f"in '{first['segment']['name']}', and in {len(wrong)} samples in all")

    # -- BACKWARDS --
    for name, samples in series.items():
        back = [(a, b) for a, b in zip(samples, samples[1:]) if b["universe"] < a["universe"] - 0.01]
        if back:
            a, b = back[0]
            fails.append(f"BACKWARDS  {name}'s universe time went from {a['universe']:.1f} to {b['universe']:.1f}, "
                         f"{len(back)} time{'s' if len(back) > 1 else ''}")
    back = [(a, b) for a, b in zip(server.lines, server.lines[1:]) if b["universe"] < a["universe"] - 0.01]
    if back:
        a, b = back[0]
        fails.append(f"BACKWARDS  the server's universe time went from {a['universe']:.1f} to {b['universe']:.1f}, "
                     f"{len(back)} time{'s' if len(back) > 1 else ''}")

    # -- what each segment says of itself --
    for s in segments:
        expect, lines = s["expect"], server.between(s["w0"], s["w1"])
        if not lines and expect and server.lines:
            voids.append(f"'{s['name']}': the server wrote nothing in it")
            continue
        late = [l for l in lines if l["t"] >= s["w0"] + expect.get("settle", 0.0)]
        if (want := expect.get("phase")) and (off := [l for l in late if l["phase"] != want]):
            fails.append(f"'{s['name']}': the server should have been at {want} and was at {off[0]['phase']} "
                         f"{off[0]['speed']}x, {off[0]['t'] - s['w0']:.1f} s in")
        for phase in expect.get("never", ()):
            if any(l["phase"] == phase for l in lines):
                fails.append(f"'{s['name']}': the server was {phase}, which it never should have been")
        if (sees := expect.get("sees")) and not any(l["phase"] == sees[0] and l["who"] == sees[1] for l in lines):
            fails.append(f"'{s['name']}': the server was never {sees[0]} for {sees[1]}")
        if (who := expect.get("who")) and (off := [l for l in lines if l["phase"] != "None" and l["who"] != who]):
            fails.append(f"'{s['name']}': the warp was {off[0]['who']}'s and should have been {who}'s")
        if (ceiling := expect.get("ceiling")) and (off := [l for l in lines if l["phase"] != "None" and l["speed"] > ceiling * 1.001]):
            fails.append(f"'{s['name']}': the room ran at {max(l['speed'] for l in off)}x, above the {ceiling}x asked for")
        if (rises := expect.get("rises")):
            wanted = [l.get("wanted") for l in lines if l.get("wanted") is not None]
            rose = sum(1 for a, b in zip(wanted, wanted[1:]) if b > a + 0.001)
            if rose < rises:
                fails.append(f"'{s['name']}': what the warp was going to rose {rose} time{'' if rose == 1 else 's'}, "
                             f"to {max(wanted, default=0):g}x, and each of {rises} presses should have raised it")
        for name, most in (expect.get("back") or {}).items():
            # Asked of the end of the segment: the write may take for a frame, and must not stick.
            tail = [x for x in series[name] if x["segment"] is s][-3:]
            if tail and any(x["speed"] is not None and x["speed"] > most * max(x["rate"], 1.0) for x in tail):
                fails.append(f"'{s['name']}': {name} was still running at {tail[-1]['speed']}x at the end of it")
        if expect.get("watch"):
            seen = []
            for l in lines:
                what = f"{l['phase']}{'' if l['phase'] == 'None' else ' ' + str(l['speed']) + 'x for ' + l['who']}"
                if not seen or seen[-1] != what:
                    seen.append(what)
            notes.append(f"'{s['name']}': the server went {' -> '.join(seen)}")

    # -- the table --
    say("step                                             server                "
        + "".join(f"  {n}:  behind/ahead   stopped   " for n in PLAYERS))
    for s in segments:
        lines = server.between(s["w0"], s["w1"])
        phases = []
        for l in lines:
            if not phases or phases[-1] != l["phase"]:
                phases.append(l["phase"])
        speeds = [l["speed"] for l in lines if l["phase"] == "Running"]
        what = ">".join(p[:3] for p in phases) + (f" {min(speeds):.3g}-{max(speeds):.3g}x" if speeds else "")
        if (jumps := server.jumps(s["w0"], s["w1"])):
            what += f" +{sum(jumps):.0f}s"
        cells = ""
        for name in PLAYERS:
            mine = [x for x in series[name] if x["segment"] is s and x["playing"]]
            longest = max((sec for sec, _, where in stopped[name] if where == s["name"]), default=0.0)
            if mine:
                on = {x["rails"] for x in mine}
                cells += (f"  {max(x['late'] for x in mine):+7.2f}/{min(x['late'] for x in mine):+7.2f} s"
                          f"  {longest:5.1f} s {'rails' if on == {True} else 'phys ' if on == {False} else 'both '}")
            else:
                cells += "        --/--            --       "
        say(f"{s['name'][:47]:48s} {what[:21]:21s}{cells}{'' if s['claims'] else '   (not judged)'}")
    say()
    say("behind and ahead are against the server's own clock at the instant each sample was taken, in")
    say("seconds of the rate then running; +N s after a speed is the clock keeping up with the room;")
    say("rails or phys is whether everything in that game's world was on rails or standing on something.")

    # How well each game knows where the server's clock is, which every one of its decisions rests on.
    for name, samples in series.items():
        off = [abs(s["own"] - s["behind"]) / max(s["rate"], 1.0) for s in samples
               if s["own"] is not None and s["playing"] and s["craft"] and s["segment"] and s["segment"]["claims"]]
        if off:
            say(f"{name}'s own reading of how far behind it was differed from that by {statistics.median(off):.2f} s "
                f"at the median and {max(off):.2f} s at worst.")
    for name in PLAYERS:
        total = sum(sec for sec, _, _ in stopped[name])
        if total:
            say(f"{name} stood still for {total:.1f} s in all while the server's clock ran, "
                f"{max(sec for sec, _, _ in stopped[name]):.1f} s at the longest.")
    say()

    for text in about.get("said", []):
        say(f"  {text}")
    if about.get("server_said"):
        say()
        say("the server said")
        for text in about["server_said"]:
            say(f"  {text}")
    say()

    # The logs, sorted as tools/breakup.sh sorts them: the rig broke, the mod broke.
    for name in PLAYERS:
        for line in about.get("logs", {}).get(name, []):
            if re.search(r"ErrorOutOfDeviceMemory|KSA has moved a method this mod patches", line):
                voids.append(f"{name}: {line[:120]}")
            elif re.search(r"faults: KSA Multiplayer is switched off| ERROR ", line):
                fails.append(f"{name}: {line[:120]}")

    say("-" * 78)
    for label, bucket in (("VOID", voids), ("FAIL", fails), ("NOTE", notes)):
        for line in dict.fromkeys(bucket):
            say(f"{label}  {line}")
    if not voids and not fails:
        say("every claim held. A clean run shows nothing broken, not a race cured.")
    say(f"\nthe trace and the logs are in {run}")

    text = "\n".join(out)
    print(text)
    (run / "report.txt").write_text(text + "\n")
    return 2 if voids else (1 if fails else 0)


# ---- a run ---------------------------------------------------------------------------------

def read_from(path: Path, offset: int) -> list[str]:
    try:
        with open(path, "rb") as f:
            f.seek(offset)
            return f.read().decode("utf-8", "replace").splitlines()
    except OSError:
        return []


def size(path: Path) -> int:
    try:
        return path.stat().st_size
    except OSError:
        return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Sample two games and the server through a walk of warps.")
    parser.add_argument("--only", help="steps to walk, separated by commas")
    parser.add_argument("--list", action="store_true", help="name the steps")
    parser.add_argument("--check", metavar="RUN", help="judge a trace already taken")
    args = parser.parse_args()

    if args.list:
        print("\n".join(STEPS))
        return 0
    if args.check:
        return judge(Path(args.check))
    steps = [s.strip() for s in args.only.split(",")] if args.only else list(STEPS)
    if unknown := [s for s in steps if s not in STEPS]:
        print(f"no step {', '.join(unknown)}; --list names them", file=sys.stderr)
        return 2

    games = profiles()
    run = HERE / ".two-games-runs" / time.strftime("%Y%m%dT%H%M%SZ-warp", time.gmtime())
    run.mkdir(parents=True)

    # Is the rig fit to measure at all? A different thing from a claim failing, and said differently.
    bad, wall = [], None
    for game in games.values():
        try:
            s = game.call("status", timeout=10)
        except Refused:
            bad.append(f"{game.name}: the bridge did not answer -- is the game up?")
            continue
        wall = s.get("wall")
        if wall is None:
            bad.append(f"{game.name}: this game's build says nothing of the time of day; deploy this tree")
        if not s.get("loaded"):
            bad.append(f"{game.name}: no world is loaded")
        if s.get("popups"):
            bad.append(f"{game.name}: a popup is over the game ({', '.join(s['popups'])})")
        if not s.get("server"):
            bad.append(f"{game.name}: not joined to a server")
        if s.get("arriving") is not None:
            bad.append(f"{game.name}: still receiving the world")
    line = last_server_line()
    if line is None or wall is None or abs(line["t"] - wall) > 5:
        bad.append("the server is writing no trace -- it was not started by this tree's two-games.sh, or has stopped")
    if bad:
        for text in bad:
            print(f"VOID: {text}")
        print("the rig did not come up; run ./tools/two-games.sh --test first", file=sys.stderr)
        return 2

    mark = {name: size(game.folder / "Logs" / "KSAMultiplayer.log") for name, game in games.items()}
    server_mark = size(SERVER_LOG)
    began = wall

    sampler = Sampler(games, run / "games.jsonl")
    sampler.start()
    walk = Walk(games, sampler)
    voids: list[str] = []
    print("walking:")
    try:
        walk.prepare()
        for step in steps:
            getattr(walk, step)()
    except Void as e:
        voids.append(str(e))
    except Refused as e:
        voids.append(f"a game stopped answering: {e}")
    finally:
        walk.end()
        # However it ended, nobody is left warping.
        line = last_server_line() or {}
        if line.get("phase") == "Running":
            games["A"].tried("mp", warp=1)
            games["B"].tried("mp", warp=1)
        sampler.stop()
    print()

    lines = []
    for text in read_from(SERVER_TRACE, 0):
        try:
            l = json.loads(text)
        except json.JSONDecodeError:
            continue
        if l["t"] >= began - 30:
            lines.append(text)
    (run / "server.jsonl").write_text("\n".join(lines) + "\n")

    def read_text(path: Path, fallback: str) -> str:
        try:
            return path.read_text().strip() or fallback
        except OSError:
            return fallback

    logs = {name: read_from(game.folder / "Logs" / "KSAMultiplayer.log", mark[name]) for name, game in games.items()}
    for name, game in games.items():
        shutil.copyfile(game.folder / "Logs" / "KSAMultiplayer.log", run / f"{name}.mod.log")
    shutil.copyfile(SERVER_LOG, run / "server.log")
    (run / "walk.json").write_text(json.dumps({
        "steps": steps,
        "segments": walk.segments,
        "said": walk.said,
        "voids": voids,
        "server_on": read_text(HERE / ".two-games-server.on", "linux"),
        "lag_ms": read_text(HERE / ".two-games-lag.ms", "0"),
        # The relay's own first line, which is the only place that says how much the delay varied
        # and what share was thrown away.
        "relay": (read_from(HERE / ".two-games-relay.log", 0) or [""])[0],
        # Only what could void or fail the run, since the whole log is beside this.
        "logs": {name: [l for l in text if re.search(r"ErrorOutOfDeviceMemory|KSA has moved a method| ERROR |faults:", l)]
                 for name, text in logs.items()},
        "server_said": [l for l in read_from(SERVER_LOG, server_mark)
                        if re.search(r"warp|the world (runs|falls|rises)|asks|clock|agreed|said no|answered", l)],
    }, indent=1))
    return judge(run)


if __name__ == "__main__":
    sys.exit(main())
