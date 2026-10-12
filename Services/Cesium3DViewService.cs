// Services/Cesium3DViewService.cs
//
// Serves a local CesiumJS page in the user's normal browser and feeds it the
// current mission: waypoints plus the three SORA volumes (flight geography,
// contingency volume, ground risk buffer).
//
// Design notes:
//  - Uses only System.Net.HttpListener, built into .NET. No new NuGet packages,
//    so nothing here can conflict with Mapsui / Avalonia / SkiaSharp versions.
//  - The page POLLS /state (2 Hz) instead of using a WebSocket, because
//    HttpListener's WebSocket support is Windows-only; on Linux
//    AcceptWebSocketAsync throws PlatformNotSupportedException. Polling a local
//    loopback socket costs nothing and behaves the same for waypoint editing.
//  - The 3D view is read-only: the desktop app stays the single source of truth
//    for the mission. Nothing the browser does can alter a flight plan.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mapsui.Projections;
using NetTopologySuite.Geometries;

namespace InfraDroneDesktop.Services
{
    public sealed class Cesium3DViewService : IDisposable
    {
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly object _stateLock = new();
        private string _stateJson = "{\"waypoints\":[],\"volumes\":null}";

        // Live vehicle (real or simulated). Built per request so "stale" is current.
        private readonly object _vehicleLock = new();
        private (double lat, double lon, double altRel, double hdg, double roll, double pitch,
                 double speed, string mode, bool armed)? _vehicle;
        private DateTime _vehicleTime = DateTime.MinValue;
        private int _port;

        public bool IsRunning => _listener?.IsListening == true;
        public string Url => $"http://localhost:{_port}/";

        /// <summary>Starts the local server (first free port from 8787) and opens the browser.</summary>
        public void StartAndOpen()
        {
            if (!IsRunning) Start();
            OpenBrowser(Url);
        }

        /// <summary>Starts the server without opening a browser (used by the Suriname demo tab).</summary>
        public void EnsureStarted()
        {
            if (!IsRunning) Start();
        }

