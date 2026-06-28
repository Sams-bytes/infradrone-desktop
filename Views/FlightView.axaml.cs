using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using InfraDroneDesktop.Services;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using Mapsui.Tiling.Layers;
using Mapsui.Nts.Extensions;
using Mapsui.Nts;
using Mapsui.Nts.Providers;
using Mapsui.Rendering.Skia;
using BruTile.Web;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InfraDroneDesktop.Views;

public partial class FlightView : UserControl
{
    private MavLinkService? _mav;
    private MemoryLayer? _droneLayer;
    private Mapsui.UI.Avalonia.MapControl? _mapControl;
    private Map? _map;
    private int _mapMode = 0; // 0=OSM, 1=PDOK Aerial, 2=Hybrid
    private ILayer? _baseLayer;
    private ILayer? _labelLayer;

    public void ToggleMapLayer()
    {
        if (_map == null || _mapControl == null) return;
        _mapMode = (_mapMode + 1) % 3;
        if (_baseLayer != null) _map.Layers.Remove(_baseLayer);
        if (_labelLayer != null) _map.Layers.Remove(_labelLayer);
        _baseLayer = null;
        _labelLayer = null;
        if (_mapMode == 0)
        {
            _baseLayer = OpenStreetMap.CreateTileLayer("OSM Base");
            _map.Layers.Insert(0, _baseLayer);
        }
        else if (_mapMode == 1)
        {
            _baseLayer = new TileLayer(new HttpTileSource(
                new BruTile.Predefined.GlobalSphericalMercator(),
                "https://service.pdok.nl/hwh/luchtfotorgb/wmts/v1_0/Actueel_ortho25/EPSG:3857/{z}/{x}/{y}.jpeg",
                name: "PDOK")) { Name = "PDOK Aerial" };
            _map.Layers.Insert(0, _baseLayer);
        }
        else
        {
            _baseLayer = new TileLayer(new HttpTileSource(
                new BruTile.Predefined.GlobalSphericalMercator(),
                "https://service.pdok.nl/hwh/luchtfotorgb/wmts/v1_0/Actueel_ortho25/EPSG:3857/{z}/{x}/{y}.jpeg",
                name: "PDOK")) { Name = "PDOK Aerial" };
            _labelLayer = OpenStreetMap.CreateTileLayer("OSM Labels");
            ((TileLayer)_labelLayer).Opacity = 0.4;
            _map.Layers.Insert(0, _baseLayer);
            _map.Layers.Insert(1, _labelLayer);
        }
        _mapControl.RefreshGraphics();
    }

