// Services/Google3DPage.cs
//
// Photorealistic 3D view (Google Maps JavaScript API, 3D Maps / Map3DElement)
// served by Cesium3DViewService at /google. Shows the same live drone, trail,
// mission route and SORA volumes as the PDOK view, with camera modes:
// Chase, Onboard (FPV), Top-down, Orbit and Free.
//
// WHY NOT GOOGLE 3D TILES IN CESIUM: since 8 July 2025 Google returns HTTP 403
// for Photorealistic 3D Tiles (Map Tiles API) to projects with an EEA billing
// address. Google's documented alternative for EEA customers is 3D Maps in the
// Maps JavaScript API, which is what this page uses.
//
// The key is inserted by the server in place of __GOOGLE_KEY__. Keep this page
// free of double quotes so the C# verbatim string needs no escaping.

namespace InfraDroneDesktop.Services
{
    internal static class Google3DPage
    {
        public const string Html = @"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<title>InfraDrone 3D — Google</title>
<style>
  html, body { margin:0; padding:0; height:100%; background:#0b1017; overflow:hidden; font:12px/1.5 system-ui, sans-serif; }
  #map { position:absolute; inset:0; }
  #hud { position:absolute; top:10px; left:10px; z-index:10; background:rgba(11,16,23,0.85); color:#cfe3f5;
         padding:10px 12px; border:1px solid #23394f; border-radius:8px; min-width:230px; }
  #hud b { color:#fff; font-size:13px; }
  .row { margin-top:8px; }
  .btn { margin:3px 3px 0 0; background:#12324a; color:#cfe3f5; border:1px solid #2a5878; border-radius:4px;
         padding:4px 8px; font:12px system-ui, sans-serif; cursor:pointer; }
  .btn.on { background:#0e7490; color:#fff; border-color:#22d3ee; }
  .k { color:#7f93a8; display:inline-block; width:62px; }
  .v { color:#fff; font-family:monospace; }
  #veh { display:none; border-top:1px solid #23394f; padding-top:8px; }
  #err { color:#ff9a9a; margin-top:6px; display:none; max-width:260px; }
  .small { color:#7f93a8; font-size:11px; }
</style>
</head>
<body>
<div id='map'></div>
<div id='hud'>
  <b>InfraDrone 3D</b> <span class='small'>Google photorealistic</span>
  <div class='row'>
    Map: <button class='btn on'>Google 3D</button><button class='btn' onclick='location.href=""/pdok""'>PDOK</button>
  </div>
  <div class='row small' id='wpcount'>waiting for app...</div>
  <div class='row' id='veh'>
    <b>Live drone</b> <span id='vstale' style='color:#fbbf24'></span><br>
    <span class='k'>Mode</span><span class='v' id='vmode'>-</span><br>
    <span class='k'>Armed</span><span class='v' id='varmed'>-</span><br>
    <span class='k'>Alt</span><span class='v' id='valt'>-</span><br>
    <span class='k'>Speed</span><span class='v' id='vspd'>-</span><br>
    <span class='k'>Heading</span><span class='v' id='vhdg'>-</span>
  </div>
  <div class='row'>
    View:<br>
    <button class='btn' data-view='chase'>Chase</button><button class='btn' data-view='fpv'>Onboard</button><button class='btn' data-view='top'>Top</button><button class='btn' data-view='orbit'>Orbit</button><button class='btn on' data-view='free'>Free</button>
  </div>
  <div class='row'>
    <button class='btn' id='btrail'>Clear trail</button><button class='btn' id='bnose' title='Rotate the drone model 90 degrees if its nose points the wrong way'>Turn model 90°</button>
  </div>
  <div id='err'></div>
</div>

<script>
// Double quotes are avoided on purpose (C# verbatim string). The onclick above
// uses doubled quotes which C# turns into one pair.
var START = { lat: 53.2194, lng: 6.5665 };   // Groningen, until the drone reports
var POLL_MS = 250;
var view = 'free';
var noseOffset = -90;
try { var saved = localStorage.getItem('infradrone.noseOffset.google'); if (saved !== null) { noseOffset = Number(saved); } } catch (e) { }

var M = null, map = null, drone = null, droneShown = false;
var trailLine = null, trail = [], lastTrailSet = 0;
var routeEls = [];
var prev = null, next = null, tNext = 0, groundMsl = 0;
var orbitHeading = 0, flown = false;

function showErr(msg) { var e = document.getElementById('err'); e.style.display = 'block'; e.textContent = msg; console.error(msg); }
function enumOr(obj, name, fallback) { return (obj && obj[name] !== undefined) ? obj[name] : fallback; }

window.gm_authFailure = function () {
  showErr('Google rejected the API key. Check that Maps JavaScript API is enabled for it and billing is active, then reload. Or use the PDOK map.');
};

function initMap() {
  try {
    M = google.maps.maps3d;
    map = new M.Map3DElement({
      center: { lat: START.lat, lng: START.lng, altitude: 0 },
      range: 1500, tilt: 60, heading: 0,
      mode: enumOr(M.MapMode, 'SATELLITE', 'SATELLITE'),
      defaultUIHidden: true
    });
    document.getElementById('map').appendChild(map);
    trailLine = new M.Polyline3DElement({
      path: [], strokeColor: '#22d3ee', outerColor: '#ffffff', strokeWidth: 6, outerWidth: 0.25,
      altitudeMode: enumOr(M.AltitudeMode, 'RELATIVE_TO_GROUND', 'RELATIVE_TO_GROUND'),
      drawsOccludedSegments: true
    });
    map.append(trailLine);
    setInterval(pollState, 500); pollState();
    setInterval(pollVehicle, POLL_MS); pollVehicle();
    requestAnimationFrame(frame);
  } catch (e) {
    showErr('3D Maps failed to start: ' + e.message);
  }
}
window.initMap = initMap;

// ---- Mission route and SORA volumes -----------------------------------------
var lastStateTxt = '';
function pollState() {
  fetch('/state', { cache: 'no-store' }).then(function (r) { return r.text(); }).then(function (txt) {
    if (txt === lastStateTxt) { return; }
    lastStateTxt = txt;
    drawMission(JSON.parse(txt));
  }).catch(function () { });
}

function drawMission(state) {
  for (var i = 0; i < routeEls.length; i++) { routeEls[i].remove(); }
  routeEls = [];
  var rel = enumOr(M.AltitudeMode, 'RELATIVE_TO_GROUND', 'RELATIVE_TO_GROUND');
  var wps = state.waypoints || [];
  document.getElementById('wpcount').textContent = wps.length + ' waypoint(s)';

  if (state.volumes) {
    addVolume(state.volumes.grb, '#dc0000', state.hcv || 100, rel);
    addVolume(state.volumes.cv, '#ffc800', state.hcv || 100, rel);
    addVolume(state.volumes.fg, '#00aa00', state.hfg || 100, rel);
  }
  if (wps.length > 0) {
    var path = [];
    for (var j = 0; j < wps.length; j++) {
      path.push({ lat: wps[j].lat, lng: wps[j].lon, altitude: wps[j].alt });
      var mk = new M.Marker3DElement({ position: { lat: wps[j].lat, lng: wps[j].lon, altitude: wps[j].alt },
                                       altitudeMode: rel, extruded: true, label: String(j + 1) });
      map.append(mk); routeEls.push(mk);
    }
    if (path.length >= 2) {
      var line = new M.Polyline3DElement({ path: path, strokeColor: '#34d399', outerColor: '#0b1017',
                                           strokeWidth: 5, outerWidth: 0.3, altitudeMode: rel, drawsOccludedSegments: true });
      map.append(line); routeEls.push(line);
    }
  }
}

function addVolume(rings, color, height, rel) {
  if (!rings) { return; }
  for (var r = 0; r < rings.length; r++) {
    var flat = rings[r]; if (!flat || flat.length < 6) { continue; }
    var path = [];
    for (var i = 0; i + 1 < flat.length; i += 2) { path.push({ lat: flat[i + 1], lng: flat[i], altitude: height }); }
    var poly = new M.Polygon3DElement({ fillColor: color + '30', strokeColor: color, strokeWidth: 2,
                                        altitudeMode: rel, extruded: true, drawsOccludedSegments: false });
    poly.path = path;
    map.append(poly); routeEls.push(poly);
  }
}

// ---- Live drone --------------------------------------------------------------
function pollVehicle() {
  fetch('/vehicle', { cache: 'no-store' }).then(function (r) { return r.json(); }).then(onVehicle).catch(function () { });
}

function onVehicle(v) {
  if (!v.present || (v.lat === 0 && v.lon === 0)) { return; }
  document.getElementById('veh').style.display = 'block';
  document.getElementById('vmode').textContent = v.mode || '-';
  document.getElementById('varmed').textContent = v.armed ? 'YES' : 'no';
  document.getElementById('valt').textContent = v.alt.toFixed(1) + ' m';
  document.getElementById('vspd').textContent = v.speed.toFixed(1) + ' m/s';
  document.getElementById('vhdg').textContent = Math.round(v.hdg) + ' deg';
  document.getElementById('vstale').textContent = v.stale ? '(no data)' : '';
  if (v.stale) { return; }
  if (typeof v.gmsl === 'number') { groundMsl = v.gmsl; }

  prev = current() || { lat: v.lat, lng: v.lon, alt: v.alt, h: v.hdg, p: v.pitch, r: v.roll };
  next = { lat: v.lat, lng: v.lon, alt: v.alt, h: v.hdg, p: v.pitch, r: v.roll };
  tNext = Date.now() + POLL_MS;

  if (!drone) {
    drone = new M.Model3DElement({
      src: location.origin + '/drone.glb',
      position: { lat: v.lat, lng: v.lon, altitude: v.alt },
      altitudeMode: enumOr(M.AltitudeMode, 'RELATIVE_TO_GROUND', 'RELATIVE_TO_GROUND'),
      orientation: { heading: 0, tilt: 0, roll: 0 }, scale: 6
    });
    map.append(drone); droneShown = true;
  }
  if (!flown) {
    flown = true;
    map.flyCameraTo({ endCamera: { center: { lat: v.lat, lng: v.lon, altitude: groundMsl + v.alt },
                                   range: 350, tilt: 65, heading: v.hdg }, durationMillis: 2500 });
  }

  var last = trail.length ? trail[trail.length - 1] : null;
  if (v.armed && (!last || Math.abs(last.lat - v.lat) + Math.abs(last.lng - v.lon) > 0.000005 || Math.abs(last.altitude - v.alt) > 0.5)) {
    trail.push({ lat: v.lat, lng: v.lon, altitude: v.alt });
    if (trail.length > 900) { trail.shift(); }
  }
}

function lerpAngle(a, b, f) { var d = ((b - a + 540) % 360) - 180; return a + d * f; }
function current() {
  if (!next) { return null; }
  if (!prev) { return next; }
  var f = (Date.now() - (tNext - POLL_MS)) / POLL_MS; f = f < 0 ? 0 : (f > 1 ? 1 : f);
  return { lat: prev.lat + (next.lat - prev.lat) * f, lng: prev.lng + (next.lng - prev.lng) * f,
           alt: prev.alt + (next.alt - prev.alt) * f, h: lerpAngle(prev.h, next.h, f),
           p: prev.p + (next.p - prev.p) * f, r: prev.r + (next.r - prev.r) * f };
}

function offsetLatLng(lat, lng, headingDeg, metres) {
  var h = headingDeg * Math.PI / 180;
  return { lat: lat + metres * Math.cos(h) / 111320,
           lng: lng + metres * Math.sin(h) / (111320 * Math.cos(lat * Math.PI / 180)) };
}

function setDroneVisible(show) {
  if (!drone || show === droneShown) { return; }
  if (show) { map.append(drone); } else { drone.remove(); }
  droneShown = show;
}

function frame() {
  requestAnimationFrame(frame);
  var c = current();
  if (!c || !drone) { return; }

  var scale = view === 'chase' ? 3 : view === 'top' ? 10 : view === 'orbit' ? 5 : Math.max(2, Math.min(40, (map.range || 300) / 50));
  drone.position = { lat: c.lat, lng: c.lng, altitude: c.alt };
  drone.orientation = { heading: c.h + noseOffset, tilt: c.p, roll: c.r };
  drone.scale = scale;
  setDroneVisible(view !== 'fpv');

  var absAlt = groundMsl + c.alt;
  if (view === 'chase') {
    map.center = { lat: c.lat, lng: c.lng, altitude: absAlt };
    map.heading = c.h; map.tilt = 75; map.range = 45; map.roll = 0;
  } else if (view === 'fpv') {
    // Camera sits on the drone looking forward: centre 60 m ahead, range 60 m.
    var ahead = offsetLatLng(c.lat, c.lng, c.h, 60);
    map.center = { lat: ahead.lat, lng: ahead.lng, altitude: absAlt + 60 * Math.sin(c.p * Math.PI / 180) };
    map.heading = c.h; map.tilt = Math.max(0, Math.min(90, 88 + c.p)); map.range = 60; map.roll = c.r;
  } else if (view === 'top') {
    map.center = { lat: c.lat, lng: c.lng, altitude: absAlt };
    map.heading = 0; map.tilt = 0; map.range = 350; map.roll = 0;
  } else if (view === 'orbit') {
    orbitHeading = (orbitHeading + 0.25) % 360;
    map.center = { lat: c.lat, lng: c.lng, altitude: absAlt };
    map.heading = orbitHeading; map.tilt = 65; map.range = 160; map.roll = 0;
  }

  var now = Date.now();
  if (now - lastTrailSet > 1000 && trailLine) { trailLine.path = trail.slice(); lastTrailSet = now; }
}

// ---- Buttons -----------------------------------------------------------------
var viewBtns = document.querySelectorAll('[data-view]');
for (var b = 0; b < viewBtns.length; b++) {
  viewBtns[b].onclick = function () {
    view = this.getAttribute('data-view');
    for (var k = 0; k < viewBtns.length; k++) { viewBtns[k].classList.toggle('on', viewBtns[k] === this); }
    if (map) { map.roll = 0; }
  };
}
document.getElementById('btrail').onclick = function () { trail = []; if (trailLine) { trailLine.path = []; } };
document.getElementById('bnose').onclick = function () {
  noseOffset = ((noseOffset + 90 + 180) % 360) - 180;
  try { localStorage.setItem('infradrone.noseOffset.google', String(noseOffset)); } catch (e) { }
};
</script>
<script async src='https://maps.googleapis.com/maps/api/js?key=__GOOGLE_KEY__&libraries=maps3d&callback=initMap&loading=async'
        onerror='showErr(""Could not load Google Maps. Check your internet connection."")'></script>
</body>
</html>";

        /// <summary>Shown at /google when no API key is configured yet.</summary>
        public const string SetupHtml = @"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<title>InfraDrone 3D — Google setup</title>
<style>
  body { margin:0; background:#0b1017; color:#cfe3f5; font:14px/1.6 system-ui, sans-serif; }
  .card { max-width:720px; margin:40px auto; background:#111a24; border:1px solid #23394f; border-radius:10px; padding:24px 28px; }
  h1 { font-size:20px; margin:0 0 6px; color:#fff; }
  ol li { margin-bottom:6px; }
  code { background:#0b1017; padding:1px 5px; border-radius:4px; color:#a5f3fc; }
  input { width:100%; box-sizing:border-box; padding:8px 10px; font:14px monospace; background:#0b1017; color:#fff;
          border:1px solid #2a5878; border-radius:6px; margin:8px 0; }
  .btn { background:#0e7490; color:#fff; border:1px solid #22d3ee; border-radius:6px; padding:8px 14px; cursor:pointer; font-size:14px; }
  .alt { background:#12324a; border-color:#2a5878; }
  #msg { margin-top:10px; color:#fbbf24; }
</style>
</head>
<body>
<div class='card'>
  <h1>Google photorealistic 3D — one-time setup</h1>
  <p>This map needs a free Google Maps API key. Google gives a free monthly allowance; a billing card is required to create the key.</p>
  <ol>
    <li>Open <code>https://console.cloud.google.com</code> and sign in with your Google account.</li>
    <li>Top bar: click the project picker, then <b>New project</b>. Name it <code>InfraDrone</code>, click <b>Create</b>, then select it.</li>
    <li>Left menu: <b>Billing</b>. Link a billing account (add a card if asked).</li>
    <li>Search bar: type <code>Maps JavaScript API</code>, open it, click <b>Enable</b>.</li>
    <li>Left menu: <b>APIs &amp; Services</b> &gt; <b>Credentials</b> &gt; <b>Create credentials</b> &gt; <b>API key</b>. Copy the key.</li>
    <li>Recommended: click the new key, under <b>API restrictions</b> choose <b>Restrict key</b> and tick <b>Maps JavaScript API</b>; under <b>Application restrictions</b> choose <b>Websites</b> and add <code>http://localhost:*/*</code>. Save.</li>
    <li>Paste the key below and click <b>Save key</b>. It is stored only on this computer.</li>
  </ol>
  <input id='key' placeholder='AIza...' autocomplete='off'>
  <button class='btn' id='save'>Save key</button>
  <button class='btn alt' onclick='location.href=""/pdok""'>Use the free PDOK map instead</button>
  <div id='msg'></div>
</div>
<script>
document.getElementById('save').onclick = function () {
  var k = document.getElementById('key').value.trim();
  fetch('/config/google-key', { method: 'POST', body: k }).then(function (r) { return r.text().then(function (t) { return { ok: r.ok, t: t }; }); })
    .then(function (res) {
      document.getElementById('msg').textContent = res.t;
      if (res.ok) { setTimeout(function () { location.href = '/google'; }, 600); }
    })
    .catch(function () { document.getElementById('msg').textContent = 'Could not reach InfraDrone Desktop.'; });
};
</script>
</body>
</html>";
    }
}
