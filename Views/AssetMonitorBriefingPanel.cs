using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static InfraDroneDesktop.Services.AssetMonitorService;

namespace InfraDroneDesktop.Views;

// =====================================================================================
// 📝 Briefing — the whole story in plain language, for a meeting with the province and
// for the practical follow-up (shortlist -> drone inspection -> NEN 2767 score).
// Every number is computed from the real screening results on disk.
// =====================================================================================
internal sealed class AmBriefingPanel : AmPanel
{
    private sealed class Item
    {
        public string AssetId = "", OwnerType = "", OwnerName = "", Manager = "", Movement = "", Evidence = "", Level = "";
        public double? Lat, Lon, Abs;
        public bool Confirmed;
        public List<string> Ids = new();
    }

    private static readonly string[] NenNames = { "", "Excellent", "Good", "Reasonable", "Moderate", "Poor", "Very poor" };
    private static string NotesFile => Path.Combine(ToolsDir, "briefing_notes.txt");
    private static string AgreedFlag => Path.Combine(ToolsDir, "briefing_thresholds_agreed.flag");

    private readonly TextBlock _headline = new() { FontSize = 24, FontWeight = FontWeight.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _subline = new() { FontSize = 13, Foreground = new SolidColorBrush(Color.Parse("#cbd5e1")), TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _numbers = new();
    private readonly StackPanel _steps = new() { Spacing = 8 };
    private readonly TextBlock _shortTitle = Am.H("");
    private readonly TextBlock _shortExplain = Am.M("");
    private readonly StackPanel _shortlist = new() { Spacing = 8 };
    private readonly StackPanel _others = new() { Spacing = 8 };
    private readonly Expander _othersBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, Watermark = "Meeting notes, questions from the province, agreements, contacts…" };
    private readonly TextBlock _msg = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#22d3ee")), TextWrapping = TextWrapping.Wrap };

    private List<Item> _confirmed = new(), _other = new();
    private List<InspectionTask> _tasks = new();
    private int _total;
    private string _release = "", _runDate = "";
    private bool _structureLevel;

    public AmBriefingPanel()
    {
        // ---------------- hero
        var hero = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(22, 18),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#0b2545"), 0), new GradientStop(Color.Parse("#13315c"), 0.55), new GradientStop(Color.Parse("#1d1146"), 1) }
            }
        };
        var heroStack = new StackPanel { Spacing = 10 };
        heroStack.Children.Add(new TextBlock { Text = "📝 BRIEFING FOR THE PROVINCE OF GRONINGEN", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#22d3ee")) });
        heroStack.Children.Add(_headline);
        heroStack.Children.Add(_subline);
        var actions = new WrapPanel();
        actions.Children.Add(Am.Btn("📄 Make one-page briefing (print / e-mail)", (_, _) => MakeHandout()));
        actions.Children.Add(Am.Btn("📋 Copy talking points", async (_, _) =>
        {
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb != null) { await cb.SetTextAsync(TalkingPoints()); Say("Talking points copied — paste them into your notes, an e-mail or a slide."); }
        }));
        actions.Children.Add(Am.Btn("🔄 Refresh", (_, _) => Load()));
        heroStack.Children.Add(actions);
        heroStack.Children.Add(_msg);
        hero.Child = heroStack;
        Root.Children.Add(hero);

        // ---------------- the three numbers
        Root.Children.Add(_numbers);

        // ---------------- what to do next (progress)
        var stepsCard = new StackPanel { Spacing = 8 };
        stepsCard.Children.Add(Am.H("✅ What to do next"));
        stepsCard.Children.Add(Am.M("Progress updates automatically when inspection tasks get a NEN 2767 score in the Condition sub-tab."));
        stepsCard.Children.Add(_steps);
        Root.Children.Add(Am.Card(stepsCard));

        // ---------------- shortlist
        var shortCard = new StackPanel { Spacing = 8 };
        shortCard.Children.Add(_shortTitle);
        shortCard.Children.Add(_shortExplain);
        var shortActions = new WrapPanel();
        shortActions.Children.Add(Am.Btn("➕ Put the whole shortlist on the inspection list", (_, _) =>
        {
            int added = AddTasks(_confirmed);
            Say(added == 0 ? "All shortlisted bridges are already on the inspection list." : $"{added} bridges added to the inspection list (📋 Inspection Tasks sub-tab).");
            Load();
        }));
        shortCard.Children.Add(shortActions);
        shortCard.Children.Add(_shortlist);
        Root.Children.Add(Am.Card(shortCard));

        _othersBox.Content = _others;
        Root.Children.Add(_othersBox);

        // ---------------- honesty box
        var honest = new StackPanel { Spacing = 6 };
        honest.Children.Add(Am.H("💬 What this is — and what it is not"));
        honest.Children.Add(Am.P("✔ It IS an early-warning list: free European satellite data (Copernicus EGMS) shows which bridges move differently from the ground around them."));
        honest.Children.Add(Am.P("✔ It IS checkable: every warning comes with the exact satellite files, measurement points and settings (Value & Audit sub-tab)."));
        honest.Children.Add(Am.P("✘ It is NOT proof of damage. Only an inspection (our drone + a qualified inspector) tells what is really going on."));
        honest.Children.Add(Am.P("✘ The warning levels (2 and 4 mm per year) are a starting point, to be set together with the province's engineers."));
        honest.Children.Add(Am.P("ℹ For many small or low bridges the satellite sees no points on the deck itself; for those only the area check (100 m grid) is available."));
        Root.Children.Add(Am.Card(honest));

        // ---------------- notes
        var notesCard = new StackPanel { Spacing = 8 };
        notesCard.Children.Add(Am.H("🗒 My notes for this project"));
        notesCard.Children.Add(Am.M($"Saved on this computer in {NotesFile}"));
        notesCard.Children.Add(_notes);
        var notesActions = new WrapPanel();
        notesActions.Children.Add(Am.Btn("💾 Save notes", (_, _) => SaveNotes(true)));
        notesCard.Children.Add(notesActions);
        Root.Children.Add(Am.Card(notesCard));
        _notes.LostFocus += (_, _) => SaveNotes(false);

        AttachedToVisualTree += (_, _) => Load();
    }

