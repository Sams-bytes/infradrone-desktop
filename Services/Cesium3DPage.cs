// Services/Cesium3DPage.cs
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
</style>
</head>
<body>
<div id='cesiumContainer'></div>
<div id='hud'>
  <b>Mission 3D</b><br>
  <span id='wpcount'>waiting for app...</span><br>
  <span id='alt'></span>
  <div style='margin-top:8px'>
    <span class='sw' style='background:#00aa00'></span>Flight geography<br>
    <span class='sw' style='background:#ffc800'></span>Contingency volume<br>
    <span class='sw' style='background:#dc0000'></span>Ground risk buffer
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
      ts.style = new Cesium.Cesium3DTileStyle({
        color: 'color(\'#7d94ad\', 0.95)'
      });
    })
    .catch(function (e) { console.warn('buildings failed at ' + url + ': ' + e.message); tryBuildings(i + 1); });
}
if (Cesium.Cesium3DTileset && Cesium.Cesium3DTileset.fromUrl) { tryBuildings(0); }
else { showErr('This Cesium build is too old for fromUrl(); buildings skipped.'); }

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

function addVolume(rings, cssColor, height) {
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
        extrudedHeight: height,
        height: 0,
        closeTop: false
      }
    });
    entities.push(e);
  }
}

function render(state) {
  clearEntities();
  var wps = state.waypoints || [];
  document.getElementById('wpcount').textContent = wps.length + ' waypoint(s)';
  document.getElementById('alt').textContent =
    state.hfg ? ('flight geography ' + state.hfg + ' m, contingency top ' + state.hcv + ' m') : '';

  if (state.volumes) {
    // Outermost first so the inner ones stay visible through them.
    addVolume(state.volumes.grb, '#dc0000', state.hcv || 100);
    addVolume(state.volumes.cv,  '#ffc800', state.hcv || 100);
    addVolume(state.volumes.fg,  '#00aa00', state.hfg || 100);
  }

  if (wps.length > 0) {
    var routePts = [];
    for (var i = 0; i < wps.length; i++) {
      routePts.push(wps[i].lon, wps[i].lat, wps[i].alt);
      entities.push(viewer.entities.add({
        position: Cesium.Cartesian3.fromDegrees(wps[i].lon, wps[i].lat, wps[i].alt),
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
      if (txt === lastJson) { return; }
      lastJson = txt;
      render(JSON.parse(txt));
    })
    .catch(function () { /* app closed or busy; keep polling quietly */ });
}
setInterval(poll, 500);
poll();
</script>
</body>
</html>";
    }
}
