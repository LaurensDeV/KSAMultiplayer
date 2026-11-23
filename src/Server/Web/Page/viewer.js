// The KSA Multiplayer web viewer. Everything a server's world shows, from /api/v1/view and the live
// socket, kept in one `state` the maps draw from. Names and chat come from players: they are only
// ever written as text, never as markup.
import { System3d } from "./map3d.js";

export const state = {
  view: null,              // the newest ServerView
  crafts: new Map(),       // key -> ViewCraft, with what each craft last said
  bodies: null,            // the system, once a game has described it (see /api/v1/bodies)
  skew: 0,                 // this browser's clock less the server's, s
  live: false,
};

const $ = (id) => document.getElementById(id);
const wallNow = () => Date.now() / 1000 - state.skew;

/** Universe time now, run on from the last clock the server said. */
export function universeNow() {
  const c = state.view?.clock;
  if (!c || !c.known) return null;
  return c.universe + c.rate * (wallNow() - c.serverTime);
}

function text(el, s) { el.textContent = s; return el; }
function el(tag, s, cls) { const e = document.createElement(tag); if (s !== undefined) e.textContent = s; if (cls) e.className = cls; return e; }

function time(u) {
  if (u === null || !isFinite(u)) return "—";
  const d = Math.floor(u / 86400), h = Math.floor(u % 86400 / 3600), m = Math.floor(u % 3600 / 60), s = Math.floor(u % 60);
  const two = (n) => String(n).padStart(2, "0");
  return `${d > 0 ? d + "d " : ""}${two(h)}:${two(m)}:${two(s)}`;
}

function take(view) {
  state.view = view;
  state.skew = Date.now() / 1000 - view.wallTime;
  for (const c of view.crafts ?? []) state.crafts.set(c.key, c);
}

function takeDelta(d) {
  const v = state.view ?? {};
  state.view = { ...v, seq: d.seq, wallTime: d.wallTime, server: d.server, clock: d.clock, warp: d.warp, players: d.players, bodiesEtag: d.bodiesEtag };
  state.skew = Date.now() / 1000 - d.wallTime;
  for (const c of d.crafts) state.crafts.set(c.key, c);
  for (const k of d.removed) state.crafts.delete(k);
  if (d.bodiesEtag && d.bodiesEtag !== state.bodiesEtag) loadBodies();
}

async function loadBodies() {
  const r = await fetch("/api/v1/bodies");
  if (!r.ok) return;
  state.bodiesEtag = r.headers.get("ETag");
  state.bodies = await r.json();
}

// ---------------------------------------------------------------- status

function drawHeader() {
  const v = state.view;
  if (!v) return;
  const s = v.server;
  text($("name"), s.name);
  document.title = `${s.name} · KSA Multiplayer`;
  const facts = [`${s.players}/${s.maxPlayers} players`];
  if (s.system) facts.push(s.system);
  if (s.ksaBuild) facts.push(`KSA ${s.ksaBuild}`);
  if (s.password) facts.push("password");
  if (!s.hasWorld) facts.push("no world yet");
  facts.push(`protocol ${s.protocol}`);
  text($("facts"), facts.join(" · "));
  text($("universe"), v.clock.known ? `T+ ${time(universeNow())}` : "time not known yet");
  const w = v.warp;
  text($("warp"), w.phase === "Running" ? `${w.speed}× warp, ${w.who}'s`
    : w.phase === "Asking" ? `${w.who} asks for ${w.speed}× · waiting on ${w.waiting.join(", ") || "nobody"}`
    : v.clock.rate === 0 ? "held" : v.clock.rate !== 1 ? `${v.clock.rate}×` : "");
  $("live").classList.toggle("on", state.live);
}

function drawPlayers() {
  const v = state.view;
  if (!v) return;
  const body = $("players").tBodies[0];
  body.replaceChildren(...v.players.map(p => {
    const tr = el("tr");
    tr.append(el("td", p.name), el("td", p.rtt >= 0 ? `${Math.round(p.rtt * 1000)} ms` : "—"),
      el("td", !p.loaded ? "joining" : p.controlling || "—"), el("td", p.can > 0 ? `${p.can.toFixed(1)}×` : "—"));
    return tr;
  }));
  $("nobody").hidden = v.players.length > 0;

  const crafts = [...state.crafts.values()].sort((a, b) => (b.flags.controlled - a.flags.controlled) || a.key.localeCompare(b.key));
  $("crafts").tBodies[0].replaceChildren(...crafts.slice(0, 200).map(c => {
    const tr = el("tr");
    const f = c.flags;
    tr.append(el("td", c.key), el("td", c.ownerName || "nobody"), el("td", c.body),
      el("td", f.landed ? "landed" : f.engineOn ? "burning" : f.rails ? "coasting" : "flying"));
    return tr;
  }));
}

