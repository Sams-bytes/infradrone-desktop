// OverijsselGeoService.cs
// Mirrors the existing Province of Groningen ArcGIS integration pattern (WMS/WFS via GeoServer),
// but scoped to province Overijssel's server, bbox-limited around Enschede's Stadskantoor
// (Hengelosestraat 51, 7514 AD Enschede — the actual municipal offices).
//
// REWRITTEN — the original base URL (gisopenbaar.overijssel.nl/data/ows) is DEAD.
// It now 302-redirects to https://www.geoportaaloverijssel.nl (confirmed via curl -v).
// The real, live GeoServer instance is at services.geodataoverijssel.nl, confirmed via
// live GetCapabilities against the B22_wegen workspace (curl + grep on 2026-08-28).
//
// IMPORTANT: verify the exact gemeentehuis coordinates via the existing PDOK Locatieserver
// search already wired into Flight View's toolbar before locking this bbox in for a real demo.
// The values below are a close estimate, not a surveyed coordinate.

using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace InfraDroneDesktop.Services
{
    /// <summary>
    /// Fixed reference point + bbox for Enschede's Stadskantoor.
    /// Keep this separate from Groningen's constants — do not merge into the
    /// existing GroningenArcGisService class or its layer list.
    /// </summary>
    public static class EnschedeReferencePoint
    {
        // Real Enschede municipal boundary (OSM Nominatim, relation 1380244, category=boundary
        // type=administrative — confirmed as the actual municipal area, not just a city-center
        // point). Covers the whole city including outlying areas like Glanerbrug, not just the
        // center. Source: nominatim.openstreetmap.org, confirmed 2026-08-28.
        public const double MinLat = 52.1612059;
        public const double MaxLat = 52.2855057;
        public const double MinLon = 6.7558928;
        public const double MaxLon = 6.9811001;

        // Kept for reference / any code still expecting a center point.
        public const double CenterLat = 52.22295793; // PDOK-confirmed, Hengelosestraat 51
        public const double CenterLon = 6.89156908; // PDOK-confirmed, Hengelosestraat 51

        public static (double minLon, double minLat, double maxLon, double maxLat) GetBBox()
        {
            return (MinLon, MinLat, MaxLon, MaxLat);
        }

        // Dutch WFS/WMS services often expect EPSG:28992 (RD New) rather than WGS84.
        // If the server rejects EPSG:4326 bbox params, reproject this bbox to RD New the
        // same way you already do with gdalwarp for orthomosaic loading, or request the
        // WMS in CRS=EPSG:4326 explicitly if the server supports it (most modern GeoServer
        // instances do via WMS 1.3.0's CRS param).
    }

    public class OverijsselGeoService
    {
        private readonly HttpClient _http;

        // CONFIRMED LIVE 2026-08-28 via curl GetCapabilities against this exact URL.
        // All five layers below live in the same B22_wegen GeoServer workspace, so there
        // is only one base URL for this whole curated set — unlike the original file's
        // per-request-guessed URL, this one is verified.
        private const string BaseUrl = "https://services.geodataoverijssel.nl/geoserver/B22_wegen/wfs";

        // Curated shortlist — NOT the full provincial catalog. Add more only as needed.
        // Every typeName below was confirmed present in the live GetCapabilities response
        // for the B22_wegen workspace (2026-08-28) — none of these are guessed.
        public static class Layers
        {
            // General road centerlines (Nationaal Wegenbestand) — city-scale, matches the
            // original "local/regional roads" intent better than the provincial-only layer.
            //
            // SCALE WARNING (confirmed 2026-08-28): this layer has 18,984 features across
            // the whole Enschede municipality — the full WFS GetFeature response is ~35MB
            // of GML. For city-wide display, use BuildWmsTileUrl(Layers.Wegen) instead of
            // FetchLayerNearEnschedeAsync — WMS renders server-side and returns a small PNG
            // tile, avoiding a ~35MB client-side XML parse + ~19k Mapsui feature objects.
            // Only use the WFS path for Wegen when you genuinely need per-segment
            // click-to-inspect attributes in a small area (a few streets, not the whole city).
            public const string Wegen = "B22_wegen:B2_Hartlijnen_wegen_NWB";

            public const string Kunstwerken = "B22_wegen:B22_Kunstwerken_langs_provinciale_wegen_en_vaarwegen";

            // Covers both national (rijks) and provincial roads. The waterways variant
            // (B22_Hectometerpunten_provinciale_waterwegen) was deliberately excluded —
            // not a road layer.
            public const string Hectometerpunten = "B22_wegen:B22_Hectometerpunten_langs_rijks-_en_provinciale_wegen_volgens_NWB";

            // Provincial roads only. Rijkswegen (national highway) day/night speed-limit
            // layers exist too (B2_Maximum_snelheden_op_Rijkswegen[_nacht]) but were left
            // out — national highways don't typically run through a city center like this
            // Enschede demo area. Swap in if the real demo route needs them.
            public const string MaxSnelheden = "B22_wegen:B22_Maximum_snelheden_op_provinciale_wegen";

            // NOTE: "Doorrijhoogte" from the original file does NOT exist as a standalone
            // layer — confirmed absent from live GetCapabilities. MAX_DOORRIJHOOGTE is
            // actually just one attribute field inside the traffic-signal-installations
            // layer below (B22_Verkeersregelinstallaties_in_beheer_bij_de_provincie_Overijssel).
            // Fetch that layer and read the MAX_DOORRIJHOOGTE field per-feature — there is
            // no separate WFS call for clearance heights on their server.
            public const string Verkeersregelinstallaties = "B22_wegen:B22_Verkeersregelinstallaties_in_beheer_bij_de_provincie_Overijssel";

            // Core cycling network (line geometry). Confirmed 2026-08-28: 1464 features
            // within Enschede's municipal boundary.
            public const string KernnetFiets = "B22_wegen:B22_Kernnet_fiets";

            // Public lighting cabinets (point geometry). Confirmed 2026-08-28: 13 features
            // within Enschede's municipal boundary.
            public const string OpenbareVerlichting = "B22_wegen:B22_Openbare_verlichting_kasten";

            // Roundabouts (point geometry). Confirmed 2026-08-28: 3 features in Enschede.
            public const string Rotondes = "B22_wegen:B22_Rotondes_in_provinciale_wegen";

            // Traffic intensity/volume (line geometry). Confirmed 2026-08-28: 13 features
            // in Enschede.
            public const string Verkeersintensiteit = "B22_wegen:B22_Verkeersintensiteit_op_provinciale_wegen";

            // NOTE: "Gladheidsmeldsysteem" (road ice/frost sensor stations) from the
            // original file could NOT be confirmed live as of 2026-08-28. The only hits
            // found were stale (2011/2020) references to the old nationaalgeoregister.nl
            // indexer, not the current portal. It is NOT in this workspace's live
            // GetCapabilities. Left out entirely rather than guessing a URL — if this
            // layer is needed, search https://www.geoportaaloverijssel.nl/browse directly
            // (JS-rendered search, not reachable via simple fetch) and confirm the real
            // typeName/workspace before adding it back here.
        }

        public OverijsselGeoService(HttpClient http)
        {
            _http = http;
        }

        /// <summary>
        /// Fetch a single layer as GML/WFS, bbox-limited to the Enschede reference area.
        /// Mirrors the pattern you already use for PDOK BAG / province Groningen WFS calls —
        /// always bbox-scoped, never a bare province-wide request.
        /// </summary>
        public async Task<XDocument> FetchLayerNearEnschedeAsync(string typeName, int maxFeatures = 500)
        {
            var (minLon, minLat, maxLon, maxLat) = EnschedeReferencePoint.GetBBox();

            var url = $"{BaseUrl}" +
                      $"?service=WFS&version=2.0.0&request=GetFeature" +
                      $"&typeNames={Uri.EscapeDataString(typeName)}" +
                      $"&outputFormat=GML32" +
                      $"&count={maxFeatures}" +
                      // lon,lat,lon,lat,EPSG:4326 order — same axis-order gotcha you hit with PDOK BAG.
                      // If this 400s, swap to lat,lon,lat,lon order per that same fix.
                      $"&bbox={minLon},{minLat},{maxLon},{maxLat},EPSG:4326";

            var response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var xml = await response.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xml);

            // The old code only checked EnsureSuccessStatusCode(), which does NOT catch a
            // WFS server returning 200 OK with an <ows:ExceptionReport> body, or (as
            // actually happened during testing) a redirect landing on an HTML page instead
            // of XML. Callers should check doc.Root?.Name.LocalName for "ExceptionReport"
            // before assuming doc contains real features.
            return doc;
        }

        /// <summary>
        /// Fetch a layer as GeoJSON (not GML), bbox-limited to Enschede. Use this for real
        /// UI wiring — this codebase's existing vector layers (Groningen roads/bridges, BAG
        /// buildings, CROW pavement layers) all use NetTopologySuite.IO.GeoJsonReader against
        /// GeoJSON, with no GML-parsing precedent anywhere. The GML method above stays for
        /// verification/smoke-testing only.
        /// </summary>
        public async Task<string> FetchLayerGeoJsonNearEnschedeAsync(string typeName, int maxFeatures = 500)
        {
            var (minLon, minLat, maxLon, maxLat) = EnschedeReferencePoint.GetBBox();

            var url = $"{BaseUrl}" +
                      $"?service=WFS&version=2.0.0&request=GetFeature" +
                      $"&typeNames={Uri.EscapeDataString(typeName)}" +
                      $"&outputFormat=application/json" +
                      // CONFIRMED REQUIRED 2026-08-28: without srsName, GeoServer returns
                      // coordinates in the layer's native CRS (RD New / EPSG:28992,
                      // six-digit meter values) even though outputFormat is GeoJSON. This
                      // silently breaks ProjectGeometry() (expects WGS84 lon/lat) with no
                      // error -- just badly wrong positions on the map. Verified via curl:
                      // without this param, coords were like [176138.04, 497637.98]; with
                      // it, real lon/lat like [6.89, 52.22].
                      $"&srsName=EPSG:4326" +
                      $"&count={maxFeatures}" +
                      $"&bbox={minLon},{minLat},{maxLon},{maxLat},EPSG:4326";

            var response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        /// <summary>
        /// WMS GetMap request for a layer, bbox-limited — use this for background/visual
        /// rendering (fast, server-drawn) rather than WFS when you don't need click-to-inspect
        /// attributes for that particular layer.
        /// </summary>
        public string BuildWmsTileUrl(string layerName, int width = 512, int height = 512)
        {
            var (minLon, minLat, maxLon, maxLat) = EnschedeReferencePoint.GetBBox();

            // Same B22_wegen workspace, WMS endpoint instead of WFS.
            const string wmsBaseUrl = "https://services.geodataoverijssel.nl/geoserver/B22_wegen/wms";

            return $"{wmsBaseUrl}" +
                   $"?service=WMS&version=1.3.0&request=GetMap" +
                   $"&layers={Uri.EscapeDataString(layerName)}" +
                   $"&bbox={minLon},{minLat},{maxLon},{maxLat}" +
                   $"&crs=EPSG:4326" +
                   $"&width={width}&height={height}" +
                   $"&format=image/png&transparent=true";
        }
    }
}
