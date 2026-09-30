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
// 📝 Briefing — the whole story in plain language (English or Dutch), for a meeting with the
// province and for the practical follow-up (shortlist -> drone inspection -> NEN 2767 score).
// Every number is computed from the real screening results on disk.
// =====================================================================================
internal sealed class AmBriefingPanel : AmPanel
{
    private sealed class Item
    {
        public string AssetId = "", OwnerType = "", OwnerName = "", Manager = "", Level = "";
        public double Diff;                    // signed mm/yr (line of sight or vertical/sideways)
        public string Kind = "";               // "los", "up", "east"
        public int TracksFlagging, TracksQualifying, DeckN, LocalN;
        public double? Lat, Lon, Abs;
        public bool Confirmed;
        public List<string> Ids = new();
        public bool Provincial => OwnerType.StartsWith("Provincie", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] NenEn = { "", "Excellent", "Good", "Reasonable", "Moderate", "Poor", "Very poor" };
    private static readonly string[] NenNl = { "", "Uitstekend", "Goed", "Redelijk", "Matig", "Slecht", "Zeer slecht" };
    private static readonly string[] MonthsNl = { "januari", "februari", "maart", "april", "mei", "juni", "juli", "augustus", "september", "oktober", "november", "december" };
    private static readonly string[] MonthsEn = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
    private static string NotesFile => Path.Combine(ToolsDir, "briefing_notes.txt");
    private static string AgreedFlag => Path.Combine(ToolsDir, "briefing_thresholds_agreed.flag");
    private static string LangFile => Path.Combine(ToolsDir, "briefing_language.txt");

    private bool _nl;
    private bool _onlyProvince;
    private string L(string en, string nl) => _nl ? nl : en;

    // controls (re-created when the language changes)
    private TextBlock _headline = new(), _subline = new(), _msg = new(), _shortTitle = new(), _shortExplain = new();
    private WrapPanel _numbers = new();
    private StackPanel _steps = new(), _shortlist = new(), _others = new();
    private Expander _othersBox = new();
    private TextBox _notes = new();

    private List<Item> _allConfirmed = new(), _allOther = new(), _confirmed = new(), _other = new();
    private List<InspectionTask> _tasks = new();
    private int _total, _roadSegments, _roadFlagged, _roadConfirmed;
    private string _release = "", _runDate = "";
    private bool _structureLevel;

    public AmBriefingPanel()
    {
        try { _nl = File.Exists(LangFile) && File.ReadAllText(LangFile).Trim() == "nl"; } catch { }
        BuildAll();
        AttachedToVisualTree += (_, _) => Load();
    }

    // =============================================================== layout
    private void BuildAll()
    {
        string savedNotes = _notes.Text ?? "";
        Root.Children.Clear();

        _headline = new TextBlock { FontSize = 24, FontWeight = FontWeight.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        _subline = new TextBlock { FontSize = 13, Foreground = new SolidColorBrush(Color.Parse("#cbd5e1")), TextWrapping = TextWrapping.Wrap };
        _msg = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#22d3ee")), TextWrapping = TextWrapping.Wrap };
        _numbers = new WrapPanel();
        _steps = new StackPanel { Spacing = 8 };
        _shortTitle = Am.H("");
        _shortExplain = Am.M("");
        _shortlist = new StackPanel { Spacing = 8 };
        _others = new StackPanel { Spacing = 8 };
        _othersBox = new Expander { HorizontalAlignment = HorizontalAlignment.Stretch, Content = _others };
        _notes = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, Text = savedNotes,
            Watermark = L("Meeting notes, questions from the province, agreements, contacts…", "Notities van het gesprek, vragen van de provincie, afspraken, contactpersonen…")
        };

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
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        top.Children.Add(new TextBlock
        {
            Text = L("📝 BRIEFING FOR THE PROVINCE OF GRONINGEN", "📝 BRIEFING VOOR DE PROVINCIE GRONINGEN"),
            FontSize = 11, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#22d3ee")), VerticalAlignment = VerticalAlignment.Center
        });
        var lang = Am.Btn(_nl ? "🌐 English" : "🌐 Nederlands", (_, _) =>
        {
            _nl = !_nl;
            try { File.WriteAllText(LangFile, _nl ? "nl" : "en"); } catch { }
            BuildAll();
            Load();
        });
        lang.Margin = new Thickness(0);
        Grid.SetColumn(lang, 1);
        top.Children.Add(lang);
        heroStack.Children.Add(top);
        heroStack.Children.Add(_headline);
        heroStack.Children.Add(_subline);
        var actions = new WrapPanel();
        actions.Children.Add(Am.Btn(L("📄 Make one-page briefing (print / e-mail)", "📄 Maak briefing van één pagina (printen / e-mailen)"), (_, _) => MakeHandout()));
        actions.Children.Add(Am.Btn(L("📋 Copy talking points", "📋 Kopieer gesprekspunten"), async (_, _) =>
        {
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb != null)
            {
                await cb.SetTextAsync(TalkingPoints());
                Say(L("Talking points copied — paste them into your notes, an e-mail or a slide.", "Gesprekspunten gekopieerd — plak ze in je notities, een e-mail of een dia."));
            }
        }));
        actions.Children.Add(Am.Btn(L("🔄 Refresh", "🔄 Vernieuwen"), (_, _) => Load()));
        heroStack.Children.Add(actions);

