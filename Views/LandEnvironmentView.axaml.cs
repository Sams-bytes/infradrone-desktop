using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
namespace InfraDroneDesktop.Views;

public partial class LandEnvironmentView : UserControl
{
    private static readonly HttpClient Http = new HttpClient();
    private const string Base = "https://geoservices.provinciegroningen.nl/server/rest/services/BasisdataGroningen/";

    // Data-driven layer table -- 39 real, confirmed layers (via curl before
    // writing this) across two source folders (Omgevingsvisie, Water), one
    // genuine cross-folder name duplicate (Bergingsgebieden) kept once.
    // (Category, Label, Folder, ServiceType, LayerId, IsPoint, ColorHex)
    private static readonly (string Cat, string Label, string Folder, string SvcType, int Id, bool Point, string Color)[] Layers = new[]
    {
        ("Nature & Ecology", "Natura 2000 areas", "Omgevingsvisie", "MapServer", 15, false, "34,197,94"),
        ("Nature & Ecology", "Forest & nature areas (outside NNN)", "Omgevingsvisie", "MapServer", 3, false, "22,163,74"),
        ("Nature & Ecology", "National Nature Network areas", "Omgevingsvisie", "MapServer", 17, false, "21,128,61"),
        ("Nature & Ecology", "NNN management areas", "Omgevingsvisie", "MapServer", 16, false, "22,101,52"),
        ("Nature & Ecology", "Ecological corridor zones", "Omgevingsvisie", "MapServer", 6, false, "132,204,22"),
        ("Nature & Ecology", "Robust corridor search area", "Omgevingsvisie", "MapServer", 28, false, "101,163,13"),
        ("Nature & Ecology", "Farmland bird habitats", "Omgevingsvisie", "MapServer", 9, false, "163,230,53"),
        ("Nature & Ecology", "Meadow bird habitats", "Omgevingsvisie", "MapServer", 10, false, "190,242,100"),
        ("Nature & Ecology", "Lauwersmeer National Park", "Omgevingsvisie", "MapServer", 13, false, "16,185,129"),
        ("Nature & Ecology", "National parks / protected landscapes", "Omgevingsvisie", "MapServer", 14, false, "5,150,105"),
        ("Nature & Ecology", "Wadden Sea region", "Omgevingsvisie", "MapServer", 23, false, "6,182,212"),

        ("Quiet & Dark Sky", "Quiet areas", "Omgevingsvisie", "MapServer", 21, false, "129,140,248"),
        ("Quiet & Dark Sky", "Quiet & darkness focus areas", "Omgevingsvisie", "MapServer", 0, false, "99,102,241"),

        ("Water", "Water storage areas", "Omgevingsvisie", "MapServer", 2, false, "56,189,248"),
        ("Water", "Primary flood defense", "Water", "FeatureServer", 584, false, "14,165,233"),
        ("Water", "Drinking water extraction area", "Water", "FeatureServer", 3, false, "2,132,199"),
        ("Water", "Groundwater protection zone", "Water", "FeatureServer", 4, false, "3,105,161"),
        ("Water", "Groundwater extraction points", "Water", "FeatureServer", 615, true, "8,145,178"),
        ("Water", "Groundwater measurement points", "Water", "FeatureServer", 614, true, "7,89,133"),
        ("Water", "Future drinking water reserve search area", "Omgevingsvisie", "MapServer", 26, false, "125,211,252"),
        ("Water", "Soil disturbance prohibited areas", "Water", "FeatureServer", 5, false, "12,74,110"),
        ("Water", "Swimming water zones", "Omgevingsvisie", "MapServer", 32, false, "34,211,238"),
        ("Water", "Swimming water locations", "Water", "FeatureServer", 0, true, "34,211,238"),

        ("Ground / Subsidence", "Peat oxidation focus areas", "Omgevingsvisie", "MapServer", 1, false, "217,119,6"),

        ("Development & Energy", "Local & regional industrial parks", "Omgevingsvisie", "MapServer", 11, false, "168,85,247"),
        ("Development & Energy", "Large-scale industrial parks", "Omgevingsvisie", "MapServer", 30, false, "147,51,234"),
        ("Development & Energy", "Large-scale wind energy zones", "Omgevingsvisie", "MapServer", 5, false, "192,132,252"),
        ("Development & Energy", "Hydrogen conversion search areas", "Omgevingsvisie", "MapServer", 27, false, "216,180,254"),

        ("Infrastructure Planning", "Reserved rail route", "Omgevingsvisie", "MapServer", 7, false, "234,88,12"),
        ("Infrastructure Planning", "Future rail search area", "Omgevingsvisie", "MapServer", 29, false, "251,146,60"),
        ("Infrastructure Planning", "N355 corridor", "Omgevingsvisie", "MapServer", 12, false, "253,186,116"),

        ("Named Regional Landscapes", "Central Wold region (Duurswold)", "Omgevingsvisie", "MapServer", 4, false, "148,163,184"),
        ("Named Regional Landscapes", "Gorecht", "Omgevingsvisie", "MapServer", 8, false, "148,163,184"),
        ("Named Regional Landscapes", "Oldambt", "Omgevingsvisie", "MapServer", 18, false, "148,163,184"),
        ("Named Regional Landscapes", "Oostpolder", "Omgevingsvisie", "MapServer", 19, false, "148,163,184"),
        ("Named Regional Landscapes", "Ring West", "Omgevingsvisie", "MapServer", 20, false, "148,163,184"),
        ("Named Regional Landscapes", "Veenkolonien (peat colonies)", "Omgevingsvisie", "MapServer", 22, false, "148,163,184"),
        ("Named Regional Landscapes", "Westerwolde", "Omgevingsvisie", "MapServer", 24, false, "148,163,184"),
        ("Named Regional Landscapes", "Wierdenland Wadden coast", "Omgevingsvisie", "MapServer", 25, false, "148,163,184"),
        ("Named Regional Landscapes", "Zuidelijk Westerkwartier", "Omgevingsvisie", "MapServer", 31, false, "148,163,184"),
    };