    private void Say(string text) => _msg.Text = text;

    private void SaveNotes(bool tell)
    {
        try
        {
            Directory.CreateDirectory(ToolsDir);
            File.WriteAllText(NotesFile, _notes.Text ?? "");
            if (tell) Say("Notes saved.");
        }
        catch (Exception ex) { Say($"Could not save notes: {ex.Message}"); }
    }

    // =============================================================== data
    private void Load()
    {
        try
        {
            if (File.Exists(NotesFile) && string.IsNullOrEmpty(_notes.Text)) _notes.Text = File.ReadAllText(NotesFile);
            var area = ReadRanked();
            var structure = ReadStructureResults();
            _tasks = LoadTasks();
            _total = area.Count > 0 ? area.Count : structure.Count;
            _structureLevel = structure.Any(s => s.Status is "Priority" or "Review" or "No unusual movement");

            var audit = _structureLevel ? ReadJson(BridgePointsAudit) : ReadManifest();
            _release = audit?["egms_release"]?.ToString() ?? "";
            _runDate = (audit?["run_utc"]?.ToString() ?? "").Split('T')[0];

            _confirmed.Clear(); _other.Clear();
            if (_structureLevel)
            {
                foreach (var s in GroupStructure(structure.Where(s => s.Status is "Priority" or "Review")))
                {
                    var it = FromStructure(s);
                    (it.Confirmed ? _confirmed : _other).Add(it);
                }
            }
            else
            {
                foreach (var a in GroupParts(area.Where(a => a.Screening is "Priority" or "Review")))
                {
                    var it = FromArea(a);
                    (it.Confirmed ? _confirmed : _other).Add(it);
                }
            }
            _confirmed = _confirmed.OrderByDescending(i => i.Abs).ToList();
            _other = _other.OrderByDescending(i => i.Level == "Priority").ThenByDescending(i => i.Abs).ToList();
        }
        catch (Exception ex) { Say($"Could not read the results: {ex.Message}"); return; }

        BuildHero();
        BuildNumbers();
        BuildSteps();
        BuildShortlist();
    }

    private static Item FromStructure(StructureResult s)
    {
        var d = s.DiffLos ?? 0;
        return new Item
        {
            AssetId = s.AssetId, OwnerType = s.OwnerType, OwnerName = s.OwnerName, Manager = s.ManagerLabel,
            Lat = s.Lat, Lon = s.Lon, Abs = Math.Abs(d), Level = s.Status, Ids = s.PartIds.Count > 0 ? s.PartIds : new List<string> { s.AssetId },
            Confirmed = s.TracksFlagging >= 2,
            Movement = $"The deck moves {Math.Abs(d):0.0} mm per year {(d < 0 ? "away from" : "towards")} the satellite compared with the ground around it" +
                       (d < 0 ? " — usually this means sinking." : "."),
            Evidence = $"Seen by {s.TracksFlagging} of {s.TracksQualifying} satellite viewing directions · {s.DeckN} radar points on the deck"
        };
    }

