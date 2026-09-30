using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
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

// =====================================================================================
// 🌍 Earthquakes — province-wide view of the official KNMI earthquake catalogue,
// linked to the bridges and road stretches the satellite flagged.
// =====================================================================================
internal sealed class AmQuakesPanel : AmPanel
{
    internal static readonly (double Min, string Hex, string Name)[] MagBins =
    {
        (2.5, "#ef4444", "strong for Groningen (M 2.5 or more) — often felt, can cause damage"),
        (1.5, "#f59e0b", "felt by some people (M 1.5 – 2.5)"),
        (double.MinValue, "#60a5fa", "small, usually not felt (below M 1.5)")
    };
    internal static string MagHex(double? m) => MagBins.First(b => (m ?? 0) >= b.Min).Hex;

    private readonly TextBlock _status = new() { FontSize = 12, Foreground = new SolidColorBrush(AColor.Parse("#94a3b8")), TextWrapping = TextWrapping.Wrap };
    private readonly Button _update;
    private readonly WrapPanel _kpis = new();
    private readonly ComboBox _period = new() { ItemsSource = new[] { "All since 1986", "Last 10 years", "Satellite period 2020–2024", "Last 12 months" }, SelectedIndex = 0, Width = 230 };
    private readonly ComboBox _minMag = new() { ItemsSource = new[] { "All magnitudes", "Magnitude 1.5 or more", "Magnitude 2.5 or more" }, SelectedIndex = 0, Width = 210 };
    private readonly CheckBox _showAssets = new() { Content = "Show flagged bridges and roads", IsChecked = true };
    private readonly Map _map = new();
    private readonly MemoryLayer _quakeLayer = new() { Name = "quakes", Style = null };
    private readonly MemoryLayer _assetLayer = new() { Name = "flagged assets", Style = null };
    private readonly Mapsui.UI.Avalonia.MapControl _mapControl;
    private readonly TextBlock _info = new() { FontSize = 13, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly YearBars _years = new() { Height = 220 };
    private readonly StackPanel _recent = new() { Spacing = 3 };
    private readonly StackPanel _assets = new() { Spacing = 6 };
    private List<AmExtras.Quake> _shown = new();
    private List<(string Label, string Kind, string Id, double Lat, double Lon)> _flagged = new();
    private Point? _dragStart, _dragLast;
    private bool _dragged;
    private MRect _home;

    public AmQuakesPanel()
    {
        // ---------------- hero
        var hero = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(22, 18),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(AColor.Parse("#2a1215"), 0), new GradientStop(AColor.Parse("#0b2545"), 0.6), new GradientStop(AColor.Parse("#13315c"), 1) }
            }
        };
        var hs = new StackPanel { Spacing = 8 };
        hs.Children.Add(new TextBlock { Text = "🌍 EARTHQUAKES IN AND AROUND THE PROVINCE", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(AColor.Parse("#fca5a5")) });
        hs.Children.Add(new TextBlock { Text = "Where the ground shakes — and which flagged bridges and roads are nearby", FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });
        hs.Children.Add(new TextBlock
        {
            Text = "Source: official earthquake catalogue of KNMI (Royal Netherlands Meteorological Institute). Most Groningen earthquakes are caused by gas extraction. " +
                   "Being near earthquakes does not prove they caused a bridge or road to move — but it is important context for the engineers.",
            FontSize = 13, Foreground = new SolidColorBrush(AColor.Parse("#cbd5e1")), TextWrapping = TextWrapping.Wrap
        });
        _update = Am.Btn("🔄 Update earthquakes from KNMI", async (_, _) => await UpdateAsync());
        hs.Children.Add(_update);
        hs.Children.Add(_status);
        hero.Child = hs;
        Root.Children.Add(hero);
        Root.Children.Add(_kpis);

        // ---------------- filters
        var filters = new WrapPanel();
        void F(string label, Control c)
        {
            var l = Am.P(label); l.VerticalAlignment = VerticalAlignment.Center; l.Margin = new Thickness(0, 0, 6, 0);
            filters.Children.Add(l); c.Margin = new Thickness(0, 0, 18, 6); filters.Children.Add(c);
        }
        F("Period:", _period);
        F("Strength:", _minMag);
        _showAssets.Dyn(CheckBox.ForegroundProperty, "AppTextPrimary");
        filters.Children.Add(_showAssets);
        _period.SelectionChanged += (_, _) => Apply();
        _minMag.SelectionChanged += (_, _) => Apply();
        _showAssets.IsCheckedChanged += (_, _) => Apply();

        // ---------------- map
        _map.Layers.Add(OpenStreetMap.CreateTileLayer("OSM Base"));
        _map.Layers.Add(_quakeLayer);
        _map.Layers.Add(_assetLayer);
        var (x1, y1) = SphericalMercator.FromLonLat(6.1, 52.8);
        var (x2, y2) = SphericalMercator.FromLonLat(7.3, 53.6);
        _home = new MRect(x1, y1, x2, y2);
        _map.Home = n => n.ZoomToBox(_home, MBoxFit.Fit);
        _mapControl = new Mapsui.UI.Avalonia.MapControl { Map = _map };
        _mapControl.AddHandler(PointerWheelChangedEvent, OnWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _mapControl.AddHandler(PointerPressedEvent, OnPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _mapControl.AddHandler(PointerMovedEvent, OnMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _mapControl.AddHandler(PointerReleasedEvent, OnReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var mapArea = new Grid { Height = 520 };
        mapArea.Children.Add(_mapControl);
        mapArea.Children.Add(LegendOverlay());
        mapArea.Children.Add(MapButtons(mapArea));

        var mapCard = new StackPanel { Spacing = 8 };
        mapCard.Children.Add(Am.H("🗺 Map of earthquakes"));
        mapCard.Children.Add(filters);
        mapCard.Children.Add(Am.M("🖱 Mouse wheel = zoom · hold the left button and drag = move · click a circle to read it. Bigger circle = stronger earthquake."));
        mapCard.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = mapArea });
        _info.Text = "👆 Click a circle (earthquake) or a purple square (flagged bridge/road) to see what it is.";
        mapCard.Children.Add(new Border { Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(AColor.Parse("#cc0b1220")), Child = _info });
        Root.Children.Add(Am.Card(mapCard));

        // ---------------- per year + recent
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,14,2*") };
        var yearsCard = new StackPanel { Spacing = 8 };
        yearsCard.Children.Add(Am.H("📊 Earthquakes per year"));
        yearsCard.Children.Add(Am.M("Bar height = number of earthquakes that year (with the filters above). Bar colour = the strongest earthquake of that year. Hover a bar for details."));
        yearsCard.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _years });
        var recentCard = new StackPanel { Spacing = 6 };
        recentCard.Children.Add(Am.H("🕒 Most recent"));
        recentCard.Children.Add(_recent);
        var c1 = Am.Card(yearsCard); var c2 = Am.Card(recentCard);
        c2.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(c2, 2);
        row.Children.Add(c1); row.Children.Add(c2);
        Root.Children.Add(row);

        // ---------------- flagged assets near earthquakes
        var assetsCard = new StackPanel { Spacing = 8 };
        assetsCard.Children.Add(Am.H("🎯 Flagged bridges and roads with the most earthquakes nearby"));
        assetsCard.Children.Add(Am.M("Only assets the satellite flagged (Priority or Review). Count = earthquakes within 5 km with the filters above. " +
                                     "Useful to decide which flagged assets to inspect first — not a proof of cause."));
        assetsCard.Children.Add(_assets);
        Root.Children.Add(Am.Card(assetsCard));

        AttachedToVisualTree += (_, _) => Load();
    }

    // =============================================================== data
    private void Load()
    {
        _flagged = new();
        try
        {
            foreach (var s in GroupStructure(ReadStructureResults().Where(r => r.Status is "Priority" or "Review")))
                if (s.Lat.HasValue && s.Lon.HasValue) _flagged.Add((s.ManagerLabel, "Bridge (on the structure)", s.AssetId, s.Lat.Value, s.Lon.Value));
            var seen = new HashSet<string>(_flagged.Select(f => f.Id));
            foreach (var a in GroupParts(ReadRanked().Where(r => r.Screening is "Priority" or "Review")))
                if (a.Lat.HasValue && a.Lon.HasValue && !seen.Contains(a.AssetId)) _flagged.Add((a.ManagerLabel, "Bridge (area check)", a.AssetId, a.Lat.Value, a.Lon.Value));
            foreach (var r in ReadStructureResults(RoadPointsCsv).Where(r => r.Status is "Priority" or "Review"))
                if (r.Lat.HasValue && r.Lon.HasValue) _flagged.Add(($"{r.RoadNumber} {r.RoadName}".Trim() is { Length: > 0 } n ? n : "Provincial road", "Road stretch", r.AssetId, r.Lat.Value, r.Lon.Value));
        }
        catch (Exception ex) { _status.Text = $"Could not read the flagged assets: {ex.Message}"; }

        var meta = ReadJson(AmExtras.QuakesMeta);
        _status.Text = meta == null
            ? "No earthquake data yet — click “Update earthquakes from KNMI”."
            : $"KNMI catalogue retrieved {meta["retrieved_utc"]?.ToString().Replace("T", " ")} · {meta["count"]} earthquakes in and around the province since 1986.";
        Apply();
    }

    private IEnumerable<AmExtras.Quake> Filtered()
    {
        var now = DateTime.UtcNow;
        var q = AmExtras.Quakes().AsEnumerable();
        q = _period.SelectedIndex switch
        {
            1 => q.Where(x => x.Time >= now.AddYears(-10)),
            2 => q.Where(x => x.Time.Year >= 2020 && x.Time.Year <= 2024),
            3 => q.Where(x => x.Time >= now.AddYears(-1)),
            _ => q
        };
        return _minMag.SelectedIndex switch
        {
            1 => q.Where(x => (x.Mag ?? 0) >= 1.5),
            2 => q.Where(x => (x.Mag ?? 0) >= 2.5),
            _ => q
        };
    }

    private void Apply()
    {
        var all = AmExtras.Quakes();
        _shown = Filtered().ToList();

        // KPIs (always about the full catalogue, so they don't jump with filters)
        _kpis.Children.Clear();
        if (all.Count > 0)
        {
            var big = all.Where(q => q.Mag.HasValue).OrderByDescending(q => q.Mag).FirstOrDefault();
            _kpis.Children.Add(Tile($"{all.Count:N0}", "earthquakes since 1986", "in and around the province", "#f59e0b"));
            if (big != null) _kpis.Children.Add(Tile($"M {big.Mag:0.0}", "largest recorded", $"{big.Time:d MMM yyyy} · {big.Place}", "#ef4444"));
            _kpis.Children.Add(Tile($"{all.Count(q => q.Time >= DateTime.UtcNow.AddYears(-1))}", "in the last 12 months", "still happening", "#a855f7"));
            _kpis.Children.Add(Tile($"{all.Count(q => q.Time.Year >= 2020 && q.Time.Year <= 2024)}", "during the satellite period", "2020–2024, same years as the movement data", "#22d3ee"));
            _kpis.Children.Add(Tile($"{all.Count(q => (q.Mag ?? 0) >= 2.5)}", "strong ones (M 2.5+)", "since 1986", "#ef4444"));
        }

        // map
        _quakeLayer.Features = _shown.OrderBy(q => q.Mag ?? 0).Select(QuakeFeature).ToList();
        _assetLayer.Features = _showAssets.IsChecked == true ? _flagged.Select(AssetFeature).ToList() : new List<IFeature>();
        _quakeLayer.DataHasChanged(); _assetLayer.DataHasChanged();
        _map.Refresh();

        // per year
        _years.SetData(_shown);

        // most recent
        _recent.Children.Clear();
        if (_shown.Count == 0) _recent.Children.Add(Am.M("No earthquakes with these filters."));
        foreach (var q in _shown.OrderByDescending(q => q.Time).Take(12))
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(AColor.Parse(MagHex(q.Mag))), VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(Am.P($"{q.Time:d MMM yyyy} · M {(q.Mag.HasValue ? q.Mag.Value.ToString("0.0", CultureInfo.InvariantCulture) : "—")} · {q.Place}"));
            _recent.Children.Add(line);
        }

        // flagged assets ranked by nearby earthquakes
        _assets.Children.Clear();
        if (_flagged.Count == 0) { _assets.Children.Add(Am.M("No flagged bridges or roads yet — run the satellite checks first.")); return; }
        var ranked = _flagged.Select(f =>
        {
            var near = _shown.Select(q => (q, d: AmExtras.DistanceKm(f.Lat, f.Lon, q.Lat, q.Lon))).Where(x => x.d <= 5).ToList();
            var strongest = near.Where(x => x.q.Mag.HasValue).OrderByDescending(x => x.q.Mag).FirstOrDefault();
            return (f, n: near.Count, strongest);
        }).Where(x => x.n > 0).OrderByDescending(x => x.n).Take(15).ToList();
        if (ranked.Count == 0) { _assets.Children.Add(Am.P("None of the flagged bridges or roads has an earthquake within 5 km (with these filters).")); return; }
        int rank = 0;
        foreach (var (f, n, strongest) in ranked)
        {
            rank++;
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*,Auto") };
            g.Children.Add(new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = new SolidColorBrush(AColor.Parse("#a855f7")), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = rank.ToString(), FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
            var t = new StackPanel { Spacing = 2 };
            t.Children.Add(Am.H(f.Label));
            t.Children.Add(Am.P($"{n} earthquakes within 5 km" + (strongest.q != null ? $" · strongest M {strongest.q.Mag:0.0} on {strongest.q.Time:d MMM yyyy}, {strongest.d:0.0} km away" : "")));
            t.Children.Add(Am.M(f.Kind + " · flagged by the satellite check"));
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            var ff = f;
            var go = Am.Btn("📍 Show on map", (_, _) => AmNav.ShowOnMap(ff.Id, ff.Lat, ff.Lon));
            Grid.SetColumn(go, 2);
            g.Children.Add(go);
            _assets.Children.Add(Am.Card(g));
        }
    }

    private static Control Tile(string big, string caption, string sub, string hex)
    {
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(new TextBlock { Text = big, FontSize = 30, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(AColor.Parse(hex)) });
        sp.Children.Add(Am.P(caption));
        sp.Children.Add(Am.M(sub));
        var inner = new Border { BorderThickness = new Thickness(4, 0, 0, 0), BorderBrush = new SolidColorBrush(AColor.Parse(hex)), Padding = new Thickness(12, 2, 4, 2), Child = sp };
        var card = Am.Card(inner);
        card.Width = 240;
        card.Margin = new Thickness(0, 0, 12, 12);
        return card;
    }

    private async Task UpdateAsync()
    {
        _update.IsEnabled = false;
        _status.Text = "Downloading the official KNMI earthquake catalogue…";
        try
        {
            var r = await RunScriptAsync(AmExtras.QuakesScript, line => Dispatcher.UIThread.Post(() => _status.Text = line));
            Load();
            if (r.ExitCode != 0 || r.Summary.Contains("FAILED")) _status.Text = r.Summary;
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _update.IsEnabled = true; }
    }

    // =============================================================== map features + interaction
    private static readonly Dictionary<string, IStyle> QStyles = new();
    private static IStyle QuakeStyle(double? mag)
    {
        double m = Math.Clamp(mag ?? 0, -1, 4);
        string key = MagHex(mag) + Math.Round(m * 2) / 2;
        if (!QStyles.TryGetValue(key, out var st))
        {
            var c = AColor.Parse(MagHex(mag));
            QStyles[key] = st = new SymbolStyle
            {
                Fill = new MBrush(new MColor(c.R, c.G, c.B, 150)), Outline = new MPen(MColor.White, 1f),
                SymbolScale = 0.25 + Math.Max(0, Math.Round(m * 2) / 2) * 0.28
            };
        }
        return st;
    }

    private static IFeature QuakeFeature(AmExtras.Quake q)
    {
        var (x, y) = SphericalMercator.FromLonLat(q.Lon, q.Lat);
        var f = new GeometryFeature { Geometry = new NetTopologySuite.Geometries.Point(x, y) };
        f.Styles.Add(QuakeStyle(q.Mag));
        return f;
    }

    private static readonly IStyle AssetStyle = new SymbolStyle
    {
        SymbolType = SymbolType.Rectangle, Fill = new MBrush(new MColor(168, 85, 247)), Outline = new MPen(MColor.White, 1.5f), SymbolScale = 0.4
    };
    private static IFeature AssetFeature((string Label, string Kind, string Id, double Lat, double Lon) a)
    {
        var (x, y) = SphericalMercator.FromLonLat(a.Lon, a.Lat);
        var f = new GeometryFeature { Geometry = new NetTopologySuite.Geometries.Point(x, y) };
        f.Styles.Add(AssetStyle);
        return f;
    }

    private (double CX, double CY, double Res, double W, double H) View()
    {
        var v = _map.Navigator.Viewport;
        return (v.CenterX, v.CenterY, v.Resolution, v.Width, v.Height);
    }

    private void ZoomAt(Point s, double factor)
    {
        var (cx, cy, res, w, h) = View();
        if (res <= 0) return;
        double wx = cx + (s.X - w / 2) * res, wy = cy - (s.Y - h / 2) * res;
        double nr = Math.Clamp(res / factor, 0.3, 2000);
        _map.Navigator.CenterOnAndZoomTo(new MPoint(wx - (s.X - w / 2) * nr, wy + (s.Y - h / 2) * nr), nr, 150);
        _map.Refresh();
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e) { ZoomAt(e.GetPosition(_mapControl), e.Delta.Y > 0 ? 1.4 : 1 / 1.4); e.Handled = true; }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_mapControl).Properties.IsLeftButtonPressed) return;
        _dragStart = _dragLast = e.GetPosition(_mapControl);
        _dragged = false;
        e.Pointer.Capture(_mapControl);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_dragLast == null) return;
        var p = e.GetPosition(_mapControl);
        if (!_dragged && _dragStart.HasValue && Math.Abs(p.X - _dragStart.Value.X) + Math.Abs(p.Y - _dragStart.Value.Y) < 4) return;
        _dragged = true;
        var (cx, cy, res, _, _) = View();
        _map.Navigator.CenterOnAndZoomTo(new MPoint(cx - (p.X - _dragLast.Value.X) * res, cy + (p.Y - _dragLast.Value.Y) * res), res, 0);
        _map.Refresh();
        _dragLast = p;
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragLast == null) return;
        if (!_dragged) Pick(e.GetPosition(_mapControl));
        _dragStart = _dragLast = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void Pick(Point s)
    {
        var (cx, cy, res, w, h) = View();
        if (res <= 0) return;
        (double sx, double sy) Screen(double lat, double lon) { var t = SphericalMercator.FromLonLat(lon, lat); return ((t.Item1 - cx) / res + w / 2, (cy - t.Item2) / res + h / 2); }
        double best = 16 * 16; string? text = null;
        if (_showAssets.IsChecked == true)
            foreach (var a in _flagged)
            {
                var (sx, sy) = Screen(a.Lat, a.Lon);
                double d = (sx - s.X) * (sx - s.X) + (sy - s.Y) * (sy - s.Y);
                if (d < best * 0.6) { best = d / 0.6; text = $"🟪 Flagged {a.Kind.ToLowerInvariant()}: {a.Label}"; }
            }
        foreach (var q in _shown)
        {
            var (sx, sy) = Screen(q.Lat, q.Lon);
            double d = (sx - s.X) * (sx - s.X) + (sy - s.Y) * (sy - s.Y);
            if (d < best)
            {
                best = d;
                text = $"🌍 Earthquake {q.Time:d MMM yyyy HH:mm} UTC · magnitude {(q.Mag.HasValue ? q.Mag.Value.ToString("0.0", CultureInfo.InvariantCulture) : "—")} " +
                       $"({MagBins.First(b => (q.Mag ?? 0) >= b.Min).Name}) · depth {(q.DepthKm.HasValue ? q.DepthKm.Value.ToString("0.0", CultureInfo.InvariantCulture) + " km" : "—")} · {q.Place}";
            }
        }
        _info.Text = text ?? "Nothing there — click closer to a circle or square, or zoom in.";
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
        panel.Children.Add(B("⤢", "Whole province", () => { _map.Navigator.ZoomToBox(_home, MBoxFit.Fit); _map.Refresh(); }));
        panel.Children.Add(B("⛶", "Bigger / smaller map", () => { mapArea.Height = mapArea.Height > 600 ? 520 : 820; }));
        return panel;
    }

    private static Control LegendOverlay()
    {
        var sp = new StackPanel { Spacing = 3 };
        foreach (var b in MagBins)
        {
            var l = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            l.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(AColor.Parse(b.Hex)), VerticalAlignment = VerticalAlignment.Center });
            l.Children.Add(new TextBlock { Text = b.Name, FontSize = 11.5, Foreground = Brushes.White });
            sp.Children.Add(l);
        }
        var a = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        a.Children.Add(new Border { Width = 12, Height = 12, Background = new SolidColorBrush(AColor.Parse("#a855f7")), BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5), VerticalAlignment = VerticalAlignment.Center });
        a.Children.Add(new TextBlock { Text = "flagged bridge or road (satellite check)", FontSize = 11.5, Foreground = Brushes.White });
        sp.Children.Add(a);
        sp.Children.Add(new TextBlock { Text = "bigger circle = stronger earthquake", FontSize = 11, Foreground = new SolidColorBrush(AColor.Parse("#cbd5e1")) });
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10), Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(AColor.Parse("#d90b1220")), IsHitTestVisible = false, Child = sp
        };
    }
}

