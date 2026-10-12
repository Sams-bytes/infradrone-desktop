// app.js — InfraDrone Suriname demo: chapters, HUD, camera, overlays, events, controls.
(function () {
  'use strict';
  const E = window.DemoEngine;
  const $ = (id) => document.getElementById(id);
  const qs = new URLSearchParams(location.search);
  const store = {
    get(k, d) { try { const v = localStorage.getItem('infradrone.demo.' + k); return v === null ? d : v; } catch (e) { return d; } },
    set(k, v) { try { localStorage.setItem('infradrone.demo.' + k, String(v)); } catch (e) { /* private mode */ } }
  };

  // ---- State ---------------------------------------------------------------------------
  const app = {
    D: null, S: null, geo: null, cfg: null, r: null, plan: null,
    lang: 'en', scenarioId: 'albina', ci: 0, simT: 0, playing: true, auto: false,
    chapterRealT: 0, doneRealT: 0, waiting: false, camManual: 'auto', muted: false,
    trail: [], firedKeys: new Set(), log: [], events: [], cam: null, dispHdg: 0, dispRoll: 0,
    realT: 0, lastOverlay: 0, lastTrailPush: 0, lastHud: 0, bannerTimer: null, rendererName: '',
  };

  const tr = (o) => (o == null ? '' : typeof o === 'string' ? o : (o[app.lang] ?? o.en ?? ''));
  function fill(text, extra) {
    const S = app.S, n = S.names, x = Object.assign({}, app.vars, extra || {});
    return text.replace(/\{(\w+)\}/g, (m, k) => (n[k] ? tr(n[k]) : x[k] !== undefined ? x[k] : m));
  }
  const T = (o, extra) => fill(tr(o), extra);
  const fmtClock = (simT) => {
    const s = Math.floor(6.5 * 3600 + simT) % 86400;
    return [Math.floor(s / 3600), Math.floor(s / 60) % 60, s % 60].map(v => String(v).padStart(2, '0')).join(':');
  };
  const fmtLL = (p) => `${Math.abs(p[0]).toFixed(5)}° ${p[0] >= 0 ? 'N' : 'S'}, ${Math.abs(p[1]).toFixed(5)}° ${p[1] >= 0 ? 'E' : 'W'}`;

  // ---- Event texts (EN / NL) -----------------------------------------------------------------
  const EV = {
    checks: { k: 'info', t: { en: 'Pre-flight checks started', nl: 'Vluchtvoorbereiding gestart' } },
    takeoff1: { k: 'info', t: { en: 'Take-off — Paramaribo (Zorg en Hoop)', nl: 'Opgestegen — Paramaribo (Zorg en Hoop)' } },
    transition: { k: 'info', t: { en: 'Transition to wing-borne flight', nl: 'Overgang naar vleugelvlucht' } },
    cruise: { k: 'ok', t: { en: 'Geofence active — Surinamese airspace only', nl: 'Geofence actief — alleen Surinaams luchtruim' } },
    stopApproach: { k: 'info', t: { en: 'Planned battery stop: {stop}', nl: 'Geplande batterijstop: {stop}' } },
    stopLanded: { k: 'ok', t: { en: 'Landed at {stop} — battery swap', nl: 'Geland in {stop} — batterijwissel' } },
    arrive: { k: 'info', t: { en: 'Approaching forward base {fob}', nl: 'Nadering voorwaartse basis {fob}' } },
    landed1: { k: 'ok', t: { en: 'Landed — forward base {fob}', nl: 'Geland — voorwaartse basis {fob}' } },
    swap: { k: 'ok', t: { en: 'Battery swapped — ready for tasking', nl: 'Batterij gewisseld — klaar voor opdracht' } },
    takeoff2: { k: 'info', t: { en: 'Take-off — patrol flight {river}', nl: 'Opgestegen — patrouillevlucht {river}' } },
    patrolStart: { k: 'info', t: { en: 'Border patrol started — {river}', nl: 'Grenspatrouille gestart — {river}' } },
    detect: { k: 'alert', banner: true, t: { en: 'ALERT — {contactTitle}', nl: 'MELDING — {contactTitle}' } },
    alertSent: { k: 'warn', t: { en: 'Position + image sent: {recipients}', nl: 'Positie + beeld verstuurd: {recipients}' } },
    boatLanded: { k: 'warn', t: { en: 'Boat at Surinamese bank — 2 persons on foot', nl: 'Boot aan Surinaamse oever — 2 personen te voet' } },
    handover: { k: 'info', t: { en: '{unit} dispatched — ETA {unitEta} min', nl: '{unit} onderweg — aankomst over {unitEta} min' } },
    unitOnScene: { k: 'ok', banner: true, t: { en: '{unit} on scene — contact handed over', nl: '{unit} ter plaatse — contact overgedragen' } },
    sarTask: { k: 'warn', banner: true, t: { en: 'NEW TASK — missing person, {sar_place}', nl: 'NIEUWE OPDRACHT — vermiste persoon, {sar_place}' } },
    searchStart: { k: 'info', t: { en: 'Search pattern started — {area_km2} km², lines every 120 m', nl: 'Zoekpatroon gestart — {area_km2} km², elke 120 m een lijn' } },
    hit: { k: 'alert', banner: true, t: { en: 'POSSIBLE PERSON — thermal signature', nl: 'MOGELIJK PERSOON — warmtebron' } },
    confirmed: { k: 'ok', banner: true, t: { en: 'PERSON CONFIRMED — coordinates sent', nl: 'PERSOON BEVESTIGD — coördinaten verstuurd' } },
    teamGo: { k: 'info', t: { en: '{team} en route — ETA {teamEta} min', nl: '{team} onderweg — aankomst over {teamEta} min' } },
    teamOnScene: { k: 'ok', t: { en: 'Rescue team reached the person', nl: 'Reddingsteam heeft de persoon bereikt' } },
    rtb: { k: 'info', t: { en: 'Returning to {fob}', nl: 'Terugkeer naar {fob}' } },
    landed2: { k: 'ok', t: { en: 'Landed — mission complete', nl: 'Geland — missie voltooid' } },
  };
  const BANNER_DETAIL = {
    detect: () => ({ d: T(app.S.contact.detail), c: fmtLL(app.S.boat.cross) + ' · ' + T(app.S.contact.recipients) }),
    unitOnScene: () => ({ d: '', c: fmtLL(app.S.boat.end) }),
    sarTask: () => ({ d: T({ en: 'Hunter, overdue 20 hours. Last known position received.', nl: 'Jager, 20 uur vermist. Laatst bekende positie ontvangen.' }), c: fmtLL(app.S.sar.lkp) }),
    hit: () => ({ d: T({ en: 'Confidence 0.84 · breaking off search to confirm', nl: 'Zekerheid 0,84 · zoektocht onderbroken om te bevestigen' }), c: fmtLL(app.S.sar.target) }),
    confirmed: () => ({ d: T({ en: 'Person, not moving · coordinates and walking route sent to {team}', nl: 'Persoon, beweegt niet · coördinaten en looproute naar {team}' }), c: fmtLL(app.S.sar.target) }),
  };

  // ---- Boot --------------------------------------------------------------------------------
  async function boot() {
    try {
      const cfgUrl = qs.get('config') || 'config.json';
      app.cfg = await fetch(cfgUrl, { cache: 'no-store' }).then(r => r.ok ? r.json() : {}).catch(() => ({}));
      app.D = await fetch('scenarios.json', { cache: 'no-store' }).then(r => r.json());
      app.geo = await fetch('geo.json').then(r => r.json()).catch(() => null);
    } catch (e) { return fatal('Could not load demo data: ' + e.message); }

    app.lang = qs.get('lang') || store.get('lang', app.cfg.lang || 'en');
    app.scenarioId = qs.get('scenario') || store.get('scenario', app.cfg.scenario || 'albina');
    if (!app.D.scenarios[app.scenarioId]) app.scenarioId = Object.keys(app.D.scenarios)[0];
    app.auto = (qs.get('auto') || store.get('auto', 'false')) === 'true';
    app.S = app.D.scenarios[app.scenarioId];
    applyAircraftOverrides();

    await initRenderer();
    buildPlan(true);
    initHUD();
    drawStatic();
    seek(Number(qs.get('chapter') || 0), false);
    $('loading').style.display = 'none';
    bindControls();
    startRemote();
    requestAnimationFrame(loop);
  }

  function applyAircraftOverrides() {
    const a = app.cfg.aircraft || {};
    for (const k of Object.keys(a)) app.D.aircraft[k] = a[k];
  }

  async function initRenderer() {
    const want = qs.get('renderer') || app.cfg.renderer || 'cesium';
    const tryOne = async (name, rcfg) => {
      $('loadText').textContent = name === 'google' ? 'Loading Google 3D…' : 'Loading 3D map…';
      const r = name === 'google' ? new GoogleRenderer() : new CesiumRenderer();
      const c = Object.assign({ modelUrl: app.cfg.modelUrl || '/drone.glb' }, rcfg);
      // Model nose direction: verified in Cesium (0). Google: press N during rehearsal if needed.
      c.noseOffset = Number(store.get('nose.' + name, c.noseOffset ?? (name === 'google' ? -90 : 0)));
      await r.init($('map'), c);
      app.r = r; app.rendererName = name;
    };
    try {
      if (want === 'google') await tryOne('google', Object.assign({}, app.cfg, { mockGoogle: qs.get('mockGoogle') || app.cfg.mockGoogle }));
      else await tryOne('cesium', app.cfg.cesium || app.cfg);
    } catch (e) {
      console.warn('[demo] renderer failed:', e.message);
      const fb = app.cfg.fallback;
      if (want === 'google' && fb) {
        $('map').innerHTML = '';
        await tryOne('cesium', fb);
        notice(T({ en: 'Google 3D unavailable (' + e.message + ') — using the satellite map', nl: 'Google 3D niet beschikbaar (' + e.message + ') — satellietkaart gebruikt' }));
      } else { fatal('Map could not start: ' + e.message); throw e; }
    }
  }

  function notice(msg) { const n = $('notice'); n.textContent = msg; n.style.display = 'block'; setTimeout(() => { n.style.display = 'none'; }, 12000); }
  function fatal(msg) { $('loadText').textContent = msg; $('loading').querySelector('.spin').style.display = 'none'; }

  // ---- Plan + derived values ---------------------------------------------------------------------
  function buildPlan() {
    const ids = app.D.chapters.map(c => c.id);
    app.plan = new E.Plan(app.S, app.D.aircraft, ids);
    computeVars();
    buildEvents();
  }
  const chIndex = (id) => app.D.chapters.findIndex(c => c.id === id);
  const chap = (id) => app.plan.ch[chIndex(id)];

  function computeVars() {
    const S = app.S, A = app.D.aircraft;
    let km = 0; for (let i = 1; i < S.transit.length; i++) km += E.dist(S.transit[i - 1], S.transit[i]) / 1000;
    const sar = chap('sar'), pat = chap('patrol'), tra = chap('transit');
    app.vars = {
      transit_km: Math.round(km), transit_min: Math.round(km * 1000 / A.cruise_ms / 60),
      standoff: S.standoff_m, area_km2: (sar.info.areaKm2 || 0).toFixed(1).replace('.', app.lang === 'nl' ? ',' : '.'),
      stop: tra.info.stop || '', unit: T(S.contact.unit), team: T(S.sar_info.team), recipients: T(S.contact.recipients),
      contactTitle: T(S.contact.title),
      unitEta: Math.round(pat.info.unitPath.len / pat.info.unitSpeed / 60),
      teamEta: Math.round(sar.info.teamWalk / 60)
    };
  }

  function buildEvents() {
    const evs = [];
    app.plan.ch.forEach((c, ci) => {
      for (const [name, t] of Object.entries(c.marks)) {
        if (!EV[name]) continue;
        if (name === 'stopApproach' && !c.info.stop) continue;
        evs.push({ key: name, t, ci, def: EV[name] });
      }
    });
    evs.sort((a, b) => a.t - b.t);
    app.events = evs;
  }

  // ---- Static overlays -------------------------------------------------------------------------------
  function drawStatic() {
    const r = app.r, S = app.S, A = app.D.aircraft;
    const safe = (f) => { try { f(); } catch (e) { console.warn('[demo] overlay', e.message); } };
    if (app.geo) {
      safe(() => r.line('border', app.geo.border, { color: '#e2e8f0', width: 1.6, ground: true, alpha: 0.7 }));
      if (app.rendererName === 'cesium') app.geo.districts.forEach((d, i) => safe(() => r.line('dist' + i, d.ring, { color: '#94a3b8', width: 1, ground: true, alpha: 0.35 })));
    }
    safe(() => r.line('focus', S.border_focus, { color: '#ef4444', width: 4, ground: true }));
    safe(() => r.line('geofence', S.geofence, { color: '#f59e0b', width: 2.5, ground: true, dash: 14 }));
    safe(() => r.line('planTransit', S.transit.map(p => [p[0], p[1], A.cruise_alt_m]), { color: '#38bdf8', width: 2, dash: 18, alpha: 0.8 }));
    safe(() => r.line('planPatrol', S.patrol.slice(0, S.boat.track_index + 3).map(p => [p[0], p[1], A.cruise_alt_m]), { color: '#4ade80', width: 2, dash: 18, alpha: 0.8 }));
    // Skip the town the forward base sits in (the FOB label names it already).
    const fobName = tr(S.names.fob);
    S.places.filter(p => p.name !== fobName).forEach((p, i) => safe(() => r.marker('pl' + i, { lat: p.lat, lon: p.lon,
      icon: p.kind === 'foreign' ? 'foreign' : p.kind === 'capital' ? 'capital' : 'town', side: p.kind === 'foreign' ? 'right' : undefined,
      color: p.kind === 'capital' ? '#fbbf24' : '#e2e8f0', label: p.name, size: 0.6, fontPx: p.kind === 'capital' ? 14 : 12, bold: p.kind === 'capital' })));
    safe(() => r.marker('hq', { lat: S.hq[0], lon: S.hq[1], icon: 'hq', color: '#0e7490', label: 'HQ · Zorg en Hoop', size: 0.75, hideBelow: 1500 }));
    safe(() => r.marker('fob', { lat: S.fob[0], lon: S.fob[1], icon: 'base', color: '#15803d', label: 'FOB · ' + T(S.names.fob), size: 0.75, hideBelow: 1500 }));
    $('credits').textContent = app.D.ui.credits + (app.rendererName === 'google' ? ' · Map data © Google' : '');
  }

  function legend() {
    const L = app.D.ui.legend;
    const item = (cls, color, txt) => `<div class="i"><span class="sw ${cls}" style="border-color:${color}"></span>${txt}</div>`;
    let h = item('', '#ef4444', T(L.border)) + item('dash', '#f59e0b', T(L.geofence)) + item('dash', '#38bdf8', T(L.route)) + item('', '#22d3ee', T(L.flown));
    if (app.ci >= chIndex('sar')) h += item('box', '#facc15', T(L.search)) + item('box', '#22d3ee', T(L.covered));
    $('legend').innerHTML = h;
  }

  // ---- Dynamic overlays: a pure function of simT ------------------------------------------------------
  function updateOverlays() {
    const r = app.r, S = app.S, t = app.simT, A = app.D.aircraft;
    const pc = chap('patrol'), sc = chap('sar');
    const iPatrol = chIndex('patrol'), iSar = chIndex('sar');
    const safe = (f) => { try { f(); } catch (e) { console.warn('[demo] overlay', e.message); } };

    // Flown track
    safe(() => r.line('trail', app.trail.length > 1 ? app.trail : null, { color: '#22d3ee', width: 4, glow: 0.25 }));

    // Border contact (patrol chapter)
    const m = pc.marks, showContact = app.ci >= iPatrol && app.ci < iSar && m.detect !== undefined && t >= m.detect;
    if (showContact) {
      const bp = new E.Path([S.boat.start, S.boat.cross, S.boat.end]);
      const f = E.clamp((t - m.detect) / (m.boatLanded - m.detect), 0, 1);
      const pos = bp.at(E.smooth01(f) * bp.len);
      app.boatPos = pos;
      safe(() => r.marker('boat', { lat: pos[0], lon: pos[1], icon: 'boat', color: '#ef4444', label: T({ en: 'Contact · 2 persons', nl: 'Contact · 2 personen' }), size: 0.8 }));
      safe(() => r.line('boatTrack', bp.slice(0, Math.max(1, E.smooth01(f) * bp.len)), { color: '#ef4444', width: 2, ground: true, dash: 8 }));
      const d = app.state;
      if (d && t < (m.unitOnScene || Infinity)) safe(() => r.line('sight', [[d.lat, d.lon, d.alt], [pos[0], pos[1], 0]], { color: '#fca5a5', width: 1.5, dash: 6, alpha: 0.8 }));
      else r.remove('sight');
      if (t >= m.alertSent) {
        const up = pc.info.unitPath, s = Math.min(up.len, (t - m.alertSent) * pc.info.unitSpeed);
        const u = up.at(s); app.unitPos = u;
        safe(() => r.marker('unit', { lat: u[0], lon: u[1], icon: 'police', color: '#3b82f6', label: T(S.contact.unit), size: 0.8 }));
        safe(() => r.line('unitTrack', up.slice(0, Math.max(1, s)), { color: '#60a5fa', width: 2, ground: true, dash: 8 }));
      } else { r.remove('unit'); r.remove('unitTrack'); }
    } else { ['boat', 'boatTrack', 'sight', 'unit', 'unitTrack'].forEach(id => r.remove(id)); app.boatPos = null; }

    // Search & rescue
    const sm = sc.marks, sarOn = app.ci >= iSar && app.ci <= iSar + 2;
    if (sarOn) {
      safe(() => r.polygon('sarArea', S.sar.area, { fill: '#facc15', alpha: 0.10 }));
      safe(() => r.line('sarOutline', S.sar.area.concat([S.sar.area[0]]), { color: '#facc15', width: 2, ground: true }));
      safe(() => r.marker('lkp', { lat: S.sar.lkp[0], lon: S.sar.lkp[1], icon: 'lkp', color: '#facc15', label: T({ en: 'Last known position', nl: 'Laatst bekende positie' }), size: 0.65 }));
      // planned lines
      if (!app.sarLinesDrawn) {
        S.sar.lines.forEach((l, i) => safe(() => r.line('sl' + i, [[l[0][0], l[0][1], A.search_alt_m], [l[1][0], l[1][1], A.search_alt_m]], { color: '#fde68a', width: 1.2, dash: 10, alpha: 0.7 })));
        app.sarLinesDrawn = true;
      }
      // coverage swaths
      const cov = coverage(t);
      S.sar.lines.forEach((l, i) => {
        const c = cov[i];
        if (!c) { r.remove('cov' + i); return; }
        safe(() => r.polygon('cov' + i, c, { fill: '#22d3ee', alpha: 0.22 }));
      });
      if (sm.hit !== undefined && t >= sm.hit) {
        const conf = t >= sm.confirmed;
        safe(() => r.marker('target', { lat: S.sar.target[0], lon: S.sar.target[1], icon: 'person', color: conf ? '#22c55e' : '#f59e0b',
          label: conf ? T({ en: 'Person · confirmed', nl: 'Persoon · bevestigd' }) : T({ en: 'Possible person', nl: 'Mogelijk persoon' }), size: 0.85, bold: true }));
      } else r.remove('target');
      if (sm.teamGo !== undefined && t >= sm.teamGo) {
        const tp = sc.info.teamPath, s = Math.min(tp.len, (t - sm.teamGo) * sc.info.teamSpeed), p = tp.at(s);
        app.teamPos = p;
        safe(() => r.marker('team', { lat: p[0], lon: p[1], icon: 'team', color: '#3b82f6', label: T(S.sar_info.team), size: 0.75 }));
        safe(() => r.line('teamRoute', [S.sar.team_start, S.sar.target], { color: '#e2e8f0', width: 2, ground: true, dash: 10, alpha: 0.85 }));
      } else { r.remove('team'); r.remove('teamRoute'); app.teamPos = null; }
    } else {
      ['sarArea', 'sarOutline', 'lkp', 'target', 'team', 'teamRoute'].forEach(id => r.remove(id));
      S.sar.lines.forEach((l, i) => { r.remove('cov' + i); r.remove('sl' + i); });
      app.sarLinesDrawn = false;
    }
  }

  // Searched strips: one rectangle per search line, as far as the drone has flown it.
  function coverage(t) {
    const out = [], sc = chap('sar'), S = app.S, half = (S.sar.spacing_m || 120) / 2;
    let covered = 0;
    for (const leg of sc.legs) {
      if (!leg.pattern) continue;
      if (t <= leg.t0) break;
      const tl = Math.min(t - leg.t0, leg.dur);
      // The confirmation pass continues straight along the same search line, so it also counts as searched.
      covered = Math.max(covered, leg.pattern.s0 + Math.min(tl, leg.dur) * app.D.aircraft.cruise_ms);
    }
    const pat = sc.legs.find(l => l.pattern && !l.pattern.partial);
    if (!pat) return out;
    const P = pat.pattern.path;
    S.sar.lines.forEach((l, i) => {
      const s0 = P.cum[2 * i], s1 = P.cum[2 * i + 1];
      if (covered <= s0) return;
      const a = P.at(s0), b = P.at(Math.min(covered, s1));
      if (E.dist(a, b) < 5) return;
      const h = E.bearing(a, b);
      out[i] = [E.dest(a, h - 90, half), E.dest(b, h - 90, half), E.dest(b, h + 90, half), E.dest(a, h + 90, half)];
    });
    return out;
  }

  // ---- Trail -----------------------------------------------------------------------------------------
  function rebuildTrail() {
    app.trail = [];
    for (let k = 0; k <= app.ci; k++) {
      const c = app.plan.ch[k];
      for (const leg of c.legs) {
        if (leg.t0 >= app.simT) break;
        if (!leg.airborne) continue;
        const end = Math.min(app.simT, leg.t0 + leg.dur), n = Math.max(2, Math.min(300, Math.ceil(leg.len / 150)));
        for (let j = 0; j <= n; j++) {
          const tt = leg.t0 + (end - leg.t0) * j / n, s = leg.st(Math.min(tt - leg.t0, leg.dur));
          app.trail.push([s.lat, s.lon, s.alt]);
        }
      }
    }
  }
  function pushTrail(s, scale) {
    if (!s.airborne && s.alt < 0.5) return;
    const last = app.trail[app.trail.length - 1];
    if (!last || E.dist(last, [s.lat, s.lon]) > Math.max(30, 3 * scale) || Math.abs(last[2] - s.alt) > 4) app.trail.push([s.lat, s.lon, s.alt]);
  }

  // ---- Chapters ------------------------------------------------------------------------------------------
  function chapterDoneTime(c) {
    const last = c.legs[c.legs.length - 1];
    return last.t0 + last.dur;
  }

  // pendingCi: chapter we are fading to. Lets fast double presses (clicker) count twice.
  function seek(i, fade = true) {
    i = E.clamp(i, 0, app.D.chapters.length - 1);
    app.pendingCi = i;
    const go = () => {
      app.pendingCi = null;
      buildPlan();
      app.ci = i; app.simT = app.plan.ch[i].tStart;
      app.firedKeys = new Set(app.events.filter(e => e.t <= app.simT + 1e-6 && e.ci < i).map(e => e.key));
      app.log = app.events.filter(e => app.firedKeys.has(e.key)).map(e => ({ key: e.key, t: e.t }));
      app.state = app.plan.stateAt(app.ci, app.simT);
      app.dispHdg = app.state.hdg;
      rebuildTrail();
      app.sarLinesDrawn = false;
      startChapter(true);
    };
    if (fade) fadeThrough(go); else go();
  }

  function next() {
    const n = app.D.chapters.length;
    if (app.pendingCi != null) { if (app.pendingCi < n - 1) seek(app.pendingCi + 1); return; }
    if (app.ci >= n - 1) return;
    if (app.waiting) {
      // Continue smoothly from wherever the drone actually is now.
      const s = app.plan.stateAt(app.ci, app.simT);
      app.plan.rebuildFrom(app.ci + 1, s, app.simT);
      buildEvents();
      app.ci += 1;
      app.simT = app.plan.ch[app.ci].tStart;
      startChapter(false);
    } else {
      seek(app.ci + 1);
    }
  }
  function prev() { seek((app.pendingCi != null ? app.pendingCi : app.ci) - 1); }

  function startChapter() {
    app.chapterRealT = 0; app.doneRealT = 0; app.waiting = false; app.playing = true;
    app.captionIdx = -1; app.closingShown = 0;
    const c = app.D.chapters[app.ci];
    titleCard();
    legend();
    document.body.classList.toggle('slide', c.id === 'report' || c.id === 'closing');
    $('report').style.display = c.id === 'report' ? 'block' : 'none';
    $('closing').style.display = c.id === 'closing' ? 'block' : 'none';
    if (c.id === 'report') renderReport();
    if (c.id === 'closing') renderClosing();
    $('checklist').style.display = 'none';
    setCaption('');
    renderLog();
    updateTop();
    updateOverlays();
    postStatus();
  }

  function titleCard() {
    const c = app.D.chapters[app.ci], n = app.D.chapters.length;
    $('tcNum').textContent = `${T(app.D.ui.chapter)} ${app.ci + 1} / ${n}`;
    $('tcTitle').textContent = T(c.title);
    $('tcSub').textContent = T(app.S.title);
    const tc = $('titlecard'); tc.classList.add('on');
    clearTimeout(app.tcTimer); app.tcTimer = setTimeout(() => tc.classList.remove('on'), 2600);
  }

  function fadeThrough(fn) {
    let f = $('fade');
    if (!f) { f = document.createElement('div'); f.id = 'fade'; document.body.appendChild(f); }
    f.style.opacity = '1';
    setTimeout(() => { fn(); setTimeout(() => { f.style.opacity = '0'; }, 120); }, 360);
  }

  // ---- Captions -------------------------------------------------------------------------------------------
  function captionIndex() {
    const def = app.D.chapters[app.ci], c = app.plan.ch[app.ci];
    const caps = def.captions || [];
    if (!caps.length) return -1;
    if (c.isStatic) return Math.min(caps.length - 1, Math.floor(app.chapterRealT / 7.5));
    const anchors = (c.anchors || []).map(n => c.marks[n]).filter(v => v !== undefined);
    let idx = -1;
    anchors.forEach((t, k) => { if (app.simT >= t - 1e-6) idx = k; });
    return Math.min(idx, caps.length - 1);
  }
  function setCaption(text) {
    const el = $('captionText');
    if (el.dataset.text === text) return;
    el.dataset.text = text;
    el.classList.remove('on');
    clearTimeout(app.capTimer);
    if (!text) return;
    app.capTimer = setTimeout(() => { el.textContent = text; el.classList.add('on'); }, 250);
  }

  // ---- Events -----------------------------------------------------------------------------------------------
  function fireEvents(prevT) {
    for (const e of app.events) {
      if (e.t > app.simT) break;
      if (app.firedKeys.has(e.key)) continue;
      if (e.ci > app.ci) continue;
      if (e.t < prevT - 5 && e.ci < app.ci) { app.firedKeys.add(e.key); app.log.push({ key: e.key, t: e.t }); continue; }
      app.firedKeys.add(e.key);
      app.log.push({ key: e.key, t: e.t });
      renderLog(e.key);
      if (e.def.banner) banner(e);
      chime(e.def.k);
    }
  }
  function renderLog(newKey) {
    const ul = $('events'), items = app.log.slice(-14).reverse();
    ul.innerHTML = items.map(l => {
      const d = EV[l.key];
      return `<li class="${d.k}${l.key === newKey ? ' new' : ''}"><time>${fmtClock(l.t).slice(0, 5)}</time><span class="dot"></span><span>${T(d.t)}</span></li>`;
    }).join('');
  }
  function banner(e) {
    const b = $('banner'), det = BANNER_DETAIL[e.key] ? BANNER_DETAIL[e.key]() : { d: '', c: '' };
    const kind = e.def.k === 'alert' ? 'alert' : e.def.k === 'ok' ? 'ok' : 'warn';
    const kword = kind === 'alert' ? T({ en: 'ALERT', nl: 'MELDING' }) : kind === 'ok' ? T({ en: 'CONFIRMED', nl: 'BEVESTIGD' }) : T({ en: 'TASK', nl: 'OPDRACHT' });
    b.innerHTML = `<div class="b ${kind}"><div class="k">${kword} · ${fmtClock(e.t)}</div><div class="t">${T(e.def.t)}</div>` +
      (det.d ? `<div class="d">${det.d}</div>` : '') + (det.c ? `<div class="c">${det.c}</div>` : '') + '</div>';
    b.classList.add('on');
    clearTimeout(app.bannerTimer); app.bannerTimer = setTimeout(() => b.classList.remove('on'), 7000);
  }
  let actx = null;
  function chime(kind) {
    if (app.muted || (kind !== 'alert' && kind !== 'ok')) return;
    try {
      actx = actx || new (window.AudioContext || window.webkitAudioContext)();
      const notes = kind === 'alert' ? [880, 660, 880] : [660, 990];
      notes.forEach((f, i) => {
        const o = actx.createOscillator(), g = actx.createGain(), t0 = actx.currentTime + i * 0.16;
        o.type = 'sine'; o.frequency.value = f; g.gain.setValueAtTime(0, t0);
        g.gain.linearRampToValueAtTime(0.12, t0 + 0.02); g.gain.exponentialRampToValueAtTime(0.0001, t0 + 0.28);
        o.connect(g).connect(actx.destination); o.start(t0); o.stop(t0 + 0.3);
      });
    } catch (e) { /* audio not available */ }
  }

  // ---- HUD ------------------------------------------------------------------------------------------------------
  function initHUD() {
    const U = app.D.ui;
    $('brand').textContent = U.brand;
    $('acTitle').textContent = T(U.aircraft);
    $('evTitle').textContent = T(U.events);
    $('acName').textContent = T(app.D.aircraft.name);
    $('simBadge').textContent = T(U.simulation);
    $('clockLbl').textContent = T(U.mission_time);
    $('bLang').textContent = app.lang.toUpperCase();
    $('bAuto').classList.toggle('on', app.auto);
    document.documentElement.lang = app.lang;
  }

  function updateTop() {
    const c = app.D.chapters[app.ci];
    $('scenTitle').textContent = T(app.S.title);
    $('chapTitle').textContent = `${app.ci + 1}/${app.D.chapters.length} · ${T(c.title)}`;
  }

  function updateHUD(s, leg) {
    const U = app.D.ui, P = U.phases;
    $('clock').textContent = fmtClock(app.simT);
    const sc = leg.scale;
    const chip = $('scaleChip');
    if (sc > 1.01 && !app.plan.ch[app.ci].isStatic) { chip.hidden = false; chip.textContent = T(U.time_x) + (sc >= 10 ? Math.round(sc) : sc); } else chip.hidden = true;
    const firstSortie = app.ci < chIndex('patrol') && !(leg.phase === 'swap' && leg.open);
    const base = firstSortie ? app.S.hq : app.S.fob;
    const dBase = E.dist([s.lat, s.lon], base) / 1000;
    const batt = s.battery, bc = batt > 40 ? 'ok' : batt > app.D.aircraft.reserve_pct ? 'warn' : 'bad';
    const bcol = batt > 40 ? '#22c55e' : batt > app.D.aircraft.reserve_pct ? '#f59e0b' : '#ef4444';
    const linkQ = s.airborne ? Math.max(2, 5 - Math.floor(dBase / 18)) : 5;
    const bars = [1, 2, 3, 4, 5].map((k, i) => `<i class="${k <= linkQ ? 'on' : ''}" style="height:${4 + i * 3}px"></i>`).join('');
    const ft = Math.floor(s.flightSec);
    const phaseName = P[s.phase] ? T(P[s.phase]) : (P[leg.phase] ? T(P[leg.phase]) : s.phase);
    const rows = [
      [T(U.phase), `<b>${phaseName}</b>`],
      [T(U.alt), `<b>${Math.round(s.alt)} m</b>`],
      [T(U.speed), `<b>${Math.round(s.spd * 3.6)} km/h</b>`],
      [T(U.heading), `<b>${String(Math.round(s.hdg)).padStart(3, '0')}°</b>`],
      [T(U.battery), `<b class="${bc}"><span class="batt"><i style="width:${batt.toFixed(0)}%;background:${bcol}"></i></span>${batt.toFixed(0)}%</b>`],
      [T(U.flight_time), `<b>${String(Math.floor(ft / 3600)).padStart(2, '0')}:${String(Math.floor(ft / 60) % 60).padStart(2, '0')}</b>`],
      [T(U.from_base), `<b>${dBase.toFixed(1)} km</b>`],
      [T(U.datalink), `<b><span class="bars">${bars}</span></b>`],
      [T(U.gps), `<b class="ok">3D · ${14 + Math.round(Math.sin(app.simT / 97) * 1.4)}</b>`],
      [T(U.geofence), `<b class="ok" style="font-family:var(--font);font-size:12px">${T(U.active)}</b>`]
    ];
    $('acRows').innerHTML = rows.map(([a, b]) => `<div class="r"><span>${a}</span>${b}</div>`).join('');
  }

  // Launch checklist + battery swap progress overlays
  function updateOverlaysUI(s, leg, tl) {
    const id = app.D.chapters[app.ci].id;
    const cl = $('checklist');
    if (id === 'launch' && leg.type === 'hold' && !s.airborne && app.chapterRealT > 2.4) {
      const items = app.D.ui.checks, done = Math.floor(tl / 1.15);
      cl.style.display = 'block';
      cl.innerHTML = `<h4>${T({ en: 'PRE-FLIGHT CHECK', nl: 'VLUCHTVOORBEREIDING' })}</h4>` +
        items.map((c, k) => `<div class="c ${k < done ? 'done' : ''}"><i>${k < done ? '✓' : ''}</i>${T(c)}</div>`).join('');
    } else cl.style.display = 'none';
    const sw = $('swap');
    if (leg.phase === 'swap' && tl < leg.dur) {
      sw.style.display = 'block';
      $('swapText').textContent = T({ en: 'Battery swap', nl: 'Batterijwissel' }) + ' · ' + Math.ceil((leg.dur - tl) / 60) + ' min';
      $('swapBar').style.width = (tl / leg.dur * 100).toFixed(0) + '%';
    } else sw.style.display = 'none';
  }

  // ---- Report + closing ---------------------------------------------------------------------------------------
  function renderReport() {
    const U = app.D.ui.report, p = app.plan;
    const iRet = chIndex('return'), rc = p.ch[iRet];
    const endT = chapterDoneTime(rc);
    let sorties = 0, flightSec = 0;
    for (let k = 0; k <= iRet; k++) for (const leg of p.ch[k].legs) {
      if (leg.sortieBreak && leg.airborne) sorties++;
      if (leg.airborne) flightSec += leg.dur;
    }
    sorties = Math.max(sorties + 1, 2);
    const km = p.distanceTo(iRet, endT) / 1000;
    const sc = chap('sar'), pc = chap('patrol');
    const foundMin = Math.round((sc.marks.hit - sc.marks.sarTask) / 60);
    const area = (sc.info.coveredKm2 || 0).toFixed(1);
    const dec = (v) => app.lang === 'nl' ? String(v).replace('.', ',') : String(v);
    const stat = (k, v) => `<div class="stat"><span>${T(k)}</span><b>${v}</b></div>`;
    const keys = ['takeoff1', 'landed1', 'takeoff2', 'detect', 'alertSent', 'unitOnScene', 'sarTask', 'hit', 'confirmed', 'landed2'];
    const rows = app.events.filter(e => keys.includes(e.key)).map(e => `<tr><td class="t">${fmtClock(e.t).slice(0, 5)}</td><td>${T(e.def.t)}</td></tr>`).join('');
    $('report').innerHTML = `<h2>${T(U.title)}</h2><div class="sub">${T(app.S.title)} · ${new Date().toLocaleDateString(app.lang === 'nl' ? 'nl-NL' : 'en-GB')}</div>
      <div class="stats">
        ${stat(U.flights, sorties)}${stat(U.distance, dec(km.toFixed(0)) + ' km')}${stat(U.flight_time, Math.floor(flightSec / 3600) + ' h ' + String(Math.round(flightSec / 60) % 60).padStart(2, '0') + ' min')}
        ${stat(U.border, dec(pc.info.patrolKm.toFixed(1)) + ' km')}${stat(U.area, dec(area) + ' km²')}${stat(U.alerts, 2)}
        ${stat(U.found, foundMin + ' min')}
      </div>
      <h3 style="margin:6px 0 6px;font-size:13px;letter-spacing:.12em;color:var(--mut)">${T(U.timeline).toUpperCase()}</h3>
      <table>${rows}</table>
      <div class="note">${T(U.note)} ${T(app.D.ui.simulation)}.</div>
      <button onclick="window.print()">${T(U.print)}</button>`;
  }
  function renderClosing() {
    const c = app.D.chapters[app.ci];
    $('closing').innerHTML = `<h2>${T(c.title)}</h2><div class="sub">DAMbv · InfraDrone</div><ul>` +
      (c.points || []).map((p, k) => `<li style="animation-delay:${(0.6 + k * 0.9).toFixed(1)}s">${T(p)}</li>`).join('') +
      `</ul><div class="who">DAMbv B.V. · Groningen, Nederland</div>`;
  }

  // ---- Camera --------------------------------------------------------------------------------------------------
  const MANUAL = ['auto', 'chase', 'top', 'orbit', 'free'];
  function desiredCamera(s, leg) {
    const c = desiredCameraRaw(s, leg);
    // Offline/satellite imagery is ~10 m per pixel: keep close-up views further back there.
    if (app.rendererName === 'cesium' && c.range < 900) c.range = Math.min(900, c.range * 2.4);
    return c;
  }
  function desiredCameraRaw(s, leg) {
    const S = app.S, rt = app.realT;
    let mode = app.camManual === 'auto' ? leg.cam : app.camManual;
    const dronePt = { lat: s.lat, lon: s.lon, alt: s.alt };
    const mid = (a, b) => ({ lat: (a[0] + b[0]) / 2, lon: (a[1] + b[1]) / 2, alt: 0 });
    switch (mode) {
      case 'overview': {
        const far = S.patrol[S.boat.track_index];
        const c = mid(S.hq, far), d = E.dist(S.hq, far);
        return Object.assign(c, { heading: 8 * Math.sin(rt / 14), pitch: -66, range: d * 1.35 + 25000 });
      }
      case 'overviewEnd': {
        const c = mid(S.fob, S.sar.target), d = E.dist(S.fob, S.sar.target);
        return Object.assign(c, { heading: rt * 2, pitch: -50, range: Math.max(30000, d * 2.6) });
      }
      case 'orbitClose': return Object.assign(dronePt, { heading: s.hdg + 140 + rt * 5, pitch: -14, range: 85 });
      case 'chase': return Object.assign(dronePt, { heading: app.dispHdg, pitch: -16, range: 170 });
      case 'chaseHigh': return Object.assign(dronePt, { heading: app.dispHdg, pitch: -36, range: 2300 });
      case 'contact': {
        const b = app.boatPos || S.boat.start;
        const c = mid([s.lat, s.lon], b), d = E.dist([s.lat, s.lon], b);
        return Object.assign(c, { heading: E.bearing([s.lat, s.lon], b), pitch: -32, range: Math.max(650, d * 1.35) });
      }
      case 'observe': {
        const b = app.boatPos || S.boat.cross;
        return { lat: b[0], lon: b[1], alt: 0, heading: E.bearing([s.lat, s.lon], b), pitch: -36, range: 560 };
      }
      case 'handover': {
        const b = S.boat.end, u = app.unitPos || S.ground_unit.path[0];
        const c = mid(b, u), d = E.dist(b, u);
        return Object.assign(c, { heading: 0, pitch: -58, range: Math.max(1600, d * 1.8) });
      }
      case 'topArea': {
        const a = S.sar.area, c = { lat: (a[0][0] + a[2][0]) / 2, lon: (a[0][1] + a[2][1]) / 2, alt: 0 };
        return Object.assign(c, { heading: 0, pitch: -70, range: 3400 });
      }
      case 'confirm': return { lat: S.sar.target[0], lon: S.sar.target[1], alt: 0, heading: 30 + rt * 8, pitch: -38, range: 430 };
      case 'team': {
        const p = app.teamPos || S.sar.team_start, c = mid(S.sar.target, p), d = E.dist(S.sar.target, p);
        return Object.assign(c, { heading: 0, pitch: -56, range: Math.max(1500, d * 1.7) });
      }
      case 'top': return Object.assign(dronePt, { heading: 0, pitch: -89, range: 900 });
      case 'orbit': return Object.assign(dronePt, { heading: rt * 10, pitch: -26, range: 320 });
      default: return Object.assign(dronePt, { heading: app.dispHdg, pitch: -20, range: 200 });
    }
  }
  function smoothCamera(des, dt) {
    if (!app.cam) { app.cam = Object.assign({}, des); return app.cam; }
    const c = app.cam, far = E.dist([c.lat, c.lon], [des.lat, des.lon]) > 40000;
    const k = 1 - Math.exp(-dt / (far ? 1.1 : 0.55));
    c.lat += (des.lat - c.lat) * k; c.lon += (des.lon - c.lon) * k; c.alt += ((des.alt || 0) - (c.alt || 0)) * k;
    c.heading = E.norm360(c.heading + E.angDiff(c.heading, des.heading) * k);
    c.pitch += (des.pitch - c.pitch) * k;
    c.range = Math.exp(Math.log(c.range) + (Math.log(des.range) - Math.log(c.range)) * k);
    return c;
  }

  // ---- Main loop ------------------------------------------------------------------------------------------------
  let lastFrame = performance.now();
  const DT_CAP = Number(qs.get('dtcap') || 0.1);
  function loop(now) {
    requestAnimationFrame(loop);
    // Cap per-frame time so a stall never makes the drone jump (test param ?dtcap= for slow software rendering).
    const dt = Math.min(DT_CAP, (now - lastFrame) / 1000); lastFrame = now;
    app.realT += dt;
    try { frame(dt); } catch (e) { console.error('[demo] frame', e); }
  }

  function frame(dt) {
    const c = app.plan.ch[app.ci], def = app.D.chapters[app.ci];
    let la = app.plan.legAt(app.ci, app.simT);
    const prevT = app.simT;
    if (app.playing) {
      app.chapterRealT += dt;
      const done = c.isStatic ? app.chapterRealT >= ((def.captions || []).length * 7.5 + 2) : app.simT >= chapterDoneTime(c);
      const scale = done ? 1 : la.leg.scale;
      if (!c.isStatic) app.simT += dt * scale;
      if (done && !app.waiting) { app.waiting = true; app.doneRealT = 0; }
      if (app.waiting) {
        app.doneRealT += dt;
        if (app.auto && app.ci < app.D.chapters.length - 1) {
          const wait = def.id === 'report' ? 15 : def.id === 'closing' ? 1e9 : 3.5;
          if (app.doneRealT > wait) next();
        }
      }
      la = app.plan.legAt(app.ci, app.simT);
    }
    const s = app.plan.stateAt(app.ci, app.simT);
    app.state = s;
    fireEvents(prevT);

    // Display smoothing for heading/roll (fast time scales make raw values jumpy).
    const kH = 1 - Math.exp(-dt / 0.25);
    app.dispHdg = E.norm360(app.dispHdg + E.angDiff(app.dispHdg, s.hdg) * kH);
    app.dispRoll += (s.roll - app.dispRoll) * kH;
    const shown = Object.assign({}, s, { hdg: app.dispHdg, roll: app.dispRoll * (la.leg.scale > 20 ? 0.3 : 1) });

    try { app.r.setDrone(shown, true); } catch (e) { console.warn('[demo] drone', e.message); }
    const cam = smoothCamera(desiredCamera(s, la.leg), dt);
    try { app.r.setCamera(cam); } catch (e) { console.warn('[demo] camera', e.message); }

    pushTrail(s, la.leg.scale);
    if (app.realT - app.lastOverlay > 0.25) { app.lastOverlay = app.realT; updateOverlays(); }
    if (app.realT - app.lastHud > 0.1) {
      app.lastHud = app.realT;
      updateHUD(s, la.leg);
      updateOverlaysUI(s, la.leg, la.tl);
      const ci = captionIndex();
      setCaption(ci >= 0 ? T(def.captions[ci]) : '');
      $('hint').textContent = T(app.D.ui.press_next);
      $('hint').classList.toggle('on', app.waiting && !app.auto && app.ci < app.D.chapters.length - 1);
      $('bPlay').textContent = app.playing ? '❚❚' : '▶';
    }
    sensorFrame(dt, s, la);
  }

  // ---- Sensor feed ------------------------------------------------------------------------------------------------
  let feed = null;
  function sensorFrame(dt, s, la) {
    feed = feed || new IRFeed($('sensor'));
    const leg = la.leg, id = app.D.chapters[app.ci].id, t = app.simT;
    const targets = [];
    const pm = chap('patrol').marks, sm = chap('sar').marks;
    let rec = false;
    if (id === 'patrol' && pm.detect !== undefined && t >= pm.detect) {
      rec = true;
      let y = 0.5, x = 0.5 + 0.03 * Math.sin(app.realT * 0.7);
      if (leg.contact) y = 0.14 + 0.36 * E.clamp(la.tl / leg.dur, 0, 1);
      const landed = t >= pm.boatLanded;
      targets.push({ kind: 'boat', x, y, angle: landed ? 75 : 95, box: true, color: '#ef4444',
        label: landed ? T({ en: 'BOAT · 2 PERSONS ON FOOT', nl: 'BOOT · 2 PERSONEN TE VOET' }) : T({ en: 'BOAT · 2 PERSONS', nl: 'BOOT · 2 PERSONEN' }), conf: 0.93 });
    }
    if (id === 'sar' && sm.hit !== undefined && t >= sm.hit) {
      rec = true;
      const conf = t >= sm.confirmed;
      let y = 0.5;
      if (leg.type === 'route') y = 0.16 + 0.34 * E.clamp(la.tl / leg.dur * 2, 0, 1);
      targets.push({ kind: 'person', x: 0.5 + 0.02 * Math.sin(app.realT), y, box: true, color: conf ? '#22c55e' : '#f59e0b',
        label: conf ? T({ en: 'PERSON · CONFIRMED', nl: 'PERSOON · BEVESTIGD' }) : T({ en: 'POSSIBLE PERSON', nl: 'MOGELIJK PERSOON' }), conf: conf ? 0.97 : 0.84 });
    }
    const mode = !s.airborne || leg.sensor === 'off' ? 'off' : leg.sensor || 'terrain';
    feed.draw(dt, { mode, speed: s.spd * Math.min(leg.scale, 6), hdg: s.hdg, alt: s.alt, lat: s.lat, lon: s.lon, clock: fmtClock(t),
      targets, rec, simLabel: T(app.D.ui.sim_sensor), standby: T({ en: 'SENSOR STANDBY', nl: 'SENSOR STAND-BY' }) });
  }

  // ---- Controls -----------------------------------------------------------------------------------------------------
  function setLang(l) {
    app.lang = l; store.set('lang', l);
    computeVars(); initHUD(); updateTop(); legend(); renderLog();
    if (app.D.chapters[app.ci].id === 'report') renderReport();
    if (app.D.chapters[app.ci].id === 'closing') { renderClosing(); document.querySelectorAll('#closing li').forEach(li => { li.style.animationDelay = '0s'; }); }
    $('captionText').dataset.text = '';
    // Re-label static markers in the new language
    try { app.r.marker('fob', { lat: app.S.fob[0], lon: app.S.fob[1], label: 'FOB · ' + T(app.S.names.fob) }); } catch (e) { /* ignore */ }
    postStatus();
  }
  function command(k, arg) {
    switch (k) {
      case 'next': next(); break;
      case 'prev': prev(); break;
      case 'home': seek(0); break;
      case 'restart': seek(app.ci); break;
      case 'goto': seek(Number(arg)); break;
      case 'play': app.playing = !app.playing; break;
      case 'pause': app.playing = false; break;
      case 'resume': app.playing = true; break;
      case 'auto': app.auto = arg !== undefined ? arg === true || arg === 'on' || arg === 'true' : !app.auto; store.set('auto', app.auto); $('bAuto').classList.toggle('on', app.auto); break;
      case 'lang': setLang(arg || (app.lang === 'en' ? 'nl' : 'en')); break;
      case 'scenario': {
        store.set('scenario', arg); const u = new URL(location.href); u.searchParams.set('scenario', arg); u.searchParams.set('lang', app.lang);
        u.searchParams.delete('chapter'); location.href = u.href; break;
      }
      case 'cam': {
        const i = MANUAL.indexOf(app.camManual); app.camManual = arg || MANUAL[(i + 1) % MANUAL.length];
        app.r.setFree(app.camManual === 'free');
        $('bCam').textContent = app.camManual === 'auto' ? 'CAM' : app.camManual.toUpperCase();
        $('bCam').classList.toggle('on', app.camManual !== 'auto'); break;
      }
      case 'nose': {
        const cur = Number(store.get('nose.' + app.rendererName, app.rendererName === 'google' ? -90 : 0)), nv = ((cur + 90 + 180) % 360) - 180;
        store.set('nose.' + app.rendererName, nv); app.r.setNose(nv); notice('Model heading offset: ' + nv + '°'); break;
      }
      case 'black': { const b = $('black'); b.style.display = b.style.display === 'block' ? 'none' : 'block'; break; }
      case 'hud': document.body.classList.toggle('hud-hidden'); break;
      case 'mute': app.muted = !app.muted; notice(app.muted ? 'Sound off' : 'Sound on'); break;
      case 'reload': { const u = new URL(location.href); u.searchParams.set('chapter', String(app.ci)); u.searchParams.set('lang', app.lang); location.href = u.href; break; }
      case 'full': if (!document.fullscreenElement) document.documentElement.requestFullscreen().catch(() => {}); else document.exitFullscreen(); break;
    }
    postStatus();
  }

  function bindControls() {
    document.addEventListener('keydown', (e) => {
      if (e.target && /INPUT|TEXTAREA/.test(e.target.tagName)) return;
      const k = e.key;
      const map = { ArrowRight: 'next', PageDown: 'next', ' ': 'next', ArrowLeft: 'prev', PageUp: 'prev', Home: 'home',
        a: 'auto', A: 'auto', p: 'play', P: 'play', r: 'restart', R: 'restart', l: 'lang', L: 'lang', c: 'cam', C: 'cam',
        f: 'full', F: 'full', b: 'black', B: 'black', '.': 'black', h: 'hud', H: 'hud', m: 'mute', M: 'mute', n: 'nose', N: 'nose', F5: 'next', Escape: null };
      if (k in map) { e.preventDefault(); if (map[k]) command(map[k]); return; }
      if (/^[1-9]$/.test(k)) { e.preventDefault(); command('goto', Number(k) - 1); }
    });
    document.querySelectorAll('#controls button').forEach(b => b.addEventListener('click', () => command(b.dataset.k)));
    let idle;
    const show = () => { $('controls').classList.add('on'); clearTimeout(idle); idle = setTimeout(() => $('controls').classList.remove('on'), 2500); };
    document.addEventListener('mousemove', show);
  }

  // ---- Remote control from the desktop app (optional) ---------------------------------------------------------------
  // Commands from the desktop "Suriname Demo" tab. The first request (after=-1) only
  // returns the latest id, so commands sent before this page opened are never replayed.
  let lastCmd = -1, remoteFails = 0;
  function startRemote() {
    const tick = () => {
      fetch('/demo/cmd?after=' + lastCmd, { cache: 'no-store' }).then(r => r.ok ? r.json() : Promise.reject(r.status))
        .then(res => {
          remoteFails = 0;
          for (const c of res.cmds || []) command(c.cmd, c.arg === null ? undefined : c.arg);
          lastCmd = res.last;
        })
        .catch(() => { remoteFails++; })
        .finally(() => { if (remoteFails < 5) setTimeout(tick, 400); });
    };
    tick();
    setInterval(postStatus, 1000);
  }
  function postStatus() {
    if (remoteFails >= 5 || !app.plan) return;
    const c = app.D.chapters[app.ci];
    const nxt = app.D.chapters[app.ci + 1];
    const body = JSON.stringify({ chapter: app.ci, chapters: app.D.chapters.length, id: c.id, title: T(c.title), playing: app.playing,
      auto: app.auto, waiting: app.waiting, lang: app.lang, scenario: app.scenarioId, clock: fmtClock(app.simT), renderer: app.rendererName,
      notes: T(c.notes), next: nxt ? T(nxt.title) : '', caption: $('captionText').dataset.text || '' });
    fetch('/demo/status', { method: 'POST', body, headers: { 'Content-Type': 'application/json' } }).catch(() => {});
  }

  window.addEventListener('error', (e) => console.error('[demo] uncaught', e.message));
  window.__demo = app; // for testing
  boot();
})();
