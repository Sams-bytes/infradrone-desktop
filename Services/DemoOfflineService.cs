// Services/DemoOfflineService.cs
//
// Prepares the Suriname demo to run with NO internet:
//  1. CesiumJS (the 3D globe library) — downloaded once from the npm registry,
//     version pinned, extracted to ~/.infradrone/offline/cesium.
//  2. Satellite imagery tiles for the demo corridors — Sentinel-2 cloudless 2016
//     by EOX IT Services GmbH (contains modified Copernicus Sentinel data 2016),
//     licensed CC BY 4.0, which allows commercial use with attribution (shown on
//     screen). Stored as ~/.infradrone/offline/tiles/s2/{z}/{x}/{y}.jpg.
//
// Tiles are chosen around every route in scenarios.json: wide context at low zoom,
// ±6 km along the routes at zoom 11-13, ±2.5 km at zoom 14, ±1.5 km around key
// points at zoom 15. About 1,450 tiles (~35 MB). Already-downloaded tiles are skipped,
// so the button can be pressed again to resume or top up.

using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InfraDroneDesktop.Services
{
    public sealed class DemoOfflineService
    {
        public const string CesiumVersion = "1.146.0";
        private const string CesiumTarball = "https://registry.npmjs.org/cesium/-/cesium-" + CesiumVersion + ".tgz";
        private const string TileUrl = "https://tiles.maps.eox.at/wmts/1.0.0/s2cloudless_3857/default/g/{z}/{y}/{x}.jpg";

        private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static string Root => Path.Combine(Home, ".infradrone", "offline");
        public static string CesiumDir => Path.Combine(Root, "cesium");
        public static string TilesDir => Path.Combine(Root, "tiles", "s2");
        public static bool CesiumReady => File.Exists(Path.Combine(CesiumDir, "Cesium.js"));

        public static int TileCount()
        {
            try { return Directory.Exists(TilesDir) ? Directory.EnumerateFiles(TilesDir, "*.jpg", SearchOption.AllDirectories).Count() : 0; }
            catch { return 0; }
        }

        /// <summary>message, fraction 0..1. Raised on a background thread.</summary>
        public event Action<string, double>? Progress;

        private static readonly HttpClient Http = CreateHttp();
        private static HttpClient CreateHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("InfraDrone-Desktop/1.0 (DAMbv; demo offline cache)");
            return h;
        }

        public async Task<(bool Ok, string Message)> PrepareAsync(string scenariosJsonPath, CancellationToken ct)
        {
            try
            {
                Directory.CreateDirectory(Root);
                if (!CesiumReady)
                {
                    Report("Downloading CesiumJS " + CesiumVersion + " (~23 MB)…", 0.02);
                    await DownloadCesiumAsync(ct);
                }
                Report("CesiumJS ready.", 0.3);

                var tiles = PlanTiles(scenariosJsonPath);
                var todo = tiles.Where(t => !File.Exists(TilePath(t.z, t.x, t.y))).ToList();
                Report($"Satellite tiles: {tiles.Count - todo.Count} of {tiles.Count} already on disk.", 0.3);

                int done = 0, failed = 0;
                using var gate = new SemaphoreSlim(6);
                var tasks = todo.Select(async t =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        if (!await DownloadTileAsync(t.z, t.x, t.y, ct)) Interlocked.Increment(ref failed);
                    }
                    finally
                    {
                        gate.Release();
                        var d = Interlocked.Increment(ref done);
                        if (d % 20 == 0 || d == todo.Count)
                            Report($"Satellite tiles: {tiles.Count - todo.Count + d} of {tiles.Count}…", 0.3 + 0.7 * d / Math.Max(1, todo.Count));
                    }
                }).ToList();
                await Task.WhenAll(tasks);

                var msg = failed == 0
                    ? $"Offline package ready: CesiumJS + {TileCount()} satellite tiles."
                    : $"Offline package ready with {failed} tiles missing (not available or network error). Press again to retry.";
                Report(msg, 1.0);
                return (true, msg);
            }
            catch (OperationCanceledException) { return (false, "Cancelled. Press again to continue where it stopped."); }
            catch (Exception ex) { return (false, "Offline preparation failed: " + ex.Message); }
        }

        private void Report(string msg, double frac) { try { Progress?.Invoke(msg, frac); } catch { } }

        // ---- CesiumJS ----------------------------------------------------------------
        private async Task DownloadCesiumAsync(CancellationToken ct)
        {
            var tmpDir = CesiumDir + ".part";
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            Directory.CreateDirectory(tmpDir);
            const string prefix = "package/Build/Cesium/";

            using (var resp = await Http.GetAsync(CesiumTarball, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var net = await resp.Content.ReadAsStreamAsync(ct);
                await using var gz = new GZipStream(net, CompressionMode.Decompress);
                using var tar = new TarReader(gz);
                TarEntry? e;
                int files = 0;
                while ((e = tar.GetNextEntry()) != null)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!e.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    if (e.EntryType != TarEntryType.RegularFile && e.EntryType != TarEntryType.V7RegularFile) continue;
                    var rel = e.Name.Substring(prefix.Length);
                    if (rel.Length == 0 || rel.Contains("..")) continue;
                    var dest = Path.Combine(tmpDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    e.ExtractToFile(dest, overwrite: true);
                    if (++files % 200 == 0) Report($"Extracting CesiumJS… {files} files", 0.05 + Math.Min(0.2, files / 6000.0));
                }
            }
            if (!File.Exists(Path.Combine(tmpDir, "Cesium.js")))
                throw new InvalidOperationException("CesiumJS package did not contain Build/Cesium/Cesium.js");
            if (Directory.Exists(CesiumDir)) Directory.Delete(CesiumDir, true);
            Directory.Move(tmpDir, CesiumDir);
        }

        // ---- Tiles --------------------------------------------------------------------------
        public static string TilePath(int z, int x, int y) =>
            Path.Combine(TilesDir, z.ToString(), x.ToString(), y + ".jpg");

        private static async Task<bool> DownloadTileAsync(int z, int x, int y, CancellationToken ct)
        {
            var url = TileUrl.Replace("{z}", z.ToString()).Replace("{y}", y.ToString()).Replace("{x}", x.ToString());
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var resp = await Http.GetAsync(url, ct);
                    if ((int)resp.StatusCode == 404) return false; // not available at this zoom
                    resp.EnsureSuccessStatusCode();
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    var path = TilePath(z, x, y);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var tmp = path + ".part";
                    await File.WriteAllBytesAsync(tmp, bytes, ct);
                    File.Move(tmp, path, overwrite: true);
                    return true;
                }
                catch (OperationCanceledException) { throw; }
                catch { await Task.Delay(500 * (attempt + 1), ct); }
            }
            return false;
        }

        /// <summary>Tiles needed for every scenario's routes (same plan as tools/suriname-demo).</summary>
        public static List<(int z, int x, int y)> PlanTiles(string scenariosJsonPath)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(scenariosJsonPath));
            var set = new HashSet<(int, int, int)>();
            var all = new List<(double lat, double lon)>();
            foreach (var sc in doc.RootElement.GetProperty("scenarios").EnumerateObject())
            {
                var S = sc.Value;
                var sar = S.GetProperty("sar");
                var area = Pts(sar.GetProperty("area"));
                var lines = new List<List<(double, double)>>
                {
                    Pts(S.GetProperty("transit")), Pts(S.GetProperty("patrol")),
                    Pts(S.GetProperty("ground_unit").GetProperty("path")),
                    area.Concat(new[] { area[0] }).ToList(),
                    new() { Pt(sar.GetProperty("team_start")), Pt(sar.GetProperty("target")) },
                    new() { Pt(S.GetProperty("patrol")[S.GetProperty("boat").GetProperty("track_index").GetInt32()]), Pt(sar.GetProperty("lines")[0][0]) },
                    new() { Pt(sar.GetProperty("target")), Pt(S.GetProperty("fob")) },
                    new() { Pt(S.GetProperty("fob")), Pt(S.GetProperty("patrol")[0]) },
                };
                var focus = new List<(double, double)>
                {
                    Pt(S.GetProperty("hq")), Pt(S.GetProperty("fob")), Pt(S.GetProperty("boat").GetProperty("cross")),
                    Pt(sar.GetProperty("target")), Pt(sar.GetProperty("lkp")), Pt(sar.GetProperty("team_start"))
                };
                focus.AddRange(area);
                foreach (var ln in lines)
                    foreach (var p in Sample(ln, 0.5))
                    {
                        all.Add(p);
                        for (int z = 11; z <= 13; z++) Around(p, 6, z, set);
                        Around(p, 2.5, 14, set);
                    }
                foreach (var p in focus) Around(p, 1.5, 15, set);
            }
            if (all.Count > 0)
            {
                double la0 = all.Min(p => p.lat) - 0.6, la1 = all.Max(p => p.lat) + 0.6;
                double lo0 = all.Min(p => p.lon) - 0.6, lo1 = all.Max(p => p.lon) + 0.6;
                for (int z = 5; z <= 10; z++)
                {
                    var (x0, y1) = Tile(la0, lo0, z); var (x1, y0) = Tile(la1, lo1, z);
                    for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) set.Add((z, x, y));
                }
            }
            return set.Select(t => (t.Item1, t.Item2, t.Item3)).OrderBy(t => t.Item1).ToList();
        }

        private static (double, double) Pt(JsonElement e) => (e[0].GetDouble(), e[1].GetDouble());
        private static List<(double, double)> Pts(JsonElement arr) => arr.EnumerateArray().Select(Pt).ToList();

        private static IEnumerable<(double lat, double lon)> Sample(List<(double lat, double lon)> line, double stepKm)
        {
            for (int i = 0; i + 1 < line.Count; i++)
            {
                var a = line[i]; var b = line[i + 1];
                var d = Math.Sqrt(Math.Pow((b.lat - a.lat) * 111.32, 2) + Math.Pow((b.lon - a.lon) * 111.32 * Math.Cos(a.lat * Math.PI / 180), 2));
                var n = Math.Max(1, (int)(d / stepKm));
                for (int k = 0; k <= n; k++) yield return (a.lat + (b.lat - a.lat) * k / n, a.lon + (b.lon - a.lon) * k / n);
            }
        }

        private static (int x, int y) Tile(double lat, double lon, int z)
        {
            double n = Math.Pow(2, z), r = lat * Math.PI / 180;
            int x = (int)Math.Floor((lon + 180) / 360 * n);
            int y = (int)Math.Floor((1 - Math.Log(Math.Tan(r) + 1 / Math.Cos(r)) / Math.PI) / 2 * n);
            return (x, y);
        }

        private static void Around((double lat, double lon) p, double km, int z, HashSet<(int, int, int)> set)
        {
            double dlat = km / 111.32, dlon = km / (111.32 * Math.Cos(p.lat * Math.PI / 180));
            var (x0, y1) = Tile(p.lat - dlat, p.lon - dlon, z);
            var (x1, y0) = Tile(p.lat + dlat, p.lon + dlon, z);
            for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) set.Add((z, x, y));
        }
    }
}
