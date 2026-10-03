using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using InfraDroneDesktop.Services;

namespace InfraDroneDesktop.Views
{
    /// <summary>
    /// 'Province Study' sub-tab of Traffic Intelligence. One tab per part of the study (from manifest.json,
    /// written by collect_study_assets.py). The site-selection tab is native, with a sub-tab per question.
    /// </summary>
    public partial class ProvinceStudyView : UserControl
    {
        public ProvinceStudyView()
        {
            InitializeComponent();
            LoadManifest();
        }

        private void LoadManifest()
        {
            int keep = Tabs.SelectedIndex;
            Tabs.Items.Clear();
            var m = StudyManifestService.Load(out var error);
            if (m == null)
            {
                HeaderText.Text = "Province study";
                GeneratedText.Text = "";
                Tabs.Items.Add(new TabItem
                {
                    Header = "No data",
                    Content = UI.Text(error + "\nRun collect_study_assets.py in ~/traffic-behaviour-study, then press Reload.",
                                      14, margin: new Thickness(16)),
                });
                return;
            }
            HeaderText.Text = m.Title;
            GeneratedText.Text = $"Updated {m.Generated}";
            foreach (var s in m.Sections)
            {
                if (s.Items.Count == 0) continue;
                Control content = s.Id == "site" ? new SiteSelectionPanel(s) : new StudySectionPanel(s);
                Tabs.Items.Add(new TabItem
                {
                    Header = string.IsNullOrEmpty(s.Short) ? s.Name : s.Short,
                    FontSize = 15,
                    Content = content,
                });
            }
            Tabs.SelectedIndex = keep >= 0 && keep < Tabs.ItemCount ? keep : 0;
        }

        private void OnReload(object? sender, RoutedEventArgs e) => LoadManifest();

        private void OnOpenFolder(object? sender, RoutedEventArgs e)
        {
            if (Directory.Exists(StudyManifestService.StudyFolder))
                StudyManifestService.OpenExternal(StudyManifestService.StudyFolder);
        }
    }

