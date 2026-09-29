using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using InfraDroneDesktop.Services;
using static InfraDroneDesktop.Services.AssetMonitorService;

namespace InfraDroneDesktop.Views;

// =====================================================================================
// Shared UI helpers (theme-aware via the app's existing DynamicResource keys)
// =====================================================================================
internal static class Am
{
    public static T Dyn<T>(this T c, AvaloniaProperty p, string key) where T : Control
    {
        c.Bind(p, c.GetResourceObservable(key));
        return c;
    }

    public static TextBlock H(string t) =>
        new TextBlock { Text = t, FontSize = 15, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap }
            .Dyn(TextBlock.ForegroundProperty, "AppTextPrimary");

    public static TextBlock P(string t) =>
        new TextBlock { Text = t, FontSize = 12, TextWrapping = TextWrapping.Wrap }
            .Dyn(TextBlock.ForegroundProperty, "AppTextPrimary");

    public static TextBlock M(string t) =>
        new TextBlock { Text = t, FontSize = 11, TextWrapping = TextWrapping.Wrap }
            .Dyn(TextBlock.ForegroundProperty, "AppTextMuted");

    public static TextBlock Mono(string t) =>
        new TextBlock { Text = t, FontSize = 12, FontFamily = new FontFamily("Consolas,DejaVu Sans Mono,monospace"), TextWrapping = TextWrapping.Wrap }
            .Dyn(TextBlock.ForegroundProperty, "AppTextPrimary");

    public static Border Card(Control child) =>
        new Border
        {
            Padding = new Thickness(12), CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(0.5), Child = child
        }.Dyn(Border.BackgroundProperty, "AppPanelBg").Dyn(Border.BorderBrushProperty, "AppPanelBorder");

    public static Button Btn(string text, EventHandler<RoutedEventArgs> onClick)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(0.5), Margin = new Thickness(0, 0, 8, 8)
        };
        b.Dyn(Button.BackgroundProperty, "AppAccentBg").Dyn(Button.ForegroundProperty, "AppAccentFg")
         .Dyn(Button.BorderBrushProperty, "AppAccentFg");
        b.Click += onClick;
        return b;
    }

    public static IBrush StatusBrush(string s) => s switch
    {
        "Priority" => Brush.Parse("#ef4444"),
        "Review" => Brush.Parse("#f59e0b"),
        "No unusual movement" => Brush.Parse("#0d9e75"),
        _ => Brush.Parse("#64748b")
    };

    public static string F(double? v, string fmt = "0.0") =>
        v.HasValue ? v.Value.ToString(fmt, CultureInfo.InvariantCulture) : "—";

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("xdg-open", path) { UseShellExecute = false });
        }
        catch (Exception ex) { Console.WriteLine($"[AssetMonitor] could not open {path}: {ex.Message}"); }
    }

    /// <summary>Accepts "12500", "12.500" (Dutch thousands), "12,5" (Dutch decimal) and "12.5".</summary>
    public static double? Num(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Replace("€", "").Replace(" ", "").Trim();
        if (s.Contains('.') && s.Contains(',')) s = s.Replace(".", "").Replace(',', '.');
        else if (s.Contains(',')) s = s.Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static Grid Row(string cols, params Control[] cells)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(cols), Margin = new Thickness(0, 1) };
        for (int i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            cells[i].VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(cells[i]);
        }
        return g;
    }
}

internal abstract class AmPanel : UserControl
{
    protected readonly StackPanel Root = new() { Spacing = 12, Margin = new Thickness(16) };

