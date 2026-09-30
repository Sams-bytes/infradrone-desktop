using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using static InfraDroneDesktop.Services.AssetMonitorService;

namespace InfraDroneDesktop.Views;

// =====================================================================================
// 🔬 Bridge Check — "is the bridge ITSELF moving?"
// Structure-level screening with EGMS Calibrated (L2b) radar points, shown as pictures:
// a funnel, bridge-vs-ground bars per satellite direction, and the real radar points on and around the deck.
// =====================================================================================
internal sealed class AmBridgeCheckPanel : UserControl
{
    private const string Red = "#ef4444", Orange = "#f59e0b", Cyan = "#22d3ee", Green = "#0d9e75", Purple = "#a855f7";

    private readonly TextBlock _headline = new() { FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _sub = new() { FontSize = 13, Foreground = new SolidColorBrush(Color.Parse("#cbd5e1")), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _runStatus = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#94a3b8")), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _runSummary = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#cbd5e1")), FontFamily = new FontFamily("DejaVu Sans Mono,monospace"), TextWrapping = TextWrapping.Wrap };
    private readonly Button _planBtn, _runBtn;
    private readonly StackPanel _funnel = new() { Spacing = 6 };
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly StackPanel _detail = new() { Spacing = 12 };
    private List<StructureResult> _bridges = new();
    private Dictionary<string, BridgeEvidence> _evidence = new();
    private readonly Dictionary<StructureResult, Border> _listItems = new();

    public AmBridgeCheckPanel()
    {
        // ---------------- hero
        var hero = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(22, 18),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#062a30"), 0), new GradientStop(Color.Parse("#0b2545"), 0.6), new GradientStop(Color.Parse("#1d1146"), 1) }
            }
        };
        var hs = new StackPanel { Spacing = 8 };
        hs.Children.Add(new TextBlock { Text = "🔬 BRIDGE CHECK — IS THE BRIDGE ITSELF MOVING?", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse(Cyan)) });
        hs.Children.Add(_headline);
        hs.Children.Add(_sub);
        var btns = new WrapPanel();
        _planBtn = Am.Btn("📦 Check download size", async (_, _) => await RunAsync(plan: true));
        _runBtn = Am.Btn("▶ Run bridge check", async (_, _) => await RunAsync(plan: false));
        btns.Children.Add(_planBtn);
        btns.Children.Add(_runBtn);
        hs.Children.Add(btns);
        hs.Children.Add(_runStatus);
        hs.Children.Add(_runSummary);
        hero.Child = hs;

        // ---------------- how it works (3 simple steps)
        var how = new WrapPanel();
        how.Children.Add(HowStep("1", "Radar points on the bridge", "Bridges reflect satellite radar strongly, so there are measurement points right on the deck."));
        how.Children.Add(HowStep("2", "Compare with the ground around it", "If the deck moves differently from the ground 20–300 m around it, the bridge itself is moving."));
        how.Children.Add(HowStep("3", "Two satellites agree?", "Satellites look from different directions. If two see the same thing, it is strong evidence."));

        // ---------------- funnel
        var funnelCard = new StackPanel { Spacing = 8 };
        funnelCard.Children.Add(Am.H("From every bridge to a short list"));
        funnelCard.Children.Add(_funnel);

