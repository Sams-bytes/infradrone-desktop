using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using InfraDroneDesktop.Services;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.Styles;
using MBrush = Mapsui.Styles.Brush;
using MColor = Mapsui.Styles.Color;
using MPen = Mapsui.Styles.Pen;
using Mapsui.Tiling;
using Mapsui.Tiling.Layers;
using Mapsui.Nts.Extensions;
using Mapsui.Nts;
using Mapsui.Nts.Providers;
using Mapsui.Rendering.Skia;
using BruTile.Cache;
using System.IO;
using BruTile.Web;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InfraDroneDesktop.Views;

public partial class FlightView : UserControl
{
    private AsvMavLinkService? _mav;
    private Mavlink1SerialService? _v1;

    public void SetMavlinkV1(Mavlink1SerialService v1)
    {
        if (_v1 == v1) return;
        _v1 = v1;
        _v1.TelemetryUpdated += OnV1Telemetry;
        _v1.StatusTextReceived += (severity, text) => ShowStatusBanner(text);
    }

    private void ShowStatusBanner(string text)
    {
        var lower = text.ToLowerInvariant();
        bool actionNeeded = lower.Contains("place") || lower.Contains("rotate") || lower.Contains("press");
        bool prearmBlock = lower.Contains("prearm") || lower.Contains("inconsistent") || lower.Contains("fail");
        Dispatcher.UIThread.Post(() =>
        {
            StatusBanner.IsVisible = true;
            StatusBannerText.Text = text;
            string accentHex, label, icon;
            if (prearmBlock) { accentHex = "#ef4444"; label = "PREFLIGHT BLOCKED"; icon = "⛔"; }
            else if (actionNeeded) { accentHex = "#f59e0b"; label = "ACTION NEEDED"; icon = "⚠"; }
            else { accentHex = "#0d9e75"; label = "VEHICLE STATUS"; icon = "ℹ"; }
            var accentBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(accentHex));
            StatusBannerAccent.Background = accentBrush;
            StatusBannerIcon.Foreground = accentBrush;
            StatusBannerIcon.Text = icon;
            StatusBannerLabel.Text = label;
        });
    }

    private DateTime _lastV1UiUpdate = DateTime.MinValue;
    private void OnV1Telemetry(Mavlink1Telemetry t)
    {
        // Only drive the HUD from v1 telemetry when the Cube Orange path isn't
        // the one actually connected -- avoids the two controllers fighting
        // over the same display if both happened to be plugged in.
        if (CubeOrangeConnected) return;

        var now = DateTime.UtcNow;
        if (now - _lastV1UiUpdate < TelemetryUiThrottle) return;
        _lastV1UiUpdate = now;

        Dispatcher.UIThread.Post(() =>
        {
            HudAlt.Text = t.Connected ? $"{t.RelativeAlt:F1}m" : "—";
            HudSpeed.Text = t.Connected ? $"{t.Speed:F1}m/s" : "—";
            HudHeading.Text = t.Connected ? $"{t.Heading:F0}°" : "—";
            HudBatt.Text = t.Connected && t.BatteryPct >= 0 ? $"{t.BatteryPct}%" : "—";
            HudMode.Text = t.Connected ? t.FlightMode : "—";
            HudGps.Text = t.Connected ? $"fix={t.GpsFix} {t.GpsSats}sat" : "—";
            HudPos.Text = t.Connected && t.GpsFix >= 3 && t.Lat != 0 ? $"{t.Lat:F5}, {t.Lon:F5}" : "—";
            if (t.Connected)
            {
                AttitudeText.Text = $"R:{t.Roll:F0}° P:{t.Pitch:F0}°";
                if (AttitudeCanvas.RenderTransform is Avalonia.Media.RotateTransform attRt)
                    attRt.Angle = -t.Roll;
                Avalonia.Controls.Canvas.SetTop(AttitudeCanvas, (t.Pitch * 3) - 75);
            }
            else
            {
                AttitudeText.Text = "R:0° P:0°";
            }
            if (t.Connected)
            {
                CompassHeadingText.Text = $"{t.Heading:F0}°";
                if (CompassNeedle.RenderTransform is Avalonia.Media.RotateTransform rt)
                    rt.Angle = t.Heading;
                if (t.Lat != 0 && t.Lon != 0)
                    UpdateDroneMarker(t.Lat, t.Lon, t.Heading);
            }
        });
    }
    private MemoryLayer? _droneLayer;
    private MemoryLayer? _adsbLayer;
    private readonly AdsbService _adsb = new AdsbService();
    private Mapsui.UI.Avalonia.MapControl? _mapControl;
    private Map? _map;
    private int _mapMode = 0; // 0=OSM, 1=PDOK Aerial, 2=Hybrid
    private ILayer? _baseLayer;
    private ILayer? _labelLayer;

    private static FileCache GetTileCache(string name)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InfraDrone", "TileCache", name);
        Directory.CreateDirectory(dir);
        return new FileCache(dir, "jpg", TimeSpan.FromDays(30));
    }

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
                name: "PDOK",
                persistentCache: GetTileCache("PDOK"))) { Name = "PDOK Aerial" };
            _map.Layers.Insert(0, _baseLayer);
        }
        else
        {
            _baseLayer = new TileLayer(new HttpTileSource(
                new BruTile.Predefined.GlobalSphericalMercator(),
                "https://service.pdok.nl/hwh/luchtfotorgb/wmts/v1_0/Actueel_ortho25/EPSG:3857/{z}/{x}/{y}.jpeg",
                name: "PDOK",
                persistentCache: GetTileCache("PDOK"))) { Name = "PDOK Aerial" };
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

        // Real OpenSky Network ADS-B data, anonymous access, bounding box
        // covering Groningen province. Anonymous quota is 400 credits/day;
        // a box this size costs 1 credit/call, so 15s polling is fine for
        // demo/testing use but would need a free account for 24/7 running.
        _adsb.AircraftUpdated += OnAdsbUpdated;
        _adsb.Start(laMin: 52.9, loMin: 6.0, laMax: 53.5, loMax: 7.2, pollSeconds: 15);
    }

    private void OnAdsbUpdated(System.Collections.Generic.List<Services.AdsbAircraft> aircraft)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_adsbLayer == null) return;
            var features = new System.Collections.Generic.List<IFeature>();
            foreach (var ac in aircraft)
            {
                var (x, y) = SphericalMercator.FromLonLat(ac.Longitude, ac.Latitude);
                var f = new PointFeature(new MPoint(x, y));
                f.Styles.Add(new SymbolStyle
                {
                    Fill = new MBrush(new MColor(59, 130, 246)),
                    Outline = new MPen(MColor.White, 1.5f),
                    SymbolScale = 0.5,
                    SymbolRotation = ac.TrueTrack ?? 0
                });
                var altText = ac.BaroAltitude.HasValue ? $"{ac.BaroAltitude:F0}m" : "alt?";
                var label = $"{(string.IsNullOrWhiteSpace(ac.Callsign) ? ac.Icao24 : ac.Callsign)} ({altText})";
                f.Styles.Add(new LabelStyle
                {
                    Text = label, Font = new Mapsui.Styles.Font { Size = 10 }, ForeColor = MColor.White,
                    BackColor = new MBrush(new MColor(15, 25, 35, 180)),
                    Offset = new Mapsui.Styles.Offset(0, -18)
                });
                features.Add(f);
            }
            _adsbLayer.Features = features;
            _mapControl?.Map.Refresh();
            AdsbCountText.Text = $"{aircraft.Count} aircraft nearby";
        });
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
        _adsbLayer = new MemoryLayer { Name = "ADS-B Aircraft" };
        map.Layers.Add(_adsbLayer);

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

    private void UpdateDroneMarker(double lat, double lon, double heading)
    {
        if (_droneLayer == null || _mapControl == null) return;
        var (x, y) = SphericalMercator.FromLonLat(lon, lat);
        var f = new PointFeature(new MPoint(x, y));
        f.Styles.Add(new SymbolStyle
        {
            Fill = new MBrush(new MColor(13, 158, 117)),
            Outline = new MPen(MColor.White, 2.5f),
            SymbolScale = 0.8,
            SymbolRotation = heading
        });
        _droneLayer.Features = new System.Collections.Generic.List<IFeature> { f };
        _mapControl.Map.Refresh();
    }

    private void HideDroneMarker()
    {
        if (_droneLayer == null) return;
        _droneLayer.Features = new System.Collections.Generic.List<IFeature>();
        _mapControl?.Map.Refresh();
    }

    public void SetMavLink(AsvMavLinkService mav)
    {
        _mav = mav;
        _mav.TelemetryUpdated += OnTelemetry;
        _mav.CommandResult += OnCommandAck;
    }

    private void OnCommandAck(string command, bool success)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var color = success ? "#0d9e75" : "#ef4444";
            var icon = success ? "✓" : "✗";
            var msg = $"{icon} {command}: {(success ? "ACCEPTED" : "REJECTED")}";

            // Show feedback in HUD mode field temporarily
            var prev = HudMode.Text;
            HudMode.Text = msg;
            HudMode.Foreground = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse(color));

            // Reset after 3 seconds
            System.Threading.Tasks.Task.Delay(3000).ContinueWith(_ =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    HudMode.Text = _mav?.Telemetry.FlightMode ?? "—";
                    HudMode.Foreground = new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.Parse("#0d9e75"));
                }));
        });
    }

    private DateTime _lastTelemetryUiUpdate = DateTime.MinValue;
    private static readonly TimeSpan TelemetryUiThrottle = TimeSpan.FromMilliseconds(150);

    private void OnTelemetry(AsvTelemetryData t)
    {
        // Real telemetry can arrive 50-100+ times/second -- throttle UI updates
        // to a human-readable rate to avoid overwhelming the renderer/GC.
        var now = DateTime.UtcNow;
        if (now - _lastTelemetryUiUpdate < TelemetryUiThrottle) return;
        _lastTelemetryUiUpdate = now;

        Dispatcher.UIThread.Post(() =>
        {
            HudAlt.Text = t.Connected ? $"{t.AltRel:F1}m" : "—";
            HudSpeed.Text = t.Connected ? $"{t.Speed:F1}m/s" : "—";
            HudHeading.Text = t.Connected ? $"{t.Heading:F0}°" : "—";

            if (t.Connected)
            {
                CompassHeadingText.Text = $"{t.Heading:F0}°";
                if (CompassNeedle.RenderTransform is Avalonia.Media.RotateTransform rt)
                    rt.Angle = t.Heading;
            }
            else
            {
                CompassHeadingText.Text = "---°";
            }

            if (t.Connected)
            {
                AttitudeText.Text = $"R:{t.Roll:F0}° P:{t.Pitch:F0}°";
                if (AttitudeCanvas.RenderTransform is Avalonia.Media.RotateTransform attRt)
                    attRt.Angle = -t.Roll; // negative: banking right should tilt horizon left visually
                // Pitch shifts the canvas vertically -- pixels-per-degree tuned to the 300px canvas
                Avalonia.Controls.Canvas.SetTop(AttitudeCanvas, (t.Pitch * 3) - 75);
            }
            else
            {
                AttitudeText.Text = "R:0° P:0°";
            }
            HudBatt.Text = t.Connected ? $"{t.BatteryPct}%" : "—";
            HudMode.Text = t.Connected ? t.FlightMode : "—";
            HudGps.Text = t.Connected ? $"{t.GpsSats} sat" : "—";
            HudPos.Text = t.Connected && t.Lat != 0 ? $"{t.Lat:F5}, {t.Lon:F5}" : "—";
            if (t.Connected && t.Lat != 0 && t.Lon != 0 && t.GpsFix >= 3)
                UpdateDroneMarker(t.Lat, t.Lon, t.Heading);
            else
                HideDroneMarker();
        });
    }

    // Routes to whichever controller is actually connected: the working
    // Cube Orange path (_mav, MAVLink v2) or the BCube/older-Pixhawk path
    // (_v1, MAVLink v1). Prefers _mav if both happen to be set; falls back
    // to _v1 -- mirrors the controller-selector pattern from MavLinkTestView.
    // Routes to whichever controller is ACTUALLY connected right now --
    // checking Telemetry.Connected, not just whether the object exists,
    // since _mav always exists as an object even with no real vehicle.
    private bool CubeOrangeConnected => _mav != null && _mav.Telemetry.Connected;

    private async void OnArm(object? s, RoutedEventArgs e)
    {
        if (CubeOrangeConnected) await _mav!.ArmAsync(true);
        else _v1?.ArmAsync(true);
    }
    private async void OnDisarm(object? s, RoutedEventArgs e)
    {
        if (CubeOrangeConnected) await _mav!.ArmAsync(false);
        else _v1?.ArmAsync(false);
    }
    private async void OnTakeoff(object? s, RoutedEventArgs e)
    {
        if (CubeOrangeConnected) await _mav!.TakeoffAsync(30);
        else _v1?.TakeoffAsync(30);
    }
    private async void OnLand(object? s, RoutedEventArgs e)
    {
        if (CubeOrangeConnected) await _mav!.LandAsync();
        else _v1?.LandAsync();
    }
    private async void OnRtl(object? s, RoutedEventArgs e)
    {
        if (CubeOrangeConnected) await _mav!.RtlAsync();
        else _v1?.RtlAsync();
    }
    // SAFETY FIX: mode IDs were previously wrong -- OnLoiter sent 5 (actually FBWA),
    // OnGuided sent 4 (actually ACRO), OnAuto sent 3 (actually TRAINING).
    // Corrected against real ArduPlane mode table (verified via mavlink_bridge.py):
    // 10=Auto, 12=Loiter, 15=Guided.
    private async void OnLoiter(object? s, RoutedEventArgs e)
    {
        if (CubeOrangeConnected) await _mav!.SetModeAsync(12);
        else _v1?.SetMode(5); // ArduCopter LOITER=5 (different numbering from ArduPlane)
    }
    private async void OnGuided(object? s, RoutedEventArgs e)
    {
        if (CubeOrangeConnected) await _mav!.SetModeAsync(15);
        else _v1?.SetMode(4); // ArduCopter GUIDED=4
    }
    private async void OnAuto(object? s, RoutedEventArgs e)
    {
        if (CubeOrangeConnected) await _mav!.SetModeAsync(10);
        else _v1?.SetMode(3); // ArduCopter AUTO=3
    }
    private void OnMapToggle(object? s, RoutedEventArgs e)
    {
        ToggleMapLayer();
        var labels = new[] { "🗺 Street", "🛰 Aerial", "🌍 Hybrid" };
        BtnMapToggle.Content = labels[_mapMode];
    }
}
