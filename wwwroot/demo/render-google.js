// render-google.js — Google Maps JavaScript API 3D Maps renderer (photorealistic where available).
// Same API as render-cesium.js. Notes from Google's 3D Maps reference:
//  - Polyline3DElement rejects an EMPTY path, so lines are only created with 2+ points.
//  - Model3DElement only supports .glb without extensions (our drone.glb has none).
//  - Map3DElement camera: center {lat,lng,altitude}, heading, tilt (0 = straight down), range.
(function () {
  'use strict';

  function enumOr(obj, name, fallback) { return (obj && obj[name] !== undefined) ? obj[name] : fallback; }
  function hex(c) { return c.length === 4 ? '#' + c[1] + c[1] + c[2] + c[2] + c[3] + c[3] : c; }
  function hexA(c, a) {
    const h = hex(c), aa = Math.round(Math.max(0, Math.min(1, a)) * 255).toString(16).padStart(2, '0');
    return h + aa;
  }

  class GoogleRenderer {
    init(el, cfg) {
      this.cfg = cfg; this.els = {}; this.free = false;
      this.nose = cfg.noseOffset ?? -90;
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error('Google 3D Maps did not start within 15 s')), 15000);
        window.gm_authFailure = () => { clearTimeout(timer); reject(new Error('Google rejected the API key')); };
        window.__idGoogleReady = () => {
          try {
            const M = this.M = google.maps.maps3d;
            this.ALT_REL = enumOr(M.AltitudeMode, 'RELATIVE_TO_GROUND', 'RELATIVE_TO_GROUND');
            this.ALT_CLAMP = enumOr(M.AltitudeMode, 'CLAMP_TO_GROUND', 'CLAMP_TO_GROUND');
            this.map = new M.Map3DElement({
              center: { lat: 5.6, lng: -55.2, altitude: 0 }, range: 250000, tilt: 45, heading: 0,
              mode: enumOr(M.MapMode, 'HYBRID', 'HYBRID'), defaultUIHidden: true
            });
            el.appendChild(this.map);
            clearTimeout(timer); resolve();
          } catch (e) { clearTimeout(timer); reject(e); }
        };
        if (cfg.mockGoogle) { const s = document.createElement('script'); s.src = cfg.mockGoogle; document.head.appendChild(s); return; }
        const s = document.createElement('script');
        s.async = true;
        s.src = 'https://maps.googleapis.com/maps/api/js?key=' + encodeURIComponent(cfg.googleKey) +
                '&libraries=maps3d&callback=__idGoogleReady&loading=async&v=weekly';
        s.onerror = () => { clearTimeout(timer); reject(new Error('Could not load Google Maps (internet?)')); };
        document.head.appendChild(s);
      });
    }

    setNose(deg) { this.nose = deg; }

    setDrone(s, visible) {
      const M = this.M;
      if (!this.drone) {
        this.drone = new M.Model3DElement({
          src: new URL(this.cfg.modelUrl || '/drone.glb', location.href).href,
          position: { lat: s.lat, lng: s.lon, altitude: Math.max(0.3, s.alt) },
          altitudeMode: this.ALT_REL, orientation: { heading: 0, tilt: 0, roll: 0 }, scale: 1
        });
        this.droneShown = false;
      }
      if (visible && !this.droneShown) { this.map.append(this.drone); this.droneShown = true; }
      if (!visible && this.droneShown) { this.drone.remove(); this.droneShown = false; }
      if (!visible) return;
      this.drone.position = { lat: s.lat, lng: s.lon, altitude: Math.max(0.3, s.alt) };
      this.drone.orientation = { heading: s.hdg + this.nose, tilt: s.pitch || 0, roll: s.roll || 0 };
      const range = this.lastRange || 500;
      this.drone.scale = Math.max(1.5, Math.min(80, range / 45));
    }

    setCamera(c) {
      this.lastRange = c.range;
      if (this.free) return;
      const m = this.map;
      m.center = { lat: c.lat, lng: c.lon, altitude: c.alt || 0 };
      m.heading = c.heading;
      m.tilt = Math.max(0, Math.min(88, 90 + c.pitch));
      m.range = c.range;
    }

    setFree(free) { this.free = free; }

    line(id, pts, st) {
      if (!pts || pts.length < 2) { this.remove(id); return; }
      const path = pts.map(p => ({ lat: p[0], lng: p[1], altitude: st.ground ? 0 : (p[2] || 0) }));
      const e = this.els[id];
      if (e) { e.path = path; return; }
      const el = new this.M.Polyline3DElement({
        path, strokeColor: hex(st.color), strokeWidth: st.width || 2,
        outerColor: hex(st.outline || '#05080b'), outerWidth: st.outline ? 0.35 : 0.15,
        altitudeMode: st.ground ? this.ALT_CLAMP : this.ALT_REL,
        drawsOccludedSegments: !st.ground
      });
      this.map.append(el); this.els[id] = el;
    }

    polygon(id, ring, st) {
      if (!ring || ring.length < 3) { this.remove(id); return; }
      const path = ring.map(p => ({ lat: p[0], lng: p[1], altitude: 0 }));
      const e = this.els[id];
      if (e) { e.path = path; return; }
      const el = new this.M.Polygon3DElement({
        fillColor: hexA(st.fill, st.alpha ?? 0.3), strokeColor: hexA(st.fill, 0.9), strokeWidth: 1,
        altitudeMode: this.ALT_CLAMP, drawsOccludedSegments: false
      });
      el.path = path;
      this.map.append(el); this.els[id] = el;
    }

    marker(id, m) {
      const pos = { lat: m.lat, lng: m.lon, altitude: m.alt || 0 };
      const e = this.els[id];
      if (e) { e.position = pos; if (m.label !== undefined) e.label = m.label; return; }
      const el = new this.M.Marker3DElement({
        position: pos, altitudeMode: m.alt ? this.ALT_REL : this.ALT_CLAMP, extruded: !!m.alt, label: m.label || ''
      });
      this.map.append(el); this.els[id] = el;
    }

    remove(id) {
      const e = this.els[id];
      if (e) { e.remove(); delete this.els[id]; }
    }
  }

  window.GoogleRenderer = GoogleRenderer;
})();
