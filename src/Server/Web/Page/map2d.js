// The 2D map: one body seen from above its pole, with every craft around it where its last word
// and its orbit put it now, and a strip of the ground for those standing on it. Plain canvas.

// ---------------------------------------------------------------- orbits

function stumpffC(z) {
  if (z > 1e-8) { const s = Math.sqrt(z); return (1 - Math.cos(s)) / z; }
  if (z < -1e-8) { const s = Math.sqrt(-z); return (Math.cosh(s) - 1) / -z; }
  return 0.5 - z / 24;
}

function stumpffS(z) {
  if (z > 1e-8) { const s = Math.sqrt(z); return (s - Math.sin(s)) / (s * s * s); }
  if (z < -1e-8) { const s = Math.sqrt(-z); return (Math.sinh(s) - s) / (s * s * s); }
  return 1 / 6 - z / 120;
}

const dot = (a, b) => a.x * b.x + a.y * b.y + a.z * b.z;
const len = (a) => Math.sqrt(dot(a, a));
const add = (a, b) => ({ x: a.x + b.x, y: a.y + b.y, z: a.z + b.z });
const mul = (a, k) => ({ x: a.x * k, y: a.y * k, z: a.z * k });

/**
 * Where a body on a two-body orbit is dt seconds on from (r0, v0), by the universal variable: one
 * equation for ellipses, parabolas and hyperbolas alike. Null where it does not converge.
 */
export function propagate(r0, v0, mu, dt) {
  const R0 = len(r0), V0 = len(v0);
  if (!(R0 > 0) || !(mu > 0) || !isFinite(dt)) return null;
  const sqmu = Math.sqrt(mu);
  const vr0 = dot(r0, v0) / R0;
  const alpha = 2 / R0 - V0 * V0 / mu;
  let x = sqmu * Math.abs(alpha) * dt;
  if (!isFinite(x) || x === 0) x = sqmu * dt / R0;
  for (let i = 0; i < 60; i++) {
    const z = alpha * x * x, C = stumpffC(z), S = stumpffS(z);
    const F = R0 * vr0 / sqmu * x * x * C + (1 - alpha * R0) * x * x * x * S + R0 * x - sqmu * dt;
    const dF = R0 * vr0 / sqmu * x * (1 - z * S) + (1 - alpha * R0) * x * x * C + R0;
    const step = F / dF;
    x -= step;
    if (!isFinite(x)) return null;
    if (Math.abs(step) < 1e-9 * Math.max(1, Math.abs(x))) break;
  }
  const z = alpha * x * x, C = stumpffC(z), S = stumpffS(z);
  const f = 1 - x * x / R0 * C, g = dt - x * x * x * S / sqmu;
  const r = add(mul(r0, f), mul(v0, g));
  const R = len(r);
  const fd = sqmu / (R * R0) * (alpha * x * x * x * S - x), gd = 1 - x * x / R * C;
  return { r, v: add(mul(r0, fd), mul(v0, gd)) };
}

/** The orbit through (r, v) as points, a whole period of an ellipse or a stretch either side of now for anything else. */
export function orbitPoints(r, v, mu, n = 128) {
  const alpha = 2 / len(r) - dot(v, v) / mu;
  const pts = [];
  if (alpha > 0) {
    const period = 2 * Math.PI * Math.sqrt(1 / (alpha * alpha * alpha) / mu);
    for (let i = 0; i <= n; i++) { const p = propagate(r, v, mu, period * i / n); if (p) pts.push(p.r); }
  } else {
    const span = 4 * len(r) / Math.max(len(v), 1);
    for (let i = -n / 2; i <= n / 2; i++) { const p = propagate(r, v, mu, span * i / (n / 2)); if (p) pts.push(p.r); }
  }
  return pts;
}

/** A body's mu and radius where a game has described the system, else nothing. */
export function bodyOf(state, id) {
  return state.bodies?.bodies?.find(b => b.id === id) ?? null;
}

/**
 * Where a craft is now in its body's inertial frame. On rails it follows its orbit; on the ground
 * it stays where it stood (turned with the body where the body's spin is known); otherwise its
 * last word runs on in a straight line, for no more than two seconds.
 */