    private readonly Dictionary<string, MemoryLayer> _activeLayers = new();
    private Map? _map;

    public LandEnvironmentView()
    {
        InitializeComponent();
        SetupMap();
        BuildCheckboxes();
    }

    private void SetupMap()
    {
        var map = new Map();
        map.Layers.Add(Mapsui.Tiling.OpenStreetMap.CreateTileLayer());
        var groningen = SphericalMercator.FromLonLat(6.5665, 53.2194);
        map.Home = n => n.CenterOnAndZoomTo(new MPoint(groningen.x, groningen.y), 20);
        _map = map;
        MapControl.Map = map;
        _map.Info += OnMapInfo;
    }

    private void BuildCheckboxes()
    {
        string? currentCategory = null;
        foreach (var layer in Layers)
        {
            if (layer.Cat != currentCategory)
            {
                currentCategory = layer.Cat;
                LayerCheckboxPanel.Children.Add(new TextBlock
                {
                    Text = currentCategory.ToUpperInvariant(),
                    FontSize = 10, FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#22c55e")),
                    Margin = new Avalonia.Thickness(0, 10, 0, 2)
                });
            }
            var cb = new CheckBox { Content = layer.Label, FontSize = 12, Foreground = Brushes.White };
            var capturedLayer = layer;
            cb.IsCheckedChanged += async (s, e) => await ToggleLayer(capturedLayer, cb.IsChecked == true);
            LayerCheckboxPanel.Children.Add(cb);
        }
    }

