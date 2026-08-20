using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using System.Linq;
using System.Collections.Generic;
using InfraDroneDesktop.Services;
namespace InfraDroneDesktop.Views;

public partial class AssetIntelligenceView : UserControl
{
    private string? _beforePath;
    private string? _afterPath;

    private void OnSubNav(object? s, RoutedEventArgs e)
    {
        var btn = s as Button;
        ChangeDetectionPanel.IsVisible = btn == BtnSubChangeDetection;
        HealthPassportPanel.IsVisible = btn == BtnSubHealthPassport;
        ResultImage.IsVisible = btn == BtnSubChangeDetection;
        PassportContentPanel.IsVisible = btn == BtnSubHealthPassport;
        foreach (var b in new[] { BtnSubChangeDetection, BtnSubHealthPassport })
        {
            bool active = b == btn;
            b.Background = new SolidColorBrush(Color.Parse(active ? "#0d3d2e" : "#1a2637"));
            b.Foreground = new SolidColorBrush(Color.Parse(active ? "#0d9e75" : "#94a3b8"));
        }
        if (btn == BtnSubHealthPassport) BuildHealthPassport();
    }

    private void BuildHealthPassport()
    {
        PassportContentPanel.Children.Clear();
        var fields = SelectedAssetContext.CurrentFields;
        if (fields == null)
        {
            PassportHeaderText.Text = "Click an asset on the map in Flight View, then 'View Health Passport'.";
            PassportHeaderText.IsVisible = true;
            return;
        }
        PassportHeaderText.IsVisible = false;

        var assetKey = InfraDroneDesktop.Views.FlightView.ResolveAssetKey(fields);
        var history = AssetHistoryStore.GetHistory(assetKey);

        // Header
        var wegnummer = fields.TryGetValue("wegnummer", out var wn) ? wn : SelectedAssetContext.CurrentLayerName;
        PassportContentPanel.Children.Add(new TextBlock
        {
            Text = $"🪪 {wegnummer}",
            FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 4)
        });
        PassportContentPanel.Children.Add(new TextBlock
        {
            Text = $"Asset id {assetKey} · {SelectedAssetContext.CurrentLayerName}",
            FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#94a3b8")), Margin = new Thickness(0, 0, 0, 16)
        });