export function craftNow(c, state, u) {
  const r0 = c.posCci, v0 = c.velCci;
  if (u === null) return { r: r0, v: v0 };
  const dt = u - c.universe;
  const body = bodyOf(state, c.body);
  if (c.flags.landed) {
    const turned = body ? ccfToCci(body, c.posCcf, u) : null;
    return { r: turned ?? r0, v: v0 };
  }
  if (c.flags.rails && body?.mu > 0) {
    const p = propagate(r0, v0, body.mu, dt);
    if (p) return p;
  }
  const t = Math.max(-2, Math.min(2, dt));
  return { r: add(r0, mul(v0, t)), v: v0 };
}

/**
 * A body-fixed position turned into the body's inertial frame at universe time u: KSA spins every
 * body about its own inertial +Z, by spinRate·u + spinPhase. Null for a body that does not spin.
 */
export function ccfToCci(body, p, u) {
  if (!body || !isFinite(body.spinRate)) return null;
  const th = body.spinRate * u + (body.spinPhase ?? 0), c = Math.cos(th), s = Math.sin(th);
  return { x: p.x * c - p.y * s, y: p.x * s + p.y * c, z: p.z };
}

/** A vector turned by a unit quaternion {x, y, z, w}, as q·v·q⁻¹. */
export function rotate(q, v) {
  const tx = 2 * (q.y * v.z - q.z * v.y), ty = 2 * (q.z * v.x - q.x * v.z), tz = 2 * (q.x * v.y - q.y * v.x);
  return {
    x: v.x + q.w * tx + (q.y * tz - q.z * ty),
    y: v.y + q.w * ty + (q.z * tx - q.x * tz),
    z: v.z + q.w * tz + (q.x * ty - q.y * tx),
  };
}

/** Where a body is on its orbit at universe time u, from its parent, in ecliptic axes. */
export function orbitPosition(o, u) {
  const e = o.eccentricity, dt = u - o.timeAtPeriapsis;
  let x, y;
  if (e < 1) {
    const a = o.periapsis / (1 - e), n = 2 * Math.PI / o.period;
    let M = (n * dt) % (2 * Math.PI);
    if (M > Math.PI) M -= 2 * Math.PI; else if (M < -Math.PI) M += 2 * Math.PI;
    let E = M + (e < 0.8 ? Math.sign(Math.sin(M)) * 0.85 * e : Math.cbrt(6 * M * e) || M);
    for (let i = 0; i < 50; i++) {
      const step = Math.max(-1, Math.min(1, (E - e * Math.sin(E) - M) / (1 - e * Math.cos(E))));
      E -= step;
      if (Math.abs(step) < 1e-10) break;
    }
    x = a * (Math.cos(E) - e);
    y = a * Math.sqrt(1 - e * e) * Math.sin(E);
  } else {
    const a = o.periapsis / (1 - e), n = Math.sqrt(o.mu / Math.pow(-a, 3)), M = n * dt;
    let H = Math.asinh(M / e);
    for (let i = 0; i < 50; i++) {
      const step = (e * Math.sinh(H) - H - M) / (e * Math.cosh(H) - 1);
      H -= step;
      if (Math.abs(step) < 1e-10) break;
    }
    x = -a * (e - Math.cosh(H));
    y = -a * Math.sqrt(e * e - 1) * Math.sinh(H);
  }
  return rotate(o.orb2ParentCce, { x, y, z: 0 });
}

/** Where a body is at universe time u, in the ecliptic frame every body's position adds up in. */
export function bodyEcl(state, id, u, depth = 0) {
  const b = bodyOf(state, id);
  if (!b || depth > 300) return { x: 0, y: 0, z: 0 };
  if (!b.parent || !b.orbit) return b.position ?? { x: 0, y: 0, z: 0 };
  return add(bodyEcl(state, b.parent, u, depth + 1), orbitPosition(b.orbit, u));
}

export { add, mul, len };

/** A colour of its own for each owner, the same on every page. */
export function colourOf(name) {
  let h = 0;
  for (const ch of name || "") h = (h * 31 + ch.charCodeAt(0)) >>> 0;
  return name ? `hsl(${h % 360} 70% 62%)` : "hsl(220 10% 60%)";
}

// Every body's inertial frame has its pole on +Z, so a body seen from above its pole is x and y.
const ax = "x", ay = "y";

// ---------------------------------------------------------------- drawing

