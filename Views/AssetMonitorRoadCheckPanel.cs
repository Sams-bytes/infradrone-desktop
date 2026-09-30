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
// 🛣 Road Check — which stretches of PROVINCIAL road sink faster than the ground around them?
// Two checks on the provincial road segments from the NWB (National Road Database):
//   area check    = EGMS Ortho 100 m grid along the road vs 1 km around (egms_screen.py --assets roads)
//   on-road check = EGMS Calibrated radar points on the road surface vs the ground around (egms_bridge_points.py --assets roads)
// =====================================================================================
internal sealed class AmRoadCheckPanel : UserControl
{
    private const string Red = "#ef4444", Orange = "#f59e0b", Cyan = "#22d3ee", Green = "#0d9e75", Purple = "#a855f7";

    private sealed class RoadItem
    {
        public string Id = "", Label = "", Municipality = "", Level = "", Source = "";
        public double? Diff, Lat, Lon;
        public bool Confirmed;
        public StructureResult? S;
        public ScreenedBridge? A;
    }

    private readonly TextBlock _headline = new() { FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _sub = new() { FontSize = 13, Foreground = new SolidColorBrush(Color.Parse("#cbd5e1")), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _runStatus = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#94a3b8")), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _runSummary = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#cbd5e1")), FontFamily = new FontFamily("DejaVu Sans Mono,monospace"), TextWrapping = TextWrapping.Wrap };
    private readonly Button _areaBtn, _pointsBtn;
    private readonly WrapPanel _kpis = new();
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly StackPanel _detail = new() { Spacing = 12 };
    private readonly Dictionary<RoadItem, Border> _listItems = new();
    private List<RoadItem> _items = new();
    private Dictionary<string, BridgeEvidence> _evidence = new();

    public AmRoadCheckPanel()
    {
        var hero = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(22, 18),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#1f2937"), 0), new GradientStop(Color.Parse("#0b2545"), 0.6), new GradientStop(Color.Parse("#13315c"), 1) }
            }
        };
        var hs = new StackPanel { Spacing = 8 };
        hs.Children.Add(new TextBlock { Text = "🛣 ROAD CHECK — PROVINCIAL ROADS", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse(Cyan)) });
        hs.Children.Add(_headline);
        hs.Children.Add(_sub);
        var btns = new WrapPanel();
        _areaBtn = Am.Btn("▶ 1. Area check (quick)", async (_, _) => await RunAsync(ScreeningScript, "--assets", "roads"));
        _pointsBtn = Am.Btn("▶ 2. On-road check (radar points on the road)", async (_, _) => await RunAsync(BridgePointsScript, "--assets", "roads"));
        btns.Children.Add(_areaBtn);
        btns.Children.Add(_pointsBtn);
        hs.Children.Add(btns);
        hs.Children.Add(new TextBlock
        {
            Text = "The area check re-uses the satellite grid already downloaded. The on-road check re-uses the point files from the Bridge Check " +
                   "(if they cover the roads; otherwise it downloads what is missing).",
            FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#94a3b8")), TextWrapping = TextWrapping.Wrap
        });
        hs.Children.Add(_runStatus);
        hs.Children.Add(_runSummary);
        hs.Children.Add(new ExtrasBar(() => Load()));
        hero.Child = hs;