    /// <summary>Small helpers to build controls in code.</summary>
    internal static class UI
    {
        public static TextBlock Text(string text, double size = 14, bool bold = false, double opacity = 1,
                                     Thickness? margin = null, IBrush? color = null)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = size,
                FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
                Opacity = opacity,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 900,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = margin ?? new Thickness(0),
            };
            if (color != null) tb.Foreground = color;   // never set null: that would make the text invisible
            return tb;
        }

        public static Border Tag(string text, IBrush bg) => new()
        {
            Background = bg,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = text, FontWeight = FontWeight.Bold, Foreground = Brushes.White },
        };

        public static Button Button(string text, Action onClick)
        {
            var b = new Button { Content = text };
            b.Click += (_, _) => onClick();
            return b;
        }

        /// <summary>"How to read this" box: section name, Dutch explanation, short English line.</summary>
        public static Control IntroBox(StudySection s)
        {
            var sp = new StackPanel { Spacing = 4 };
            sp.Children.Add(Text(s.Name, 15, bold: true));
            if (!string.IsNullOrWhiteSpace(s.IntroNl)) sp.Children.Add(Text(s.IntroNl, 14));
            if (!string.IsNullOrWhiteSpace(s.IntroEn)) sp.Children.Add(Text("EN: " + s.IntroEn, 12, opacity: 0.7));
            return new Border
            {
                Child = sp,
                Padding = new Thickness(12, 8),
                Margin = new Thickness(6, 8, 6, 4),
                BorderThickness = new Thickness(4, 0, 0, 0),
                BorderBrush = Brush.Parse("#1D4E89"),
                Background = new SolidColorBrush(Color.Parse("#1D4E89"), 0.12),
            };
        }

        public static Grid Table(List<List<string>> rows, int maxRows = 40)
        {
            var g = new Grid();
            int cols = rows.Max(r => r.Count);
            for (int c = 0; c < cols; c++) g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            for (int r = 0; r < Math.Min(rows.Count, maxRows); r++)
            {
                g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (int c = 0; c < rows[r].Count; c++)
                {
                    var tb = new TextBlock
                    {
                        Text = rows[r][c],
                        Margin = new Thickness(0, 3, 22, 3),
                        FontWeight = r == 0 ? FontWeight.Bold : FontWeight.Normal,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 520,
                    };
                    Grid.SetRow(tb, r);
                    Grid.SetColumn(tb, c);
                    g.Children.Add(tb);
                }
            }
            return g;
        }
    }

    /// <summary>A study section: list of results on the left, details on the right.</summary>
    internal class StudySectionPanel : UserControl
    {
        private readonly StackPanel _detail = new() { Spacing = 10, Margin = new Thickness(4) };
        private Bitmap? _bitmap;

        public StudySectionPanel(StudySection s)
        {
            var list = new ListBox
            {
                ItemsSource = s.Items,
                Margin = new Thickness(4, 8, 6, 4),
                ItemTemplate = new FuncDataTemplate<StudyItem>((it, _) =>
                {
                    var sp = new StackPanel { Margin = new Thickness(2, 4), Spacing = 1 };
                    if (it == null) return sp;
                    sp.Children.Add(new TextBlock { Text = it.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
                    sp.Children.Add(new TextBlock { Text = it.KindLabel, FontSize = 11, Foreground = it.TagBrush });
                    return sp;
                }),
            };
            list.SelectionChanged += (_, _) => { if (list.SelectedItem is StudyItem it) Show(it); };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("330,*") };
            grid.Children.Add(list);
            var right = new ScrollViewer { Content = _detail, Margin = new Thickness(6, 8, 4, 4) };
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            var dock = new DockPanel();
            var box = UI.IntroBox(s);
            DockPanel.SetDock(box, Dock.Top);
            dock.Children.Add(box);
            dock.Children.Add(grid);
            Content = dock;
            if (s.Items.Count > 0) list.SelectedIndex = 0;
        }

        private void Show(StudyItem it)
        {
            _detail.Children.Clear();
            _detail.Children.Add(UI.Text(it.Title, 22, bold: true));
            _detail.Children.Add(UI.Tag(it.TagLabel, it.TagBrush));
            _detail.Children.Add(UI.Text(it.Exists ? it.Description : it.Description + "\n\nFile not found: " + it.FilePath));
            if (it.Exists)
            {
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                string label = it.Kind switch
                {
                    "video" => "Play video",
                    "html" => "Open in browser",
                    "csv" => "Open table",
                    _ => "Open full size",
                };
                buttons.Children.Add(UI.Button(label, () => StudyManifestService.OpenExternal(it.FilePath)));
                buttons.Children.Add(UI.Button("Show in folder", () =>
                {
                    var dir = Path.GetDirectoryName(it.FilePath);
                    if (dir != null) StudyManifestService.OpenExternal(dir);
                }));
                _detail.Children.Add(buttons);
            }
            SetImage(it.Kind == "image" ? it.FilePath : it.Thumb);
            if (it.Table is { Count: > 0 }) _detail.Children.Add(UI.Table(it.Table));
            if (!string.IsNullOrEmpty(it.Source)) _detail.Children.Add(UI.Text("Source: " + it.Source, 12, opacity: 0.7));
        }

        private void SetImage(string? path)
        {
            var old = _bitmap;
            _bitmap = null;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try { _bitmap = new Bitmap(path); } catch (Exception) { _bitmap = null; }
            }
            if (_bitmap != null)
                _detail.Children.Add(new Image { Source = _bitmap, Stretch = Stretch.Uniform, MaxHeight = 760,
                                                 HorizontalAlignment = HorizontalAlignment.Left });
            old?.Dispose();
        }
    }

    /// <summary>Native site selection: one sub-tab per question with ranked candidates from candidates.csv.</summary>
    internal class SiteSelectionPanel : UserControl
    {
        private static readonly (string Q, string Tab, string Rule)[] Questions =
        {
            ("Q1", "1 Rijcurves (roundabouts)",
             "Roundabouts: crashes within 60 m, plus extra weight for small roundabouts (< 40 m across) and main roads."),
            ("Q2", "2 Inhaalverbod (overtaking ban)",
             "Overtaking-ban signs F1/F3: crashes within about 500 m of the signs; head-on crashes count three times."),
            ("Q3", "3 Bermverharding (shoulder paving)",
             "Shoulder paving: verge-condition records within 50 m and crashes within 100 m; single-vehicle crashes count double."),
        };

        public SiteSelectionPanel(StudySection s)
        {
            var map = s.Items.FirstOrDefault(i => i.Kind == "html");
            var csv = s.Items.FirstOrDefault(i => i.Kind == "csv");
            var cands = csv != null ? StudyManifestService.LoadCandidates(csv.FilePath) : new List<SiteCandidate>();

            var top = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 6, 6) };
            top.Children.Add(UI.IntroBox(s));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(6, 0, 0, 0) };
            if (map is { Exists: true })
                actions.Children.Add(UI.Button("Open interactive map", () => StudyManifestService.OpenExternal(map.FilePath)));
            if (csv is { Exists: true })
                actions.Children.Add(UI.Button("Open folder (GIS layers, CSV)", () =>
                {
                    var dir = Path.GetDirectoryName(csv.FilePath);
                    if (dir != null) StudyManifestService.OpenExternal(dir);
                }));
            top.Children.Add(actions);

            var sub = new TabControl { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var (q, tab, rule) in Questions)
            {
                var rows = cands.Where(c => c.Question == q).OrderBy(c => c.Rank).ToList();
                sub.Items.Add(new TabItem
                {
                    Header = tab,
                    FontSize = 14,
                    Content = rows.Count > 0 ? QuestionPanel(q, rows, rule)
                                             : UI.Text("No candidates for this question yet. Run site_selection.py, "
                                                       + "then collect_study_assets.py, then Reload.", 14,
                                                       margin: new Thickness(12)),
                });
            }

            var dock = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            dock.Children.Add(top);
            dock.Children.Add(sub);
            Content = dock;
        }

        private Control QuestionPanel(string q, List<SiteCandidate> rows, string rule)
        {
            var detail = new StackPanel { Spacing = 10, Margin = new Thickness(4) };
            var color = StudyColors.ForQuestion(q);
            var list = new ListBox
            {
                ItemsSource = rows,
                Margin = new Thickness(4, 8, 6, 4),
                ItemTemplate = new FuncDataTemplate<SiteCandidate>((c, _) =>
                {
                    var sp = new StackPanel { Margin = new Thickness(2, 4), Spacing = 1 };
                    if (c == null) return sp;
                    sp.Children.Add(new TextBlock { Text = c.ListLine, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
                    sp.Children.Add(new TextBlock { Text = c.ScoreLine, FontSize = 11, Foreground = color });
                    return sp;
                }),
            };
            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedItem is not SiteCandidate c) return;
                detail.Children.Clear();
                detail.Children.Add(UI.Text($"{c.Rank}. {c.Label}", 22, bold: true));
                detail.Children.Add(UI.Tag($"Score {c.Score}", color));
                detail.Children.Add(UI.Text(c.Reasons, 15));
                detail.Children.Add(UI.Text("Coordinates (latitude, longitude): " + c.Coordinates, 14, opacity: 0.85));
                var b = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                string lat = c.Lat.ToString(CultureInfo.InvariantCulture), lon = c.Lon.ToString(CultureInfo.InvariantCulture);
                b.Children.Add(UI.Button("Show on map (OpenStreetMap)", () => StudyManifestService.OpenExternal(
                    $"https://www.openstreetmap.org/?mlat={lat}&mlon={lon}#map=17/{lat}/{lon}")));
                b.Children.Add(UI.Button("Satellite view", () => StudyManifestService.OpenExternal(
                    $"https://www.google.com/maps/@{lat},{lon},250m/data=!3m1!1e3")));
                b.Children.Add(UI.Button("Copy coordinates", async () =>
                {
                    var clip = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (clip != null) await clip.SetTextAsync(c.Coordinates);
                }));
                detail.Children.Add(b);
                detail.Children.Add(UI.Text("How this list is ranked: " + rule, 12, opacity: 0.7));
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("330,*") };
            grid.Children.Add(list);
            var right = new ScrollViewer { Content = detail, Margin = new Thickness(6, 8, 4, 4) };
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            list.SelectedIndex = 0;
            return grid;
        }
    }
}
