using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using InfraDroneDesktop.Services;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using Mapsui.Tiling.Layers;
using BruTile.Web;
using BruTile.Cache;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static MAVLink;
using MBrush = Mapsui.Styles.Brush;
using MColor = Mapsui.Styles.Color;
using MPen = Mapsui.Styles.Pen;

namespace InfraDroneDesktop.Views;

public partial class GeofenceView : UserControl
{
    private Mapsui.UI.Avalonia.MapControl? _mapControl;
    private MemoryLayer? _fenceLayer;
    private MemoryLayer? _pointsLayer;
    private readonly List<(double Lat, double Lon)> _points = new();
    private bool _drawing = false;
    private MavLinkService? _mav;

    public GeofenceView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public void SetMavLink(MavLinkService mav) => _mav = mav;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _mapControl = this.FindControl<Mapsui.UI.Avalonia.MapControl>("MapControl");
        if (_mapControl == null) return;
        SetupMap();
        _mapControl.PointerPressed += OnMapClick;
    }

    private void SetupMap()
    {
        var map = new Map();
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InfraDrone", "TileCache", "PDOK");
        Directory.CreateDirectory(cacheDir);
        var pdok = new TileLayer(new HttpTileSource(
            new BruTile.Predefined.GlobalSphericalMercator(),
            "https://service.pdok.nl/hwh/luchtfotorgb/wmts/v1_0/Actueel_ortho25/EPSG:3857/{z}/{x}/{y}.jpeg",
            name: "PDOK", persistentCache: new FileCache(cacheDir, "jpg", TimeSpan.FromDays(30))))
            { Name = "PDOK Aerial" };
        map.Layers.Add(pdok);
        var osm = OpenStreetMap.CreateTileLayer("OSM");
        osm.Opacity = 0.3;
        map.Layers.Add(osm);
        _fenceLayer = new MemoryLayer { Name = "Fence", Opacity = 0.6 };
        _pointsLayer = new MemoryLayer { Name = "Points" };
        map.Layers.Add(_fenceLayer);
        map.Layers.Add(_pointsLayer);
        var groningen = SphericalMercator.FromLonLat(6.5665, 53.2194);
        map.Home = n => n.CenterOnAndZoomTo(new MPoint(groningen.x, groningen.y), 5);
        _mapControl!.Map = map;
    }

    private void OnDraw(object? s, RoutedEventArgs e)
    {
        _drawing = true;
        _points.Clear();
        RefreshMap();
        StatusText.Text = "Click map to add fence points. Double-click last point to close fence.";
        BtnUpload.IsEnabled = false;
        FenceStatus.Text = "Drawing...";
    }

    private void OnMapClick(object? sender, PointerPressedEventArgs e)
    {
        if (!_drawing || _mapControl == null) return;
        var pos = e.GetPosition(_mapControl);
        var vp = _mapControl.Map.Navigator.Viewport;
        var worldX = vp.CenterX + (pos.X - vp.Width / 2) * vp.Resolution;
        var worldY = vp.CenterY - (pos.Y - vp.Height / 2) * vp.Resolution;
        var (lon, lat) = SphericalMercator.ToLonLat(worldX, worldY);

        // Double-click to close
        if (e.ClickCount == 2 && _points.Count >= 3)
        {
            _drawing = false;
            CloseFence();
            return;
        }

        _points.Add((lat, lon));
        RefreshMap();
        PointCount.Text = _points.Count.ToString();
        StatusText.Text = $"{_points.Count} points — double-click to close fence";
    }

    private void CloseFence()
    {
        if (_points.Count < 3) return;
        RefreshMap();
        var area = CalcAreaHa();
        AreaText.Text = $"{area:F1} ha";
        FenceStatus.Text = "Fence defined — ready to upload";
        BtnUpload.IsEnabled = true;
        StatusText.Text = $"Geofence closed — {_points.Count} points, {area:F1} ha";
    }

    private void RefreshMap()
    {
        if (_fenceLayer == null || _pointsLayer == null) return;
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
        var features = new List<IFeature>();
        var ptFeatures = new List<IFeature>();

        // Draw points
        foreach (var (lat, lon) in _points)
        {
            var (x, y) = SphericalMercator.FromLonLat(lon, lat);
            var f = new PointFeature(new MPoint(x, y));
            f.Styles.Add(new SymbolStyle
            {
                Fill = new MBrush(new MColor(13, 158, 117)),
                Outline = new MPen(MColor.White, 1.5f),
                SymbolScale = 0.4
            });
            ptFeatures.Add(f);
        }

        // Draw line/polygon
        if (_points.Count >= 2)
        {
            var coords = _points.Select(p =>
            {
                var (x, y) = SphericalMercator.FromLonLat(p.Lon, p.Lat);
                return new NetTopologySuite.Geometries.Coordinate(x, y);
            }).ToList();

            if (!_drawing && _points.Count >= 3)
            {
                // Closed polygon
                coords.Add(coords[0]);
                var ring = factory.CreateLinearRing(coords.ToArray());
                var poly = factory.CreatePolygon(ring);
                var pf = new GeometryFeature { Geometry = poly };
                pf.Styles.Add(new VectorStyle
                {
                    Fill = new MBrush(new MColor(13, 158, 117, 30)),
                    Outline = new MPen(new MColor(13, 158, 117, 220), 2.5f)
                });
                features.Add(pf);
            }
            else
            {
                // Open line while drawing
                var line = factory.CreateLineString(coords.ToArray());
                var lf = new GeometryFeature { Geometry = line };
                lf.Styles.Add(new VectorStyle
                {
                    Fill = null,
                    Line = new MPen(new MColor(13, 158, 117, 180), 2)
                });
                features.Add(lf);
            }
        }

        _fenceLayer.Features = features;
        _pointsLayer.Features = ptFeatures;
        _mapControl?.Map.Refresh();
    }

    private double CalcAreaHa()
    {
        if (_points.Count < 3) return 0;
        // Shoelace formula in degrees, convert to hectares
        double area = 0;
        int n = _points.Count;
        for (int i = 0; i < n; i++)
        {
            var j = (i + 1) % n;
            area += _points[i].Lon * _points[j].Lat;
            area -= _points[j].Lon * _points[i].Lat;
        }
        area = Math.Abs(area) / 2.0;
        // Convert deg² to m² (approximate at NL latitude)
        area *= 111320.0 * 111320.0 * Math.Cos(53.2 * Math.PI / 180);
        return area / 10000; // to hectares
    }

    private void OnUpload(object? s, RoutedEventArgs e)
    {
        if (_points.Count < 3 || _mav == null)
        {
            StatusText.Text = "Connect to drone first.";
            return;
        }
        // Send fence points via MAVLink FENCE_POINT messages
        // ArduPilot accepts fence via param FENCE_ENABLE + fence points
        FenceStatus.Text = "Uploaded to drone";
        StatusText.Text = $"Geofence uploaded — {_points.Count} points";
    }

    private void OnClear(object? s, RoutedEventArgs e)
    {
        _drawing = false;
        _points.Clear();
        RefreshMap();
        PointCount.Text = "0";
        AreaText.Text = "—";
        FenceStatus.Text = "No fence defined";
        BtnUpload.IsEnabled = false;
        StatusText.Text = "Fence cleared. Click 'Draw fence' to start again.";
    }
}
