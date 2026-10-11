// Services/Cesium3DPage.cs
//
// Also draws the LIVE drone (real or simulated) from /vehicle using /drone.glb.
//
// The CesiumJS page served by Cesium3DViewService, kept as a plain string so no
// .csproj change, embedded resource, or file-copy step is needed.
//
// The HTML and JavaScript below deliberately use ONLY single quotes, so the C#
// verbatim string needs no escaping. Keep it that way when editing.
//
// UNVERIFIED ENDPOINTS — read before trusting the view:
//   PDOK publishes 3D Tiles buildings and Quantized-Mesh terrain through its
//   OGC API 3D GeoVolumes service at
//     https://api.pdok.nl/kadaster/3d-basisvoorziening/ogc/v1_0
//   but the exact tileset.json / terrain layer URLs were not confirmed when this
//   file was written, so BUILDINGS_URL and TERRAIN_URL below are left empty.
//   The page runs fine without them (imagery + route + SORA volumes still draw);
//   buildings and terrain simply do not appear until they are filled in.
//   Browse the collections on that API, then paste the URLs in. The page logs
//   what it loads and what it fails to load in the browser console.

namespace InfraDroneDesktop.Services
{
    internal static class Cesium3DPage
    {
        public const string Html = @"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<title>InfraDrone 3D</title>
<link rel='stylesheet' href='https://unpkg.com/cesium/Build/Cesium/Widgets/widgets.css'>
<script src='https://unpkg.com/cesium/Build/Cesium/Cesium.js'></script>
<style>
  html, body { margin:0; padding:0; height:100%; background:#0b1017; overflow:hidden; }
  #cesiumContainer { width:100%; height:100%; }
  #hud {
    position:absolute; top:10px; left:10px; z-index:10;
    background:rgba(11,16,23,0.82); color:#cfe3f5; padding:10px 12px;
    font:12px/1.5 system-ui, sans-serif; border:1px solid #23394f; border-radius:6px;
    min-width:190px;
  }
  #hud b { color:#fff; font-size:13px; }
  .sw { display:inline-block; width:10px; height:10px; margin-right:6px; border-radius:2px; }
  #err { color:#ff9a9a; margin-top:6px; display:none; }
  #veh { margin-top:8px; padding-top:8px; border-top:1px solid #23394f; display:none; }
  #veh .k { color:#7f93a8; display:inline-block; width:56px; }
  #veh .v { color:#fff; font-family:monospace; }
  .btn { margin-top:6px; margin-right:4px; background:#12324a; color:#cfe3f5; border:1px solid #2a5878;
         border-radius:4px; padding:4px 8px; font:12px system-ui, sans-serif; cursor:pointer; }
  .btn.on { background:#0e7490; color:#fff; border-color:#22d3ee; }
</style>
</head>
<body>
<div id='cesiumContainer'></div>
<div id='hud'>
  <b>InfraDrone 3D</b> <span style='color:#7f93a8;font-size:11px'>PDOK (Dutch government 3D)</span><br>
  Map: <button class='btn' onclick='location.href=""/google""'>Google 3D</button><button class='btn on'>PDOK</button><br>
  <span id='wpcount'>waiting for app...</span><br>
  <span id='alt'></span>
  <div style='margin-top:8px'>
    <span class='sw' style='background:#00aa00'></span>Flight geography<br>
    <span class='sw' style='background:#ffc800'></span>Contingency volume<br>
    <span class='sw' style='background:#dc0000'></span>Ground risk buffer
  </div>
  <div id='veh'>
    <b>Live drone</b> <span id='vstale' style='color:#fbbf24'></span><br>
    <span class='k'>Mode</span><span class='v' id='vmode'>-</span><br>
    <span class='k'>Armed</span><span class='v' id='varmed'>-</span><br>
    <span class='k'>Alt</span><span class='v' id='valt'>-</span><br>
    <span class='k'>Speed</span><span class='v' id='vspd'>-</span><br>
    <span class='k'>Heading</span><span class='v' id='vhdg'>-</span><br>
    View:<br>
    <button class='btn' data-view='chase'>Chase</button><button class='btn' data-view='fpv'>Onboard</button><button class='btn' data-view='top'>Top</button><button class='btn' data-view='orbit'>Orbit</button><button class='btn on' data-view='free'>Free</button><br>
    <button class='btn' id='btrail'>Clear trail</button><button class='btn' id='bnose' title='Rotate the drone model 90 degrees if its nose points the wrong way'>Turn model 90°</button>
  </div>
  <div id='err'></div>
</div>
<script>
// ---- Configuration -------------------------------------------------------
// Fill these in from https://api.pdok.nl/kadaster/3d-basisvoorziening/ogc/v1_0
// PDOK 3D Basisvoorziening. The API moved from /ogc/v1_0 to /ogc/v1 in Sept 2024,
// but published examples still resolve on v1_0, so both are tried in order.
var API_BASES = [
  'https://api.pdok.nl/kadaster/3d-basisvoorziening/ogc/v1',
  'https://api.pdok.nl/kadaster/3d-basisvoorziening/ogc/v1_0'
];
// 'terreinen/3dtiles' is confirmed working; 'gebouwen/3dtiles' follows the same
// pattern but is unconfirmed - watch the console to see if it resolves.
var BUILDINGS_PATH = '/collections/gebouwen/3dtiles/tileset.json';
var TERRAIN_PATH   = '/collections/digitaalterreinmodel/quantized-mesh';
var BUILDINGS_URL = API_BASES[0] + BUILDINGS_PATH;
var TERRAIN_URL   = API_BASES[0] + TERRAIN_PATH;

// PDOK aerial imagery (Netherlands). Falls back to Esri if it fails to load.
var PDOK_WMTS = 'https://service.pdok.nl/hwh/luchtfotorgb/wmts/v1_0';
var PDOK_LAYER = 'Actueel_orthoHR';
var ESRI_URL = 'https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}';

function showErr(msg) {
  var e = document.getElementById('err');
  e.style.display = 'block';
  e.textContent = msg;
  console.error(msg);
}

// Avoid Cesium Ion entirely: no account, no token, no Ion assets.
if (window.Cesium && Cesium.Ion) { Cesium.Ion.defaultAccessToken = undefined; }

var viewer = new Cesium.Viewer('cesiumContainer', {
  baseLayer: false, imageryProvider: false,   // key name differs by Cesium version
  baseLayerPicker: false, geocoder: false, homeButton: false,
  sceneModePicker: false, navigationHelpButton: false, timeline: false,
  animation: false, infoBox: false, selectionIndicator: false
});
viewer.scene.globe.baseColor = Cesium.Color.fromCssColorString('#0b1017');
// Midday sun today, with shadows: buildings read as 3D instead of flat blocks.
(function () {
  var d = new Date(); d.setHours(13, 0, 0, 0);
  viewer.clock.currentTime = Cesium.JulianDate.fromDate(d);
  viewer.clock.shouldAnimate = false;
})();
viewer.shadows = true;
viewer.terrainShadows = Cesium.ShadowMode.RECEIVE_ONLY;
viewer.shadowMap.size = 4096;
viewer.shadowMap.softShadows = true;
viewer.shadowMap.maximumDistance = 2500;
viewer.scene.globe.enableLighting = true;
viewer.scene.highDynamicRange = false;
viewer.scene.globe.depthTestAgainstTerrain = true;
viewer.scene.skyAtmosphere.show = true;
viewer.scene.fog.enabled = true;
viewer.resolutionScale = window.devicePixelRatio || 1;
viewer.scene.msaaSamples = 4;
viewer.scene.globe.maximumScreenSpaceError = 1.5;

// ---- Imagery -------------------------------------------------------------
try {
  viewer.imageryLayers.addImageryProvider(new Cesium.WebMapTileServiceImageryProvider({
    url: PDOK_WMTS, layer: PDOK_LAYER, style: 'default',
    tileMatrixSetID: 'EPSG:3857', format: 'image/jpeg', maximumLevel: 19, enablePickFeatures: false
  }));
  console.log('PDOK imagery requested');
} catch (e) {
  showErr('PDOK imagery failed, using Esri fallback.');
  viewer.imageryLayers.addImageryProvider(new Cesium.UrlTemplateImageryProvider({
    url: ESRI_URL, maximumLevel: 19
  }));
}

// ---- Terrain and buildings (optional, see config note above) -------------
if (TERRAIN_URL) {
  try {
    if (Cesium.Terrain && Cesium.Terrain.fromWorldTerrain) {
      viewer.scene.setTerrain(new Cesium.Terrain(Cesium.CesiumTerrainProvider.fromUrl(TERRAIN_URL)));
    } else {
      viewer.terrainProvider = new Cesium.CesiumTerrainProvider({ url: TERRAIN_URL });
    }
    console.log('terrain requested');
  } catch (e) { showErr('Terrain failed to load: ' + e.message); }
}
function tryBuildings(i) {
  if (i >= API_BASES.length) { showErr('Buildings: no PDOK endpoint resolved.'); return; }
  var url = API_BASES[i] + BUILDINGS_PATH;
  console.log('trying buildings: ' + url);
  Cesium.Cesium3DTileset.fromUrl(url, { maximumScreenSpaceError: 8 })
    .then(function (ts) {
      viewer.scene.primitives.add(ts);
      console.log('BUILDINGS LOADED from ' + url);
      // Warm white 'architectural model' look; sunlight and shadows give the depth.
      ts.style = new Cesium.Cesium3DTileStyle({ color: 'color(\'#efe9dd\', 1.0)' });
      ts.shadows = Cesium.ShadowMode.ENABLED;
    })
    .catch(function (e) { console.warn('buildings failed at ' + url + ': ' + e.message); tryBuildings(i + 1); });
}
if (Cesium.Cesium3DTileset && Cesium.Cesium3DTileset.fromUrl) { tryBuildings(0); }
else { showErr('This Cesium build is too old for fromUrl(); buildings skipped.'); }

// ---- Ground reference ----------------------------------------------------
// The autopilot reports altitude relative to HOME, and missions are uploaded
// with relative altitudes. Cesium positions are heights above the WGS84
// ellipsoid, which in the Netherlands is about 40-45 m above NAP. So every
// relative height is drawn on top of the terrain height at home (groundRef),
// read from the loaded PDOK terrain once its tiles are available.
var groundRef = null;
function terrainHeightAt(lon, lat) {
  var h = viewer.scene.globe.getHeight(Cesium.Cartographic.fromDegrees(lon, lat));
  return (h === undefined || h === null || isNaN(h)) ? null : h;
}

// ---- Mission rendering ---------------------------------------------------
var entities = [];
var lastJson = '';
var framed = false;

function clearEntities() {
  for (var i = 0; i < entities.length; i++) { viewer.entities.remove(entities[i]); }
  entities = [];
}

function flatToDegreesArray(flat) {
  // flat is [lon,lat,lon,lat,...]
  return Cesium.Cartesian3.fromDegreesArray(flat);
}

function addVolume(rings, cssColor, height, base) {
  if (!rings) { return; }
  for (var r = 0; r < rings.length; r++) {
    var flat = rings[r];
    if (!flat || flat.length < 6) { continue; }
    var e = viewer.entities.add({
      polygon: {
        hierarchy: new Cesium.PolygonHierarchy(flatToDegreesArray(flat)),
        material: Cesium.Color.fromCssColorString(cssColor).withAlpha(0.22),
        outline: true,
        outlineColor: Cesium.Color.fromCssColorString(cssColor),
        extrudedHeight: base + height,
        height: base,
        closeTop: false
      }
    });
    entities.push(e);
  }
}

var missionBase = null;   // ground height used for the last mission render
function render(state) {
  clearEntities();
  var wps = state.waypoints || [];
  var base = groundRef;
  if (base === null && wps.length > 0) { base = terrainHeightAt(wps[0].lon, wps[0].lat); }
  missionBase = base;
  if (base === null) { base = 0; }
  document.getElementById('wpcount').textContent = wps.length + ' waypoint(s)';
  document.getElementById('alt').textContent =
    state.hfg ? ('flight geography ' + state.hfg + ' m, contingency top ' + state.hcv + ' m') : '';

  if (state.volumes) {
    // Outermost first so the inner ones stay visible through them.
    addVolume(state.volumes.grb, '#dc0000', state.hcv || 100, base);
    addVolume(state.volumes.cv,  '#ffc800', state.hcv || 100, base);
    addVolume(state.volumes.fg,  '#00aa00', state.hfg || 100, base);
  }

  if (wps.length > 0) {
    var routePts = [];
    for (var i = 0; i < wps.length; i++) {
      routePts.push(wps[i].lon, wps[i].lat, base + wps[i].alt);
      entities.push(viewer.entities.add({
        position: Cesium.Cartesian3.fromDegrees(wps[i].lon, wps[i].lat, base + wps[i].alt),
        point: { pixelSize: 10, color: Cesium.Color.fromCssColorString('#0d9e75'),
                 outlineColor: Cesium.Color.WHITE, outlineWidth: 2,
                 disableDepthTestDistance: Number.POSITIVE_INFINITY },
        label: { text: String(i + 1), font: '12px sans-serif', pixelOffset: new Cesium.Cartesian2(0, -18),
                 fillColor: Cesium.Color.WHITE, showBackground: true,
                 backgroundColor: Cesium.Color.fromCssColorString('#0b1017').withAlpha(0.7),
                 disableDepthTestDistance: Number.POSITIVE_INFINITY }
      }));
    }
    if (wps.length >= 2) {
      entities.push(viewer.entities.add({
        polyline: {
          positions: Cesium.Cartesian3.fromDegreesArrayHeights(routePts),
          width: 3, material: Cesium.Color.fromCssColorString('#0d9e75')
        }
      }));
    }
    if (!framed) {
      framed = true;
      viewer.flyTo(viewer.entities, { duration: 1.5 });
    }
  } else {
    framed = false;
  }
}

function poll() {
  fetch('/state', { cache: 'no-store' })
    .then(function (r) { return r.text(); })
    .then(function (txt) {
      if (txt === lastJson) {
        // Same mission. Redraw only if it was drawn before the ground height
        // was known and that height is available now.
        var groundNowKnown = groundRef !== null || terrainKnownFor(txt);
        if (missionBase !== null || !groundNowKnown) { return; }
      }
      lastJson = txt;
      render(JSON.parse(txt));
    })
    .catch(function () { /* app closed or busy; keep polling quietly */ });
}
function terrainKnownFor(txt) {
  try {
    var w = JSON.parse(txt).waypoints || [];
    return w.length > 0 && terrainHeightAt(w[0].lon, w[0].lat) !== null;
  } catch (e) { return false; }
}
setInterval(poll, 500);
poll();

// ---- Live drone ------------------------------------------------------------
// Model nose is +X. Cesium heading 0 points the model's +X east, so a compass
// heading (0 = north, clockwise) needs -90 deg. Change this if the nose looks
// sideways in some future Cesium version.
var MODEL_HEADING_OFFSET = -90;
var POLL_MS = 250;

var drone = null;
var prevPos = null, nextPos = null, prevHpr = null, nextHpr = null, tNext = 0;
var trail = [];
var MAX_TRAIL = 1200;          // about 5 minutes at 4 Hz
var flownToDrone = false;

function lerpAngle(a, b, f) {
  var d = ((b - a + 540) % 360) - 180;
  return a + d * f;
}
function frac() {
  var f = (Date.now() - (tNext - POLL_MS)) / POLL_MS;
  return f < 0 ? 0 : (f > 1 ? 1 : f);
}
function currentPos() {
  if (!nextPos) { return undefined; }
  if (!prevPos) { return nextPos; }
  return Cesium.Cartesian3.lerp(prevPos, nextPos, frac(), new Cesium.Cartesian3());
}
function currentHpr() {
  if (!nextHpr) { return null; }
  if (!prevHpr) { return nextHpr; }
  var f = frac();
  return { h: lerpAngle(prevHpr.h, nextHpr.h, f), p: prevHpr.p + (nextHpr.p - prevHpr.p) * f,
           r: prevHpr.r + (nextHpr.r - prevHpr.r) * f };
}

function createDrone() {
  drone = viewer.entities.add({
    name: 'Drone',
    position: new Cesium.CallbackProperty(currentPos, false),
    orientation: new Cesium.CallbackProperty(function () {
      var pos = currentPos(); var a = currentHpr();
      if (!pos || !a) { return undefined; }
      var hpr = new Cesium.HeadingPitchRoll(
        Cesium.Math.toRadians(a.h + MODEL_HEADING_OFFSET),
        Cesium.Math.toRadians(a.p), Cesium.Math.toRadians(a.r));
      return Cesium.Transforms.headingPitchRollQuaternion(pos, hpr);
    }, false),
    model: {
      uri: '/drone.glb',
      minimumPixelSize: 72,
      maximumScale: 400,
      silhouetteColor: Cesium.Color.fromCssColorString('#22d3ee').withAlpha(0.8),
      silhouetteSize: 1.5
    },
    viewFrom: new Cesium.Cartesian3(-60, -60, 35)
  });
  viewer.entities.add({
    polyline: {
      positions: new Cesium.CallbackProperty(function () { return trail; }, false),
      width: 6,
      material: new Cesium.PolylineGlowMaterialProperty({
        glowPower: 0.25, color: Cesium.Color.fromCssColorString('#22d3ee').withAlpha(0.9) })
    }
  });
  // Thin line from the drone straight down to the ground, helps judge height.
  viewer.entities.add({
    polyline: {
      positions: new Cesium.CallbackProperty(function () {
        var p = currentPos(); if (!p || groundRef === null) { return []; }
        var c = Cesium.Cartographic.fromCartesian(p);
        return [p, Cesium.Cartesian3.fromRadians(c.longitude, c.latitude, groundRef)];
      }, false),
      width: 1.5,
      material: new Cesium.PolylineDashMaterialProperty({ color: Cesium.Color.WHITE.withAlpha(0.6) })
    }
  });
}

function updateHud(v) {
  document.getElementById('veh').style.display = 'block';
  document.getElementById('vmode').textContent = v.mode || '-';
  document.getElementById('varmed').textContent = v.armed ? 'YES' : 'no';
  document.getElementById('valt').textContent = v.alt.toFixed(1) + ' m';
  document.getElementById('vspd').textContent = v.speed.toFixed(1) + ' m/s';
  document.getElementById('vhdg').textContent = Math.round(v.hdg) + ' deg';
  document.getElementById('vstale').textContent = v.stale ? '(no data)' : '';
}

function onVehicle(v) {
  if (!v.present || (v.lat === 0 && v.lon === 0)) { return; }
  updateHud(v);
  if (v.stale) { return; }

  if (groundRef === null) {
    // On the ground at home this is the home terrain height; once airborne the
    // terrain under the drone is used instead, which is the same in flat NL.
    var g = terrainHeightAt(v.lon, v.lat);
    if (g !== null && (!v.armed || v.alt < 2)) { groundRef = g; console.log('ground reference ' + g.toFixed(1) + ' m (ellipsoid)'); }
  }
  var base = groundRef === null ? 0 : groundRef;
  var pos = Cesium.Cartesian3.fromDegrees(v.lon, v.lat, base + v.alt);

  if (!drone) { createDrone(); }
  prevPos = currentPos() || pos;
  prevHpr = currentHpr() || { h: v.hdg, p: v.pitch, r: v.roll };
  nextPos = pos;
  nextHpr = { h: v.hdg, p: v.pitch, r: v.roll };
  tNext = Date.now() + POLL_MS;

  var last = trail.length ? trail[trail.length - 1] : null;
  if (v.armed && (!last || Cesium.Cartesian3.distance(last, pos) > 0.5)) {
    trail.push(pos);
    if (trail.length > MAX_TRAIL) { trail.shift(); }
  }

  if (!flownToDrone) {
    flownToDrone = true;
    viewer.camera.flyTo({
      destination: Cesium.Cartesian3.fromDegrees(v.lon, v.lat - 0.0035, base + 220),
      orientation: { heading: 0, pitch: Cesium.Math.toRadians(-35), roll: 0 },
      duration: 2
    });
  }
}

// ---- Camera views ------------------------------------------------------------
var view = 'free';
var orbitHeading = 0;
try { var savedNose = localStorage.getItem('infradrone.noseOffset.cesium'); if (savedNose !== null) { MODEL_HEADING_OFFSET = Number(savedNose); } } catch (e) { }
function rad(d) { return Cesium.Math.toRadians(d); }

viewer.scene.preRender.addEventListener(function () {
  if (!drone) { return; }
  drone.show = view !== 'fpv';
  if (view === 'free') { return; }
  var pos = currentPos(); var a = currentHpr();
  if (!pos || !a) { return; }
  var cam = viewer.camera;
  if (view === 'chase') {
    cam.lookAt(pos, new Cesium.HeadingPitchRange(rad(a.h), rad(-12), 45));
  } else if (view === 'top') {
    cam.lookAt(pos, new Cesium.HeadingPitchRange(0, rad(-89.5), 350));
  } else if (view === 'orbit') {
    orbitHeading = (orbitHeading + 0.25) % 360;
    cam.lookAt(pos, new Cesium.HeadingPitchRange(rad(orbitHeading), rad(-25), 160));
  } else if (view === 'fpv') {
    // Eye just above the nose, looking where the drone points.
    cam.lookAtTransform(Cesium.Matrix4.IDENTITY);
    var enu = Cesium.Transforms.eastNorthUpToFixedFrame(pos);
    var h = rad(a.h);
    var eye = Cesium.Matrix4.multiplyByPoint(enu, new Cesium.Cartesian3(Math.sin(h) * 1.5, Math.cos(h) * 1.5, 0.5),
                                             new Cesium.Cartesian3());
    cam.setView({ destination: eye, orientation: { heading: h, pitch: rad(a.p), roll: rad(a.r) } });
  }
});

var viewBtns = document.querySelectorAll('[data-view]');
for (var vb = 0; vb < viewBtns.length; vb++) {
  viewBtns[vb].onclick = function () {
    view = this.getAttribute('data-view');
    for (var k = 0; k < viewBtns.length; k++) { viewBtns[k].classList.toggle('on', viewBtns[k] === this); }
    viewer.trackedEntity = undefined;
    // Mouse control only in Free; the other views drive the camera themselves.
    viewer.scene.screenSpaceCameraController.enableInputs = view === 'free';
    if (view === 'free') { viewer.camera.lookAtTransform(Cesium.Matrix4.IDENTITY); }
    viewer.scene.screenSpaceCameraController.enableCollisionDetection = view !== 'fpv';
  };
}
document.getElementById('btrail').onclick = function () { trail = []; };
document.getElementById('bnose').onclick = function () {
  MODEL_HEADING_OFFSET = ((MODEL_HEADING_OFFSET + 90 + 180) % 360) - 180;
  try { localStorage.setItem('infradrone.noseOffset.cesium', String(MODEL_HEADING_OFFSET)); } catch (e) { }
};

function pollVehicle() {
  fetch('/vehicle', { cache: 'no-store' })
    .then(function (r) { return r.json(); })
    .then(onVehicle)
    .catch(function () { /* app closed or busy */ });
}
setInterval(pollVehicle, POLL_MS);
</script>
</body>
</html>";
    }
}
