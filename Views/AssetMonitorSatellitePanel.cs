using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using static InfraDroneDesktop.Services.AssetMonitorService;

namespace InfraDroneDesktop.Views;

/// <summary>Cross-sub-tab navigation: "📍 Show on map" from the Satellite Screening sub-tab.</summary>
internal static class AmNav
{
    public static event Action<string?, double, double>? ShowOnMapRequested;
    public static void ShowOnMap(string? assetId, double lat, double lon) => ShowOnMapRequested?.Invoke(assetId, lat, lon);
}

// =====================================================================================
// 🛰 Satellite Screening — visual dashboard. Every number and dot is real screening data.
// =====================================================================================
internal sealed class AmSatellitePanel : AmPanel
{
    private const string Red = "#ef4444", Orange = "#f59e0b", Green = "#0d9e75", Grey = "#64748b";

    private readonly TextBlock _token = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#94a3b8")) };
    private readonly TextBlock _heroSub = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#cbd5e1")) };
    private readonly TextBlock _summary = Am.Mono("");
    private readonly Button _run;
    private readonly WrapPanel _kpis = new();
    private readonly SkyData _sky = new();
    private readonly SkyDots _dots;
    private readonly SkyPulse _pulse;
    private readonly TextBlock _hover = new() { FontSize = 12, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _managers = new() { Spacing = 8 };
    private readonly ContentControl _histUp = new(), _histEast = new();
    private readonly WrapPanel _watch = new();
    private readonly StackPanel _table = new() { Spacing = 1 };
    private double _review = 2, _priority = 4;

    public AmSatellitePanel()
    {
        _dots = new SkyDots(_sky);
        _pulse = new SkyPulse(_sky);
        _pulse.Hovered += b => _hover.Text = b == null
            ? "Hover a dot to see which bridge it is · click a red or orange dot to open it on the map"
            : $"{b.Screening} · {b.ManagerLabel} · vertical {Am.F(b.UpDiff)} / sideways {Am.F(b.EastDiff)} mm/yr vs surroundings";
        _pulse.Clicked += b => { if (b.Lat.HasValue && b.Lon.HasValue) AmNav.ShowOnMap(b.AssetId, b.Lat.Value, b.Lon.Value); };

        // ---------------- hero banner
        var hero = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(20, 16),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#0b2545"), 0), new GradientStop(Color.Parse("#13315c"), 0.55), new GradientStop(Color.Parse("#1d1146"), 1) }
            }
        };
        var heroStack = new StackPanel { Spacing = 8 };
        heroStack.Children.Add(new TextBlock { Text = "🛰  Ground-motion screening from space", FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Brushes.White });
        heroStack.Children.Add(_heroSub);
        _heroSub.Text = "Sentinel-1 radar satellites measure millimetre ground movement. Every bridge in the province is compared with its surroundings.";
        _run = Am.Btn("▶ Run satellite screening", async (_, _) => await RunAsync());
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(_run);
        buttons.Children.Add(Am.Btn("📂 Open results folder", (_, _) => Am.OpenFolder(ScreeningOut)));
        heroStack.Children.Add(buttons);
        heroStack.Children.Add(_token);
        heroStack.Children.Add(_status);
        hero.Child = heroStack;
        Root.Children.Add(hero);

        // ---------------- KPI tiles
        Root.Children.Add(_kpis);