    protected AmPanel()
    {
        Content = new ScrollViewer
        {
            Content = Root,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    protected static void Ui(Action a) => Dispatcher.UIThread.Post(a);
}

// =====================================================================================
// 1. Asset Register — who manages each road and bridge (register values only)
// =====================================================================================
internal sealed class AmRegisterPanel : AmPanel
{
    private readonly TextBlock _status = Am.M("");
    private readonly TextBlock _summary = Am.Mono("");
    private readonly StackPanel _bridgeCard = new() { Spacing = 3 };
    private readonly StackPanel _roadCard = new() { Spacing = 3 };
    private readonly ComboBox _dataset = new() { ItemsSource = new[] { "Bridges", "Roads" }, SelectedIndex = 0, Width = 140, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ComboBox _type = new() { Width = 280 };
    private readonly StackPanel _names = new() { Spacing = 2 };
    private readonly Button _refresh;
    private OwnerStats _bridges = new(), _roads = new();
    private bool _loaded;

    public AmRegisterPanel()
    {
        Root.Children.Add(Am.H("🗂 Asset Register — who manages each road and bridge"));
        Root.Children.Add(Am.P(
            "Sources (live, official): NWB (Nationaal Wegenbestand, National Road Database, Rijkswaterstaat) for road managers, " +
            "and BGT (Basisregistratie Grootschalige Topografie, Large-Scale Topography Register, Kadaster) for bridge source-keepers. " +
            "Filtered to the official Province of Groningen boundary. Only register values are shown — nothing is inferred."));

        _refresh = Am.Btn("🔄 Refresh from official registers", async (_, _) => await RefreshAsync());
        var buttons = new WrapPanel();
        buttons.Children.Add(_refresh);
        buttons.Children.Add(Am.Btn("📂 Open data folder", (_, _) => Am.OpenFolder(OwnershipOut)));
        Root.Children.Add(buttons);
        Root.Children.Add(_status);
        Root.Children.Add(_summary);

        var cards = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        var c1 = Am.Card(_bridgeCard); var c2 = Am.Card(_roadCard);
        Grid.SetColumn(c2, 2);
        cards.Children.Add(c1); cards.Children.Add(c2);
        Root.Children.Add(cards);

        var filter = new StackPanel { Spacing = 8 };
        filter.Children.Add(Am.H("Filter by manager"));
        var pickers = new StackPanel { Orientation = Orientation.Horizontal };
        pickers.Children.Add(_dataset);
        pickers.Children.Add(_type);
        filter.Children.Add(pickers);
        filter.Children.Add(_names);
        Root.Children.Add(Am.Card(filter));

        _dataset.SelectionChanged += (_, _) => UpdateTypes();
        _type.SelectionChanged += (_, _) => UpdateNames();
        AttachedToVisualTree += async (_, _) => { if (!_loaded) { _loaded = true; await LoadAsync(); } };
    }

    private async Task LoadAsync()
    {
        _status.Text = "Reading register files…";
        try
        {
            (_bridges, _roads) = await Task.Run(() => (ReadOwnerStats(BridgesFile), ReadOwnerStats(RoadsFile)));
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not read register files: {ex.Message}";
            return;
        }
        Fill(_bridgeCard, "🌉 Bridges (BGT bridge decks)", _bridges);
        Fill(_roadCard, "🛣 Roads (NWB road segments)", _roads);
        _status.Text = _bridges.Total == 0 && _roads.Total == 0
            ? "No register files yet — click “Refresh from official registers”."
            : $"Register files — bridges: {_bridges.FileTime:yyyy-MM-dd HH:mm}, roads: {_roads.FileTime:yyyy-MM-dd HH:mm}";
        UpdateTypes();
    }

    private static void Fill(StackPanel p, string title, OwnerStats s)
    {
        p.Children.Clear();
        p.Children.Add(Am.H(title));
        if (s.Total == 0) { p.Children.Add(Am.M("No data yet.")); return; }
        p.Children.Add(Am.P($"Total in province: {s.Total:N0}"));
        foreach (var kv in s.ByType.OrderByDescending(k => k.Value))
            p.Children.Add(Am.P($"{kv.Value:N0}  ·  {kv.Key}"));
        if (s.Flagged > 0)
            p.Children.Add(new TextBlock
            {
                Text = $"⚠ {s.Flagged:N0} registered under a municipality code that is not in today's official municipality list — the register entry needs updating by its keeper.",
                Foreground = Brush.Parse("#f59e0b"), FontSize = 12, TextWrapping = TextWrapping.Wrap
            });
    }

    private OwnerStats Current => _dataset.SelectedIndex == 1 ? _roads : _bridges;

    private void UpdateTypes()
    {
        var types = new List<string> { "All managers" };
        types.AddRange(Current.ByType.Keys.OrderBy(k => k));
        _type.ItemsSource = types;
        _type.SelectedIndex = 0;
        UpdateNames();
    }

    private void UpdateNames()
    {
        _names.Children.Clear();
        var stats = Current;
        var chosen = _type.SelectedItem as string;
        var counts = new Dictionary<string, int>();
        foreach (var (type, names) in stats.NamesByType)
        {
            if (chosen != null && chosen != "All managers" && type != chosen) continue;
            foreach (var (name, n) in names) counts[name] = counts.GetValueOrDefault(name) + n;
        }
        if (counts.Count == 0) { _names.Children.Add(Am.M("No data.")); return; }
        foreach (var (name, n) in counts.OrderByDescending(k => k.Value).Take(200))
            _names.Children.Add(Am.Row("90,*", Am.Mono($"{n:N0}"), Am.P(name)));
        if (counts.Count > 200) _names.Children.Add(Am.M($"… and {counts.Count - 200} more"));
    }

    private async Task RefreshAsync()
    {
        _refresh.IsEnabled = false;
        _summary.Text = "";
        _status.Text = "Downloading live register data (about 10 minutes)…";
        try
        {
            var r = await RunScriptAsync(OwnershipScript, line => Ui(() => _status.Text = line));
            _summary.Text = r.Summary;
            _status.Text = r.ExitCode == 0 ? "Finished." : $"Script ended with code {r.ExitCode}. Full output: {r.ConsoleLog}";
            await LoadAsync();
        }
        catch (Exception ex) { _status.Text = $"Could not run the register script: {ex.Message}"; }
        finally { _refresh.IsEnabled = true; }
    }
}

// =====================================================================================
// 2. Satellite Screening — EGMS ground motion at every bridge
// =====================================================================================
internal sealed class AmSatellitePanel : AmPanel
{
    private readonly TextBlock _token = Am.P("");
    private readonly TextBlock _status = Am.M("");
    private readonly TextBlock _summary = Am.Mono("");
    private readonly StackPanel _counts = new() { Spacing = 3 };
    private readonly StackPanel _list = new() { Spacing = 1 };
    private readonly Button _run;

    public AmSatellitePanel()
    {
        Root.Children.Add(Am.H("🛰 Satellite Screening — is any bridge moving differently from its surroundings?"));
        Root.Children.Add(Am.P(
            "Data: EGMS (European Ground Motion Service, Copernicus), from Sentinel-1 radar satellites. " +
            "The newest release is downloaded automatically. EGMS is published once a year, so screening is yearly. " +
            "For each bridge the software compares ground movement at the bridge (within 150 m) with its surroundings (150 m – 1 km)."));
        Root.Children.Add(Am.M(
            "Limitations shown honestly: the Ortho product is a 100 m grid, so this screens the ground at and around a bridge, not the deck itself. " +
            "The Review (2 mm/year) and Priority (4 mm/year) thresholds are placeholder settings that must be agreed with the province's engineers."));

        Root.Children.Add(_token);
        _run = Am.Btn("▶ Run satellite screening", async (_, _) => await RunAsync());
        var buttons = new WrapPanel();
        buttons.Children.Add(_run);
        buttons.Children.Add(Am.Btn("📂 Open results folder", (_, _) => Am.OpenFolder(ScreeningOut)));
        Root.Children.Add(buttons);
        Root.Children.Add(_status);
        Root.Children.Add(_summary);
        Root.Children.Add(Am.Card(_counts));

        var listBox = new StackPanel { Spacing = 6 };
        listBox.Children.Add(Am.H("Ranked by movement difference (top 100)"));
        listBox.Children.Add(_list);
        Root.Children.Add(Am.Card(listBox));

        AttachedToVisualTree += (_, _) => { CheckToken(); _ = LoadAsync(); };
    }

    private void CheckToken()
    {
        _token.Text = File.Exists(TokenFile)
            ? "✅ Copernicus access token found."
            : "⚠ No Copernicus access token yet. Get a free one at land.copernicus.eu (log in → your profile → API tokens → create), " +
              $"then save the file as: {TokenFile}";
        _run.IsEnabled = File.Exists(TokenFile);
    }

    private async Task LoadAsync()
    {
        List<ScreenedBridge> rows;
        try { rows = await Task.Run(() => ReadRanked()); }
        catch (Exception ex) { _status.Text = $"Could not read results: {ex.Message}"; return; }

        _counts.Children.Clear();
        _list.Children.Clear();
        if (rows.Count == 0) { _counts.Children.Add(Am.M("No screening results yet — run the screening.")); return; }

        var release = ReadManifest()?["egms_release"]?.ToString() ?? "?";
        var flaggedBridges = GroupParts(rows.Where(r => r.Screening is "Priority" or "Review")).Count;
        _counts.Children.Add(Am.H($"Results — EGMS release {release}, {rows.Count:N0} bridge deck parts"));
        _counts.Children.Add(Am.M($"Flagged deck parts (Priority + Review) belong to about {flaggedBridges:N0} separate bridge locations."));
        foreach (var g in rows.GroupBy(r => r.Screening).OrderByDescending(g => g.Count()))
            _counts.Children.Add(new TextBlock { Text = $"{g.Count():N0}  ·  {g.Key}", FontSize = 13, Foreground = Am.StatusBrush(g.Key) });

        var grouped = GroupParts(rows.Where(r => r.MaxAbs.HasValue));
        _list.Children.Add(Am.M(
            "All values in mm per year. Negative vertical = sinking. “Vertical diff” = movement at the bridge minus its surroundings. " +
            "“Sideways diff” = the same for east-west movement. The status uses whichever difference is larger. " +
            "“Points” = satellite measurement points at the bridge / in the surroundings (few points = weaker evidence). " +
            "Deck parts of one bridge with identical measurements are shown as one row."));
        const string cols = "36,90,*,70,85,85,85,85,70";
        _list.Children.Add(Am.Row(cols, Am.M("#"), Am.M("Status"), Am.M("Registered manager"), Am.M("Parts"),
            Am.M("Vertical diff"), Am.M("Sideways diff"), Am.M("At bridge ↕"), Am.M("Surround. ↕"), Am.M("Points")));
        int i = 0;
        foreach (var r in grouped.OrderByDescending(r => r.MaxAbs).Take(100))
        {
            i++;
            _list.Children.Add(Am.Row(cols,
                Am.M(i.ToString()),
                new TextBlock { Text = r.Screening, Foreground = Am.StatusBrush(r.Screening), FontSize = 12 },
                Am.P(r.ManagerLabel),
                Am.Mono(r.Parts > 1 ? $"×{r.Parts}" : "1"),
                Am.Mono(Am.F(r.UpDiff)),
                Am.Mono(Am.F(r.EastDiff)),
                Am.Mono(Am.F(r.UpLocal)),
                Am.Mono(Am.F(r.UpGround)),
                Am.Mono($"{r.LocalN}/{r.GroundN}")));
        }
    }

    private async Task RunAsync()
    {
        _run.IsEnabled = false;
        _summary.Text = "";
        _status.Text = "Connecting to EGMS… (first run downloads the satellite files; this can take several minutes)";
        try
        {
            var r = await RunScriptAsync(ScreeningScript, line => Ui(() => _status.Text = line));
            _summary.Text = r.Summary;
            _status.Text = r.ExitCode == 0 ? "Finished." : $"Script ended with code {r.ExitCode}. Full output: {r.ConsoleLog}";
            await LoadAsync();
        }
        catch (Exception ex) { _status.Text = $"Could not run the screening script: {ex.Message}"; }
        finally { CheckToken(); }
    }
}

// =====================================================================================
// 3. Inspection Tasks — only flagged bridges get a drone inspection
// =====================================================================================
internal sealed class AmTasksPanel : AmPanel
{
    private static readonly string[] Statuses = { "Open", "Scheduled", "Inspected", "Closed" };
    private readonly TextBlock _status = Am.M("");
    private readonly StackPanel _list = new() { Spacing = 2 };
    private List<InspectionTask> _tasks = new();

    public AmTasksPanel()
    {
        Root.Children.Add(Am.H("📋 Inspection Tasks — the drone goes only where the satellite flagged something"));
        Root.Children.Add(Am.P(
            "Every bridge marked Priority or Review by the satellite screening becomes a task. " +
            "Existing tasks keep their status when you create tasks from a newer screening. Tasks are saved in: " + TasksFile));
        var buttons = new WrapPanel();
        buttons.Children.Add(Am.Btn("➕ Create tasks from latest screening", (_, _) =>
        {
            try
            {
                var (added, existing) = CreateTasksFromScreening();
                _status.Text = $"Added {added} new tasks ({existing} were already in the list).";
            }
            catch (Exception ex) { _status.Text = $"Could not create tasks: {ex.Message}"; }
            Reload();
        }));
        Root.Children.Add(buttons);
        Root.Children.Add(_status);
        Root.Children.Add(Am.Card(_list));
        AttachedToVisualTree += (_, _) => Reload();
    }

    private void Reload()
    {
        _tasks = LoadTasks();
        _list.Children.Clear();
        if (_tasks.Count == 0) { _list.Children.Add(Am.M("No tasks yet. Run the satellite screening, then click “Create tasks”.")); return; }

        const string cols = "90,*,60,80,170,150,140";
        _list.Children.Add(Am.Row(cols, Am.M("Screening"), Am.M("Registered manager"), Am.M("Parts"), Am.M("mm/yr"),
            Am.M("Location (lat, lon)"), Am.M("Aircraft"), Am.M("Status")));
        foreach (var t in _tasks.OrderBy(t => t.Screening == "Priority" ? 0 : 1).ThenByDescending(t => t.MaxAbsMmPerYear))
        {
            var task = t;
            var combo = new ComboBox { ItemsSource = Statuses, SelectedItem = task.Status, Width = 130 };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is string s && s != task.Status)
                {
                    task.Status = s;
                    SaveTasks(_tasks);
                    _status.Text = $"Saved: {task.OwnerName} → {s}";
                }
            };
            var coords = task.Lat.HasValue && task.Lon.HasValue
                ? $"{task.Lat.Value.ToString("0.00000", CultureInfo.InvariantCulture)}, {task.Lon.Value.ToString("0.00000", CultureInfo.InvariantCulture)}"
                : "—";
            var copy = new Button { Content = "📋 " + coords, FontSize = 11, Padding = new Thickness(6, 2) };
            ToolTip.SetTip(copy, "Copy coordinates (paste into Mission Planner)");
            copy.Click += async (_, _) =>
            {
                var cb = TopLevel.GetTopLevel(this)?.Clipboard;
                if (cb != null && coords != "—") { await cb.SetTextAsync(coords); _status.Text = $"Copied {coords}"; }
            };
            _list.Children.Add(Am.Row(cols,
                new TextBlock { Text = task.Screening, Foreground = Am.StatusBrush(task.Screening), FontSize = 12 },
                Am.P(OwnerLabel(task.OwnerType, task.OwnerName)),
                Am.Mono(task.Parts > 1 ? $"×{task.Parts}" : "1"),
                Am.Mono(Am.F(task.MaxAbsMmPerYear)),
                copy,
                Am.M(task.Aircraft),
                combo));
        }
    }
}

// =====================================================================================
// 4. Condition — NEN 2767 score recorded by the inspector after the flight
// =====================================================================================
internal sealed class AmConditionPanel : AmPanel
{
    private static readonly string[] Scores =
        { "— not scored", "1 · Excellent", "2 · Good", "3 · Reasonable", "4 · Moderate", "5 · Poor", "6 · Very poor" };
    private readonly TextBlock _status = Am.M("");
    private readonly StackPanel _overview = new() { Spacing = 3 };
    private readonly StackPanel _list = new() { Spacing = 6 };
    private List<InspectionTask> _tasks = new();