export class Map2d {
  constructor(canvas, ground, state, universeNow) {
    Object.assign(this, { canvas, ground, state, universeNow });
    this.orbits = new Map();   // craft key -> { seq, pts }
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

  draw(bodyId) {
    const { ctx, w, h } = this.fit(this.canvas);
    ctx.clearRect(0, 0, w, h);
    const crafts = [...this.state.crafts.values()].filter(c => c.body === bodyId);
    const body = bodyOf(this.state, bodyId);
    const u = this.universeNow();

    const placed = crafts.map(c => ({ c, at: craftNow(c, this.state, u) }));
    // Scaled to the body and the furthest craft, with a margin.
    let reach = body?.radius ?? 0;
    for (const p of placed) reach = Math.max(reach, Math.hypot(p.at.r[ax], p.at.r[ay]));
    if (!(reach > 0)) reach = 1;
    const k = 0.45 * Math.min(w, h) / reach;
    const cx = w / 2, cy = h / 2;
    const X = (p) => cx + p[ax] * k, Y = (p) => cy - p[ay] * k;

    const radius = body?.radius;
    ctx.fillStyle = "#26324a";
    ctx.strokeStyle = "#3d4d6e";
    ctx.beginPath();
    ctx.arc(cx, cy, radius ? radius * k : 4, 0, 2 * Math.PI);
    ctx.fill();
    ctx.stroke();
    if (body?.atmosphere > 0) {
      ctx.strokeStyle = "rgba(108, 182, 255, 0.25)";
      ctx.beginPath();
      ctx.arc(cx, cy, (radius + body.atmosphere) * k, 0, 2 * Math.PI);
      ctx.stroke();
    }

    ctx.font = "12px system-ui, sans-serif";
    for (const { c, at } of placed) {
      const colour = colourOf(c.ownerName);
      if (body?.mu > 0 && !c.flags.landed) {
        let o = this.orbits.get(c.key);
        if (!o || o.seq !== c.sequence) {
          o = { seq: c.sequence, pts: orbitPoints(c.posCci, c.velCci, body.mu) };
          this.orbits.set(c.key, o);
        }
        ctx.strokeStyle = colour;
        ctx.globalAlpha = c.flags.controlled ? 0.7 : 0.3;
        ctx.beginPath();
        o.pts.forEach((p, i) => i ? ctx.lineTo(X(p), Y(p)) : ctx.moveTo(X(p), Y(p)));
        ctx.stroke();
        ctx.globalAlpha = 1;
      }
      ctx.fillStyle = colour;
      ctx.beginPath();
      ctx.arc(X(at.r), Y(at.r), c.flags.controlled ? 4.5 : 2.5, 0, 2 * Math.PI);
      ctx.fill();
      if (c.flags.controlled || crafts.length <= 12) {
        ctx.fillStyle = "#e6e9ef";
        ctx.fillText(c.ownerName ? `${c.key} · ${c.ownerName}` : c.key, X(at.r) + 7, Y(at.r) - 6);
      }
    }
    for (const key of this.orbits.keys()) if (!this.state.crafts.has(key)) this.orbits.delete(key);

    ctx.fillStyle = "#8b93a3";
    ctx.fillText(`${bodyId || "no body"} · ${crafts.length} craft${body ? "" : " · no system description yet, so no orbits"}`, 10, h - 10);
    this.drawGround(crafts);
  }

  /** Craft standing on the body, by latitude and longitude off where they stand in its own frame. */
  drawGround(crafts) {
    const { ctx, w, h } = this.fit(this.ground);
    ctx.clearRect(0, 0, w, h);
    ctx.strokeStyle = "#2a303c";
    for (let lon = -180; lon <= 180; lon += 30) { const x = (lon + 180) / 360 * w; ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, h); ctx.stroke(); }
    for (let lat = -60; lat <= 60; lat += 30) { const y = (90 - lat) / 180 * h; ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(w, y); ctx.stroke(); }
    ctx.font = "12px system-ui, sans-serif";
    for (const c of crafts.filter(c => c.flags.landed)) {
      const p = c.posCcf, r = Math.hypot(p.x, p.y, p.z);
      if (!(r > 0)) continue;
      const lat = Math.asin(p.z / r) * 180 / Math.PI, lon = Math.atan2(p.y, p.x) * 180 / Math.PI;
      const x = (lon + 180) / 360 * w, y = (90 - lat) / 180 * h;
      ctx.fillStyle = colourOf(c.ownerName);
      ctx.beginPath();
      ctx.arc(x, y, 3.5, 0, 2 * Math.PI);
      ctx.fill();
      ctx.fillStyle = "#e6e9ef";
      ctx.fillText(c.key, x + 6, y - 4);
    }
  }
}