/// <summary>Earthquakes per year: bar height = count, bar colour = strongest earthquake that year.</summary>
internal sealed class YearBars : Control
{
    private List<(int Year, int Count, double? Max)> _rows = new();
    private int _hot = -1;
    private static readonly IBrush Bg = new SolidColorBrush(AColor.Parse("#0b1220"));
    private static readonly IPen GridPen = new APen(new SolidColorBrush(AColor.Parse("#1e293b")), 1);

    public YearBars() { Cursor = new Cursor(StandardCursorType.Arrow); }

    public void SetData(List<AmExtras.Quake> quakes)
    {
        if (quakes.Count == 0) { _rows = new(); InvalidateVisual(); return; }
        int y0 = quakes.Min(q => q.Time.Year), y1 = quakes.Max(q => q.Time.Year);
        var by = quakes.GroupBy(q => q.Time.Year).ToDictionary(g => g.Key, g => (g.Count(), g.Max(q => q.Mag)));
        _rows = Enumerable.Range(y0, y1 - y0 + 1).Select(y => by.TryGetValue(y, out var v) ? (y, v.Item1, v.Item2) : (y, 0, (double?)null)).ToList();
        _hot = -1;
        InvalidateVisual();
    }

    private static FormattedText Txt(string s, double size = 10.5, string hex = "#94a3b8") =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, new SolidColorBrush(AColor.Parse(hex)));

    private (double left, double top, double w, double h) Frame() => (40, 12, Math.Max(10, Bounds.Width - 52), Math.Max(10, Bounds.Height - 40));

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawRectangle(Bg, null, new Rect(Bounds.Size));
        if (_rows.Count == 0) { ctx.DrawText(Txt("No earthquakes with these filters.", 12, "#cbd5e1"), new Point(16, 16)); return; }
        var (left, top, w, h) = Frame();
        int max = Math.Max(1, _rows.Max(r => r.Count));
        int step = max <= 10 ? 2 : max <= 50 ? 10 : max <= 200 ? 25 : 50;
        for (int v = 0; v <= max; v += step)
        {
            double y = top + h - v / (double)max * h;
            ctx.DrawLine(GridPen, new Point(left, y), new Point(left + w, y));
            var t = Txt(v.ToString());
            ctx.DrawText(t, new Point(left - 6 - t.Width, y - 7));
        }
        double bw = w / _rows.Count;
        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            double bh = r.Count / (double)max * h;
            var rect = new Rect(left + i * bw + bw * 0.12, top + h - bh, Math.Max(1, bw * 0.76), bh);
            if (r.Count > 0) ctx.DrawRectangle(new SolidColorBrush(AColor.Parse(AmQuakesPanel.MagHex(r.Max)), i == _hot ? 1 : 0.85), null, rect, 2, 2);
            int labelEvery = _rows.Count > 30 ? 5 : _rows.Count > 15 ? 2 : 1;
            if (r.Year % labelEvery == 0 || _rows.Count <= 15)
            {
                var t = Txt(r.Year.ToString(), 10);
                ctx.DrawText(t, new Point(left + i * bw + bw / 2 - t.Width / 2, top + h + 6));
            }
        }
        if (_hot >= 0 && _hot < _rows.Count)
        {
            var r = _rows[_hot];
            var ft = Txt($"{r.Year}: {r.Count} earthquakes" + (r.Max.HasValue ? $", strongest M {r.Max.Value.ToString("0.0", CultureInfo.InvariantCulture)}" : ""), 12, "#ffffff");
            double x = Math.Min(left + _hot * bw, left + w - ft.Width - 10);
            ctx.DrawRectangle(new SolidColorBrush(AColor.Parse("#f0111827")), null, new Rect(x - 4, top + 2, ft.Width + 8, 20), 4, 4);
            ctx.DrawText(ft, new Point(x, top + 4));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_rows.Count == 0) return;
        var (left, _, w, _) = Frame();
        int i = (int)Math.Floor((e.GetPosition(this).X - left) / (w / _rows.Count));
        _hot = i >= 0 && i < _rows.Count ? i : -1;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _hot = -1; InvalidateVisual(); }
}
