using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using static InfraDroneDesktop.Services.AssetMonitorService;
using MBrush = Mapsui.Styles.Brush;
using MColor = Mapsui.Styles.Color;
using MPen = Mapsui.Styles.Pen;
using AColor = Avalonia.Media.Color;
using APen = Avalonia.Media.Pen;

namespace InfraDroneDesktop.Views;

/// <summary>
/// User-friendly view of the radar points of one road stretch:
/// plain summary, satellite-direction switch, the points on a real street map (click a point for details),
/// and an "along the road" chart that shows WHERE on the stretch the movement is.
/// Every dot is a real EGMS Calibrated measurement point; nothing is interpolated.
/// </summary>
internal sealed class RoadEvidenceView : UserControl
{
    internal static readonly (double Max, string Hex, string Name, string Word, string Meaning)[] Bins =
    {
        (-4, "#ef4444", "RED", "sinking fast", "Sinking fast — moves away from the satellite by more than 4 mm per year"),
        (-2, "#f59e0b", "ORANGE", "sinking", "Sinking — moves away from the satellite by 2 to 4 mm per year"),
        (2, "#22d3ee", "LIGHT BLUE", "stable", "Stable — less than 2 mm per year up or down (normal)"),
        (double.MaxValue, "#3b82f6", "DARK BLUE", "rising", "Rising — moves towards the satellite by more than 2 mm per year")
    };
    internal static string VelHex(double v) => Bins.First(b => v <= b.Max).Hex;
    internal static string VelWord(double v) => Bins.First(b => v <= b.Max).Word;

    private readonly BridgeEvidence _ev;
    private readonly List<string> _tracks;
    private string? _track;                     // null = all directions
    private readonly StackPanel _summary = new() { Spacing = 4 };
    private readonly WrapPanel _chips = new();
    private readonly TextBlock _pointInfo = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
    private readonly Map _map = new();
    private readonly MemoryLayer _ringLayer = new() { Name = "ground points", IsMapInfoLayer = true, Style = null };
    private readonly MemoryLayer _deckLayer = new() { Name = "road points", IsMapInfoLayer = true, Style = null };
    private readonly ProfileChart _profile;
    private readonly Mapsui.UI.Avalonia.MapControl _mapControl;
    private Point? _dragStart, _dragLast;
    private bool _dragged;
    private MRect? _home;
    private readonly TextBlock _profileNote = Am.M("");

