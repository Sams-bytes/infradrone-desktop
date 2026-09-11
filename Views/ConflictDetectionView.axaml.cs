using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using InfraDroneDesktop.Services;
namespace InfraDroneDesktop.Views;

// Lightweight display wrapper -- keeps ConflictEvent (the JSON/service model)
// free of UI concerns, computed once per filter application rather than via
// XAML value converters.
public class ConflictEventRow
{
    public ConflictEvent Source { get; set; } = null!;
    public string Line1 { get; set; } = "";
    public string Line2 { get; set; } = "";
    public string BadgeText { get; set; } = "";
    public IBrush BadgeBg { get; set; } = Brushes.Transparent;
    public IBrush BadgeFg { get; set; } = Brushes.White;
    public IBrush BorderBrush { get; set; } = Brushes.Transparent;
}

public partial class ConflictDetectionView : UserControl
{
    private ConflictDataset? _dataset;

    // Raised when the user clicks Play on a conflict -- TrafficIntelligenceView
    // subscribes to this and switches to the Traffic Replay sub-tab. Precise
    // seek-to-timestamp inside the player is a follow-up (TrafficPlayerView
    // does not yet expose a public seek method).
    public event Action<ConflictEvent>? PlayRequested;

    public ConflictDetectionView()
    {
        InitializeComponent();
        _dataset = ConflictDetectionService.Load();
        PopulateRoundaboutFilter();
        SeverityFilter.SelectedIndex = 0;
        ApplyFilters();
    }

    private void PopulateRoundaboutFilter()
    {
        RoundaboutFilter.Items.Clear();
        RoundaboutFilter.Items.Add(new ComboBoxItem { Content = "All roundabouts" });
        if (_dataset != null)
        {
            var roundabouts = _dataset.Events.Select(e => e.Roundabout).Distinct().OrderBy(r => r);
            foreach (var r in roundabouts)
                RoundaboutFilter.Items.Add(new ComboBoxItem { Content = r });
        }
        RoundaboutFilter.SelectedIndex = 0;
    }

    private void OnFilterChanged(object? sender, SelectionChangedEventArgs e) => ApplyFilters();

    private void ApplyFilters()
    {
        if (_dataset == null)
        {
            TxtTotal.Text = "0";
            TxtHigh.Text = "0";
            TxtAvgTtc.Text = "--";
            TxtRecordings.Text = "0";
            EventsList.ItemsSource = null;
            return;
        }

        IEnumerable<ConflictEvent> filtered = _dataset.Events;

        var roundaboutSel = (RoundaboutFilter.SelectedItem as ComboBoxItem)?.Content as string;
        if (!string.IsNullOrEmpty(roundaboutSel) && roundaboutSel != "All roundabouts")
            filtered = filtered.Where(ev => ev.Roundabout == roundaboutSel);

        var severitySel = (SeverityFilter.SelectedItem as ComboBoxItem)?.Content as string;
        if (severitySel == "High only")
            filtered = filtered.Where(ev => ev.Severity == "high");
        else if (severitySel == "Medium only")
            filtered = filtered.Where(ev => ev.Severity == "medium");

        var filteredList = filtered.OrderBy(ev => ev.MinTtcS).ToList();

        TxtTotal.Text = filteredList.Count.ToString();
        TxtHigh.Text = filteredList.Count(ev => ev.Severity == "high").ToString();
        TxtAvgTtc.Text = filteredList.Count > 0 ? filteredList.Average(ev => ev.MinTtcS).ToString("0.00") + "s" : "--";
        TxtRecordings.Text = _dataset.RecordingsScanned.ToString();

        var highBg = new SolidColorBrush(Color.Parse("#3d1f14"));
        var highFg = new SolidColorBrush(Color.Parse("#e2854a"));
        var mediumBg = new SolidColorBrush(Color.Parse("#3d3410"));
        var mediumFg = new SolidColorBrush(Color.Parse("#eab308"));

        var rows = filteredList.Select(ev => new ConflictEventRow
        {
            Source = ev,
            Line1 = $"Vehicle {ev.ObjIdA} vs {ev.ObjIdB} \u00b7 {ev.Roundabout} / {ev.Table}",
            Line2 = $"TTC {ev.MinTtcS:0.00}s \u00b7 dist {ev.MinDistanceM:0.0}m \u00b7 t={ev.TStart:0.0}s",
            BadgeText = ev.Severity == "high" ? "HIGH" : "MEDIUM",
            BadgeBg = ev.Severity == "high" ? highBg : mediumBg,
            BadgeFg = ev.Severity == "high" ? highFg : mediumFg,
            BorderBrush = ev.Severity == "high" ? highFg : mediumFg,
        }).ToList();

        EventsList.ItemsSource = rows;
    }

    private void OnPlayClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ConflictEventRow row)
            PlayRequested?.Invoke(row.Source);
    }
}