        // ---------------- "seen from space" map + manager breakdown
        var skyBox = new Grid { Height = 460 };
        skyBox.Children.Add(_dots);
        skyBox.Children.Add(_pulse);
        var hoverBar = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10), Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.Parse("#cc0b1220")), Child = _hover, IsHitTestVisible = false
        };
        skyBox.Children.Add(hoverBar);
        _hover.Text = "Hover a dot to see which bridge it is · click a red or orange dot to open it on the map";
        var skyCard = new StackPanel { Spacing = 8 };
        skyCard.Children.Add(Am.H("Every bridge, seen from space"));
        skyCard.Children.Add(Am.M("Each dot is one bridge deck part at its real location. Green = no unusual movement, grey = no satellite data, orange = Review, pulsing red = Priority."));
        skyCard.Children.Add(skyBox);

        var managerCard = new StackPanel { Spacing = 8 };
        managerCard.Children.Add(Am.H("Flagged bridges per manager"));
        managerCard.Children.Add(Am.M("Separate bridge locations flagged Priority (red) or Review (orange), grouped by the registered manager type."));
        managerCard.Children.Add(_managers);

        var row1 = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,12,2*") };
        var c1 = Am.Card(skyCard); var c2 = Am.Card(managerCard);
        Grid.SetColumn(c2, 2);
        row1.Children.Add(c1); row1.Children.Add(c2);
        Root.Children.Add(row1);

        // ---------------- histograms
        var row2 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        var h1 = Am.Card(_histUp); var h2 = Am.Card(_histEast);
        Grid.SetColumn(h2, 2);
        row2.Children.Add(h1); row2.Children.Add(h2);
        Root.Children.Add(row2);

        // ---------------- watchlist
        Root.Children.Add(Am.H("🎯 Watchlist — the bridges moving most differently from their surroundings"));
        Root.Children.Add(_watch);

        // ---------------- technical details (collapsed)
        var tech = new StackPanel { Spacing = 8 };
        tech.Children.Add(Am.M(
            "Data: EGMS (European Ground Motion Service, Copernicus) Ortho product, a 100 m grid — it screens the ground at and around a bridge, not the deck itself. " +
            "Bridge = within 150 m; surroundings = 150 m to 1 km. The Review and Priority thresholds are placeholder settings to be agreed with the province's engineers."));
        tech.Children.Add(_summary);
        tech.Children.Add(Am.H("Ranked table (top 100 bridge locations)"));
        tech.Children.Add(_table);
        Root.Children.Add(new Expander { Header = "Technical details, limitations and full ranked table", Content = tech, HorizontalAlignment = HorizontalAlignment.Stretch });

        AttachedToVisualTree += (_, _) => { CheckToken(); _ = LoadAsync(); };
    }

    // =============================================================== data -> visuals
    private void CheckToken()
    {
        bool ok = File.Exists(TokenFile);
        _token.Text = ok ? "✅ Copernicus access token found." :
            "⚠ No Copernicus access token yet — get a free one at land.copernicus.eu (profile → API Tokens), save it as " + TokenFile;
        _run.IsEnabled = ok;
    }

    private async Task LoadAsync()
    {
        List<ScreenedBridge> rows;
        try { rows = await Task.Run(() => ReadRanked()); }
        catch (Exception ex) { _status.Text = $"Could not read results: {ex.Message}"; return; }

        var m = ReadManifest();
        var set = m?["settings_placeholders_to_agree_with_province"];
        if (double.TryParse(set?["REVIEW_MM_YR"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rv)) _review = rv;
        if (double.TryParse(set?["PRIORITY_MM_YR"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pv)) _priority = pv;

        _kpis.Children.Clear(); _managers.Children.Clear(); _watch.Children.Clear(); _table.Children.Clear();
        if (rows.Count == 0)
        {
            _kpis.Children.Add(Am.M("No screening results yet — click “Run satellite screening”."));
            _sky.Load(rows); _dots.InvalidateVisual(); _pulse.InvalidateVisual();
            return;
        }

        var release = m?["egms_release"]?.ToString() ?? "?";
        var run = m?["run_utc"]?.ToString() ?? "";
        _heroSub.Text = $"EGMS release {release}  ·  {rows.Count:N0} bridge deck parts screened  ·  run {run.Replace("T", " ").Replace("+00:00", " UTC")}";

        var flaggedLoc = GroupParts(rows.Where(r => r.Screening is "Priority" or "Review")).ToList();
        int pr = rows.Count(r => r.Screening == "Priority"), rev = rows.Count(r => r.Screening == "Review");
        int ok = rows.Count(r => r.Screening == "No unusual movement"), nod = rows.Count(r => r.Screening == "No satellite data");

        _kpis.Children.Add(Kpi("Bridge deck parts screened", rows.Count, "#3b82f6", "every bridge in the province register"));
        _kpis.Children.Add(Kpi("Priority", pr, Red, $"moving ≥ {_priority:0.#} mm/yr differently"));
        _kpis.Children.Add(Kpi("Review", rev, Orange, $"moving ≥ {_review:0.#} mm/yr differently"));
        _kpis.Children.Add(Kpi("Flagged bridge locations", flaggedLoc.Count, "#a855f7", "deck parts of one bridge counted once"));
        _kpis.Children.Add(Kpi("No unusual movement", ok, Green, "no drone visit needed from this screening"));
        _kpis.Children.Add(Kpi("No satellite data", nod, Grey, "no radar measurement points nearby"));

        _sky.Load(rows);
        _dots.InvalidateVisual();
        _pulse.InvalidateVisual();

        BuildManagers(flaggedLoc);
        _histUp.Content = Histogram("↕ Vertical movement vs surroundings", "mm per year · negative = sinking faster than the area around it",
            rows.Where(r => r.UpDiff.HasValue).Select(r => r.UpDiff!.Value).ToList());
        _histEast.Content = Histogram("↔ Sideways (east-west) movement vs surroundings", "mm per year · negative = moving west, positive = moving east",
            rows.Where(r => r.EastDiff.HasValue).Select(r => r.EastDiff!.Value).ToList());

        int rank = 0;
        foreach (var b in flaggedLoc.OrderByDescending(r => r.MaxAbs).Take(12))
            _watch.Children.Add(WatchCard(++rank, b));

        BuildTable(GroupParts(rows.Where(r => r.MaxAbs.HasValue)).OrderByDescending(r => r.MaxAbs).Take(100).ToList());
    }

    // ---------------- KPI tile with count-up animation
    private static Control Kpi(string caption, int value, string color, string sub)
    {
        var num = new TextBlock { Text = "0", FontSize = 32, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse(color)) };
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(num);
        sp.Children.Add(Am.P(caption));
        sp.Children.Add(Am.M(sub));
        var inner = new Border { BorderThickness = new Thickness(4, 0, 0, 0), BorderBrush = new SolidColorBrush(Color.Parse(color)), Padding = new Thickness(12, 2, 4, 2), Child = sp };
        var card = Am.Card(inner);
        card.Width = 230;
        card.Margin = new Thickness(0, 0, 12, 12);
        CountUp(num, value);
        return card;
    }

    private static void CountUp(TextBlock tb, int target)
    {
        var start = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            var t = Math.Min(1.0, (DateTime.UtcNow - start).TotalMilliseconds / 1100.0);
            var eased = 1 - Math.Pow(1 - t, 3);
            tb.Text = ((int)Math.Round(target * eased)).ToString("N0", CultureInfo.InvariantCulture);
            if (t >= 1) timer.Stop();
        };
        timer.Start();
    }

    private static void After(int ms, Action a)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); a(); };
        t.Start();
    }

    private string SeverityColor(double v) => Math.Abs(v) >= _priority ? Red : Math.Abs(v) >= _review ? Orange : Green;

    // ---------------- manager breakdown (stacked bars)
    private void BuildManagers(List<ScreenedBridge> flagged)
    {
        var groups = flagged.GroupBy(b => b.OwnerType.Split(' ')[0])
            .Select(g => (Type: g.Key, P: g.Count(b => b.Screening == "Priority"), R: g.Count(b => b.Screening == "Review")))
            .OrderByDescending(g => g.P + g.R).ToList();
        if (groups.Count == 0) { _managers.Children.Add(Am.M("Nothing flagged.")); return; }
        int max = groups.Max(g => g.P + g.R);
        foreach (var g in groups)
        {
            var bar = new Grid { Height = 14 };
            bar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(g.P, 0.0001), GridUnitType.Star)));
            bar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(g.R, 0.0001), GridUnitType.Star)));
            bar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(max - g.P - g.R, 0.0001), GridUnitType.Star)));
            var p = new Border { Background = new SolidColorBrush(Color.Parse(Red)), CornerRadius = new CornerRadius(3, 0, 0, 3) };
            var r = new Border { Background = new SolidColorBrush(Color.Parse(Orange)) };
            Grid.SetColumn(r, 1);
            bar.Children.Add(p); bar.Children.Add(r);
            var line = new StackPanel { Spacing = 3 };
            line.Children.Add(Am.Row("*,Auto", Am.P(g.Type), Am.M($"{g.P} Priority · {g.R} Review")));
            line.Children.Add(bar);
            _managers.Children.Add(line);
        }
    }

    // ---------------- histogram with growing bars
    private Control Histogram(string title, string explain, List<double> values)
    {
        var box = new StackPanel { Spacing = 6 };
        box.Children.Add(Am.H(title));
        box.Children.Add(Am.M(explain));
        if (values.Count == 0) { box.Children.Add(Am.M("No data.")); return box; }

        var bins = new int[21];   // -10 .. +10 mm/yr, ends collect everything beyond
        foreach (var v in values) bins[(int)Math.Round(Math.Clamp(v, -10, 10)) + 10]++;
        int max = Math.Max(1, bins.Max());
        var cols = string.Join(",", Enumerable.Repeat("*", 21));
        var chart = new Grid { Height = 170, ColumnDefinitions = new ColumnDefinitions(cols) };
        for (int i = 0; i < 21; i++)
        {
            int val = i - 10;
            double h = bins[i] == 0 ? 0 : Math.Max(3, 165 * Math.Sqrt(bins[i]) / Math.Sqrt(max));
            var bar = new Border
            {
                Height = 0, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1.5, 0),
                CornerRadius = new CornerRadius(3, 3, 0, 0), Background = new SolidColorBrush(Color.Parse(SeverityColor(val))),
                Transitions = new Transitions { new DoubleTransition { Property = Layoutable.HeightProperty, Duration = TimeSpan.FromMilliseconds(900), Easing = new CubicEaseOut() } }
            };
            var range = val <= -10 ? "−10 or less" : val >= 10 ? "10 or more" : val.ToString(CultureInfo.InvariantCulture);
            ToolTip.SetTip(bar, $"{range} mm/yr: {bins[i]:N0} bridge deck parts");
            Grid.SetColumn(bar, i);
            chart.Children.Add(bar);
            After(150 + i * 25, () => bar.Height = h);
        }
        var axis = new Grid { ColumnDefinitions = new ColumnDefinitions(cols) };
        foreach (var (col, label) in new[] { (0, "≤−10"), (5, "−5"), (10, "0"), (15, "+5"), (20, "≥10") })
        {
            var t = Am.M(label);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(t, col);
            axis.Children.Add(t);
        }
        box.Children.Add(chart);
        box.Children.Add(axis);
        box.Children.Add(Am.M($"Bar height uses a square-root scale so the few flagged bridges stay visible next to the many normal ones. " +
                              $"Colour: green < {_review:0.#}, orange ≥ {_review:0.#}, red ≥ {_priority:0.#} mm/yr. Hover a bar for the exact count."));
        return box;
    }

    // ---------------- watchlist card
    private Control WatchCard(int rank, ScreenedBridge b)
    {
        var color = b.Screening == "Priority" ? Red : Orange;
        var sp = new StackPanel { Spacing = 8 };

        var badge = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = new SolidColorBrush(Color.Parse(color)),
            Child = new TextBlock { Text = rank.ToString(), FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        var title = new StackPanel { Spacing = 1, Margin = new Thickness(10, 0, 0, 0) };
        title.Children.Add(new TextBlock { Text = b.Screening.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse(color)) });
        title.Children.Add(Am.H(b.ManagerLabel));
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(badge);
        head.Children.Add(title);
        sp.Children.Add(head);

        sp.Children.Add(Diverging("↕ Vertical", b.UpDiff, "sinking", "rising"));
        sp.Children.Add(Diverging("↔ Sideways", b.EastDiff, "west", "east"));

        var dots = new string('●', Math.Min(b.LocalN ?? 0, 10));
        sp.Children.Add(Am.M($"Satellite points at bridge: {dots} {b.LocalN ?? 0}   ·   around: {b.GroundN ?? 0}" +
                             (b.Parts > 1 ? $"   ·   {b.Parts} deck parts" : "")));
        if (b.Lat.HasValue && b.Lon.HasValue)
        {
            var go = Am.Btn("📍 Show on map", (_, _) => AmNav.ShowOnMap(b.AssetId, b.Lat!.Value, b.Lon!.Value));
            go.Margin = new Thickness(0);
            sp.Children.Add(go);
        }

        var accent = new Border { BorderThickness = new Thickness(0, 3, 0, 0), BorderBrush = new SolidColorBrush(Color.Parse(color)), Padding = new Thickness(0, 10, 0, 0), Child = sp };
        var card = Am.Card(accent);
        card.Width = 330;
        card.Margin = new Thickness(0, 0, 12, 12);
        card.Opacity = 0;
        card.Transitions = new Transitions { new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(500) } };
        After(200 + rank * 90, () => card.Opacity = 1);
        return card;
    }

    /// <summary>Bar growing left (negative) or right (positive) from a centre line; full width = 10 mm/yr.</summary>
    private Control Diverging(string label, double? v, string negWord, string posWord)
    {
        var sp = new StackPanel { Spacing = 3 };
        sp.Children.Add(Am.P(v.HasValue
            ? $"{label}: {v.Value.ToString("+0.0;−0.0;0.0", CultureInfo.InvariantCulture)} mm/yr ({(v.Value < 0 ? negWord : posWord)}) vs surroundings"
            : $"{label}: no satellite data"));
        var track = new Grid { Height = 10 };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.Parse("#33475569")) });
        if (v.HasValue)
        {
            double frac = Math.Min(1.0, Math.Abs(v.Value) / 10.0);
            var halves = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
            var inner = new Grid();
            var bar = new Border { CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.Parse(SeverityColor(v.Value))) };
            if (v.Value < 0)
            {
                inner.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(1 - frac, 0.0001), GridUnitType.Star)));
                inner.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(frac, 0.0001), GridUnitType.Star)));
                Grid.SetColumn(bar, 1);
                Grid.SetColumn(inner, 0);
            }
            else
            {
                inner.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(frac, 0.0001), GridUnitType.Star)));
                inner.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(1 - frac, 0.0001), GridUnitType.Star)));
                Grid.SetColumn(bar, 0);
                Grid.SetColumn(inner, 1);
            }
            inner.Children.Add(bar);
            halves.Children.Add(inner);
            track.Children.Add(halves);
        }
        track.Children.Add(new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Center, Background = Brushes.White, Opacity = 0.7 });
        sp.Children.Add(track);
        return sp;
    }

    // ---------------- full table (inside the expander)
    private void BuildTable(List<ScreenedBridge> top)
    {
        const string cols = "36,90,*,60,85,85,85,85,70";
        _table.Children.Add(Am.Row(cols, Am.M("#"), Am.M("Status"), Am.M("Registered manager"), Am.M("Parts"),
            Am.M("Vertical diff"), Am.M("Sideways diff"), Am.M("At bridge ↕"), Am.M("Surround. ↕"), Am.M("Points")));
        int i = 0;
        foreach (var r in top)
        {
            i++;
            _table.Children.Add(Am.Row(cols, Am.M(i.ToString()),
                new TextBlock { Text = r.Screening, Foreground = Am.StatusBrush(r.Screening), FontSize = 12 },
                Am.P(r.ManagerLabel), Am.Mono(r.Parts > 1 ? $"×{r.Parts}" : "1"),
                Am.Mono(Am.F(r.UpDiff)), Am.Mono(Am.F(r.EastDiff)), Am.Mono(Am.F(r.UpLocal)), Am.Mono(Am.F(r.UpGround)),
                Am.Mono($"{r.LocalN}/{r.GroundN}")));
        }
    }

    // ---------------- run
    private async Task RunAsync()
    {
        _run.IsEnabled = false;
        _pulse.Scanning = true;
        _status.Text = "Connecting to EGMS… (the first run downloads the satellite files; this can take several minutes)";
        try
        {
            var r = await RunScriptAsync(ScreeningScript, line => Dispatcher.UIThread.Post(() => _status.Text = line));
            _summary.Text = r.Summary;
            _status.Text = r.ExitCode == 0 ? "Finished — results below." : $"Script ended with code {r.ExitCode}. Full output: {r.ConsoleLog}";
            await LoadAsync();
        }
        catch (Exception ex) { _status.Text = $"Could not run the screening script: {ex.Message}"; }
        finally { _pulse.Scanning = false; CheckToken(); }
    }
}

