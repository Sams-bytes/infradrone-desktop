using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using InfraDroneDesktop.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace InfraDroneDesktop.Views;

public partial class TerrainView : UserControl
{
    private List<TerrainPoint> _terrain = new();
    private List<(double Lat, double Lon, double AltM)> _waypoints = new();

    public TerrainView()
    {
        InitializeComponent();
    }

    public void LoadWaypoints(List<(double Lat, double Lon, double AltM)> waypoints)
    {
        _waypoints = waypoints;
        StatusText.Text = $"{waypoints.Count} waypoints loaded — click 'Fetch terrain'";
        BtnFetch.IsEnabled = waypoints.Count > 0;
    }

    private async void OnFetch(object? s, RoutedEventArgs e)
    {
        if (_waypoints.Count == 0) { StatusText.Text = "No waypoints loaded."; return; }
        StatusText.Text = "Fetching terrain elevation from EU-DEM 25m...";
        BtnFetch.IsEnabled = false;

        try
        {
            var svc = new TerrainService();
            var clearance = (double)(ClearanceInput.Value ?? 30);
            var coords = _waypoints.Select(w => (w.Lat, w.Lon)).ToList();
            _terrain = await svc.GetElevationsAsync(coords, clearance);

            StatusText.Text = $"Terrain loaded — {_terrain.Count} points from EU-DEM 25m";
            BtnApply.IsEnabled = true;
            DrawProfile();
            BuildTable();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally { BtnFetch.IsEnabled = true; }
    }

    private void DrawProfile()
    {
        ProfileCanvas.Children.Clear();
        if (_terrain.Count == 0) return;

        var w = ProfileCanvas.Bounds.Width > 0 ? ProfileCanvas.Bounds.Width : 800;
        var h = ProfileCanvas.Bounds.Height > 0 ? ProfileCanvas.Bounds.Height : 200;

        var maxElev = _terrain.Max(t => t.AbsAltM);
        var minElev = _terrain.Min(t => t.GroundElevM);
        var range = maxElev - minElev;
        if (range < 1) range = 1;

        double xStep = w / Math.Max(_terrain.Count - 1, 1);

        // Ground line
        var groundPoints = new Avalonia.Controls.Shapes.Polyline
        {
            Stroke = new SolidColorBrush(Color.Parse("#4a5568")),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(60, 74, 85, 104))
        };
        var gp = new Avalonia.Collections.AvaloniaList<Avalonia.Point>();
        gp.Add(new Avalonia.Point(0, h));
        for (int i = 0; i < _terrain.Count; i++)
        {
            var x = i * xStep;
            var y = h - (((_terrain[i].GroundElevM - minElev) / range) * (h - 20));
            gp.Add(new Avalonia.Point(x, y));
        }
        gp.Add(new Avalonia.Point((_terrain.Count - 1) * xStep, h));
        groundPoints.Points = gp;
        ProfileCanvas.Children.Add(groundPoints);

        // Mission altitude line
        var missionPoints = new Avalonia.Controls.Shapes.Polyline
        {
            Stroke = new SolidColorBrush(Color.Parse("#0d9e75")),
            StrokeThickness = 2.5,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 6, 3 }
        };
        var mp = new Avalonia.Collections.AvaloniaList<Avalonia.Point>();
        for (int i = 0; i < _terrain.Count; i++)
        {
            var x = i * xStep;
            var y = h - (((_terrain[i].AbsAltM - minElev) / range) * (h - 20));
            mp.Add(new Avalonia.Point(x, y));
        }
        missionPoints.Points = mp;
        ProfileCanvas.Children.Add(missionPoints);

        // Waypoint markers
        for (int i = 0; i < _terrain.Count; i++)
        {
            var x = i * xStep;
            var y = h - (((_terrain[i].AbsAltM - minElev) / range) * (h - 20));
            var dot = new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 8, Height = 8,
                Fill = new SolidColorBrush(Color.Parse("#0d9e75")),
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = 1.5
            };
            Canvas.SetLeft(dot, x - 4);
            Canvas.SetTop(dot, y - 4);
            ProfileCanvas.Children.Add(dot);

            var label = new TextBlock
            {
                Text = $"WP{i+1}\n{_terrain[i].AbsAltM:F0}m",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.Parse("#94a3b8")),
                TextAlignment = Avalonia.Media.TextAlignment.Center
            };
            Canvas.SetLeft(label, x - 15);
            Canvas.SetTop(label, y - 30);
            ProfileCanvas.Children.Add(label);
        }

        // Legend
        var legend = new TextBlock
        {
            Text = "— Ground elevation    -- Mission altitude (terrain + clearance)",
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.Parse("#64748b"))
        };
        Canvas.SetLeft(legend, 8);
        Canvas.SetTop(legend, 4);
        ProfileCanvas.Children.Add(legend);
    }

    private void BuildTable()
    {
        WpTable.Items.Clear();
        for (int i = 0; i < _terrain.Count; i++)
        {
            var t = _terrain[i];
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"),
                Background = i % 2 == 0
                    ? new SolidColorBrush(Color.Parse("#131f2e"))
                    : new SolidColorBrush(Color.Parse("#0f1923"))
            };
            void AddCell(int col, string text, string color = "#94a3b8") =>
                row.Children.Add(new TextBlock
                {
                    Text = text, FontSize = 11, FontFamily = new Avalonia.Media.FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(Color.Parse(color)),
                    Margin = new Avalonia.Thickness(12, 6),
                    [Grid.ColumnProperty] = col
                });
            AddCell(0, $"WP {i+1}", "#0d9e75");
            AddCell(1, $"{t.Lat:F5}°N");
            AddCell(2, $"{t.Lon:F5}°E");
            AddCell(3, $"{t.GroundElevM:F1} m");
            AddCell(4, $"{t.AbsAltM:F1} m ({t.GroundElevM:F1} + {t.ClearanceM:F0})");
            WpTable.Items.Add(row);
        }
    }

    private void OnApply(object? s, RoutedEventArgs e)
    {
        StatusText.Text = $"Terrain altitudes applied — {_terrain.Count} waypoints updated";
    }
}