    public AmConditionPanel()
    {
        Root.Children.Add(Am.H("🏗 Condition — NEN 2767 score per inspected asset"));
        Root.Children.Add(Am.P(
            "NEN 2767 is the Dutch standard for rating the condition of built assets, on a scale from 1 (excellent) to 6 (very poor). " +
            "Province engineers already plan maintenance with it, so results are delivered in the same scale they use today."));
        Root.Children.Add(Am.M(
            "The score is set by a qualified inspector from the drone imagery and site visit. AI detections support the inspector; they do not set the score."));
        Root.Children.Add(_status);
        Root.Children.Add(Am.Card(_overview));
        Root.Children.Add(_list);
        AttachedToVisualTree += (_, _) => Reload();
    }

    private void Reload()
    {
        _tasks = LoadTasks();
        _overview.Children.Clear();
        _list.Children.Clear();
        if (_tasks.Count == 0) { _overview.Children.Add(Am.M("No inspection tasks yet — create them in the Inspection Tasks sub-tab.")); return; }

        var scored = _tasks.Where(t => t.Nen2767Score.HasValue).ToList();
        _overview.Children.Add(Am.H($"{scored.Count} of {_tasks.Count} flagged assets scored"));
        foreach (var g in scored.GroupBy(t => t.Nen2767Score!.Value).OrderBy(g => g.Key))
            _overview.Children.Add(Am.P($"{g.Count()}  ·  {Scores[g.Key]}"));

        foreach (var t in _tasks.OrderBy(t => t.Nen2767Score.HasValue ? 1 : 0).ThenBy(t => t.OwnerName))
        {
            var task = t;
            var score = new ComboBox { ItemsSource = Scores, SelectedIndex = task.Nen2767Score ?? 0, Width = 160, Margin = new Thickness(0, 0, 8, 0) };
            var inspector = new TextBox { Text = task.Inspector, Watermark = "Inspector name", Width = 160, Margin = new Thickness(0, 0, 8, 0) };
            var notes = new TextBox { Text = task.Notes, Watermark = "Findings / notes", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 50 };
            var save = Am.Btn("💾 Save", (_, _) =>
            {
                int idx = score.SelectedIndex;
                task.Nen2767Score = idx <= 0 ? null : idx;
                task.Inspector = inspector.Text ?? "";
                task.Notes = notes.Text ?? "";
                if (task.Nen2767Score.HasValue)
                {
                    task.Status = "Inspected";
                    task.InspectedUtc = DateTime.UtcNow.ToString("o");
                }
                SaveTasks(_tasks);
                _status.Text = $"Saved condition for {task.OwnerName}.";
                Reload();
            });

            var body = new StackPanel { Spacing = 6 };
            body.Children.Add(Am.P($"{OwnerLabel(task.OwnerType, task.OwnerName)}   ·   screening: {task.Screening} ({Am.F(task.MaxAbsMmPerYear)} mm/yr)   ·   status: {task.Status}"));
            if (!string.IsNullOrEmpty(task.InspectedUtc)) body.Children.Add(Am.M($"Last scored (UTC): {task.InspectedUtc}"));
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(score);
            line.Children.Add(inspector);
            body.Children.Add(line);
            body.Children.Add(notes);
            body.Children.Add(save);
            _list.Children.Add(Am.Card(body));
        }
    }
}

// =====================================================================================
// 5. Value & Audit — savings from the province's own numbers + full audit trail
// =====================================================================================
internal sealed class AmValueAuditPanel : AmPanel
{
    private readonly TextBox _n = Box("e.g. 14246");
    private readonly TextBox _f = Box("from screening");
    private readonly TextBox _cost = Box("€ per inspection");
    private readonly TextBox _cycle = Box("years");
    private readonly TextBox _riskCycle = Box("years");
    private readonly TextBox _platform = Box("€ per year");
    private readonly StackPanel _result = new() { Spacing = 3 };
    private readonly StackPanel _audit = new() { Spacing = 3 };