// =====================================================================================
// "Seen from space" mini-map: real bridge positions drawn as dots (no map tiles needed)
// =====================================================================================
internal sealed class SkyData
{
    public readonly List<(double X, double Y, ScreenedBridge B)> All = new();
    public readonly List<(double X, double Y, ScreenedBridge B)> Flagged = new();
    public double MinX, MaxX, MinY, MaxY;
    private const double LatScale = 0.6;   // cos(53.2°): keeps the province's true shape

    public void Load(List<ScreenedBridge> rows)
    {
        All.Clear(); Flagged.Clear();
        foreach (var r in rows)
        {
            if (!r.Lat.HasValue || !r.Lon.HasValue) continue;
            var d = (r.Lon.Value * LatScale, r.Lat.Value, r);
            All.Add(d);
            if (r.Screening is "Priority" or "Review") Flagged.Add(d);
        }
        // draw order: Review under Priority
        Flagged.Sort((a, b) => (a.B.Screening == "Priority").CompareTo(b.B.Screening == "Priority"));
        if (All.Count == 0) return;
        MinX = All.Min(d => d.X); MaxX = All.Max(d => d.X);
        MinY = All.Min(d => d.Y); MaxY = All.Max(d => d.Y);
    }

    public Point ToScreen(Size s, double x, double y)
    {
        const double pad = 18;
        double w = Math.Max(1e-9, MaxX - MinX), h = Math.Max(1e-9, MaxY - MinY);
        double k = Math.Min((s.Width - 2 * pad) / w, (s.Height - 2 * pad) / h);
        double ox = (s.Width - w * k) / 2, oy = (s.Height - h * k) / 2;
        return new Point(ox + (x - MinX) * k, oy + (MaxY - y) * k);
    }
}

