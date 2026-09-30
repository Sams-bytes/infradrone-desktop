using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using static InfraDroneDesktop.Services.AssetMonitorService;

namespace InfraDroneDesktop.Views;

// =====================================================================================
// Extras for the Asset Monitor:
//   1. Movement history ("is it getting worse?")  -> TimeSeriesView  (data: egms_timeseries.py)
//   2. Earthquakes nearby (KNMI)                   -> QuakesView      (data: knmi_quakes.py)
//   3. Draft drone flight plan (.waypoints)        -> FlightPlanner
//   4. Per-asset inspection report (HTML -> PDF)   -> InspectionReport
// =====================================================================================
internal static class AmExtras
{
    public static string TimeseriesScript => Path.Combine(ToolsDir, "egms_timeseries.py");
    public static string QuakesScript => Path.Combine(ToolsDir, "knmi_quakes.py");
    public static string TimeseriesFile => Path.Combine(ScreeningOut, "timeseries.json");
    public static string QuakesFile => Path.Combine(ScreeningOut, "knmi_quakes.csv");
    public static string QuakesMeta => Path.Combine(ScreeningOut, "knmi_quakes_meta.json");
    public static string FlightPlanDir => Path.Combine(ToolsDir, "flight_plans");
    public static string InspectionsDir => Path.Combine(ToolsDir, "inspections");
    public static string AirspaceFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "agri_drone", "airspace_nl.geojson");

    public static string Safe(string id) => new string(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
    public static string AssetFolder(string assetId) => Path.Combine(InspectionsDir, Safe(assetId));

    // ---------------- movement history
    public sealed class TsTrack
    {
        public string Track = "";
        public int DeckN, GroundN;
        public List<DateTime> Dates = new();
        public List<double?> Deck = new(), Ground = new();
        public double? VAll, VFirst, VSecond, Accel, SeasonMm;
    }
    public sealed class TsAsset { public string Verdict = "", BestTrack = ""; public bool Seasonal; public List<TsTrack> Tracks = new(); }

    private static JsonNode? _tsCache; private static DateTime _tsTime;
    public static TsAsset? History(string kind, IEnumerable<string> ids)
    {
        if (!File.Exists(TimeseriesFile)) return null;
        var t = File.GetLastWriteTimeUtc(TimeseriesFile);
        if (_tsCache == null || t != _tsTime) { _tsCache = JsonNode.Parse(File.ReadAllText(TimeseriesFile)); _tsTime = t; }
        var group = _tsCache?[kind] as JsonObject;
        if (group == null) return null;
        foreach (var id in ids)
        {
            if (group[id] is not JsonObject rec) continue;
            var a = new TsAsset { Verdict = rec["verdict"]?.ToString() ?? "", BestTrack = rec["best_track"]?.ToString() ?? "", Seasonal = rec["seasonal"]?.GetValue<bool>() ?? false };
            foreach (var tr in (rec["tracks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                double? N(string k) => tr[k] is JsonNode n ? n.GetValue<double>() : null;
                a.Tracks.Add(new TsTrack
                {
                    Track = tr["track"]?.ToString() ?? "", DeckN = tr["deck_n"]?.GetValue<int>() ?? 0, GroundN = tr["ground_n"]?.GetValue<int>() ?? 0,
                    Dates = (tr["dates"] as JsonArray ?? new JsonArray()).Select(d => DateTime.ParseExact(d!.ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList(),
                    Deck = (tr["deck"] as JsonArray ?? new JsonArray()).Select(v => v == null ? (double?)null : v.GetValue<double>()).ToList(),
                    Ground = (tr["ground"] as JsonArray ?? new JsonArray()).Select(v => v == null ? (double?)null : v.GetValue<double>()).ToList(),
                    VAll = N("v_all"), VFirst = N("v_first"), VSecond = N("v_second"), Accel = N("accel"), SeasonMm = N("season_mm")
                });
            }
            return a;
        }
        return null;
    }

    // ---------------- earthquakes
    public sealed record Quake(DateTime Time, double Lat, double Lon, double? Mag, double? DepthKm, string Type, string Place);
    private static List<Quake>? _quakes; private static DateTime _qTime;
    public static List<Quake> Quakes()
    {
        if (!File.Exists(QuakesFile)) return new();
        var t = File.GetLastWriteTimeUtc(QuakesFile);
        if (_quakes != null && t == _qTime) return _quakes;
        var list = new List<Quake>();
        var lines = File.ReadAllLines(QuakesFile);
        if (lines.Length > 1)
        {
            var h = lines[0].Split(',').ToList();
            int I(string n) => h.IndexOf(n);
            int ct = I("time"), cla = I("lat"), clo = I("lon"), cm = I("magnitude"), cd = I("depth_km"), cty = I("type"), cp = I("location");
            foreach (var line in lines.Skip(1))
            {
                var p = SplitCsvLine(line);
                string G(int i) => i >= 0 && i < p.Count ? p[i] : "";
                if (!DateTime.TryParse(G(ct), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)) continue;
                if (!double.TryParse(G(cla), NumberStyles.Float, CultureInfo.InvariantCulture, out var la)) continue;
                if (!double.TryParse(G(clo), NumberStyles.Float, CultureInfo.InvariantCulture, out var lo)) continue;
                double? m = double.TryParse(G(cm), NumberStyles.Float, CultureInfo.InvariantCulture, out var mv) ? mv : null;
                double? d = double.TryParse(G(cd), NumberStyles.Float, CultureInfo.InvariantCulture, out var dv) ? dv : null;
                list.Add(new Quake(time, la, lo, m, d, G(cty), G(cp)));
            }
        }
        _quakes = list; _qTime = t;
        return list;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var r = new List<string>(); var sb = new StringBuilder(); bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q) { if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; } else sb.Append(c); }
            else if (c == '"') q = true;
            else if (c == ',') { r.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        r.Add(sb.ToString());
        return r;
    }

    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        double dLat = (lat2 - lat1) * Math.PI / 180, dLon = (lon2 - lon1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Sqrt(a));
    }

    public static string Signed(double? v, string fmt = "0.0") => v.HasValue ? v.Value.ToString("+" + fmt + ";−" + fmt + ";" + fmt, CultureInfo.InvariantCulture) : "—";

    public static string VerdictText(string verdict) => verdict switch
    {
        "Speeding up" => "⚠ GETTING WORSE — the movement is speeding up",
        "Slowing down" => "↘ SLOWING DOWN — the movement is getting smaller",
        "Steady" => "→ STEADY — moving at a constant speed, not speeding up",
        _ => "Not enough measurements to judge the trend"
    };
    public static string VerdictHex(string verdict) => verdict switch { "Speeding up" => "#ef4444", "Slowing down" => "#0d9e75", "Steady" => "#f59e0b", _ => "#64748b" };
}

// =====================================================================================
// Buttons to fetch/refresh the extra data (placed in the Bridge Check and Road Check headers)
// =====================================================================================
internal sealed class ExtrasBar : UserControl
{
    private readonly TextBlock _status = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#94a3b8")), TextWrapping = TextWrapping.Wrap };
    private readonly Button _hist, _quake;

    public ExtrasBar(Action onDone)
    {
        _hist = Am.Btn("📈 Load movement history (2020–2024)", async (_, _) => await Run(AmExtras.TimeseriesScript, onDone,
            "Reading the movement history of every flagged bridge and road from the downloaded satellite files — this can take a while…"));
        _quake = Am.Btn("🌍 Update earthquakes (KNMI)", async (_, _) => await Run(AmExtras.QuakesScript, onDone, "Downloading the official KNMI earthquake catalogue…"));
        var row = new WrapPanel();
        row.Children.Add(_hist);
        row.Children.Add(_quake);
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(row);
        sp.Children.Add(_status);
        Content = sp;
        var hasTs = File.Exists(AmExtras.TimeseriesFile); var hasQ = File.Exists(AmExtras.QuakesFile);
        _status.Text = (hasTs ? "✅ movement history loaded" : "movement history: not loaded yet") + " · " + (hasQ ? "✅ earthquakes loaded" : "earthquakes: not loaded yet");
    }

    private async Task Run(string script, Action onDone, string msg)
    {
        _hist.IsEnabled = _quake.IsEnabled = false;
        _status.Text = msg;
        try
        {
            var r = await RunScriptAsync(script, line => Dispatcher.UIThread.Post(() => _status.Text = line));
            _status.Text = r.ExitCode == 0 ? r.Summary.Replace("\n", "  ") : $"Ended with code {r.ExitCode}. Full output: {r.ConsoleLog}";
            onDone();
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _hist.IsEnabled = _quake.IsEnabled = true; }
    }
}

// =====================================================================================
// 📈 "Is it getting worse?" — movement history chart + plain verdict
// =====================================================================================
internal sealed class TimeSeriesView : UserControl
{
    public TimeSeriesView(string kind, IEnumerable<string> ids, string assetWord)
    {
        var sp = new StackPanel { Spacing = 8 };
        sp.Children.Add(Am.H("📈 Is it getting worse? — movement 2020–2024"));
        var h = AmExtras.History(kind, ids);
        if (h == null || h.Tracks.Count == 0)
        {
            sp.Children.Add(Am.M(File.Exists(AmExtras.TimeseriesFile)
                ? "No movement history for this one (not enough radar points on it for a reliable history)."
                : "Click “📈 Load movement history” at the top of this page to see how this has moved over time."));
            Content = sp;
            return;
        }
        var chart = new TimeSeriesChart { Height = 280 };
        var verdict = new Border
        {
            Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.Parse(AmExtras.VerdictHex(h.Verdict)), 0.18), BorderBrush = new SolidColorBrush(Color.Parse(AmExtras.VerdictHex(h.Verdict))), BorderThickness = new Thickness(1.5),
            Child = new TextBlock { Text = AmExtras.VerdictText(h.Verdict), FontSize = 16, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse(AmExtras.VerdictHex(h.Verdict))) }
        };
        sp.Children.Add(verdict);
        var facts = new StackPanel { Spacing = 3 };
        var picker = new ComboBox { ItemsSource = h.Tracks.Select(t => $"🛰 {t.Track}  ({t.DeckN} points on the {assetWord.ToLowerInvariant()}, {t.GroundN} around)").ToList(), MinWidth = 380 };
        void Show(int i)
        {
            var t = h.Tracks[i];
            chart.SetData(t, assetWord);
            facts.Children.Clear();
            facts.Children.Add(Am.P($"First half of the period: the {assetWord.ToLowerInvariant()} moved {AmExtras.Signed(t.VFirst)} mm per year more than the ground around it."));
            facts.Children.Add(Am.P($"Second half of the period: {AmExtras.Signed(t.VSecond)} mm per year." +
                                    (t.Accel.HasValue ? $"  (Acceleration {AmExtras.Signed(t.Accel, "0.00")} mm/yr² — negative means the sinking speeds up.)" : "")));
            if (t.SeasonMm.HasValue)
                facts.Children.Add(Am.P(t.SeasonMm.Value >= 2
                    ? $"🌡 It also moves with the seasons: about ±{t.SeasonMm.Value:0.0} mm between summer and winter. That is normal for {(assetWord == "Bridge" ? "bridges (steel and concrete expand when warm)" : "roads and soil (wet/dry seasons)")} and is not damage."
                    : $"🌡 Hardly any seasonal movement (±{t.SeasonMm.Value:0.0} mm)."));
        }
        int best = Math.Max(0, h.Tracks.FindIndex(t => t.Track == h.BestTrack));
        picker.SelectedIndex = best;
        picker.SelectionChanged += (_, _) => { if (picker.SelectedIndex >= 0) Show(picker.SelectedIndex); };
        sp.Children.Add(Am.M("Satellite view:"));
        sp.Children.Add(picker);
        sp.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = chart });
        sp.Children.Add(facts);
        sp.Children.Add(Am.M("Each line starts at 0 on the first satellite pass. The RED line is what matters: the asset's own movement with the area's movement taken out. " +
                             "If it bends downwards more and more, the sinking is speeding up. Values along the satellite's line of sight; " +
                             "the ±1 mm/yr limit for “speeding up” is a placeholder to agree with the province's engineers."));
        Show(best);
        Content = sp;
    }
}

