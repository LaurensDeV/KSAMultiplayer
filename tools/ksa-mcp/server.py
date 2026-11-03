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


def bridge() -> Path:
    # The mod's own subfolder, so another mod's bridge never answers a command meant for this one.
    return user_dir() / "Logs" / "bridge" / "KSAMultiplayer"


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
    inbox, outbox = bridge() / "in", bridge() / "out"
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
               "leave it (disconnect=true), or say something to everyone (chat). With no argument it "
               "answers as ksa_status does.",
               {"connect": {"type": "string"}, "name": {"type": "string"}, "password": {"type": "string"},
                "disconnect": {"type": "boolean"}, "chat": {"type": "string"},
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
    "ksa_launch": ("Start KSA with this tree's deployed build if it is not running, and optionally load a "
                   "save. Uses a running game rather than starting a second.",
                   {"save": {"type": "string"}}, [], lambda a: [_text(launch(a.get("save")))]),
    "ksa_quit": ("Close the game, only if this server launched it.", {}, [], lambda a: [_text(quit_game())]),
    "ksa_load": ("Load a save by name.", {"save": {"type": "string"}}, ["save"],
                 lambda a: _dump(send("load", timeout=90, save=a["save"]))),
    "ksa_save": ("Write this world to a save of this name.", {"name": {"type": "string"}}, ["name"],
                 lambda a: _dump(send("save", **a))),
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