internal sealed class SkyDots : Control
{
    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#0b1220"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#1a2740")), 1);
    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#0d9e75"), 0.55);
    private static readonly IBrush NoData = new SolidColorBrush(Color.Parse("#64748b"), 0.35);
    private readonly SkyData _d;
    public SkyDots(SkyData d) { _d = d; }

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.DrawRectangle(Bg, null, new Rect(size), 8, 8);
        if (_d.All.Count == 0) return;
        // faint graticule every 0.1 degree
        for (double lon = Math.Ceiling(_d.MinX / 0.6 * 10) / 10; lon * 0.6 <= _d.MaxX; lon += 0.1)
            ctx.DrawLine(GridPen, _d.ToScreen(size, lon * 0.6, _d.MinY), _d.ToScreen(size, lon * 0.6, _d.MaxY));
        for (double lat = Math.Ceiling(_d.MinY * 10) / 10; lat <= _d.MaxY; lat += 0.1)
            ctx.DrawLine(GridPen, _d.ToScreen(size, _d.MinX, lat), _d.ToScreen(size, _d.MaxX, lat));
        foreach (var (x, y, b) in _d.All)
        {
            if (b.Screening is "Priority" or "Review") continue;   // drawn by the pulse layer
            var p = _d.ToScreen(size, x, y);
            if (b.Screening == "No unusual movement") ctx.DrawEllipse(Ok, null, p, 1.4, 1.4);
            else ctx.DrawEllipse(NoData, null, p, 1.1, 1.1);
        }
    }
}