internal sealed class TimeSeriesChart : Control
{
    private AmExtras.TsTrack? _t;
    private string _word = "Bridge";
    private int _hot = -1;
    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#0b1220"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#1e293b")), 1);
    private static readonly IPen Zero = new Pen(new SolidColorBrush(Color.Parse("#94a3b8")), 1.2);
    public TimeSeriesChart() { Cursor = new Cursor(StandardCursorType.Cross); }
    public void SetData(AmExtras.TsTrack t, string word) { _t = t; _word = word; _hot = -1; InvalidateVisual(); }
    private static FormattedText Txt(string s, double size = 11, string hex = "#94a3b8") =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, new SolidColorBrush(Color.Parse(hex)));

    private (double left, double top, double w, double h, double vmin, double vmax) Frame()
    {
        const double left = 52, right = 16, top = 14, bottom = 28;
        double w = Math.Max(10, Bounds.Width - left - right), h = Math.Max(10, Bounds.Height - top - bottom);
        var vals = new List<double>();
        if (_t != null)
            for (int i = 0; i < _t.Dates.Count; i++)
            {
                if (_t.Deck[i] is double d) vals.Add(d);
                if (_t.Ground[i] is double g) vals.Add(g);
                if (_t.Deck[i] is double d2 && _t.Ground[i] is double g2) vals.Add(d2 - g2);
            }
        double vmin = vals.Count > 0 ? Math.Floor(Math.Min(-5, vals.Min() - 1) / 5) * 5 : -10, vmax = vals.Count > 0 ? Math.Ceiling(Math.Max(5, vals.Max() + 1) / 5) * 5 : 5;
        return (left, top, w, h, vmin, vmax);
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawRectangle(Bg, null, new Rect(Bounds.Size));
        if (_t == null || _t.Dates.Count < 2) return;
        var (left, top, w, h, vmin, vmax) = Frame();
        double t0 = _t.Dates[0].Ticks, t1 = _t.Dates[^1].Ticks;
        double X(DateTime d) => left + (d.Ticks - t0) / (t1 - t0) * w;
        double Y(double v) => top + (vmax - v) / (vmax - vmin) * h;
        for (double v = vmin; v <= vmax + 1e-9; v += 5)
        {
            ctx.DrawLine(Math.Abs(v) < 1e-9 ? Zero : GridPen, new Point(left, Y(v)), new Point(left + w, Y(v)));
            var t = Txt(v.ToString("+0;−0;0", CultureInfo.InvariantCulture) + " mm");
            ctx.DrawText(t, new Point(left - 6 - t.Width, Y(v) - 7));
        }
        for (int y = _t.Dates[0].Year + 1; y <= _t.Dates[^1].Year; y++)
        {
            var d = new DateTime(y, 1, 1);
            ctx.DrawLine(GridPen, new Point(X(d), top), new Point(X(d), top + h));
            var t = Txt(y.ToString());
            ctx.DrawText(t, new Point(X(d) - t.Width / 2, top + h + 6));
        }
        void Line(Func<int, double?> val, string hex, double width, bool dashed = false)
        {
            var pen = new Pen(new SolidColorBrush(Color.Parse(hex)), width) { DashStyle = dashed ? DashStyle.Dash : null };
            Point? prev = null;
            for (int i = 0; i < _t.Dates.Count; i++)
            {
                if (val(i) is not double v) { prev = null; continue; }
                var p = new Point(X(_t.Dates[i]), Y(v));
                if (prev.HasValue) ctx.DrawLine(pen, prev.Value, p);
                prev = p;
            }
        }
        Line(i => _t.Ground[i], "#0d9e75", 1.6, true);
        Line(i => _t.Deck[i], "#93c5fd", 1.4);
        Line(i => _t.Deck[i] is double d && _t.Ground[i] is double g ? d - g : null, "#ef4444", 2.6);

        // legend
        double lx = left + 8, ly = top + h - 62;
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#e60b1220")), new Pen(new SolidColorBrush(Color.Parse("#334155")), 1), new Rect(lx, ly, 330, 56), 6, 6);
        void Key(double yy, string hex, bool dashed, string text)
        {
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse(hex)), 2.4) { DashStyle = dashed ? DashStyle.Dash : null }, new Point(lx + 8, yy), new Point(lx + 30, yy));
            ctx.DrawText(Txt(text, 11, "#e2e8f0"), new Point(lx + 36, yy - 8));
        }
        Key(ly + 12, "#ef4444", false, $"{_word}'s OWN movement (area movement taken out)");
        Key(ly + 28, "#93c5fd", false, $"{_word} total movement");
        Key(ly + 44, "#0d9e75", true, "ground around it (the area)");

        if (_hot >= 0 && _hot < _t.Dates.Count)
        {
            double x = X(_t.Dates[_hot]);
            ctx.DrawLine(new Pen(Brushes.White, 1), new Point(x, top), new Point(x, top + h));
            string s = $"{_t.Dates[_hot]:d MMM yyyy}: own {AmExtras.Signed(_t.Deck[_hot] - _t.Ground[_hot])} mm · total {AmExtras.Signed(_t.Deck[_hot])} · ground {AmExtras.Signed(_t.Ground[_hot])}";
            var ft = Txt(s, 12, "#ffffff");
            double bx = Math.Min(x + 8, left + w - ft.Width - 12);
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#f0111827")), null, new Rect(bx - 4, top + 4, ft.Width + 8, 20), 4, 4);
            ctx.DrawText(ft, new Point(bx, top + 6));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_t == null || _t.Dates.Count < 2) return;
        var (left, _, w, _, _, _) = Frame();
        double x = e.GetPosition(this).X;
        double frac = Math.Clamp((x - left) / w, 0, 1);
        long target = _t.Dates[0].Ticks + (long)(frac * (_t.Dates[^1].Ticks - _t.Dates[0].Ticks));
        int best = 0; long bd = long.MaxValue;
        for (int i = 0; i < _t.Dates.Count; i++) { long d = Math.Abs(_t.Dates[i].Ticks - target); if (d < bd) { bd = d; best = i; } }
        _hot = best;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _hot = -1; InvalidateVisual(); }
}