    public RoadEvidenceView(BridgeEvidence ev, string strongestTrack)
    {
        _ev = ev;
        _tracks = ev.Deck.Select(p => p.Track).Concat(ev.Ring.Select(p => p.Track)).Distinct().OrderBy(t => t).ToList();
        _track = _tracks.Contains(strongestTrack) ? strongestTrack : null;
        _profile = new ProfileChart { Height = 260 };
        _profile.Hovered += text => _profileNote.Text = text ?? DefaultProfileNote();

        // ---- map: street map + road line + radar points
        _map.Layers.Add(OpenStreetMap.CreateTileLayer("OSM Base"));
        var roadLayer = new MemoryLayer { Name = "road", Style = null, Features = RoadFeatures() };
        _map.Layers.Add(roadLayer);
        _map.Layers.Add(_ringLayer);
        _map.Layers.Add(_deckLayer);
        _home = Box();
        if (_home != null) { var hb = _home; _map.Home = n => n.ZoomToBox(hb, MBoxFit.Fit); }
        _mapControl = new Mapsui.UI.Avalonia.MapControl { Map = _map };
        // The page around the map scrolls; handle the mouse on the map ourselves so zoom and pan always work.
        _mapControl.AddHandler(PointerWheelChangedEvent, OnMapWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _mapControl.AddHandler(PointerPressedEvent, OnMapPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _mapControl.AddHandler(PointerMovedEvent, OnMapMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _mapControl.AddHandler(PointerReleasedEvent, OnMapReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var mapArea = new Grid { Height = 460 };
        mapArea.Children.Add(_mapControl);
        mapArea.Children.Add(MapLegendOverlay());
        mapArea.Children.Add(MapButtons(mapArea));

        // ---- big, plain legend
        var legend = new StackPanel { Spacing = 6 };
        legend.Children.Add(Am.H("🎨 What the colours mean"));
        foreach (var b in Bins)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            line.Children.Add(new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(AColor.Parse(b.Hex)),
                                           BorderBrush = Brushes.White, BorderThickness = new Thickness(2) });
            line.Children.Add(new TextBlock { Text = b.Name, Width = 90, FontWeight = FontWeight.Bold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                                              Foreground = new SolidColorBrush(AColor.Parse(b.Hex)) });
            var meaning = Am.P(b.Meaning);
            meaning.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(meaning);
            legend.Children.Add(line);
        }
        legend.Children.Add(Am.H("🔵 What the dots mean"));
        legend.Children.Add(Am.P("● Big dot with a white ring = a measurement ON the road surface."));
        legend.Children.Add(Am.P("• Small dot = a measurement on the ground AROUND the road (the reference: what the area does anyway)."));
        legend.Children.Add(Am.P("━ White line = the road stretch itself."));
        legend.Children.Add(Am.M("A road is a concern when its big dots are clearly more red/orange than the small dots around it: then the road sinks faster than its surroundings. " +
                                 "“Moving away from the satellite” almost always means sinking; the satellite looks at the ground at an angle, so the numbers are not purely up-down."));

        var infoBox = new Border
        {
            Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(AColor.Parse("#cc0b1220")),
            Child = _pointInfo
        };
        _pointInfo.Text = "👆 Click any dot on the map to see what it measures, in plain words.";

        // ---- layout
        var root = new StackPanel { Spacing = 10 };
        root.Children.Add(Am.H("📡 Radar points on and around this road"));
        root.Children.Add(_summary);
        root.Children.Add(Am.M("Satellite view — each satellite looks from its own angle, so compare points from one view at a time:"));
        root.Children.Add(_chips);
        root.Children.Add(Am.M("🖱 Scroll the mouse wheel on the map to zoom · hold the left button and drag to move · click a dot to read it · or use the buttons on the map."));
        root.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = mapArea });
        root.Children.Add(infoBox);
        root.Children.Add(Am.Card(legend));
        root.Children.Add(Am.H("📏 Along the road — where does it move?"));
        root.Children.Add(Am.M("Left to right = distance along the road stretch. Up and down = speed in mm per year: dots BELOW the zero line move away from the satellite (usually sinking), " +
                               "dots ABOVE it move towards it. The chart has its own labels in the corner."));
        root.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _profile });
        root.Children.Add(_profileNote);
        Content = root;

        BuildChips();
        Apply();
    }

    private string Friendly(string track)
    {
        int i = _tracks.IndexOf(track);
        string letter = i >= 0 && i < 26 ? ((char)('A' + i)).ToString() : "?";
        return $"satellite view {letter} ({track})";
    }

    private void BuildChips()
    {
        _chips.Children.Clear();
        void Chip(string label, string? track)
        {
            var b = new ToggleButton { Content = label, IsChecked = _track == track, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(10, 4) };
            b.Click += (_, _) => { _track = track; BuildChips(); Apply(); };
            _chips.Children.Add(b);
        }
        foreach (var t in _tracks) Chip("🛰 " + char.ToUpper(Friendly(t)[0]) + Friendly(t)[1..], t);
        Chip("All views together", null);
    }

    private IEnumerable<RadarPoint> Deck => _ev.Deck.Where(p => _track == null || p.Track == _track);
    private IEnumerable<RadarPoint> Ring => _ev.Ring.Where(p => _track == null || p.Track == _track);

    private static double? Median(IEnumerable<double> v)
    {
        var a = v.OrderBy(x => x).ToArray();
        if (a.Length == 0) return null;
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    private static string S(double? v) => v.HasValue ? v.Value.ToString("+0.0;−0.0;0.0", CultureInfo.InvariantCulture) : "—";

    private void Apply()
    {
        var deck = Deck.ToList();
        var ring = Ring.ToList();
        double? dm = Median(deck.Select(p => p.Vel)), rm = Median(ring.Select(p => p.Vel));

        // ---- plain summary
        _summary.Children.Clear();
        var row = new WrapPanel();
        row.Children.Add(Chip($"🛣 On the road: {deck.Count} points · typical {S(dm)} mm/yr ({(dm.HasValue ? VelWord(dm.Value) : "no data")})", dm.HasValue ? VelHex(dm.Value) : "#64748b"));
        row.Children.Add(Chip($"🌳 Ground around: {ring.Count} points · typical {S(rm)} mm/yr ({(rm.HasValue ? VelWord(rm.Value) : "no data")})", rm.HasValue ? VelHex(rm.Value) : "#64748b"));
        if (dm.HasValue && rm.HasValue)
        {
            double diff = dm.Value - rm.Value;
            string verdict = Math.Abs(diff) >= 4 ? "road moves much more than its surroundings" : Math.Abs(diff) >= 2 ? "road moves more than its surroundings" : "road moves like its surroundings";
            row.Children.Add(Chip($"➜ Difference: {S(diff)} mm/yr — {verdict}", Math.Abs(diff) >= 4 ? "#ef4444" : Math.Abs(diff) >= 2 ? "#f59e0b" : "#0d9e75"));
        }
        _summary.Children.Add(row);
        if (_track == null)
            _summary.Children.Add(Am.M("“All views together” mixes satellites that look from different angles — use it to see where points are, " +
                                       "and pick one satellite view for exact numbers."));

        // ---- map
        _deckLayer.Features = deck.Where(p => p.Lon.HasValue).Select(p => PointFeature(p, true)).ToList();
        _ringLayer.Features = ring.Where(p => p.Lon.HasValue).Select(p => PointFeature(p, false)).ToList();
        _deckLayer.DataHasChanged(); _ringLayer.DataHasChanged();
        _map.Refresh();

        // ---- profile
        _profile.SetData(deck, ring, _ev.LengthM ?? deck.Concat(ring).Select(p => p.Along ?? 0).DefaultIfEmpty(100).Max(), rm);
        _profileNote.Text = DefaultProfileNote();
    }

    private string DefaultProfileNote()
    {
        var z = _profile.Zone;
        return z == null ? "Hover a dot for its exact value."
            : $"⚠ Most movement is concentrated between {z.Value.From:0} m and {z.Value.To:0} m along this stretch " +
              $"({z.Value.Count} road points there move at least 2 mm/yr more than the ground). Hover a dot for its exact value.";
    }

    private static Control Chip(string text, string hex) => new Border
    {
        Padding = new Thickness(10, 5), CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 8, 6),
        Background = new SolidColorBrush(AColor.Parse(hex), 0.18), BorderBrush = new SolidColorBrush(AColor.Parse(hex)), BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(AColor.Parse(hex)) }
    };

    // ---- map interaction (own handling: works inside the scrolling page)
    private (double CX, double CY, double Res, double W, double H) View()
    {
        var v = _map.Navigator.Viewport;
        return (v.CenterX, v.CenterY, v.Resolution, v.Width, v.Height);
    }

    private void ZoomAt(Point screen, double factor)
    {
        var (cx, cy, res, w, h) = View();
        if (res <= 0) return;
        double wx = cx + (screen.X - w / 2) * res, wy = cy - (screen.Y - h / 2) * res;   // world point under the mouse
        double nr = Math.Clamp(res / factor, 0.05, 200);
        _map.Navigator.CenterOnAndZoomTo(new MPoint(wx - (screen.X - w / 2) * nr, wy + (screen.Y - h / 2) * nr), nr, 150);
        _map.Refresh();
    }

    private void OnMapWheel(object? sender, PointerWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(_mapControl), e.Delta.Y > 0 ? 1.4 : 1 / 1.4);
        e.Handled = true;   // do not scroll the page
    }

    private void OnMapPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_mapControl).Properties.IsLeftButtonPressed) return;
        _dragStart = _dragLast = e.GetPosition(_mapControl);
        _dragged = false;
        e.Pointer.Capture(_mapControl);
        e.Handled = true;
    }

    private void OnMapMoved(object? sender, PointerEventArgs e)
    {
        if (_dragLast == null) return;
        var p = e.GetPosition(_mapControl);
        double dx = p.X - _dragLast.Value.X, dy = p.Y - _dragLast.Value.Y;
        if (!_dragged && _dragStart.HasValue && Math.Abs(p.X - _dragStart.Value.X) + Math.Abs(p.Y - _dragStart.Value.Y) < 4) return;
        _dragged = true;
        var (cx, cy, res, _, _) = View();
        _map.Navigator.CenterOnAndZoomTo(new MPoint(cx - dx * res, cy + dy * res), res, 0);
        _map.Refresh();
        _dragLast = p;
        e.Handled = true;
    }

    private void OnMapReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragLast == null) return;
        var p = e.GetPosition(_mapControl);
        if (!_dragged) ShowNearest(p);
        _dragStart = _dragLast = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>Click on the map -> nearest radar point within 14 pixels.</summary>
    private void ShowNearest(Point screen)
    {
        var (cx, cy, res, w, h) = View();
        if (res <= 0) return;
        RadarPoint? best = null; bool bestOnRoad = false; double bestD = 14 * 14;
        void Check(IEnumerable<RadarPoint> pts, bool onRoad)
        {
            foreach (var p in pts.Where(p => p.Lon.HasValue))
            {
                var t = SphericalMercator.FromLonLat(p.Lon!.Value, p.Lat!.Value);
                double sx = (t.Item1 - cx) / res + w / 2, sy = (cy - t.Item2) / res + h / 2;
                double d = (sx - screen.X) * (sx - screen.X) + (sy - screen.Y) * (sy - screen.Y);
                if (onRoad) d *= 0.6;   // prefer road points when they overlap ground points
                if (d < bestD) { bestD = d; best = p; bestOnRoad = onRoad; }
            }
        }
        Check(Deck, true);
        Check(Ring, false);
        if (best == null) { _pointInfo.Text = "No measurement point there — click closer to a dot, or zoom in."; return; }
        var v = best.Vel;
        _pointInfo.Text = $"{(bestOnRoad ? "🛣 Measurement ON the road" : "🌳 Measurement on the ground around the road")}: " +
                          $"{v.ToString("+0.0;−0.0;0.0", CultureInfo.InvariantCulture)} mm per year → {VelWord(v).ToUpperInvariant()} " +
                          $"({(v < 0 ? "moves away from the satellite, usually sinking" : "moves towards the satellite")}) · {Friendly(best.Track)}" +
                          (best.Along.HasValue ? $" · {best.Along.Value:0} m along the road" : "");
    }

    private Control MapButtons(Grid mapArea)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10), Spacing = 6 };
        Button B(string text, string tip, Action a)
        {
            var b = new Button { Content = text, FontSize = 16, Width = 44, Height = 40, HorizontalContentAlignment = HorizontalAlignment.Center,
                                 Background = new SolidColorBrush(AColor.Parse("#e60b1220")), Foreground = Brushes.White };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => a();
            return b;
        }
        panel.Children.Add(B("➕", "Zoom in", () => { var (_, _, _, w, h) = View(); ZoomAt(new Point(w / 2, h / 2), 1.6); }));
        panel.Children.Add(B("➖", "Zoom out", () => { var (_, _, _, w, h) = View(); ZoomAt(new Point(w / 2, h / 2), 1 / 1.6); }));
        panel.Children.Add(B("⤢", "Show the whole stretch again", () => { if (_home != null) { _map.Navigator.ZoomToBox(_home, MBoxFit.Fit); _map.Refresh(); } }));
        panel.Children.Add(B("⛶", "Bigger / smaller map", () => { mapArea.Height = mapArea.Height > 500 ? 460 : 760; }));
        return panel;
    }

    private static Control MapLegendOverlay()
    {
        var sp = new StackPanel { Spacing = 3 };
        foreach (var b in Bins)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            line.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(AColor.Parse(b.Hex)), VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(new TextBlock { Text = $"{b.Name}: {b.Word}", FontSize = 12, Foreground = Brushes.White });
            sp.Children.Add(line);
        }
        sp.Children.Add(new TextBlock { Text = "● big = on the road   • small = around", FontSize = 11, Foreground = new SolidColorBrush(AColor.Parse("#cbd5e1")) });
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10),
            Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(AColor.Parse("#d90b1220")),
            IsHitTestVisible = false, Child = sp
        };
    }

    // ---- Mapsui features
    private static readonly Dictionary<string, IStyle> Styles = new();
    private static IStyle PointStyle(double v, bool onRoad)
    {
        var key = VelHex(v) + (onRoad ? "R" : "G");
        if (!Styles.TryGetValue(key, out var st))
        {
            var c = AColor.Parse(VelHex(v));
            Styles[key] = st = onRoad
                ? new SymbolStyle { Fill = new MBrush(new MColor(c.R, c.G, c.B)), Outline = new MPen(MColor.White, 2f), SymbolScale = 0.55 }
                : new SymbolStyle { Fill = new MBrush(new MColor(c.R, c.G, c.B, 170)), Outline = new MPen(new MColor(0, 0, 0, 120), 0.5f), SymbolScale = 0.28 };
        }
        return st;
    }

    private static IFeature PointFeature(RadarPoint p, bool onRoad)
    {
        var (x, y) = SphericalMercator.FromLonLat(p.Lon!.Value, p.Lat!.Value);
        var f = new GeometryFeature { Geometry = new NetTopologySuite.Geometries.Point(x, y) };
        f["vel"] = p.Vel;
        f["track"] = p.Track;
        if (p.Along.HasValue) f["along"] = p.Along.Value;
        f.Styles.Add(PointStyle(p.Vel, onRoad));
        return f;
    }

    private List<IFeature> RoadFeatures()
    {
        var list = new List<IFeature>();
        foreach (var line in _ev.OutlineLonLat.Where(l => l.Count >= 2))
        {
            var coords = line.Select(q => { var (x, y) = SphericalMercator.FromLonLat(q.Lon, q.Lat); return new NetTopologySuite.Geometries.Coordinate(x, y); }).ToArray();
            var f = new GeometryFeature { Geometry = new NetTopologySuite.Geometries.LineString(coords) };
            f.Styles.Add(new VectorStyle { Line = new MPen(new MColor(15, 23, 42), 9f) });
            f.Styles.Add(new VectorStyle { Line = new MPen(MColor.White, 5f) });
            list.Add(f);
        }
        return list;
    }

    private MRect? Box()
    {
        var pts = _ev.Deck.Concat(_ev.Ring).Where(p => p.Lon.HasValue).Select(p => { var t = SphericalMercator.FromLonLat(p.Lon!.Value, p.Lat!.Value); return (X: t.Item1, Y: t.Item2); })
            .Concat(_ev.OutlineLonLat.SelectMany(l => l).Select(q => { var t = SphericalMercator.FromLonLat(q.Lon, q.Lat); return (X: t.Item1, Y: t.Item2); })).ToList();
        if (pts.Count == 0) return null;
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        double pad = Math.Max(60, Math.Max(maxX - minX, maxY - minY) * 0.08);
        return new MRect(minX - pad, minY - pad, maxX + pad, maxY + pad);
    }
}

