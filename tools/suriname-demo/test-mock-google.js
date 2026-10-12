// Test-only stand-in for google.maps.maps3d that enforces the documented rules,
// so the Google renderer code paths can be exercised without network access.
(function () {
  const stats = window.__gmStats = { created: {}, setPath: 0, errors: [] };
  const fail = (m) => { stats.errors.push(m); throw new Error(m); };
  const isLL = (p) => p && typeof p.lat === 'number' && typeof p.lng === 'number' && isFinite(p.lat) && isFinite(p.lng);
  const hex = /^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$/;
  const AltitudeMode = { ABSOLUTE: 'ABSOLUTE', CLAMP_TO_GROUND: 'CLAMP_TO_GROUND', RELATIVE_TO_GROUND: 'RELATIVE_TO_GROUND', RELATIVE_TO_MESH: 'RELATIVE_TO_MESH' };
  const MapMode = { HYBRID: 'HYBRID', SATELLITE: 'SATELLITE' };
  const checkMode = (m) => { if (m !== undefined && !AltitudeMode[m]) fail('bad altitudeMode ' + m); };
  function base(name) {
    stats.created[name] = (stats.created[name] || 0) + 1;
    const el = document.createElement('div'); el.dataset.gm = name; return el;
  }
  class Map3DElement {
    constructor(o) {
      const el = base('Map3DElement'); el.style.cssText = 'position:absolute;inset:0;background:#1d3b2a';
      const st = {};
      const def = (k, check) => Object.defineProperty(el, k, { get: () => st[k], set: (v) => { check && check(v); st[k] = v; } });
      def('center', (v) => { if (!isLL(v)) fail('center needs lat/lng'); });
      def('heading', (v) => { if (!isFinite(v)) fail('heading NaN'); });
      def('tilt', (v) => { if (!(v >= 0 && v <= 90)) fail('tilt out of range ' + v); });
      def('range', (v) => { if (!(v > 0)) fail('range must be > 0: ' + v); });
      def('roll'); def('mode'); def('defaultUIHidden');
      Object.assign(el, o);
      return el;
    }
  }
  function withPath(name, opts, allowEmptyCtor) {
    const el = base(name); let path;
    Object.defineProperty(el, 'path', { get: () => path, set: (v) => {
      const arr = Array.from(v || []);
      if (arr.length === 0) fail(`<${name}>: Cannot set property "path" to : empty iterable`);
      arr.forEach(p => { if (!isLL(p)) fail(name + ' path point invalid'); });
      stats.setPath++; path = arr; } });
    const o = Object.assign({}, opts); const p = o.path; delete o.path;
    if (o.strokeColor && !hex.test(o.strokeColor)) fail(name + ' strokeColor ' + o.strokeColor);
    if (o.outerColor && !hex.test(o.outerColor)) fail(name + ' outerColor ' + o.outerColor);
    if (o.fillColor && !hex.test(o.fillColor)) fail(name + ' fillColor ' + o.fillColor);
    checkMode(o.altitudeMode);
    Object.assign(el, o);
    if (p !== undefined) el.path = p;
    return el;
  }
  class Polyline3DElement { constructor(o) { return withPath('Polyline3DElement', o); } }
  class Polygon3DElement { constructor(o) { return withPath('Polygon3DElement', o); } }
  class Marker3DElement {
    constructor(o) {
      const el = base('Marker3DElement'); let pos;
      Object.defineProperty(el, 'position', { get: () => pos, set: (v) => { if (!isLL(v)) fail('marker position invalid'); pos = v; } });
      checkMode(o.altitudeMode);
      if (o.label !== undefined && typeof o.label !== 'string') fail('marker label must be string');
      const p = o.position; const oo = Object.assign({}, o); delete oo.position; Object.assign(el, oo); el.position = p;
      return el;
    }
  }
  class Model3DElement {
    constructor(o) {
      const el = base('Model3DElement'); let pos;
      if (!/\.glb($|\?)/.test(String(o.src))) fail('model src must be .glb');
      Object.defineProperty(el, 'position', { get: () => pos, set: (v) => { if (!isLL(v)) fail('model position invalid'); pos = v; } });
      checkMode(o.altitudeMode);
      const p = o.position; const oo = Object.assign({}, o); delete oo.position; Object.assign(el, oo); el.position = p;
      return el;
    }
  }
  window.google = { maps: { maps3d: { Map3DElement, Polyline3DElement, Polygon3DElement, Marker3DElement, Model3DElement, AltitudeMode, MapMode } } };
  // Make <div>.append behave like Map3DElement.append
  setTimeout(() => window.__idGoogleReady && window.__idGoogleReady(), 50);
})();