// =====================================================================================
// 🌍 Earthquakes near this asset (KNMI catalogue)
// =====================================================================================
internal sealed class QuakesView : UserControl
{
    public QuakesView(double? lat, double? lon)
    {
        var sp = new StackPanel { Spacing = 8 };
        sp.Children.Add(Am.H("🌍 Earthquakes nearby (official KNMI catalogue)"));
        var all = AmExtras.Quakes();
        if (!lat.HasValue || !lon.HasValue) { sp.Children.Add(Am.M("No location for this asset.")); Content = sp; return; }
        if (all.Count == 0)
        {
            sp.Children.Add(Am.M(File.Exists(AmExtras.QuakesFile) ? "The KNMI catalogue has no earthquakes in this area." : "Click “🌍 Update earthquakes (KNMI)” at the top of this page."));
            Content = sp; return;
        }
        var radii = new[] { 2.0, 5.0, 10.0 };
        var picker = new ComboBox { ItemsSource = radii.Select(r => $"within {r:0} km").ToList(), SelectedIndex = 1, Width = 160 };
        var body = new StackPanel { Spacing = 6 };
        void Show()
        {
            double r = radii[Math.Max(0, picker.SelectedIndex)];
            var near = all.Select(q => (q, d: AmExtras.DistanceKm(lat.Value, lon.Value, q.Lat, q.Lon))).Where(x => x.d <= r).OrderByDescending(x => x.q.Time).ToList();
            body.Children.Clear();
            if (near.Count == 0) { body.Children.Add(Am.P($"No recorded earthquakes within {r:0} km since 1986.")); return; }
            var big = near.Where(x => x.q.Mag.HasValue).OrderByDescending(x => x.q.Mag).FirstOrDefault();
            var closest = near.OrderBy(x => x.d).First();
            var wrap = new WrapPanel();
            wrap.Children.Add(Stat($"{near.Count}", $"earthquakes within {r:0} km since 1986", "#f59e0b"));
            if (big.q != null) wrap.Children.Add(Stat($"M {big.q.Mag:0.0}", $"largest — {big.q.Time:d MMM yyyy}, {big.d:0.0} km away", "#ef4444"));
            wrap.Children.Add(Stat($"{closest.d:0.0} km", $"closest — {closest.q.Time:d MMM yyyy}, M {closest.q.Mag:0.0}", "#a855f7"));
            wrap.Children.Add(Stat($"{near.Count(x => x.q.Time.Year >= 2020 && x.q.Time.Year <= 2024)}", "during the satellite period 2020–2024", "#22d3ee"));
            body.Children.Add(wrap);
            body.Children.Add(Am.M("Most recent:"));
            foreach (var (q, d) in near.Take(8))
                body.Children.Add(Am.P($"{q.Time:d MMM yyyy}   ·   magnitude {(q.Mag.HasValue ? q.Mag.Value.ToString("0.0", CultureInfo.InvariantCulture) : "—")}   ·   {d:0.0} km away   ·   {q.Place}"));
        }
        picker.SelectionChanged += (_, _) => Show();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(Am.M("Show earthquakes"));
        head.Children.Add(picker);
        sp.Children.Add(head);
        sp.Children.Add(body);
        sp.Children.Add(Am.M("Groningen's earthquakes are mostly caused by gas extraction. Being near earthquakes does not prove they caused this movement — " +
                             "but it is important context for the engineers. Source: KNMI (Royal Netherlands Meteorological Institute) event catalogue."));
        Show();
        Content = sp;
    }

