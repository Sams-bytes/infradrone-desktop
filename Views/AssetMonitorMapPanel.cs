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
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using static InfraDroneDesktop.Services.AssetMonitorService;
using MBrush = Mapsui.Styles.Brush;
using MColor = Mapsui.Styles.Color;
using MPen = Mapsui.Styles.Pen;

namespace InfraDroneDesktop.Views;

/// <summary>
/// 🗺 Map sub-tab: every bridge as a coloured dot (satellite screening status), optional roads coloured by manager.
/// Click any bridge or road -> info card with register, satellite and inspection information.
/// Data: screening_out/bridges_screening.geojson (or the register file if no screening yet) and ownership_out/roads_by_owner.geojson.
/// Own map + own info card: the Mission Planner map is not touched.
/// </summary>
internal sealed class AmMapPanel : UserControl
{
    // ---- colours (bridges: screening status, roads: manager type)
    private static readonly Dictionary<string, MColor> StatusColor = new()
    {
        ["Priority"] = new MColor(239, 68, 68),
        ["Review"] = new MColor(245, 158, 11),
        ["No unusual movement"] = new MColor(13, 158, 117),
        ["No satellite data"] = new MColor(100, 116, 139),
        ["Not screened"] = new MColor(148, 163, 184)
    };
    private static readonly Dictionary<string, MColor> RoadColor = new()
    {
        ["Rijk"] = new MColor(168, 85, 247),
        ["Provincie"] = new MColor(59, 130, 246),
        ["Gemeente"] = new MColor(148, 163, 184),
        ["Waterschap"] = new MColor(6, 182, 212),
        ["Overig"] = new MColor(234, 179, 8)
    };

    private readonly Mapsui.UI.Avalonia.MapControl _mapControl = new();
    private readonly Map _map = new();
    private readonly MemoryLayer _bridgeLayer = new() { Name = "Asset Monitor bridges", IsMapInfoLayer = true, Style = null };
    private readonly MemoryLayer _roadLayer = new() { Name = "Asset Monitor roads", IsMapInfoLayer = true, Style = null };

    private List<IFeature> _allBridges = new();
    private List<IFeature> _allRoads = new();
    private readonly Dictionary<string, CheckBox> _statusChecks = new();
    private readonly CheckBox _roadsCheck = new() { Content = "Show roads", Margin = new Thickness(0, 0, 12, 0) };
    private readonly ComboBox _roadType = new() { Width = 170, ItemsSource = new[] { "All managers", "Rijk", "Provincie", "Gemeente", "Waterschap", "Overig" }, SelectedIndex = 0 };
    private readonly TextBlock _status = Am.M("");
    private readonly Border _card;
    private readonly StackPanel _cardBody = new() { Spacing = 4 };
    private bool _loaded;
    private readonly MRect _province;

