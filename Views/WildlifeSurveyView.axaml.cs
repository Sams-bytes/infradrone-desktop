using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using InfraDroneDesktop.Services;
using SkiaSharp;
using Mapsui;
using Mapsui.Tiling;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Nts;
using Mapsui.UI.Avalonia;
using NetTopologySuite.Geometries;

namespace InfraDroneDesktop.Views
{
    public enum WildlifeModelType { RoeDeer, BigBird }

    public class UnifiedDetection
    {
        public string Label { get; set; } = "";
        public float Confidence { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
    }

    public class WildlifeBatchResult
    {
        public string ImagePath { get; set; } = "";
        public List<UnifiedDetection> Detections { get; set; } = new();
    }

    public partial class WildlifeSurveyView : UserControl
    {
        private readonly WildlifeDetectionService _roeDeerAi = new WildlifeDetectionService();
        private readonly BigBirdDetectionService _birdAi = new BigBirdDetectionService();
        private WildlifeModelType _activeModel = WildlifeModelType.RoeDeer;

        private string _imagePath = "";
        private readonly ObservableCollection<WildlifeBatchResult> _batchResults = new();
        private List<WildlifeBatchResult> _batchList = new();
        private int _currentBatchIndex = -1;
        private MapControl? _mapControl;
        private Mapsui.Map? _map;

        public WildlifeSurveyView()
        {
            InitializeComponent();
        }

        private WildlifeModelType GetSelectedModelType()
        {
            return RadioBigBird.IsChecked == true ? WildlifeModelType.BigBird : WildlifeModelType.RoeDeer;
        }

        private bool IsActiveModelLoaded()
        {
            return _activeModel == WildlifeModelType.RoeDeer ? _roeDeerAi.IsLoaded : _birdAi.IsLoaded;
        }

        private List<UnifiedDetection> RunDetection(string imagePath)
        {
            if (_activeModel == WildlifeModelType.RoeDeer)
            {
                return _roeDeerAi.Detect(imagePath).Select(d => new UnifiedDetection
                {
                    Label = d.Label, Confidence = d.Confidence,
                    X = d.X, Y = d.Y, Width = d.Width, Height = d.Height
                }).ToList();
            }
            else
            {
                return _birdAi.Detect(imagePath).Select(d => new UnifiedDetection
                {
                    Label = d.Label, Confidence = d.Confidence,
                    X = d.X, Y = d.Y, Width = d.Width, Height = d.Height
                }).ToList();
            }
        }

        private SKBitmap DrawUnifiedDetections(string imagePath, List<UnifiedDetection> detections)
        {
            var bitmap = SKBitmap.Decode(imagePath);
            using var canvas = new SKCanvas(bitmap);
            using var textPaint = new SKPaint { TextSize = 24, IsAntialias = true };
            using var bgPaint = new SKPaint { Color = SKColors.Black.WithAlpha(180), Style = SKPaintStyle.Fill };

            var color = _activeModel == WildlifeModelType.RoeDeer
                ? new SKColor(220, 38, 38)
                : new SKColor(34, 197, 94);

            foreach (var d in detections)
            {
                using var boxPaint = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
                textPaint.Color = color;

                canvas.DrawRect(d.X, d.Y, d.Width, d.Height, boxPaint);
                var label = $"{d.Label} {d.Confidence:P0}";
                var textWidth = textPaint.MeasureText(label);
                canvas.DrawRect(d.X, d.Y - 28, textWidth + 8, 28, bgPaint);
                canvas.DrawText(label, d.X + 4, d.Y - 8, textPaint);
            }
            return bitmap;
        }

        private async void OnLoadModel(object? s, RoutedEventArgs e)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var modelsFolder = await top.StorageProvider.TryGetFolderFromPathAsync(
                "/home/sam/infradrone-desktop/models");
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select trained ONNX model",
                SuggestedStartLocation = modelsFolder,
                FileTypeFilter = new[] { new FilePickerFileType("ONNX model") { Patterns = new[] { "*.onnx" } } }
            });
            if (files.Count == 0) return;

            _activeModel = GetSelectedModelType();
            var path = files[0].Path.LocalPath;
            bool loaded = _activeModel == WildlifeModelType.RoeDeer
                ? _roeDeerAi.LoadModel(path)
                : _birdAi.LoadModel(path);

