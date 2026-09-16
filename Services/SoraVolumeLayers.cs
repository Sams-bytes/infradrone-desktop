// Services/SoraVolumeLayers.cs
//
// Turns a planned waypoint route into the three SORA ground-projection areas
// (Flight Geography, Contingency Volume, Ground Risk Buffer) as NetTopologySuite
// polygons in EPSG:3857, wrapped as Mapsui MemoryLayers, plus a KML exporter in
// the presentation form Annex A §A.5.1 asks for (FG green, CV yellow, GRB red).
//
// Geometry: FG  = route corridor  = route.Buffer(S_FG / 2)   (S_FG ≥ 3·CD, A.5.2.2)
//           CV  = FG.Buffer(S_CV)                          (A.5.2.3)
//           GRB = CV.Buffer(S_GRB)                         (A.5.2.4)
// NetTopologySuite's default buffer uses round caps/joins, which gives the rounded
// ends at waypoints. Buffering is done in Web Mercator; metres are scaled by
// 1/cos(lat) at the route's mean latitude so ground distances are correct
// (at 53° N the factor is ~1.66 — skipping this would under-size every buffer).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using NetTopologySuite.Geometries;

namespace InfraDroneDesktop.Services
{
    public sealed class SoraVolumeGeometry
    {
        public Geometry FlightGeography { get; init; } = default!;   // EPSG:3857
        public Geometry ContingencyVolume { get; init; } = default!; // EPSG:3857
        public Geometry GroundRiskBuffer { get; init; } = default!;  // EPSG:3857
        public SoraVolumeResult Dimensions { get; init; } = default!;
    }

    public static class SoraVolumeLayers
    {
        // Annex A §A.5.1 presentation colours
        private static readonly Mapsui.Styles.Color FgColor  = Mapsui.Styles.Color.FromArgb(255,   0, 170,   0);
        private static readonly Mapsui.Styles.Color CvColor  = Mapsui.Styles.Color.FromArgb(255, 255, 200,   0);
        private static readonly Mapsui.Styles.Color GrbColor = Mapsui.Styles.Color.FromArgb(255, 220,   0,   0);

        /// <param name="waypointsLatLon">Route in WGS84 (lat, lon) order.</param>
        /// <param name="p">SORA parameters; HFG should equal the mission altitude.</param>
        public static SoraVolumeGeometry Build(IReadOnlyList<(double lat, double lon)> waypointsLatLon,
                                               SoraVolumeParameters p)
        {
            if (waypointsLatLon == null || waypointsLatLon.Count == 0)
                throw new ArgumentException("At least one waypoint is required.");

            var dims = SoraVolumeCalculator.Compute(p);

            var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(3857);
            var coords = waypointsLatLon
                .Select(w => { var (x, y) = SphericalMercator.FromLonLat(w.lon, w.lat); return new Coordinate(x, y); })
                .ToArray();

            Geometry route = coords.Length == 1
                ? factory.CreatePoint(coords[0])
                : factory.CreateLineString(coords);

            // Web Mercator scale factor at mean latitude
            double meanLat = waypointsLatLon.Average(w => w.lat);
            double k = 1.0 / Math.Cos(meanLat * Math.PI / 180.0);

            var fg  = route.Buffer(0.5 * dims.SFGmin * k);
            var cv  = fg.Buffer(dims.SCV * k);
            var grb = cv.Buffer(dims.SGRB * k);

            return new SoraVolumeGeometry
            {
                FlightGeography = fg, ContingencyVolume = cv, GroundRiskBuffer = grb, Dimensions = dims
            };
        }

        /// <summary>
        /// Three MemoryLayers, ordered outer→inner so the FG draws on top.
        /// Add them to the map in this order; remove by Name when the route changes.
        /// </summary>
        public static List<MemoryLayer> ToLayers(SoraVolumeGeometry g)
        {
            return new List<MemoryLayer>
            {
                MakeLayer("SORA_GRB", g.GroundRiskBuffer,  GrbColor, 0.25),
                MakeLayer("SORA_CV",  g.ContingencyVolume, CvColor,  0.30),
                MakeLayer("SORA_FG",  g.FlightGeography,   FgColor,  0.35),
            };
        }