    private static TextBox Box(string hint) => new() { Watermark = hint, Width = 170 };

    public AmValueAuditPanel()
    {
        // ---------------- Value
        Root.Children.Add(Am.H("💶 Value — what does targeted inspection save?"));
        Root.Children.Add(Am.P(
            "Today every asset is inspected on a fixed cycle. With satellite screening, flagged assets are inspected straight away, " +
            "and assets with no unusual movement can follow a longer, risk-based cycle — if the province's engineers and any legal requirements allow it. " +
            "The calculator only uses numbers you enter plus the real screening counts. The software does not estimate any cost."));

        var form = new StackPanel { Spacing = 6 };
        form.Children.Add(Am.Row("330,*", Am.P("Assets in scope (from register, editable)"), _n));
        form.Children.Add(Am.Row("330,*", Am.P("Flagged by screening: Priority + Review (editable)"), _f));
        form.Children.Add(Am.Row("330,*", Am.P("Cost of one inspection (€) — province's figure"), _cost));
        form.Children.Add(Am.Row("330,*", Am.P("Current inspection cycle (years)"), _cycle));
        form.Children.Add(Am.Row("330,*", Am.P("Agreed risk-based cycle for unflagged assets (years)"), _riskCycle));
        form.Children.Add(Am.Row("330,*", Am.P("Yearly cost of screening platform (€)"), _platform));
        form.Children.Add(Am.Btn("🧮 Calculate", (_, _) => Calculate()));
        form.Children.Add(_result);
        Root.Children.Add(Am.Card(form));

        // ---------------- Audit
        Root.Children.Add(Am.H("🔍 Audit — every flag can be traced and re-checked"));
        Root.Children.Add(Am.P(
            "Each screening run writes an audit manifest: the exact satellite files used (with SHA-256 checksums, a digital fingerprint that changes if a single byte changes), " +
            "the register file used, the settings, and the time. An auditor can re-run the same inputs later and must get the same flags."));
        var buttons = new WrapPanel();
        buttons.Children.Add(Am.Btn("🔄 Reload audit record", (_, _) => LoadAudit()));
        buttons.Children.Add(Am.Btn("📂 Open audit folder", (_, _) => Am.OpenFolder(ScreeningOut)));
        Root.Children.Add(buttons);
        Root.Children.Add(Am.Card(_audit));

        AttachedToVisualTree += (_, _) => { Prefill(); LoadAudit(); };
    }