    private static Control Stat(string big, string text, string hex)
    {
        var s = new StackPanel { Spacing = 1 };
        s.Children.Add(new TextBlock { Text = big, FontSize = 22, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse(hex)) });
        s.Children.Add(Am.M(text));
        return new Border { Padding = new Thickness(10, 6), Margin = new Thickness(0, 0, 10, 8), Width = 210, CornerRadius = new CornerRadius(6),
                            BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = new SolidColorBrush(Color.Parse(hex)), Child = s };
    }
}

// =====================================================================================
// ✈ Draft flight plan (standard ArduPilot/QGroundControl .waypoints file)
// =====================================================================================
internal static class FlightPlanner
{
    public sealed record Settings(bool Vtol, double AltitudeM, double OffsetM);

    private const int FrameRelAlt = 3;
    private const int CmdWaypoint = 16, CmdRtl = 20, CmdTakeoff = 22, CmdVtolTakeoff = 84, CmdSetRoi = 201;

    public static (string? Path, List<string> Notes) Generate(InspectionTask task, Settings s)
    {
        var notes = new List<string>();
        if (!task.Lat.HasValue || !task.Lon.HasValue) return (null, new List<string> { "This task has no location." });
        if (s.AltitudeM <= 0 || s.AltitudeM > 120) return (null, new List<string> { "Altitude must be between 1 and 120 m (EU open/specific category limit is 120 m above ground)." });

        var ids = task.PartIds.Count > 0 ? task.PartIds : new List<string> { task.AssetId };
        BridgeEvidence? ev = null;
        foreach (var file in new[] { BridgePointsDetails, RoadPointsDetails })
        {
            var all = ReadEvidence(file);
            ev = ids.Select(id => all.GetValueOrDefault(id)).FirstOrDefault(e => e != null);
            if (ev != null) break;
        }
        bool isRoad = task.Aircraft.Contains("corridor", StringComparison.OrdinalIgnoreCase) ||
                      (ev != null && ev.LengthM.HasValue);

        var wps = new List<(double Lat, double Lon)>();
        double cLat = task.Lat.Value, cLon = task.Lon.Value;
        if (isRoad && ev != null && ev.OutlineLonLat.Count > 0 && ev.OutlineLonLat[0].Count >= 2)
        {
            var line = ev.OutlineLonLat.OrderByDescending(l => l.Count).First();
            int step = Math.Max(1, line.Count / 40);
            for (int i = 0; i < line.Count; i += step) wps.Add((line[i].Lat, line[i].Lon));
            if (wps[^1] != (line[^1].Lat, line[^1].Lon)) wps.Add((line[^1].Lat, line[^1].Lon));
            notes.Add($"Route follows the road centre line ({wps.Count} waypoints, about {ev.LengthM:0} m).");
        }
        else
        {
            double radius = s.OffsetM;
            if (ev != null && ev.OutlineLonLat.Count > 0)
            {
                var pts = ev.OutlineLonLat.SelectMany(l => l).ToList();
                cLat = pts.Average(p => p.Lat); cLon = pts.Average(p => p.Lon);
                radius = pts.Max(p => AmExtras.DistanceKm(cLat, cLon, p.Lat, p.Lon) * 1000) + s.OffsetM;
                notes.Add($"Orbit around the bridge outline from the register: radius {radius:0} m ({s.OffsetM:0} m outside the deck).");
            }
            else notes.Add($"No bridge outline available — orbit of {radius:0} m around the task location.");
            for (int i = 0; i < 12; i++)
            {
                double a = i * Math.PI * 2 / 12;
                wps.Add((cLat + radius * Math.Cos(a) / 111320.0, cLon + radius * Math.Sin(a) / (111320.0 * Math.Cos(cLat * Math.PI / 180))));
            }
            wps.Add(wps[0]);
        }

        // official airspace check (109 LVNL zones) — real data only; if missing, say so
        if (File.Exists(AmExtras.AirspaceFile))
        {
            try
            {
                var fc = new NetTopologySuite.IO.GeoJsonReader().Read<NetTopologySuite.Features.FeatureCollection>(File.ReadAllText(AmExtras.AirspaceFile));
                var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
                var hits = new HashSet<string>();
                foreach (var f in fc)
                {
                    if (f.Geometry == null) continue;
                    foreach (var w in wps)
                        if (f.Geometry.Contains(factory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(w.Lon, w.Lat))))
                        {
                            string name = "";
                            foreach (var key in new[] { "name", "NAME", "Name", "naam", "designator", "id" })
                                if (f.Attributes != null && f.Attributes.Exists(key)) { name = f.Attributes[key]?.ToString() ?? ""; break; }
                            hits.Add(string.IsNullOrWhiteSpace(name) ? "unnamed zone" : name);
                            break;
                        }
                }
                notes.Add(hits.Count == 0 ? "✅ Route is outside all zones in the official airspace file (109 LVNL zones)."
                                          : "⛔ Route enters official airspace zone(s): " + string.Join(", ", hits) + " — permission needed before flying.");
            }
            catch (Exception ex) { notes.Add($"⚠ Airspace file could not be read ({ex.Message}) — check airspace manually."); }
        }
        else notes.Add("⚠ Official airspace file not found (~/agri_drone/airspace_nl.geojson) — check airspace manually before flying.");