        // Stat cards: built year + trend rate (computed from real crow_X_YYYY pairs,
        // same approach as Flight View's Predict Trend, kept separate/duplicated
        // deliberately to avoid touching that already-working feature)
        var statRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 16) };
        string builtYear = fields.TryGetValue("jaarvanaanleg", out var jy) ? jy : "Unknown";
        statRow.Children.Add(MakeStatCard("Built", builtYear));

        var trendInfo = ComputeSimpleTrend(fields);
        if (trendInfo != null)
        {
            statRow.Children.Add(MakeStatCard(trendInfo.Value.Metric + " rate", trendInfo.Value.RateText));
            statRow.Children.Add(MakeStatCard("2029 projection", trendInfo.Value.Proj5Text));
        }
        PassportContentPanel.Children.Add(statRow);

        // Timeline: combine real government survey dates + local ticket history,
        // sorted chronologically.
        var events = new List<(DateTime Date, string Label, string Detail, string IconColor)>();
        foreach (var key in fields.Keys.Where(k => k.StartsWith("crow_inp_date_")))
        {
            if (DateTime.TryParse(fields[key], out var d))
                events.Add((d, "Official CROW survey", $"Survey conducted {d:yyyy-MM-dd}", "#3b82f6"));
        }
        foreach (var h in history)
        {
            events.Add((h.Date, h.Type == "ticket" ? "Maintenance ticket generated" : h.Type,
                $"{h.Severity} severity — {h.Description}", "#a855f7"));
        }
        events.Sort((a, b) => a.Date.CompareTo(b.Date));

        PassportContentPanel.Children.Add(new TextBlock
        {
            Text = "Timeline", FontSize = 12, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#94a3b8")), Margin = new Thickness(0, 8, 0, 8)
        });

        if (events.Count == 0)
        {
            PassportContentPanel.Children.Add(new TextBlock
            {
                Text = "No dated survey history or logged tickets found for this asset yet.",
                FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#64748b"))
            });
        }
        foreach (var ev in events)
        {
            var row = new Border { Background = new SolidColorBrush(Color.Parse("#1a2637")), CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 6) };
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(new TextBlock { Text = $"{ev.Date:yyyy-MM-dd} · {ev.Label}", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse(ev.IconColor)) });
            stack.Children.Add(new TextBlock { Text = ev.Detail, FontSize = 11, Foreground = Brushes.White, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            row.Child = stack;
            PassportContentPanel.Children.Add(row);
        }
    }

    private static Control MakeStatCard(string label, string value)
    {
        var card = new Border { Background = new SolidColorBrush(Color.Parse("#1a2637")), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10), MinWidth = 120 };
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#94a3b8")) });
        stack.Children.Add(new TextBlock { Text = value, FontSize = 16, FontWeight = FontWeight.Bold, Foreground = Brushes.White });
        card.Child = stack;
        return card;
    }

    private static (string Metric, string RateText, string Proj5Text)? ComputeSimpleTrend(Dictionary<string, string> fields)
    {
        var dateFields = fields.Keys.Where(k => k.StartsWith("crow_inp_date_"))
            .Select(k => k.Substring("crow_inp_date_".Length)).OrderBy(y => y).ToList();
        if (dateFields.Count < 2) return null;
        var yearA = dateFields[0];
        var yearB = dateFields[dateFields.Count - 1];
        if (!DateTime.TryParse(fields[$"crow_inp_date_{yearA}"], out var dateA)) return null;
        if (!DateTime.TryParse(fields[$"crow_inp_date_{yearB}"], out var dateB)) return null;
        var yearsElapsed = (dateB - dateA).TotalDays / 365.25;
        if (yearsElapsed <= 0) return null;

        var candidates = fields.Keys.Where(k => k.StartsWith("crow_") && k.EndsWith($"_{yearB}") && !k.StartsWith("crow_inp_date") && !k.StartsWith("crow_alg_")).ToList();
        foreach (var keyB in candidates)
        {
            var metric = keyB.Substring("crow_".Length, keyB.Length - "crow_".Length - $"_{yearB}".Length);
            var keyA = $"crow_{metric}_{yearA}";
            if (!fields.TryGetValue(keyA, out var strA) || !fields.TryGetValue(keyB, out var strB)) continue;
            if (!double.TryParse(strA, System.Globalization.CultureInfo.InvariantCulture, out var valA)) continue;
            if (!double.TryParse(strB, System.Globalization.CultureInfo.InvariantCulture, out var valB)) continue;
            var rate = (valB - valA) / yearsElapsed;
            if (Math.Abs(rate) < 0.01) continue;
            var proj = valB + rate * 3;
            return (metric, $"{rate:+0.##;-0.##}/yr", $"~{proj:0.##}");
        }
        return null;
    }

    public void ShowHealthPassport()
    {
        OnSubNav(BtnSubHealthPassport, new RoutedEventArgs());
    }
    private readonly string _venvPython =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "infradrone-desktop", "change_detection_env", "bin", "python3");
    private readonly string _scriptPath =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "infradrone-desktop", "change_detection.py");

    public AssetIntelligenceView()
    {
        InitializeComponent();
    }

    private async void OnSelectBefore(object? s, RoutedEventArgs e) => await SelectImage(isBefore: true);
    private async void OnSelectAfter(object? s, RoutedEventArgs e) => await SelectImage(isBefore: false);

    private async Task SelectImage(bool isBefore)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var downloadsFolder = await top.StorageProvider.TryGetFolderFromPathAsync(
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = isBefore ? "Select 'Before' orthomosaic" : "Select 'After' orthomosaic",
            SuggestedStartLocation = downloadsFolder,
            FileTypeFilter = new[] { new FilePickerFileType("GeoTIFF") { Patterns = new[] { "*.tif", "*.tiff" } } }
        });
        if (files.Count == 0) return;
        var path = files[0].Path.LocalPath;
        if (isBefore)
        {
            _beforePath = path;
            BtnSelectBefore.Content = $"📁 Before: {Path.GetFileName(path)}";
        }
        else
        {
            _afterPath = path;
            BtnSelectAfter.Content = $"📁 After: {Path.GetFileName(path)}";
        }
        BtnCompare.IsEnabled = _beforePath != null && _afterPath != null;
        StatusText.Text = BtnCompare.IsEnabled ? "Ready to compare." : "Select both images.";
    }

    private async void OnCompare(object? s, RoutedEventArgs e)
    {
        if (_beforePath == null || _afterPath == null) return;
        BtnCompare.IsEnabled = false;
        StatusText.Text = "Running change detection (aligning images, computing difference)...";
        var diffOut = Path.Combine(Path.GetTempPath(), $"change_diff_{DateTime.Now.Ticks}.png");
        var psi = new ProcessStartInfo
        {
            FileName = _venvPython,
            Arguments = $"\"{_scriptPath}\" \"{_beforePath}\" \"{_afterPath}\" \"{diffOut}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        try
        {
            var proc = Process.Start(psi);
            var stdout = await proc!.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var beforePngOut = diffOut.Replace(".png", "_before.png");
            if (proc.ExitCode == 0 && File.Exists(diffOut) && File.Exists(beforePngOut))
            {
                // Composite the heatmap over the "before" image so the change
                // is shown in context of the real scene, not floating on
                // transparency by itself. Uses the script's PNG-converted copy
                // of "before" instead of the raw .tif -- Avalonia's Bitmap
                // loader can't reliably read GeoTIFF, even though the Python
                // side (via PIL) reads it fine for the actual computation.
                using var before = new Bitmap(beforePngOut);
                using var heatmap = new Bitmap(diffOut);
                var rt = new Avalonia.Media.Imaging.RenderTargetBitmap(
                    new Avalonia.PixelSize(before.PixelSize.Width, before.PixelSize.Height));
                using (var ctx = rt.CreateDrawingContext())
                {
                    ctx.DrawImage(before, new Avalonia.Rect(0, 0, before.PixelSize.Width, before.PixelSize.Height));
                    ctx.DrawImage(heatmap, new Avalonia.Rect(0, 0, before.PixelSize.Width, before.PixelSize.Height));
                }
                ResultImage.Source = rt;

                var lastLine = stdout.Trim().Split('\n');
                var summary = lastLine.Length >= 2 ? lastLine[^2] : "Comparison complete.";
                StatusText.Text = summary;
            }
            else
            {
                StatusText.Text = $"Change detection failed: {stderr.Substring(0, Math.Min(300, stderr.Length))}";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to run comparison: {ex.Message}";
        }
        finally
        {
            BtnCompare.IsEnabled = true;
        }
    }
}
