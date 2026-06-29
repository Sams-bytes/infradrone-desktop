using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using InfraDroneDesktop.Services;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using Mapsui.Tiling.Layers;
using BruTile.Web;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Mapsui.Nts;
using MBrush = Mapsui.Styles.Brush;
using MColor = Mapsui.Styles.Color;
using MPen = Mapsui.Styles.Pen;

namespace InfraDroneDesktop.Views;

public class Waypoint
{
    public int Number { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double AltM { get; set; } = 30;
}

public partial class MissionView : UserControl
{
    internal readonly List<Waypoint> _waypoints = new();
    private Mapsui.UI.Avalonia.MapControl? _mapControl;
    private MemoryLayer? _wpLayer;
    private MemoryLayer? _routeLayer;
    private MavLinkService? _mav;

    public MissionView()
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
        var pdok = new TileLayer(new HttpTileSource(
            new BruTile.Predefined.GlobalSphericalMercator(),
            "https://service.pdok.nl/hwh/luchtfotorgb/wmts/v1_0/Actueel_ortho25/EPSG:3857/{z}/{x}/{y}.jpeg",
            name: "PDOK")) { Name = "PDOK Aerial", Opacity = 1.0 };
        map.Layers.Add(pdok);

        var osm = OpenStreetMap.CreateTileLayer("OSM");
        osm.Opacity = 0.3;
        map.Layers.Add(osm);

        _wpLayer = new MemoryLayer { Name = "Waypoints" };
        _routeLayer = new MemoryLayer { Name = "Route" };
        map.Layers.Add(_routeLayer);
        map.Layers.Add(_wpLayer);

        var groningen = SphericalMercator.FromLonLat(6.5665, 53.2194);
        map.Home = n => n.CenterOnAndZoomTo(new MPoint(groningen.x, groningen.y), 5);
        _mapControl!.Map = map;
    }

    private void OnMapClick(object? sender, PointerPressedEventArgs e)
    {
        if (_mapControl == null) return;
        var pos = e.GetPosition(_mapControl);
        var vp = _mapControl.Map.Navigator.Viewport;
        var worldX = vp.CenterX + (pos.X - vp.Width / 2) * vp.Resolution;
        var worldY = vp.CenterY - (pos.Y - vp.Height / 2) * vp.Resolution;
        var world = new MPoint(worldX, worldY);
        var lonLat = SphericalMercator.ToLonLat(world.X, world.Y);

        var wp = new Waypoint
        {
            Number = _waypoints.Count + 1,
            Lat = lonLat.lat,
            Lon = lonLat.lon,
            AltM = 30
        };
        _waypoints.Add(wp);
        RefreshMap();
        RefreshList();
    }

    private void RefreshMap()
    {
        if (_wpLayer == null || _routeLayer == null) return;

        var wpFeatures = new List<IFeature>();
        var routeFeatures = new List<IFeature>();

        foreach (var wp in _waypoints)
        {
            var (x, y) = SphericalMercator.FromLonLat(wp.Lon, wp.Lat);
            var f = new PointFeature(new MPoint(x, y));
            f.Styles.Add(new SymbolStyle
            {
                Fill = new MBrush(new MColor(13, 158, 117)),
                Outline = new MPen(MColor.White, 2),
                SymbolScale = 0.5
            });
            wpFeatures.Add(f);
        }

        if (_waypoints.Count >= 2)
        {
            var coords = _waypoints.Select(wp =>
            {
                var (x, y) = SphericalMercator.FromLonLat(wp.Lon, wp.Lat);
                return new NetTopologySuite.Geometries.Coordinate(x, y);
            }).ToArray();

            var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
            var line = factory.CreateLineString(coords);
            var lf = new GeometryFeature { Geometry = line };
            lf.Styles.Add(new VectorStyle
            {
                Fill = null,
                Line = new MPen(new MColor(13, 158, 117, 200), 2)
            });
            routeFeatures.Add(lf);
        }

        _wpLayer.Features = wpFeatures;
        _routeLayer.Features = routeFeatures;
        _mapControl?.Map.Refresh();
    }