        var filter = new ComboBox
        {
            ItemsSource = new[] { L("All bridge managers", "Alle beheerders"), L("Only bridges managed by the province", "Alleen bruggen in beheer van de provincie") },
            SelectedIndex = _onlyProvince ? 1 : 0, MinWidth = 320
        };
        filter.SelectionChanged += (_, _) => { _onlyProvince = filter.SelectedIndex == 1; ApplyFilterAndBuild(); };
        var filterRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        filterRow.Children.Add(new TextBlock { Text = L("Show:", "Toon:"), Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        filterRow.Children.Add(filter);
        heroStack.Children.Add(filterRow);
        heroStack.Children.Add(_msg);
        hero.Child = heroStack;
        Root.Children.Add(hero);

        Root.Children.Add(_numbers);

        var stepsCard = new StackPanel { Spacing = 8 };
        stepsCard.Children.Add(Am.H(L("✅ What to do next", "✅ Volgende stappen")));
        stepsCard.Children.Add(Am.M(L("Progress updates automatically when inspection tasks get a NEN 2767 score in the Condition sub-tab.",
                                      "De voortgang werkt zichzelf bij zodra inspecties een NEN 2767-score krijgen in het tabblad Condition.")));
        stepsCard.Children.Add(_steps);
        Root.Children.Add(Am.Card(stepsCard));

        var shortCard = new StackPanel { Spacing = 8 };
        shortCard.Children.Add(_shortTitle);
        shortCard.Children.Add(_shortExplain);
        var shortActions = new WrapPanel();
        shortActions.Children.Add(Am.Btn(L("➕ Put the whole shortlist on the inspection list", "➕ Zet de hele shortlist op de inspectielijst"), (_, _) =>
        {
            int added = AddTasks(_confirmed);
            Say(added == 0 ? L("All shortlisted bridges are already on the inspection list.", "Alle bruggen van de shortlist staan al op de inspectielijst.")
                           : L($"{added} bridges added to the inspection list (📋 Inspection Tasks sub-tab).", $"{added} bruggen toegevoegd aan de inspectielijst (tabblad 📋 Inspection Tasks)."));
            Load();
        }));
        shortCard.Children.Add(shortActions);
        shortCard.Children.Add(_shortlist);
        Root.Children.Add(Am.Card(shortCard));
        Root.Children.Add(_othersBox);

        var honest = new StackPanel { Spacing = 6 };
        honest.Children.Add(Am.H(L("💬 What this is — and what it is not", "💬 Wat dit wel is — en wat niet")));
        foreach (var line in HonestLines()) honest.Children.Add(Am.P(line));
        Root.Children.Add(Am.Card(honest));

        var notesCard = new StackPanel { Spacing = 8 };
        notesCard.Children.Add(Am.H(L("🗒 My notes for this project", "🗒 Mijn notities voor dit project")));
        notesCard.Children.Add(Am.M(L($"Saved on this computer in {NotesFile}", $"Opgeslagen op deze computer in {NotesFile}")));
        notesCard.Children.Add(_notes);
        var notesActions = new WrapPanel();
        notesActions.Children.Add(Am.Btn(L("💾 Save notes", "💾 Notities opslaan"), (_, _) => SaveNotes(true)));
        notesCard.Children.Add(notesActions);
        Root.Children.Add(Am.Card(notesCard));
        _notes.LostFocus += (_, _) => SaveNotes(false);
    }

    private IEnumerable<string> HonestLines()
    {
        yield return L("✔ It IS an early-warning list: Copernicus radar-satellite measurements (EGMS) show which bridges move differently from the ground around them.",
                       "✔ Het IS een vroegtijdige waarschuwingslijst: radarsatellietmetingen van Copernicus (EGMS) laten zien welke bruggen anders bewegen dan de grond eromheen.");
        yield return L("✔ It IS checkable: every warning comes with the exact satellite files, measurement points and settings (Value & Audit sub-tab).",
                       "✔ Het IS controleerbaar: bij elke waarschuwing zijn de exacte satellietbestanden, meetpunten en instellingen vastgelegd.");
        yield return L("✘ It is NOT proof of damage. Only an inspection (our drone + a qualified inspector) tells what is really going on.",
                       "✘ Het is GEEN bewijs van schade. Alleen een inspectie (onze drone + een gekwalificeerde inspecteur) laat zien wat er werkelijk aan de hand is.");
        yield return L("✘ The warning levels (2 and 4 mm per year) are a starting point, to be set together with the province's engineers.",
                       "✘ De waarschuwingsgrenzen (2 en 4 mm per jaar) zijn een startpunt, vast te stellen samen met de ingenieurs van de provincie.");
        yield return L("ℹ Not every bridge belongs to the province — some belong to ProRail (railway bridges), Rijkswaterstaat, municipalities or water boards. Share those signals with the manager.",
                       "ℹ Niet elke brug is van de provincie — sommige zijn van ProRail (spoorbruggen), Rijkswaterstaat, gemeenten of waterschappen. Deel die signalen met de beheerder.");
        yield return L("ℹ For many small or low bridges the satellite sees no points on the deck itself; for those only the area check (100 m grid) is available.",
                       "ℹ Bij veel kleine of lage bruggen ziet de satelliet geen punten op het brugdek zelf; daar is alleen de gebiedscontrole (100 m-raster) beschikbaar.");
    }

    private void Say(string text) => _msg.Text = text;

    private void SaveNotes(bool tell)
    {
        try
        {
            Directory.CreateDirectory(ToolsDir);
            File.WriteAllText(NotesFile, _notes.Text ?? "");
            if (tell) Say(L("Notes saved.", "Notities opgeslagen."));
        }
        catch (Exception ex) { Say($"{L("Could not save notes", "Notities opslaan mislukt")}: {ex.Message}"); }
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

            _allConfirmed = new(); _allOther = new();
            if (_structureLevel)
                foreach (var s in GroupStructure(structure.Where(s => s.Status is "Priority" or "Review")))
                {
                    var it = new Item
                    {
                        AssetId = s.AssetId, OwnerType = s.OwnerType, OwnerName = s.OwnerName, Manager = s.ManagerLabel, Level = s.Status,
                        Diff = s.DiffLos ?? 0, Kind = "los", TracksFlagging = s.TracksFlagging, TracksQualifying = s.TracksQualifying, DeckN = s.DeckN,
                        Lat = s.Lat, Lon = s.Lon, Abs = Math.Abs(s.DiffLos ?? 0), Confirmed = s.TracksFlagging >= 2,
                        Ids = s.PartIds.Count > 0 ? s.PartIds : new List<string> { s.AssetId }
                    };
                    (it.Confirmed ? _allConfirmed : _allOther).Add(it);
                }
            else
                foreach (var a in GroupParts(area.Where(a => a.Screening is "Priority" or "Review")))
                {
                    double up = a.UpDiff ?? 0, ew = a.EastDiff ?? 0;
                    bool vertical = Math.Abs(up) >= Math.Abs(ew);
                    var it = new Item
                    {
                        AssetId = a.AssetId, OwnerType = a.OwnerType, OwnerName = a.OwnerName, Manager = a.ManagerLabel, Level = a.Screening,
                        Diff = vertical ? up : ew, Kind = vertical ? "up" : "east", LocalN = a.LocalN ?? 0,
                        Lat = a.Lat, Lon = a.Lon, Abs = a.MaxAbs, Confirmed = a.Screening == "Priority",
                        Ids = a.PartIds.Count > 0 ? a.PartIds : new List<string> { a.AssetId }
                    };
                    (it.Confirmed ? _allConfirmed : _allOther).Add(it);
                }
            _allConfirmed = _allConfirmed.OrderByDescending(i => i.Abs).ToList();
            _allOther = _allOther.OrderByDescending(i => i.Level == "Priority").ThenByDescending(i => i.Abs).ToList();

            // provincial roads (if the Road Check has been run)
            var roadArea = ReadRanked(RoadsRankedCsv);
            var roadPts = ReadStructureResults(RoadPointsCsv);
            _roadSegments = roadArea.Count > 0 ? roadArea.Count : roadPts.Count;
            var flaggedIds = new HashSet<string>(roadPts.Where(r => r.Status is "Priority" or "Review").Select(r => r.AssetId));
            foreach (var r in roadArea.Where(r => r.Screening is "Priority" or "Review")) flaggedIds.Add(r.AssetId);
            _roadFlagged = flaggedIds.Count;
            _roadConfirmed = roadPts.Count(r => r.TracksFlagging >= 2);
        }
        catch (Exception ex) { Say($"{L("Could not read the results", "Resultaten lezen mislukt")}: {ex.Message}"); return; }
        ApplyFilterAndBuild();
    }

