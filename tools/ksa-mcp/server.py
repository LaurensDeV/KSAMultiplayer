#!/usr/bin/env python3
"""An MCP server that lets an agent drive a running KSA through the mod's bridge.

The mod reads commands from <KSA user dir>/Logs/bridge/KSAMultiplayer/in and answers in .../out
(src/Mod/Bridge.cs): files, so nothing needs a port and it works across the WSL boundary. This turns
that folder into tools -- join a server, fly a named craft, step the world, photograph it -- and hands
pictures back inline.

KSA_USER_DIR names a profile, so two games on one machine are two players:

    KSA_USER_DIR="$(./tools/two-games.sh --dir A)" python3 tools/ksa-mcp/server.py cli status '{}'

Stdlib and Pillow only: MCP is JSON-RPC over stdin and stdout, one message a line.

    python3 tools/ksa-mcp/server.py                     # serve MCP on stdio
    python3 tools/ksa-mcp/server.py cli status          # one tool from a shell
    python3 tools/ksa-mcp/server.py cli mp '{"connect":"127.0.0.1:50100","name":"Alpha"}'
"""

from __future__ import annotations

import base64
import io
import json
import os
import signal
import subprocess
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

_user_dir: Path | None = None


def user_dir() -> Path:
    global _user_dir
    # KSA_USER_DIR names another profile, such as a second game's, for two players on one machine.
    if _user_dir is None and os.environ.get("KSA_USER_DIR"):
        _user_dir = Path(os.environ["KSA_USER_DIR"])
    if _user_dir is None:
        out = subprocess.run([str(REPO / "tools" / "ksa-user-dir.sh")], capture_output=True, text=True)
        _user_dir = Path(out.stdout.strip())
    return _user_dir


def bridge_of(profile: Path) -> Path:
    # The mod's own subfolder, so another mod's bridge never answers a command meant for this one.
    return profile / "Logs" / "bridge" / "KSAMultiplayer"


def bridge() -> Path:
    return bridge_of(user_dir())


def game_running() -> bool:
    try:
        out = subprocess.run(["tasklist.exe"], capture_output=True, text=True, timeout=15).stdout.lower()
    except (OSError, subprocess.TimeoutExpired):
        return False
    return "starmap.exe" in out or "kittenspaceagency" in out


class Refused(Exception):
    pass


def wsl(path: str) -> str:
    """A path the mod wrote, from Windows, as this side reads it."""
    if len(path) > 2 and path[1] == ":" and path[2] in "\\/":
        return f"/mnt/{path[0].lower()}/" + path[3:].replace("\\", "/")
    return path


def send(cmd: str, timeout: float = 30.0, **args) -> dict:
    """One command to the mod, and its reply. Raises Refused with the mod's own reason."""
    return send_to(bridge(), cmd, timeout, **args)


def send_to(folder: Path, cmd: str, timeout: float = 30.0, **args) -> dict:
    """One command to the game whose bridge this folder is, so one process can drive two games."""
    inbox, outbox = folder / "in", folder / "out"
    inbox.mkdir(parents=True, exist_ok=True)
    ident = f"{time.time_ns()}"
    body = {"id": ident, "cmd": cmd, **{k: v for k, v in args.items() if v is not None}}

    part = inbox / f"{ident}.json.part"
    part.write_text(json.dumps(body))
    part.rename(inbox / f"{ident}.json")

    reply_path = outbox / f"{ident}.json"
    deadline = time.time() + timeout
    while time.time() < deadline:
        if reply_path.exists():
            try:
                reply = json.loads(reply_path.read_text())
            except json.JSONDecodeError:
                time.sleep(0.05)
                continue
            reply_path.unlink(missing_ok=True)
            if not reply.get("ok"):
                raise Refused(reply.get("error") or "refused")
            return reply.get("data") or {}
        time.sleep(0.05)

    (inbox / f"{ident}.json").unlink(missing_ok=True)
    raise Refused(f"no reply to '{cmd}' in {timeout:.0f} s -- is the game running with this build?")


# ---- the game's lifetime -------------------------------------------------------------------

STATE = Path(__file__).resolve().parent / ".launched"