        private void Start()
        {
            Exception? last = null;
            for (var port = 8787; port < 8797; port++)
            {
                try
                {
                    var listener = new HttpListener();
                    listener.Prefixes.Add($"http://localhost:{port}/");
                    listener.Start();
                    _listener = listener;
                    _port = port;
                    _cts = new CancellationTokenSource();
                    _ = Task.Run(() => LoopAsync(_cts.Token));
                    Console.WriteLine($"[Cesium3D] serving on {Url}");
                    return;
                }
                catch (Exception ex) { last = ex; }
            }
            throw new InvalidOperationException("Could not bind a local port for the 3D view.", last);
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener?.IsListening == true)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; } // listener stopped
                // Handle requests in parallel: the 3D library loads many files at once.
                _ = Task.Run(() => Handle(ctx));
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url?.AbsolutePath ?? "/";
                if (TryServeDemo(ctx, path)) return;
                if (path == "/state")
                {
                    string json;
                    lock (_stateLock) json = _stateJson;
                    Write(ctx, "application/json", json);
                }
                else if (path == "/vehicle")
                {
                    Write(ctx, "application/json", VehicleJson());
                }
                else if (path == "/drone.glb")
                {
                    WriteBytes(ctx, "model/gltf-binary", Drone3DModel.Glb);
                }
                else if (path == "/config/google-key" && ctx.Request.HttpMethod == "POST")
                {
                    string body;
                    using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                        body = reader.ReadToEnd();
                    var (ok, msg) = SaveGoogleKey(body);
                    ctx.Response.StatusCode = ok ? 200 : 400;
                    Write(ctx, "text/plain; charset=utf-8", msg);
                }
                else if (path == "/pdok")
                {
                    Write(ctx, "text/html; charset=utf-8", Cesium3DPage.Html);
                }
                else if (path == "/google")
                {
                    var key = GetGoogleKey();
                    Write(ctx, "text/html; charset=utf-8",
                          key == null ? Google3DPage.SetupHtml : Google3DPage.Html.Replace("__GOOGLE_KEY__", key));
                }
                else
                {
                    // Default: Google photorealistic if a key is set, otherwise PDOK.
                    var key = GetGoogleKey();
                    Write(ctx, "text/html; charset=utf-8",
                          key == null ? Cesium3DPage.Html : Google3DPage.Html.Replace("__GOOGLE_KEY__", key));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Cesium3D] request error: " + ex.Message);
                try { ctx.Response.StatusCode = 500; ctx.Response.OutputStream.Close(); } catch { }
            }
        }

        // ---- Suriname demo + offline package ------------------------------------------------------
        private static readonly string DemoRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot", "demo");
        public static string DemoScenariosPath => Path.Combine(DemoRoot, "scenarios.json");

        private bool TryServeDemo(HttpListenerContext ctx, string path)
        {
            if (path == "/demo" || path == "/demo/")
            {
                ctx.Response.Redirect("/demo/index.html" + (ctx.Request.Url?.Query ?? ""));
                ctx.Response.Close();
                return true;
            }
            if (path == "/demo/config.json") { Write(ctx, "application/json", DemoConfigJson()); return true; }
            if (path == "/demo/cmd")
            {
                int.TryParse(ctx.Request.QueryString["after"], out var after);
                if (ctx.Request.QueryString["after"] == null) after = -1;
                Write(ctx, "application/json", DemoBridge.CommandsAfterJson(after));
                return true;
            }
            if (path == "/demo/status" && ctx.Request.HttpMethod == "POST")
            {
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    DemoBridge.SetStatus(reader.ReadToEnd());
                ctx.Response.StatusCode = 204; ctx.Response.Close();
                return true;
            }
            if (path.StartsWith("/demo/", StringComparison.Ordinal))
            { ServeFile(ctx, DemoRoot, path.Substring("/demo/".Length), cache: false); return true; }
            if (path.StartsWith("/offline/cesium/", StringComparison.Ordinal))
            { ServeFile(ctx, DemoOfflineService.CesiumDir, path.Substring("/offline/cesium/".Length), cache: true); return true; }
            if (path.StartsWith("/offline/tiles/s2/", StringComparison.Ordinal))
            { ServeFile(ctx, DemoOfflineService.TilesDir, path.Substring("/offline/tiles/s2/".Length), cache: true); return true; }
            return false;
        }

        private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html; charset=utf-8", [".js"] = "application/javascript; charset=utf-8",
            [".mjs"] = "application/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8",
            [".json"] = "application/json", [".xml"] = "application/xml", [".png"] = "image/png",
            [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".svg"] = "image/svg+xml", [".wasm"] = "application/wasm",
            [".glb"] = "model/gltf-binary", [".gltf"] = "model/gltf+json", [".ktx2"] = "image/ktx2",
            [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf", [".txt"] = "text/plain; charset=utf-8"
        };

        // Serves a file under 'root' only (no path escapes). Missing files: 404 with empty body.
        private static void ServeFile(HttpListenerContext ctx, string root, string rel, bool cache)
        {
            var res = ctx.Response;
            try
            {
                rel = Uri.UnescapeDataString(rel).Replace('/', Path.DirectorySeparatorChar);
                var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(Path.Combine(root, rel));
                if (!full.StartsWith(rootFull, StringComparison.Ordinal) || !File.Exists(full))
                {
                    res.StatusCode = 404; res.ContentLength64 = 0; res.OutputStream.Close(); return;
                }
                res.ContentType = Mime.TryGetValue(Path.GetExtension(full), out var ct) ? ct : "application/octet-stream";
                res.Headers["Cache-Control"] = cache ? "max-age=86400" : "no-store";
                using var fs = File.OpenRead(full);
                res.ContentLength64 = fs.Length;
                fs.CopyTo(res.OutputStream);
                res.OutputStream.Close();
            }
            catch (HttpListenerException) { /* browser closed the connection */ }
        }

        private static string DemoConfigJson()
        {
            var s = DemoBridge.Settings;
            var key = GetGoogleKey();
            var localCesium = DemoOfflineService.CesiumReady;
            var localTiles = Directory.Exists(DemoOfflineService.TilesDir);
            const string cdn = "https://cdn.jsdelivr.net/npm/cesium@" + DemoOfflineService.CesiumVersion + "/Build/Cesium/";
            const string eox = "https://tiles.maps.eox.at/wmts/1.0.0/s2cloudless_3857/default/g/{z}/{y}/{x}.jpg";
            var localImg = new Dictionary<string, object> { ["url"] = "/offline/tiles/s2/{z}/{x}/{y}.jpg", ["maxLevel"] = 15 };
            var onlineImg = new Dictionary<string, object> { ["url"] = eox, ["maxLevel"] = 15 };

            // Satellite map: offline package where available, otherwise straight from the internet.
            var useOnline = s.Map == "online";
            var satBase = useOnline || !localCesium ? cdn : "/offline/cesium/";
            var satImg = useOnline || !localTiles ? onlineImg : localImg;
            var fbBase = localCesium ? "/offline/cesium/" : cdn;
            var fbImg = localTiles ? localImg : onlineImg;
            var renderer = s.Map == "google" && key != null ? "google" : "cesium";

            var cfg = new Dictionary<string, object?>
            {
                ["renderer"] = renderer,
                ["googleKey"] = renderer == "google" ? key : null,
                ["cesiumBase"] = satBase,
                ["imagery"] = satImg,
                ["fallback"] = new Dictionary<string, object> { ["cesiumBase"] = fbBase, ["imagery"] = fbImg },
                ["modelUrl"] = "/drone.glb",
                ["lang"] = s.Lang,
                ["scenario"] = s.Scenario
            };
            if (s.EnduranceMin is int e && e > 0) cfg["aircraft"] = new Dictionary<string, object> { ["endurance_min"] = e };
            return System.Text.Json.JsonSerializer.Serialize(cfg);
        }

        private static void Write(HttpListenerContext ctx, string contentType, string body)
            => WriteBytes(ctx, contentType, Encoding.UTF8.GetBytes(body));

        private static void WriteBytes(HttpListenerContext ctx, string contentType, byte[] bytes)
        {
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        /// <summary>
        /// Publishes the current mission. Safe to call on every waypoint change;
        /// it only swaps a string, it does not touch the network.
        /// </summary>
        /// <param name="waypoints">lat, lon, altitude (m) per waypoint, in order.</param>
        /// <param name="volumes">SORA geometry in EPSG:3857, or null to draw none.</param>
        public void Publish(IReadOnlyList<(double lat, double lon, double altM)> waypoints,
                            SoraVolumeGeometry? volumes)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("{\"waypoints\":[");
            for (var i = 0; i < waypoints.Count; i++)
            {
                var w = waypoints[i];
                if (i > 0) sb.Append(',');
                sb.Append('{')
                  .Append("\"lat\":").Append(w.lat.ToString("F7", inv)).Append(',')
                  .Append("\"lon\":").Append(w.lon.ToString("F7", inv)).Append(',')
                  .Append("\"alt\":").Append(w.altM.ToString("F1", inv))
                  .Append('}');
            }
            sb.Append(']');

            if (volumes == null)
            {
                sb.Append(",\"volumes\":null");
            }
            else
            {
                var d = volumes.Dimensions;
                sb.Append(",\"hfg\":").Append(d.HFG.ToString("F1", inv));
                sb.Append(",\"hcv\":").Append(d.HCV.ToString("F1", inv));
                sb.Append(",\"volumes\":{");
                sb.Append("\"fg\":").Append(RingsJson(volumes.FlightGeography)).Append(',');
                sb.Append("\"cv\":").Append(RingsJson(volumes.ContingencyVolume)).Append(',');
                sb.Append("\"grb\":").Append(RingsJson(volumes.GroundRiskBuffer));
                sb.Append('}');
            }
            sb.Append('}');

            lock (_stateLock) _stateJson = sb.ToString();
        }

        /// <summary>
        /// Publishes the live vehicle pose. Called from the telemetry handler
        /// (already throttled there); it only swaps a value under a lock.
        /// altRelM is altitude above HOME, as reported by the autopilot.
        /// </summary>
        public void PublishVehicle(double lat, double lon, double altRelM, double headingDeg,
                                   double rollDeg, double pitchDeg, double speedMs,
                                   string mode, bool armed)
        {
            lock (_vehicleLock)
            {
                _vehicle = (lat, lon, altRelM, headingDeg, rollDeg, pitchDeg, speedMs, mode ?? "", armed);
                _vehicleTime = DateTime.UtcNow;
            }
        }

        // ---- Google Maps API key (stored only on this computer) --------------
        private static string GoogleKeyPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".infradrone", "google_maps_api_key.txt");

        private static readonly System.Text.RegularExpressions.Regex KeyPattern =
            new("^[A-Za-z0-9_-]{20,100}$");

        /// <summary>Key from INFRADRONE_GOOGLE_MAPS_KEY, else the saved file, else null.</summary>
        public static string? GetGoogleKey()
        {
            var env = Environment.GetEnvironmentVariable("INFRADRONE_GOOGLE_MAPS_KEY");
            if (!string.IsNullOrWhiteSpace(env) && KeyPattern.IsMatch(env.Trim())) return env.Trim();
            try
            {
                if (File.Exists(GoogleKeyPath))
                {
                    var k = File.ReadAllText(GoogleKeyPath).Trim();
                    if (KeyPattern.IsMatch(k)) return k;
                }
            }
            catch { }
            return null;
        }

        private static (bool ok, string msg) SaveGoogleKey(string key)
        {
            key = (key ?? "").Trim();
            if (!KeyPattern.IsMatch(key))
                return (false, "That does not look like a Google API key (letters, digits, - and _, usually starting with AIza).");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(GoogleKeyPath)!);
                File.WriteAllText(GoogleKeyPath, key);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(GoogleKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return (true, "Key saved. Loading the Google 3D map...");
            }
            catch (Exception ex) { return (false, "Could not save the key: " + ex.Message); }
        }

        private string VehicleJson()
        {
            var inv = CultureInfo.InvariantCulture;
            lock (_vehicleLock)
            {
                if (_vehicle == null) return "{\"present\":false}";
                var v = _vehicle.Value;
                var age = (DateTime.UtcNow - _vehicleTime).TotalSeconds;
                var mode = v.mode.Replace("\\", "").Replace("\"", "");
                return "{\"present\":true" +
                       ",\"stale\":" + (age > 5 ? "true" : "false") +
                       ",\"lat\":" + v.lat.ToString("F7", inv) +
                       ",\"lon\":" + v.lon.ToString("F7", inv) +
                       ",\"alt\":" + v.altRel.ToString("F2", inv) +
                       ",\"hdg\":" + v.hdg.ToString("F1", inv) +
                       ",\"roll\":" + v.roll.ToString("F1", inv) +
                       ",\"pitch\":" + v.pitch.ToString("F1", inv) +
                       ",\"speed\":" + v.speed.ToString("F1", inv) +
                       ",\"mode\":\"" + mode + "\"" +
                       ",\"armed\":" + (v.armed ? "true" : "false") +
                       GroundMslJson(inv) + "}";
            }
        }

        /// <summary>
        /// Home ground height above sea level (alt MSL - alt above home), from the
        /// Live Telemetry service if it has both. Used by the Google camera, whose
        /// centre altitude is above sea level. Empty when unknown.
        /// </summary>
        private static string GroundMslJson(CultureInfo inv)
        {
            var snap = LiveTelemetryService.Shared?.Snapshot();
            if (snap == null || double.IsNaN(snap.AltMsl) || double.IsNaN(snap.AltRel)) return "";
            return ",\"gmsl\":" + (snap.AltMsl - snap.AltRel).ToString("F1", inv);
        }

        /// <summary>Outer rings only, as [[lon,lat,lon,lat,...], ...] in WGS84.</summary>
        private static string RingsJson(Geometry geom)
        {
            var inv = CultureInfo.InvariantCulture;
            var polys = geom is MultiPolygon mp
                ? mp.Geometries.OfType<Polygon>()
                : new[] { (Polygon)geom }.AsEnumerable();

            var sb = new StringBuilder("[");
            var first = true;
            foreach (var poly in polys)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('[');
                var coords = poly.ExteriorRing.Coordinates;
                for (var i = 0; i < coords.Length; i++)
                {
                    var (lon, lat) = SphericalMercator.ToLonLat(coords[i].X, coords[i].Y);
                    if (i > 0) sb.Append(',');
                    sb.Append(lon.ToString("F7", inv)).Append(',').Append(lat.ToString("F7", inv));
                }
                sb.Append(']');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static void OpenBrowser(string url)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
                else
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cesium3D] could not open a browser: {ex.Message}. Open {url} manually.");
            }
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            _listener = null;
        }
    }
}