internal sealed class SkyPulse : Control
{
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#ef4444"));
    private static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#f59e0b"));
    private readonly SkyData _d;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private double _phase;
    private ScreenedBridge? _hot;
    public bool Scanning { get; set; }
    public event Action<ScreenedBridge?>? Hovered;
    public event Action<ScreenedBridge>? Clicked;

    public SkyPulse(SkyData d)
    {
        _d = d;
        Cursor = new Cursor(StandardCursorType.Hand);
        _timer.Tick += (_, _) => { _phase = (_phase + 0.035) % 1.0; InvalidateVisual(); };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _timer.Start(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { base.OnDetachedFromVisualTree(e); _timer.Stop(); }

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.DrawRectangle(Brushes.Transparent, null, new Rect(size));   // makes the whole area clickable
        if (Scanning)
        {
            double x = _phase * size.Width;
            var band = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#0022d3ee"), 0), new GradientStop(Color.Parse("#6622d3ee"), 1) }
            };
            ctx.DrawRectangle(band, null, new Rect(Math.Max(0, x - 60), 0, 60, size.Height));
        }
        if (_d.All.Count == 0) return;
        foreach (var (x, y, b) in _d.Flagged)
        {
            var p = _d.ToScreen(size, x, y);
            if (b.Screening == "Priority")
            {
                // pulsing halo, each dot slightly out of phase so the map "breathes"
                double ph = (_phase + (x * 7 + y * 3) % 1.0) % 1.0;
                double r = 3 + 9 * ph;
                ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#ef4444"), 0.45 * (1 - ph)), null, p, r, r);
                ctx.DrawEllipse(Red, new Pen(Brushes.White, 0.8), p, 3.2, 3.2);
            }
            else ctx.DrawEllipse(Orange, null, p, 2.3, 2.3);
        }
        if (_hot != null && _hot.Lat.HasValue && _hot.Lon.HasValue)
        {
            var p = _d.ToScreen(size, _hot.Lon.Value * 0.6, _hot.Lat.Value);
            ctx.DrawEllipse(null, new Pen(Brushes.White, 1.5), p, 8, 8);
        }
    }

    private ScreenedBridge? Nearest(Point pos)
    {
        var size = Bounds.Size;
        ScreenedBridge? best = null; double bestD = 12 * 12;
        foreach (var (x, y, b) in _d.Flagged)
        {
            var p = _d.ToScreen(size, x, y);
            double dd = (p.X - pos.X) * (p.X - pos.X) + (p.Y - pos.Y) * (p.Y - pos.Y);
            if (dd < bestD) { bestD = dd; best = b; }
        }
        if (best != null) return best;
        bestD = 5 * 5;
        foreach (var (x, y, b) in _d.All)
        {
            var p = _d.ToScreen(size, x, y);
            double dd = (p.X - pos.X) * (p.X - pos.X) + (p.Y - pos.Y) * (p.Y - pos.Y);
            if (dd < bestD) { bestD = dd; best = b; }
        }
        return best;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var hit = Nearest(e.GetPosition(this));
        if (!ReferenceEquals(hit, _hot)) { _hot = hit; Hovered?.Invoke(hit); }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hot = null;
        Hovered?.Invoke(null);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var hit = Nearest(e.GetPosition(this));
        if (hit != null) Clicked?.Invoke(hit);
    }
}