def launch(save: str | None = None) -> str:
    """Starts the game if none is running. Never a second one, and never over the player's."""
    if game_running():
        note = "a game is already running; using it"
    else:
        log = open(Path(__file__).resolve().parent / "launch.log", "w")
        proc = subprocess.Popen([str(REPO / "tools" / "run.sh")], cwd=REPO, stdout=log,
                                stderr=subprocess.STDOUT, start_new_session=True)
        STATE.write_text(str(proc.pid))
        note = "launched"

    deadline = time.time() + 300
    while True:
        try:
            send("status", timeout=5)
            break
        except Refused:
            if time.time() > deadline:
                raise Refused("the game never answered on the bridge")
            time.sleep(2)

    if save:
        data = send("load", timeout=90, save=save)
        note += f"; loaded '{save}', flying {data.get('craft')}"
    return note


def quit_game() -> str:
    """Closes a game this server launched, and refuses any other: that one is somebody's session."""
    if not STATE.exists():
        raise Refused("this server did not launch the running game; it is the player's to close")
    pid = int(STATE.read_text())
    try:
        os.killpg(pid, signal.SIGTERM)
    except ProcessLookupError:
        pass
    STATE.unlink(missing_ok=True)
    return "closed"


# ---- captures ------------------------------------------------------------------------------

def capture(label: str = "shot", frames: int = 1, every_frames: int | None = None,
            width: int = 960, ui: bool | None = None) -> list[dict]:
    """Pictures and what each one was of."""
    data = send("capture", timeout=60 + frames * 10, label=label, frames=frames,
                every_frames=every_frames, ui=ui)
    shots = data["frames"]
    content: list[dict] = [_text("\n".join(
        f"{s.get('file')}\n  " + ", ".join(
            f"{k}={s[k]}" for k in ("label", "universe", "stage", "craft", "puppets") if k in s)
        for s in shots))]
    for shot in shots:
        content.append(_image(wsl(shot["file"]), width))
    return content


def _text(text: str) -> dict:
    return {"type": "text", "text": text}


def _image(path: str, width: int) -> dict:
    """A PNG the mod wrote, as a JPEG small enough to read in a conversation."""
    from PIL import Image

    img = Image.open(path).convert("RGB")
    if img.width > width:
        img = img.resize((width, max(1, round(img.height * width / img.width))), Image.LANCZOS)
    buffer = io.BytesIO()
    img.save(buffer, "JPEG", quality=85)
    return {"type": "image", "data": base64.b64encode(buffer.getvalue()).decode(), "mimeType": "image/jpeg"}


def log_tail(pattern: str = "", lines: int = 40) -> str:
    text = (user_dir() / "Logs" / "KSAMultiplayer.log").read_text(errors="replace").splitlines()
    if pattern:
        text = [t for t in text if pattern.lower() in t.lower()]
    return "\n".join(text[-lines:])


# ---- the tools -----------------------------------------------------------------------------

def _num(desc):
    return {"type": "number", "description": desc}


def _dump(data) -> list[dict]:
    return [_text(json.dumps(data, indent=1))]