            if (loaded)
            {
                var name = _activeModel == WildlifeModelType.RoeDeer ? _roeDeerAi.ModelName : _birdAi.ModelName;
                ModelStatusText.Text = $"Loaded ({_activeModel}): {name}";
                BtnRunDetection.IsEnabled = true;
                StatusText.Text = "Model loaded. Select an image or folder.";
            }
            else
            {
                ModelStatusText.Text = "Failed to load model.";
            }
        }

        private async void OnSelectImage(object? s, RoutedEventArgs e)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select image",
                FileTypeFilter = new[] { new FilePickerFileType("Images") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png" } } }
            });
            if (files.Count == 0) return;

            _imagePath = files[0].Path.LocalPath;
            BatchListPanel.IsVisible = false;
            using var stream = File.OpenRead(_imagePath);
            ImageDisplay.Source = new Bitmap(stream);
            StatusText.Text = $"Loaded: {Path.GetFileName(_imagePath)}";
        }

        private async void OnSelectFolder(object? s, RoutedEventArgs e)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var folders = await top.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "Select folder of images" });
            if (folders.Count == 0) return;

            var folderPath = folders[0].Path.LocalPath;
            var imagePaths = Directory.GetFiles(folderPath)
                .Where(f => f.EndsWith(".jpg") || f.EndsWith(".jpeg") || f.EndsWith(".png"))
                .ToList();
            await RunBatchAsync(imagePaths);
        }

        private async void OnOneClickDemo(object? s, RoutedEventArgs e)
        {
            await PreloadDemoAsync();
        }

        public async Task PreloadDemoAsync()
        {
            _activeModel = GetSelectedModelType();

            if (_activeModel == WildlifeModelType.RoeDeer)
            {
                var modelPath = "/home/sam/infradrone-desktop/models/wildlife_roe_deer_v1.onnx";
                if (!File.Exists(modelPath)) { StatusText.Text = "Demo model not found: " + modelPath; return; }
                if (_roeDeerAi.LoadModel(modelPath))
                {
                    ModelStatusText.Text = $"Loaded (RoeDeer): {_roeDeerAi.ModelName}";
                    BtnRunDetection.IsEnabled = true;
                }
                else { ModelStatusText.Text = "Failed to load model."; return; }

                var folderPath = "/home/sam/wildlife-survey/dataset/valid/images";
                if (!Directory.Exists(folderPath)) { StatusText.Text = "Demo folder not found: " + folderPath; return; }
                var imagePaths = Directory.GetFiles(folderPath)
                    .Where(f => f.EndsWith(".jpg") || f.EndsWith(".jpeg") || f.EndsWith(".png"))
                    .ToList();
                await RunBatchAsync(imagePaths);
            }
            else
            {
                var modelPath = "/home/sam/infradrone-desktop/models/bigbird_v1.onnx";
                if (!File.Exists(modelPath)) { StatusText.Text = "Demo model not found: " + modelPath; return; }
                if (_birdAi.LoadModel(modelPath))
                {
                    ModelStatusText.Text = $"Loaded (BigBird): {_birdAi.ModelName}";
                    BtnRunDetection.IsEnabled = true;
                }
                else { ModelStatusText.Text = "Failed to load model."; return; }

                var folderPath = "/home/sam/bigbird-survey/dataset/testtrain_dataset/test";
                if (!Directory.Exists(folderPath)) { StatusText.Text = "Demo folder not found: " + folderPath; return; }
                var imagePaths = Directory.GetFiles(folderPath)
                    .Where(f => f.EndsWith(".jpg") || f.EndsWith(".jpeg") || f.EndsWith(".png"))
                    .Take(30)
                    .ToList();
                await RunBatchAsync(imagePaths);
            }
        }

        private async Task RunBatchAsync(List<string> imagePaths)
        {
            if (imagePaths.Count == 0)
            {
                StatusText.Text = "No images found in that folder.";
                return;
            }

            StatusText.Text = $"Running detection on {imagePaths.Count} images...";
            _batchResults.Clear();

            await Task.Run(() =>
            {
                for (int i = 0; i < imagePaths.Count; i++)
                {
                    var dets = RunDetection(imagePaths[i]);
                    _batchResults.Add(new WildlifeBatchResult { ImagePath = imagePaths[i], Detections = dets });
                }
            });

            _batchList = _batchResults.ToList();
            _currentBatchIndex = 0;

            BatchListPanel.IsVisible = true;
            BatchList.Items.Clear();
            foreach (var r in _batchResults)
            {
                var btn = new Button
                {
                    Content = $"{Path.GetFileName(r.ImagePath)} ({r.Detections.Count} det.)",
                    FontSize = 10,
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                    Background = Avalonia.Media.Brushes.Transparent,
                    Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e2e8f0"))
                };
                var result = r;
                btn.Click += (_, __) => { _currentBatchIndex = _batchList.IndexOf(result); ShowBatchImage(); };
                BatchList.Items.Add(btn);
            }

            BtnPrevImage.IsEnabled = true;
            BtnNextImage.IsEnabled = true;
            BtnShowOnMap.IsEnabled = _batchList.Any(r => r.Detections.Count > 0);
            ShowBatchImage();

            var totalDetections = _batchResults.Sum(r => r.Detections.Count);
            var imagesWithDetections = _batchResults.Count(r => r.Detections.Count > 0);
            StatusText.Text = $"Done: {imagePaths.Count} images processed, " +
                $"{totalDetections} detection(s) across {imagesWithDetections} image(s).";
        }

        private void ShowResult(WildlifeBatchResult r)
        {
            _imagePath = r.ImagePath;
            using var annotated = DrawUnifiedDetections(r.ImagePath, r.Detections);
            using var data = annotated.Encode(SKEncodedImageFormat.Png, 100);
            using var ms = new MemoryStream(data.ToArray());
            ImageDisplay.Source = new Bitmap(ms);
            StatusText.Text = $"{Path.GetFileName(r.ImagePath)}: {r.Detections.Count} detection(s).";
        }

        private void ShowBatchImage()
        {
            if (_currentBatchIndex < 0 || _currentBatchIndex >= _batchList.Count) return;
            var r = _batchList[_currentBatchIndex];
            ShowResult(r);
            BatchPositionText.Text = $"Image {_currentBatchIndex + 1} of {_batchList.Count}";
        }

        private void OnPrevImage(object? s, RoutedEventArgs e)
        {
            if (_currentBatchIndex > 0) { _currentBatchIndex--; ShowBatchImage(); }
        }

        private void OnNextImage(object? s, RoutedEventArgs e)
        {
            if (_currentBatchIndex < _batchList.Count - 1) { _currentBatchIndex++; ShowBatchImage(); }
        }

        private void OnRunDetection(object? s, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_imagePath) || !IsActiveModelLoaded())
                {
                    StatusText.Text = "Load a model and select an image first.";
                    return;
                }
                var dets = RunDetection(_imagePath);
                using var annotated = DrawUnifiedDetections(_imagePath, dets);

                using var data = annotated.Encode(SKEncodedImageFormat.Png, 100);
                using var ms = new MemoryStream(data.ToArray());
                ImageDisplay.Source = new Bitmap(ms);
                StatusText.Text = $"{dets.Count} detection(s) found.";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG] EXCEPTION in OnRunDetection: {ex}");
                StatusText.Text = $"ERROR: {ex.Message}";
            }
        }

        private void OnShowOnMap(object? s, RoutedEventArgs e)
        {
            if (_batchList.Count == 0) return;

            if (_map == null)
            {
                _map = new Mapsui.Map();
                _map.Layers.Add(OpenStreetMap.CreateTileLayer("OSM Base"));

                _mapControl = new MapControl { Map = _map };
                MapControlHost.Child = _mapControl;
            }

            const double startLon = 6.60, startLat = 52.40;
            const double endLon = 6.62, endLat = 52.40;

            var features = new List<IFeature>();
            var pathCoords = new List<Coordinate>();

            for (int i = 0; i < _batchList.Count; i++)
            {
                var t = _batchList.Count > 1 ? (double)i / (_batchList.Count - 1) : 0;
                var lon = startLon + (endLon - startLon) * t;
                var lat = startLat + (endLat - startLat) * t;
                var (x, y) = SphericalMercator.FromLonLat(lon, lat);
                pathCoords.Add(new Coordinate(x, y));

                var r = _batchList[i];
                if (r.Detections.Count == 0) continue;

                var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
                var point = factory.CreatePoint(new Coordinate(x, y));
                var pf = new GeometryFeature { Geometry = point };
                pf["label"] = $"{System.IO.Path.GetFileName(r.ImagePath)}: {r.Detections.Count} detection(s)";
                var pinColor = _activeModel == WildlifeModelType.RoeDeer
                    ? new Mapsui.Styles.Color(220, 38, 38)
                    : new Mapsui.Styles.Color(34, 197, 94);
                pf.Styles.Add(new Mapsui.Styles.SymbolStyle
                {
                    Fill = new Mapsui.Styles.Brush(pinColor),
                    SymbolScale = 0.8,
                });
                features.Add(pf);
            }

            var pinLayer = new MemoryLayer
            {
                Name = "Wildlife Detections (simulated GPS)",
                Features = features,
                IsMapInfoLayer = true,
            };

            var toRemove = _map.Layers.Where(l => l.Name == "Wildlife Detections (simulated GPS)" || l.Name == "Simulated Flight Path").ToList();
            foreach (var l in toRemove) _map.Layers.Remove(l);

            if (pathCoords.Count >= 2)
            {
                var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
                var line = factory.CreateLineString(pathCoords.ToArray());
                var lineFeature = new GeometryFeature { Geometry = line };
                lineFeature.Styles.Add(new Mapsui.Styles.VectorStyle
                {
                    Line = new Mapsui.Styles.Pen(new Mapsui.Styles.Color(139, 92, 246), 2f)
                });
                var pathLayer = new MemoryLayer
                {
                    Name = "Simulated Flight Path",
                    Features = new List<IFeature> { lineFeature },
                };
                _map.Layers.Add(pathLayer);
            }

            _map.Layers.Add(pinLayer);

            MapPanelBorder.IsVisible = true;
            if (pathCoords.Count > 0)
            {
                var (cx, cy) = SphericalMercator.FromLonLat(
                    (startLon + endLon) / 2, (startLat + endLat) / 2);
                _map.Navigator.CenterOnAndZoomTo(new MPoint(cx, cy), _map.Navigator.Resolutions[14]);
            }
            _mapControl?.Refresh();

            StatusText.Text = $"Showing {features.Count} detection pin(s) on a simulated flight path.";
        }
    }
}
