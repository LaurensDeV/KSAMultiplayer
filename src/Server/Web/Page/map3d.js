// The 3D whole-system view: every body where its orbit puts it now, every craft where its last word
// and its orbit put it, from system scale down to one craft. Plain canvas, no library.
//
// Everything is kept in doubles and drawn relative to whatever is in focus (a floating origin), so
// zoomed in on a craft a solar system away from the origin nothing jitters: the numbers the canvas
// is given are small, whatever the numbers they came from.
import { bodyEcl, craftNow, orbitPoints, rotate, colourOf, bodyOf, add, len } from "./map2d.js";

const sub = (a, b) => ({ x: a.x - b.x, y: a.y - b.y, z: a.z - b.z });

const BODY_COLOURS = { star: "#ffd27a", planet: "#6c9fe0", moon: "#a7adb8", minor: "#8a7a66" };

export class System3d {
  constructor(canvas, state, universeNow, controls) {
    Object.assign(this, { canvas, state, universeNow, controls });
    this.yaw = 0.6;
    this.pitch = 0.5;
    this.distance = null;       // m from the focus; null until there is something to fit
    this.focus = null;          // { kind: "body" | "craft", id }
    this.bodyOrbits = new Map();
    this.craftOrbits = new Map();
    this.drawn = [];            // what was put where, for a click to find
    this.listen();
  }

  listen() {
    let drag = null;
    this.canvas.addEventListener("pointerdown", (e) => { drag = { x: e.clientX, y: e.clientY, x0: e.clientX, y0: e.clientY, moved: false }; this.canvas.setPointerCapture(e.pointerId); });
    this.canvas.addEventListener("pointermove", (e) => {
      if (!drag) return;
      const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
      // A click that wobbles a few pixels is still a click, and turns nothing.
      if (!drag.moved && Math.abs(e.clientX - drag.x0) + Math.abs(e.clientY - drag.y0) <= 8) return;
      drag.moved = true;
      this.yaw -= dx * 0.008;
      this.pitch = Math.max(-1.55, Math.min(1.55, this.pitch + dy * 0.008));
      drag.x = e.clientX; drag.y = e.clientY;
    });
    this.canvas.addEventListener("pointerup", (e) => {
      if (drag && !drag.moved) this.pick(e.offsetX, e.offsetY);
      drag = null;
    });
    this.canvas.addEventListener("wheel", (e) => {
      e.preventDefault();
      if (this.distance) this.distance = Math.max(10, this.distance * Math.exp(e.deltaY * 0.0015));
    }, { passive: false });
    this.controls.follow.addEventListener("change", () => {
      const name = this.controls.follow.value;
      const p = this.state.view?.players.find(q => q.name === name);
      if (p?.controlling) this.focusOn({ kind: "craft", id: p.controlling });
    });
  }

  focusOn(f) {
    this.focus = f;
    // Close enough to see what it is: a body a few radii out, a craft a few kilometres.
    const b = f.kind === "body" ? bodyOf(this.state, f.id) : null;
    this.distance = b ? Math.max(b.radius, 1e3) * 6 : 5e3;
  }

  pick(x, y) {
    let best = null, bestD = 14;
    for (const d of this.drawn) {
      const dd = Math.hypot(d.x - x, d.y - y) - d.r;
      if (dd < bestD) { best = d; bestD = dd; }
    }
    if (best) {
      this.controls.follow.value = "";
      this.focusOn(best.what);
    }
  }

  /** Where something is now in the ecliptic frame. */
  where(f, u) {
    if (f.kind === "body") return bodyEcl(this.state, f.id, u);
    const c = this.state.crafts.get(f.id);
    if (!c) return null;
    const b = bodyOf(this.state, c.body);
    const at = craftNow(c, this.state, u).r;
    return add(bodyEcl(this.state, c.body, u), b ? rotate(b.cci2Cce, at) : at);
  }

