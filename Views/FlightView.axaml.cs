using System.Threading.Tasks;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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
    private MemoryLayer? _wpLayer;
    private MemoryLayer? _routeLayer;
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
        _routeLayer = new MemoryLayer { Name = "Mission Route" };
        map.Layers.Add(_routeLayer);
        _wpLayer = new MemoryLayer { Name = "Mission Waypoints" };
        map.Layers.Add(_wpLayer);

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
                    Fill = new Mapsui.Styles.Brush(new Mapsui.Styles.Color((int)r, (int)g, (int)b)),
                    Outline = new Mapsui.Styles.Pen(new Mapsui.Styles.Color((int)r, (int)g, (int)b), 2.0f)
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
        if (geom is NetTopologySuite.Geometries.Point pt)
        {
            var (x, y) = SphericalMercator.FromLonLat(pt.X, pt.Y);
            return factory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(x, y));
        }
        if (geom is NetTopologySuite.Geometries.LineString ls)
        {
            return factory.CreateLineString(ProjectRing(ls.Coordinates));
        }
        if (geom is NetTopologySuite.Geometries.MultiLineString mls)
        {
            var lines = new NetTopologySuite.Geometries.LineString[mls.NumGeometries];
            for (int i = 0; i < mls.NumGeometries; i++)
            {
                var l = (NetTopologySuite.Geometries.LineString)mls.GetGeometryN(i);
                lines[i] = factory.CreateLineString(ProjectRing(l.Coordinates));
            }
            return factory.CreateMultiLineString(lines);
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

    // --- Minimal waypoint plotting on this view's own map (no export/import/survey) ---
    internal readonly System.Collections.Generic.List<Waypoint> _waypoints = new();
    private bool _addWpMode = false;
    private Avalonia.Point _wpPressPos;

    private void OnAddWpToggle(object? s, RoutedEventArgs e)
    {
        _addWpMode = !_addWpMode;
        BtnAddWpToggle.Content = _addWpMode ? "📍 Add WP: ON" : "📍 Add WP: OFF";
        BtnAddWpToggle.Background = _addWpMode
            ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0d3d2e"))
            : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1a2637"));
        if (_addWpMode)
        {
            _mapControl.PointerPressed += OnWpMapPressed;
            _mapControl.PointerReleased += OnWpMapReleased;
        }
        else
        {
            _mapControl.PointerPressed -= OnWpMapPressed;
            _mapControl.PointerReleased -= OnWpMapReleased;
        }
    }

    private void OnWpMapPressed(object? sender, PointerPressedEventArgs e)
    {
        _wpPressPos = e.GetPosition(_mapControl);
    }

    private void OnWpMapReleased(object? sender, PointerReleasedEventArgs e)
    {
        var releasePos = e.GetPosition(_mapControl);
        var dx = releasePos.X - _wpPressPos.X;
        var dy = releasePos.Y - _wpPressPos.Y;
        if (Math.Sqrt(dx * dx + dy * dy) > 5) return; // was a pan/drag, not a click
        if (_mapControl?.Map == null) return;
        var vp = _mapControl.Map.Navigator.Viewport;
        var worldX = vp.CenterX + (releasePos.X - vp.Width / 2) * vp.Resolution;
        var worldY = vp.CenterY - (releasePos.Y - vp.Height / 2) * vp.Resolution;
        var lonLat = Mapsui.Projections.SphericalMercator.ToLonLat(worldX, worldY);
        _waypoints.Add(new Waypoint
        {
            Number = _waypoints.Count + 1,
            Lat = lonLat.lat,
            Lon = lonLat.lon,
            AltM = 30
        });
        RefreshWaypointList();
        RefreshWpMap();
        MissionStatusText.Text = $"{_waypoints.Count} waypoint(s) placed.";
    }

    private void RefreshWpMap()
    {
        if (_wpLayer == null || _routeLayer == null) return;
        var wpFeatures = new System.Collections.Generic.List<IFeature>();
        var routeFeatures = new System.Collections.Generic.List<IFeature>();
        foreach (var wp in _waypoints)
        {
            var (x, y) = Mapsui.Projections.SphericalMercator.FromLonLat(wp.Lon, wp.Lat);
            var f = new PointFeature(new MPoint(x, y));
            f.Styles.Add(new SymbolStyle
            {
                Fill = new Mapsui.Styles.Brush(new Mapsui.Styles.Color(13, 158, 117)),
                Outline = new Mapsui.Styles.Pen(Mapsui.Styles.Color.White, 2),
                SymbolScale = 0.5
            });
            wpFeatures.Add(f);
        }
        if (_waypoints.Count >= 2)
        {
            var coords = _waypoints.Select(wp =>
            {
                var (x, y) = Mapsui.Projections.SphericalMercator.FromLonLat(wp.Lon, wp.Lat);
                return new NetTopologySuite.Geometries.Coordinate(x, y);
            }).ToArray();
            var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
            var line = factory.CreateLineString(coords);
            var lf = new GeometryFeature { Geometry = line };
            lf.Styles.Add(new VectorStyle
            {
                Fill = null,
                Line = new Mapsui.Styles.Pen(new Mapsui.Styles.Color(13, 158, 117, 200), 2)
            });
            routeFeatures.Add(lf);
        }
        _wpLayer.Features = wpFeatures;
        _routeLayer.Features = routeFeatures;
        _mapControl?.Map.Refresh();
    }
    private void RefreshWaypointList()
    {
        WpCount.Text = $"{_waypoints.Count} points";
        WaypointList.Items.Clear();
        foreach (var wp in _waypoints)
        {
            var altBox = new TextBox { Text = wp.AltM.ToString("F0"), Width = 50, FontSize = 10, Height = 26,
                Background = Avalonia.Media.SolidColorBrush.Parse("#f8fafc"), Foreground = Avalonia.Media.SolidColorBrush.Parse("#0f1923"),
                BorderBrush = Avalonia.Media.SolidColorBrush.Parse("#0d9e75"), BorderThickness = new Avalonia.Thickness(1.5),
                FontWeight = FontWeight.Bold, Padding = new Avalonia.Thickness(4) };
            altBox.LostFocus += (_, _) => { if (double.TryParse(altBox.Text, out var v)) wp.AltM = v; };

            var holdBox = new TextBox { Text = wp.HoldTimeSec.ToString("F0"), Width = 50, FontSize = 10, Height = 26,
                Background = Avalonia.Media.SolidColorBrush.Parse("#f8fafc"), Foreground = Avalonia.Media.SolidColorBrush.Parse("#0f1923"),
                BorderBrush = Avalonia.Media.SolidColorBrush.Parse("#0d9e75"), BorderThickness = new Avalonia.Thickness(1.5),
                FontWeight = FontWeight.Bold, Padding = new Avalonia.Thickness(4) };
            holdBox.LostFocus += (_, _) => { if (double.TryParse(holdBox.Text, out var v)) wp.HoldTimeSec = v; };

            var radiusBox = new TextBox { Text = wp.AcceptRadiusM.ToString("F0"), Width = 50, FontSize = 10, Height = 26,
                Background = Avalonia.Media.SolidColorBrush.Parse("#f8fafc"), Foreground = Avalonia.Media.SolidColorBrush.Parse("#0f1923"),
                BorderBrush = Avalonia.Media.SolidColorBrush.Parse("#0d9e75"), BorderThickness = new Avalonia.Thickness(1.5),
                FontWeight = FontWeight.Bold, Padding = new Avalonia.Thickness(4) };
            radiusBox.LostFocus += (_, _) => { if (double.TryParse(radiusBox.Text, out var v)) wp.AcceptRadiusM = v; };

            var yawBox = new TextBox { Text = wp.YawDeg.ToString("F0"), Width = 50, FontSize = 10, Height = 26,
                Background = Avalonia.Media.SolidColorBrush.Parse("#f8fafc"), Foreground = Avalonia.Media.SolidColorBrush.Parse("#0f1923"),
                BorderBrush = Avalonia.Media.SolidColorBrush.Parse("#0d9e75"), BorderThickness = new Avalonia.Thickness(1.5),
                FontWeight = FontWeight.Bold, Padding = new Avalonia.Thickness(4) };
            yawBox.LostFocus += (_, _) => { if (double.TryParse(yawBox.Text, out var v)) wp.YawDeg = v; };

            var camCheck = new CheckBox { Content = "📷", IsChecked = wp.CameraTrigger, FontSize = 10,
                Foreground = Avalonia.Media.SolidColorBrush.Parse("#94a3b8") };
            camCheck.IsCheckedChanged += (_, _) => wp.CameraTrigger = camCheck.IsChecked ?? false;

            WaypointList.Items.Add(new Border
            {
                Background = Avalonia.Media.SolidColorBrush.Parse("#131f2e"),
                BorderBrush = Avalonia.Media.SolidColorBrush.Parse("#1e3a5f"),
                BorderThickness = new Avalonia.Thickness(0.5),
                CornerRadius = new Avalonia.CornerRadius(6),
                Padding = new Avalonia.Thickness(10, 6),
                Margin = new Avalonia.Thickness(2),
                Width = 190,
                Child = new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock { Text = $"WP {wp.Number}", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Avalonia.Media.SolidColorBrush.Parse("#0d9e75") },
                        new TextBlock { Text = $"{wp.Lat:F5}°N  {wp.Lon:F5}°E", FontSize = 9, Foreground = Avalonia.Media.SolidColorBrush.Parse("#94a3b8"), FontFamily = new FontFamily("Consolas") },
                        new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                            Children = { new TextBlock { Text = "Alt(m):", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Avalonia.Media.SolidColorBrush.Parse("#cbd5e1"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, altBox } },
                        new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                            Children = { new TextBlock { Text = "Hold(s):", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Avalonia.Media.SolidColorBrush.Parse("#cbd5e1"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, holdBox } },
                        new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                            Children = { new TextBlock { Text = "Radius(m):", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Avalonia.Media.SolidColorBrush.Parse("#cbd5e1"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, radiusBox } },
                        new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                            Children = { new TextBlock { Text = "Yaw(°):", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Avalonia.Media.SolidColorBrush.Parse("#cbd5e1"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, yawBox } },
                        camCheck,
                    }
                }
            });
        }
    }

    private void OnViewFlightLog(object? s, RoutedEventArgs e)
    {
        var path = _v1?.FlightLogPath;
        if (path == null || !System.IO.File.Exists(path))
        {
            MissionStatusText.Text = "No flight log yet -- connect BCube first.";
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }
    private void OnClearWaypoints(object? s, RoutedEventArgs e)
    {
        _waypoints.Clear();
        RefreshWaypointList();
        RefreshWpMap();
        MissionStatusText.Text = "Waypoints cleared.";
    }

    private async void OnUploadMission(object? s, RoutedEventArgs e)
    {
        if (_waypoints.Count == 0) { MissionStatusText.Text = "No waypoints to upload."; return; }
        bool bcubeConnected = _v1 != null && _v1.Telemetry.Connected;
        if (!CubeOrangeConnected && !bcubeConnected)
        {
            MissionStatusText.Text = "No vehicle connected -- cannot upload.";
            return;
        }
        MissionStatusText.Text = $"Uploading {_waypoints.Count} waypoints...";
        var wps = _waypoints.Select(wp => (wp.Lat, wp.Lon, (float)wp.AltM)).ToList();
        if (bcubeConnected)
        {
            var result = await _v1!.UploadMissionAsync(wps);
            MissionStatusText.Text = result switch
            {
                Mavlink1SerialService.MissionUploadResult.Success => $"Upload complete: {_waypoints.Count} waypoints sent to BCube.",
                Mavlink1SerialService.MissionUploadResult.Timeout => "Upload FAILED: vehicle stopped responding (timeout).",
                Mavlink1SerialService.MissionUploadResult.Rejected => "Upload FAILED: vehicle rejected the mission.",
                _ => "Upload failed: unknown error."
            };
        }
        else
        {
            MissionStatusText.Text = "Cube Orange mission upload not yet implemented.";
        }
    }

    private MemoryLayer? _orthoLayer;
    public void AddOrthomosaicLayer(byte[] imageBytes, double minX, double minY, double maxX, double maxY)
    {
        if (_map == null || _mapControl == null) return;
        if (_orthoLayer != null) _map.Layers.Remove(_orthoLayer);
        var extent = new Mapsui.MRect(minX, minY, maxX, maxY);
        var raster = new Mapsui.MRaster(imageBytes, extent);
        var feature = new Mapsui.Layers.RasterFeature(raster);
        feature.Styles.Add(new Mapsui.Styles.RasterStyle());
        _orthoLayer = new MemoryLayer { Name = "Orthomosaic", Features = new System.Collections.Generic.List<IFeature> { feature } };
        _map.Layers.Add(_orthoLayer);
        _mapControl.Map.Refresh();
    }

    private MemoryLayer? _groningenRoadLayer;
    private MemoryLayer? _groningenBridgeLayer;
    private bool _groningenInfoWired = false;
    private static readonly System.Net.Http.HttpClient _groningenHttp = new System.Net.Http.HttpClient();
    private void OnGroningenMapInfo(object? sender, Mapsui.MapInfoEventArgs e)
    {
        var feature = e.MapInfo?.Feature;
        var layer = e.MapInfo?.Layer;
        if (feature == null || (layer != _groningenRoadLayer && layer != _groningenBridgeLayer && layer != _groningenGuardrailLayer && layer != _groningenCrackingLayer && layer != _groningenRavelingLayer && layer != _groningenUnevennessLayer && layer != _groningenRuttingLayer && layer != _groningenLongEvennessLayer && layer != _bagBuildingsLayer && layer != _bermconditiesLayer && layer != _duikersLayer && layer != _geluidsschermenLayer))
        {
            GroningenInfoCard.IsVisible = false;
            return;
        }
        var lines = new System.Collections.Generic.List<string>();
        _lastClickedFields = new System.Collections.Generic.Dictionary<string, string>();
        foreach (var field in feature.Fields)
        {
            if (field == "SHAPE.STArea()" || field == "SHAPE.STLength()") continue;
            var val = feature[field];
            if (val == null || string.IsNullOrEmpty(val.ToString())) continue;
            lines.Add($"{field}: {val}");
            _lastClickedFields[field] = val.ToString()!;
        }
        _lastClickedLayerName = layer?.Name ?? "Unknown layer";
        var worldPos = e.MapInfo?.WorldPosition;
        if (worldPos != null)
        {
            var (lon, lat) = SphericalMercator.ToLonLat(worldPos.X, worldPos.Y);
            _lastClickedLat = lat;
            _lastClickedLon = lon;
        }
        GroningenInfoText.Text = string.Join("\n", lines);
        TicketStatusText.Text = "";
        TicketDescriptionBox.Text = "";
        GroningenInfoCard.IsVisible = true;
        TicketDescriptionBox.InvalidateVisual();
        TicketSeverityCombo.InvalidateVisual();
    }
    private System.Collections.Generic.Dictionary<string, string>? _lastClickedFields;
    private string _lastClickedLayerName = "";
    private double _lastClickedLat, _lastClickedLon;

    private void OnPredictTrend(object? s, RoutedEventArgs e)
    {
        if (_lastClickedFields == null)
        {
            TrendText.Text = "Click a CROW pavement-condition asset first.";
            return;
        }
        // Find real dated survey pairs, e.g. crow_inp_date_2021 / crow_inp_date_2023.
        // Only uses years actually present on this feature -- doesn't assume 2021/2023
        // specifically, since different segments may have been surveyed in different years.
        var dateFields = _lastClickedFields.Keys
            .Where(k => k.StartsWith("crow_inp_date_"))
            .Select(k => k.Substring("crow_inp_date_".Length))
            .OrderBy(y => y)
            .ToList();
        if (dateFields.Count < 2)
        {
            TrendText.Text = "No paired survey dates found on this asset -- trend prediction needs at least two dated CROW measurements.";
            return;
        }
        var yearA = dateFields[0];
        var yearB = dateFields[dateFields.Count - 1];
        if (!DateTime.TryParse(_lastClickedFields[$"crow_inp_date_{yearA}"], out var dateA) ||
            !DateTime.TryParse(_lastClickedFields[$"crow_inp_date_{yearB}"], out var dateB))
        {
            TrendText.Text = "Could not parse survey dates.";
            return;
        }
        var yearsElapsed = (dateB - dateA).TotalDays / 365.25;
        if (yearsElapsed <= 0)
        {
            TrendText.Text = "Survey dates invalid (non-positive time span).";
            return;
        }

        var metricPrefixes = _lastClickedFields.Keys
            .Where(k => k.StartsWith("crow_") && k.EndsWith($"_{yearB}") && !k.StartsWith("crow_inp_date") && !k.StartsWith("crow_alg_"))
            .Select(k => k.Substring("crow_".Length, k.Length - "crow_".Length - $"_{yearB}".Length))
            .Distinct()
            .OrderBy(m => m)
            .ToList();

        var lines = new System.Collections.Generic.List<string>();
        lines.Add($"Real survey dates used: {dateA:yyyy-MM-dd} -> {dateB:yyyy-MM-dd} ({yearsElapsed:0.0} years)");
        lines.Add("");
        foreach (var metric in metricPrefixes)
        {
            var keyA = $"crow_{metric}_{yearA}";
            var keyB = $"crow_{metric}_{yearB}";
            if (!_lastClickedFields.TryGetValue(keyA, out var strA) || !_lastClickedFields.TryGetValue(keyB, out var strB)) continue;
            if (!double.TryParse(strA, System.Globalization.CultureInfo.InvariantCulture, out var valA)) continue;
            if (!double.TryParse(strB, System.Globalization.CultureInfo.InvariantCulture, out var valB)) continue;

            var ratePerYear = (valB - valA) / yearsElapsed;
            var trend = ratePerYear > 0.01 ? "worsening" : ratePerYear < -0.01 ? "improving" : "stable";
            var proj3 = valB + ratePerYear * 3;
            var proj5 = valB + ratePerYear * 5;
            lines.Add($"{metric}: {valA:0.##} -> {valB:0.##} ({trend}, {ratePerYear:+0.##;-0.##;0}/yr)");
            lines.Add($"  projected in 3yr: {proj3:0.##}  |  5yr: {proj5:0.##}");
        }
        if (lines.Count <= 2)
        {
            lines.Add("No paired numeric CROW metrics found for this asset.");
        }
        lines.Add("");
        lines.Add("Linear projection from measured rate -- not an official CROW maintenance-trigger threshold. For guidance only.");
        TrendText.Text = string.Join("\n", lines);
    }

    private async void OnGenerateTicket(object? s, RoutedEventArgs e)
    {
        if (_lastClickedFields == null)
        {
            TicketStatusText.Text = "Click an asset on the map first.";
            return;
        }
        var description = TicketDescriptionBox.Text?.Trim();
        if (string.IsNullOrEmpty(description))
        {
            TicketStatusText.Text = "Enter a description of the finding first.";
            return;
        }
        var severity = (TicketSeverityCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Medium";

        BtnGenerateTicket.IsEnabled = false;
        TicketStatusText.Text = "Generating ticket...";
        try
        {
            var inputData = new
            {
                layer_name = _lastClickedLayerName,
                fields = _lastClickedFields,
                description,
                severity,
                lat = _lastClickedLat,
                lon = _lastClickedLon,
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
            };
            var inputJson = System.Text.Json.JsonSerializer.Serialize(inputData);
            var inputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ticket_input_{DateTime.Now.Ticks}.json");
            await System.IO.File.WriteAllTextAsync(inputPath, inputJson);

            var downloadsDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            System.IO.Directory.CreateDirectory(downloadsDir);
            var outPdf = System.IO.Path.Combine(downloadsDir, $"maintenance_ticket_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

            var scriptPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "infradrone-desktop", "generate_maintenance_ticket.py");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/python3",
                Arguments = "\"" + scriptPath + "\" \"" + inputPath + "\" \"" + outPdf + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var proc = System.Diagnostics.Process.Start(psi);
            var stderr = await proc!.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode == 0 && System.IO.File.Exists(outPdf))
            {
                TicketStatusText.Text = $"Saved: {outPdf}";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = outPdf,
                    UseShellExecute = true
                });
            }
            else
            {
                TicketStatusText.Text = $"Failed: {stderr.Substring(0, Math.Min(200, stderr.Length))}";
            }
        }
        catch (Exception ex)
        {
            TicketStatusText.Text = $"Failed: {ex.Message}";
        }
        finally
        {
            BtnGenerateTicket.IsEnabled = true;
        }
    }
    private async void OnGroningenRoadsToggled(object? s, RoutedEventArgs e)
    {
        if (_map == null || _mapControl == null) return;
        if (ChkGroningenRoads.IsChecked != true)
        {
            if (_groningenRoadLayer != null)
            {
                _map.Layers.Remove(_groningenRoadLayer);
                _groningenRoadLayer = null;
                GroningenInfoCard.IsVisible = false;
                _mapControl.Map.Refresh();
            }
            return;
        }
        try
        {
            // Real, public, no-login ArcGIS Server (Province of Groningen), confirmed
            // live via curl before writing this code. Layer 314 = "Open verharding
            // berm" (road-shoulder paving) under Mobiliteit/Areaalviewer. Data comes
            // back as WGS84 lon/lat when requesting f=geojson, so it plugs directly
            // into the same ProjectGeometry() used for airspace zones -- no separate
            // RD New reprojection needed.
            var url = "https://geoservices.provinciegroningen.nl/server/rest/services/Mobiliteit/Areaalviewer/MapServer/314/query?where=1%3D1&outFields=*&f=geojson&resultRecordCount=2000";
            var json = await _groningenHttp.GetStringAsync(url);
            var reader = new NetTopologySuite.IO.GeoJsonReader();
            var fc = reader.Read<NetTopologySuite.Features.FeatureCollection>(json);
            var features = new System.Collections.Generic.List<IFeature>();
            foreach (var f in fc)
            {
                if (f.Geometry == null) continue;
                var mf = new GeometryFeature { Geometry = ProjectGeometry(f.Geometry) };
                mf.Styles.Add(new VectorStyle
                {
                    Fill = new Mapsui.Styles.Brush(new Mapsui.Styles.Color(59, 130, 246, 100)),
                    Outline = new Mapsui.Styles.Pen(new Mapsui.Styles.Color(59, 130, 246), 1.5f)
                });
                if (f.Attributes != null)
                {
                    foreach (var attrName in f.Attributes.GetNames())
                        mf[attrName] = f.Attributes[attrName];
                }
                features.Add(mf);
            }
            if (_groningenRoadLayer != null) _map.Layers.Remove(_groningenRoadLayer);
            _groningenRoadLayer = new MemoryLayer { Name = "Groningen Road Assets", Features = features, IsMapInfoLayer = true, Opacity = 0.6 };
            _map.Layers.Add(_groningenRoadLayer);
            var extent = _groningenRoadLayer.Extent;
            if (extent != null) _map.Navigator.ZoomToBox(extent, MBoxFit.Fit);
            _mapControl.Map.Refresh();
            Console.WriteLine($"[GroningenRoads] Loaded {features.Count} features from live province ArcGIS server, extent: {extent}");
            if (!_groningenInfoWired)
            {
                _groningenInfoWired = true;
                _map.Info += OnGroningenMapInfo;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GroningenRoads] Failed to load: {ex.Message}");
        }
    }

    private async void OnGroningenBridgesToggled(object? s, RoutedEventArgs e)
    {
        if (_map == null || _mapControl == null) return;
        if (ChkGroningenBridges.IsChecked != true)
        {
            if (_groningenBridgeLayer != null)
            {
                _map.Layers.Remove(_groningenBridgeLayer);
                _groningenBridgeLayer = null;
                GroningenInfoCard.IsVisible = false;
                _mapControl.Map.Refresh();
            }
            return;
        }
        try
        {
            // Mobiliteit/BruggenVast, layer id=2 ("Bruggen vast" / Fixed Bridges),
            // confirmed live via a real query before writing this -- point geometry,
            // native RD New (28992) but f=geojson auto-reprojects to WGS84 lon/lat.
            var url = "https://geoservices.provinciegroningen.nl/server/rest/services/Mobiliteit/BruggenVast/MapServer/2/query?where=1%3D1&outFields=*&f=geojson&resultRecordCount=2000";
            var json = await _groningenHttp.GetStringAsync(url);
            var reader = new NetTopologySuite.IO.GeoJsonReader();
            var fc = reader.Read<NetTopologySuite.Features.FeatureCollection>(json);
            var features = new System.Collections.Generic.List<IFeature>();
            foreach (var f in fc)
            {
                if (f.Geometry == null) continue;
                var mf = new GeometryFeature { Geometry = ProjectGeometry(f.Geometry) };
                mf.Styles.Add(new SymbolStyle
                {
                    Fill = new Mapsui.Styles.Brush(new Mapsui.Styles.Color(234, 179, 8)),
                    Outline = new Mapsui.Styles.Pen(Mapsui.Styles.Color.White, 1.5f),
                    SymbolScale = 0.6
                });
                if (f.Attributes != null)
                {
                    foreach (var attrName in f.Attributes.GetNames())
                        mf[attrName] = f.Attributes[attrName];
                }
                features.Add(mf);
            }
            if (_groningenBridgeLayer != null) _map.Layers.Remove(_groningenBridgeLayer);
            _groningenBridgeLayer = new MemoryLayer { Name = "Groningen Bridges", Features = features, IsMapInfoLayer = true };
            _map.Layers.Add(_groningenBridgeLayer);
            var extent = _groningenBridgeLayer.Extent;
            if (extent != null) _map.Navigator.ZoomToBox(extent, MBoxFit.Fit);
            _mapControl.Map.Refresh();
            Console.WriteLine($"[GroningenBridges] Loaded {features.Count} bridges from live province ArcGIS server");
            if (!_groningenInfoWired)
            {
                _groningenInfoWired = true;
                _map.Info += OnGroningenMapInfo;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GroningenBridges] Failed to load: {ex.Message}");
        }
    }

    private MemoryLayer? _groningenGuardrailLayer;
    private MemoryLayer? _groningenCrackingLayer;
    private MemoryLayer? _groningenRavelingLayer;
    private MemoryLayer? _groningenUnevennessLayer;
    private MemoryLayer? _groningenRuttingLayer;
    private MemoryLayer? _groningenLongEvennessLayer;
    private MemoryLayer? _bagBuildingsLayer;
    private MemoryLayer? _bermconditiesLayer;
    private MemoryLayer? _duikersLayer;
    private MemoryLayer? _geluidsschermenLayer;
    private Mapsui.Layers.ImageLayer? _ahnElevationLayer;
    private Mapsui.Layers.ImageLayer? _sentinelNdviLayer;
    private async void OnGroningenGuardrailsToggled(object? s, RoutedEventArgs e)
    {
        if (_map == null || _mapControl == null) return;
        if (ChkGroningenGuardrails.IsChecked != true)
        {
            if (_groningenGuardrailLayer != null)
            {
                _map.Layers.Remove(_groningenGuardrailLayer);
                _groningenGuardrailLayer = null;
                GroningenInfoCard.IsVisible = false;
                _mapControl.Map.Refresh();
            }
            return;
        }
        try
        {
            // Mobiliteit/Geleiderails (Guardrails), layer id=0, confirmed live via
            // curl before writing this -- polyline geometry, native RD New (28992)
            // but f=geojson auto-reprojects to WGS84 lon/lat, same as Roads/Bridges.
            var url = "https://geoservices.provinciegroningen.nl/server/rest/services/Mobiliteit/Geleiderails/MapServer/0/query?where=1%3D1&outFields=*&f=geojson&resultRecordCount=2000";
            var json = await _groningenHttp.GetStringAsync(url);
            var reader = new NetTopologySuite.IO.GeoJsonReader();
            var fc = reader.Read<NetTopologySuite.Features.FeatureCollection>(json);
            var features = new System.Collections.Generic.List<IFeature>();
            foreach (var f in fc)
            {
                if (f.Geometry == null) continue;
                var mf = new GeometryFeature { Geometry = ProjectGeometry(f.Geometry) };
                mf.Styles.Add(new VectorStyle
                {
                    Line = new Mapsui.Styles.Pen(new Mapsui.Styles.Color(239, 68, 68), 2.5f)
                });
                if (f.Attributes != null)
                {
                    foreach (var attrName in f.Attributes.GetNames())
                        mf[attrName] = f.Attributes[attrName];
                }
                features.Add(mf);
            }
            if (_groningenGuardrailLayer != null) _map.Layers.Remove(_groningenGuardrailLayer);
            _groningenGuardrailLayer = new MemoryLayer { Name = "Groningen Guardrails", Features = features, IsMapInfoLayer = true };
            _map.Layers.Add(_groningenGuardrailLayer);
            var extent = _groningenGuardrailLayer.Extent;
            if (extent != null) _map.Navigator.ZoomToBox(extent, MBoxFit.Fit);
            _mapControl.Map.Refresh();
            Console.WriteLine($"[GroningenGuardrails] Loaded {features.Count} guardrail segments from live province ArcGIS server");
            if (!_groningenInfoWired)
            {
                _groningenInfoWired = true;
                _map.Info += OnGroningenMapInfo;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GroningenGuardrails] Failed to load: {ex.Message}");
        }
    }

    private async void OnGroningenRavelingToggled(object? s, RoutedEventArgs e)
    {
        if (_map == null || _mapControl == null) return;
        if (ChkGroningenRaveling.IsChecked != true)
        {
            if (_groningenRavelingLayer != null)
            {
                _map.Layers.Remove(_groningenRavelingLayer);
                _groningenRavelingLayer = null;
                GroningenInfoCard.IsVisible = false;
                _mapControl.Map.Refresh();
            }
            return;
        }
        try
        {
            // Hosted/Vergelijking_weginspecties, layer id=101 ("Vergelijking Rafeling"),
            // same real official CROW pavement survey source as Cracking, confirmed
            // via the same server listing before writing this.
            var url = "https://geoservices.provinciegroningen.nl/server/rest/services/Hosted/Vergelijking_weginspecties/FeatureServer/101/query?where=1%3D1&outFields=*&f=geojson&resultRecordCount=2000";
            var json = await _groningenHttp.GetStringAsync(url);
            var reader = new NetTopologySuite.IO.GeoJsonReader();
            var fc = reader.Read<NetTopologySuite.Features.FeatureCollection>(json);
            var features = new System.Collections.Generic.List<IFeature>();
            foreach (var f in fc)
            {
                if (f.Geometry == null) continue;
                var mf = new GeometryFeature { Geometry = ProjectGeometry(f.Geometry) };
                mf.Styles.Add(new VectorStyle
                {
                    Fill = new Mapsui.Styles.Brush(new Mapsui.Styles.Color(139, 92, 246, 130)),
                    Outline = new Mapsui.Styles.Pen(new Mapsui.Styles.Color(139, 92, 246), 1.5f)
                });
                if (f.Attributes != null)
                {
                    foreach (var attrName in f.Attributes.GetNames())
                        mf[attrName] = f.Attributes[attrName];
                }
                features.Add(mf);
            }
            if (_groningenRavelingLayer != null) _map.Layers.Remove(_groningenRavelingLayer);
            _groningenRavelingLayer = new MemoryLayer { Name = "Groningen Raveling", Features = features, IsMapInfoLayer = true, Opacity = 0.7 };
            _map.Layers.Add(_groningenRavelingLayer);
            var extent = _groningenRavelingLayer.Extent;
            if (extent != null) _map.Navigator.ZoomToBox(extent, MBoxFit.Fit);
            _mapControl.Map.Refresh();
            Console.WriteLine($"[GroningenRaveling] Loaded {features.Count} segments from live province ArcGIS server");
            if (!_groningenInfoWired)
            {
                _groningenInfoWired = true;
                _map.Info += OnGroningenMapInfo;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GroningenRaveling] Failed to load: {ex.Message}");
        }
    }
    private async void OnGroningenUnevennessToggled(object? s, RoutedEventArgs e)
    {
        if (_map == null || _mapControl == null) return;
        if (ChkGroningenUnevenness.IsChecked != true)
        {
            if (_groningenUnevennessLayer != null)
            {
                _map.Layers.Remove(_groningenUnevennessLayer);
                _groningenUnevennessLayer = null;
                GroningenInfoCard.IsVisible = false;
                _mapControl.Map.Refresh();
            }
            return;
        }
        try
        {
            // Hosted/Vergelijking_weginspecties, layer id=103 ("Vergelijking oneffenheden"),
            // same real official CROW pavement survey source as Cracking, confirmed
            // via the same server listing before writing this.
            var url = "https://geoservices.provinciegroningen.nl/server/rest/services/Hosted/Vergelijking_weginspecties/FeatureServer/103/query?where=1%3D1&outFields=*&f=geojson&resultRecordCount=2000";
            var json = await _groningenHttp.GetStringAsync(url);
            var reader = new NetTopologySuite.IO.GeoJsonReader();
            var fc = reader.Read<NetTopologySuite.Features.FeatureCollection>(json);
            var features = new System.Collections.Generic.List<IFeature>();
            foreach (var f in fc)
            {
                if (f.Geometry == null) continue;
                var mf = new GeometryFeature { Geometry = ProjectGeometry(f.Geometry) };
                mf.Styles.Add(new VectorStyle
                {
                    Fill = new Mapsui.Styles.Brush(new Mapsui.Styles.Color(20, 184, 166, 130)),
                    Outline = new Mapsui.Styles.Pen(new Mapsui.Styles.Color(20, 184, 166), 1.5f)
                });
                if (f.Attributes != null)
                {
                    foreach (var attrName in f.Attributes.GetNames())
                        mf[attrName] = f.Attributes[attrName];
                }
                features.Add(mf);
            }
            if (_groningenUnevennessLayer != null) _map.Layers.Remove(_groningenUnevennessLayer);
            _groningenUnevennessLayer = new MemoryLayer { Name = "Groningen Unevenness", Features = features, IsMapInfoLayer = true, Opacity = 0.7 };
            _map.Layers.Add(_groningenUnevennessLayer);
            var extent = _groningenUnevennessLayer.Extent;
            if (extent != null) _map.Navigator.ZoomToBox(extent, MBoxFit.Fit);
            _mapControl.Map.Refresh();
            Console.WriteLine($"[GroningenUnevenness] Loaded {features.Count} segments from live province ArcGIS server");
            if (!_groningenInfoWired)
            {
                _groningenInfoWired = true;
                _map.Info += OnGroningenMapInfo;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GroningenUnevenness] Failed to load: {ex.Message}");
        }
    }
    private async void OnGroningenRuttingToggled(object? s, RoutedEventArgs e)
    {
        if (_map == null || _mapControl == null) return;
        if (ChkGroningenRutting.IsChecked != true)
        {
            if (_groningenRuttingLayer != null)
            {
                _map.Layers.Remove(_groningenRuttingLayer);
                _groningenRuttingLayer = null;
                GroningenInfoCard.IsVisible = false;
                _mapControl.Map.Refresh();
            }
            return;
        }
        try
        {
            // Hosted/Vergelijking_weginspecties, layer id=104 ("Vergelijking spoorvorming"),
            // same real official CROW pavement survey source as Cracking, confirmed
            // via the same server listing before writing this.
            var url = "https://geoservices.provinciegroningen.nl/server/rest/services/Hosted/Vergelijking_weginspecties/FeatureServer/104/query?where=1%3D1&outFields=*&f=geojson&resultRecordCount=2000";
            var json = await _groningenHttp.GetStringAsync(url);
            var reader = new NetTopologySuite.IO.GeoJsonReader();
            var fc = reader.Read<NetTopologySuite.Features.FeatureCollection>(json);
            var features = new System.Collections.Generic.List<IFeature>();
            foreach (var f in fc)
            {
                if (f.Geometry == null) continue;
                var mf = new GeometryFeature { Geometry = ProjectGeometry(f.Geometry) };
                mf.Styles.Add(new VectorStyle
                {
                    Fill = new Mapsui.Styles.Brush(new Mapsui.Styles.Color(236, 72, 153, 130)),
                    Outline = new Mapsui.Styles.Pen(new Mapsui.Styles.Color(236, 72, 153), 1.5f)
                });
                if (f.Attributes != null)
                {
                    foreach (var attrName in f.Attributes.GetNames())
                        mf[attrName] = f.Attributes[attrName];
                }
                features.Add(mf);
            }
            if (_groningenRuttingLayer != null) _map.Layers.Remove(_groningenRuttingLayer);
            _groningenRuttingLayer = new MemoryLayer { Name = "Groningen Rutting", Features = features, IsMapInfoLayer = true, Opacity = 0.7 };
            _map.Layers.Add(_groningenRuttingLayer);
            var extent = _groningenRuttingLayer.Extent;
            if (extent != null) _map.Navigator.ZoomToBox(extent, MBoxFit.Fit);
            _mapControl.Map.Refresh();
            Console.WriteLine($"[GroningenRutting] Loaded {features.Count} segments from live province ArcGIS server");
            if (!_groningenInfoWired)
            {
                _groningenInfoWired = true;
                _map.Info += OnGroningenMapInfo;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GroningenRutting] Failed to load: {ex.Message}");
        }
    }
    private async void OnGroningenLongEvennessToggled(object? s, RoutedEventArgs e)
    {
        if (_map == null || _mapControl == null) return;
        if (ChkGroningenLongEvenness.IsChecked != true)
        {
            if (_groningenLongEvennessLayer != null)
            {
                _map.Layers.Remove(_groningenLongEvennessLayer);
                _groningenLongEvennessLayer = null;
                GroningenInfoCard.IsVisible = false;
                _mapControl.Map.Refresh();
            }
            return;
        }
        try
        {
            // Hosted/Vergelijking_weginspecties, layer id=105 ("Vergelijking langsonvlakheid"),
            // same real official CROW pavement survey source as Cracking, confirmed
            // via the same server listing before writing this.
            var url = "https://geoservices.provinciegroningen.nl/server/rest/services/Hosted/Vergelijking_weginspecties/FeatureServer/105/query?where=1%3D1&outFields=*&f=geojson&resultRecordCount=2000";
            var json = await _groningenHttp.GetStringAsync(url);
            var reader = new NetTopologySuite.IO.GeoJsonReader();
            var fc = reader.Read<NetTopologySuite.Features.FeatureCollection>(json);
            var features = new System.Collections.Generic.List<IFeature>();
            foreach (var f in fc)
            {
                if (f.Geometry == null) continue;
                var mf = new GeometryFeature { Geometry = ProjectGeometry(f.Geometry) };
                mf.Styles.Add(new VectorStyle
                {
                    Fill = new Mapsui.Styles.Brush(new Mapsui.Styles.Color(6, 182, 212, 130)),
                    Outline = new Mapsui.Styles.Pen(new Mapsui.Styles.Color(6, 182, 212), 1.5f)
                });
                if (f.Attributes != null)
                {
                    foreach (var attrName in f.Attributes.GetNames())
                        mf[attrName] = f.Attributes[attrName];
                }
                features.Add(mf);
            }
            if (_groningenLongEvennessLayer != null) _map.Layers.Remove(_groningenLongEvennessLayer);
            _groningenLongEvennessLayer = new MemoryLayer { Name = "Groningen LongEvenness", Features = features, IsMapInfoLayer = true, Opacity = 0.7 };
            _map.Layers.Add(_groningenLongEvennessLayer);
            var extent = _groningenLongEvennessLayer.Extent;
            if (extent != null) _map.Navigator.ZoomToBox(extent, MBoxFit.Fit);
            _mapControl.Map.Refresh();
            Console.WriteLine($"[GroningenLongEvenness] Loaded {features.Count} segments from live province ArcGIS server");
            if (!_groningenInfoWired)
            {
                _groningenInfoWired = true;
                _map.Info += OnGroningenMapInfo;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GroningenLongEvenness] Failed to load: {ex.Message}");
        }
    }

        private async void OnBagBuildingsToggled(object? s, RoutedEventArgs e)
    {
        if (_map == null || _mapControl == null) return;
        if (ChkBagBuildings.IsChecked != true)
        {
            if (_bagBuildingsLayer != null)
            {
                _map.Layers.Remove(_bagBuildingsLayer);
                _bagBuildingsLayer = null;
                GroningenInfoCard.IsVisible = false;
                _mapControl.Map.Refresh();
            }
            return;
        }
        try
        {
            // National PDOK BAG (Basisregistratie Adressen en Gebouwen) WFS,
            // confirmed live via curl before writing this -- version=2.0.0 is
            // required explicitly (server errors without it). This is a national
            // dataset, not scoped to Groningen like the province server, so we
            // fix a bounding box around Groningen city center by default --
            // querying with no bbox would return buildings from anywhere in NL.
            // bbox axis order confirmed via live test: lat,lon,lat,lon for
            // EPSG:4326 here, even though the returned geometry itself is lon,lat.
            var url = "https://service.pdok.nl/lv/bag/wfs/v2_0?service=WFS&version=2.0.0&request=GetFeature&typeName=bag:pand&count=2000&srsName=EPSG:4326&outputFormat=json&bbox=53.19,6.53,53.25,6.60,EPSG:4326";
            var json = await _groningenHttp.GetStringAsync(url);
            var reader = new NetTopologySuite.IO.GeoJsonReader();
            var fc = reader.Read<NetTopologySuite.Features.FeatureCollection>(json);
            var features = new System.Collections.Generic.List<IFeature>();
            foreach (var f in fc)
            {
                if (f.Geometry == null) continue;
                var mf = new GeometryFeature { Geometry = ProjectGeometry(f.Geometry) };
                mf.Styles.Add(new VectorStyle
                {
                    Fill = new Mapsui.Styles.Brush(new Mapsui.Styles.Color(236, 72, 255, 160)),
                    Outline = new Mapsui.Styles.Pen(new Mapsui.Styles.Color(236, 72, 255), 2.5f)
                });
                if (f.Attributes != null)
                {
                    foreach (var attrName in f.Attributes.GetNames())
                        mf[attrName] = f.Attributes[attrName];
                }
                features.Add(mf);
            }
            if (_bagBuildingsLayer != null) _map.Layers.Remove(_bagBuildingsLayer);
            _bagBuildingsLayer = new MemoryLayer { Name = "BAG Buildings", Features = features, IsMapInfoLayer = true, Opacity = 0.6 };
            _map.Layers.Add(_bagBuildingsLayer);
            var extent = _bagBuildingsLayer.Extent;
            if (extent != null) _map.Navigator.ZoomToBox(extent, MBoxFit.Fit);
            _mapControl.Map.Refresh();
            Console.WriteLine($"[BagBuildings] Loaded {features.Count} buildings from live national PDOK WFS server");
            if (!_groningenInfoWired)
            {
                _groningenInfoWired = true;
                _map.Info += OnGroningenMapInfo;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BagBuildings] Failed to load: {ex.Message}");
        }
    }

    private async void OnAhnElevationToggled(object? s, RoutedEventArgs e)
    {
        if (_map == null || _mapControl == null) return;
        if (ChkAhnElevation.IsChecked != true)
        {
            if (_ahnElevationLayer != null)
            {
                _map.Layers.Remove(_ahnElevationLayer);
                _ahnElevationLayer = null;
                AhnLegendCard.IsVisible = false;
                _mapControl.Map.Refresh();
            }
            return;
        }
        try
        {
            // AHN (Actueel Hoogtebestand Nederland) national elevation WMS,
            // confirmed live via curl before writing this -- unlike everything
            // else in this file, this is raster/imagery data (rendered elevation
            // map), not vector features, so it uses Mapsui's WmsProvider + a
            // dynamic ImageLayer instead of MemoryLayer/GeometryFeature.
            // dtm_05m = Digital Terrain Model, bare ground elevation excluding
            // buildings/trees -- confirmed EPSG:3857 is directly supported by
            // this server, so no reprojection wrapper (ProjectingProvider) needed.
            var capUrl = "https://service.pdok.nl/rws/ahn/wms/v1_0?SERVICE=WMS&request=GetCapabilities";
            var xmlString = await _groningenHttp.GetStringAsync(capUrl);
            var xmlDoc = new System.Xml.XmlDocument();
            xmlDoc.LoadXml(xmlString);
            Func<string, System.Threading.Tasks.Task<System.IO.Stream>> fetchFunc =
                async (url) => await _groningenHttp.GetStreamAsync(url);
            var provider = new Mapsui.Providers.Wms.WmsProvider(xmlDoc, fetchFunc, null);
            provider.AddLayer("dtm_05m");
            provider.SetImageFormat("image/png");
            provider.CRS = "EPSG:3857";
            _ahnElevationLayer = new Mapsui.Layers.ImageLayer("AHN Elevation (DTM)")
            {
                DataSource = provider,
                Opacity = 0.65
            };
            _map.Layers.Add(_ahnElevationLayer);
            _mapControl.Map.Refresh();
            Console.WriteLine("[AhnElevation] WMS layer added -- renders dynamically as the map is panned/zoomed");

            // Real official legend graphic from the same server, confirmed
            // present via curl before wiring this in.
            var legendUrl = "https://service.pdok.nl/rws/actueel-hoogtebestand-nederland/wms/v1_0?language=dut&version=1.3.0&service=WMS&request=GetLegendGraphic&sld_version=1.1.0&layer=dtm_05m&format=image/png&STYLE=default&WIDTH=180&HEIGHT=1000";
            var legendBytes = await _groningenHttp.GetByteArrayAsync(legendUrl);
            using (var ms = new System.IO.MemoryStream(legendBytes))
            {
                AhnLegendImage.Source = new Avalonia.Media.Imaging.Bitmap(ms);
            }
            AhnLegendCard.IsVisible = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AhnElevation] Failed to load: {ex.Message}");
        }
    }

    private async void OnSentinelNdviToggled(object? s, RoutedEventArgs e)
    {
        if (_map == null || _mapControl == null) return;
        if (ChkSentinelNdvi.IsChecked != true)
        {
            if (_sentinelNdviLayer != null)
            {
                _map.Layers.Remove(_sentinelNdviLayer);
                _sentinelNdviLayer = null;
                NdviLegendCard.IsVisible = false;
                _mapControl.Map.Refresh();
            }
            return;
        }
        try
        {
            // Copernicus Data Space Ecosystem, Sentinel Hub WMS, real free account
            // (30,000 processing units/month), confirmed live via curl before
            // writing this -- correct CDSE base URL is sh.dataspace.copernicus.eu,
            // NOT the legacy services.sentinel-hub.com (which returned "Invalid
            // instance id" for the exact same instance ID). Config instance
            // "Groningen Infrastructure Inspection" already has an NDVI layer
            // predefined. EPSG:3857 confirmed supported, same as AHN. Unlike AHN,
            // this is a real time series, so a TIME date range is required --
            // using the last 60 days to maximise chance of a usable, low-cloud
            // recent image without guessing an exact cloud-free date.
            const string instanceId = "db16f964-34a8-4c4b-84a5-81fcc451e418";
            var capUrl = $"https://sh.dataspace.copernicus.eu/ogc/wms/{instanceId}?REQUEST=GetCapabilities";
            var xmlString = await _groningenHttp.GetStringAsync(capUrl);
            var xmlDoc = new System.Xml.XmlDocument();
            xmlDoc.LoadXml(xmlString);
            Func<string, System.Threading.Tasks.Task<System.IO.Stream>> fetchFunc =
                async (url) => await _groningenHttp.GetStreamAsync(url);
            var provider = new Mapsui.Providers.Wms.WmsProvider(xmlDoc, fetchFunc, null);
            provider.AddLayer("NDVI");
            provider.SetImageFormat("image/png");
            provider.CRS = "EPSG:3857";
            var end = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var start = DateTime.UtcNow.AddDays(-60).ToString("yyyy-MM-dd");
            provider.ExtraParams = new System.Collections.Generic.Dictionary<string, string>
            {
                ["TIME"] = $"{start}/{end}"
            };
            _sentinelNdviLayer = new Mapsui.Layers.ImageLayer("Sentinel-2 NDVI")
            {
                DataSource = provider,
                Opacity = 0.75
            };
            _map.Layers.Add(_sentinelNdviLayer);
            _mapControl.Map.Refresh();
            Console.WriteLine($"[SentinelNdvi] WMS layer added, time range {start}/{end} -- renders dynamically as the map is panned/zoomed");

            // Real, documented default Sentinel Hub NDVI colour ramp (from their
            // own published custom-scripts repository, matching this layer's
            // exact formula) -- built ourselves since, unlike AHN, this server
            // provides no LegendURL for this layer.
            var ndviRamp = new (double Value, string Hex, string Label)[]
            {
                (-0.5, "#0c0c0c", "Water"),
                (-0.2, "#bfbfbf", "Water/wet soil"),
                (-0.1, "#dbdbdb", "Rock/bare"),
                (0.0, "#eaeaea", "Bare soil"),
                (0.05, "#ede8b5", "Very sparse veg."),
                (0.1, "#ccc682", "Sparse veg."),
                (0.15, "#afc160", "Sparse veg."),
                (0.2, "#91bf51", "Grassland"),
                (0.3, "#70a33f", "Shrubland"),
                (0.4, "#4f892d", "Moderate veg."),
                (0.5, "#306d1c", "Healthy veg."),
                (0.6, "#0f540a", "Dense veg."),
                (1.0, "#004400", "Very dense/forest")
            };
            NdviLegendPanel.Children.Clear();
            NdviLegendPanel.Children.Add(new TextBlock { Text = "NDVI Legend", FontSize = 12, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#22c55e")), Margin = new Avalonia.Thickness(0,0,0,4) });
            foreach (var (val, hex, label) in ndviRamp.Reverse())
            {
                var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
                row.Children.Add(new Border { Width = 16, Height = 12, Background = new SolidColorBrush(Avalonia.Media.Color.Parse(hex)), BorderBrush = Brushes.White, BorderThickness = new Avalonia.Thickness(0.5) });
                row.Children.Add(new TextBlock { Text = $"{val:0.00}  {label}", FontSize = 9, Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#e2e8f0")) });
                NdviLegendPanel.Children.Add(row);
            }
            NdviLegendCard.IsVisible = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SentinelNdvi] Failed to load: {ex.Message}");
        }
    }

    private void OnLocationSearchKeyDown(object? s, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter) _ = DoLocationSearchAsync();
    }
    private async void OnLocationSearch(object? s, RoutedEventArgs e) => await DoLocationSearchAsync();

    private async System.Threading.Tasks.Task DoLocationSearchAsync()
    {
        var query = LocationSearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(query) || _map == null || _mapControl == null) return;
        try
        {
            // Real, public, no-login PDOK Locatieserver (national geocoding
            // service), confirmed live via curl before writing this. The
            // suggest endpoint already includes a lat/lon centroid directly,
            // no separate lookup call needed for simple search-and-jump.
            var url = $"https://api.pdok.nl/bzk/locatieserver/search/v3_1/suggest?q={Uri.EscapeDataString(query)}&fl=id,weergavenaam,centroide_ll&rows=1";
            var json = await _groningenHttp.GetStringAsync(url);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var docs = doc.RootElement.GetProperty("response").GetProperty("docs");
            if (docs.GetArrayLength() == 0)
            {
                MissionStatusText.Text = $"No location found for '{query}'.";
                return;
            }
            var first = docs[0];
            var name = first.GetProperty("weergavenaam").GetString();
            var pointStr = first.GetProperty("centroide_ll").GetString(); // "POINT(lon lat)"
            var inner = pointStr!.Substring(pointStr.IndexOf('(') + 1).TrimEnd(')');
            var parts = inner.Split(' ');
            double lon = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            double lat = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            var (x, y) = SphericalMercator.FromLonLat(lon, lat);
            _map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), _map.Navigator.Resolutions[14]);
            _mapControl.Map.Refresh();
            MissionStatusText.Text = $"Jumped to: {name}";
        }
        catch (Exception ex)
        {
            MissionStatusText.Text = $"Search failed: {ex.Message}";
        }
    }

    private async void OnBermconditiesToggled(object? s, RoutedEventArgs e)
    {
        if (_map == null || _mapControl == null) return;
        if (ChkBermcondities.IsChecked != true)
        {
            if (_bermconditiesLayer != null)
            {
                _map.Layers.Remove(_bermconditiesLayer);
                _bermconditiesLayer = null;
                GroningenInfoCard.IsVisible = false;
                _mapControl.Map.Refresh();
            }
            return;
        }
        try
        {
            // Mobiliteit/Bermcondities, layer id=15, confirmed live via curl
            // before writing this -- polyline geometry, same pattern as Guardrails.
            var url = "https://geoservices.provinciegroningen.nl/server/rest/services/Mobiliteit/Bermcondities/MapServer/15/query?where=1%3D1&outFields=*&f=geojson&resultRecordCount=2000";
            var json = await _groningenHttp.GetStringAsync(url);
            var reader = new NetTopologySuite.IO.GeoJsonReader();
            var fc = reader.Read<NetTopologySuite.Features.FeatureCollection>(json);
            var features = new System.Collections.Generic.List<IFeature>();
            foreach (var f in fc)
            {
                if (f.Geometry == null) continue;
                var mf = new GeometryFeature { Geometry = ProjectGeometry(f.Geometry) };
                mf.Styles.Add(new VectorStyle
                {
                    Line = new Mapsui.Styles.Pen(new Mapsui.Styles.Color(234, 88, 12), 2.5f)
                });
                if (f.Attributes != null)
                {
                    foreach (var attrName in f.Attributes.GetNames())
                        mf[attrName] = f.Attributes[attrName];
                }
                features.Add(mf);
            }
            if (_bermconditiesLayer != null) _map.Layers.Remove(_bermconditiesLayer);
            _bermconditiesLayer = new MemoryLayer { Name = "Groningen Bermcondities", Features = features, IsMapInfoLayer = true };
            _map.Layers.Add(_bermconditiesLayer);
            var extent = _bermconditiesLayer.Extent;
            if (extent != null) _map.Navigator.ZoomToBox(extent, MBoxFit.Fit);
            _mapControl.Map.Refresh();
            Console.WriteLine($"[Bermcondities] Loaded {features.Count} segments from live province ArcGIS server");
            if (!_groningenInfoWired)
            {
                _groningenInfoWired = true;
                _map.Info += OnGroningenMapInfo;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Bermcondities] Failed to load: {ex.Message}");
        }
    }
    private async void OnDuikersToggled(object? s, RoutedEventArgs e)
    {
        if (_map == null || _mapControl == null) return;
        if (ChkDuikers.IsChecked != true)
        {
            if (_duikersLayer != null)
            {
                _map.Layers.Remove(_duikersLayer);
                _duikersLayer = null;
                GroningenInfoCard.IsVisible = false;
                _mapControl.Map.Refresh();
            }
            return;
        }
        try
        {
            // Mobiliteit/Duikers, layer id=1, confirmed live via curl
            // before writing this -- polyline geometry, same pattern as Guardrails.
            var url = "https://geoservices.provinciegroningen.nl/server/rest/services/Mobiliteit/Duikers/MapServer/1/query?where=1%3D1&outFields=*&f=geojson&resultRecordCount=2000";
            var json = await _groningenHttp.GetStringAsync(url);
            var reader = new NetTopologySuite.IO.GeoJsonReader();
            var fc = reader.Read<NetTopologySuite.Features.FeatureCollection>(json);
            var features = new System.Collections.Generic.List<IFeature>();
            foreach (var f in fc)
            {
                if (f.Geometry == null) continue;
                var mf = new GeometryFeature { Geometry = ProjectGeometry(f.Geometry) };
                mf.Styles.Add(new VectorStyle
                {
                    Line = new Mapsui.Styles.Pen(new Mapsui.Styles.Color(6, 182, 212), 2.5f)
                });
                if (f.Attributes != null)
                {
                    foreach (var attrName in f.Attributes.GetNames())
                        mf[attrName] = f.Attributes[attrName];
                }
                features.Add(mf);
            }
            if (_duikersLayer != null) _map.Layers.Remove(_duikersLayer);
            _duikersLayer = new MemoryLayer { Name = "Groningen Duikers", Features = features, IsMapInfoLayer = true };
            _map.Layers.Add(_duikersLayer);
            var extent = _duikersLayer.Extent;
            if (extent != null) _map.Navigator.ZoomToBox(extent, MBoxFit.Fit);
            _mapControl.Map.Refresh();
            Console.WriteLine($"[Duikers] Loaded {features.Count} segments from live province ArcGIS server");
            if (!_groningenInfoWired)
            {
                _groningenInfoWired = true;
                _map.Info += OnGroningenMapInfo;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Duikers] Failed to load: {ex.Message}");
        }
    }
    private async void OnGeluidsschermenToggled(object? s, RoutedEventArgs e)
    {
        if (_map == null || _mapControl == null) return;
        if (ChkGeluidsschermen.IsChecked != true)
        {
            if (_geluidsschermenLayer != null)
            {
                _map.Layers.Remove(_geluidsschermenLayer);
                _geluidsschermenLayer = null;
                GroningenInfoCard.IsVisible = false;
                _mapControl.Map.Refresh();
            }
            return;
        }
        try
        {
            // Mobiliteit/Geluidsschermen, layer id=1, confirmed live via curl
            // before writing this -- polyline geometry, same pattern as Guardrails.
            var url = "https://geoservices.provinciegroningen.nl/server/rest/services/Mobiliteit/Geluidsschermen/MapServer/1/query?where=1%3D1&outFields=*&f=geojson&resultRecordCount=2000";
            var json = await _groningenHttp.GetStringAsync(url);
            var reader = new NetTopologySuite.IO.GeoJsonReader();
            var fc = reader.Read<NetTopologySuite.Features.FeatureCollection>(json);
            var features = new System.Collections.Generic.List<IFeature>();
            foreach (var f in fc)
            {
                if (f.Geometry == null) continue;
                var mf = new GeometryFeature { Geometry = ProjectGeometry(f.Geometry) };
                mf.Styles.Add(new VectorStyle
                {
                    Line = new Mapsui.Styles.Pen(new Mapsui.Styles.Color(168, 85, 247), 2.5f)
                });
                if (f.Attributes != null)
                {
                    foreach (var attrName in f.Attributes.GetNames())
                        mf[attrName] = f.Attributes[attrName];
                }
                features.Add(mf);
            }
            if (_geluidsschermenLayer != null) _map.Layers.Remove(_geluidsschermenLayer);
            _geluidsschermenLayer = new MemoryLayer { Name = "Groningen Geluidsschermen", Features = features, IsMapInfoLayer = true };
            _map.Layers.Add(_geluidsschermenLayer);
            var extent = _geluidsschermenLayer.Extent;
            if (extent != null) _map.Navigator.ZoomToBox(extent, MBoxFit.Fit);
            _mapControl.Map.Refresh();
            Console.WriteLine($"[Geluidsschermen] Loaded {features.Count} segments from live province ArcGIS server");
            if (!_groningenInfoWired)
            {
                _groningenInfoWired = true;
                _map.Info += OnGroningenMapInfo;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Geluidsschermen] Failed to load: {ex.Message}");
        }
    }

        private async void OnGroningenCrackingToggled(object? s, RoutedEventArgs e)
    {
        if (_map == null || _mapControl == null) return;
        if (ChkGroningenCracking.IsChecked != true)
        {
            if (_groningenCrackingLayer != null)
            {
                _map.Layers.Remove(_groningenCrackingLayer);
                _groningenCrackingLayer = null;
                GroningenInfoCard.IsVisible = false;
                _mapControl.Map.Refresh();
            }
            return;
        }
        try
        {
            // Hosted/Vergelijking_weginspecties, layer id=102 ("Vergelijking
            // scheurvorming" / Cracking Comparison) -- real official CROW-standard
            // pavement condition survey data, confirmed live via curl before
            // writing this. Polygon geometry, same pattern as Roads.
            var url = "https://geoservices.provinciegroningen.nl/server/rest/services/Hosted/Vergelijking_weginspecties/FeatureServer/102/query?where=1%3D1&outFields=*&f=geojson&resultRecordCount=2000";
            var json = await _groningenHttp.GetStringAsync(url);
            var reader = new NetTopologySuite.IO.GeoJsonReader();
            var fc = reader.Read<NetTopologySuite.Features.FeatureCollection>(json);
            var features = new System.Collections.Generic.List<IFeature>();
            foreach (var f in fc)
            {
                if (f.Geometry == null) continue;
                var mf = new GeometryFeature { Geometry = ProjectGeometry(f.Geometry) };
                mf.Styles.Add(new VectorStyle
                {
                    Fill = new Mapsui.Styles.Brush(new Mapsui.Styles.Color(249, 115, 22, 130)),
                    Outline = new Mapsui.Styles.Pen(new Mapsui.Styles.Color(249, 115, 22), 1.5f)
                });
                if (f.Attributes != null)
                {
                    foreach (var attrName in f.Attributes.GetNames())
                        mf[attrName] = f.Attributes[attrName];
                }
                features.Add(mf);
            }
            if (_groningenCrackingLayer != null) _map.Layers.Remove(_groningenCrackingLayer);
            _groningenCrackingLayer = new MemoryLayer { Name = "Groningen Road Cracking (official)", Features = features, IsMapInfoLayer = true, Opacity = 0.7 };
            _map.Layers.Add(_groningenCrackingLayer);
            var extent = _groningenCrackingLayer.Extent;
            if (extent != null) _map.Navigator.ZoomToBox(extent, MBoxFit.Fit);
            _mapControl.Map.Refresh();
            Console.WriteLine($"[GroningenCracking] Loaded {features.Count} cracking-comparison segments from live province ArcGIS server");
            if (!_groningenInfoWired)
            {
                _groningenInfoWired = true;
                _map.Info += OnGroningenMapInfo;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GroningenCracking] Failed to load: {ex.Message}");
        }
    }
}
