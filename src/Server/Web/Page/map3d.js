// The 3D whole-system view: every body where its orbit puts it now, every craft where its last word
// and its orbit put it, from system scale down to one craft. three.js, vendored beside this file.
//
// Everything is kept in doubles here and handed to the GPU relative to whatever is in focus (a
// floating origin), with a logarithmic depth buffer: in float32 world coordinates a craft a
// solar system from the origin would jitter by kilometres, and a linear depth buffer could not
// hold a craft a few metres away and a star an AU away in one frame.
import * as THREE from "./three.module.min.js";
import { bodyEcl, craftNow, orbitPoints, rotate, colourOf, bodyOf, add } from "./map2d.js";

const sub = (a, b) => ({ x: a.x - b.x, y: a.y - b.y, z: a.z - b.z });
const BODY_COLOURS = { star: 0xffd27a, planet: 0x6c9fe0, moon: 0xa7adb8, minor: 0x8a7a66 };

export class System3d {
  constructor(canvas, labels, state, universeNow, controls) {
    Object.assign(this, { canvas, labels, state, universeNow, controls });
    this.yaw = 0.6;
    this.pitch = 0.5;
    this.distance = null;          // m from the focus; null until there is something to fit
    this.focus = null;             // { kind: "body" | "craft", id }
    this.drawn = [];               // what was put where on screen, for a click to find

    this.renderer = new THREE.WebGLRenderer({ canvas, antialias: true, logarithmicDepthBuffer: true, alpha: true });
    this.scene = new THREE.Scene();
    this.camera = new THREE.PerspectiveCamera(50, 1, 1, 1e15);
    this.camera.up.set(0, 0, 1);   // ecliptic north
    // Enough that a night side still reads as a sphere, not a hole.
    this.scene.add(new THREE.AmbientLight(0xffffff, 0.45));
    this.sun = new THREE.PointLight(0xffffff, 2.6, 0, 0);
    this.scene.add(this.sun);
    this.sphere = new THREE.SphereGeometry(1, 64, 32);
    this.bodies = new Map();       // id -> { mesh, dot, orbit, body }
    this.crafts = new Map();       // key -> { dot, orbit, seq, colour }
    this.bodiesFrom = null;        // the description the meshes were made from
    this.listen();
  }

  listen() {
    let drag = null;
    this.canvas.addEventListener("pointerdown", (e) => {
      drag = { x: e.clientX, y: e.clientY, x0: e.clientX, y0: e.clientY, moved: false };
      this.canvas.setPointerCapture(e.pointerId);
    });
    this.canvas.addEventListener("pointermove", (e) => {
      if (!drag) return;
      // A click that wobbles a few pixels is still a click, and turns nothing.
      if (!drag.moved && Math.abs(e.clientX - drag.x0) + Math.abs(e.clientY - drag.y0) <= 8) return;
      drag.moved = true;
      this.yaw -= (e.clientX - drag.x) * 0.008;
      this.pitch = Math.max(-1.55, Math.min(1.55, this.pitch + (e.clientY - drag.y) * 0.008));
      drag.x = e.clientX; drag.y = e.clientY;
    });
    this.canvas.addEventListener("pointerup", (e) => {
      if (drag && !drag.moved) this.pick(e.offsetX, e.offsetY);
      drag = null;
    });
    this.canvas.addEventListener("wheel", (e) => {
      e.preventDefault();
      // A third or so a notch: the view runs from metres to astronomical units, and a wheel has to
      // cross that in a reasonable number of turns.
      if (this.distance) this.distance = Math.max(10, this.distance * Math.exp(e.deltaY * 0.003));
    }, { passive: false });
    this.controls.follow.addEventListener("change", () => {
      const p = this.state.view?.players.find(q => q.name === this.controls.follow.value);
      if (p?.controlling) this.focusOn({ kind: "craft", id: p.controlling });
    });
  }