    public FlightView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _mapControl = this.FindControl<Mapsui.UI.Avalonia.MapControl>("MapControl");
        if (_mapControl == null) return;
        _mapControl.Renderer = new MapRenderer();
        SetupMap();
    }

    private void SetupMap()
    {
        var map = new Map();

        // Default: OSM street map
        _baseLayer = OpenStreetMap.CreateTileLayer("OSM Base");
        map.Layers.Add(_baseLayer);



        LoadAirspace(map);

        _droneLayer = new MemoryLayer { Name = "Drone" };
        map.Layers.Add(_droneLayer);

        var groningen = SphericalMercator.FromLonLat(6.5665, 53.2194);
        map.Home = n => n.CenterOnAndZoomTo(new MPoint(groningen.x, groningen.y), 5);
        _map = map;
        _mapControl!.Map = map;
    }

    private void LoadAirspace(Map map)
    {
        try
        {
            var geojsonPath = "/home/sam/agri_drone/airspace_nl.geojson";
            if (!System.IO.File.Exists(geojsonPath)) return;
            var reader = new NetTopologySuite.IO.GeoJsonReader();
            var fc = reader.Read<NetTopologySuite.Features.FeatureCollection>(
                System.IO.File.ReadAllText(geojsonPath));
            var features = new List<IFeature>();
            foreach (var f in fc)
            {
                if (f.Geometry == null) continue;
                var colorHex = f.Attributes?["_color"]?.ToString() ?? "#dc2626";
                byte r = 220, g = 38, b = 38;
                try {
                    r = Convert.ToByte(colorHex.Substring(1,2), 16);
                    g = Convert.ToByte(colorHex.Substring(3,2), 16);
                    b = Convert.ToByte(colorHex.Substring(5,2), 16);
                } catch {}
                var mf = new GeometryFeature { Geometry = ProjectGeometry(f.Geometry) };
                mf.Styles.Add(new VectorStyle
                {
                    Fill = new Brush(new Color((int)r, (int)g, (int)b)),
                    Outline = new Pen(new Color((int)r, (int)g, (int)b), 2.0f)
                });
                features.Add(mf);
            }
            map.Layers.Add(new MemoryLayer
            {
                Name = "Airspace",
                Features = features,
                IsMapInfoLayer = true,
                Opacity = 0.25
            });
            Console.WriteLine("[Airspace] Loaded " + features.Count + " zones");
        }
        catch (Exception ex) { Console.WriteLine("[Airspace] " + ex.Message); }
    }

    private NetTopologySuite.Geometries.Geometry ProjectGeometry(NetTopologySuite.Geometries.Geometry geom)
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
        NetTopologySuite.Geometries.Coordinate[] ProjectRing(NetTopologySuite.Geometries.Coordinate[] ring)
        {
            var result = new NetTopologySuite.Geometries.Coordinate[ring.Length];
            for (int i = 0; i < ring.Length; i++)
            {
                var (x, y) = SphericalMercator.FromLonLat(ring[i].X, ring[i].Y);
                result[i] = new NetTopologySuite.Geometries.Coordinate(x, y);
            }
            return result;
        }
        if (geom is NetTopologySuite.Geometries.Polygon poly)
        {
            var shell = factory.CreateLinearRing(ProjectRing(poly.ExteriorRing.Coordinates));
            return factory.CreatePolygon(shell);
        }
        if (geom is NetTopologySuite.Geometries.MultiPolygon mp)
        {
            var polys = new NetTopologySuite.Geometries.Polygon[mp.NumGeometries];
            for (int i = 0; i < mp.NumGeometries; i++)
            {
                var p = (NetTopologySuite.Geometries.Polygon)mp.GetGeometryN(i);
                var shell = factory.CreateLinearRing(ProjectRing(p.ExteriorRing.Coordinates));
                polys[i] = factory.CreatePolygon(shell);
            }
            return factory.CreateMultiPolygon(polys);
        }
        return geom;
    }

    public void SetMavLink(MavLinkService mav)
    {
        _mav = mav;
        _mav.TelemetryUpdated += OnTelemetry;
    }

    private void OnTelemetry(TelemetryData t)
    {
        Dispatcher.UIThread.Post(() =>
        {
            HudAlt.Text = t.Connected ? $"{t.AltRel:F1}m" : "—";
            HudSpeed.Text = t.Connected ? $"{t.Speed:F1}m/s" : "—";
            HudHeading.Text = t.Connected ? $"{t.Heading:F0}°" : "—";
            HudBatt.Text = t.Connected ? $"{t.BatteryPct}%" : "—";
            HudMode.Text = t.Connected ? t.FlightMode : "—";
            HudGps.Text = t.Connected ? $"{t.GpsSats} sat" : "—";
            HudPos.Text = t.Connected && t.Lat != 0 ? $"{t.Lat:F5}, {t.Lon:F5}" : "—";
        });
    }

    private void OnArm(object? s, RoutedEventArgs e) =>
        _mav?.SendCommandAsync("127.0.0.1", 14550, 400, 1, 21196);
    private void OnDisarm(object? s, RoutedEventArgs e) =>
        _mav?.SendCommandAsync("127.0.0.1", 14550, 400, 0);
    private void OnTakeoff(object? s, RoutedEventArgs e) =>
        _mav?.SendCommandAsync("127.0.0.1", 14550, 22, 0, 0, 0, 0, 0, 0, 30);
    private void OnLand(object? s, RoutedEventArgs e) =>
        _mav?.SendCommandAsync("127.0.0.1", 14550, 21);
    private void OnRtl(object? s, RoutedEventArgs e) =>
        _mav?.SendCommandAsync("127.0.0.1", 14550, 20);
    private void OnLoiter(object? s, RoutedEventArgs e) =>
        _mav?.SendCommandAsync("127.0.0.1", 14550, 176, 1, 5);
    private void OnGuided(object? s, RoutedEventArgs e) =>
        _mav?.SendCommandAsync("127.0.0.1", 14550, 176, 1, 4);
    private void OnAuto(object? s, RoutedEventArgs e) =>
        _mav?.SendCommandAsync("127.0.0.1", 14550, 176, 1, 3);
    private void OnMapToggle(object? s, RoutedEventArgs e)
    {
        ToggleMapLayer();
        var labels = new[] { "🗺 Street", "🛰 Aerial", "🌍 Hybrid" };
        BtnMapToggle.Content = labels[_mapMode];
    }
}
