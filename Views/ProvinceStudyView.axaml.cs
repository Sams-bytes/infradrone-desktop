using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using InfraDroneDesktop.Services;

namespace InfraDroneDesktop.Views
{
    /// <summary>
    /// 'Province Study' sub-tab of Traffic Intelligence: browses the Province of Groningen traffic-study results
    /// listed in ~/DAMbv_Data/09_Province_Traffic_Study/manifest.json (written by collect_study_assets.py).
    /// Pictures and tables show in the app; videos and the HTML report open in the system's default program.
    /// </summary>
    public partial class ProvinceStudyView : UserControl
    {
        private const string AllSections = "All sections";
        private List<StudyItem> _all = new();
        private StudyItem? _current;
        private Bitmap? _bitmap;

        public ProvinceStudyView()
        {
            InitializeComponent();
            LoadManifest();
        }

        private void LoadManifest()
        {
            var m = StudyManifestService.Load(out var error);
            if (m == null)
            {
                HeaderText.Text = "Province study";
                GeneratedText.Text = "";
                ItemList.ItemsSource = null;
                ShowEmpty("No study data found",
                    error + "\nRun collect_study_assets.py in ~/traffic-behaviour-study, then press Reload.");
                return;
            }
            HeaderText.Text = m.Title;
            GeneratedText.Text = $"Updated {m.Generated}";
            _all = m.Sections.SelectMany(s => s.Items.Select(i => { i.Section = s.Name; return i; })).ToList();
            var filters = new List<string> { AllSections };
            filters.AddRange(m.Sections.Where(s => s.Items.Count > 0).Select(s => s.Name));
            SectionFilter.ItemsSource = filters;
            SectionFilter.SelectedIndex = 0;   // triggers ApplyFilter via SelectionChanged
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var f = SectionFilter.SelectedItem as string;
            var items = (f == null || f == AllSections) ? _all : _all.Where(i => i.Section == f).ToList();
            ItemList.ItemsSource = items;
            if (items.Count > 0)
                ItemList.SelectedIndex = 0;
            else
                ShowEmpty("Nothing in this section yet", "Run collect_study_assets.py again after new results.");
        }

        private void ShowEmpty(string title, string text)
        {
            _current = null;
            DTitle.Text = title;
            DDesc.Text = text;
            DTagBorder.IsVisible = false;
            OpenBtn.IsVisible = FolderBtn.IsVisible = false;
            SetImage(null);
            TablePanel.Children.Clear();
            DSource.Text = "";
        }

        private void ShowItem(StudyItem it)
        {
            _current = it;
            DTitle.Text = it.Title;
            DDesc.Text = it.Exists ? it.Description : it.Description + "\n\nFile not found: " + it.FilePath;
            DTag.Text = it.TagLabel;
            DTagBorder.Background = it.TagBrush;
            DTagBorder.IsVisible = true;
            OpenBtn.IsVisible = FolderBtn.IsVisible = it.Exists;
            OpenBtn.Content = it.Kind switch
            {
                "video" => "Play video",
                "html" => "Open report in browser",
                "csv" => "Open table",
                _ => "Open full size",
            };
            SetImage(it.Kind == "image" ? it.FilePath : it.Thumb);
            BuildTable(it.Table);
            DSource.Text = string.IsNullOrEmpty(it.Source) ? "" : "Source: " + it.Source;
        }

        private void SetImage(string? path)
        {
            var old = _bitmap;
            _bitmap = null;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try { _bitmap = new Bitmap(path); }
                catch (Exception) { _bitmap = null; }
            }
            DImage.Source = _bitmap;
            DImage.IsVisible = _bitmap != null;
            old?.Dispose();
        }

        private void BuildTable(List<List<string>>? rows)
        {
            TablePanel.Children.Clear();
            if (rows == null || rows.Count == 0) return;
            var g = new Grid();
            int cols = rows.Max(r => r.Count);
            for (int c = 0; c < cols; c++) g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            for (int r = 0; r < Math.Min(rows.Count, 40); r++)
            {
                g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (int c = 0; c < rows[r].Count; c++)
                {
                    var tb = new TextBlock
                    {
                        Text = rows[r][c],
                        Margin = new Thickness(0, 3, 22, 3),
                        FontWeight = r == 0 ? FontWeight.Bold : FontWeight.Normal,
                    };
                    Grid.SetRow(tb, r);
                    Grid.SetColumn(tb, c);
                    g.Children.Add(tb);
                }
            }
            TablePanel.Children.Add(g);
        }

        private void OnFilterChanged(object? sender, SelectionChangedEventArgs e) => ApplyFilter();

        private void OnItemSelected(object? sender, SelectionChangedEventArgs e)
        {
            if (ItemList.SelectedItem is StudyItem it) ShowItem(it);
        }

        private void OnReload(object? sender, RoutedEventArgs e) => LoadManifest();

        private void OnOpen(object? sender, RoutedEventArgs e)
        {
            if (_current is { Exists: true }) StudyManifestService.OpenExternal(_current.FilePath);
        }

        private void OnShowInFolder(object? sender, RoutedEventArgs e)
        {
            if (_current is { Exists: true })
            {
                var dir = Path.GetDirectoryName(_current.FilePath);
                if (dir != null) StudyManifestService.OpenExternal(dir);
            }
        }

        private void OnOpenFolder(object? sender, RoutedEventArgs e)
        {
            if (Directory.Exists(StudyManifestService.StudyFolder))
                StudyManifestService.OpenExternal(StudyManifestService.StudyFolder);
        }
    }
}
