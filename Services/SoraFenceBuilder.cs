// Services/SoraFenceBuilder.cs
//
// Turns the SORA ground risk buffer into a polygon an autopilot will accept.
//
// Two hard constraints drive everything here:
//   1. ArduPilot polygon fences are limited to 70 points (all vehicle types).
//      A NetTopologySuite Buffer() with round joins easily produces several
//      hundred, so simplification is mandatory, not cosmetic.
//   2. Simplification MUST NOT enlarge the polygon. The ground risk buffer is
//      the area the operation was approved for; a fence even slightly outside
//      it would let the aircraft legally-but-wrongly leave the approved volume.
//      So every step here shrinks or holds, never grows, and the result is
//      verified with Covers() before it is returned.
//
// Nothing in this file talks to a vehicle. It is pure geometry so it can be
// unit-tested and eyeballed on the map with no hardware connected.

using System;
using System.Collections.Generic;
using System.Linq;
using Mapsui.Projections;
using NetTopologySuite.Geometries;
using NetTopologySuite.Simplify;

namespace InfraDroneDesktop.Services
{
    public sealed class FenceBuildResult
    {
        /// <summary>Fence vertices in WGS84, in order, first point NOT repeated at the end.</summary>
        public IReadOnlyList<(double lat, double lon)> Points { get; init; } = Array.Empty<(double, double)>();
        /// <summary>The simplified polygon in EPSG:3857, for drawing on the map.</summary>
        public Geometry Polygon { get; init; } = default!;
        /// <summary>Metres the fence was pulled inside the ground risk buffer.</summary>
        public double InsetMetres { get; init; }
        /// <summary>Simplification tolerance actually used, in metres.</summary>
        public double ToleranceMetres { get; init; }
        /// <summary>Area given up versus the original buffer, as a percentage.</summary>
        public double AreaLossPercent { get; init; }
        public string Summary { get; init; } = "";
    }

    public static class SoraFenceBuilder
    {
        /// <summary>ArduPilot's documented limit for polygon fences, all vehicle types.</summary>
        public const int MaxFencePoints = 70;

        /// <summary>
        /// Builds a fence polygon from the ground risk buffer.
        /// </summary>
        /// <param name="groundRiskBuffer">GRB geometry in EPSG:3857, from SoraVolumeLayers.Build().</param>
        /// <param name="meanLatitude">Mean latitude of the route, for the Mercator scale correction.</param>
        /// <param name="safetyInsetMetres">
        /// Extra margin pulled inside the GRB before simplifying. Simplification can
        /// cut corners outward on concave shapes; insetting first guarantees headroom.
        /// </param>
        /// <param name="maxPoints">Vertex ceiling. Defaults to ArduPilot's 70.</param>
        public static FenceBuildResult Build(Geometry groundRiskBuffer,
                                             double meanLatitude,
                                             double safetyInsetMetres = 5.0,
                                             int maxPoints = MaxFencePoints)
        {
            if (groundRiskBuffer == null || groundRiskBuffer.IsEmpty)
                throw new ArgumentException("Ground risk buffer is empty — plan a route first.");
            if (maxPoints < 4)
                throw new ArgumentException("A fence needs at least 4 points.");

            // Web Mercator metres are inflated by 1/cos(lat); undo that so the
            // inset and tolerance below are real ground metres.
            double k = 1.0 / Math.Cos(meanLatitude * Math.PI / 180.0);

            // A MultiPolygon means the route produced disjoint areas. ArduPilot
            // takes a single inclusion polygon, so use the largest and say so.
            Polygon source;
            var wasMulti = false;
            if (groundRiskBuffer is MultiPolygon mp && mp.NumGeometries > 1)
            {
                wasMulti = true;
                source = mp.Geometries.OfType<Polygon>().OrderByDescending(p => p.Area).First();
            }
            else
            {
                source = groundRiskBuffer is MultiPolygon m1
                    ? (Polygon)m1.GetGeometryN(0)
                    : (Polygon)groundRiskBuffer;
            }

            double originalArea = source.Area;

            // Step 1: pull inward by the safety inset (negative buffer).
            var inset = source.Buffer(-safetyInsetMetres * k);
            if (inset.IsEmpty)
                throw new InvalidOperationException(
                    $"Ground risk buffer is too small to inset by {safetyInsetMetres} m. " +
                    "Lower the inset or check the SORA parameters.");
            if (inset is MultiPolygon im && im.NumGeometries > 1)
                inset = im.Geometries.OfType<Polygon>().OrderByDescending(p => p.Area).First();

            // Step 2: raise the tolerance until the ring fits under the point cap.
            // Douglas-Peucker on a convex-ish ring only removes vertices, so the
            // result stays within the input; the Covers() check below proves it.
            double tolerance = 1.0;
            Geometry simplified = inset;
            var ring = ExteriorRing(simplified);
            while (ring.Length - 1 > maxPoints && tolerance < 2000.0)
            {
                simplified = DouglasPeuckerSimplifier.Simplify(inset, tolerance * k);
                if (simplified.IsEmpty) break;
                if (simplified is MultiPolygon sm && sm.NumGeometries > 1)
                    simplified = sm.Geometries.OfType<Polygon>().OrderByDescending(p => p.Area).First();
                ring = ExteriorRing(simplified);
                tolerance *= 1.35;
            }

            if (simplified.IsEmpty || ring.Length - 1 > maxPoints)
                throw new InvalidOperationException(
                    $"Could not reduce the fence below {maxPoints} points. The route may be too " +
                    "complex for a single inclusion fence — consider splitting the mission.");

            // Step 3: prove the fence is inside the approved area. This is the
            // check that matters; if it ever fails, do not upload.
            if (!source.Covers(simplified))
                throw new InvalidOperationException(
                    "Simplified fence is not fully inside the ground risk buffer. " +
                    "Refusing to build it — raise the safety inset and retry.");

            var pts = new List<(double lat, double lon)>();
            // Drop the duplicated closing vertex: the fence protocol closes implicitly.
            for (var i = 0; i < ring.Length - 1; i++)
            {
                var (lon, lat) = SphericalMercator.ToLonLat(ring[i].X, ring[i].Y);
                pts.Add((lat, lon));
            }

            double lossPct = originalArea > 0 ? (1.0 - simplified.Area / originalArea) * 100.0 : 0.0;

            var summary = $"{pts.Count} points, inset {safetyInsetMetres:F0} m, " +
                          $"tolerance {tolerance / 1.35:F0} m, {lossPct:F1}% area given up";
            if (wasMulti) summary += " (route produced separate areas — largest used)";

            return new FenceBuildResult
            {
                Points = pts,
                Polygon = simplified,
                InsetMetres = safetyInsetMetres,
                ToleranceMetres = tolerance / 1.35,
                AreaLossPercent = lossPct,
                Summary = summary
            };
        }

        private static Coordinate[] ExteriorRing(Geometry g)
        {
            if (g is Polygon p) return p.ExteriorRing.Coordinates;
            if (g is MultiPolygon m && m.NumGeometries > 0)
                return ((Polygon)m.GetGeometryN(0)).ExteriorRing.Coordinates;
            return Array.Empty<Coordinate>();
        }

        /// <summary>
        /// Centroid of the fence, usable as the legacy return point (index 0).
        /// Guaranteed to be inside the polygon, unlike a plain centroid on a
        /// concave shape.
        /// </summary>
        public static (double lat, double lon) InteriorPoint(Geometry polygon3857)
        {
            var pt = polygon3857.InteriorPoint;
            var (lon, lat) = SphericalMercator.ToLonLat(pt.X, pt.Y);
            return (lat, lon);
        }
    }
}