        // write QGC WPL 110
        string F(double v) => v.ToString("0.0000000", CultureInfo.InvariantCulture);
        string A(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        var lines = new List<string> { "QGC WPL 110", $"0\t1\t0\t{CmdWaypoint}\t0\t0\t0\t0\t{F(cLat)}\t{F(cLon)}\t0.00\t1" };
        int seq = 1;
        void Add(int cmd, double lat, double lon, double alt, double p1 = 0) => lines.Add($"{seq++}\t0\t{FrameRelAlt}\t{cmd}\t{A(p1)}\t0.00\t0.00\t0.00\t{F(lat)}\t{F(lon)}\t{A(alt)}\t1");
        Add(s.Vtol ? CmdVtolTakeoff : CmdTakeoff, 0, 0, s.AltitudeM);
        if (!isRoad) Add(CmdSetRoi, cLat, cLon, 0);          // point the camera at the bridge
        foreach (var w in wps) Add(CmdWaypoint, w.Lat, w.Lon, s.AltitudeM);
        if (!isRoad) Add(CmdSetRoi, 0, 0, 0);                // cancel the camera point-of-interest
        Add(CmdRtl, 0, 0, 0);

        Directory.CreateDirectory(AmExtras.FlightPlanDir);
        var path = Path.Combine(AmExtras.FlightPlanDir, $"{AmExtras.Safe(task.AssetId)}_{(s.Vtol ? "loong_vtol" : "quadcopter")}_{DateTime.Now:yyyyMMdd_HHmm}.waypoints");
        File.WriteAllLines(path, lines);
        notes.Insert(0, $"Saved: {path}");
        notes.Add($"Take-off: {(s.Vtol ? "VTOL take-off (Loong 2160)" : "vertical take-off (quadcopter)")} to {s.AltitudeM:0} m above the launch point · ends with Return-To-Launch.");
        notes.Add("DRAFT — the pilot must review it (obstacles, bridge height, trees, power lines, people, SORA volume, NOTAMs) before any flight. Open it in QGroundControl: Plan → File → Open.");
        return (path, notes);
    }
}