        var listCard = new StackPanel { Spacing = 8 };
        listCard.Children.Add(Am.H("Road stretches with a warning sign"));
        listCard.Children.Add(Am.M("On-road results first (strongest), then area-only results. Click a stretch to see the evidence."));
        listCard.Children.Add(_list);
        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("360,14,*") };
        var lc = Am.Card(listCard); lc.VerticalAlignment = VerticalAlignment.Top;
        var dc = Am.Card(_detail); dc.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(dc, 2);
        main.Children.Add(lc); main.Children.Add(dc);

        var root = new StackPanel { Spacing = 14, Margin = new Thickness(16) };
        root.Children.Add(hero);
        root.Children.Add(_kpis);
        root.Children.Add(main);
        root.Children.Add(Am.Card(Am.M(
            "Road segments: provincial roads from the NWB (National Road Database, Rijkswaterstaat). A segment runs between two junctions and can be several hundred metres long, " +
            "so a short sinking spot inside a long segment is averaged with the rest of it. Area check: EGMS Ortho grid within 100 m of the road vs 100 m – 1 km around. " +
            "On-road check: EGMS Calibrated radar points within 6 m of the road centre line vs points 20–300 m around, per satellite track (line of sight). " +
            "Warning levels (2 and 4 mm per year) are placeholders to agree with the province's engineers. Early warning only — an inspection shows what is really going on.")));
        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        AttachedToVisualTree += (_, _) => Load();
    }

    private static string RoadLabel(string number, string name, string fallbackId)
    {
        var s = $"{number} {name}".Trim();
        return s.Length > 0 ? s : $"Road segment {fallbackId}";
    }

    // =============================================================== data
    private void Load()
    {
        List<ScreenedBridge> area; List<StructureResult> onRoad;
        try
        {
            area = ReadRanked(RoadsRankedCsv);
            onRoad = ReadStructureResults(RoadPointsCsv);
            _evidence = ReadEvidence(RoadPointsDetails);
        }
        catch (Exception ex) { _runStatus.Text = $"Could not read road results: {ex.Message}"; return; }
        bool token = File.Exists(TokenFile);
        _areaBtn.IsEnabled = _pointsBtn.IsEnabled = token;

        _items.Clear();
        var seen = new HashSet<string>();
        foreach (var s in onRoad.Where(r => r.Status is "Priority" or "Review"))
        {
            _items.Add(new RoadItem
            {
                Id = s.AssetId, Label = RoadLabel(s.RoadNumber, s.RoadName, s.AssetId), Municipality = s.Municipality,
                Level = s.Status, Source = "On-road", Diff = s.DiffLos, Lat = s.Lat, Lon = s.Lon, Confirmed = s.TracksFlagging >= 2, S = s
            });
            seen.Add(s.AssetId);
        }
        foreach (var a in area.Where(r => r.Screening is "Priority" or "Review" && !seen.Contains(r.AssetId)))
        {
            double up = a.UpDiff ?? 0, ew = a.EastDiff ?? 0;
            _items.Add(new RoadItem
            {
                Id = a.AssetId, Label = RoadLabel(a.RoadNumber, a.RoadName, a.AssetId), Municipality = a.Municipality,
                Level = a.Screening, Source = "Area", Diff = Math.Abs(up) >= Math.Abs(ew) ? up : ew, Lat = a.Lat, Lon = a.Lon, A = a
            });
        }
        _items = _items.OrderBy(i => i.Source == "On-road" ? 0 : 1).ThenByDescending(i => i.Confirmed)
                       .ThenByDescending(i => i.Level == "Priority").ThenByDescending(i => Math.Abs(i.Diff ?? 0)).ToList();

        int segments = area.Count > 0 ? area.Count : onRoad.Count;
        int confirmed = _items.Count(i => i.Confirmed);
        if (segments == 0)
        {
            _headline.Text = "No road check yet.";
            _sub.Text = "Click “1. Area check” (a few minutes). Then “2. On-road check” for radar points on the road surface itself.";
        }
        else
        {
            _headline.Text = _items.Count == 0
                ? $"All {segments:N0} provincial road segments checked — no stretch sinks faster than its surroundings."
                : $"{_items.Count} provincial road stretches move differently from the ground around them" + (confirmed > 0 ? $" — {confirmed} confirmed by two satellites." : ".");
            _sub.Text = $"{segments:N0} provincial road segments checked with Copernicus radar-satellite measurements (EGMS). " +
                        (onRoad.Count > 0 ? $"On-road check: {onRoad.Count(r => r.Status is "Priority" or "Review" or "No unusual movement"):N0} segments had enough radar points on the road surface. " :
                                            "Tip: run the on-road check for stronger evidence. ");
        }

        _kpis.Children.Clear();
        if (segments > 0)
        {
            _kpis.Children.Add(Tile($"{segments:N0}", "provincial road segments checked", "#3b82f6"));
            _kpis.Children.Add(Tile($"{_items.Count(i => i.Level == "Priority")}", "stretches: Priority", Red));
            _kpis.Children.Add(Tile($"{_items.Count(i => i.Level == "Review")}", "stretches: Review", Orange));
            _kpis.Children.Add(Tile($"{confirmed}", "confirmed by 2 satellite directions", Purple));
        }
        BuildList();
        _detail.Children.Clear();
        if (_items.Count > 0) Select(_items[0]);
        else _detail.Children.Add(Am.M("Nothing to show yet."));
    }

    private static Control Tile(string big, string caption, string color)
    {
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(new TextBlock { Text = big, FontSize = 32, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse(color)) });
        sp.Children.Add(Am.P(caption));
        var inner = new Border { BorderThickness = new Thickness(4, 0, 0, 0), BorderBrush = new SolidColorBrush(Color.Parse(color)), Padding = new Thickness(12, 2, 4, 2), Child = sp };
        var card = Am.Card(inner);
        card.Width = 240;
        card.Margin = new Thickness(0, 0, 12, 12);
        return card;
    }

    private void BuildList()
    {
        _list.Children.Clear();
        _listItems.Clear();
        int rank = 0;
        foreach (var it in _items.Take(150))
        {
            rank++;
            var item = it;
            var color = it.Level == "Priority" ? Red : Orange;
            var sp = new StackPanel { Spacing = 2 };
            sp.Children.Add(new TextBlock
            {
                Text = $"#{rank}  " + (it.Confirmed ? "✔✔ ON-ROAD · 2 SATELLITES" : it.Source == "On-road" ? "✔ ON-ROAD · 1 satellite direction" : "AREA CHECK (100 m grid)"),
                FontSize = 10, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse(it.Confirmed ? Purple : "#94a3b8"))
            });
            sp.Children.Add(Am.P(it.Label + (string.IsNullOrWhiteSpace(it.Municipality) ? "" : $" · {it.Municipality}")));
            sp.Children.Add(new TextBlock { Text = $"{Math.Abs(it.Diff ?? 0):0.0} mm/yr {(it.Diff < 0 ? "more sinking" : "different")} than the ground · {it.Level}", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse(color)) });
            var border = new Border
            {
                Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(3, 0, 0, 0),
                BorderBrush = new SolidColorBrush(Color.Parse(color)), Background = Brushes.Transparent, Child = sp,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            border.PointerPressed += (_, _) => Select(item);
            _listItems[it] = border;
            _list.Children.Add(border);
        }
        if (_items.Count > 150) _list.Children.Add(Am.M($"… and {_items.Count - 150} more (weakest signals)."));
    }

    private void Select(RoadItem it)
    {
        foreach (var (k, v) in _listItems)
            v.Background = k == it ? new SolidColorBrush(Color.Parse("#223b82f6")) : Brushes.Transparent;
        _detail.Children.Clear();
        var color = it.Level == "Priority" ? Red : Orange;

        _detail.Children.Add(new TextBlock
        {
            Text = it.Confirmed ? "✔✔  ON THE ROAD SURFACE · CONFIRMED BY TWO SATELLITE DIRECTIONS"
                 : it.Source == "On-road" ? "✔  ON THE ROAD SURFACE · ONE SATELLITE DIRECTION" : "AREA CHECK (100 m GRID ALONG THE ROAD)",
            FontSize = 12, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse(it.Confirmed ? Purple : "#94a3b8"))
        });
        var title = Am.H(it.Label);
        title.FontSize = 20;
        _detail.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(it.Municipality)) _detail.Children.Add(Am.M($"Municipality: {it.Municipality} · managed by the Province of Groningen"));

        double d = it.Diff ?? 0;
        string sentence = it.S != null
            ? $"The road surface moves {Math.Abs(d):0.0} mm per year {(d < 0 ? "more away from the satellite" : "more towards the satellite")} than the ground around it" +
              (d < 0 ? " — in plain words: this stretch sinks faster than its surroundings." : ".")
            : Math.Abs(it.A?.UpDiff ?? 0) >= Math.Abs(it.A?.EastDiff ?? 0)
                ? $"The ground along this road {(d < 0 ? "sinks" : "rises")} {Math.Abs(d):0.0} mm per year {(d < 0 ? "faster" : "more")} than the area around it."
                : $"The ground along this road moves {Math.Abs(d):0.0} mm per year sideways ({(d < 0 ? "west" : "east")}) compared with the area around it.";
        _detail.Children.Add(new TextBlock { Text = sentence, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse(color)) });

        if (it.S != null)
        {
            var ev = _evidence.GetValueOrDefault(it.Id);
            if (ev != null && ev.HasMapPositions)
            {
                _detail.Children.Add(Am.Card(new RoadEvidenceView(ev, it.S.Track)));
                var barBox = new StackPanel { Spacing = 6 };
                barBox.Children.Add(Am.H("Road vs ground, per satellite view"));
                barBox.Children.Add(Am.M("Each pair: the ground around the road (left) and the road surface (right). Bars going down = moving away from the satellite (sinking)."));
                barBox.Children.Add(AmBridgeCheckPanel.Bars(ev.Tracks, "Road"));
                _detail.Children.Add(Am.Card(barBox));
            }
            else if (ev != null)
            {
                _detail.Children.Add(Am.M("ⓘ Click “2. On-road check” once more to see these points on a street map and along the road (no new download needed)."));
                var plot = new RadarPlot(ev) { Height = 340 };
                _detail.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = plot });
                _detail.Children.Add(AmBridgeCheckPanel.Bars(ev.Tracks, "Road"));
            }
            _detail.Children.Add(Am.M($"Strongest direction: {it.S.Track} · {it.S.DeckN} points on the road, {it.S.RingN} around it · " +
                                      $"seen by {it.S.TracksFlagging} of {it.S.TracksQualifying} satellite directions"));
        }
        else if (it.A != null)
        {
            var a = it.A;
            _detail.Children.Add(Am.P($"Vertical: along the road {Am.F(a.UpLocal)} mm/yr · around it {Am.F(a.UpGround)} mm/yr · difference {Am.F(a.UpDiff)} mm/yr"));
            _detail.Children.Add(Am.P($"Sideways (east-west) difference: {Am.F(a.EastDiff)} mm/yr · satellite grid points: {a.LocalN} along the road, {a.GroundN} around"));
            _detail.Children.Add(Am.M("This is the area check (100 m grid). Run “2. On-road check” to see whether the road surface itself shows the same movement."));
        }

        if (it.S != null) _detail.Children.Add(Am.Card(new TimeSeriesView("roads", new List<string> { it.Id }, "Road")));
        _detail.Children.Add(Am.Card(new QuakesView(it.Lat, it.Lon)));

        var actions = new WrapPanel();
        if (it.Lat.HasValue && it.Lon.HasValue)
        {
            actions.Children.Add(Am.Btn("📍 Show on map", (_, _) => AmNav.ShowOnMap(it.Id, it.Lat!.Value, it.Lon!.Value)));
            var coords = $"{it.Lat.Value.ToString("0.00000", CultureInfo.InvariantCulture)}, {it.Lon.Value.ToString("0.00000", CultureInfo.InvariantCulture)}";
            actions.Children.Add(Am.Btn("📋 Copy location", async (_, _) =>
            {
                var cb = TopLevel.GetTopLevel(this)?.Clipboard;
                if (cb != null) await cb.SetTextAsync(coords);
            }));
        }
        var tasks = LoadTasks();
        var task = tasks.FirstOrDefault(t => t.AssetId == it.Id || t.PartIds.Contains(it.Id));
        if (task == null)
            actions.Children.Add(Am.Btn("➕ Add to inspection list", (_, _) =>
            {
                tasks.Add(new InspectionTask
                {
                    AssetId = it.Id, OwnerType = "Provincie (province)", OwnerName = "Provincie Groningen · " + it.Label,
                    Screening = it.Level, MaxAbsMmPerYear = Math.Abs(it.Diff ?? 0), Lat = it.Lat, Lon = it.Lon,
                    Aircraft = "Fixed-wing VTOL (Loong 2160) — road corridor survey", PartIds = new List<string> { it.Id },
                    EgmsRelease = ReadJson(it.S != null ? RoadPointsAudit : RoadsAudit)?["egms_release"]?.ToString() ?? "",
                    CreatedUtc = DateTime.UtcNow.ToString("o")
                });
                SaveTasks(tasks);
                Select(it);
            }));
        else
            actions.Children.Add(Am.P(task.Nen2767Score is int sc ? $"✅ Inspected — NEN 2767 score {sc}" : $"📋 On the inspection list ({task.Status})"));
        _detail.Children.Add(actions);
    }

    private async Task RunAsync(string script, params string[] args)
    {
        _areaBtn.IsEnabled = _pointsBtn.IsEnabled = false;
        _runStatus.Text = "Working… (the status line shows progress; large files can take a while)";
        try
        {
            var r = await RunScriptAsync(script, line => Dispatcher.UIThread.Post(() => _runStatus.Text = line), args);
            _runStatus.Text = r.ExitCode == 0 ? "Finished." : $"Script ended with code {r.ExitCode}. Full output: {r.ConsoleLog}";
            Load();
            _runSummary.Text = r.Summary;
        }
        catch (Exception ex) { _runStatus.Text = $"Could not run the road check: {ex.Message}"; }
        finally { _areaBtn.IsEnabled = _pointsBtn.IsEnabled = File.Exists(TokenFile); }
    }
}