        // ---------------- list + evidence
        var listCard = new StackPanel { Spacing = 8 };
        listCard.Children.Add(Am.H("Bridges moving on their own"));
        listCard.Children.Add(Am.M("Confirmed by two satellite directions first. Click a bridge to see the evidence."));
        listCard.Children.Add(_list);
        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("340,14,*") };
        var lc = Am.Card(listCard); lc.VerticalAlignment = VerticalAlignment.Top;
        var dc = Am.Card(_detail); dc.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(dc, 2);
        main.Children.Add(lc);
        main.Children.Add(dc);

        var root = new StackPanel { Spacing = 14, Margin = new Thickness(16) };
        root.Children.Add(hero);
        root.Children.Add(how);
        root.Children.Add(Am.Card(funnelCard));
        root.Children.Add(main);
        root.Children.Add(Am.Card(Am.M(
            "Data: Copernicus European Ground Motion Service (EGMS), Calibrated point product (level L2b). Movement is measured along each satellite's line of sight " +
            "(towards or away from it), not purely up-down; moving away usually means sinking. Warning levels (2 and 4 mm per year) are placeholders to agree " +
            "with the province's engineers. This is an early warning, not a diagnosis — the drone inspection shows what is really going on.")));
        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        AttachedToVisualTree += (_, _) => Load();
    }

    private static Control HowStep(string n, string title, string text)
    {
        var sp = new StackPanel { Spacing = 4 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(Color.Parse(Cyan)),
            Child = new TextBlock { Text = n, FontWeight = FontWeight.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        head.Children.Add(Am.H(title));
        sp.Children.Add(head);
        sp.Children.Add(Am.M(text));
        var card = Am.Card(sp);
        card.Width = 330;
        card.Margin = new Thickness(0, 0, 12, 0);
        return card;
    }

    // =============================================================== data
    private void Load()
    {
        List<StructureResult> all;
        try
        {
            all = ReadStructureResults();
            _evidence = ReadEvidence();
        }
        catch (Exception ex) { _runStatus.Text = $"Could not read results: {ex.Message}"; return; }

        _planBtn.IsEnabled = _runBtn.IsEnabled = File.Exists(TokenFile);
        if (all.Count == 0)
        {
            _headline.Text = "No bridge check yet.";
            _sub.Text = "Click “Check download size” first, then “Run bridge check”. Downloaded satellite files are kept and re-used.";
            _funnel.Children.Clear(); _list.Children.Clear(); _detail.Children.Clear();
            return;
        }

        _bridges = GroupStructure(all.Where(r => r.Status is "Priority" or "Review"))
            .OrderByDescending(r => r.TracksFlagging >= 2).ThenByDescending(r => Math.Abs(r.DiffLos ?? 0)).ToList();
        int confirmed = _bridges.Count(b => b.TracksFlagging >= 2);
        var release = ReadJson(BridgePointsAudit)?["egms_release"]?.ToString() ?? "";
        _headline.Text = $"{confirmed} bridges move on their own — seen by two satellites.";
        _sub.Text = $"Out of {all.Count:N0} bridge deck parts, the satellites (Copernicus EGMS {release}) could measure {all.Count(r => r.Status is "Priority" or "Review" or "No unusual movement"):N0} on the structure itself. " +
                    $"{_bridges.Count} bridges move differently from the ground around them; {confirmed} of them are confirmed from two directions.";
        if (_evidence.Count == 0)
            _sub.Text += "  ⓘ Click “Run bridge check” once more to add the evidence pictures (no new download needed).";

        BuildFunnel(all, confirmed);
        BuildList();
        if (_bridges.Count > 0) Select(_bridges[0]);
    }

    private void BuildFunnel(List<StructureResult> all, int confirmed)
    {
        _funnel.Children.Clear();
        int total = all.Count;
        int anyPoints = all.Count(r => r.Status != "No points on structure");
        int measured = all.Count(r => r.Status is "Priority" or "Review" or "No unusual movement");
        var steps = new (int value, string text, string color, double width)[]
        {
            (total, "bridge deck parts in the province register", "#3b82f6", 1.00),
            (anyPoints, "have radar points on the deck", "#6366f1", 0.82),
            (measured, "have enough points on the deck and around it to judge", Cyan, 0.64),
            (_bridges.Count, "bridges move differently from the ground around them", Orange, 0.46),
            (confirmed, "bridges confirmed by two satellite directions — inspect first", Red, 0.30)
        };
        int i = 0;
        foreach (var (value, text, color, width) in steps)
        {
            var bar = new Border
            {
                CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 8), Opacity = 0,
                Background = new SolidColorBrush(Color.Parse(color), 0.9),
                Transitions = new Avalonia.Animation.Transitions { new Avalonia.Animation.DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(450) } },
                Child = new TextBlock { Text = $"{value:N0}  ·  {text}", FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap }
            };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength((1 - width) / 2, GridUnitType.Star)));
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(width, GridUnitType.Star)));
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength((1 - width) / 2, GridUnitType.Star)));
            Grid.SetColumn(bar, 1);
            row.Children.Add(bar);
            _funnel.Children.Add(row);
            int delay = 120 + i++ * 180;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
            t.Tick += (_, _) => { t.Stop(); bar.Opacity = 1; };
            t.Start();
        }
        _funnel.Children.Add(Am.M("The funnel shape is illustrative; the numbers are the real counts from the latest bridge check."));
    }

    private void BuildList()
    {
        _list.Children.Clear();
        _listItems.Clear();
        int rank = 0;
        foreach (var b in _bridges)
        {
            rank++;
            var bridge = b;
            bool conf = b.TracksFlagging >= 2;
            var color = b.Status == "Priority" ? Red : Orange;
            var sp = new StackPanel { Spacing = 2 };
            sp.Children.Add(new TextBlock
            {
                Text = $"#{rank}  " + (conf ? "✔✔ CONFIRMED BY 2 SATELLITES" : "✔ one satellite direction"), FontSize = 10, FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.Parse(conf ? Purple : "#94a3b8"))
            });
            sp.Children.Add(Am.P(b.ManagerLabel));
            sp.Children.Add(new TextBlock { Text = $"{Math.Abs(b.DiffLos ?? 0):0.0} mm/yr {(b.DiffLos < 0 ? "more sinking" : "different")} than the ground · {b.Status}", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse(color)), TextWrapping = TextWrapping.Wrap });
            var item = new Border
            {
                Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(3, 0, 0, 0),
                BorderBrush = new SolidColorBrush(Color.Parse(color)), Background = Brushes.Transparent, Child = sp,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            item.PointerPressed += (_, _) => Select(bridge);
            _listItems[b] = item;
            _list.Children.Add(item);
        }
    }

    private async Task RunAsync(bool plan)
    {
        _planBtn.IsEnabled = _runBtn.IsEnabled = false;
        _runStatus.Text = plan ? "Asking EGMS which point files cover the province…"
                               : "Reading the radar points for every bridge — this can take a while (downloaded files are re-used)…";
        try
        {
            var r = await RunScriptAsync(BridgePointsScript, line => Dispatcher.UIThread.Post(() => _runStatus.Text = line),
                                         plan ? new[] { "--plan" } : Array.Empty<string>());
            _runStatus.Text = r.ExitCode == 0 ? "Finished." : $"Script ended with code {r.ExitCode}. Full output: {r.ConsoleLog}";
            Load();
            _runSummary.Text = r.Summary;
        }
        catch (Exception ex) { _runStatus.Text = $"Could not run the bridge check: {ex.Message}"; }
        finally { _planBtn.IsEnabled = _runBtn.IsEnabled = File.Exists(TokenFile); }
    }

    // =============================================================== evidence for one bridge
    private void Select(StructureResult b)
    {
        foreach (var (k, v) in _listItems)
            v.Background = k == b ? new SolidColorBrush(Color.Parse("#223b82f6")) : Brushes.Transparent;

        _detail.Children.Clear();
        bool conf = b.TracksFlagging >= 2;
        var color = b.Status == "Priority" ? Red : Orange;

        // title
        _detail.Children.Add(new TextBlock
        {
            Text = conf ? "✔✔  CONFIRMED BY TWO SATELLITE DIRECTIONS" : "✔  SEEN BY ONE SATELLITE DIRECTION",
            FontSize = 12, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse(conf ? Purple : "#94a3b8"))
        });
        var title = Am.H(b.ManagerLabel);
        title.FontSize = 20;
        _detail.Children.Add(title);

        // plain sentence
        double d = b.DiffLos ?? 0;
        _detail.Children.Add(new TextBlock
        {
            Text = $"This bridge moves {Math.Abs(d):0.0} mm per year {(d < 0 ? "more away from the satellite" : "more towards the satellite")} than the ground around it" +
                   (d < 0 ? " — in plain words: the bridge sinks faster than its surroundings." : "."),
            FontSize = 15, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse(color)), FontWeight = FontWeight.SemiBold
        });

        // evidence
        var ev = b.PartIds.Select(id => _evidence.GetValueOrDefault(id)).FirstOrDefault(e => e != null)
                 ?? _evidence.GetValueOrDefault(b.AssetId);
        if (ev == null)
        {
            _detail.Children.Add(Am.M("Evidence pictures are not available yet for this bridge — click “Run bridge check” once more (no new download needed)."));
            // still show the summary numbers
            _detail.Children.Add(Bars(new List<TrackEvidence> { new(b.Track, b.DeckN, b.DeckMedian ?? 0, b.RingN, b.RingMedian, b.DiffLos, true) }));
        }
        else
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,14,*") };
            var barBox = new StackPanel { Spacing = 6 };
            barBox.Children.Add(Am.H("Bridge vs ground, per satellite direction"));
            barBox.Children.Add(Am.M("Each pair: the ground around the bridge (left) and the bridge deck (right). Bars going down = moving away from the satellite (sinking)."));
            barBox.Children.Add(Bars(ev.Tracks));
            var plotBox = new StackPanel { Spacing = 6 };
            plotBox.Children.Add(Am.H("The radar points themselves"));
            var plot = new RadarPlot(ev) { Height = 340 };
            var zoom = new ToggleButton { Content = "🔍 Zoom in on the bridge", Margin = new Thickness(0, 0, 0, 4) };
            zoom.IsCheckedChanged += (_, _) => plot.Zoomed = zoom.IsChecked == true;
            plotBox.Children.Add(zoom);
            plotBox.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = plot });
            plotBox.Children.Add(Am.M($"White outline = the bridge deck. Large dots = the {ev.Deck.Count} radar points on the deck; small dots = points on the ground around it " +
                                      $"({ev.Ring.Count} of {ev.RingTotal} shown). Colour: red ≤ −4, orange ≤ −2, cyan in between, blue ≥ +2 mm/yr."));
            var b1 = Am.Card(barBox); var b2 = Am.Card(plotBox);
            Grid.SetColumn(b2, 2);
            row.Children.Add(b1); row.Children.Add(b2);
            _detail.Children.Add(row);
        }

        // facts + actions
        _detail.Children.Add(Am.M($"Strongest direction: {b.Track} · {b.DeckN} points on the deck, {b.RingN} around it" +
                                  (b.Coherence.HasValue ? $" · point quality {b.Coherence.Value.ToString("0.00", CultureInfo.InvariantCulture)} (0–1)" : "") +
                                  (b.Parts > 1 ? $" · {b.Parts} register deck parts" : "")));
        var actions = new WrapPanel();
        if (b.Lat.HasValue && b.Lon.HasValue)
            actions.Children.Add(Am.Btn("📍 Show on map", (_, _) => AmNav.ShowOnMap(b.AssetId, b.Lat!.Value, b.Lon!.Value)));
        var tasks = LoadTasks();
        var ids = b.PartIds.Count > 0 ? b.PartIds : new List<string> { b.AssetId };
        var task = tasks.FirstOrDefault(t => ids.Contains(t.AssetId) || t.PartIds.Any(ids.Contains));
        if (task == null)
            actions.Children.Add(Am.Btn("➕ Add to inspection list", (_, _) =>
            {
                tasks.Add(new InspectionTask
                {
                    AssetId = b.AssetId, OwnerType = b.OwnerType, OwnerName = b.OwnerName, Screening = b.Status,
                    MaxAbsMmPerYear = Math.Abs(b.DiffLos ?? 0), Lat = b.Lat, Lon = b.Lon, Parts = ids.Count, PartIds = ids,
                    EgmsRelease = ReadJson(BridgePointsAudit)?["egms_release"]?.ToString() ?? "", CreatedUtc = DateTime.UtcNow.ToString("o")
                });
                SaveTasks(tasks);
                Select(b);
            }));
        else
            actions.Children.Add(Am.P(task.Nen2767Score is int sc ? $"✅ Inspected — NEN 2767 score {sc}" : $"📋 On the inspection list ({task.Status})"));
        _detail.Children.Add(actions);
    }

    /// <summary>Pairs of bars per satellite track: ground (left) vs deck (right), zero line in the middle.</summary>
    private static Control Bars(List<TrackEvidence> tracks)
    {
        var shown = tracks.Where(t => t.RingMed.HasValue).ToList();
        if (shown.Count == 0) return Am.M("No per-direction numbers available.");
        double max = Math.Max(2, shown.Max(t => Math.Max(Math.Abs(t.DeckMed), Math.Abs(t.RingMed ?? 0))));
        const double half = 80;
        var wrap = new WrapPanel();
        foreach (var t in shown)
        {
            var pair = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 18, 8), Width = 150 };
            var chart = new Grid { Height = half * 2 + 2, ColumnDefinitions = new ColumnDefinitions("*,10,*"), RowDefinitions = new RowDefinitions("*,2,*") };
            void Bar(int col, double v, string color)
            {
                var bar = new Border
                {
                    Height = 0, Background = new SolidColorBrush(Color.Parse(color)),
                    CornerRadius = v >= 0 ? new CornerRadius(4, 4, 0, 0) : new CornerRadius(0, 0, 4, 4),
                    VerticalAlignment = v >= 0 ? VerticalAlignment.Bottom : VerticalAlignment.Top,
                    Transitions = new Avalonia.Animation.Transitions { new Avalonia.Animation.DoubleTransition { Property = Layoutable.HeightProperty, Duration = TimeSpan.FromMilliseconds(700), Easing = new Avalonia.Animation.Easings.CubicEaseOut() } }
                };
                Grid.SetColumn(bar, col);
                Grid.SetRow(bar, v >= 0 ? 0 : 2);
                chart.Children.Add(bar);
                double h = Math.Max(2, Math.Abs(v) / max * half);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                timer.Tick += (_, _) => { timer.Stop(); bar.Height = h; };
                timer.Start();
            }
            var zero = new Border { Background = Brushes.White, Opacity = 0.6 };
            Grid.SetRow(zero, 1); Grid.SetColumnSpan(zero, 3);
            chart.Children.Add(zero);
            Bar(0, t.RingMed ?? 0, Green);
            Bar(2, t.DeckMed, t.Diff is double df && Math.Abs(df) >= 4 ? Red : t.Diff is double df2 && Math.Abs(df2) >= 2 ? Orange : Cyan);
            pair.Children.Add(chart);
            var labels = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
            var l1 = Am.M($"Ground\n{(t.RingMed ?? 0).ToString("+0.0;−0.0;0.0", CultureInfo.InvariantCulture)}"); l1.TextAlignment = TextAlignment.Center;
            var l2 = Am.M($"Bridge\n{t.DeckMed.ToString("+0.0;−0.0;0.0", CultureInfo.InvariantCulture)}"); l2.TextAlignment = TextAlignment.Center;
            Grid.SetColumn(l2, 1);
            labels.Children.Add(l1); labels.Children.Add(l2);
            pair.Children.Add(labels);
            pair.Children.Add(new TextBlock
            {
                Text = (t.Diff.HasValue ? $"difference {t.Diff.Value.ToString("+0.0;−0.0;0.0", CultureInfo.InvariantCulture)} mm/yr" : "too few points") + $"\n{t.Track}",
                FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse(t.Diff is double d3 && Math.Abs(d3) >= 2 ? Orange : "#94a3b8"))
            });
            wrap.Children.Add(pair);
        }
        return wrap;
    }
}