// =====================================================================================
// 📄 Inspection report per asset (HTML, printable to PDF) — everything we know about one bridge/road
// =====================================================================================
internal static class InspectionReport
{
    private static readonly string[] Nen = { "", "Excellent", "Good", "Reasonable", "Moderate", "Poor", "Very poor" };

    public static string Build(InspectionTask task)
    {
        string E(string s) => WebUtility.HtmlEncode(s);
        var ids = task.PartIds.Count > 0 ? task.PartIds : new List<string> { task.AssetId };
        var folder = AmExtras.AssetFolder(task.AssetId);
        Directory.CreateDirectory(folder);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Inspection report</title><style>");
        sb.Append("body{font-family:system-ui,Segoe UI,Roboto,sans-serif;max-width:900px;margin:30px auto;padding:0 20px;color:#1e293b}");
        sb.Append("h1{font-size:24px;margin:4px 0}h2{font-size:16px;color:#0f3a6b;margin:24px 0 8px;border-bottom:2px solid #e2e8f0;padding-bottom:4px}");
        sb.Append(".muted{color:#64748b;font-size:12px}.box{background:#f8fafc;border-radius:6px;padding:10px 14px;font-size:14px}");
        sb.Append("table{border-collapse:collapse;width:100%;font-size:13px}td,th{border-bottom:1px solid #e2e8f0;padding:6px;text-align:left}th{background:#f1f5f9;width:38%}");
        sb.Append(".score{font-size:30px;font-weight:700}.photos{display:flex;flex-wrap:wrap;gap:8px}.photos img{width:280px;border-radius:6px;border:1px solid #cbd5e1}");
        sb.Append("</style></head><body>");
        sb.Append($"<div class=\"muted\">DAMbv · Inspection report · {DateTime.Now:d MMMM yyyy}</div>");
        sb.Append($"<h1>{E(OwnerLabel(task.OwnerType, task.OwnerName))}</h1>");
        sb.Append($"<div class=\"muted\">Register ID {E(task.AssetId)}{(ids.Count > 1 ? $" (+{ids.Count - 1} deck parts)" : "")} · location {task.Lat?.ToString("0.00000", CultureInfo.InvariantCulture)}, {task.Lon?.ToString("0.00000", CultureInfo.InvariantCulture)}</div>");

        // ---- condition
        sb.Append("<h2>Condition (NEN 2767)</h2><div class=\"box\">");
        if (task.Nen2767Score is int sc)
            sb.Append($"<span class=\"score\">{sc}</span> — {Nen[sc]}<br>Inspector: {E(task.Inspector)} · scored {E(task.InspectedUtc.Split('T')[0])}");
        else sb.Append("Not yet scored — this report shows the satellite evidence that led to the inspection.");
        if (!string.IsNullOrWhiteSpace(task.Notes)) sb.Append($"<p><b>Findings:</b> {E(task.Notes).Replace("\n", "<br>")}</p>");
        sb.Append($"<p class=\"muted\">Status: {E(task.Status)} · inspection task created {E(task.CreatedUtc.Split('T')[0])}</p></div>");

        // ---- satellite evidence
        sb.Append("<h2>Why this asset was selected — satellite evidence</h2><table>");
        var area = ReadRanked().Concat(ReadRanked(RoadsRankedCsv)).FirstOrDefault(r => ids.Contains(r.AssetId));
        if (area != null)
            sb.Append($"<tr><th>Area check (100 m grid)</th><td>{E(area.Screening)} · vertical {AmExtras.Signed(area.UpDiff)} mm/yr, sideways {AmExtras.Signed(area.EastDiff)} mm/yr compared with the surroundings</td></tr>");
        var st = ReadStructureResults().Concat(ReadStructureResults(RoadPointsCsv)).FirstOrDefault(r => ids.Contains(r.AssetId));
        if (st != null)
            sb.Append($"<tr><th>Radar points on the structure</th><td>{E(st.Status)} · {AmExtras.Signed(st.DiffLos)} mm/yr compared with the ground around it · {st.DeckN} points on it, {st.RingN} around · " +
                      $"seen by {st.TracksFlagging} of {st.TracksQualifying} satellite views</td></tr>");
        var kind = RoadPointsCsv.Length > 0 && ReadStructureResults(RoadPointsCsv).Any(r => ids.Contains(r.AssetId)) ? "roads" : "bridges";
        var hist = AmExtras.History(kind, ids);
        if (hist != null)
        {
            var bt = hist.Tracks.FirstOrDefault(t => t.Track == hist.BestTrack) ?? hist.Tracks[0];
            sb.Append($"<tr><th>Movement over time (2020–2024)</th><td>{E(AmExtras.VerdictText(hist.Verdict))} · first half {AmExtras.Signed(bt.VFirst)} mm/yr, second half {AmExtras.Signed(bt.VSecond)} mm/yr" +
                      (bt.SeasonMm is double sm && sm >= 2 ? $" · seasonal swing ±{sm:0.0} mm (normal)" : "") + "</td></tr>");
        }
        if (task.Lat.HasValue && task.Lon.HasValue)
        {
            var near = AmExtras.Quakes().Select(q => (q, d: AmExtras.DistanceKm(task.Lat.Value, task.Lon.Value, q.Lat, q.Lon))).Where(x => x.d <= 5).ToList();
            if (File.Exists(AmExtras.QuakesFile))
            {
                var big = near.Where(x => x.q.Mag.HasValue).OrderByDescending(x => x.q.Mag).FirstOrDefault();
                sb.Append($"<tr><th>Earthquakes within 5 km (KNMI, since 1986)</th><td>{near.Count}" +
                          (big.q != null ? $" · largest M {big.q.Mag:0.0} on {big.q.Time:d MMM yyyy}, {big.d:0.0} km away" : "") + "</td></tr>");
            }
        }
        if (area == null && st == null) sb.Append("<tr><td colspan=\"2\">No satellite result stored for this asset.</td></tr>");
        sb.Append("</table>");

        // ---- photos
        var photos = Directory.GetFiles(folder).Where(f => new[] { ".jpg", ".jpeg", ".png" }.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f).ToList();
        sb.Append("<h2>Drone photos</h2>");
        if (photos.Count == 0) sb.Append($"<p class=\"muted\">No photos yet — put the inspection photos in {E(folder)} and make the report again.</p>");
        else
        {
            sb.Append("<div class=\"photos\">");
            foreach (var p in photos.Take(24)) sb.Append($"<figure style=\"margin:0\"><img src=\"{E(Path.GetFileName(p))}\"><figcaption class=\"muted\">{E(Path.GetFileName(p))}</figcaption></figure>");
            sb.Append("</div>");
        }

        // ---- data and audit
        sb.Append("<h2>Data sources and audit</h2><div class=\"box\" style=\"font-size:12px\">");
        foreach (var (label, file) in new[] { ("Area check", ManifestFile), ("Road area check", RoadsAudit), ("Structure check", BridgePointsAudit), ("Road structure check", RoadPointsAudit) })
        {
            var m = ReadJson(file);
            if (m == null) continue;
            sb.Append($"<p><b>{label}:</b> Copernicus EGMS release {E(m["egms_release"]?.ToString() ?? "")}, run {E(m["run_utc"]?.ToString() ?? "")}, script {E(m["script"]?.ToString() ?? "")}. " +
                      $"Satellite files and SHA-256 checksums: {E(Path.GetFileName(file))}.</p>");
        }
        var qm = ReadJson(AmExtras.QuakesMeta);
        if (qm != null) sb.Append($"<p><b>Earthquakes:</b> KNMI FDSN event service, retrieved {E(qm["retrieved_utc"]?.ToString() ?? "")}, SHA-256 {E(qm["sha256"]?.ToString() ?? "")}.</p>");
        sb.Append("<p>Satellite results are early-warning signals, not proof of damage. Warning levels are placeholders to be agreed with the asset manager's engineers. " +
                  "Satellite values are along the line of sight; negative usually means sinking.</p></div>");
        sb.Append("<p class=\"muted\">DAMbv B.V. (Dutch Autonomous Mobility), Groningen</p></body></html>");

        var path = Path.Combine(folder, $"report_{AmExtras.Safe(task.AssetId)}.html");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }
}
