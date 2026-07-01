using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using InfraDroneDesktop.Services;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace InfraDroneDesktop.Views;

public class BatchResult
{
    public string ImagePath { get; set; } = "";
    public List<Detection> Detections { get; set; } = new();
}

public partial class AiView : UserControl
{
    private readonly DefectDetectionService _ai = new DefectDetectionService();
    private string? _imagePath;
    private List<BatchResult> _batchResults = new();

    public AiView()
    {
        InitializeComponent();
    }

    private void OnLoadModel(object? s, RoutedEventArgs e)
    {
        var modelPath = "/home/sam/infradrone-desktop/models/pothole_detector.onnx";
        if (!File.Exists(modelPath))
        {
            StatusText.Text = "Model file not found at " + modelPath;
            return;
        }
        StatusText.Text = "Loading model...";
        var ok = _ai.LoadModel(modelPath);
        if (ok)
        {
            ModelDot.Fill = new SolidColorBrush(Color.Parse("#0d9e75"));
            ModelStatus.Text = $"Loaded: {_ai.ModelName}";
            BtnSelectImage.IsEnabled = true;
            BtnSelectFolder.IsEnabled = true;
            StatusText.Text = "Model ready. Select an image or folder to analyze.";
        }
        else
        {
            ModelDot.Fill = new SolidColorBrush(Color.Parse("#ef4444"));
            ModelStatus.Text = "Failed to load model";
            StatusText.Text = "Check console for error details.";
        }
    }

