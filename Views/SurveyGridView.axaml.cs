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
using MBrush = Mapsui.Styles.Brush;
using MColor = Mapsui.Styles.Color;
using MPen = Mapsui.Styles.Pen;

namespace InfraDroneDesktop.Views;

public partial class SurveyGridView : UserControl
{
    private Mapsui.UI.Avalonia.MapControl? _mapControl;
    private MemoryLayer? _areaLayer;
    private MemoryLayer? _gridLayer;
    private readonly List<(double Lat, double Lon)> _areaPoints = new();
    private bool _drawing = false;
    private bool _corridorMode = false;
    private readonly List<(double Lat, double Lon)> _corridorPoints = new();
    private SurveyGridResult? _result;
    private CorridorScanResult? _corridorResult;

    public SurveyGridView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

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

        _areaLayer = new MemoryLayer { Name = "Area" };
        _gridLayer = new MemoryLayer { Name = "Grid" };
        map.Layers.Add(_areaLayer);
        map.Layers.Add(_gridLayer);

        var groningen = SphericalMercator.FromLonLat(6.5665, 53.2194);
        map.Home = n => n.CenterOnAndZoomTo(new MPoint(groningen.x, groningen.y), 5);
        _mapControl!.Map = map;
    }

    private void OnDrawArea(object? s, RoutedEventArgs e)
    {
        _drawing = true;
        _areaPoints.Clear();
        BtnGenerate.IsEnabled = false;
        ResultsCard.IsVisible = false;
        BtnSendToMission.IsEnabled = false;
        RefreshMap();
        AreaStatus.Text = "Click map to add area corners. Double-click to close.";
    }

    private void OnMapClick(object? sender, PointerPressedEventArgs e)
    {
        if (!_drawing || _mapControl == null) return;
        var pos = e.GetPosition(_mapControl);
        var vp = _mapControl.Map.Navigator.Viewport;
        var worldX = vp.CenterX + (pos.X - vp.Width / 2) * vp.Resolution;
        var worldY = vp.CenterY - (pos.Y - vp.Height / 2) * vp.Resolution;
        var (lon, lat) = SphericalMercator.ToLonLat(worldX, worldY);

        if (e.ClickCount == 2 && _corridorMode && _corridorPoints.Count >= 2)
        {
            _drawing = false;
            CorridorStatus.Text = $"Centreline: {_corridorPoints.Count} points, {_corridorPoints.Count - 1} segments";
            BtnGenerate.IsEnabled = true;
            return;
        }
        if (e.ClickCount == 2 && _areaPoints.Count >= 3)
        {
            _drawing = false;
            AreaStatus.Text = $"Area defined — {_areaPoints.Count} points";
            BtnGenerate.IsEnabled = true;
            RefreshMap();
            return;
        }

        if (_corridorMode)
        {
            _corridorPoints.Add((lat, lon));
            CorridorStatus.Text = $"{_corridorPoints.Count} points — double-click to finish";
            if (_corridorPoints.Count >= 2) DrawGrid(_corridorPoints);
        }
        else
        {
            _areaPoints.Add((lat, lon));
            AreaStatus.Text = $"{_areaPoints.Count} points — double-click to close";
        }
        RefreshMap();
    }

    private void RefreshMap()
    {
        if (_areaLayer == null || _mapControl == null) return;
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
        var features = new List<IFeature>();

        if (_areaPoints.Count >= 2)
        {
            var coords = _areaPoints.Select(p =>
            {
                var (x, y) = SphericalMercator.FromLonLat(p.Lon, p.Lat);
                return new NetTopologySuite.Geometries.Coordinate(x, y);
            }).ToList();

            if (!_drawing && _areaPoints.Count >= 3)
            {
                coords.Add(coords[0]);
                var ring = factory.CreateLinearRing(coords.ToArray());
                var poly = factory.CreatePolygon(ring);
                var pf = new GeometryFeature { Geometry = poly };
                pf.Styles.Add(new VectorStyle
                {
                    Fill = new MBrush(new MColor(13, 158, 117, 30)),
                    Outline = new MPen(new MColor(13, 158, 117, 220), 2)
                });
                features.Add(pf);
            }
            else
            {
                var line = factory.CreateLineString(coords.ToArray());
                var lf = new GeometryFeature { Geometry = line };
                lf.Styles.Add(new VectorStyle { Fill = null, Line = new MPen(new MColor(13, 158, 117), 2) });
                features.Add(lf);
            }
        }
        _areaLayer.Features = features;
        _mapControl.Map.Refresh();
    }

    private void DrawGrid(List<(double Lat, double Lon)> waypoints)
    {
        if (_gridLayer == null || _mapControl == null) return;
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
        var coords = waypoints.Select(p =>
        {
            var (x, y) = SphericalMercator.FromLonLat(p.Lon, p.Lat);
            return new NetTopologySuite.Geometries.Coordinate(x, y);
        }).ToArray();
        var line = factory.CreateLineString(coords);
        var lf = new GeometryFeature { Geometry = line };
        lf.Styles.Add(new VectorStyle { Fill = null, Line = new MPen(new MColor(245, 158, 11, 220), 2) });
        _gridLayer.Features = new List<IFeature> { lf };
        _mapControl.Map.Refresh();
    }

    private CameraProfile GetSelectedCamera()
    {
        return CameraSelect.SelectedIndex switch
        {
            1 => CameraProfile.ParrotSequoia,
            2 => CameraProfile.Generic20MP,
            _ => CameraProfile.DjiMavic3E
        };
    }

    private void OnModeArea(object? s, RoutedEventArgs e)
    {
        _corridorMode = false;
        AreaModePanel.IsVisible = true;
        CorridorModePanel.IsVisible = false;
        BtnModeArea.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0d3d2e"));
        BtnModeArea.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0d9e75"));
        BtnModeCorridor.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1a2637"));
        BtnModeCorridor.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#94a3b8"));
    }

    private void OnModeCorridor(object? s, RoutedEventArgs e)
    {
        _corridorMode = true;
        AreaModePanel.IsVisible = false;
        CorridorModePanel.IsVisible = true;
        BtnModeCorridor.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0d3d2e"));
        BtnModeCorridor.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0d9e75"));
        BtnModeArea.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1a2637"));
        BtnModeArea.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#94a3b8"));
    }

    private void OnDrawCorridor(object? s, RoutedEventArgs e)
    {
        _drawing = true;
        _corridorPoints.Clear();
        CorridorStatus.Text = "Click to place centreline points — double-click to finish";
    }

    private void OnGenerate(object? s, RoutedEventArgs e)
    {
        var camera = GetSelectedCamera();
        var altitude = (double)(AltitudeInput.Value ?? 60);
        var frontOverlap = (double)(FrontOverlapInput.Value ?? 80);
        var sideOverlap = (double)(SideOverlapInput.Value ?? 70);
        var speed = (double)(SpeedInput.Value ?? 8);

        if (_corridorMode)
        {
            if (_corridorPoints.Count < 2) { CorridorStatus.Text = "Draw a centreline first."; return; }
            double.TryParse(CorridorWidth.Text, out var corridorW);
            if (corridorW <= 0) corridorW = 100;
            _corridorResult = SurveyGridService.GenerateCorridor(
                _corridorPoints, corridorW, altitude, camera, frontOverlap, sideOverlap, speed);
            GsdText.Text = $"{_corridorResult.GsdCm:F1} cm/px";
            FootprintText.Text = $"{_corridorResult.NumSwaths} swath(s)";
            LineSpacingText.Text = $"{_corridorResult.LineSpacingM:F0} m";
            PhotosText.Text = $"~{_corridorResult.EstimatedPhotos} photos";
            FlightTimeText.Text = $"~{_corridorResult.EstimatedFlightTimeMin:F1} min";
            AreaText.Text = $"{_corridorResult.CorridorLengthKm:F2} km corridor";
            ResultsCard.IsVisible = true;
            BtnSendToMission.IsEnabled = true;
            DrawGrid(_corridorResult.Waypoints);
        }
        else
        {
            if (_areaPoints.Count < 3) return;
            _result = SurveyGridService.GenerateGrid(_areaPoints, altitude, camera, frontOverlap, sideOverlap, speed);
            GsdText.Text = $"{_result.GsdCm:F1} cm/px";
            FootprintText.Text = $"{_result.FootprintWidthM:F0} x {_result.FootprintHeightM:F0} m";
            LineSpacingText.Text = $"{_result.LineSpacingM:F0} m";
            PhotosText.Text = $"~{_result.EstimatedPhotos} photos";
            FlightTimeText.Text = $"~{_result.EstimatedFlightTimeMin:F1} min";
            AreaText.Text = $"{_result.AreaHa:F2} ha";
            ResultsCard.IsVisible = true;
            BtnSendToMission.IsEnabled = true;
            DrawGrid(_result.Waypoints);
        }
    }

    public List<(double Lat, double Lon, double AltM)>? GetGeneratedWaypoints()
    {
        var alt = (double)(AltitudeInput.Value ?? 60);
        if (_corridorMode && _corridorResult != null)
            return _corridorResult.Waypoints.Select(p => (p.Lat, p.Lon, alt)).ToList();
        if (_result == null) return null;
        return _result.Waypoints.Select(p => (p.Lat, p.Lon, alt)).ToList();
    }

    public event Action? SendToMissionRequested;
    private void OnSendToMission(object? s, RoutedEventArgs e) => SendToMissionRequested?.Invoke();
}