/// <summary>"Along the road" chart: distance along the stretch (x) vs velocity (y). Real points only.</summary>
internal sealed class ProfileChart : Control
{
    private List<RadarPoint> _deck = new(), _ring = new();
    private double _length = 100;
    private double? _groundMedian;
    private (double X, double Y, RadarPoint P)? _hot;
    private readonly List<(Point Pos, RadarPoint P)> _drawn = new();
    public (double From, double To, int Count)? Zone { get; private set; }
    public event Action<string?>? Hovered;

    private static readonly IBrush Bg = new SolidColorBrush(AColor.Parse("#0b1220"));
    private static readonly IPen GridPen = new APen(new SolidColorBrush(AColor.Parse("#1e293b")), 1);
    private static readonly IPen ZeroPen = new APen(new SolidColorBrush(AColor.Parse("#94a3b8")), 1.2);
    private static readonly IPen GroundPen = new APen(new SolidColorBrush(AColor.Parse("#0d9e75")), 1.6) { DashStyle = DashStyle.Dash };
    private static readonly IBrush GroundDot = new SolidColorBrush(AColor.Parse("#94a3b8"), 0.45);
    private static readonly IBrush ZoneBrush = new SolidColorBrush(AColor.Parse("#ef4444"), 0.13);

    public ProfileChart() { Cursor = new Cursor(StandardCursorType.Cross); }

