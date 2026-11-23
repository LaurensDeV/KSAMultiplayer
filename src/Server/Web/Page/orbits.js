// Where things are: a craft run on along its orbit from its last word, and a body on its own.

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
  // With commas, which three.js reads as well as a canvas does; the newer form it takes for white.
  return name ? `hsl(${h % 360}, 70%, 62%)` : "hsl(220, 10%, 60%)";
}
