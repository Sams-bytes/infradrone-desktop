// render-cesium.js — CesiumJS map renderer for the demo (offline package or online CDN).
// Same small API as render-google.js:
//   init(el, cfg) · setDrone(s, visible) · setCamera(c) · setFree(bool)
//   line(id, pts, style) · polygon(id, ring, style) · marker(id, m) · remove(id)
(function () {
  'use strict';

  function loadScript(src) {
    return new Promise((res, rej) => {
      const s = document.createElement('script'); s.src = src; s.onload = res;
      s.onerror = () => rej(new Error('Could not load ' + src)); document.head.appendChild(s);
    });
  }
  function loadCss(href) { const l = document.createElement('link'); l.rel = 'stylesheet'; l.href = href; document.head.appendChild(l); }

  const ICON_CACHE = {};
  // Small vector icons drawn on a canvas, so no image files are needed.
  function icon(kind, color) {
    const key = kind + color;
    if (ICON_CACHE[key]) return ICON_CACHE[key];
    const c = document.createElement('canvas'); c.width = c.height = 44;
    const g = c.getContext('2d');
    g.translate(22, 22);
    g.shadowColor = 'rgba(0,0,0,0.6)'; g.shadowBlur = 4;
    g.lineWidth = 2.5; g.strokeStyle = '#ffffff'; g.fillStyle = color;
    const circle = (r) => { g.beginPath(); g.arc(0, 0, r, 0, Math.PI * 2); g.fill(); g.stroke(); };
    if (kind === 'hq' || kind === 'base') {
      g.beginPath(); g.rect(-11, -11, 22, 22); g.fill(); g.stroke();
      g.shadowBlur = 0; g.fillStyle = '#fff'; g.font = '800 12px system-ui, sans-serif'; g.textAlign = 'center'; g.textBaseline = 'middle';
      g.fillText(kind === 'hq' ? 'HQ' : 'B', 0, 1);
    } else if (kind === 'boat' || kind === 'police') {
      g.beginPath(); g.moveTo(0, -14); g.lineTo(9, 6); g.lineTo(0, 12); g.lineTo(-9, 6); g.closePath(); g.fill(); g.stroke();
    } else if (kind === 'person' || kind === 'team') {
      circle(9); g.shadowBlur = 0; g.fillStyle = '#fff'; g.beginPath(); g.arc(0, -3, 3, 0, Math.PI * 2); g.fill();
      g.fillRect(-3.5, 1, 7, 6);
    } else if (kind === 'lkp') {
      g.beginPath(); g.moveTo(-10, -10); g.lineTo(10, 10); g.moveTo(10, -10); g.lineTo(-10, 10); g.strokeStyle = color; g.lineWidth = 4; g.stroke();
    } else if (kind === 'foreign') {
      g.beginPath(); g.arc(0, 0, 5, 0, Math.PI * 2); g.fillStyle = '#94a3b8'; g.fill(); g.stroke();
    } else {
      circle(kind === 'capital' ? 7 : 5);
    }
    ICON_CACHE[key] = c.toDataURL();
    return ICON_CACHE[key];
  }

  class CesiumRenderer {
    async init(el, cfg) {
      this.cfg = cfg;
      const base = cfg.cesiumBase.replace(/\/?$/, '/');
      window.CESIUM_BASE_URL = base;
      loadCss(base + 'Widgets/widgets.css');
      await loadScript(base + 'Cesium.js');
      const C = this.C = window.Cesium;
      if (C.Ion) C.Ion.defaultAccessToken = undefined;

      const ne = C.ImageryLayer.fromProviderAsync(C.TileMapServiceImageryProvider.fromUrl(C.buildModuleUrl('Assets/Textures/NaturalEarthII')));
      const v = this.v = new C.Viewer(el, {
        baseLayer: ne, baseLayerPicker: false, geocoder: false, homeButton: false, sceneModePicker: false,
        navigationHelpButton: false, timeline: false, animation: false, infoBox: false, selectionIndicator: false,
        fullscreenButton: false, requestRenderMode: false, shouldAnimate: true
      });
      v.cesiumWidget.creditContainer.style.display = 'none'; // attribution is shown in our own footer
      if (cfg.imagery && cfg.imagery.url) {
        const p = new C.UrlTemplateImageryProvider({
          url: cfg.imagery.url, maximumLevel: cfg.imagery.maxLevel || 14, minimumLevel: 0,
          tilingScheme: new C.WebMercatorTilingScheme(), credit: ''
        });
        p.errorEvent.addEventListener(() => { /* missing tiles fall back to lower zoom; stay quiet */ });
        const layer = v.imageryLayers.addImageryProvider(p);
        layer.brightness = 0.92; layer.contrast = 1.08; layer.saturation = 0.9;
      }
      const sc = v.scene;
      sc.globe.baseColor = C.Color.fromCssColorString('#0b1a24');
      sc.skyAtmosphere.show = true; sc.fog.enabled = true; sc.fog.density = 0.0001; sc.globe.showGroundAtmosphere = false;
      sc.screenSpaceCameraController.enableInputs = false;
      v.screenSpaceEventHandler.removeInputAction(C.ScreenSpaceEventType.LEFT_DOUBLE_CLICK);
      v.resolutionScale = Math.min(window.devicePixelRatio || 1, 2);
      sc.postProcessStages.fxaa.enabled = true;
      this.ents = {};
      this.nose = cfg.noseOffset ?? 0;
      this.droneState = null;
      this.free = false;
    }

    setNose(deg) { this.nose = deg; }

    setDrone(s, visible) {
      const C = this.C;
      this.droneState = s;
      if (!this.drone) {
        const posCb = new C.CallbackProperty(() => this.dronePos(), false);
        this.drone = this.v.entities.add({
          position: posCb,
          orientation: new C.CallbackProperty(() => {
            const d = this.droneState; if (!d) return undefined;
            const hpr = new C.HeadingPitchRoll(C.Math.toRadians(d.hdg + this.nose), C.Math.toRadians(d.pitch || 0), C.Math.toRadians(d.roll || 0));
            return C.Transforms.headingPitchRollQuaternion(this.dronePos(), hpr);
          }, false),
          model: { uri: this.cfg.modelUrl || '/drone.glb', minimumPixelSize: 72, maximumScale: 20000,
                   silhouetteColor: C.Color.fromCssColorString('#22d3ee').withAlpha(0.9), silhouetteSize: 1.5 }
        });
      }
      this.drone.show = !!visible;
    }
    dronePos() {
      const d = this.droneState;
      return d ? this.C.Cartesian3.fromDegrees(d.lon, d.lat, Math.max(0.3, d.alt)) : undefined;
    }

    setCamera(c) {
      if (this.free) return;
      const C = this.C;
      const center = C.Cartesian3.fromDegrees(c.lon, c.lat, c.alt || 0);
      this.v.camera.lookAt(center, new C.HeadingPitchRange(C.Math.toRadians(c.heading), C.Math.toRadians(c.pitch), c.range));
    }

    setFree(free) {
      this.free = free;
      if (free) this.v.camera.lookAtTransform(this.C.Matrix4.IDENTITY);
      this.v.scene.screenSpaceCameraController.enableInputs = free;
    }

    line(id, pts, st) {
      const C = this.C;
      if (!pts || pts.length < 2) { this.remove(id); return; }
      // No terrain is loaded, so 'ground' lines are drawn a few metres above the
      // ellipsoid instead of clamped: identical look, instant (clamping builds in workers).
      const flat = [];
      for (const p of pts) flat.push(p[1], p[0], st.ground ? 4 : (p[2] || 0));
      const positions = C.Cartesian3.fromDegreesArrayHeights(flat);
      let e = this.ents[id];
      if (e) { e.polyline.positions = positions; return; }
      const col = C.Color.fromCssColorString(st.color).withAlpha(st.alpha ?? 1);
      let material = col;
      if (st.dash) material = new C.PolylineDashMaterialProperty({ color: col, dashLength: st.dash });
      if (st.glow) material = new C.PolylineGlowMaterialProperty({ color: col, glowPower: st.glow });
      if (st.outline) material = new C.PolylineOutlineMaterialProperty({ color: col, outlineColor: C.Color.fromCssColorString(st.outline), outlineWidth: 2 });
      this.ents[id] = this.v.entities.add({ polyline: { positions, width: st.width || 2, material, arcType: C.ArcType.GEODESIC } });
    }

    polygon(id, ring, st) {
      const C = this.C;
      if (!ring || ring.length < 3) { this.remove(id); return; }
      const flat = []; for (const p of ring) flat.push(p[1], p[0]);
      const hierarchy = new C.PolygonHierarchy(C.Cartesian3.fromDegreesArray(flat));
      let e = this.ents[id];
      if (e) { e.polygon.hierarchy = hierarchy; return; }
      this.ents[id] = this.v.entities.add({ polygon: { hierarchy, height: st.height || 0,
        material: C.Color.fromCssColorString(st.fill).withAlpha(st.alpha ?? 0.3) } });
    }

    marker(id, m) {
      const C = this.C;
      const pos = C.Cartesian3.fromDegrees(m.lon, m.lat, m.alt || 0);
      let e = this.ents[id];
      if (e) {
        e.position = pos;
        if (e.label && m.label !== undefined) e.label.text = m.label;
        return;
      }
      const ddc = m.hideBelow ? new C.DistanceDisplayCondition(m.hideBelow, Number.MAX_VALUE) : undefined;
      this.ents[id] = this.v.entities.add({
        position: pos,
        billboard: { image: icon(m.icon || 'dot', m.color || '#22d3ee'), scale: m.size || 0.7, disableDepthTestDistance: Number.POSITIVE_INFINITY,
                     verticalOrigin: C.VerticalOrigin.CENTER, distanceDisplayCondition: ddc },
        label: m.label ? { text: m.label, font: (m.bold ? '700 ' : '600 ') + (m.fontPx || 13) + 'px system-ui, Segoe UI, sans-serif',
          horizontalOrigin: m.side === 'right' ? C.HorizontalOrigin.LEFT : C.HorizontalOrigin.CENTER,
          fillColor: C.Color.WHITE, outlineColor: C.Color.BLACK, outlineWidth: 3, style: C.LabelStyle.FILL_AND_OUTLINE,
          showBackground: true, backgroundColor: C.Color.fromCssColorString('#08111a').withAlpha(0.72),
          backgroundPadding: new C.Cartesian2(7, 4), pixelOffset: m.side === 'right' ? new C.Cartesian2(14, 0) : new C.Cartesian2(0, -24),
          verticalOrigin: m.side === 'right' ? C.VerticalOrigin.CENTER : C.VerticalOrigin.BOTTOM, disableDepthTestDistance: Number.POSITIVE_INFINITY,
          distanceDisplayCondition: ddc } : undefined
      });
    }

    remove(id) {
      const e = this.ents[id];
      if (e) { this.v.entities.remove(e); delete this.ents[id]; }
    }
  }

  window.CesiumRenderer = CesiumRenderer;
})();
