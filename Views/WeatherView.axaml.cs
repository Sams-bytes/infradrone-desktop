using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace InfraDroneDesktop.Views;

public partial class WeatherView : UserControl
{
    private readonly HttpClient _http = new HttpClient();
    private const double LAT = 53.2194;
    private const double LON = 6.5665;

    // Operational limits
    private const double WIND_MAX = 12.0;
    private const double GUST_VTOL = 10.0;
    private const double GUST_FW = 15.0;
    private const double TEMP_MIN = 0.0;
    private const double TEMP_CAUTION = 10.0;
    private const double VIS_MIN = 500.0;
    private const double CLOUD_MAX = 90.0;

    public WeatherView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _ = FetchWeatherAsync();
    }

    private async void OnRefresh(object? s, RoutedEventArgs e) => await FetchWeatherAsync();

    private async Task FetchWeatherAsync()
    {
        GoText.Text = "...";
        ContentPanel.Children.Clear();

        try
        {
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={LAT}&longitude={LON}" +
                      "&current=temperature_2m,relative_humidity_2m,apparent_temperature," +
                      "weather_code,wind_speed_10m,wind_direction_10m,wind_gusts_10m,visibility" +
                      "&hourly=temperature_2m,wind_speed_10m,wind_gusts_10m" +
                      "&wind_speed_unit=ms&forecast_days=1&timezone=Europe/Amsterdam";

            var json = await _http.GetStringAsync(url);
            var doc = JsonDocument.Parse(json);
            var current = doc.RootElement.GetProperty("current");
            var hourly = doc.RootElement.GetProperty("hourly");

            var wind = current.GetProperty("wind_speed_10m").GetDouble();
            var gusts = current.GetProperty("wind_gusts_10m").GetDouble();
            var temp = current.GetProperty("temperature_2m").GetDouble();
            var feelsLike = current.GetProperty("apparent_temperature").GetDouble();
            var humidity = current.GetProperty("relative_humidity_2m").GetDouble();
            var windDir = current.GetProperty("wind_direction_10m").GetDouble();
            var weatherCode = current.GetProperty("weather_code").GetInt32();
            var vis = current.TryGetProperty("visibility", out var v) ? v.GetDouble() : 30000;

            // Determine Go/No-Go
            bool isGo = wind <= WIND_MAX && gusts <= GUST_VTOL && temp >= TEMP_MIN && vis >= VIS_MIN;
            bool isCaution = !isGo || gusts > GUST_VTOL * 0.8 || temp < TEMP_CAUTION;

            UpdatedText.Text = $"Updated {DateTime.Now:HH:mm} local";

            if (isGo && !isCaution)
            {
                GoBadge.Background = new SolidColorBrush(Color.Parse("#0d3d2e"));
                GoBadge.BorderBrush = new SolidColorBrush(Color.Parse("#0d9e75"));
                GoText.Text = "GO";
                GoText.Foreground = new SolidColorBrush(Color.Parse("#0d9e75"));
            }
            else if (isGo)
            {
                GoBadge.Background = new SolidColorBrush(Color.Parse("#3d2e0d"));
                GoBadge.BorderBrush = new SolidColorBrush(Color.Parse("#f59e0b"));
                GoText.Text = "CAUTION";
                GoText.Foreground = new SolidColorBrush(Color.Parse("#f59e0b"));
            }
            else
            {
                GoBadge.Background = new SolidColorBrush(Color.Parse("#3d1515"));
                GoBadge.BorderBrush = new SolidColorBrush(Color.Parse("#ef4444"));
                GoText.Text = "NO-GO";
                GoText.Foreground = new SolidColorBrush(Color.Parse("#ef4444"));
            }

            // Big stat cards
            var cards = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*") };
            cards.Children.Add(StatCard(0, "WIND SPEED", $"{wind:F1} m/s",
                $"From {windDir:F0}° {DirName(windDir)}", wind <= WIND_MAX, $"Limit: {WIND_MAX} m/s"));
            cards.Children.Add(StatCard(1, "GUSTS", $"{gusts:F1} m/s",
                $"VTOL limit {GUST_VTOL} m/s · FW limit {GUST_FW} m/s",
                gusts <= GUST_VTOL, $"VTOL: {GUST_VTOL} m/s · FW: {GUST_FW} m/s"));
            cards.Children.Add(StatCard(2, "TEMPERATURE", $"{temp:F0}°C",
                $"Feels like {feelsLike:F0}°C · RH {humidity:F0}%",
                temp >= TEMP_MIN, $"Min: {TEMP_MIN}°C · Caution: <{TEMP_CAUTION}°C"));
            cards.Children.Add(StatCard(3, "VISIBILITY", $"{vis/1000:F1} km",
                $"Cloud cover ~{weatherCode}% · Code {weatherCode}",
                vis >= VIS_MIN, $"VLOS min: {VIS_MIN}m · Recommended: 2km+"));
            ContentPanel.Children.Add(cards);

            // Safety limits table
            ContentPanel.Children.Add(SectionHeader("FLIGHT SAFETY LIMITS — ALL PARAMETERS"));
            ContentPanel.Children.Add(SafetyTable(new[]
            {
                ("Wind speed", $"{wind:F1} m/s", $"Limit: {WIND_MAX} m/s", wind <= WIND_MAX),
                ("Gusts — VTOL ops", $"{gusts:F1} m/s", $"Limit: {GUST_VTOL} m/s", gusts <= GUST_VTOL),
                ("Gusts — fixed-wing", $"{gusts:F1} m/s", $"Limit: {GUST_FW} m/s", gusts <= GUST_FW),
                ("Temperature — LiPo", $"{temp:F0} °C", $"Min: {TEMP_MIN}°C", temp >= TEMP_MIN),
                ("Temperature — caution", $"{temp:F0} °C", $"Caution: <{TEMP_CAUTION}°C", temp >= TEMP_CAUTION),
                ("Visibility", $"{vis/1000:F1} km", $"Min: {VIS_MIN}m VLOS", vis >= VIS_MIN),
            }));

            // 6-hour forecast
            ContentPanel.Children.Add(SectionHeader("6-HOUR WIND & GUST FORECAST"));
            var forecast = BuildForecast(hourly);
            ContentPanel.Children.Add(forecast);

            // Limits reference
            ContentPanel.Children.Add(SectionHeader("OPERATIONAL LIMITS REFERENCE (EASA Open Category / DAMbv ConOps)"));
            ContentPanel.Children.Add(LimitsTable());
        }
        catch (Exception ex)
        {
            GoText.Text = "ERR";
            ContentPanel.Children.Add(new TextBlock
            {
                Text = $"Weather fetch failed: {ex.Message}",
                FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#ef4444")),
                Margin = new Avalonia.Thickness(0, 8)
            });
        }
    }

    private Border StatCard(int col, string label, string value, string sub, bool ok, string limit)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1a2637")),
            BorderBrush = new SolidColorBrush(ok ? Color.Parse("#1e3a5f") : Color.Parse("#ef4444")),
            BorderThickness = new Avalonia.Thickness(0.5),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(12),
            Margin = new Avalonia.Thickness(4),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = label, FontSize = 9, FontWeight = Avalonia.Media.FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#4a5568")), LetterSpacing = 1 },
                    new TextBlock { Text = value, FontSize = 22, FontWeight = Avalonia.Media.FontWeight.Bold,
                        Foreground = new SolidColorBrush(ok ? Color.Parse("#e2e8f0") : Color.Parse("#ef4444")) },
                    new TextBlock { Text = sub, FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#64748b")),
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = (ok ? "✓ " : "⚠ ") + limit, FontSize = 10,
                        Foreground = new SolidColorBrush(ok ? Color.Parse("#0d9e75") : Color.Parse("#f59e0b")) }
                }
            },
            [Grid.ColumnProperty] = col
        };
        return card;
    }

    private Border SectionHeader(string text)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1e3a5f")),
            Padding = new Avalonia.Thickness(12, 6),
            Margin = new Avalonia.Thickness(0, 8, 0, 2),
            Child = new TextBlock
            {
                Text = text, FontSize = 9, FontWeight = Avalonia.Media.FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.Parse("#60a5fa")), LetterSpacing = 1
            }
        };
    }

    private Grid SafetyTable((string label, string value, string limit, bool ok)[] rows)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,120,*,60") };
        int r = 0;
        foreach (var (label, value, limit, ok) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var bg = r % 2 == 0 ? "#131f2e" : "#0f1923";
            var row = new Border { Background = new SolidColorBrush(Color.Parse(bg)),
                Padding = new Avalonia.Thickness(12, 6), [Grid.RowProperty] = r };
            row.Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,120,*,60"),
                Children =
                {
                    new TextBlock { Text = label, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#e2e8f0")), [Grid.ColumnProperty]=0 },
                    new TextBlock { Text = value, FontSize = 11, FontFamily = new Avalonia.Media.FontFamily("Consolas"),
                        Foreground = new SolidColorBrush(ok ? Color.Parse("#e2e8f0") : Color.Parse("#ef4444")), [Grid.ColumnProperty]=1 },
                    new TextBlock { Text = limit, FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#64748b")), [Grid.ColumnProperty]=2 },
                    new TextBlock { Text = ok ? "✓ OK" : "⚠ CAUTION", FontSize = 10, FontWeight = Avalonia.Media.FontWeight.Bold,
                        Foreground = new SolidColorBrush(ok ? Color.Parse("#0d9e75") : Color.Parse("#f59e0b")), [Grid.ColumnProperty]=3 }
                }};
            grid.Children.Add(row);
            r++;
        }
        return grid;
    }

    private StackPanel BuildForecast(JsonElement hourly)
    {
        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        var times = hourly.GetProperty("time");
        var winds = hourly.GetProperty("wind_speed_10m");
        var gusts = hourly.GetProperty("wind_gusts_10m");
        var temps = hourly.GetProperty("temperature_2m");
        var now = DateTime.Now.Hour;

        int count = 0;
        for (int i = 0; i < times.GetArrayLength() && count < 7; i++)
        {
            var t = times[i].GetString() ?? "";
            if (!t.Contains($"T{now:00}:") && count == 0) continue;
            var w = winds[i].GetDouble();
            var g = gusts[i].GetDouble();
            var temp = temps[i].GetDouble();
            var isGo = w <= WIND_MAX && g <= GUST_VTOL;
            var dotColor = isGo ? "#0d9e75" : g <= GUST_FW ? "#f59e0b" : "#ef4444";
            var label = count == 0 ? "NOW" : $"+{count}h";

            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#1a2637")),
                BorderBrush = new SolidColorBrush(Color.Parse(dotColor)),
                BorderThickness = new Avalonia.Thickness(0.5),
                CornerRadius = new Avalonia.CornerRadius(6),
                Padding = new Avalonia.Thickness(10, 8),
                MinWidth = 70,
                Child = new StackPanel
                {
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock { Text = label, FontSize = 9, FontWeight = Avalonia.Media.FontWeight.Bold,
                            Foreground = new SolidColorBrush(Color.Parse("#64748b")), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center },
                        new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8,
                            Fill = new SolidColorBrush(Color.Parse(dotColor)),
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center },
                        new TextBlock { Text = $"{w:F1}", FontSize = 14, FontWeight = Avalonia.Media.FontWeight.Bold,
                            Foreground = new SolidColorBrush(Color.Parse("#e2e8f0")), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center },
                        new TextBlock { Text = $"↑{g:F1} m/s", FontSize = 9, Foreground = new SolidColorBrush(Color.Parse("#64748b")),
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center },
                        new TextBlock { Text = $"{temp:F0}°C", FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#94a3b8")),
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center }
                    }
                }
            });
            count++;
        }
        return panel;
    }

    private Grid LimitsTable()
    {
        var rows = new[]
        {
            ("Max wind speed", $"{WIND_MAX} m/s"),
            ("Max gusts — VTOL", $"{GUST_VTOL} m/s"),
            ("Max gusts — fixed-wing", $"{GUST_FW} m/s"),
            ("Precipitation", "None permitted"),
            ("Min visibility (VLOS)", $"{VIS_MIN}m"),
            ("Min temperature", $"{TEMP_MIN}°C (LiPo)"),
            ("Caution temperature", $"<{TEMP_CAUTION}°C"),
            ("Max cloud cover", $"{CLOUD_MAX}%"),
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        int r = 0;
        foreach (var (label, value) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var bg = r % 2 == 0 ? "#131f2e" : "#0f1923";
            grid.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse(bg)),
                Padding = new Avalonia.Thickness(12, 5), [Grid.RowProperty] = r,
                Child = new TextBlock { Text = label, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#94a3b8")) }
            });
            grid.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse(bg)),
                Padding = new Avalonia.Thickness(12, 5), [Grid.RowProperty] = r, [Grid.ColumnProperty] = 1,
                Child = new TextBlock { Text = value, FontSize = 11, FontWeight = Avalonia.Media.FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#e2e8f0")) }
            });
            r++;
        }
        return grid;
    }

    private static string DirName(double deg) => deg switch
    {
        < 22.5 or >= 337.5 => "N", < 67.5 => "NE", < 112.5 => "E",
        < 157.5 => "SE", < 202.5 => "S", < 247.5 => "SW",
        < 292.5 => "W", _ => "NW"
    };
}
