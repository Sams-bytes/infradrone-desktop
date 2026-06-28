using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using InfraDroneDesktop.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace InfraDroneDesktop.Views;

public partial class NotamView : UserControl
{
    private NotamService? _notamService;
    private string _filter = "all";

    public NotamView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _notamService = new NotamService();
        _notamService.NotamsUpdated += RefreshList;
        Task.Run(() => _notamService.FetchAsync());
    }

    private void RefreshList()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_notamService == null) return;

            StatusText.Text = _notamService.Status;
            var notams = _notamService.Notams;

            var filtered = _filter switch
            {
                "ehgg" => notams.Where(n => n.Icao == "EHGG").ToList(),
                "ehaa" => notams.Where(n => n.Icao == "EHAA").ToList(),
                "active" => notams.Where(n => n.IsActive).ToList(),
                _ => notams
            };

            ActiveCount.Text = notams.Count(n => n.IsActive).ToString();
            TotalCount.Text = notams.Count.ToString();

            NotamList.Children.Clear();
            foreach (var notam in filtered)
            {
                NotamList.Children.Add(CreateNotamCard(notam));
            }
        });
    }

    private Border CreateNotamCard(NotamEntry notam)
    {
        var fillColor = notam.IsActive ? "#3d1a1a" : "#1a2637";
        var borderColor = notam.IsActive ? "#ef4444" : "#2d3f52";
        var statusText = notam.IsActive ? "ACTIVE" : "EXPIRED";
        var statusColor = notam.IsActive ? "#ef4444" : "#4a5568";

        return new Border
        {
            Background = SolidColorBrush.Parse(fillColor),
            BorderBrush = SolidColorBrush.Parse(borderColor),
            BorderThickness = new Avalonia.Thickness(0, 0, 0, 0.5),
            Padding = new Avalonia.Thickness(16, 10),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Children =
                {
                    new StackPanel
                    {
                        Width = 80,
                        Spacing = 4,
                        Children =
                        {
                            new TextBlock { Text = notam.Icao, FontSize = 13, FontWeight = FontWeight.Bold,
                                Foreground = SolidColorBrush.Parse("#e2e8f0") },
                            new Border
                            {
                                Background = SolidColorBrush.Parse(notam.IsActive ? "#4d1a1a" : "#1e2d3d"),
                                CornerRadius = new Avalonia.CornerRadius(3),
                                Padding = new Avalonia.Thickness(4,2),
                                Child = new TextBlock { Text = statusText, FontSize = 9,
                                    FontWeight = FontWeight.Bold,
                                    Foreground = SolidColorBrush.Parse(statusColor) }
                            }
                        }
                    },
                    new StackPanel
                    {
                        [Grid.ColumnProperty] = 1,
                        Margin = new Avalonia.Thickness(12, 0),
                        Spacing = 4,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = notam.Text.Length > 200 ? notam.Text[..200] + "..." : notam.Text,
                                FontSize = 11, Foreground = SolidColorBrush.Parse("#94a3b8"),
                                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                                FontFamily = new FontFamily("Consolas,Monospace")
                            },
                            new TextBlock
                            {
                                Text = $"Valid: {notam.Start} → {notam.End}  |  {notam.Classification}",
                                FontSize = 10, Foreground = SolidColorBrush.Parse("#4a5568")
                            }
                        }
                    },
                    new TextBlock
                    {
                        [Grid.ColumnProperty] = 2,
                        Text = "",
                        FontSize = 11, Foreground = SolidColorBrush.Parse("#64748b"),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    }
                }
            }
        };
    }

    private void OnRefresh(object? s, RoutedEventArgs e) =>
        Task.Run(() => _notamService?.FetchAsync());

    private void OnFilterAll(object? s, RoutedEventArgs e) { _filter = "all"; RefreshList(); }
    private void OnFilterEHGG(object? s, RoutedEventArgs e) { _filter = "ehgg"; RefreshList(); }
    private void OnFilterEHAA(object? s, RoutedEventArgs e) { _filter = "ehaa"; RefreshList(); }
    private void OnFilterActive(object? s, RoutedEventArgs e) { _filter = "active"; RefreshList(); }
}