    private void ApplyFilterAndBuild()
    {
        _confirmed = _onlyProvince ? _allConfirmed.Where(i => i.Provincial).ToList() : _allConfirmed;
        _other = _onlyProvince ? _allOther.Where(i => i.Provincial).ToList() : _allOther;
        BuildHero();
        BuildNumbers();
        BuildSteps();
        BuildShortlist();
    }

    private string Movement(Item it)
    {
        double a = Math.Abs(it.Diff);
        string x = a.ToString("0.0", CultureInfo.InvariantCulture);
        if (_nl) x = x.Replace('.', ',');   // Dutch decimal comma (no dependency on installed cultures)
        return it.Kind switch
        {
            "los" => it.Diff < 0
                ? L($"The deck moves {x} mm per year more away from the satellite than the ground around it — usually this means the bridge is sinking.",
                    $"Het brugdek beweegt {x} mm per jaar meer van de satelliet af dan de grond eromheen — meestal betekent dit dat de brug zakt.")
                : L($"The deck moves {x} mm per year more towards the satellite than the ground around it.",
                    $"Het brugdek beweegt {x} mm per jaar meer naar de satelliet toe dan de grond eromheen."),
            "up" => it.Diff < 0
                ? L($"The ground at this bridge sinks {x} mm per year faster than the area around it.", $"De grond bij deze brug zakt {x} mm per jaar sneller dan de omgeving.")
                : L($"The ground at this bridge rises {x} mm per year more than the area around it.", $"De grond bij deze brug stijgt {x} mm per jaar meer dan de omgeving."),
            _ => L($"The ground at this bridge moves {x} mm per year sideways ({(it.Diff < 0 ? "west" : "east")}) compared with the area around it.",
                   $"De grond bij deze brug beweegt {x} mm per jaar zijwaarts ({(it.Diff < 0 ? "west" : "oost")}) ten opzichte van de omgeving.")
        };
    }