    private void Prefill()
    {
        try
        {
            var rows = ReadRanked();
            if (rows.Count == 0) return;
            if (string.IsNullOrWhiteSpace(_n.Text)) _n.Text = rows.Count.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(_f.Text))
                _f.Text = rows.Count(r => r.Screening is "Priority" or "Review").ToString(CultureInfo.InvariantCulture);
        }
        catch { /* leave the fields for manual entry */ }
    }

    private void Calculate()
    {
        _result.Children.Clear();
        var n = Am.Num(_n.Text); var f = Am.Num(_f.Text); var c = Am.Num(_cost.Text);
        var t = Am.Num(_cycle.Text); var tr = Am.Num(_riskCycle.Text); var p = Am.Num(_platform.Text);

        var missing = new List<string>();
        if (n is null) missing.Add("assets in scope");
        if (f is null) missing.Add("flagged count");
        if (c is null) missing.Add("cost per inspection");
        if (t is null or <= 0) missing.Add("current cycle");
        if (tr is null or <= 0) missing.Add("risk-based cycle");
        if (p is null) missing.Add("platform cost");
        if (missing.Count > 0) { _result.Children.Add(Am.P("Please fill in: " + string.Join(", ", missing))); return; }

        double current = n!.Value * c!.Value / t!.Value;
        double targeted = f!.Value * c.Value + (n.Value - f.Value) * c.Value / tr!.Value + p!.Value;
        double diff = current - targeted;
        string E(double v) => "€ " + v.ToString("N0", CultureInfo.InvariantCulture).Replace(",", ".");   // Dutch thousands separator

        _result.Children.Add(Am.Mono($"Today (fixed cycle):  N × cost ÷ cycle = {E(current)} per year"));
        _result.Children.Add(Am.Mono($"Targeted:  flagged × cost + (N − flagged) × cost ÷ risk cycle + platform = {E(targeted)} per year"));
        _result.Children.Add(new TextBlock
        {
            Text = diff >= 0 ? $"Saving: {E(diff)} per year ({diff / current:P0})" : $"No saving with these inputs: {E(-diff)} per year more expensive",
            FontSize = 15, FontWeight = FontWeight.Bold,
            Foreground = Brush.Parse(diff >= 0 ? "#0d9e75" : "#ef4444")
        });
        _result.Children.Add(Am.M("Legal minimum inspection intervals always take precedence over this calculation."));
    }