  focusOn(f) {
    this.focus = f;
    // Close enough to see what it is and where: a body a few radii out, a craft far enough to see
    // its orbit round the body it is near.
    const b = f.kind === "body" ? bodyOf(this.state, f.id) : null;
    this.distance = b ? Math.max(b.radius, 1e3) * 6 : 2.5e6;
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

  /** Where a craft is now in the ecliptic frame. */
  craftEcl(c, u) {
    const b = bodyOf(this.state, c.body);
    const at = craftNow(c, this.state, u).r;
    return add(bodyEcl(this.state, c.body, u), b ? rotate(b.cci2Cce, at) : at);
  }

  where(f, u) {
    if (f.kind === "body") return bodyEcl(this.state, f.id, u);
    const c = this.state.crafts.get(f.id);
    return c ? this.craftEcl(c, u) : null;
  }

  /** Meshes, dots and orbit lines for a new description, the old ones thrown away. */
  build(description) {
    for (const k of this.bodies.values()) {
      this.scene.remove(k.mesh, k.dot);
      k.mesh.material.dispose();
      k.dot.material.dispose();
      if (k.orbit) { this.scene.remove(k.orbit); k.orbit.geometry.dispose(); k.orbit.material.dispose(); }
    }
    this.bodies.clear();
    for (const b of description.bodies) {
      if (b.kind === "barycenter") continue;
      const colour = BODY_COLOURS[b.kind] ?? 0x99aaaa;
      const mesh = new THREE.Mesh(this.sphere, b.kind === "star"
        ? new THREE.MeshBasicMaterial({ color: colour })
        : new THREE.MeshStandardMaterial({ color: colour, roughness: 1, metalness: 0 }));
      mesh.scale.setScalar(b.radius);
      // A body too far off to be more than a speck is still a dot, so a system has a shape.
      const dot = new THREE.Sprite(new THREE.SpriteMaterial({ color: colour, sizeAttenuation: false }));
      dot.scale.set(0.006, 0.006, 1);
      this.scene.add(mesh, dot);
      let orbit = null;
      if (b.orbit && b.orbit.eccentricity < 1) {
        const o = b.orbit, a = o.periapsis / (1 - o.eccentricity), bb = a * Math.sqrt(1 - o.eccentricity ** 2);
        const pts = [];
        for (let i = 0; i <= 256; i++) {
          const E = 2 * Math.PI * i / 256, p = rotate(o.orb2ParentCce, { x: a * (Math.cos(E) - o.eccentricity), y: bb * Math.sin(E), z: 0 });
          pts.push(new THREE.Vector3(p.x, p.y, p.z));
        }
        orbit = new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts),
          new THREE.LineBasicMaterial({ color: 0x6c9fe0, transparent: true, opacity: 0.4 }));
        this.scene.add(orbit);
      }
      this.bodies.set(b.id, { mesh, dot, orbit, body: b });
    }
    this.bodiesFrom = description;
  }

  craft(c) {
    let k = this.crafts.get(c.key);
    if (!k) {
      const colour = new THREE.Color(colourOf(c.ownerName));
      const dot = new THREE.Sprite(new THREE.SpriteMaterial({ color: colour, sizeAttenuation: false }));
      k = { dot, orbit: null, seq: -1, colour };
      this.scene.add(dot);
      this.crafts.set(c.key, k);
    }
    return k;
  }

  dropCraft(key) {
    const k = this.crafts.get(key);
    if (!k) return;
    this.scene.remove(k.dot);
    k.dot.material.dispose();
    if (k.orbit) { this.scene.remove(k.orbit); k.orbit.geometry.dispose(); k.orbit.material.dispose(); }
    this.crafts.delete(key);
  }

  fit() {
    const w = this.canvas.clientWidth, h = this.canvas.clientHeight, dpr = window.devicePixelRatio || 1;
    if (this.canvas.width !== Math.round(w * dpr) || this.canvas.height !== Math.round(h * dpr)) {
      this.renderer.setPixelRatio(dpr);
      this.renderer.setSize(w, h, false);
      this.camera.aspect = w / Math.max(1, h);
      this.camera.updateProjectionMatrix();
    }
    const l = this.labels;
    if (l.width !== Math.round(w * dpr) || l.height !== Math.round(h * dpr)) { l.width = Math.round(w * dpr); l.height = Math.round(h * dpr); }
    const ctx = l.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    return { ctx, w, h };
  }

  draw() {
    const { ctx, w, h } = this.fit();
    const description = this.state.bodies;
    this.controls.note.textContent = description ? "Drag to turn, wheel to zoom, click a body or a craft to go to it."
      : "Waiting for a game in the world to describe the system.";
    if (!description) { this.renderer.clear(); return; }
    if (description !== this.bodiesFrom) this.build(description);
    const bodies = description.bodies;
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
    const rel = (p) => sub(p, origin);
    const set = (o, p) => o.position.set(p.x, p.y, p.z);

    const cp = Math.cos(this.pitch);
    this.camera.position.set(this.distance * cp * Math.cos(this.yaw), this.distance * cp * Math.sin(this.yaw), this.distance * Math.sin(this.pitch));
    this.camera.lookAt(0, 0, 0);
    this.camera.updateMatrixWorld();

    // Where each body is now, relative to the focus: what everything else is placed from.
    const centres = new Map();
    for (const b of bodies) centres.set(b.id, rel(bodyEcl(this.state, b.id, u)));
    const star = bodies.find(b => b.kind === "star");
    if (star) set(this.sun, centres.get(star.id));

    const showOrbits = this.controls.orbits.checked, debris = this.controls.debris.checked;
    const labels = [];
    for (const [id, k] of this.bodies) {
      const at = centres.get(id);
      set(k.mesh, at);
      set(k.dot, at);
      if (k.orbit) {
        k.orbit.visible = showOrbits;
        set(k.orbit, centres.get(k.body.parent));
      }
      labels.push({ at, text: id, colour: "#c9d1dd", what: { kind: "body", id }, r: k.body.radius, body: k.body });
    }

    const standing = new Set();
    for (const c of this.state.crafts.values()) {
      const isFocus = this.focus.kind === "craft" && this.focus.id === c.key;
      if (!c.flags.controlled && !debris && !isFocus) continue;
      standing.add(c.key);
      const k = this.craft(c);
      const b = bodyOf(this.state, c.body);
      const centre = centres.get(c.body) ?? rel({ x: 0, y: 0, z: 0 });
      const at = rel(this.craftEcl(c, u));
      set(k.dot, at);
      const size = c.flags.controlled ? 0.011 : 0.007;
      k.dot.scale.set(size, size, 1);
      if (b?.mu > 0 && !c.flags.landed) {
        if (k.seq !== c.sequence) {
          const pts = orbitPoints(c.posCci, c.velCci, b.mu, 160).map(p => { const q = rotate(b.cci2Cce, p); return new THREE.Vector3(q.x, q.y, q.z); });
          if (k.orbit) { k.orbit.geometry.dispose(); k.orbit.geometry = new THREE.BufferGeometry().setFromPoints(pts); }
          else {
            k.orbit = new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts),
              new THREE.LineBasicMaterial({ color: k.colour, transparent: true, opacity: c.flags.controlled ? 0.8 : 0.35 }));
            this.scene.add(k.orbit);
          }
          k.seq = c.sequence;
        }
        k.orbit.visible = showOrbits;
        set(k.orbit, centre);
      } else if (k.orbit) k.orbit.visible = false;
      labels.push({ at, text: c.ownerName ? `${c.key} · ${c.ownerName}` : c.key, colour: "#e6e9ef", what: { kind: "craft", id: c.key }, r: 0 });
    }
    for (const key of [...this.crafts.keys()]) if (!standing.has(key)) this.dropCraft(key);

    this.renderer.render(this.scene, this.camera);
    this.label(ctx, w, h, labels, centres, this.controls.labels.checked);

    ctx.fillStyle = "#8b93a3";
    ctx.font = "12px system-ui, sans-serif";
    ctx.fillText(`${this.focus.kind === "body" ? this.focus.id : `${this.focus.id} (craft)`} · ${distanceText(this.distance)} out`, 10, h - 10);
  }

  /**
   * Names over what the GPU drew, and the places a click can find: none behind the camera, and
   * none whose line of sight passes through a body, so a label never shows through a planet.
   */
  label(ctx, w, h, labels, centres, show) {
    this.drawn = [];
    const eye = this.camera.position;
    const spheres = [...this.bodies.values()].map(k => ({ c: centres.get(k.body.id), r: k.body.radius, id: k.body.id }));
    const v = new THREE.Vector3();
    const toPixels = h / (2 * Math.tan(this.camera.fov * Math.PI / 360));
    ctx.font = "12px system-ui, sans-serif";
    for (const l of labels) {
      v.set(l.at.x, l.at.y, l.at.z).project(this.camera);
      if (v.z > 1 || v.z < -1) continue;
      const x = (v.x + 1) / 2 * w, y = (1 - v.y) / 2 * h;
      if (x < -50 || x > w + 50 || y < -50 || y > h + 50) continue;
      if (hidden(eye, l, spheres)) continue;
      const dist = Math.hypot(l.at.x - eye.x, l.at.y - eye.y, l.at.z - eye.z);
      const r = l.r > 0 ? Math.max(3, l.r / dist * toPixels) : 5;
      this.drawn.push({ x, y, r, what: l.what });
      // A small moon a world away is a dot, and its name would only crowd the planet's.
      if (!show || (l.body && l.body.kind !== "star" && l.body.kind !== "planet" && r < 4)) continue;
      ctx.fillStyle = l.colour;
      ctx.fillText(l.text, x + Math.min(r, 400) + 5, y - 4);
    }
  }
}