    private string Evidence(Item it) => it.Kind == "los"
        ? L($"Seen by {it.TracksFlagging} of {it.TracksQualifying} satellite viewing directions · {it.DeckN} radar points on the deck",
            $"Gezien door {it.TracksFlagging} van {it.TracksQualifying} satellietrichtingen · {it.DeckN} radarpunten op het brugdek")
        : L($"{it.LocalN} satellite points at the bridge (100 m area grid)", $"{it.LocalN} satellietpunten bij de brug (100 m-raster)");

    private string TaskStatus(InspectionTask? t)
    {
        if (t == null) return L("Inspection: not planned yet", "Inspectie: nog niet gepland");
        if (t.Nen2767Score is int sc) return L($"Inspection: done — NEN 2767 score {sc} ({NenEn[sc]})", $"Inspectie: uitgevoerd — NEN 2767-score {sc} ({NenNl[sc]})");
        string st = _nl ? t.Status switch { "Open" => "open", "Scheduled" => "ingepland", "Inspected" => "geïnspecteerd", "Closed" => "afgesloten", _ => t.Status } : t.Status.ToLowerInvariant();
        return L($"Inspection: {st} (on the inspection list)", $"Inspectie: {st} (staat op de inspectielijst)");
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

    private string Today() => _nl ? $"{DateTime.Now.Day} {MonthsNl[DateTime.Now.Month - 1]} {DateTime.Now.Year}" : $"{DateTime.Now.Day} {MonthsEn[DateTime.Now.Month - 1]} {DateTime.Now.Year}";
    private string N(int v)
    {
        var s = v.ToString("N0", CultureInfo.InvariantCulture);
        return _nl ? s.Replace(",", ".") : s;   // Dutch thousands separator
    }

    // =============================================================== visuals
    private void BuildHero()
    {
        if (_total == 0)
        {
            _headline.Text = L("No satellite check yet.", "Nog geen satellietcontrole.");
            _subline.Text = L("Open the 🛰 Satellite Screening sub-tab and click “Run satellite screening”. This page then fills itself.",
                              "Open het tabblad 🛰 Satellite Screening en klik op “Run satellite screening”. Deze pagina vult zich daarna vanzelf.");
            return;
        }
        int n = _confirmed.Count;
        _headline.Text = _onlyProvince
            ? L($"We checked all {N(_total)} bridges in the province from space. {n} of the province's own bridges need a closer look first.",
                $"We hebben alle {N(_total)} bruggen in de provincie vanuit de ruimte gecontroleerd. {n} bruggen in beheer van de provincie verdienen als eerste een nadere inspectie.")
            : L($"We checked all {N(_total)} bridges in the province from space. {n} {(n == 1 ? "needs" : "need")} a closer look first.",
                $"We hebben alle {N(_total)} bruggen in de provincie vanuit de ruimte gecontroleerd. {n} {(n == 1 ? "verdient" : "verdienen")} als eerste een nadere inspectie.");
        _subline.Text = _structureLevel
            ? L($"Copernicus radar-satellite measurements (EGMS, release {_release}), processed {_runDate}. The {n} bridges below move differently from the ground around them, " +
                $"seen by two independent satellite viewing directions — the strongest signal we have. Another {_other.Count} bridges show a weaker warning sign.",
                $"Radarsatellietmetingen van Copernicus (EGMS, release {_release}), verwerkt op {_runDate}. De {n} bruggen hieronder bewegen anders dan de grond eromheen, " +
                $"gezien vanuit twee onafhankelijke satellietrichtingen — het sterkste signaal dat we hebben. Nog {_other.Count} bruggen tonen een zwakker waarschuwingssignaal.")
            : L($"Copernicus radar-satellite measurements (EGMS, release {_release}), processed {_runDate}. The {n} bridges below show the strongest warning sign; another {_other.Count} show a weaker one.",
                $"Radarsatellietmetingen van Copernicus (EGMS, release {_release}), verwerkt op {_runDate}. De {n} bruggen hieronder tonen het sterkste waarschuwingssignaal; nog {_other.Count} een zwakker signaal.");
    }

    private void BuildNumbers()
    {
        _numbers.Children.Clear();
        if (_total == 0) return;
        _numbers.Children.Add(Tile(N(_total), L("bridges checked from space", "bruggen vanuit de ruimte gecontroleerd"), L("every bridge deck in the national register (BGT)", "elk brugdek in de basisregistratie (BGT)"), "#3b82f6"));
        _numbers.Children.Add(Tile($"{_confirmed.Count}", _structureLevel ? L("confirmed by 2 satellite directions", "bevestigd door 2 satellietrichtingen") : L("strongest warning sign", "sterkste waarschuwingssignaal"),
                                   L("inspect these first", "deze eerst inspecteren"), "#ef4444"));
        _numbers.Children.Add(Tile($"{_confirmed.Count + _other.Count}", L("bridges with a warning sign", "bruggen met een waarschuwingssignaal"), L("candidates for a drone check", "kandidaten voor een dronecontrole"), "#f59e0b"));
        var (_, scored) = Progress(_confirmed.Concat(_other).ToList());
        _numbers.Children.Add(Tile($"{scored}", L("already inspected", "al geïnspecteerd"), L("with a NEN 2767 condition score", "met een NEN 2767-conditiescore"), "#0d9e75"));
        if (_roadSegments > 0)
            _numbers.Children.Add(Tile($"{_roadFlagged}", L("provincial road stretches with a warning sign", "provinciale wegvakken met een waarschuwingssignaal"),
                                       L($"of {N(_roadSegments)} checked · {_roadConfirmed} confirmed by 2 satellites · see 🛣 Road Check",
                                         $"van {N(_roadSegments)} gecontroleerd · {_roadConfirmed} bevestigd door 2 satellieten · zie 🛣 Road Check"), "#a855f7"));
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
        AddStep(_total > 0 ? 2 : 0, L("1. Check every bridge from space", "1. Alle bruggen vanuit de ruimte controleren"),
            _total > 0 ? L($"Done — {N(_total)} bridges, satellite data {_release}, processed {_runDate}.", $"Klaar — {N(_total)} bruggen, satellietdata {_release}, verwerkt op {_runDate}.")
                       : L("Run the satellite screening.", "Voer de satellietcontrole uit."));
        AddStep(_confirmed.Count == 0 || s1 == _confirmed.Count ? 2 : p1 > 0 ? 1 : 0,
            L($"2. Inspect the {_confirmed.Count} shortlisted bridges with the drone", $"2. De {_confirmed.Count} bruggen van de shortlist met de drone inspecteren"),
            L($"{p1} of {_confirmed.Count} on the inspection list · {s1} of {_confirmed.Count} scored (NEN 2767).", $"{p1} van {_confirmed.Count} op de inspectielijst · {s1} van {_confirmed.Count} beoordeeld (NEN 2767)."));
        AddStep(_other.Count == 0 || s2 == _other.Count ? 2 : p2 > 0 ? 1 : 0,
            L($"3. Inspect the other {_other.Count} bridges with a warning sign", $"3. De overige {_other.Count} bruggen met een waarschuwingssignaal inspecteren"),
            L($"{p2} of {_other.Count} on the inspection list · {s2} of {_other.Count} scored.", $"{p2} van {_other.Count} op de inspectielijst · {s2} van {_other.Count} beoordeeld."));

        var agreeBox = new CheckBox
        {
            IsChecked = File.Exists(AgreedFlag),
            Content = L("4. Warning levels agreed with the province's engineers (tick when done)", "4. Waarschuwingsgrenzen afgestemd met de ingenieurs van de provincie (aanvinken als klaar)")
        };
        agreeBox.Dyn(CheckBox.ForegroundProperty, "AppTextPrimary");
        agreeBox.IsCheckedChanged += (_, _) =>
        {
            try
            {
                if (agreeBox.IsChecked == true) File.WriteAllText(AgreedFlag, DateTime.UtcNow.ToString("o"));
                else if (File.Exists(AgreedFlag)) File.Delete(AgreedFlag);
            }
            catch (Exception ex) { Say(ex.Message); }
        };
        _steps.Children.Add(agreeBox);
    }

    private void AddStep(int state, string title, string detail)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new TextBlock { Text = state == 2 ? "✅" : state == 1 ? "🔶" : "⬜", FontSize = 18 });
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
            ? L($"🎯 Shortlist — {_confirmed.Count} bridges confirmed by two satellite viewing directions", $"🎯 Shortlist — {_confirmed.Count} bruggen bevestigd door twee satellietrichtingen")
            : L($"🎯 Shortlist — {_confirmed.Count} bridges with the strongest warning sign", $"🎯 Shortlist — {_confirmed.Count} bruggen met het sterkste waarschuwingssignaal");
        _shortExplain.Text = L("Inspect these first. “Show on map” shows the bridge and its satellite measurement points; “Add to inspection list” puts it in the 📋 Inspection Tasks sub-tab for the drone flight.",
                               "Deze eerst inspecteren. “Toon op kaart” laat de brug en de satellietmeetpunten zien; “Op inspectielijst” zet de brug in het tabblad 📋 Inspection Tasks voor de dronevlucht.");
        if (_confirmed.Count == 0) _shortlist.Children.Add(Am.M(L("Nothing on the shortlist.", "Niets op de shortlist.")));
        int i = 0;
        foreach (var it in _confirmed) _shortlist.Children.Add(Row(++i, it, "#ef4444"));
        _othersBox.Header = L($"Other bridges with a weaker warning sign ({_other.Count}) — click to open", $"Overige bruggen met een zwakker waarschuwingssignaal ({_other.Count}) — klik om te openen");
        foreach (var it in _other) _others.Children.Add(Row(++i, it, it.Level == "Priority" ? "#ef4444" : "#f59e0b"));
    }