    public AmMapPanel()
    {
        _map.Layers.Add(OpenStreetMap.CreateTileLayer("OSM Base"));
        _map.Layers.Add(_roadLayer);
        _map.Layers.Add(_bridgeLayer);
        var (x1, y1) = SphericalMercator.FromLonLat(6.15, 52.84);
        var (x2, y2) = SphericalMercator.FromLonLat(7.25, 53.56);
        _province = new MRect(x1, y1, x2, y2);
        _map.Home = n => n.ZoomToBox(_province, MBoxFit.Fit);
        _mapControl.Map = _map;
        _map.Info += OnMapInfo;

        // ---- top bar: filters + legend
        var bar = new WrapPanel { Margin = new Thickness(12, 8) };
        foreach (var s in new[] { "Priority", "Review", "No unusual movement", "No satellite data" })
        {
            var c = StatusColor[s];
            var cb = new CheckBox
            {
                IsChecked = s is "Priority" or "Review",
                Margin = new Thickness(0, 0, 12, 0),
                Content = new TextBlock { Text = "● " + s, Foreground = new SolidColorBrush(Avalonia.Media.Color.FromRgb((byte)c.R, (byte)c.G, (byte)c.B)) }
            };
            cb.IsCheckedChanged += (_, _) => ApplyBridgeFilter();
            _statusChecks[s] = cb;
            bar.Children.Add(cb);
        }
        _roadsCheck.Dyn(CheckBox.ForegroundProperty, "AppTextPrimary");
        _roadsCheck.IsCheckedChanged += async (_, _) => await ToggleRoadsAsync();
        _roadType.SelectionChanged += (_, _) => ApplyRoadFilter();
        var home = Am.Btn("⤢ Whole province", (_, _) => { _map.Navigator.ZoomToBox(_province, MBoxFit.Fit); _map.Refresh(); });
        home.Margin = new Thickness(0, 0, 12, 0);
        bar.Children.Add(home);
        bar.Children.Add(_roadsCheck);
        bar.Children.Add(_roadType);
        var legend = Am.M("   Roads: purple = Rijk · blue = Provincie · grey = Gemeente · cyan = Waterschap · yellow = Overig. Click any dot or road for details.");
        legend.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(legend);

        // ---- info card (right side, over the map)
        _card = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12), Width = 380, MaxHeight = 620, CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12), BorderThickness = new Thickness(1), IsVisible = false,
            Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#ee131f2e")),
            BorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#3b82f6")),
            Child = new ScrollViewer { Content = _cardBody, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };

        var mapArea = new Grid();
        mapArea.Children.Add(_mapControl);
        mapArea.Children.Add(_card);
        var statusBox = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12), Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#cc131f2e")), Child = _status
        };
        mapArea.Children.Add(statusBox);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(bar);
        Grid.SetRow(mapArea, 1);
        root.Children.Add(mapArea);
        Content = root;

        AttachedToVisualTree += async (_, _) => { if (!_loaded) { _loaded = true; await LoadBridgesAsync(); } };
    }

    // =============================================================== loading
    private async Task LoadBridgesAsync()
    {
        var file = File.Exists(Path.Combine(ScreeningOut, "bridges_screening.geojson"))
            ? Path.Combine(ScreeningOut, "bridges_screening.geojson") : BridgesFile;
        if (!File.Exists(file)) { _status.Text = "No bridge data yet — run the Asset Register refresh first."; return; }
        _status.Text = "Loading bridges…";
        try
        {
            _allBridges = await Task.Run(() => ReadFeatures(file, asPoints: true, styleFor: f =>
            {
                var s = Attr(f, "screening") ?? "Not screened";
                var c = StatusColor.GetValueOrDefault(s, StatusColor["Not screened"]);
                return BridgeStyle(s, c);
            }));
            // draw Priority on top of everything else
            _allBridges = _allBridges.OrderBy(f => (Attr(f, "screening") ?? "") switch
                { "Priority" => 3, "Review" => 2, "No unusual movement" => 1, _ => 0 }).ToList();
            ApplyBridgeFilter();
            _status.Text = $"{_allBridges.Count:N0} bridge deck parts loaded from {Path.GetFileName(file)}";
        }
        catch (Exception ex) { _status.Text = $"Could not load bridges: {ex.Message}"; }
    }

    private async Task ToggleRoadsAsync()
    {
        if (_roadsCheck.IsChecked != true) { _roadLayer.Features = new List<IFeature>(); Refresh(_roadLayer); return; }
        if (_allRoads.Count == 0)
        {
            if (!File.Exists(RoadsFile)) { _status.Text = "No road data yet — run the Asset Register refresh first."; return; }
            _status.Text = "Loading roads (large file, a few seconds)…";
            try
            {
                _allRoads = await Task.Run(() => ReadFeatures(RoadsFile, asPoints: false, styleFor: f =>
                {
                    var t = (Attr(f, "owner_type") ?? "Overig").Split(' ')[0];
                    return RoadStyle(t);
                }));
                _status.Text = $"{_allRoads.Count:N0} road segments loaded";
            }
            catch (Exception ex) { _status.Text = $"Could not load roads: {ex.Message}"; return; }
        }
        ApplyRoadFilter();
    }

    /// <summary>GeoJSON (WGS84) -> Mapsui features in map projection, all register/screening attributes copied unchanged.</summary>
    private static List<IFeature> ReadFeatures(string path, bool asPoints, Func<IFeature, IStyle> styleFor)
    {
        var reader = new NetTopologySuite.IO.GeoJsonReader();
        var fc = reader.Read<NetTopologySuite.Features.FeatureCollection>(File.ReadAllText(path));
        var list = new List<IFeature>(fc.Count);
        foreach (var f in fc)
        {
            if (f.Geometry == null || f.Geometry.IsEmpty) continue;
            NtsGeometry g = asPoints ? f.Geometry.InteriorPoint : f.Geometry.Copy();
            var lonLat = f.Geometry.InteriorPoint;
            g.Apply(new ToMercator());
            g.GeometryChanged();
            var mf = new GeometryFeature { Geometry = g };
            if (f.Attributes != null)
                foreach (var name in f.Attributes.GetNames())
                    mf[name] = f.Attributes[name];
            mf["_lat"] = lonLat.Y;
            mf["_lon"] = lonLat.X;
            mf.Styles.Add(styleFor(mf));
            list.Add(mf);
        }
        return list;
    }

    private sealed class ToMercator : NetTopologySuite.Geometries.ICoordinateSequenceFilter
    {
        public bool Done => false;
        public bool GeometryChanged => true;
        public void Filter(NetTopologySuite.Geometries.CoordinateSequence seq, int i)
        {
            var (x, y) = SphericalMercator.FromLonLat(seq.GetX(i), seq.GetY(i));
            seq.SetX(i, x);
            seq.SetY(i, y);
        }
    }

    // one shared style object per category (14k features -> 5 style objects)
    private static readonly Dictionary<string, IStyle> BridgeStyles = new();
    private static IStyle BridgeStyle(string status, MColor c)
    {
        lock (BridgeStyles)
        {
            if (!BridgeStyles.TryGetValue(status, out var st))
                BridgeStyles[status] = st = new SymbolStyle
                {
                    Fill = new MBrush(c), Outline = new MPen(MColor.White, 1f),
                    SymbolScale = status is "Priority" ? 0.7 : status is "Review" ? 0.55 : 0.4
                };
            return st;
        }
    }

    private static readonly Dictionary<string, IStyle> RoadStyles = new();
    private static IStyle RoadStyle(string type)
    {
        lock (RoadStyles)
        {
            if (!RoadStyles.TryGetValue(type, out var st))
                RoadStyles[type] = st = new VectorStyle { Line = new MPen(RoadColor.GetValueOrDefault(type, RoadColor["Overig"]), 2f) };
            return st;
        }
    }

    // =============================================================== filters
    private void ApplyBridgeFilter()
    {
        var shown = _statusChecks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToHashSet();
        _bridgeLayer.Features = _allBridges.Where(f => shown.Contains(Attr(f, "screening") ?? "Not screened")
                                                     || (shown.Contains("No satellite data") && Attr(f, "screening") == null)).ToList();
        Refresh(_bridgeLayer);
        _status.Text = $"Showing {_bridgeLayer.Features.Count():N0} of {_allBridges.Count:N0} bridge deck parts";
    }

    private void ApplyRoadFilter()
    {
        if (_roadsCheck.IsChecked != true || _allRoads.Count == 0) return;
        var t = _roadType.SelectedItem as string ?? "All managers";
        _roadLayer.Features = t == "All managers"
            ? _allRoads
            : _allRoads.Where(f => (Attr(f, "owner_type") ?? "").StartsWith(t, StringComparison.OrdinalIgnoreCase)).ToList();
        Refresh(_roadLayer);
        _status.Text = $"Showing {_roadLayer.Features.Count():N0} road segments ({t})";
    }

    private void Refresh(MemoryLayer layer)
    {
        layer.DataHasChanged();
        _map.Refresh();
    }

    private static object? Raw(IFeature f, string key) => f.Fields.Contains(key) ? f[key] : null;

    /// <summary>Attribute as text; numbers always with a dot, whatever the computer's language setting.</summary>
    private static string? Attr(IFeature f, string key)
    {
        var v = Raw(f, key);
        var s = v is IFormattable fm ? fm.ToString(null, CultureInfo.InvariantCulture) : v?.ToString();
        return string.IsNullOrWhiteSpace(s) || s == "NaN" ? null : s;
    }

    private static double? Num(IFeature f, string key)
    {
        var v = Raw(f, key);
        if (v is IConvertible c and not string)
        {
            try { var d = c.ToDouble(CultureInfo.InvariantCulture); return double.IsNaN(d) ? null : d; } catch { return null; }
        }
        return double.TryParse(v?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && !double.IsNaN(p) ? p : null;
    }

    // =============================================================== click -> info card
    private void OnMapInfo(object? sender, MapInfoEventArgs e)
    {
        var f = e.MapInfo?.Feature;
        if (f == null || (e.MapInfo?.Layer != _bridgeLayer && e.MapInfo?.Layer != _roadLayer)) return;
        bool isBridge = e.MapInfo!.Layer == _bridgeLayer;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowCard(f, isBridge));
    }

    private void ShowCard(IFeature f, bool isBridge)
    {
        _cardBody.Children.Clear();
        TextBlock T(string text, double size = 12, string color = "#e2e8f0", bool bold = false) => new()
        {
            Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse(color)),
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal
        };
        void Section(string title) => _cardBody.Children.Add(T(title, 11, "#3b82f6", true));

        var ownerType = Attr(f, "owner_type") ?? "Unknown";
        var ownerName = Attr(f, "owner_name") ?? "";
        var lat = Num(f, "_lat"); var lon = Num(f, "_lon");
        var coords = lat.HasValue && lon.HasValue
            ? $"{lat.Value.ToString("0.00000", CultureInfo.InvariantCulture)}, {lon.Value.ToString("0.00000", CultureInfo.InvariantCulture)}" : "—";

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(T(isBridge ? "🌉 Bridge (deck part)" : "🛣 Road segment", 14, "#ffffff", true));
        var close = new Button { Content = "✕", Padding = new Thickness(6, 0), FontSize = 12 };
        close.Click += (_, _) => _card.IsVisible = false;
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        _cardBody.Children.Add(header);

        // ---- manager (register)
        Section("REGISTERED MANAGER");
        _cardBody.Children.Add(T(OwnerLabel(ownerType, ownerName), 14, "#ffffff", true));
        _cardBody.Children.Add(T($"Type: {ownerType}"));
        if (Attr(f, "owner_code") is string code) _cardBody.Children.Add(T($"Register code: {code}"));
        if (Attr(f, "owner_source") is string src) _cardBody.Children.Add(T($"Source: {src}", 11, "#94a3b8"));
        if (Attr(f, "code_status") is string cs && cs != "current")
            _cardBody.Children.Add(T($"⚠ {cs} — the register entry needs updating by its keeper.", 11, "#f59e0b"));

        // ---- satellite
        string? assetId = Attr(f, "asset_id");
        if (isBridge)
        {
            Section("SATELLITE SCREENING");
            var scr = Attr(f, "screening");
            if (scr == null) _cardBody.Children.Add(T("Not screened yet — run the satellite screening.", 12, "#94a3b8"));
            else
            {
                var c = StatusColor.GetValueOrDefault(scr, StatusColor["Not screened"]);
                _cardBody.Children.Add(new TextBlock { Text = "● " + scr, FontSize = 14, FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Avalonia.Media.Color.FromRgb((byte)c.R, (byte)c.G, (byte)c.B)) });
                string F(string k) => Num(f, k) is double d ? d.ToString("0.0", CultureInfo.InvariantCulture) : "—";
                _cardBody.Children.Add(T($"Vertical at bridge: {F("up_local")} mm/yr   ·   surroundings: {F("up_ground")} mm/yr"));
                _cardBody.Children.Add(T($"Vertical difference: {F("up_diff")} mm/yr   ·   sideways difference: {F("east_diff")} mm/yr"));
                _cardBody.Children.Add(T($"Satellite points at bridge / surroundings: {F("up_local_n").Replace(".0", "")} / {F("up_ground_n").Replace(".0", "")}"));
                _cardBody.Children.Add(T($"EGMS release {Attr(f, "egms_release") ?? "?"} · screened {Attr(f, "screened_utc") ?? "?"}", 11, "#94a3b8"));
                _cardBody.Children.Add(T("Negative vertical = sinking. Few points = weaker evidence.", 11, "#94a3b8"));
            }

            // ---- inspection
            Section("INSPECTION");
            var task = assetId == null ? null : LoadTasks().FirstOrDefault(t => t.AssetId == assetId || t.PartIds.Contains(assetId));
            if (task != null)
            {
                _cardBody.Children.Add(T($"Task status: {task.Status}"));
                _cardBody.Children.Add(T(task.Nen2767Score is int sc ? $"NEN 2767 condition score: {sc}" : "NEN 2767 condition score: not scored yet"));
                if (!string.IsNullOrWhiteSpace(task.Notes)) _cardBody.Children.Add(T($"Findings: {task.Notes}", 11));
            }
            else if (assetId != null)
            {
                _cardBody.Children.Add(T("No inspection task for this bridge.", 12, "#94a3b8"));
                var add = Am.Btn("➕ Create inspection task", (_, _) =>
                {
                    var tasks = LoadTasks();
                    tasks.Add(new InspectionTask
                    {
                        AssetId = assetId, OwnerType = ownerType, OwnerName = ownerName,
                        Screening = Attr(f, "screening") ?? "Not screened",
                        MaxAbsMmPerYear = new[] { Num(f, "up_diff"), Num(f, "east_diff") }.Where(v => v.HasValue).Select(v => Math.Abs(v!.Value)).DefaultIfEmpty().Max(),
                        Lat = lat, Lon = lon, EgmsRelease = Attr(f, "egms_release") ?? "",
                        PartIds = new List<string> { assetId },
                        CreatedUtc = DateTime.UtcNow.ToString("o")
                    });
                    SaveTasks(tasks);
                    ShowCard(f, isBridge);
                });
                _cardBody.Children.Add(add);
            }
        }

        // ---- location
        Section("LOCATION");
        var copy = Am.Btn("📋 " + coords, async (_, _) =>
        {
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb != null && coords != "—") { await cb.SetTextAsync(coords); _status.Text = $"Copied {coords} — paste into Mission Planner"; }
        });
        _cardBody.Children.Add(copy);
        if (assetId != null) _cardBody.Children.Add(T($"Register ID: {assetId}", 11, "#94a3b8"));

        // ---- every other register field, unchanged
        Section("ALL REGISTER FIELDS");
        var skip = new HashSet<string> { "owner_type", "owner_name", "owner_code", "owner_source", "code_status", "screening",
            "up_local", "up_ground", "up_diff", "east_diff", "up_local_n", "up_ground_n", "east_local", "east_ground",
            "east_local_n", "east_ground_n", "egms_release", "screened_utc", "asset_id", "_lat", "_lon", "lat", "lon" };
        foreach (var field in f.Fields.Where(k => !skip.Contains(k)).OrderBy(k => k))
        {
            var v = Attr(f, field);
            if (v != null) _cardBody.Children.Add(T($"{field}: {v}", 11, "#cbd5e1"));
        }

        _card.IsVisible = true;
    }
}