TOOLS = {
    "ksa_status": ("What this game is doing and what it shares: scene, flown craft, universe time, the "
                   "session's stage, the server and its players, who the server says flies what, which of "
                   "those craft stand here as puppets and how old each one's state is.", {}, [],
                   lambda a: _dump(send("status"))),
    "ksa_craft": ("Every craft in this world: its Id, body, situation, parts, whether it has physics, and "
                  "whether this game owns it or holds it as another player's puppet.", {}, [],
                  lambda a: _dump(send("craft"))),
    "ksa_mp": ("Join a server (connect, as host:port or a host for port 50100, with name and password), "
               "leave it (disconnect=true), or type a line into the chat (chat), which goes to the "
               "server as a command when it starts with a / and to everyone otherwise. With no "
               "argument it answers as ksa_status does.",
               {"connect": {"type": "string"}, "name": {"type": "string"}, "password": {"type": "string"},
                "disconnect": {"type": "boolean"}, "chat": {"type": "string"},
                "editor": {"type": "boolean", "description": "open or close the vehicle editor, which the mod behaves differently in"},
                "blank": {"type": "boolean", "description": "with editor=true, open it empty even while flying, as KSA's Build New Vehicle does"},
                "launch": {"type": "boolean", "description": "press the editor's Launch, which puts what is in it on a pad"},
                "pads": {"type": "boolean", "description": "what the pad picker sees: the site chosen, its pads, and which is free"},
                "board": _num("show the player list for this many seconds, as holding Tab does"),
                "warp": _num("ask everyone for the world to run at this speed; 0 pauses"),
                "vote": {"type": "boolean", "description": "answer the warp somebody asked for"}}, [],
               lambda a: _dump(send("mp", **a))),
    "ksa_goto": ("Fly the craft of this Id and point the camera at it, so each of two games flies a "
                 "different craft out of the same world.", {"craft": {"type": "string"}}, ["craft"],
                lambda a: _dump(send("goto", **a))),
    "ksa_eva": ("Send a kitten out of a craft's hatch: the named craft, or the flown one, and the named "
                "kitten or the first aboard. The shortest way to make a craft the loaded world did not have.",
                {"craft": {"type": "string"}, "kitten": {"type": "string"}}, [],
                lambda a: _dump(send("eva", **a))),
    "ksa_watch": ("Put the camera on a craft without taking it over, which is the only thing anyone can "
                  "do with a craft another player flies. Or on a 'body', as picking a planet in the map does: "
                  "KSA lets go of the craft being flown, which is a game at no craft at all.",
                  {"craft": {"type": "string"}, "body": {"type": "string"}}, [],
                  lambda a: _dump(send("watch", **a))),
    "ksa_warpkey": ("Press a warp key: by 1 faster, -1 slower, 0 for normal speed. While connected the "
                    "mod works the speed out from the warp the server is running, not this world's.",
                    {"by": _num("1, -1 or 0")}, [],
                    lambda a: _dump(send("warpkey", **a))),
    "ksa_stage": ("Fire a craft's next sequence, which is what the space bar does: the named craft, or the "
                  "flown one. The way to drop a stage while another game is watching.",
                  {"craft": {"type": "string"}}, [],
                  lambda a: _dump(send("stage", **a))),
    "ksa_setorbit": ("Put a craft into a circular orbit of a body, as the debug menu's Set Orbit window "
                     "does: the named craft, or the flown one. The only way to make a craft change the "
                     "body it is going round without flying there, which is what a test of whether the "
                     "other games follow it needs.",
                     {"craft": {"type": "string"}, "body": {"type": "string"},
                      "altitude": _num("metres above the mean radius")}, ["body"],
                     lambda a: _dump(send("setorbit", **a))),
    "ksa_modspeed": ("Write the simulation speed straight at KSA, the way another mod does, with none "
                     "of the guard ksa_speed keeps. For testing whether a program's write is put to "
                     "the room as though the player had asked for it.",
                     {"x": _num("the speed to write")}, ["x"],
                     lambda a: _dump(send("modspeed", **a))),
    "ksa_pickspeed": ("Pick a simulation speed the way the player picks one from KSA's own speed menu: "
                      "an input event carrying a speed. The other half of ksa_modspeed, and the half "
                      "that should reach the room.",
                      {"x": _num("the speed to pick")}, ["x"],
                      lambda a: _dump(send("pickspeed", **a))),
    "ksa_engine": ("Light or shut a craft's engine, which is the ignition key and the throttle: the named "
                   "craft, or the flown one. For making a craft burn while another game watches it.",
                   {"craft": {"type": "string"}, "on": {"type": "boolean"}, "throttle": _num("0 to 1")}, [],
                   lambda a: _dump(send("engine", **a))),
    "ksa_launch": ("Start KSA with this tree's deployed build if it is not running, and optionally load a "
                   "save. Uses a running game rather than starting a second.",
                   {"save": {"type": "string"}}, [], lambda a: [_text(launch(a.get("save")))]),
    "ksa_quit": ("Close the game, only if this server launched it.", {}, [], lambda a: [_text(quit_game())]),
    "ksa_start": ("Press Start on KSA's configuration at start: a fresh world with the game's own "
                  "starting craft in it and no save read. Does nothing once a world is up.",
                  {"system": {"type": "string"}}, [],
                  lambda a: _dump(send("start", timeout=200, **a))),
    "ksa_load": ("Load a save by name. From the configuration at start this presses Start first, so "
                 "it works on a game that has only just opened.", {"save": {"type": "string"}}, ["save"],
                 lambda a: _dump(send("load", timeout=200, save=a["save"]))),
    "ksa_save": ("Write this world to a save of this name.", {"name": {"type": "string"}}, ["name"],
                 lambda a: _dump(send("save", **a))),
    "ksa_fail": ("Blow parts off a craft, or crash it (whole=true, which sheds debris and destroys it), "
                 "through the same event KSA's own G-load path raises: the named craft, or the flown one. "
                 "The only way to reach a part failure on the ground, where KSA stands that path down for "
                 "anything touching terrain, ocean or another craft. Aimed at a puppet, it is how a run "
                 "watches the mod refuse to strip another player's craft. Applied on the next frame, so "
                 "read what came of it from ksa_craft and KSA's own log.",
                 {"craft": {"type": "string"}, "parts": _num("how many parts to fail"),
                  "whole": {"type": "boolean", "description": "destroy the craft outright, as a crash does"}}, [],
                 lambda a: _dump(send("fail", **a))),
    "ksa_design": ("The vehicle editor's designs. With no argument, what designs there are and what the "
                   "editor holds; save=<craft> writes a craft standing in this world out as a design of "
                   "the same name; load=<design> puts one into the open editor. Opening the editor with "
                   "nothing flown (ksa_mp editor=true), loading a design and then ksa_mp launch=true "
                   "builds and launches a craft with no hand on the mouse. No part can be placed.",
                   {"save": {"type": "string"}, "load": {"type": "string"}}, [],
                   lambda a: _dump(send("design", **a))),
    "ksa_pause": ("Pause the world. Refused while connected: the server's clock holds the speed.", {}, [],
                  lambda a: _dump(send("pause"))),
    "ksa_resume": ("Resume the world. Refused while connected.", {}, [], lambda a: _dump(send("resume"))),
    "ksa_speed": ("Set the simulation speed. Refused while connected.", {"x": _num("speed multiple")}, ["x"],
                  lambda a: _dump(send("speed", x=a["x"]))),
    "ksa_step": ("Run the world for so many simulated seconds, then pause. Refused while connected.",
                 {"seconds": _num("sim seconds")}, ["seconds"],
                 lambda a: _dump(send("step", timeout=a["seconds"] * 20 + 40, seconds=a["seconds"]))),
    "ksa_capture": ("Screenshot the game. frames>1 takes a series, every_frames rendered frames apart. "
                    "ui=true keeps the interface in the picture, which KSA otherwise hides.",
                    {"label": {"type": "string"}, "frames": _num("count"),
                     "every_frames": _num("frames"), "width": _num("px"),
                     "ui": {"type": "boolean", "description": "keep the interface, to see the mod's window"}}, [],
                    lambda a: capture(**a)),
    "ksa_motion": ("Record where every craft this game owns or is sent stands, at every frame and physics "
                   "step: start=true begins, stop=true ends and names the file. tools/motion.py records "
                   "both games at once and lays the two accounts side by side.",
                   {"start": {"type": "boolean"}, "stop": {"type": "boolean"}}, [],
                   lambda a: _dump(send("motion", timeout=60, **a))),
    "ksa_move": ("Hold a craft's translation keys, or let them go (stop=true): forward, right and up are "
                 "1, -1 or 0. A kitten on the ground walks, and sprints with sprint=true; a craft "
                 "fires its thrusters. The named craft, or the flown one. Held until told otherwise.",
                 {"craft": {"type": "string"}, "forward": _num("1, -1 or 0"), "right": _num("1, -1 or 0"),
                  "up": _num("1, -1 or 0"), "sprint": {"type": "boolean"}, "stop": {"type": "boolean"}}, [],
                 lambda a: _dump(send("move", **a))),
    "ksa_put": ("Put a craft beside another (beside), so many metres further out and moving as it "
                "moves: two craft in one place with nobody flying a rendezvous. With no beside it is "
                "lifted that far above where it stands and let go. With 'port' it is put that far out in "
                "front of the other's free docking port, which is where an approach starts.",
                {"craft": {"type": "string"}, "beside": {"type": "string"}, "metres": _num("how far out"),
                 "port": {"type": "boolean"}}, [],
                lambda a: _dump(send("put", **a))),
    "ksa_attitude": ("Tell a craft's flight computer what to point at, as the buttons on KSA's panel do: "
                     "Up, Prograde, Retrograde, RadialOut and the rest, or none to hand it back. The named "
                     "craft, or the flown one.",
                     {"craft": {"type": "string"}, "track": {"type": "string"}}, [],
                     lambda a: _dump(send("attitude", **a))),
    "ksa_land": ("Bring a craft down on its engine, lighting and shutting it, since KSA holds a throttle to "
                 "the engine's minimum: 'climb' flies it up that many metres first, 'slow' is the slowing the "
                 "descent is planned on in m/s^2, 'touch' the last speed. With nothing, how it is doing.",
                 {"craft": {"type": "string"}, "climb": _num("m"), "slow": _num("m/s^2"), "touch": _num("m/s"),
                  "stop": {"type": "boolean"}}, [],
                 lambda a: _dump(send("land", **a))),
    "ksa_dock": ("Dock two craft port to port wherever they are, through the event KSA's own contact "
                 "raises: the lighter is taken into the heavier. With 'undo', undock the craft at its first "
                 "docked port, as the port's own menu does.",
                 {"craft": {"type": "string"}, "to": {"type": "string"}, "undo": {"type": "boolean"}}, [],
                 lambda a: _dump(send("dock", **a))),
    "ksa_approach": ("Fly a craft onto another's docking port on its thrusters: their port targeted, the craft "
                     "controlled from its own, the flight computer lining the two up, each translation key "
                     "tried to learn what it does and then held as needed. Nothing here docks them: the ports "
                     "touch and KSA does. 'final' is the last closing speed in m/s, 'hold' how far out it "
                     "waits to be on the port's line, 'fly' false only aims and measures. With nothing, how it "
                     "is going.",
                     {"craft": {"type": "string"}, "to": {"type": "string"}, "final": _num("m/s"), "hold": _num("m"),
                      "seconds": _num("s"), "fly": {"type": "boolean"}, "stop": {"type": "boolean"}}, [],
                     lambda a: _dump(send("approach", **a))),
    "ksa_log": ("The mod's log, filtered.", {"pattern": {"type": "string"}, "lines": _num("count")}, [],
                lambda a: [_text(log_tail(a.get("pattern", ""), int(a.get("lines", 40))))]),
}