    private static Item FromArea(ScreenedBridge a)
    {
        double up = a.UpDiff ?? 0, ew = a.EastDiff ?? 0;
        string movement = Math.Abs(up) >= Math.Abs(ew)
            ? (up < 0 ? $"The ground at this bridge sinks {Math.Abs(up):0.0} mm per year faster than the area around it."
                      : $"The ground at this bridge rises {up:0.0} mm per year more than the area around it.")
            : $"The ground at this bridge moves {Math.Abs(ew):0.0} mm per year sideways ({(ew < 0 ? "west" : "east")}) compared with the area around it.";
        return new Item
        {
            AssetId = a.AssetId, OwnerType = a.OwnerType, OwnerName = a.OwnerName, Manager = a.ManagerLabel,
            Lat = a.Lat, Lon = a.Lon, Abs = a.MaxAbs, Level = a.Screening, Ids = a.PartIds.Count > 0 ? a.PartIds : new List<string> { a.AssetId },
            Confirmed = a.Screening == "Priority", Movement = movement,
            Evidence = $"{a.LocalN ?? 0} satellite points at the bridge (100 m area grid)"
        };
    }

    private static InspectionTask? FindTask(List<InspectionTask> tasks, List<string> ids) =>
        tasks.FirstOrDefault(t => ids.Contains(t.AssetId) || t.PartIds.Any(ids.Contains));

    private int AddTasks(IEnumerable<Item> items)
    {
        var tasks = LoadTasks();
        int added = 0;
        foreach (var it in items)
        {
            if (FindTask(tasks, it.Ids) != null) continue;
            tasks.Add(new InspectionTask
            {
                AssetId = it.AssetId, OwnerType = it.OwnerType, OwnerName = it.OwnerName, Screening = it.Level,
                MaxAbsMmPerYear = it.Abs, Lat = it.Lat, Lon = it.Lon, EgmsRelease = _release,
                Parts = it.Ids.Count, PartIds = it.Ids, CreatedUtc = DateTime.UtcNow.ToString("o")
            });
            added++;
        }
        SaveTasks(tasks);
        return added;
    }

    private (int planned, int scored) Progress(List<Item> items)
    {
        int planned = 0, scored = 0;
        foreach (var it in items)
        {
            var t = FindTask(_tasks, it.Ids);
            if (t != null) planned++;
            if (t?.Nen2767Score != null) scored++;
        }
        return (planned, scored);
    }

    // =============================================================== visuals
    private void BuildHero()
    {
        if (_total == 0)
        {
            _headline.Text = "No satellite check yet.";
            _subline.Text = "Open the 🛰 Satellite Screening sub-tab and click “Run satellite screening”. This page then fills itself.";
            return;
        }
        int n = _confirmed.Count;
        _headline.Text = $"We checked all {_total:N0} bridges in the province from space. {n} {(n == 1 ? "needs" : "need")} a closer look first.";
        _subline.Text = _structureLevel
            ? $"Free European satellite data (Copernicus EGMS, {_release}), processed {_runDate}. " +
              $"The {n} bridges below are seen moving differently from their surroundings by two independent satellite viewing directions — the strongest signal we have. " +
              $"Another {_other.Count} bridges show a weaker warning sign."
            : $"Free European satellite data (Copernicus EGMS, {_release}), processed {_runDate}. " +
              $"The {n} bridges below show the strongest warning sign; another {_other.Count} show a weaker one. " +
              "Tip: run the structure-level screening for stronger evidence.";
    }

    private void BuildNumbers()
    {
        _numbers.Children.Clear();
        if (_total == 0) return;
        _numbers.Children.Add(Tile($"{_total:N0}", "bridges checked from space", "every bridge deck in the province register", "#3b82f6"));
        _numbers.Children.Add(Tile($"{_confirmed.Count}", _structureLevel ? "confirmed by 2 satellite directions" : "strongest warning sign", "inspect these first", "#ef4444"));
        _numbers.Children.Add(Tile($"{_confirmed.Count + _other.Count}", "bridges with a warning sign", "candidates for a drone check", "#f59e0b"));
        var (_, scored) = Progress(_confirmed.Concat(_other).ToList());
        _numbers.Children.Add(Tile($"{scored}", "already inspected", "with a NEN 2767 condition score", "#0d9e75"));
    }

