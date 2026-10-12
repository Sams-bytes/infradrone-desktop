// engine.js — InfraDrone Suriname demo: geometry helpers and the flight-plan engine.
//
// The whole demo is a deterministic function of simulated time (simT). Each
// chapter is a list of "legs" (hold, vertical climb, transition, route, orbit,
// approach, vertical descent). stateAt(simT) gives position, altitude, attitude
// and phase, so any chapter can be jumped to cleanly and replayed identically.
//
// The last leg of every chapter is "open-ended": it keeps going (hover, orbit,
// standing on the ground) while the presenter talks, and the next chapter is
// rebuilt from wherever the drone actually is when Next is pressed.
(function () {
  'use strict';

  const R = 6371008.8, D2R = Math.PI / 180, R2D = 180 / Math.PI, G = 9.81;

  // ---- Geometry ------------------------------------------------------------------
  function dist(a, b) {
    const dLat = (b[0] - a[0]) * D2R, dLon = (b[1] - a[1]) * D2R;
    const s = Math.sin(dLat / 2) ** 2 + Math.cos(a[0] * D2R) * Math.cos(b[0] * D2R) * Math.sin(dLon / 2) ** 2;
    return 2 * R * Math.asin(Math.min(1, Math.sqrt(s)));
  }
  function bearing(a, b) {
    const y = Math.sin((b[1] - a[1]) * D2R) * Math.cos(b[0] * D2R);
    const x = Math.cos(a[0] * D2R) * Math.sin(b[0] * D2R) - Math.sin(a[0] * D2R) * Math.cos(b[0] * D2R) * Math.cos((b[1] - a[1]) * D2R);
    return norm360(Math.atan2(y, x) * R2D);
  }
  function dest(p, brg, d) {
    const lat1 = p[0] * D2R, lon1 = p[1] * D2R, t = brg * D2R, dr = d / R;
    const lat2 = Math.asin(Math.sin(lat1) * Math.cos(dr) + Math.cos(lat1) * Math.sin(dr) * Math.cos(t));
    const lon2 = lon1 + Math.atan2(Math.sin(t) * Math.sin(dr) * Math.cos(lat1), Math.cos(dr) - Math.sin(lat1) * Math.sin(lat2));
    return [lat2 * R2D, lon2 * R2D];
  }
  function norm360(a) { return ((a % 360) + 360) % 360; }
  function angDiff(a, b) { return ((b - a + 540) % 360) - 180; }
  function lerp(a, b, f) { return a + (b - a) * f; }
  function clamp(v, lo, hi) { return v < lo ? lo : v > hi ? hi : v; }
  function smooth01(f) { f = clamp(f, 0, 1); return f * f * (3 - 2 * f); }

  class Path {
    constructor(pts) {
      this.pts = pts.map(p => [p[0], p[1]]);
      this.cum = [0];
      for (let i = 1; i < this.pts.length; i++) this.cum.push(this.cum[i - 1] + dist(this.pts[i - 1], this.pts[i]));
      this.len = this.cum[this.cum.length - 1];
    }
    at(s) {
      s = clamp(s, 0, this.len);
      let lo = 0, hi = this.cum.length - 1;
      while (hi - lo > 1) { const m = (lo + hi) >> 1; if (this.cum[m] <= s) lo = m; else hi = m; }
      const segLen = this.cum[hi] - this.cum[lo];
      const f = segLen > 0 ? (s - this.cum[lo]) / segLen : 0;
      const a = this.pts[lo], b = this.pts[hi];
      return [lerp(a[0], b[0], f), lerp(a[1], b[1], f)];
    }
    // Sub-path between two distances, as a new point list.
    slice(s0, s1) {
      const out = [this.at(s0)];
      for (let i = 0; i < this.cum.length; i++) if (this.cum[i] > s0 && this.cum[i] < s1) out.push(this.pts[i]);
      out.push(this.at(s1));
      return out;
    }
    // Distance along the path of the point nearest to p.
    project(p) {
      let best = 0, bestD = Infinity;
      for (let i = 1; i < this.pts.length; i++) {
        const segLen = this.cum[i] - this.cum[i - 1];
        const n = Math.max(1, Math.ceil(segLen / 20));
        for (let k = 0; k <= n; k++) {
          const s = this.cum[i - 1] + segLen * k / n, d = dist(this.at(s), p);
          if (d < bestD) { bestD = d; best = s; }
        }
      }
      return best;
    }
  }

  // ---- Legs -------------------------------------------------------------------------
  // Every leg: { type, phase, dur (sim s), scale (time ×), cam, sensor, airborne,
  //   len (m flown), marks: [{name, frac}], open (extrapolates past dur), st(tl) }
  function st(lat, lon, alt, hdg, pitch, roll, spd, phase, airborne) {
    return { lat, lon, alt, hdg: norm360(hdg), pitch, roll, spd, phase, airborne };
  }

  function holdLeg(p, alt, hdg, dur, o) {
    const airborne = alt > 0.5;
    return Object.assign({ type: 'hold', phase: airborne ? 'hover' : 'ground', dur, scale: 1, airborne, len: 0, marks: [], open: false,
      st: () => st(p[0], p[1], alt, hdg, 0, 0, 0, null, airborne) }, o);
  }

  function yawLeg(p, alt, h0, h1, o) {
    const d = angDiff(h0, h1), dur = Math.max(2, Math.abs(d) / 30);
    return Object.assign({ type: 'yaw', phase: 'hover', dur, scale: 1, airborne: true, len: 0, marks: [], open: false,
      st: tl => st(p[0], p[1], alt, h0 + d * smooth01(tl / dur), 0, 0, 0, null, true) }, o);
  }

  function vtolLeg(p, hdg, a0, a1, rate, o) {
    const dur = Math.abs(a1 - a0) / rate, up = a1 > a0;
    return Object.assign({ type: up ? 'vtol_up' : 'vtol_down', phase: up ? 'vtol_up' : 'vtol_down', dur, scale: 1, airborne: true, len: 0, marks: [], open: false,
      st: tl => {
        const f = smooth01(tl / dur);
        return st(p[0], p[1], lerp(a0, a1, f), hdg, 0, 0, Math.abs(a1 - a0) / dur, null, true);
      } }, o);
  }

  function transitionLeg(p, hdg, alt, v, o) {
    const dur = 12, d = v * dur / 2, end = dest(p, hdg, d);
    return Object.assign({ type: 'transition', phase: 'transition', dur, scale: 1, airborne: true, len: d, end, marks: [], open: false,
      st: tl => {
        const t = clamp(tl, 0, dur), s = 0.5 * (v / dur) * t * t;
        const q = dest(p, hdg, s);
        return st(q[0], q[1], alt, hdg, 4 * (1 - t / dur), 0, v * t / dur, null, true);
      } }, o);
  }

  function approachLeg(from, fob, alt, v, o) {
    // Decelerates from cruise to a hover over the base. 'from' is where it starts.
    const d = dist(from, fob), dur = 2 * d / v, hdg = bearing(from, fob);
    return Object.assign({ type: 'approach', phase: 'approach', dur, scale: 1, airborne: true, len: d, marks: [], open: false,
      st: tl => {
        const t = clamp(tl, 0, dur), s = v * t - 0.5 * (v / dur) * t * t;
        const q = dest(from, hdg, s);
        return st(q[0], q[1], alt, hdg, -3 * (1 - t / dur), 0, v * (1 - t / dur), null, true);
      } }, o);
  }

  // altFn(s, len) gives altitude at distance s along the path.
  function routeLeg(pts, altFn, v, o) {
    const path = new Path(pts), dur = path.len / v;
    const leg = Object.assign({ type: 'route', phase: 'cruise', dur, scale: 1, airborne: true, len: path.len, path, marks: [], open: false,
      st: tl => {
        const s = clamp(tl, 0, dur) * v;
        const q = path.at(s);
        const back = path.at(Math.max(0, s - 60)), fwd = path.at(Math.min(path.len, s + 60));
        let hdg = (fwd[0] === back[0] && fwd[1] === back[1]) ? (leg.hdg0 || 0) : bearing(back, fwd);
        // Bank angle from turn rate over the next/previous 120 m (coordinated turn).
        const h1 = bearing(back, q), h2 = bearing(q, fwd);
        const dh = (dist(back, q) > 5 && dist(q, fwd) > 5) ? angDiff(h1, h2) : 0;
        const omega = dh * D2R / (120 / v);
        const roll = clamp(Math.atan(v * omega / G) * R2D, -35, 35);
        const a = altFn(s, path.len), a2 = altFn(Math.min(path.len, s + 50), path.len);
        const pitch = clamp(Math.atan2(a2 - a, 50) * R2D + 2, -10, 15);
        return st(q[0], q[1], a, hdg, pitch, roll, v, null, true);
      } }, o);
    if (pts.length > 1) leg.hdg0 = bearing(pts[0], pts[1]);
    return leg;
  }

  // Clockwise orbit around 'center'. Starts at the angle where 'entry' lies.
  function orbitLeg(center, radius, entry, alt0, alt1, altDur, v, dur, o) {
    const theta0 = bearing(center, entry), w = v / radius * R2D; // deg/s
    const roll = Math.atan(v * v / (radius * G)) * R2D;
    return Object.assign({ type: 'orbit', phase: 'observe', dur, scale: 1, airborne: true, len: v * dur, center, radius, marks: [], open: false,
      st: tl => {
        const th = theta0 + w * tl, q = dest(center, th, radius);
        const a = lerp(alt0, alt1, smooth01(altDur > 0 ? tl / altDur : 1));
        return st(q[0], q[1], a, th + 90, 2, roll, v, null, true);
      } }, o);
  }

  // Altitude profile: climb from a0 to a1 over the first 'climbDist' metres,
  // descend to a2 over the last 'descDist' metres.
  function profile(a0, a1, climbDist, a2, descDist) {
    return (s, len) => {
      let a = a1;
      if (climbDist > 0 && s < climbDist) a = lerp(a0, a1, smooth01(s / climbDist));
      if (descDist > 0 && s > len - descDist) a = lerp(a1, a2, smooth01((s - (len - descDist)) / descDist));
      return a;
    };
  }

  // ---- Chapter builders ---------------------------------------------------------------
  // Each returns { legs, anchors: [mark names for captions], info }.
  function fitScale(simSeconds, realSeconds, lo, hi) {
    const s = simSeconds / realSeconds;
    const nice = [1, 1.5, 2, 3, 4, 5, 6, 8, 10, 12, 15, 20, 25, 30, 40, 50, 60, 80, 100, 120, 150, 200, 250, 300];
    let best = nice[0];
    for (const n of nice) if (Math.abs(Math.log(n / s)) < Math.abs(Math.log(best / s))) best = n;
    return clamp(best, lo, hi);
  }

  function buildChapter(id, S, A, start) {
    const v = A.cruise_ms, VA = A.vtol_alt_m, CA = A.cruise_alt_m;
    const legs = [];
    const add = (leg) => { legs.push(leg); return leg; };
    const endPos = () => { const l = legs[legs.length - 1]; const e = l.st(l.dur); return [e.lat, e.lon]; };
    const s0 = start;              // drone state at chapter start
    const p0 = [s0.lat, s0.lon];
    const info = {};

    if (id === 'briefing' || id === 'report' || id === 'closing') {
      add(holdLeg(p0, s0.alt, s0.hdg, 0.001, { cam: id === 'briefing' ? 'overview' : 'overviewEnd', sensor: 'off', phase: s0.airborne ? 'hover' : 'ground', open: true }));
      return { legs, anchors: [], info, isStatic: true };
    }

    if (id === 'launch') {
      const hdg = bearing(S.hq, S.transit[1]);
      add(holdLeg(S.hq, 0, hdg, 8, { cam: 'orbitClose', sensor: 'off', marks: [{ name: 'checks', frac: 0 }] }));
      add(vtolLeg(S.hq, hdg, 0, VA, A.vtol_climb_ms, { cam: 'orbitClose', sensor: 'terrain', scale: 1.5, marks: [{ name: 'takeoff1', frac: 0 }] }));
      add(holdLeg(S.hq, VA, hdg, 6, { cam: 'orbitClose', sensor: 'terrain', open: true, marks: [{ name: 'hover1', frac: 0 }] }));
      return { legs, anchors: ['checks', 'takeoff1', 'hover1'], info };
    }

    if (id === 'transit') {
      const route = [p0].concat(S.transit.slice(1));
      const names = S.transit_names || [];
      const hdg = bearing(p0, route[1]);
      if (Math.abs(angDiff(s0.hdg, hdg)) > 2) add(yawLeg(p0, VA, s0.hdg, hdg, { cam: 'orbitClose', sensor: 'terrain' }));
      const tr = add(transitionLeg(p0, hdg, VA, v, { cam: 'chase', sensor: 'terrain', scale: 1.5, marks: [{ name: 'transition', frac: 0 }] }));
      const fob = S.fob;
      const full = new Path([tr.end].concat(route.slice(1)));
      // Battery: split the ferry with a landing + battery swap if it needs > 80% endurance.
      const ferrySec = full.len / v + 200;
      let stopAt = null;
      if (ferrySec > 0.8 * A.endurance_min * 60) {
        let bestI = -1, bestErr = Infinity;
        for (let i = 1; i < route.length - 1; i++) {
          if (!names[i]) continue;
          const err = Math.abs(full.project(route[i]) - full.len / 2);
          if (err < bestErr) { bestErr = err; bestI = i; }
        }
        if (bestI > 0) stopAt = { idx: bestI, name: names[bestI], p: route[bestI] };
      }
      const segments = stopAt
        ? [{ pts: [tr.end].concat(route.slice(1, stopAt.idx + 1)), base: stopAt.p, name: stopAt.name, stop: true },
           { pts: [stopAt.p].concat(route.slice(stopAt.idx + 1)), base: fob, name: '', stop: false }]
        : [{ pts: [tr.end].concat(route.slice(1)), base: fob, name: '', stop: false }];
      info.stop = stopAt ? stopAt.name : null;
      let first = true;
      for (const seg of segments) {
        if (!first) {
          // Relaunch after the battery swap at the stop.
          const h = bearing(seg.pts[0], seg.pts[1]);
          add(vtolLeg(seg.pts[0], h, 0, VA, A.vtol_climb_ms, { cam: 'orbitClose', sensor: 'terrain', scale: 3, sortieBreak: true }));
          const t2 = add(transitionLeg(seg.pts[0], h, VA, v, { cam: 'chase', sensor: 'terrain', scale: 2 }));
          seg.pts[0] = t2.end;
        }
        // Route ends 'approachDist' before the base, then decelerates to a hover over it.
        const path = new Path(seg.pts);
        const approachDist = v * 12 / 2;
        const lastB = bearing(path.at(Math.max(0, path.len - 300)), seg.base);
        const apStart = dest(seg.base, lastB + 180, approachDist);
        const pts = path.slice(0, Math.max(0, path.len - approachDist)).concat([apStart]);
        const p = new Path(pts);
        const climbD = 2500, descD = 2500;
        const prof = profile(VA, CA, climbD, VA, descD);
        // Fast middle part, slower climb and descent so they are watchable.
        const s1 = Math.min(climbD, p.len * 0.3), s2 = Math.max(s1, p.len - descD);
        const mid = (s2 - s1) / v;
        const fast = fitScale(mid, stopAt ? 24 : 40, 20, 300);
        info.fastScale = Math.max(info.fastScale || 0, fast);
        add(routeLeg(p.slice(0, s1), (s) => prof(s, p.len), v, { cam: 'chase', sensor: 'terrain', scale: 12, marks: first ? [{ name: 'climb', frac: 0.3 }] : [] }));
        add(routeLeg(p.slice(s1, s2), (s) => prof(s + s1, p.len), v, { cam: 'chaseHigh', sensor: 'terrain', scale: fast, marks: first ? [{ name: 'cruise', frac: 0.05 }, { name: 'cruise35', frac: 0.35 }] : [] }));
        add(routeLeg(p.slice(s2, p.len), (s) => prof(s + s2, p.len), v, { cam: 'chase', sensor: 'terrain', scale: 12, phase: 'approach' }));
        add(approachLeg(apStart, seg.base, VA, v, { cam: 'orbitClose', sensor: 'terrain', scale: 2, marks: seg.stop ? [{ name: 'stopApproach', frac: 0 }] : [{ name: 'arrive', frac: 0 }] }));
        add(vtolLeg(seg.base, lastB, VA, 0, 1.5, { cam: 'orbitClose', sensor: 'off', scale: 5, marks: [{ name: seg.stop ? 'stopLanded' : 'landed1', frac: 1 }] }));
        add(holdLeg(seg.base, 0, lastB, 600, { cam: 'orbitClose', sensor: 'off', phase: 'swap', scale: 100, open: !seg.stop, sortieBreak: true,
          marks: [{ name: seg.stop ? 'stopSwap' : 'swap', frac: 0 }] }));
        first = false;
      }
      return { legs, anchors: ['transition', 'cruise', 'cruise35', 'arrive'], info };
    }

    if (id === 'patrol') {
      const fob = S.fob, track = S.patrol, idx = S.boat.track_index;
      const hdg = bearing(fob, track[0]);
      const onGround = !s0.airborne;
      if (onGround) add(vtolLeg(p0, hdg, 0, VA, A.vtol_climb_ms, { cam: 'orbitClose', sensor: 'terrain', scale: 2, sortieBreak: true, marks: [{ name: 'takeoff2', frac: 0 }] }));
      const tr = add(transitionLeg(p0, hdg, VA, v, { cam: 'chase', sensor: 'terrain' }));
      const center = track[idx], R0 = 250;
      const entry = dest(center, bearing(center, track[idx - 1]), R0);
      const path = new Path([tr.end].concat(track.slice(0, idx)).concat([entry]));
      const detectDist = 1500;
      const sDet = Math.max(500, path.len - detectDist);
      const prof = profile(VA, CA, 1500, CA, 0);
      const patrolScale = fitScale(sDet / v, 40, 5, 40);
      add(routeLeg(path.slice(0, sDet), (s) => prof(s, path.len), v, { cam: 'chase', sensor: 'river', phase: 'patrol', scale: patrolScale,
        marks: [{ name: 'patrolStart', frac: 0 }, { name: 'patrolAI', frac: 0.35 }] }));
      add(routeLeg(path.slice(sDet, path.len), () => CA, v, { cam: 'contact', sensor: 'river', phase: 'observe', scale: 3, contact: true,
        marks: [{ name: 'detect', frac: 0 }] }));
      add(orbitLeg(center, R0, entry, CA, CA, 0, v * 0.8, 40, { cam: 'observe', sensor: 'river', phase: 'observe', scale: 1,
        marks: [{ name: 'alertSent', frac: 0.12 }, { name: 'boatLanded', frac: 0.85 }] }));
      // Police boat: dispatched at alertSent, travels its path at unit speed.
      const unitPath = new Path(S.ground_unit.path);
      const unitSpeed = (S.contact.unit_kmh || 35) / 3.6;
      const remain = Math.max(30, unitPath.len / unitSpeed - 40 * 0.88);
      const hand = orbitLeg(center, R0, endPos(), CA, CA, 0, v * 0.8, remain, { cam: 'handover', sensor: 'river', phase: 'observe',
        scale: fitScale(remain, 30, 5, 60), open: true, marks: [{ name: 'handover', frac: 0 }] });
      add(hand);
      // Border kilometres patrolled = along the river track only (not the hop from the base).
      info.unitPath = unitPath; info.unitSpeed = unitSpeed;
      info.patrolKm = new Path(track.slice(0, idx).concat([entry])).len / 1000;
      return { legs, anchors: ['patrolStart', 'patrolAI', 'detect', 'alertSent', 'handover'], info };
    }

    if (id === 'sar') {
      const lines = S.sar.lines, tgt = S.sar.target;
      const patPts = [];
      lines.forEach(l => { patPts.push(l[0]); patPts.push(l[1]); });
      const pat = new Path(patPts);
      const sTarget = pat.project(tgt);
      const sHit = Math.max(0, sTarget - 70);
      // Transit from the current position to the first search line.
      const startP = s0.airborne ? p0 : S.fob;
      if (!s0.airborne) add(vtolLeg(startP, bearing(startP, patPts[0]), 0, VA, A.vtol_climb_ms, { cam: 'orbitClose', sensor: 'terrain', scale: 2, sortieBreak: true }));
      const toArea = new Path([startP, patPts[0]]);
      add(routeLeg(toArea.pts, profile(s0.alt || VA, A.search_alt_m, 1000, A.search_alt_m, 0), v, { cam: 'chaseHigh', sensor: 'forest', phase: 'cruise',
        scale: fitScale(toArea.len / v, 22, 5, 60), marks: [{ name: 'sarTask', frac: 0 }] }));
      const patScale = fitScale(sHit / v, 55, 4, 40);
      add(routeLeg(pat.slice(0, sHit), () => A.search_alt_m, v, { cam: 'topArea', sensor: 'forest', phase: 'search', scale: patScale, pattern: { path: pat, s0: 0 },
        marks: [{ name: 'searchStart', frac: 0 }, { name: 'searchIR', frac: 0.2 }] }));
      // Confirmation pass over the target, then a descending orbit around it.
      const lineB = bearing(pat.at(sHit), pat.at(Math.min(pat.len, sTarget + 10)));
      const Rc = 150, entry = dest(tgt, lineB, Rc);
      add(routeLeg([pat.at(sHit), tgt, entry], () => A.search_alt_m, v, { cam: 'confirm', sensor: 'forest', phase: 'confirm', scale: 1, pattern: { path: pat, s0: sHit, partial: true },
        marks: [{ name: 'hit', frac: 0 }] }));
      add(orbitLeg(tgt, Rc, entry, A.search_alt_m, A.confirm_alt_m, 25, v * 0.8, 40, { cam: 'confirm', sensor: 'forest', phase: 'confirm', scale: 1,
        marks: [{ name: 'confirmed', frac: 0.7 }] }));
      const teamPath = new Path([S.sar.team_start, tgt]);
      const teamSpeed = (S.sar_info.team_kmh || 3) / 3.6;
      const walk = teamPath.len / teamSpeed, overhead = Math.min(walk, 480);
      add(orbitLeg(tgt, Rc, endPos(), A.confirm_alt_m, A.confirm_alt_m, 0, v * 0.8, overhead, { cam: 'team', sensor: 'forest', phase: 'observe',
        scale: fitScale(overhead, 18, 5, 100), open: true, marks: [{ name: 'teamGo', frac: 0 }] }));
      info.teamWalk = walk;
      info.searchSec = sHit / v; info.teamPath = teamPath; info.teamSpeed = teamSpeed;
      info.areaKm2 = (dist(S.sar.area[0], S.sar.area[1]) * dist(S.sar.area[1], S.sar.area[2])) / 1e6;
      info.coveredKm2 = sHit * (S.sar.spacing_m || 120) / 1e6;
      return { legs, anchors: ['sarTask', 'searchStart', 'searchIR', 'hit', 'confirmed'], info };
    }

    if (id === 'return') {
      const fob = S.fob;
      const approachDist = v * 12 / 2;
      const b = bearing(p0, fob);
      const apStart = dest(fob, b + 180, approachDist);
      const path = new Path([p0, apStart]);
      const prof = profile(s0.alt || A.confirm_alt_m, CA, 1000, VA, Math.min(2500, path.len * 0.4));
      add(routeLeg(path.pts, prof, v, { cam: 'chaseHigh', sensor: 'terrain', phase: 'rtb', scale: fitScale(path.len / v, 35, 5, 60), marks: [{ name: 'rtb', frac: 0 }] }));
      add(approachLeg(apStart, fob, VA, v, { cam: 'orbitClose', sensor: 'terrain', scale: 2 }));
      add(vtolLeg(fob, b, VA, 0, 1.5, { cam: 'orbitClose', sensor: 'off', scale: 5, marks: [{ name: 'land2', frac: 0 }, { name: 'landed2', frac: 1 }] }));
      add(holdLeg(fob, 0, b, 30, { cam: 'orbitClose', sensor: 'off', open: true }));
      return { legs, anchors: ['rtb', 'land2'], info };
    }
    throw new Error('Unknown chapter ' + id);
  }

  // ---- Plan ------------------------------------------------------------------------------
  class Plan {
    constructor(S, A, chapterIds) {
      this.S = S; this.A = A; this.ids = chapterIds;
      this.ch = [];
      const start = { lat: S.hq[0], lon: S.hq[1], alt: 0, hdg: bearing(S.hq, S.transit[1]), airborne: false };
      this.rebuildFrom(0, start, 0);
    }

    // (Re)build chapters i.. from a given state and time. Earlier chapters are kept.
    rebuildFrom(i, startState, t) {
      this.ch.length = i;
      let s = startState, time = t;
      for (let k = i; k < this.ids.length; k++) {
        const c = buildChapter(this.ids[k], this.S, this.A, s);
        c.id = this.ids[k]; c.tStart = time;
        for (const leg of c.legs) { leg.t0 = time; time += leg.dur; }
        c.tEnd = time;
        c.marks = {};
        for (const leg of c.legs) for (const m of leg.marks) c.marks[m.name] = leg.t0 + m.frac * leg.dur;
        if (c.info.teamWalk && c.marks.teamGo !== undefined) c.marks.teamOnScene = c.marks.teamGo + c.info.teamWalk;
        if (c.info.unitPath && c.marks.alertSent !== undefined) c.marks.unitOnScene = c.marks.alertSent + c.info.unitPath.len / c.info.unitSpeed;
        this.ch.push(c);
        const last = c.legs[c.legs.length - 1];
        s = last.st(last.dur);
        if (s.airborne === undefined) s.airborne = last.airborne;
      }
      this.computeSorties();
    }

    // Leg active at simT within chapter ci (the chapter's last leg extrapolates).
    legAt(ci, t) {
      const c = this.ch[ci];
      for (let k = 0; k < c.legs.length; k++) {
        const leg = c.legs[k];
        if (t < leg.t0 + leg.dur || k === c.legs.length - 1) return { leg, k, tl: Math.max(0, t - leg.t0) };
      }
      return null;
    }

    stateAt(ci, t) {
      const { leg, tl } = this.legAt(ci, t);
      const s = leg.st(leg.open ? tl : Math.min(tl, leg.dur));
      s.phase = s.phase || leg.phase;
      s.leg = leg; s.tl = tl;
      s.battery = this.batteryAt(leg, Math.min(tl, leg.dur));
      s.flightSec = this.flightSecAt(leg, Math.min(tl, leg.dur));
      return s;
    }

    // Battery: sorties start at legs flagged sortieBreak (after a battery swap).
    computeSorties() {
      let used = 0, total = 0;
      for (const c of this.ch) for (const leg of c.legs) {
        if (leg.sortieBreak) used = 0;
        leg.usedBefore = used; leg.totalBefore = total;
        if (leg.airborne) { used += leg.dur; total += leg.dur; }
      }
    }
    batteryAt(leg, tl) {
      const used = leg.usedBefore + (leg.airborne ? tl : 0);
      return clamp(100 - used / (this.A.endurance_min * 60) * 100, 0, 100);
    }
    flightSecAt(leg, tl) { return leg.totalBefore + (leg.airborne ? tl : 0); }

    // Distance flown up to t (for the report).
    distanceTo(ci, t) {
      let d = 0;
      for (let k = 0; k <= ci && k < this.ch.length; k++) for (const leg of this.ch[k].legs) {
        if (leg.t0 >= t) break;
        const f = clamp((t - leg.t0) / leg.dur, 0, 1);
        d += (leg.len || 0) * (k < ci || t >= leg.t0 + leg.dur ? 1 : f);
      }
      return d;
    }
  }

  window.DemoEngine = { dist, bearing, dest, angDiff, norm360, lerp, clamp, smooth01, Path, Plan };
})();