def call(name: str, args: dict) -> tuple[list[dict], bool]:
    if name not in TOOLS:
        return [_text(f"no tool {name}")], True
    try:
        return TOOLS[name][3](args or {}), False
    except Refused as e:
        return [_text(str(e))], True
    except Exception as e:  # noqa: BLE001 -- a tool that throws must still answer
        return [_text(f"{type(e).__name__}: {e}")], True


def serve() -> None:
    def reply(ident, result=None, error=None):
        if ident is None:
            return
        body = {"jsonrpc": "2.0", "id": ident}
        body["error" if error else "result"] = error or result
        sys.stdout.write(json.dumps(body) + "\n")
        sys.stdout.flush()

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            message = json.loads(line)
        except json.JSONDecodeError:
            continue

        method, ident, params = message.get("method"), message.get("id"), message.get("params") or {}

        if method == "initialize":
            reply(ident, {"protocolVersion": "2024-11-05", "capabilities": {"tools": {}},
                          "serverInfo": {"name": "ksa", "version": "1.0.0"}})
        elif method == "tools/list":
            reply(ident, {"tools": [
                {"name": name,
                 "description": spec[0],
                 "inputSchema": {"type": "object", "properties": spec[1], "required": spec[2]}}
                for name, spec in TOOLS.items()]})
        elif method == "tools/call":
            content, failed = call(params.get("name", ""), params.get("arguments") or {})
            reply(ident, {"content": content, "isError": failed})
        elif method and method.startswith("notifications/"):
            continue
        elif ident is not None:
            reply(ident, error={"code": -32601, "message": f"no method {method}"})


def cli(argv: list[str]) -> int:
    if not argv:
        print(__doc__)
        print("tools: " + ", ".join(sorted(t.removeprefix("ksa_") for t in TOOLS)))
        return 2
    name = argv[0] if argv[0].startswith("ksa_") else "ksa_" + argv[0]
    args = json.loads(argv[1]) if len(argv) > 1 else {}
    content, failed = call(name, args)
    for part in content:
        if part["type"] == "text":
            print(part["text"])
        else:
            print(f"<{part['mimeType']}, {len(part['data'])} b64 chars>")
    return 1 if failed else 0


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "cli":
        sys.exit(cli(sys.argv[2:]))
    serve()
