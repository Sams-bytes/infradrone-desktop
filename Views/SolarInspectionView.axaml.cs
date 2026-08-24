using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using InfraDroneDesktop.Services;
namespace InfraDroneDesktop.Views;

public class SolarResultRow
{
    public string ImagePath { get; set; } = "";
    public string Label { get; set; } = "";
    public float Confidence { get; set; }
    public string Severity { get; set; } = "";
    public string SeverityColor { get; set; } = "";
}

public partial class SolarInspectionView : UserControl
{
    private readonly SolarDefectService _solar = new();
    private List<SolarResultRow> _results = new();

    // Real severity mapping -- not all 12 real defect classes carry the
    // same urgency. Hot spots and offline modules are the ones that
    // actually risk fire/safety or total output loss; soiling/vegetation/
    // shadowing are routine maintenance, not urgent defects.
    private static readonly Dictionary<string, (string Severity, string Color)> SeverityMap = new()
    {
        ["Hot-Spot"] = ("Critical", "#dc2626"),
        ["Hot-Spot-Multi"] = ("Critical", "#dc2626"),
        ["Offline-Module"] = ("Critical", "#dc2626"),
        ["Cracking"] = ("High", "#ea580c"),
        ["Diode-Multi"] = ("High", "#ea580c"),
        ["Cell"] = ("Medium", "#eab308"),
        ["Cell-Multi"] = ("Medium", "#eab308"),
        ["Diode"] = ("Medium", "#eab308"),
        ["Soiling"] = ("Low", "#0d9e75"),
        ["Vegetation"] = ("Low", "#0d9e75"),
        ["Shadowing"] = ("Low", "#0d9e75"),
        ["No-Anomaly"] = ("None", "#64748b"),
    };

    public SolarInspectionView()
    {
        InitializeComponent();
        var modelPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "infradrone-desktop", "models", "solar_defect_classifier.onnx");
        var labelsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "infradrone-desktop", "models", "solar_defect_labels.json");
        if (File.Exists(modelPath) && File.Exists(labelsPath))
        {
            _solar.LoadModel(modelPath, labelsPath);
        }
    }

    private async void OnSelectFolder(object? s, RoutedEventArgs e)
    {
        if (!_solar.IsLoaded)
        {
            StatusText.Text = "Model not loaded -- check models/solar_defect_classifier.onnx exists.";
            return;
        }
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select folder of panel images" });
        if (folders.Count == 0) return;
        var folderPath = folders[0].Path.LocalPath;

        var imageFiles = Directory.GetFiles(folderPath, "*.*")
            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .Take(200) // reasonable batch cap so this stays responsive
            .ToList();

        if (imageFiles.Count == 0)
        {
            StatusText.Text = "No images found in that folder.";
            return;
        }

        StatusText.Text = $"Classifying {imageFiles.Count} images...";
        BtnSelectFolder.IsEnabled = false;
        _results.Clear();

        await Task.Run(() =>
        {
            foreach (var path in imageFiles)
            {
                var result = _solar.Classify(path);
                if (result == null) continue;
                var (severity, color) = SeverityMap.TryGetValue(result.Label, out var sv) ? sv : ("Unknown", "#94a3b8");
                _results.Add(new SolarResultRow
                {
                    ImagePath = path,
                    Label = result.Label,
                    Confidence = result.Confidence,
                    Severity = severity,
                    SeverityColor = color
                });
            }
        });

        // Sort so the most urgent findings appear first
        var severityOrder = new Dictionary<string, int> { ["Critical"] = 0, ["High"] = 1, ["Medium"] = 2, ["Low"] = 3, ["Unknown"] = 4, ["None"] = 5 };
        _results = _results.OrderBy(r => severityOrder.TryGetValue(r.Severity, out var o) ? o : 9).ToList();

        RenderResults();
        var flagged = _results.Count(r => r.Severity != "None");
        StatusText.Text = $"{imageFiles.Count} panels classified. {flagged} flagged with a real anomaly (not 'No-Anomaly').";
        BtnSelectFolder.IsEnabled = true;
        BtnGenerateReport.IsEnabled = _results.Count > 0;
    }

    private void RenderResults()
    {
        ResultsList.Items.Clear();
        foreach (var r in _results)
        {
            var row = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#1a2637")),
                CornerRadius = new Avalonia.CornerRadius(6),
                Padding = new Avalonia.Thickness(10),
                Margin = new Avalonia.Thickness(0, 0, 0, 4),
                BorderBrush = new SolidColorBrush(Color.Parse(r.SeverityColor)),
                BorderThickness = new Avalonia.Thickness(0, 0, 0, 2)
            };
            var stack = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12 };
            stack.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse(r.SeverityColor)),
                CornerRadius = new Avalonia.CornerRadius(4),
                Padding = new Avalonia.Thickness(8, 3),
                Child = new TextBlock { Text = r.Severity, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brushes.White }
            });
            stack.Children.Add(new TextBlock { Text = Path.GetFileName(r.ImagePath), FontSize = 12, Foreground = Brushes.White, Width = 140 });
            stack.Children.Add(new TextBlock { Text = r.Label, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#e2e8f0")), Width = 130 });
            stack.Children.Add(new TextBlock { Text = $"{r.Confidence:P0}", FontSize = 12, FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(Color.Parse("#94a3b8")) });
            row.Child = stack;
            ResultsList.Items.Add(row);
        }
    }

    private async void OnGenerateReport(object? s, RoutedEventArgs e)
    {
        BtnGenerateReport.IsEnabled = false;
        StatusText.Text = "Generating report...";
        try
        {
            var inputData = new
            {
                results = _results.Select(r => new { r.ImagePath, r.Label, r.Confidence, r.Severity }).ToList(),
                model_accuracy = "50.17% (first-generation model, real trained classifier on RaptorMaps' public dataset)"
            };
            var inputJson = System.Text.Json.JsonSerializer.Serialize(inputData);
            var inputPath = Path.Combine(Path.GetTempPath(), $"solar_report_input_{DateTime.Now.Ticks}.json");
            await File.WriteAllTextAsync(inputPath, inputJson);

            var downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloadsDir);
            var outPdf = Path.Combine(downloadsDir, $"solar_inspection_report_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
            var scriptPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "infradrone-desktop", "generate_solar_report.py");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/python3",
                Arguments = "\"" + scriptPath + "\" \"" + inputPath + "\" \"" + outPdf + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var proc = System.Diagnostics.Process.Start(psi);
            var stderr = await proc!.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode == 0 && File.Exists(outPdf))
            {
                StatusText.Text = $"Saved: {outPdf}";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = outPdf, UseShellExecute = true });
            }
            else
            {
                StatusText.Text = $"Failed: {stderr.Substring(0, Math.Min(300, stderr.Length))}";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed: {ex.Message}";
        }
        finally
        {
            BtnGenerateReport.IsEnabled = true;
        }
    }
}
