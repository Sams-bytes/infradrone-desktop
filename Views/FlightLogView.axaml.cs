using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
using System.Threading.Tasks;
using System.Timers;
using static MAVLink;
using MBrush = Mapsui.Styles.Brush;
using MColor = Mapsui.Styles.Color;
using MPen = Mapsui.Styles.Pen;

namespace InfraDroneDesktop.Views;

public partial class FlightLogView : UserControl
{
    private Mapsui.UI.Avalonia.MapControl? _mapControl;
    private MemoryLayer? _trackLayer;
    private MemoryLayer? _posLayer;
    private FlightLogSession? _session;
    private int _playIndex = 0;
    private Timer? _timer;
    private bool _playing = false;

    public FlightLogView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _mapControl = this.FindControl<Mapsui.UI.Avalonia.MapControl>("MapControl");
        if (_mapControl == null) return;
        SetupMap();
        Timeline.PropertyChanged += (s, e) =>
        {
            if (e.Property.Name == "Value" && !_playing && _session != null)
                SeekTo((int)Timeline.Value);
        };
    }

    private void SetupMap()
    {
        var map = new Map();
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
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
        _trackLayer = new MemoryLayer { Name = "Track" };
        _posLayer = new MemoryLayer { Name = "Position" };
        map.Layers.Add(_trackLayer);
        map.Layers.Add(_posLayer);
        var groningen = SphericalMercator.FromLonLat(6.5665, 53.2194);
        map.Home = n => n.CenterOnAndZoomTo(new MPoint(groningen.x, groningen.y), 5);
        _mapControl!.Map = map;
        // Auto-load last CSV if exists
        var defaultCsv = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "infradrone-desktop", "mav_track.csv");
        if (System.IO.File.Exists(defaultCsv))
            Dispatcher.UIThread.Post(async () => { await Task.Delay(500); await LoadCsvAsync(defaultCsv); });
    }

    private async Task LoadCsvAsync(string path)
    {
        StatusText.Text = "Loading default log...";
        var svc = new FlightLogService();
        _session = await svc.ParseCsvAsync(path);
        if (_session == null || _session.Points.Count == 0)
        { StatusText.Text = "No GPS data found."; return; }
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = $"{System.IO.Path.GetFileName(path)} — {_session.Points.Count} points";
            MaxAlt.Text = $"{_session.MaxAlt:F1}m";
            MaxSpeed.Text = $"{_session.MaxSpeed:F1}m/s";
            TotalDist.Text = $"{_session.TotalDistKm:F2}km";
            Duration.Text = _session.Duration.ToString(@"hh\:mm\:ss");
            Timeline.Maximum = _session.Points.Count - 1;
            Timeline.Value = 0;
            Timeline.IsEnabled = true;
            BtnPlay.IsEnabled = true;
            BtnPause.IsEnabled = true;
            BtnReset.IsEnabled = true;
            DrawFullTrack();
            SeekTo(0);
            CenterOnTrack();
        });
    }

    private async void OnOpen(object? s, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open flight log",
            FileTypeFilter = new[] { new FilePickerFileType("Flight Log") { Patterns = new[] { "*.tlog", "*.csv" } } }
        });
        if (files.Count == 0) return;
        var path = files[0].Path.LocalPath;
        StatusText.Text = "Parsing log...";
        var svc = new FlightLogService();
        _session = path.EndsWith(".csv")
            ? await svc.ParseCsvAsync(path)
            : await svc.ParseTlogAsync(path);
        if (_session == null || _session.Points.Count == 0)
        {
            StatusText.Text = "No GPS data found in log.";
            return;
        }
        StatusText.Text = $"{Path.GetFileName(path)} — {_session.Points.Count} points";
        MaxAlt.Text = $"{_session.MaxAlt:F1}m";
        MaxSpeed.Text = $"{_session.MaxSpeed:F1}m/s";
        TotalDist.Text = $"{_session.TotalDistKm:F2}km";
        Duration.Text = $"{_session.Duration:hh\\:mm\\:ss}";
        Timeline.Maximum = _session.Points.Count - 1;
        Timeline.Value = 0;
        Timeline.IsEnabled = true;
        BtnPlay.IsEnabled = true;
        BtnPause.IsEnabled = true;
        BtnReset.IsEnabled = true;
        DrawFullTrack();
        SeekTo(0);
        CenterOnTrack();
    }

    private void DrawFullTrack()
    {
        if (_session == null || _trackLayer == null) return;
        var coords = _session.Points
            .Where(p => p.Lat != 0 && p.Lon != 0)
            .Select(p => { var (x,y) = SphericalMercator.FromLonLat(p.Lon, p.Lat);
                           return new NetTopologySuite.Geometries.Coordinate(x, y); })
            .ToArray();
        if (coords.Length < 2) return;
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
        var line = factory.CreateLineString(coords);
        var f = new GeometryFeature { Geometry = line };
        f.Styles.Add(new VectorStyle { Fill = null, Line = new MPen(new MColor(55, 138, 221, 160), 2) });
        _trackLayer.Features = new List<IFeature> { f };
        _mapControl?.Map.Refresh();
    }

    private void SeekTo(int index)
    {
        if (_session == null || _posLayer == null) return;
        index = Math.Clamp(index, 0, _session.Points.Count - 1);
        _playIndex = index;
        var pt = _session.Points[index];
        if (pt.Lat == 0 && pt.Lon == 0) return;
        var (x, y) = SphericalMercator.FromLonLat(pt.Lon, pt.Lat);
        var f = new PointFeature(new MPoint(x, y));
        f.Styles.Add(new SymbolStyle
        {
            Fill = new MBrush(new MColor(13, 158, 117)),
            Outline = new MPen(MColor.White, 2),
            SymbolScale = 0.7
        });
        _posLayer.Features = new List<IFeature> { f };
        _mapControl?.Map.Refresh();
        var elapsed = pt.Time - _session.Start;
        TimeText.Text = $"{elapsed:mm\\:ss} / {_session.Duration:mm\\:ss}";
        PosText.Text = $"{pt.Lat:F5}°N {pt.Lon:F5}°E  Alt:{pt.AltRel:F1}m  {pt.Mode}";
        Dispatcher.UIThread.Post(() => Timeline.Value = index);
    }

    private void CenterOnTrack()
    {
        if (_session == null || _mapControl == null || _session.Points.Count == 0) return;
        var validPts = _session.Points.Where(p => p.Lat != 0).ToList();
        if (validPts.Count == 0) return;
        var midLat = validPts.Average(p => p.Lat);
        var midLon = validPts.Average(p => p.Lon);
        var (x, y) = SphericalMercator.FromLonLat(midLon, midLat);
        _mapControl.Map.Navigator.CenterOn(new MPoint(x, y));
    }

    private void OnPlay(object? s, RoutedEventArgs e)
    {
        if (_session == null) return;
        _playing = true;
        _timer = new Timer(100);
        _timer.Elapsed += (_, _) =>
        {
            if (!_playing) { _timer?.Stop(); return; }
            _playIndex++;
            if (_playIndex >= _session.Points.Count) { _playing = false; _timer?.Stop(); return; }
            Dispatcher.UIThread.Post(() => SeekTo(_playIndex));
        };
        _timer.Start();
    }

    private void OnPause(object? s, RoutedEventArgs e) { _playing = false; _timer?.Stop(); }

    private void OnReset(object? s, RoutedEventArgs e)
    {
        _playing = false;
        _timer?.Stop();
        SeekTo(0);
    }
}