  fit(canvas) {
    const dpr = window.devicePixelRatio || 1;
    const w = canvas.clientWidth, h = canvas.clientHeight;
    if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) {
      canvas.width = Math.round(w * dpr);
      canvas.height = Math.round(h * dpr);
    }
    const ctx = canvas.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    return { ctx, w, h };
  }

  draw() {
    const { ctx, w, h } = this.fit(this.canvas);
    ctx.clearRect(0, 0, w, h);
    const bodies = this.state.bodies?.bodies;
    this.controls.note.textContent = bodies ? "Drag to turn, wheel to zoom, click a body or a craft to go to it."
      : "Waiting for a game in the world to describe the system.";
    if (!bodies) return;
    const u = this.universeNow() ?? 0;

    // Nothing chosen: the biggest star, far enough out to see the planets.
    if (!this.focus) {
      const star = bodies.filter(b => b.kind === "star").sort((a, b) => b.mu - a.mu)[0] ?? bodies[0];
      this.focus = { kind: "body", id: star.id };
      const reach = Math.max(...bodies.filter(b => b.parent === star.id && b.orbit).map(b => b.orbit.periapsis), star.radius * 10);
      this.distance = reach * 2.5;
    }
    const follow = this.controls.follow.value;
    if (follow) {
      const p = this.state.view?.players.find(q => q.name === follow);
      if (p?.controlling && this.focus.id !== p.controlling) this.focus = { kind: "craft", id: p.controlling };
    }
    const origin = this.where(this.focus, u) ?? { x: 0, y: 0, z: 0 };

    // A camera turned by yaw about the ecliptic +Z and pitched above the plane, distance out.
    const cy = Math.cos(this.yaw), sy = Math.sin(this.yaw), cp = Math.cos(this.pitch), sp = Math.sin(this.pitch);
    const f = 0.9 * Math.min(w, h);
    const project = (pEcl) => {
      const p = sub(pEcl, origin);
      // Into camera axes: right, up, and depth away from the camera.
      const x1 = p.x * cy + p.y * sy, y1 = -p.x * sy + p.y * cy;
      const right = y1, up = p.z * cp - x1 * sp, fwd = x1 * cp + p.z * sp;
      const depth = this.distance - fwd;
      if (depth <= this.distance * 1e-6) return null;
      return { x: w / 2 + right / depth * f, y: h / 2 - up / depth * f, depth, scale: f / depth };
    };

    const items = [];
    this.drawn = [];

    // Orbits of the bodies, as lines through the parent's position now.
    if (this.controls.orbits.checked) {
      for (const b of bodies) {
        if (!b.orbit || b.orbit.eccentricity >= 1) continue;
        let pts = this.bodyOrbits.get(b.id);
        if (!pts) {
          const o = b.orbit, a = o.periapsis / (1 - o.eccentricity), bb = a * Math.sqrt(1 - o.eccentricity ** 2);
          pts = [];
          for (let i = 0; i <= 180; i++) {
            const E = 2 * Math.PI * i / 180;
            pts.push(rotate(o.orb2ParentCce, { x: a * (Math.cos(E) - o.eccentricity), y: bb * Math.sin(E), z: 0 }));
          }
          this.bodyOrbits.set(b.id, pts);
        }
        const centre = bodyEcl(this.state, b.parent, u);
        this.line(ctx, pts.map(p => project(add(centre, p))), "rgba(108, 159, 224, 0.35)");
      }
    }

    for (const b of bodies) {
      if (b.kind === "barycenter") continue;
      const at = project(bodyEcl(this.state, b.id, u));
      if (!at) continue;
      items.push({ depth: at.depth, draw: () => {
        const r = Math.max(2.5, b.radius * at.scale);
        const colour = BODY_COLOURS[b.kind] ?? "#9aa";
        const g = ctx.createRadialGradient(at.x - r * 0.35, at.y - r * 0.35, r * 0.1, at.x, at.y, r);
        g.addColorStop(0, colour);
        g.addColorStop(1, b.kind === "star" ? colour : "#11161f");
        ctx.fillStyle = g;
        ctx.beginPath();
        ctx.arc(at.x, at.y, Math.min(r, 1e5), 0, 2 * Math.PI);
        ctx.fill();
        if (this.controls.labels.checked && (r > 3 || b.kind === "star" || b.kind === "planet")) this.label(ctx, at.x + r + 4, at.y - 4, b.id, "#c9d1dd");
        this.drawn.push({ x: at.x, y: at.y, r, what: { kind: "body", id: b.id } });
      } });
    }

    for (const c of this.state.crafts.values()) {
      if (!c.flags.controlled && !this.controls.debris.checked && !(this.focus.kind === "craft" && this.focus.id === c.key)) continue;
      const b = bodyOf(this.state, c.body);
      const centre = bodyEcl(this.state, c.body, u);
      const now = craftNow(c, this.state, u).r;
      const at = project(add(centre, b ? rotate(b.cci2Cce, now) : now));
      if (!at) continue;
      const colour = colourOf(c.ownerName);
      if (this.controls.orbits.checked && b?.mu > 0 && !c.flags.landed) {
        let o = this.craftOrbits.get(c.key);
        if (!o || o.seq !== c.sequence) {
          o = { seq: c.sequence, pts: orbitPoints(c.posCci, c.velCci, b.mu, 96).map(p => rotate(b.cci2Cce, p)) };
          this.craftOrbits.set(c.key, o);
        }
        this.line(ctx, o.pts.map(p => project(add(centre, p))), colour, c.flags.controlled ? 0.65 : 0.25);
      }
      items.push({ depth: at.depth - 1, draw: () => {
        ctx.fillStyle = colour;
        const s = c.flags.controlled ? 4 : 2.5;
        ctx.fillRect(at.x - s, at.y - s, 2 * s, 2 * s);
        if (this.controls.labels.checked) this.label(ctx, at.x + 7, at.y - 6, c.ownerName ? `${c.key} · ${c.ownerName}` : c.key, "#e6e9ef");
        this.drawn.push({ x: at.x, y: at.y, r: s, what: { kind: "craft", id: c.key } });
      } });
    }
    for (const key of this.craftOrbits.keys()) if (!this.state.crafts.has(key)) this.craftOrbits.delete(key);

    // Far to near, so what is in front is drawn over what is behind.
    items.sort((a, b) => b.depth - a.depth).forEach(i => i.draw());

    ctx.fillStyle = "#8b93a3";
    ctx.font = "12px system-ui, sans-serif";
    ctx.fillText(`${this.focus.kind === "body" ? this.focus.id : `${this.focus.id} (craft)`} · ${distanceText(this.distance)} out`, 10, h - 10);
  }

  line(ctx, pts, colour, alpha = 1) {
    ctx.strokeStyle = colour;
    ctx.globalAlpha = alpha;
    ctx.beginPath();
    let open = false;
    for (const p of pts) {
      if (!p) { open = false; continue; }
      if (open) ctx.lineTo(p.x, p.y); else { ctx.moveTo(p.x, p.y); open = true; }
    }
    ctx.stroke();
    ctx.globalAlpha = 1;
  }

  label(ctx, x, y, text, colour) {
    ctx.font = "12px system-ui, sans-serif";
    ctx.fillStyle = colour;
    ctx.fillText(text, x, y);
  }
}

function distanceText(m) {
  if (m >= 1.496e11 * 0.1) return `${(m / 1.496e11).toFixed(2)} AU`;
  if (m >= 1e6) return `${(m / 1e3).toLocaleString(undefined, { maximumFractionDigits: 0 })} km`;
  if (m >= 1e3) return `${(m / 1e3).toFixed(1)} km`;
  return `${m.toFixed(0)} m`;
}