    public void SetData(List<RadarPoint> deck, List<RadarPoint> ring, double length, double? groundMedian)
    {
        _deck = deck.Where(p => p.Along.HasValue).OrderBy(p => p.Along).ToList();
        _ring = ring.Where(p => p.Along.HasValue).ToList();
        _length = Math.Max(1, length);
        _groundMedian = groundMedian;
        // zone = range along the road where road points move ≥ 2 mm/yr more than the typical ground (at least 2 such points)
        Zone = null;
        if (_groundMedian.HasValue)
        {
            var hot = _deck.Where(p => Math.Abs(p.Vel - _groundMedian.Value) >= 2).ToList();
            if (hot.Count >= 2) Zone = (hot.Min(p => p.Along!.Value), hot.Max(p => p.Along!.Value), hot.Count);
        }
        InvalidateVisual();
    }

    private static FormattedText Txt(string s, double size = 11, string hex = "#94a3b8") =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, new SolidColorBrush(AColor.Parse(hex)));

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.DrawRectangle(Bg, null, new Rect(size));
        _drawn.Clear();
        const double left = 52, right = 16, top = 14, bottom = 30;
        double w = Math.Max(10, size.Width - left - right), h = Math.Max(10, size.Height - top - bottom);
        var all = _deck.Concat(_ring).Select(p => p.Vel).ToList();
        double vmin = Math.Min(-6, (all.Count > 0 ? all.Min() : 0) - 1), vmax = Math.Max(3, (all.Count > 0 ? all.Max() : 0) + 1);
        vmin = Math.Floor(vmin / 2) * 2; vmax = Math.Ceiling(vmax / 2) * 2;
        double X(double along) => left + along / _length * w;
        double Y(double v) => top + (vmax - v) / (vmax - vmin) * h;

        // grid + axis labels
        for (double v = vmin; v <= vmax + 1e-9; v += 2)
        {
            ctx.DrawLine(Math.Abs(v) < 1e-9 ? ZeroPen : GridPen, new Point(left, Y(v)), new Point(left + w, Y(v)));
            var t = Txt(v.ToString("+0;−0;0", CultureInfo.InvariantCulture));
            ctx.DrawText(t, new Point(left - 8 - t.Width, Y(v) - 7));
        }
        double step = _length <= 250 ? 50 : _length <= 600 ? 100 : _length <= 1500 ? 250 : 500;
        for (double a = 0; a <= _length + 1e-9; a += step)
        {
            ctx.DrawLine(GridPen, new Point(X(a), top), new Point(X(a), top + h));
            var t = Txt($"{a:0} m");
            ctx.DrawText(t, new Point(X(a) - t.Width / 2, top + h + 8));
        }
        ctx.DrawText(Txt("mm/yr", 10), new Point(4, top - 2));

        if (Zone.HasValue)
        {
            double x0 = X(Zone.Value.From) - 6, x1 = X(Zone.Value.To) + 6;
            ctx.DrawRectangle(ZoneBrush, null, new Rect(x0, top, Math.Max(12, x1 - x0), h));
        }
        foreach (var p in _ring)
            ctx.DrawEllipse(GroundDot, null, new Point(X(p.Along!.Value), Y(p.Vel)), 2.2, 2.2);
        if (_groundMedian.HasValue)
            ctx.DrawLine(GroundPen, new Point(left, Y(_groundMedian.Value)), new Point(left + w, Y(_groundMedian.Value)));
        foreach (var p in _deck)
        {
            var pos = new Point(X(p.Along!.Value), Y(p.Vel));
            ctx.DrawEllipse(new SolidColorBrush(AColor.Parse(RoadEvidenceView.VelHex(p.Vel))), new APen(Brushes.White, 1.2), pos, 5, 5);
            _drawn.Add((pos, p));
        }
        if (_hot.HasValue)
            ctx.DrawEllipse(null, new APen(Brushes.White, 2), new Point(_hot.Value.X, _hot.Value.Y), 9, 9);

        // ---- labels inside the chart
        if (Zone.HasValue)
        {
            var zl = Txt("⚠ sinking zone", 11, "#fca5a5");
            ctx.DrawText(zl, new Point(Math.Max(left + 2, X(Zone.Value.From) - 4), top + 2));
        }
        ctx.DrawText(Txt("▲ moves towards the satellite", 10, "#93c5fd"), new Point(left + 6, top + 2));
        ctx.DrawText(Txt("▼ moves away (usually sinking)", 10, "#fca5a5"), new Point(left + 6, top + h - 14));
        // legend box, top-right
        double lx = left + w - 190, ly = top + 6;
        ctx.DrawRectangle(new SolidColorBrush(AColor.Parse("#e60b1220")), new APen(new SolidColorBrush(AColor.Parse("#334155")), 1), new Rect(lx, ly, 184, 76), 6, 6);
        ctx.DrawEllipse(new SolidColorBrush(AColor.Parse("#ef4444")), new APen(Brushes.White, 1.2), new Point(lx + 12, ly + 12), 5, 5);
        ctx.DrawText(Txt("measurement on the road", 11, "#e2e8f0"), new Point(lx + 24, ly + 4));
        ctx.DrawEllipse(GroundDot, null, new Point(lx + 12, ly + 30), 2.5, 2.5);
        ctx.DrawText(Txt("measurement on the ground", 11, "#e2e8f0"), new Point(lx + 24, ly + 22));
        ctx.DrawLine(GroundPen, new Point(lx + 4, ly + 48), new Point(lx + 20, ly + 48));
        ctx.DrawText(Txt("typical ground movement", 11, "#e2e8f0"), new Point(lx + 24, ly + 40));
        ctx.DrawRectangle(ZoneBrush, new APen(new SolidColorBrush(AColor.Parse("#ef4444")), 1), new Rect(lx + 5, ly + 60, 14, 10));
        ctx.DrawText(Txt("where the road sinks most", 11, "#e2e8f0"), new Point(lx + 24, ly + 58));
        if (_deck.Count == 0)
            ctx.DrawText(Txt("No road points for this satellite view.", 13, "#cbd5e1"), new Point(left + 12, top + 12));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        (Point Pos, RadarPoint P)? best = null;
        double bestD = 12 * 12;
        foreach (var d in _drawn)
        {
            double dd = (d.Pos.X - pos.X) * (d.Pos.X - pos.X) + (d.Pos.Y - pos.Y) * (d.Pos.Y - pos.Y);
            if (dd < bestD) { bestD = dd; best = d; }
        }
        _hot = best.HasValue ? (best.Value.Pos.X, best.Value.Pos.Y, best.Value.P) : null;
        Hovered?.Invoke(best.HasValue
            ? $"Road point at {best.Value.P.Along:0} m: {best.Value.P.Vel.ToString("+0.0;−0.0;0.0", CultureInfo.InvariantCulture)} mm/yr → {RoadEvidenceView.VelWord(best.Value.P.Vel).ToUpperInvariant()}" +
              (_groundMedian.HasValue ? $" (ground around: {_groundMedian.Value.ToString("+0.0;−0.0;0.0", CultureInfo.InvariantCulture)})" : "") +
              $" · {best.Value.P.Track}"
            : null);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hot = null;
        Hovered?.Invoke(null);
        InvalidateVisual();
    }
}
