// Services/LvnlAirspaceService.cs
//
// Official Dutch airspace from LVNL (Luchtverkeersleiding Nederland, Dutch air
// traffic control), live from its public ArcGIS service - no login:
//   https://services-eu1.arcgis.com/OtUwzhpKSdeXgRIB/arcgis/rest/services/Airspaces_data/FeatureServer
// (found via the LVNL Geoportaal, geoportaal.lvnl.nl, item f70d761a86654aea8a7f1764c83d183a;
// service description "Airspaces within the Dutch FIR", copyright LVNL).
// Fields used: Name, Designator, Class, LowerLimit/Unit/Reference, UpperLimit/Unit/Reference.
//
// OFFLINE FIRST: the last download is cached in ~/.infradrone/airspace/, so the
// map and the route check work at a demo with no internet. Order of preference:
//   1. cache (instant), refreshed in the background when older than 24 h
//   2. the old static file ~/agri_drone/airspace_nl.geojson (labelled as such)
//
// SCOPE - honest limits:
//   * This is manned-aviation airspace (CTR, ATZ, restricted/danger areas, ...).
//     It does NOT include every drone rule (municipal bans, nature reserves,
//     temporary NOTAM restrictions). Always also check NOTAMs and the GO drone app.
//   * Height check is approximate: limits in feet MSL/flight level are compared
//     with the route's height above take-off plus a 500 ft margin (Dutch terrain
//     is near sea level; QNH and terrain are not modelled).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace InfraDroneDesktop.Services
{
    public enum AirspaceSeverity { NoGo, Caution }

    public sealed class AirspaceZone
    {
        public string Category { get; init; } = "";      // e.g. "CTR", "Restricted area"
        public string Name { get; init; } = "";
        public string Designator { get; init; } = "";
        public string Class { get; init; } = "";
        public string LowerText { get; init; } = "";       // e.g. "SFC", "1500 FT MSL", "FL 65"
        public string UpperText { get; init; } = "";
        public double LowerFt { get; init; }               // approximate, feet above sea level
        public AirspaceSeverity Severity { get; init; }
        public string Advice { get; init; } = "";
        public Geometry Geometry { get; init; } = default!; // WGS84 lon/lat
        public string Label => string.IsNullOrWhiteSpace(Designator) ? Name : $"{Name} ({Designator})";
        public string Limits => $"{LowerText} – {UpperText}";
    }

    public sealed class AirspaceConflict
    {
        public AirspaceZone Zone { get; init; } = default!;
        public bool RouteEnters { get; init; }      // false = only nearby
        public double DistanceM { get; init; }      // 0 when the route enters it
    }

    public sealed class LvnlAirspaceService
    {
        public static LvnlAirspaceService Instance { get; } = new LvnlAirspaceService();
        private LvnlAirspaceService() { }

        public const string ServiceUrl =
            "https://services-eu1.arcgis.com/OtUwzhpKSdeXgRIB/arcgis/rest/services/Airspaces_data/FeatureServer";

        // Layers that matter below ~400 ft. FIR / ACC sectors / IFR / VFR / delegated
        // areas are left out: they don't restrict a low drone flight by themselves.
        private static readonly (int Id, string Category, AirspaceSeverity Sev, string Advice)[] Layers =
        {
            (19, "Prohibited area",   AirspaceSeverity.NoGo,    "Flying is not allowed."),
            (18, "Restricted area",   AirspaceSeverity.NoGo,    "Only with permission of the area's authority."),
            (15, "CTR",               AirspaceSeverity.NoGo,    "Control zone around an airport: needs LVNL / air traffic control permission."),
            (7,  "ATZ",               AirspaceSeverity.NoGo,    "Aerodrome traffic zone: needs permission from the aerodrome."),
            (9,  "HPZ/HTZ",           AirspaceSeverity.NoGo,    "Helicopter protection / traffic zone: needs permission."),
            (17, "Danger area",       AirspaceSeverity.Caution, "Hazardous activity (e.g. firing) when active - check NOTAMs."),
            (3,  "TSA",               AirspaceSeverity.Caution, "Temporary segregated area - check NOTAMs for activation."),
            (5,  "TRA",               AirspaceSeverity.Caution, "Temporary reserved area - check NOTAMs for activation."),
            (6,  "Parajump",          AirspaceSeverity.Caution, "Parachute jumping area - check activity times."),
            (1,  "Low flying area",   AirspaceSeverity.Caution, "Military low flying - expect fast low traffic."),
            (11, "Glider area",       AirspaceSeverity.Caution, "Gliders, often without ADS-B (FLARM only)."),
            (8,  "TMZ/RTMZ",          AirspaceSeverity.Caution, "Transponder mandatory zone - check whether your operation needs one."),
            (10, "RMZ",               AirspaceSeverity.Caution, "Radio mandatory zone - check radio requirements."),
            (14, "TMA",               AirspaceSeverity.Caution, "Terminal control area - normally above drone height; check its lower limit."),
            (13, "CTA",               AirspaceSeverity.Caution, "Control area - normally above drone height; check its lower limit."),
        };

        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
        private readonly object _lock = new();
        private List<AirspaceZone> _zones = new();

        public IReadOnlyList<AirspaceZone> Zones { get { lock (_lock) return _zones; } }
        /// <summary>Where the zones came from, for the map label, e.g. "LVNL live · downloaded 13-10 09:12".</summary>
        public string SourceText { get; private set; } = "Airspace: not loaded";
        public event Action? ZonesChanged;

        private static string CacheDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".infradrone", "airspace");
        private static string CacheFile => Path.Combine(CacheDir, "lvnl_airspace.geojson");
        private static string OldStaticFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "agri_drone", "airspace_nl.geojson");

        // ------------------------------------------------------------------
        /// <summary>Loads the cache (or old static file) now; refreshes from LVNL in the background if stale.</summary>
        public void LoadCachedThenRefresh()
        {
            if (File.Exists(CacheFile) && TryLoad(CacheFile, fromCache: true))
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(CacheFile);
                if (age > TimeSpan.FromHours(24)) _ = RefreshAsync();
                return;
            }
            if (File.Exists(OldStaticFile)) TryLoadOldStatic();
            _ = RefreshAsync();
        }

        /// <summary>Downloads all relevant layers from LVNL and replaces the cache. Returns a status line.</summary>
        public async Task<string> RefreshAsync(CancellationToken ct = default)
        {
            try
            {
                var all = new List<IFeature>();
                foreach (var (id, category, _, _) in Layers)
                {
                    int offset = 0;
                    while (true)
                    {
                        var url = $"{ServiceUrl}/{id}/query?where=1%3D1&outFields=*&outSR=4326" +
                                  $"&geometryPrecision=6&resultOffset={offset}&resultRecordCount=1000&f=geojson";
                        var json = await _http.GetStringAsync(url, ct);
                        var fc = new GeoJsonReader().Read<FeatureCollection>(json);
                        foreach (var f in fc)
                        {
                            f.Attributes ??= new AttributesTable();
                            SetAttr(f.Attributes, "_category", category);
                            SetAttr(f.Attributes, "_layer", id);
                            all.Add(f);
                        }
                        if (fc.Count < 1000) break;
                        offset += fc.Count;
                    }
                }
                if (all.Count == 0) throw new InvalidOperationException("LVNL returned no airspace.");

                Directory.CreateDirectory(CacheDir);
                var outFc = new FeatureCollection();
                foreach (var f in all) outFc.Add(f);
                var tmp = CacheFile + ".tmp";
                File.WriteAllText(tmp, new GeoJsonWriter().Write(outFc));
                File.Move(tmp, CacheFile, overwrite: true);   // never leave a half-written cache
                TryLoad(CacheFile, fromCache: false);
                return $"Airspace updated from LVNL: {all.Count} zones.";
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Airspace] LVNL refresh failed: " + ex.Message);
                return "Could not reach LVNL (" + ex.Message + "). Keeping " + SourceText.ToLowerInvariant() + ".";
            }
        }

        private static void SetAttr(IAttributesTable t, string name, object value)
        {
            if (t.Exists(name)) t[name] = value; else t.Add(name, value);
        }

        private bool TryLoad(string path, bool fromCache)
        {
            try
            {
                var fc = new GeoJsonReader().Read<FeatureCollection>(File.ReadAllText(path));
                var zones = new List<AirspaceZone>();
                foreach (var f in fc)
                {
                    if (f.Geometry == null || f.Geometry.IsEmpty) continue;
                    var a = f.Attributes;
                    int layer = ToInt(Get(a, "_layer"));
                    var def = Layers.FirstOrDefault(l => l.Id == layer);
                    if (def.Category == null) continue;

                    var (lowerText, lowerFt) = Limit(Get(a, "LowerLimit"), Get(a, "LowerLimitUnit"), Get(a, "LowerLimitReference"));
                    var (upperText, _) = Limit(Get(a, "UpperLimit"), Get(a, "UpperLimitUnit"), Get(a, "UpperLimitReference"));
                    zones.Add(new AirspaceZone
                    {
                        Category = def.Category,
                        Name = Get(a, "Name")?.ToString()?.Trim() ?? "",
                        Designator = Get(a, "Designator")?.ToString()?.Trim() ?? "",
                        Class = Get(a, "Class")?.ToString()?.Trim() ?? "",
                        LowerText = lowerText,
                        UpperText = upperText,
                        LowerFt = lowerFt,
                        Severity = def.Sev,
                        Advice = def.Advice,
                        Geometry = f.Geometry,
                    });
                }
                if (zones.Count == 0) return false;
                lock (_lock) _zones = zones;
                var when = File.GetLastWriteTime(path);
                SourceText = $"LVNL airspace · {zones.Count} zones · downloaded {when:dd-MM HH:mm}" +
                             (fromCache ? " (cached)" : "");
                ZonesChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Airspace] could not read " + path + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>Fallback: the older static LVNL file, shown as CTR-type zones with names only.</summary>
        private void TryLoadOldStatic()
        {
            try
            {
                var fc = new GeoJsonReader().Read<FeatureCollection>(File.ReadAllText(OldStaticFile));
                var zones = new List<AirspaceZone>();
                foreach (var f in fc)
                {
                    if (f.Geometry == null || f.Geometry.IsEmpty) continue;
                    var name = Get(f.Attributes, "name")?.ToString() ?? Get(f.Attributes, "designator")?.ToString() ?? "Zone";
                    zones.Add(new AirspaceZone
                    {
                        Category = "Zone (offline copy)", Name = name, LowerText = "?", UpperText = "?",
                        LowerFt = 0, Severity = AirspaceSeverity.Caution,
                        Advice = "Old offline copy without height limits - refresh from LVNL when online.",
                        Geometry = f.Geometry,
                    });
                }
                lock (_lock) _zones = zones;
                SourceText = $"Old offline airspace copy · {zones.Count} zones · refresh when online";
                ZonesChanged?.Invoke();
            }
            catch (Exception ex) { Console.WriteLine("[Airspace] old file: " + ex.Message); }
        }

        private static object? Get(IAttributesTable? t, string name) =>
            t != null && t.Exists(name) ? t[name] : null;

        private static int ToInt(object? o)
        {
            if (o == null) return -1;
            if (o is JsonElement je && je.TryGetInt32(out var i)) return i;
            return int.TryParse(o.ToString(), out var v) ? v : (int)Math.Round(Convert.ToDouble(o));
        }

        /// <summary>Text like "1500 FT MSL" and an approximate value in feet above sea level.</summary>
        private static (string Text, double Ft) Limit(object? value, object? unit, object? reference)
        {
            var r = reference?.ToString()?.Trim().ToUpperInvariant() ?? "";
            var u = unit?.ToString()?.Trim().ToUpperInvariant() ?? "FT";
            double v = 0;
            if (value != null) double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float,
                                               System.Globalization.CultureInfo.InvariantCulture, out v);
            if (r is "SFC" or "GND" || (v == 0 && r != "STD")) return ("SFC", 0);
            if (u == "FL") return ($"FL {v:0}", v * 100);          // flight level: hundreds of feet (std pressure)
            if (u == "M") return ($"{v:0} M {r}", v * 3.281);
            return ($"{v:0} FT {r}".Trim(), v);
        }

        // ------------------------------------------------------------------
        /// <summary>
        /// Checks a planned route - every leg, not just the waypoints - against the
        /// zones whose lower limit is within reach of the route's highest point.
        /// Also lists relevant zones within nearbyM of the route.
        /// </summary>
        public List<AirspaceConflict> CheckRoute(IReadOnlyList<(double Lat, double Lon, double AltM)> route,
                                                 double nearbyM = 500)
        {
            var result = new List<AirspaceConflict>();
            if (route == null || route.Count == 0) return result;
            var gf = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
            Geometry path = route.Count == 1
                ? gf.CreatePoint(new Coordinate(route[0].Lon, route[0].Lat))
                : gf.CreateLineString(route.Select(p => new Coordinate(p.Lon, p.Lat)).ToArray());
            double maxFt = route.Max(p => p.AltM) * 3.281;
            double midLat = route.Average(p => p.Lat);
            double mPerDeg = 111_320.0 * Math.Cos(midLat * Math.PI / 180.0); // conservative (smaller of lat/lon)

            foreach (var z in Zones)
            {
                if (z.LowerFt > maxFt + 500) continue; // zone starts well above the route
                try
                {
                    if (z.Geometry.Intersects(path))
                        result.Add(new AirspaceConflict { Zone = z, RouteEnters = true, DistanceM = 0 });
                    else
                    {
                        double d = z.Geometry.Distance(path) * mPerDeg;
                        if (d <= nearbyM)
                            result.Add(new AirspaceConflict { Zone = z, RouteEnters = false, DistanceM = d });
                    }
                }
                catch { /* invalid source geometry: skip rather than crash the check */ }
            }
            return result
                .OrderBy(c => c.RouteEnters ? 0 : 1)
                .ThenBy(c => c.Zone.Severity)
                .ThenBy(c => c.DistanceM)
                .ToList();
        }

        /// <summary>All zones at a point (for click-to-inspect).</summary>
        public List<AirspaceZone> ZonesAt(double lat, double lon)
        {
            var pt = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory()
                         .CreatePoint(new Coordinate(lon, lat));
            var list = new List<AirspaceZone>();
            foreach (var z in Zones)
            {
                try { if (z.Geometry.Contains(pt)) list.Add(z); } catch { }
            }
            return list.OrderBy(z => z.Severity).ThenBy(z => z.LowerFt).ToList();
        }
    }
}