// Whether the straight line from the eye to a label's point passes through any body on the way.
function hidden(eye, l, spheres) {
  const d = { x: l.at.x - eye.x, y: l.at.y - eye.y, z: l.at.z - eye.z };
  const len2 = d.x * d.x + d.y * d.y + d.z * d.z;
  for (const s of spheres) {
    if (l.what.kind === "body" && l.what.id === s.id) continue;
    const m = { x: eye.x - s.c.x, y: eye.y - s.c.y, z: eye.z - s.c.z };
    const b = m.x * d.x + m.y * d.y + m.z * d.z, c = m.x * m.x + m.y * m.y + m.z * m.z - s.r * s.r;
    const disc = b * b - len2 * c;
    if (disc <= 0) continue;
    const t = (-b - Math.sqrt(disc)) / len2;
    if (t > 1e-9 && t < 1 - 1e-6) return true;
  }
  return false;
}

function distanceText(m) {
  if (m >= 1.496e11 * 0.1) return `${(m / 1.496e11).toFixed(2)} AU`;
  if (m >= 1e6) return `${(m / 1e3).toLocaleString(undefined, { maximumFractionDigits: 0 })} km`;
  if (m >= 1e3) return `${(m / 1e3).toFixed(1)} km`;
  return `${m.toFixed(0)} m`;
}