    private async Task ToggleLayer((string Cat, string Label, string Folder, string SvcType, int Id, bool Point, string Color) def, bool isChecked)
    {
        if (_map == null) return;
        if (!isChecked)
        {
            if (_activeLayers.TryGetValue(def.Label, out var existing))
            {
                _map.Layers.Remove(existing);
                _activeLayers.Remove(def.Label);
                MapControl.Refresh();
            }
            return;
        }
        try
        {
            var url = $"{Base}{def.Folder}/{def.SvcType}/{def.Id}/query?where=1%3D1&outFields=*&f=geojson&resultRecordCount=2000";
            var json = await Http.GetStringAsync(url);
            var reader = new GeoJsonReader();
            var fc = reader.Read<FeatureCollection>(json);
            var features = new List<Mapsui.IFeature>();
            foreach (var f in fc)
            {
                if (f.Geometry == null) continue;
                var mf = new GeometryFeature { Geometry = ProjectGeometry(f.Geometry) };
                var (r, g, b) = ParseColor(def.Color);
                if (def.Point)
                {
                    mf.Styles.Add(new SymbolStyle
                    {
                        Fill = new Mapsui.Styles.Brush(new Mapsui.Styles.Color(r, g, b)),
                        Outline = new Mapsui.Styles.Pen(Mapsui.Styles.Color.White, 1.0f),
                        SymbolScale = 0.5
                    });
                }
                else
                {
                    mf.Styles.Add(new VectorStyle
                    {
                        Fill = new Mapsui.Styles.Brush(new Mapsui.Styles.Color(r, g, b, 110)),
                        Outline = new Mapsui.Styles.Pen(new Mapsui.Styles.Color(r, g, b), 1.5f),
                        Line = new Mapsui.Styles.Pen(new Mapsui.Styles.Color(r, g, b), 2.5f)
                    });
                }
                if (f.Attributes != null)
                {
                    foreach (var attrName in f.Attributes.GetNames())
                        mf[attrName] = f.Attributes[attrName];
                }
                features.Add(mf);
            }
            var memLayer = new MemoryLayer { Name = def.Label, Features = features, IsMapInfoLayer = true };
            _activeLayers[def.Label] = memLayer;
            _map.Layers.Add(memLayer);
            var extent = memLayer.Extent;
            if (extent != null) _map.Navigator.ZoomToBox(extent, MBoxFit.Fit);
            MapControl.Refresh();
            Console.WriteLine($"[LandEnv] {def.Label}: loaded {features.Count} from live province server");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LandEnv] {def.Label} failed: {ex.Message}");
        }
    }

    private void OnMapInfo(object? sender, Mapsui.MapInfoEventArgs e)
    {
        var feature = e.MapInfo?.Feature;
        var layer = e.MapInfo?.Layer;
        bool isOurs = false;
        foreach (var l in _activeLayers.Values) if (l == layer) isOurs = true;
        if (feature == null || !isOurs)
        {
            InfoCard.IsVisible = false;
            return;
        }
        var lines = new List<string>();
        foreach (var field in feature.Fields)
        {
            if (field == "SHAPE.STArea()" || field == "SHAPE.STLength()") continue;
            var val = feature[field];
            if (val == null || string.IsNullOrEmpty(val.ToString())) continue;
            lines.Add($"{field}: {val}");
        }
        InfoText.Text = string.Join("\n", lines);
        InfoCard.IsVisible = true;
    }

    private static (byte, byte, byte) ParseColor(string csv)
    {
        var parts = csv.Split(',');
        return (byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]));
    }

    private static NetTopologySuite.Geometries.Geometry ProjectGeometry(NetTopologySuite.Geometries.Geometry geom)
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
        Coordinate[] ProjectRing(Coordinate[] ring)
        {
            var result = new Coordinate[ring.Length];
            for (int i = 0; i < ring.Length; i++)
            {
                var (x, y) = SphericalMercator.FromLonLat(ring[i].X, ring[i].Y);
                result[i] = new Coordinate(x, y);
            }
            return result;
        }
        if (geom is Polygon poly)
        {
            var shell = factory.CreateLinearRing(ProjectRing(poly.ExteriorRing.Coordinates));
            return factory.CreatePolygon(shell);
        }
        if (geom is MultiPolygon mp)
        {
            var polys = new Polygon[mp.NumGeometries];
            for (int i = 0; i < mp.NumGeometries; i++)
            {
                var p = (Polygon)mp.GetGeometryN(i);
                var shell = factory.CreateLinearRing(ProjectRing(p.ExteriorRing.Coordinates));
                polys[i] = factory.CreatePolygon(shell);
            }
            return factory.CreateMultiPolygon(polys);
        }
        if (geom is Point pt)
        {
            var (x, y) = SphericalMercator.FromLonLat(pt.X, pt.Y);
            return factory.CreatePoint(new Coordinate(x, y));
        }
        if (geom is LineString ls)
        {
            return factory.CreateLineString(ProjectRing(ls.Coordinates));
        }
        if (geom is MultiLineString mls)
        {
            var lines = new LineString[mls.NumGeometries];
            for (int i = 0; i < mls.NumGeometries; i++)
            {
                var l = (LineString)mls.GetGeometryN(i);
                lines[i] = factory.CreateLineString(ProjectRing(l.Coordinates));
            }
            return factory.CreateMultiLineString(lines);
        }
        return geom;
    }
}