        private static MemoryLayer MakeLayer(string name, Geometry geom, Mapsui.Styles.Color color, double opacity)
        {
            return new MemoryLayer
            {
                Name = name,
                Features = new List<Mapsui.IFeature> { new GeometryFeature(geom) },
                Style = new VectorStyle
                {
                    Fill = new Mapsui.Styles.Brush(color),
                    Outline = new Mapsui.Styles.Pen(color, 2)
                },
                Opacity = opacity   // fill transparency must go here, not per-feature brush alpha
            };
        }

        /// <summary>Remove any previously added SORA layers from a map.</summary>
        public static void RemoveFrom(Mapsui.Map map)
        {
            foreach (var l in map.Layers.Where(l => l.Name != null && l.Name.StartsWith("SORA_")).ToList())
                map.Layers.Remove(l);
        }

        /// <summary>
        /// KML in the Annex A §A.5.1 form (three transparent polygons + calculation
        /// inputs/results in the description) for the operations manual Part C.
        /// </summary>
        public static string ToKml(SoraVolumeGeometry g, SoraVolumeParameters p, string areaName)
        {
            var d = g.Dimensions;
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document>");
            sb.AppendLine($"<name>{Esc(areaName)}</name>");
            sb.AppendLine("<description><![CDATA[Calculated per JARUS SORA Annex A 2.5, section A.5.2.<br/>"
                + $"Airframe: {p.Airframe}; V0: {p.V0.ToString(inv)} m/s; CD: {p.CD.ToString(inv)} m; HFG: {p.HFG.ToString(inv)} m<br/>"
                + $"Assumptions: SGNSS {p.SGNSS.ToString(inv)} m, SPos {p.SPos.ToString(inv)} m, SK {p.SK.ToString(inv)} m, tR {p.TR.ToString(inv)} s, "
                + $"altitude {(p.BarometricAltitude ? "barometric" : "GNSS")}<br/>"
                + $"Results: SCV {d.SCV.ToString("F1", inv)} m, HCV {d.HCV.ToString("F1", inv)} m, SGRB {d.SGRB.ToString("F1", inv)} m ({d.Method})]]></description>");
            sb.AppendLine(Style("fg",  "7f00aa00")); // KML colour is aabbggrr
            sb.AppendLine(Style("cv",  "7f00c8ff"));
            sb.AppendLine(Style("grb", "7f0000dc"));
            sb.AppendLine(Placemark("Ground Risk Buffer", "grb", g.GroundRiskBuffer));
            sb.AppendLine(Placemark("Contingency Volume", "cv",  g.ContingencyVolume));
            sb.AppendLine(Placemark("Flight Geography",   "fg",  g.FlightGeography));
            sb.AppendLine("</Document></kml>");
            return sb.ToString();
        }

        private static string Style(string id, string abgr) =>
            $"<Style id=\"{id}\"><LineStyle><color>ff{abgr.Substring(2)}</color><width>2</width></LineStyle>"
          + $"<PolyStyle><color>{abgr}</color></PolyStyle></Style>";

        private static string Placemark(string name, string styleId, Geometry geom)
        {
            var sb = new StringBuilder();
            sb.Append($"<Placemark><name>{Esc(name)}</name><styleUrl>#{styleId}</styleUrl>");
            var polys = geom is MultiPolygon mp ? mp.Geometries.Cast<Polygon>() : new[] { (Polygon)geom };
            sb.Append("<MultiGeometry>");
            foreach (var poly in polys)
            {
                sb.Append("<Polygon><outerBoundaryIs><LinearRing><coordinates>");
                sb.Append(Ring(poly.ExteriorRing));
                sb.Append("</coordinates></LinearRing></outerBoundaryIs>");
                foreach (var hole in poly.InteriorRings)
                    sb.Append("<innerBoundaryIs><LinearRing><coordinates>" + Ring(hole) + "</coordinates></LinearRing></innerBoundaryIs>");
                sb.Append("</Polygon>");
            }
            sb.Append("</MultiGeometry></Placemark>");
            return sb.ToString();
        }

        private static string Ring(LineString ring)
        {
            var inv = CultureInfo.InvariantCulture;
            return string.Join(" ", ring.Coordinates.Select(c =>
            {
                var (lon, lat) = SphericalMercator.ToLonLat(c.X, c.Y);
                return lon.ToString("F7", inv) + "," + lat.ToString("F7", inv) + ",0";
            }));
        }

        private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