    private void LoadAudit()
    {
        _audit.Children.Clear();
        var m = ReadManifest();
        if (m == null) { _audit.Children.Add(Am.M("No audit record yet — run the satellite screening first.")); return; }

        _audit.Children.Add(Am.H("Latest screening run"));
        _audit.Children.Add(Am.P($"Run (UTC): {m["run_utc"]}    ·    Script: {m["script"]}"));
        _audit.Children.Add(Am.P($"Satellite data: EGMS release {m["egms_release"]}  ({m["egms_api"]})"));
        if (m["egms_files"] is System.Text.Json.Nodes.JsonArray files)
            foreach (var fnode in files)
                _audit.Children.Add(Am.Mono($"{fnode?["productType"]}  {fnode?["filename"]}  version {fnode?["version"]}\n  SHA-256 {fnode?["sha256"]}"));
        var bi = m["bridges_input"];
        _audit.Children.Add(Am.P($"Register input: {bi?["count"]} bridges from {bi?["path"]}"));
        _audit.Children.Add(Am.Mono($"  SHA-256 {bi?["sha256"]}"));
        _audit.Children.Add(Am.P($"Asset ID field used: {m["asset_id_field"]}"));
        _audit.Children.Add(Am.H("Settings used (placeholders until agreed with the province)"));
        _audit.Children.Add(Am.Mono(m["settings_placeholders_to_agree_with_province"]?.ToJsonString() ?? "—"));
        _audit.Children.Add(Am.H("Result counts"));
        _audit.Children.Add(Am.Mono(m["result_counts"]?.ToJsonString() ?? "—"));
        _audit.Children.Add(Am.H("How an auditor verifies a flag"));
        _audit.Children.Add(Am.P("1. Download the same EGMS files named above and check their SHA-256 fingerprints match."));
        _audit.Children.Add(Am.P("2. Use the same register file (same fingerprint) and the same settings."));
        _audit.Children.Add(Am.P("3. Re-run egms_screen.py — the ranked list and every flag must come out identical."));
        _audit.Children.Add(Am.P("4. Each bridge's numbers (movement at the bridge, surroundings, number of satellite points) are in bridges_ranked.csv."));
    }
}