    private async void OnSelectFolder(object? s, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select folder of images to analyze"
        });
        if (folders.Count == 0) return;
        var folderPath = folders[0].Path.LocalPath;
        var exts = new[] { ".jpg", ".jpeg", ".png", ".tif" };
        var imagePaths = Directory.GetFiles(folderPath)
            .Where(p => exts.Contains(Path.GetExtension(p).ToLower()))
            .OrderBy(p => p)
            .ToList();
        if (imagePaths.Count == 0)
        {
            StatusText.Text = "No images found in selected folder.";
            return;
        }
        await RunBatchDetection(imagePaths);
    }

    private async void OnSelectImage(object? s, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select image to analyze",
            FileTypeFilter = new[] { new FilePickerFileType("Images") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.tif" } } }
        });
        if (files.Count == 0) return;
        _imagePath = files[0].Path.LocalPath;
        StatusText.Text = $"Loaded: {Path.GetFileName(_imagePath)}";
        BtnRunDetection.IsEnabled = true;

        // Show original image
        using var stream = File.OpenRead(_imagePath);
        ImageDisplay.Source = new Bitmap(stream);
        ResultsList.Items.Clear();
    }

    private async Task RunBatchDetection(List<string> imagePaths)
    {
        _batchResults.Clear();
        BatchList.Items.Clear();
        ResultsList.Items.Clear();
        BatchSummaryBox.IsVisible = true;
        BatchListPanel.IsVisible = true;

        int totalDetections = 0;
        for (int i = 0; i < imagePaths.Count; i++)
        {
            StatusText.Text = $"Analyzing {i + 1}/{imagePaths.Count}...";
            await Task.Delay(1);
            var dets = _ai.Detect(imagePaths[i]);
            totalDetections += dets.Count;
            _batchResults.Add(new BatchResult { ImagePath = imagePaths[i], Detections = dets });
        }

        BatchSummaryText.Text = $"{imagePaths.Count} images analyzed\n{totalDetections} pothole(s) detected total";
        PopulateBatchList();
        StatusText.Text = $"Batch complete: {imagePaths.Count} images, {totalDetections} detections.";
    }

    private void PopulateBatchList()
    {
        BatchList.Items.Clear();
        foreach (var r in _batchResults)
        {
            var item = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#131f2e")),
                CornerRadius = new Avalonia.CornerRadius(6),
                Padding = new Avalonia.Thickness(8, 6),
                Margin = new Avalonia.Thickness(0, 2),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Child = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children =
                    {
                        new TextBlock { Text = Path.GetFileName(r.ImagePath), FontSize = 10,
                            Foreground = new SolidColorBrush(Color.Parse("#e2e8f0")),
                            [Grid.ColumnProperty] = 0 },
                        new TextBlock { Text = r.Detections.Count.ToString(), FontSize = 10,
                            FontFamily = new FontFamily("Consolas"),
                            Foreground = new SolidColorBrush(Color.Parse(r.Detections.Count > 0 ? "#0d9e75" : "#64748b")),
                            [Grid.ColumnProperty] = 1 }
                    }
                }
            };
            item.PointerPressed += (s2, e2) => ShowBatchResult(r);
            BatchList.Items.Add(item);
        }
    }

    private void ShowBatchResult(BatchResult r)
    {
        _imagePath = r.ImagePath;
        using var annotated = _ai.DrawDetections(r.ImagePath, r.Detections);
        using var data = annotated.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream(data.ToArray());
        ImageDisplay.Source = new Bitmap(ms);

        ResultsList.Items.Clear();
        foreach (var d in r.Detections.OrderByDescending(x => x.Confidence))
        {
            ResultsList.Items.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#131f2e")),
                CornerRadius = new Avalonia.CornerRadius(6),
                Padding = new Avalonia.Thickness(8, 6),
                Margin = new Avalonia.Thickness(0, 2),
                Child = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children =
                    {
                        new TextBlock { Text = d.Label, FontSize = 11,
                            Foreground = new SolidColorBrush(Color.Parse("#e2e8f0")),
                            [Grid.ColumnProperty] = 0 },
                        new TextBlock { Text = $"{d.Confidence:P0}", FontSize = 11,
                            FontFamily = new FontFamily("Consolas"),
                            Foreground = new SolidColorBrush(Color.Parse("#0d9e75")),
                            [Grid.ColumnProperty] = 1 }
                    }
                }
            });
        }
        if (r.Detections.Count == 0)
            ResultsList.Items.Add(new TextBlock { Text = "No detections.",
                FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#64748b")) });
        StatusText.Text = $"{Path.GetFileName(r.ImagePath)}: {r.Detections.Count} detection(s)";
    }

    private async void OnGenerateAiReport(object? s, RoutedEventArgs e)
    {
        if (_batchResults.Count == 0) return;
        StatusText.Text = "Building report...";
        BtnGenerateAiReport.IsEnabled = false;

        var reportDir = Path.Combine(Path.GetTempPath(), "infradrone_ai_report_" + DateTime.Now.Ticks);
        var imagesDir = Path.Combine(reportDir, "images");
        Directory.CreateDirectory(imagesDir);

        var manifest = new List<object>();
        foreach (var r in _batchResults)
        {
            var outName = Path.GetFileNameWithoutExtension(r.ImagePath) + ".png";
            var outPath = Path.Combine(imagesDir, outName);
            using (var annotated = _ai.DrawDetections(r.ImagePath, r.Detections))
            using (var data = annotated.Encode(SKEncodedImageFormat.Png, 100))
            using (var fs = File.OpenWrite(outPath))
            {
                data.SaveTo(fs);
            }
            manifest.Add(new
            {
                filename = Path.GetFileName(r.ImagePath),
                annotated_image = outName,
                detections = r.Detections.Select(d => new
                {
                    label = d.Label,
                    confidence = d.Confidence,
                    x = d.X, y = d.Y, width = d.Width, height = d.Height
                })
            });
        }

        var jsonPath = Path.Combine(reportDir, "manifest.json");
        File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(manifest,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var outPdf = Path.Combine(reportDir, "ai_defect_report.pdf");
        var script = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "infradrone-desktop", "generate_ai_report.py");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/home/sam/agridrone_env/bin/python3",
            Arguments = $"{script} {jsonPath} {imagesDir} {outPdf}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var proc = System.Diagnostics.Process.Start(psi);
        await Task.Run(() => proc?.WaitForExit());

        BtnGenerateAiReport.IsEnabled = true;
        if (File.Exists(outPdf))
            StatusText.Text = $"Report saved: {outPdf}";
        else
            StatusText.Text = "Report generation failed — check generate_ai_report.py output.";
    }

    private void OnRunDetection(object? s, RoutedEventArgs e)
    {
        if (_imagePath == null || !_ai.IsLoaded) return;
        StatusText.Text = "Running inference...";
        BtnRunDetection.IsEnabled = false;

        var detections = _ai.Detect(_imagePath);

        StatusText.Text = $"Found {detections.Count} object(s)";
        BtnRunDetection.IsEnabled = true;

        // Draw boxes
        using var annotated = _ai.DrawDetections(_imagePath, detections);
        using var data = annotated.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream(data.ToArray());
        ImageDisplay.Source = new Bitmap(ms);

        // Update results list
        ResultsList.Items.Clear();
        foreach (var d in detections.OrderByDescending(x => x.Confidence))
        {
            ResultsList.Items.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#131f2e")),
                CornerRadius = new Avalonia.CornerRadius(6),
                Padding = new Avalonia.Thickness(8, 6),
                Margin = new Avalonia.Thickness(0, 2),
                Child = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children =
                    {
                        new TextBlock { Text = d.Label, FontSize = 11,
                            Foreground = new SolidColorBrush(Color.Parse("#e2e8f0")),
                            [Grid.ColumnProperty] = 0 },
                        new TextBlock { Text = $"{d.Confidence:P0}", FontSize = 11,
                            FontFamily = new FontFamily("Consolas"),
                            Foreground = new SolidColorBrush(Color.Parse("#0d9e75")),
                            [Grid.ColumnProperty] = 1 }
                    }
                }
            });
        }
        if (detections.Count == 0)
            ResultsList.Items.Add(new TextBlock { Text = "No objects detected.",
                FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#64748b")) });
    }
}