    private Control Row(int rank, Item it, string color)
    {
        var task = FindTask(_tasks, it.Ids);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*,Auto") };
        grid.Children.Add(new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16), VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.Parse(color)),
            Child = new TextBlock { Text = rank.ToString(), FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });

        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(Am.H(it.Manager + (it.Provincial ? "" : L("  · not managed by the province", "  · niet in beheer van de provincie"))));
        text.Children.Add(Am.P(Movement(it)));
        text.Children.Add(Am.M(Evidence(it) + (it.Ids.Count > 1 ? L($" · {it.Ids.Count} register deck parts", $" · {it.Ids.Count} brugdekdelen in het register") : "")));
        text.Children.Add(new TextBlock
        {
            Text = TaskStatus(task), FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse(task?.Nen2767Score != null ? "#0d9e75" : task != null ? "#22d3ee" : "#94a3b8"))
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var buttons = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        if (it.Lat.HasValue && it.Lon.HasValue)
            buttons.Children.Add(Am.Btn(L("📍 Show on map", "📍 Toon op kaart"), (_, _) => AmNav.ShowOnMap(it.AssetId, it.Lat!.Value, it.Lon!.Value)));
        if (task == null)
            buttons.Children.Add(Am.Btn(L("➕ Add to inspection list", "➕ Op inspectielijst"), (_, _) =>
            {
                AddTasks(new[] { it });
                Say(L($"{it.Manager} added to the inspection list.", $"{it.Manager} toegevoegd aan de inspectielijst."));
                Load();
            }));
        if (it.Lat.HasValue && it.Lon.HasValue)
        {
            var coords = $"{it.Lat.Value.ToString("0.00000", CultureInfo.InvariantCulture)}, {it.Lon.Value.ToString("0.00000", CultureInfo.InvariantCulture)}";
            buttons.Children.Add(Am.Btn(L("📋 Copy location", "📋 Kopieer locatie"), async (_, _) =>
            {
                var cb = TopLevel.GetTopLevel(this)?.Clipboard;
                if (cb != null) { await cb.SetTextAsync(coords); Say(L($"Copied {coords} — paste it into Mission Planner.", $"{coords} gekopieerd — plak het in Mission Planner.")); }
            }));
        }
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);
        return Am.Card(new Border { BorderThickness = new Thickness(4, 0, 0, 0), BorderBrush = new SolidColorBrush(Color.Parse(color)), Padding = new Thickness(10, 4, 4, 4), Child = grid });
    }

    // =============================================================== talking points + handout
    private string TalkingPoints()
    {
        var sb = new StringBuilder();
        sb.AppendLine(L("TALKING POINTS — Province of Groningen, bridge monitoring", "GESPREKSPUNTEN — Provincie Groningen, monitoring van bruggen"));
        sb.AppendLine();
        sb.AppendLine(L("1. The problem", "1. Het probleem"));
        sb.AppendLine(L("   You manage many bridges and inspect them on fixed cycles. Problems can grow between inspections.",
                        "   U beheert veel bruggen en inspecteert ze volgens vaste cycli. Problemen kunnen tussen twee inspecties in groeien."));
        sb.AppendLine(L("2. What we did", "2. Wat we hebben gedaan"));
        sb.AppendLine(L($"   We checked all {N(_total)} bridge decks in the province with Copernicus radar-satellite measurements (EGMS, release {_release}).",
                        $"   We hebben alle {N(_total)} brugdekken in de provincie gecontroleerd met radarsatellietmetingen van Copernicus (EGMS, release {_release})."));
        sb.AppendLine(L("   The satellites measure millimetre ground movement. We compare each bridge with the ground around it.",
                        "   De satellieten meten bodembeweging op de millimeter. We vergelijken elke brug met de grond eromheen."));
        sb.AppendLine(L("3. The result", "3. Het resultaat"));
        sb.AppendLine(_structureLevel
            ? L($"   {_confirmed.Count} bridges are seen moving differently by two independent satellite viewing directions. Another {_other.Count} show a weaker sign.",
                $"   {_confirmed.Count} bruggen bewegen anders, gezien vanuit twee onafhankelijke satellietrichtingen. Nog {_other.Count} tonen een zwakker signaal.")
            : L($"   {_confirmed.Count} bridges show the strongest warning sign. Another {_other.Count} show a weaker one.",
                $"   {_confirmed.Count} bruggen tonen het sterkste waarschuwingssignaal. Nog {_other.Count} een zwakker signaal."));
        if (_roadSegments > 0)
            sb.AppendLine(L($"   Provincial roads: {_roadFlagged} of {N(_roadSegments)} road stretches show a warning sign ({_roadConfirmed} confirmed by two satellites).",
                            $"   Provinciale wegen: {_roadFlagged} van {N(_roadSegments)} wegvakken tonen een waarschuwingssignaal ({_roadConfirmed} bevestigd door twee satellieten)."));
        sb.AppendLine(L("4. Our proposal", "4. Ons voorstel"));
        sb.AppendLine(L($"   We fly our drone to the {_confirmed.Count} shortlisted bridges first and give each a NEN 2767 condition score — the scale your engineers already use.",
                        $"   We vliegen met onze drone eerst naar de {_confirmed.Count} bruggen van de shortlist en geven elke brug een NEN 2767-conditiescore — de schaal die uw ingenieurs al gebruiken."));
        sb.AppendLine(L("   Result: you know where to look first, instead of inspecting everything on a fixed schedule.",
                        "   Resultaat: u weet waar u eerst moet kijken, in plaats van alles volgens een vast schema te inspecteren."));
        sb.AppendLine(L("5. Honesty", "5. Eerlijk gezegd"));
        sb.AppendLine(L("   These are early warning signs, not proof of damage. The warning levels are a starting point we set together with your engineers.",
                        "   Dit zijn vroege waarschuwingssignalen, geen bewijs van schade. De waarschuwingsgrenzen zijn een startpunt dat we samen met uw ingenieurs vaststellen."));
        sb.AppendLine(L("   Some flagged bridges belong to other managers (e.g. ProRail, Rijkswaterstaat, municipalities); we share those signals with them.",
                        "   Sommige gesignaleerde bruggen zijn van andere beheerders (bijv. ProRail, Rijkswaterstaat, gemeenten); die signalen delen we met hen."));
        sb.AppendLine();
        sb.AppendLine("SHORTLIST");
        int i = 0;
        foreach (var it in _confirmed) sb.AppendLine($"   {++i}. {it.Manager} — {Movement(it)}");
        return sb.ToString();
    }

    private void MakeHandout()
    {
        if (_total == 0) { Say(L("Run the satellite screening first.", "Voer eerst de satellietcontrole uit.")); return; }
        string E(string s) => WebUtility.HtmlEncode(s);
        string Loc(Item it)
        {
            if (!it.Lat.HasValue || !it.Lon.HasValue) return "—";
            string la = it.Lat.Value.ToString("0.00000", CultureInfo.InvariantCulture), lo = it.Lon.Value.ToString("0.00000", CultureInfo.InvariantCulture);
            return $"<a href=\"https://www.openstreetmap.org/?mlat={la}&mlon={lo}#map=18/{la}/{lo}\">{L("map", "kaart")}</a>";
        }
        string Table(IEnumerable<Item> items, ref int counter)
        {
            var t = new StringBuilder();
            t.Append($"<table><tr><th>#</th><th>{L("Registered manager", "Beheerder")}</th><th>{L("What the satellite sees", "Wat de satelliet ziet")}</th><th>{L("Evidence", "Onderbouwing")}</th><th>{L("Location", "Locatie")}</th></tr>");
            foreach (var it in items)
                t.Append($"<tr><td>{++counter}</td><td>{E(it.Manager)}</td><td>{E(Movement(it))}</td><td>{E(Evidence(it))}</td><td>{Loc(it)}</td></tr>");
            t.Append("</table>");
            return t.ToString();
        }

        var prov = _confirmed.Where(i => i.Provincial).ToList();
        var others = _confirmed.Where(i => !i.Provincial).ToList();
        var sb = new StringBuilder();
        sb.Append($"<!doctype html><html lang=\"{(_nl ? "nl" : "en")}\"><head><meta charset=\"utf-8\"><title>{L("Bridge monitoring briefing — Province of Groningen", "Briefing brugmonitoring — provincie Groningen")}</title><style>");
        sb.Append("body{font-family:system-ui,-apple-system,Segoe UI,Roboto,sans-serif;max-width:900px;margin:32px auto;padding:0 20px;color:#1e293b}");
        sb.Append("h1{font-size:26px;margin:0 0 6px}h2{font-size:17px;margin:26px 0 8px;color:#0f3a6b}.muted{color:#64748b;font-size:13px}");
        sb.Append(".nums{display:flex;gap:14px;flex-wrap:wrap;margin:18px 0}.num{flex:1;min-width:170px;border-left:5px solid;padding:8px 14px;background:#f8fafc;border-radius:6px}");
        sb.Append(".num b{display:block;font-size:34px}table{border-collapse:collapse;width:100%;font-size:13px}td,th{border-bottom:1px solid #e2e8f0;padding:7px 6px;text-align:left;vertical-align:top}");
        sb.Append("th{background:#f1f5f9}.box{background:#f8fafc;border-radius:6px;padding:10px 16px;font-size:14px}@media print{a{color:#1e293b}}");
        sb.Append("</style></head><body>");
        sb.Append($"<div class=\"muted\">DAMbv · {L("Bridge monitoring briefing", "Briefing brugmonitoring")} · {Today()}</div>");
        sb.Append($"<h1>{E(_headline.Text ?? "")}</h1>");
        sb.Append($"<p class=\"muted\">{L("Copernicus radar-satellite measurements (EGMS, European Ground Motion Service)", "Radarsatellietmetingen van Copernicus (EGMS, European Ground Motion Service)")}, release {E(_release)}, {L("processed", "verwerkt op")} {E(_runDate)}.</p>");
        sb.Append("<div class=\"nums\">");
        sb.Append($"<div class=\"num\" style=\"border-color:#3b82f6\"><b>{N(_total)}</b>{L("bridges checked from space", "bruggen vanuit de ruimte gecontroleerd")}</div>");
        sb.Append($"<div class=\"num\" style=\"border-color:#ef4444\"><b>{_confirmed.Count}</b>{(_structureLevel ? L("confirmed by two satellite directions", "bevestigd door twee satellietrichtingen") : L("strongest warning sign", "sterkste waarschuwingssignaal"))}</div>");
        sb.Append($"<div class=\"num\" style=\"border-color:#f59e0b\"><b>{_confirmed.Count + _other.Count}</b>{L("bridges with a warning sign", "bruggen met een waarschuwingssignaal")}</div>");
        if (_roadSegments > 0)
            sb.Append($"<div class=\"num\" style=\"border-color:#a855f7\"><b>{_roadFlagged}</b>{L($"provincial road stretches with a warning sign (of {N(_roadSegments)})", $"provinciale wegvakken met een waarschuwingssignaal (van {N(_roadSegments)})")}</div>");
        sb.Append("</div>");
        sb.Append($"<h2>{L("How it works", "Zo werkt het")}</h2><div class=\"box\">" + L(
            "Radar satellites (Sentinel-1) measure ground and structure movement to the millimetre. For every bridge we compare its movement with the ground around it. " +
            "Bridges that move differently get a warning sign. Our drone then inspects only those bridges, and a qualified inspector gives a NEN 2767 condition score (1 excellent – 6 very poor).",
            "Radarsatellieten (Sentinel-1) meten de beweging van bodem en bouwwerken op de millimeter. Voor elke brug vergelijken we de beweging met de grond eromheen. " +
            "Bruggen die anders bewegen krijgen een waarschuwingssignaal. Onze drone inspecteert daarna alleen die bruggen, en een gekwalificeerde inspecteur geeft een NEN 2767-conditiescore (1 uitstekend – 6 zeer slecht).") + "</div>");
        int counter = 0;
        if (prov.Count > 0)
        {
            sb.Append($"<h2>{L($"Shortlist — bridges managed by the province ({prov.Count})", $"Shortlist — bruggen in beheer van de provincie ({prov.Count})")}</h2>");
            sb.Append(Table(prov, ref counter));
        }
        if (others.Count > 0)
        {
            sb.Append($"<h2>{L($"Shortlist — bridges of other managers, to share with them ({others.Count})", $"Shortlist — bruggen van andere beheerders, om met hen te delen ({others.Count})")}</h2>");
            sb.Append(Table(others, ref counter));
        }
        sb.Append($"<h2>{L("What this is — and what it is not", "Wat dit wel is — en wat niet")}</h2><div class=\"box\">");
        foreach (var line in HonestLines()) sb.Append($"<p>{E(line)}</p>");
        sb.Append("</div>");
        sb.Append($"<h2>{L("Proposal", "Voorstel")}</h2><div class=\"box\">" + L(
            "A pilot: drone inspection of the shortlisted bridges, a NEN 2767 score per bridge, and a yearly satellite re-check of every bridge and provincial road.",
            "Een pilot: drone-inspectie van de bruggen op de shortlist, een NEN 2767-score per brug, en een jaarlijkse satellietcontrole van alle bruggen en provinciale wegen.") + "</div>");
        sb.Append("<p class=\"muted\">DAMbv B.V. (Dutch Autonomous Mobility), Groningen</p></body></html>");

        try
        {
            Directory.CreateDirectory(ScreeningOut);
            var path = Path.Combine(ScreeningOut, _nl ? "briefing_provincie_groningen_nl.html" : "briefing_province_groningen_en.html");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", path) { UseShellExecute = false });
            Say(L($"Briefing opened in your browser. To make a PDF: press Ctrl+P there and choose “Save as PDF”. File: {path}",
                  $"Briefing geopend in je browser. Voor een PDF: druk daar op Ctrl+P en kies “Opslaan als PDF”. Bestand: {path}"));
        }
        catch (Exception ex) { Say(ex.Message); }
    }
}