const chatSeen = new Set();
function addChat(line) {
  if (chatSeen.has(line.seq)) return;
  chatSeen.add(line.seq);
  const li = el("li");
  li.append(el("time", new Date(line.wallTime * 1000).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })));
  switch (line.kind) {
    case "chat":
      if (line.who) { li.append(el("span", line.who, "who"), document.createTextNode(line.text)); }
      else li.append(el("span", line.text, "server"));
      break;
    // The server says joins and leaves in the chat itself, so these would only say them twice.
    case "joined": case "left": return;
    case "gone": li.append(el("span", `${line.text} is gone`, "server")); break;
    case "dock": li.append(el("span", `${line.who} docked ${line.text}`, "server")); break;
    case "rename": li.append(el("span", `${line.who}'s ${line.text} here`, "server")); break;
    default: return;
  }
  const list = $("chat");
  const atEnd = list.scrollTop + list.clientHeight >= list.scrollHeight - 4;
  list.append(li);
  while (list.children.length > 300) list.firstChild.remove();
  if (atEnd) list.scrollTop = list.scrollHeight;
}

// ---------------------------------------------------------------- the feed

let backoff = 1;
function connect() {
  const ws = new WebSocket(`${location.protocol === "https:" ? "wss" : "ws"}://${location.host}/api/v1/live`);
  ws.onopen = () => { state.live = true; backoff = 1; };
  ws.onmessage = (m) => {
    const msg = JSON.parse(m.data);
    if (msg.type === "view") take(msg.view);
    else if (msg.type === "delta") takeDelta(msg);
    else if (msg.type === "event") addChat(msg.event);
    drawPlayers();
    fillFollow();
  };
  ws.onclose = () => {
    state.live = false;
    setTimeout(connect, backoff * 1000);
    backoff = Math.min(backoff * 2, 30);
  };
}

async function start() {
  try {
    const r = await fetch("/api/v1/view");
    if (r.ok) take(await r.json());
  } catch { }
  try {
    const r = await fetch("/api/v1/chat?after=0");
    if (r.ok) for (const line of await r.json()) addChat(line);
  } catch { }
  drawPlayers();
  loadBodies().catch(() => { });
  connect();
  frame();
}

// ---------------------------------------------------------------- tabs and maps

let tab = "status";
for (const b of document.querySelectorAll("nav button")) {
  b.onclick = () => {
    tab = b.dataset.tab;
    for (const x of document.querySelectorAll("nav button")) x.classList.toggle("on", x === b);
    for (const s of document.querySelectorAll(".tab")) s.classList.toggle("on", s.id === `tab-${tab}`);
    if (tab === "admin") session();
  };
}

const system3d = new System3d($("map3d"), $("map3d-labels"), state, universeNow, {
  orbits: $("show-orbits"), labels: $("show-labels"), debris: $("show-debris"), follow: $("follow"), note: $("system-note"),
});

function fillFollow() {
  const sel = $("follow");
  const names = (state.view?.players ?? []).filter(p => p.controlling).map(p => p.name);
  const had = sel.value;
  const now = [...sel.options].slice(1).map(o => o.value).join("\n");
  if (now !== names.join("\n")) {
    sel.replaceChildren(el("option", "nobody"), ...names.map(n => { const o = el("option", n); o.value = n; return o; }));
    sel.options[0].value = "";
    if (names.includes(had)) sel.value = had;
  }
}

function frame() {
  drawHeader();
  if (tab === "system") system3d.draw();
  requestAnimationFrame(frame);
}

// ---------------------------------------------------------------- admin

async function post(path, body) {
  const r = await fetch(path, {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-KSAMP": "1" },
    body: JSON.stringify(body ?? {}),
    credentials: "same-origin",
  });
  let json = {};
  try { json = await r.json(); } catch { }
  return { ok: r.ok, ...json };
}

async function session() {
  const r = await fetch("/api/v1/admin/session", { credentials: "same-origin" });
  const s = r.ok ? await r.json() : { on: false, in: false };
  $("admin-off").hidden = s.on;
  $("login").hidden = !s.on || s.in;
  $("console").hidden = !s.in;
}

$("login").onsubmit = async (e) => {
  e.preventDefault();
  const r = await post("/api/v1/admin/login", { password: $("password").value });
  $("password").value = "";
  text($("login-said"), r.ok ? "" : r.say ?? "could not log in");
  session();
};

$("logout").onclick = async () => { await post("/api/v1/admin/logout"); session(); };

async function run(line) {
  const out = $("output");
  out.append(document.createTextNode(`> ${line}\n`));
  const r = await post("/api/v1/admin/command", { text: line });
  if (r.status === 401 || r.say === "not logged in") { session(); }
  // Some commands answer only in the server's log, as say does: done is still worth saying.
  const lines = r.lines ?? [r.say ?? "no answer"];
  out.append(document.createTextNode((lines.length ? lines : ["done"]).join("\n") + "\n"));
  out.scrollTop = out.scrollHeight;
}

$("command").onsubmit = (e) => {
  e.preventDefault();
  const line = $("line").value.trim();
  if (!line) return;
  $("line").value = "";
  run(line);
};

for (const b of document.querySelectorAll("#quick button[data-line]")) b.onclick = () => run(b.dataset.line);
for (const b of document.querySelectorAll("#quick button[data-fill]")) b.onclick = () => { $("line").value = b.dataset.fill; $("line").focus(); };

start();
