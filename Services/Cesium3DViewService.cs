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
                try
                {
                    var path = ctx.Request.Url?.AbsolutePath ?? "/";
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
                    else
                    {
                        Write(ctx, "text/html; charset=utf-8", Cesium3DPage.Html);
                    }
                }
                catch (Exception ex) { Console.WriteLine("[Cesium3D] request error: " + ex.Message); }
            }
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
                       ",\"armed\":" + (v.armed ? "true" : "false") + "}";
            }
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