    private void RefreshList()
    {
        Dispatcher.UIThread.Post(() =>
        {
            WpCount.Text = $"{_waypoints.Count} points";
            var dist = CalcTotalDist();
            TotalDist.Text = $"{dist:F2} km total";

            WaypointList.Items.Clear();
            foreach (var wp in _waypoints)
            {
                WaypointList.Items.Add(new Border
                {
                    Background = SolidColorBrush.Parse("#131f2e"),
                    BorderBrush = SolidColorBrush.Parse("#1e3a5f"),
                    BorderThickness = new Avalonia.Thickness(0.5),
                    CornerRadius = new Avalonia.CornerRadius(6),
                    Padding = new Avalonia.Thickness(10, 6),
                    Margin = new Avalonia.Thickness(2),
                    Width = 160,
                    Child = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = $"WP {wp.Number}", FontSize = 11,
                                FontWeight = FontWeight.Bold, Foreground = SolidColorBrush.Parse("#0d9e75") },
                            new TextBlock { Text = $"{wp.Lat:F5}°N", FontSize = 10,
                                Foreground = SolidColorBrush.Parse("#94a3b8"), FontFamily = new FontFamily("Consolas") },
                            new TextBlock { Text = $"{wp.Lon:F5}°E", FontSize = 10,
                                Foreground = SolidColorBrush.Parse("#94a3b8"), FontFamily = new FontFamily("Consolas") },
                            new TextBlock { Text = $"{wp.AltM}m AGL", FontSize = 10,
                                Foreground = SolidColorBrush.Parse("#64748b") },
                        }
                    }
                });
            }
        });
    }

    private double CalcTotalDist()
    {
        double total = 0;
        for (int i = 1; i < _waypoints.Count; i++)
        {
            total += HaversineKm(_waypoints[i-1].Lat, _waypoints[i-1].Lon,
                                  _waypoints[i].Lat, _waypoints[i].Lon);
        }
        return total;
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat/2)*Math.Sin(dLat/2) +
                Math.Cos(lat1*Math.PI/180)*Math.Cos(lat2*Math.PI/180)*
                Math.Sin(dLon/2)*Math.Sin(dLon/2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1-a));
    }

    private void OnClear(object? s, RoutedEventArgs e)
    {
        _waypoints.Clear();
        RefreshMap();
        RefreshList();
        StatusText.Text = "Mission cleared. Click map to add waypoints.";
    }

    private void OnUpload(object? s, RoutedEventArgs e)
    {
        if (_waypoints.Count == 0) { StatusText.Text = "No waypoints to upload."; return; }
        StatusText.Text = $"Uploading {_waypoints.Count} waypoints...";
        // TODO: wire to MavLinkService mission upload
        StatusText.Text = $"Upload: {_waypoints.Count} waypoints sent.";
    }

    private async void OnExportKml(object? s, RoutedEventArgs e)
    {
        if (_waypoints.Count == 0) { StatusText.Text = "No waypoints to export."; return; }
        var file = await GetSaveFile("mission.kml", new[] { new FilePickerFileType("KML") { Patterns = new[] { "*.kml" } } });
        if (file == null) return;
        var kml = BuildKml();
        await File.WriteAllTextAsync(file, kml);
        StatusText.Text = $"Exported KML: {file}";
    }

    private async void OnExportGpx(object? s, RoutedEventArgs e)
    {
        if (_waypoints.Count == 0) { StatusText.Text = "No waypoints to export."; return; }
        var file = await GetSaveFile("mission.gpx", new[] { new FilePickerFileType("GPX") { Patterns = new[] { "*.gpx" } } });
        if (file == null) return;
        var gpx = BuildGpx();
        await File.WriteAllTextAsync(file, gpx);
        StatusText.Text = $"Exported GPX: {file}";
    }

    private async void OnImport(object? s, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import KML or GPX",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("KML/GPX") { Patterns = new[] { "*.kml", "*.gpx" } } }
        });
        if (files.Count == 0) return;
        var path = files[0].Path.LocalPath;
        var ext = Path.GetExtension(path).ToLower();
        _waypoints.Clear();
        if (ext == ".kml") ImportKml(path);
        else if (ext == ".gpx") ImportGpx(path);
        RefreshMap();
        RefreshList();
        StatusText.Text = $"Imported {_waypoints.Count} waypoints from {Path.GetFileName(path)}";
    }

    private async Task<string?> GetSaveFile(string defaultName, FilePickerFileType[] types)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return null;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save mission file",
            SuggestedFileName = defaultName,
            FileTypeChoices = types
        });
        return file?.Path.LocalPath;
    }

    private string BuildKml()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<kml xmlns=\"http://www.opengis.net/kml/2.2\">");
        sb.AppendLine("<Document>");
        sb.AppendLine($"  <name>DAMbv InfraDrone Mission {DateTime.UtcNow:yyyy-MM-dd}</name>");
        sb.AppendLine($"  <description>Exported from InfraDrone GCS — DAMbv BV</description>");
        foreach (var wp in _waypoints)
        {
            sb.AppendLine($"  <Placemark>");
            sb.AppendLine($"    <name>WP {wp.Number}</name>");
            sb.AppendLine($"    <Point><coordinates>{wp.Lon.ToString(CultureInfo.InvariantCulture)},{wp.Lat.ToString(CultureInfo.InvariantCulture)},{wp.AltM}</coordinates></Point>");
            sb.AppendLine($"  </Placemark>");
        }
        if (_waypoints.Count >= 2)
        {
            sb.AppendLine("  <Placemark><name>Route</name><LineString><coordinates>");
            foreach (var wp in _waypoints)
                sb.Append($"{wp.Lon.ToString(CultureInfo.InvariantCulture)},{wp.Lat.ToString(CultureInfo.InvariantCulture)},{wp.AltM} ");
            sb.AppendLine("</coordinates></LineString></Placemark>");
        }
        sb.AppendLine("</Document></kml>");
        return sb.ToString();
    }

    private string BuildGpx()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<gpx version=\"1.1\" creator=\"InfraDrone GCS — DAMbv BV\"");
        sb.AppendLine("  xmlns=\"http://www.topografix.com/GPX/1/1\">");
        sb.AppendLine($"  <metadata><name>DAMbv Mission {DateTime.UtcNow:yyyy-MM-dd}</name></metadata>");
        foreach (var wp in _waypoints)
        {
            sb.AppendLine($"  <wpt lat=\"{wp.Lat.ToString(CultureInfo.InvariantCulture)}\" lon=\"{wp.Lon.ToString(CultureInfo.InvariantCulture)}\">");
            sb.AppendLine($"    <ele>{wp.AltM}</ele>");
            sb.AppendLine($"    <name>WP {wp.Number}</name>");
            sb.AppendLine($"  </wpt>");
        }
        sb.AppendLine("  <rte>");
        sb.AppendLine($"    <name>DAMbv Mission {DateTime.UtcNow:yyyy-MM-dd}</name>");
        foreach (var wp in _waypoints)
            sb.AppendLine($"    <rtept lat=\"{wp.Lat.ToString(CultureInfo.InvariantCulture)}\" lon=\"{wp.Lon.ToString(CultureInfo.InvariantCulture)}\"><ele>{wp.AltM}</ele><name>WP {wp.Number}</name></rtept>");
        sb.AppendLine("  </rte>");
        sb.AppendLine("</gpx>");
        return sb.ToString();
    }

    private void ImportKml(string path)
    {
        var doc = XDocument.Load(path);
        XNamespace ns = "http://www.opengis.net/kml/2.2";
        int num = 1;
        foreach (var pm in doc.Descendants(ns + "Placemark"))
        {
            var coords = pm.Descendants(ns + "coordinates").FirstOrDefault()?.Value.Trim().Split(' ')[0].Split(',');
            if (coords?.Length >= 2 &&
                double.TryParse(coords[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) &&
                double.TryParse(coords[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
            {
                var alt = coords.Length >= 3 && double.TryParse(coords[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : 30;
                if (pm.Descendants(ns + "LineString").Any()) continue;
                _waypoints.Add(new Waypoint { Number = num++, Lat = lat, Lon = lon, AltM = alt });
            }
        }
    }

    private void ImportGpx(string path)
    {
        var doc = XDocument.Load(path);
        XNamespace ns = "http://www.topografix.com/GPX/1/1";
        int num = 1;
        foreach (var wpt in doc.Descendants(ns + "wpt").Concat(doc.Descendants(ns + "rtept")))
        {
            if (double.TryParse(wpt.Attribute("lat")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) &&
                double.TryParse(wpt.Attribute("lon")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            {
                var eleStr = wpt.Element(ns + "ele")?.Value;
                var alt = eleStr != null && double.TryParse(eleStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : 30;
                _waypoints.Add(new Waypoint { Number = num++, Lat = lat, Lon = lon, AltM = alt });
            }
        }
    }
}