/// <summary>Top-down picture of one bridge: deck outline + real radar points on and around it, coloured by velocity.</summary>
internal sealed class RadarPlot : Control
{
    private readonly BridgeEvidence _ev;
    private bool _zoomed;
    public bool Zoomed { get => _zoomed; set { _zoomed = value; InvalidateVisual(); } }
    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#0b1220"));
    private static readonly IPen RingPen = new Pen(new SolidColorBrush(Color.Parse("#1e3a5f")), 1) { DashStyle = DashStyle.Dash };

    public RadarPlot(BridgeEvidence ev) { _ev = ev; }

    private static IBrush VelBrush(double v, double alpha) => new SolidColorBrush(Color.Parse(
        v <= -4 ? "#ef4444" : v <= -2 ? "#f59e0b" : v >= 2 ? "#3b82f6" : "#22d3ee"), alpha);

    public override void Render(DrawingContext ctx)
    {
        var s = Bounds.Size;
        ctx.DrawRectangle(Bg, null, new Rect(s));
        double extent = 300;
        if (_zoomed)
        {
            var xs = _ev.Outline.SelectMany(p => p).Select(p => Math.Max(Math.Abs(p.X), Math.Abs(p.Y))).DefaultIfEmpty(20).Max();
            extent = Math.Max(25, xs * 1.8);
        }
        double k = Math.Min(s.Width, s.Height) / 2 / (extent * 1.05);
        var c = new Point(s.Width / 2, s.Height / 2);
        Point P(double dx, double dy) => new(c.X + dx * k, c.Y - dy * k);

        if (!_zoomed)
        {
            ctx.DrawEllipse(null, RingPen, c, 20 * k, 20 * k);
            ctx.DrawEllipse(null, RingPen, c, 300 * k, 300 * k);
        }
        foreach (var p in _ev.Ring)
            ctx.DrawEllipse(VelBrush(p.Vel, 0.75), null, P(p.Dx, p.Dy), _zoomed ? 3.5 : 2.2, _zoomed ? 3.5 : 2.2);

        var outlinePen = new Pen(Brushes.White, 1.6);
        foreach (var poly in _ev.Outline)
            for (int i = 1; i < poly.Count; i++)
                ctx.DrawLine(outlinePen, P(poly[i - 1].X, poly[i - 1].Y), P(poly[i].X, poly[i].Y));

        double r = _zoomed ? 7 : 4.5;
        foreach (var p in _ev.Deck)
            ctx.DrawEllipse(VelBrush(p.Vel, 1), new Pen(Brushes.White, 1.2), P(p.Dx, p.Dy), r, r);

        // scale bar
        double barM = _zoomed ? 10 : 100;
        var y = s.Height - 16;
        var pen = new Pen(Brushes.White, 2);
        ctx.DrawLine(pen, new Point(14, y), new Point(14 + barM * k, y));
        ctx.DrawLine(pen, new Point(14, y - 4), new Point(14, y + 4));
        ctx.DrawLine(pen, new Point(14 + barM * k, y - 4), new Point(14 + barM * k, y + 4));
        var ft = new FormattedText($"{barM:0} m", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.White);
        ctx.DrawText(ft, new Point(18 + barM * k, y - 8));
    }
}