    private static Control Tile(string big, string caption, string sub, string color)
    {
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(new TextBlock { Text = big, FontSize = 38, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse(color)) });
        sp.Children.Add(Am.P(caption));
        sp.Children.Add(Am.M(sub));
        var inner = new Border { BorderThickness = new Thickness(4, 0, 0, 0), BorderBrush = new SolidColorBrush(Color.Parse(color)), Padding = new Thickness(12, 2, 4, 2), Child = sp };
        var card = Am.Card(inner);
        card.Width = 250;
        card.Margin = new Thickness(0, 0, 12, 12);
        return card;
    }

    private void BuildSteps()
    {
        _steps.Children.Clear();
        var (p1, s1) = Progress(_confirmed);
        var (p2, s2) = Progress(_other);
        bool agreed = File.Exists(AgreedFlag);

        AddStep(_total > 0 ? 2 : 0, "1. Check every bridge from space",
            _total > 0 ? $"Done — {_total:N0} bridges, satellite data {_release}, processed {_runDate}." : "Run the satellite screening.");
        AddStep(_confirmed.Count == 0 ? 2 : s1 == _confirmed.Count ? 2 : p1 > 0 ? 1 : 0,
            $"2. Inspect the {_confirmed.Count} shortlisted bridges with the drone",
            $"{p1} of {_confirmed.Count} on the inspection list · {s1} of {_confirmed.Count} scored (NEN 2767).");
        AddStep(_other.Count == 0 ? 2 : s2 == _other.Count ? 2 : p2 > 0 ? 1 : 0,
            $"3. Inspect the other {_other.Count} bridges with a warning sign",
            $"{p2} of {_other.Count} on the inspection list · {s2} of {_other.Count} scored.");

        var agreeBox = new CheckBox { IsChecked = agreed, Content = "4. Warning levels agreed with the province's engineers (tick when done)" };
        agreeBox.Dyn(CheckBox.ForegroundProperty, "AppTextPrimary");
        agreeBox.IsCheckedChanged += (_, _) =>
        {
            try
            {
                if (agreeBox.IsChecked == true) File.WriteAllText(AgreedFlag, DateTime.UtcNow.ToString("o"));
                else if (File.Exists(AgreedFlag)) File.Delete(AgreedFlag);
            }
            catch (Exception ex) { Say($"Could not save: {ex.Message}"); }
        };
        _steps.Children.Add(agreeBox);
    }

    private void AddStep(int state, string title, string detail)
    {
        var icon = state == 2 ? "✅" : state == 1 ? "🔶" : "⬜";
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new TextBlock { Text = icon, FontSize = 18, VerticalAlignment = VerticalAlignment.Top });
        var txt = new StackPanel { Spacing = 1 };
        txt.Children.Add(Am.P(title));
        txt.Children.Add(Am.M(detail));
        row.Children.Add(txt);
        _steps.Children.Add(row);
    }

    private void BuildShortlist()
    {
        _shortlist.Children.Clear();
        _others.Children.Clear();
        _shortTitle.Text = _structureLevel
            ? $"🎯 Shortlist — {_confirmed.Count} bridges confirmed by two satellite viewing directions"
            : $"🎯 Shortlist — {_confirmed.Count} bridges with the strongest warning sign";
        _shortExplain.Text = "Inspect these first. Click “Show on map” to see the bridge and its satellite measurement points; " +
                             "“Add to inspection list” puts it in the 📋 Inspection Tasks sub-tab for the drone flight.";
        if (_confirmed.Count == 0) _shortlist.Children.Add(Am.M("Nothing on the shortlist."));
        int i = 0;
        foreach (var it in _confirmed) _shortlist.Children.Add(Row(++i, it, "#ef4444"));

        _othersBox.Header = $"Other bridges with a weaker warning sign ({_other.Count}) — click to open";
        foreach (var it in _other) _others.Children.Add(Row(++i, it, it.Level == "Priority" ? "#ef4444" : "#f59e0b"));
    }

    private Control Row(int rank, Item it, string color)
    {
        var task = FindTask(_tasks, it.Ids);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*,Auto") };

        var badge = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16), VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.Parse(color)),
            Child = new TextBlock { Text = rank.ToString(), FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        grid.Children.Add(badge);

        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(Am.H(it.Manager));
        text.Children.Add(Am.P(it.Movement));
        text.Children.Add(Am.M(it.Evidence + (it.Ids.Count > 1 ? $" · {it.Ids.Count} register deck parts" : "")));
        string status = task == null ? "Inspection: not planned yet"
            : task.Nen2767Score is int sc ? $"Inspection: done — NEN 2767 score {sc} ({NenNames[sc]})"
            : $"Inspection: {task.Status.ToLowerInvariant()} (on the inspection list)";
        text.Children.Add(new TextBlock
        {
            Text = status, FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse(task?.Nen2767Score != null ? "#0d9e75" : task != null ? "#22d3ee" : "#94a3b8"))
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var buttons = new StackPanel { Spacing = 0, Margin = new Thickness(8, 0, 0, 0) };
        if (it.Lat.HasValue && it.Lon.HasValue)
            buttons.Children.Add(Am.Btn("📍 Show on map", (_, _) => AmNav.ShowOnMap(it.AssetId, it.Lat!.Value, it.Lon!.Value)));
        if (task == null)
            buttons.Children.Add(Am.Btn("➕ Add to inspection list", (_, _) => { AddTasks(new[] { it }); Say($"{it.Manager} added to the inspection list."); Load(); }));
        if (it.Lat.HasValue && it.Lon.HasValue)
        {
            var coords = $"{it.Lat.Value.ToString("0.00000", CultureInfo.InvariantCulture)}, {it.Lon.Value.ToString("0.00000", CultureInfo.InvariantCulture)}";
            buttons.Children.Add(Am.Btn("📋 Copy location", async (_, _) =>
            {
                var cb = TopLevel.GetTopLevel(this)?.Clipboard;
                if (cb != null) { await cb.SetTextAsync(coords); Say($"Copied {coords} — paste it into Mission Planner."); }
            }));
        }
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);

        var accent = new Border { BorderThickness = new Thickness(4, 0, 0, 0), BorderBrush = new SolidColorBrush(Color.Parse(color)), Padding = new Thickness(10, 4, 4, 4), Child = grid };
        return Am.Card(accent);
    }

    // =============================================================== talking points + handout
    private string TalkingPoints()
    {
        var sb = new StringBuilder();
        sb.AppendLine("TALKING POINTS — Province of Groningen, bridge monitoring");
        sb.AppendLine();
        sb.AppendLine("1. The problem");
        sb.AppendLine($"   You manage many bridges and inspect them on fixed cycles. Problems can grow between inspections.");
        sb.AppendLine("2. What we did");
        sb.AppendLine($"   We checked all {_total:N0} bridge decks in the province with free European satellite data (Copernicus EGMS, {_release}).");
        sb.AppendLine("   The satellites measure millimetre ground movement. We compare each bridge with the ground around it.");
        sb.AppendLine("3. The result");
        sb.AppendLine(_structureLevel
            ? $"   {_confirmed.Count} bridges are seen moving differently by two independent satellite viewing directions. Another {_other.Count} show a weaker sign."
            : $"   {_confirmed.Count} bridges show the strongest warning sign. Another {_other.Count} show a weaker one.");
        sb.AppendLine("4. Our proposal");
        sb.AppendLine($"   We fly our drone to the {_confirmed.Count} shortlisted bridges first and give each a NEN 2767 condition score — the scale your engineers already use.");
        sb.AppendLine("   Result: you know where to look first, instead of inspecting everything on a fixed schedule.");
        sb.AppendLine("5. Honesty");
        sb.AppendLine("   These are early warning signs, not proof of damage. The warning levels are a starting point we set together with your engineers.");
        sb.AppendLine("   Every warning is checkable: exact satellite files, measurement points and settings are recorded.");
        sb.AppendLine();
        sb.AppendLine("SHORTLIST");
        int i = 0;
        foreach (var it in _confirmed) sb.AppendLine($"   {++i}. {it.Manager} — {it.Movement}");
        return sb.ToString();
    }

    private void MakeHandout()
    {
        if (_total == 0) { Say("Run the satellite screening first."); return; }
        string E(string s) => WebUtility.HtmlEncode(s);
        string Loc(Item it) => it.Lat.HasValue && it.Lon.HasValue
            ? $"<a href=\"https://www.openstreetmap.org/?mlat={it.Lat.Value.ToString("0.00000", CultureInfo.InvariantCulture)}&mlon={it.Lon.Value.ToString("0.00000", CultureInfo.InvariantCulture)}#map=18/{it.Lat.Value.ToString("0.00000", CultureInfo.InvariantCulture)}/{it.Lon.Value.ToString("0.00000", CultureInfo.InvariantCulture)}\">map</a>"
            : "—";
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Bridge monitoring briefing — Province of Groningen</title><style>");
        sb.Append("body{font-family:system-ui,-apple-system,Segoe UI,Roboto,sans-serif;max-width:900px;margin:32px auto;padding:0 20px;color:#1e293b}");
        sb.Append("h1{font-size:26px;margin:0 0 6px}h2{font-size:17px;margin:26px 0 8px;color:#0f3a6b}.muted{color:#64748b;font-size:13px}");
        sb.Append(".nums{display:flex;gap:14px;flex-wrap:wrap;margin:18px 0}.num{flex:1;min-width:170px;border-left:5px solid;padding:8px 14px;background:#f8fafc;border-radius:6px}");
        sb.Append(".num b{display:block;font-size:34px}table{border-collapse:collapse;width:100%;font-size:13px}td,th{border-bottom:1px solid #e2e8f0;padding:7px 6px;text-align:left;vertical-align:top}");
        sb.Append("th{background:#f1f5f9}.box{background:#f8fafc;border-radius:6px;padding:10px 16px;font-size:14px}@media print{a{color:#1e293b}}");
        sb.Append("</style></head><body>");
        sb.Append($"<div class=\"muted\">DAMbv · Bridge monitoring briefing · {DateTime.Now:d MMMM yyyy}</div>");
        sb.Append($"<h1>We checked all {_total:N0} bridges in the province from space. {_confirmed.Count} need a closer look first.</h1>");
        sb.Append($"<p class=\"muted\">Free European satellite data: Copernicus European Ground Motion Service (EGMS), release {E(_release)}, processed {E(_runDate)}.</p>");
        sb.Append("<div class=\"nums\">");
        sb.Append($"<div class=\"num\" style=\"border-color:#3b82f6\"><b>{_total:N0}</b>bridges checked from space</div>");
        sb.Append($"<div class=\"num\" style=\"border-color:#ef4444\"><b>{_confirmed.Count}</b>{(_structureLevel ? "confirmed by two satellite directions" : "strongest warning sign")}</div>");
        sb.Append($"<div class=\"num\" style=\"border-color:#f59e0b\"><b>{_confirmed.Count + _other.Count}</b>bridges with a warning sign</div>");
        sb.Append("</div>");
        sb.Append("<h2>How it works</h2><div class=\"box\">Radar satellites (Sentinel-1) measure ground and structure movement to the millimetre. " +
                  "For every bridge we compare its movement with the ground around it. Bridges that move differently get a warning sign. " +
                  "Our drone then inspects only those bridges, and a qualified inspector gives a NEN 2767 condition score (1 excellent – 6 very poor).</div>");
        sb.Append($"<h2>Shortlist — inspect these first ({_confirmed.Count})</h2><table><tr><th>#</th><th>Registered manager</th><th>What the satellite sees</th><th>Evidence</th><th>Location</th></tr>");
        int i = 0;
        foreach (var it in _confirmed)
            sb.Append($"<tr><td>{++i}</td><td>{E(it.Manager)}</td><td>{E(it.Movement)}</td><td>{E(it.Evidence)}</td><td>{Loc(it)}</td></tr>");
        sb.Append("</table>");
        sb.Append("<h2>What this is — and what it is not</h2><div class=\"box\"><p>✔ An early-warning list based on free, public satellite data, fully checkable (data version, measurement points and settings are recorded).</p>" +
                  "<p>✘ Not proof of damage — only an inspection shows what is really going on.</p>" +
                  "<p>✘ The warning levels (2 and 4 mm per year) are a starting point, to be agreed with your engineers.</p>" +
                  "<p>ℹ For many small or low bridges the satellite sees no points on the deck itself; for those only an area check is available.</p></div>");
        sb.Append("<h2>Proposal</h2><div class=\"box\">A pilot: drone inspection of the shortlisted bridges, a NEN 2767 score per bridge, and a yearly satellite re-check of every bridge in the province.</div>");
        sb.Append("<p class=\"muted\">DAMbv B.V. (Dutch Autonomous Mobility), Groningen</p></body></html>");

        try
        {
            Directory.CreateDirectory(ScreeningOut);
            var path = Path.Combine(ScreeningOut, "briefing_province_groningen.html");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", path) { UseShellExecute = false });
            Say($"Briefing opened in your browser. To make a PDF: press Ctrl+P there and choose “Save as PDF”. File: {path}");
        }
        catch (Exception ex) { Say($"Could not create the briefing: {ex.Message}"); }
    }
}
