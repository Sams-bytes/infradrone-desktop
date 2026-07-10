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

namespace InfraDroneDesktop.Views
{
    public class AerialBatchResult
    {
        public string ImagePath { get; set; } = "";
        public List<AerialDetection> Detections { get; set; } = new();
    }

    public partial class AerialDetectionView : UserControl
    {
        private readonly AerialDetectionService _ai = new AerialDetectionService();
        private string _imagePath = "";
        private readonly ObservableCollection<AerialBatchResult> _batchResults = new();
        private List<AerialBatchResult> _batchList = new();
        private int _currentBatchIndex = -1;

        public AerialDetectionView()
        {
            InitializeComponent();
        }

        private async void OnLoadModel(object? s, RoutedEventArgs e)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select trained ONNX model",
                FileTypeFilter = new[] { new FilePickerFileType("ONNX model") { Patterns = new[] { "*.onnx" } } }
            });
            if (files.Count == 0) return;

            var path = files[0].Path.LocalPath;
            if (_ai.LoadModel(path))
            {
                ModelStatusText.Text = $"Loaded: {_ai.ModelName}";
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
                    var dets = _ai.Detect(imagePaths[i]);
                    _batchResults.Add(new AerialBatchResult { ImagePath = imagePaths[i], Detections = dets });
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
            ShowBatchImage();

            StatusText.Text = $"Done: {imagePaths.Count} images processed.";
        }

        private void ShowResult(AerialBatchResult r)
        {
            _imagePath = r.ImagePath;
            using var annotated = _ai.DrawDetections(r.ImagePath, r.Detections);
            using var data = annotated.Encode(SKEncodedImageFormat.Png, 100);
            using var ms = new MemoryStream(data.ToArray());
            ImageDisplay.Source = new Bitmap(ms);
            StatusText.Text = $"{Path.GetFileName(r.ImagePath)}: {r.Detections.Count} detection(s)";
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
            if (_currentBatchIndex > 0)
            {
                _currentBatchIndex--;
                ShowBatchImage();
            }
        }

        private void OnNextImage(object? s, RoutedEventArgs e)
        {
            if (_currentBatchIndex < _batchList.Count - 1)
            {
                _currentBatchIndex++;
                ShowBatchImage();
            }
        }

        private void OnRunDetection(object? s, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_imagePath) || !_ai.IsLoaded)
            {
                StatusText.Text = "Load a model and select an image first.";
                return;
            }
            var dets = _ai.Detect(_imagePath);
            using var annotated = _ai.DrawDetections(_imagePath, dets);
            using var data = annotated.Encode(SKEncodedImageFormat.Png, 100);
            using var ms = new MemoryStream(data.ToArray());
            ImageDisplay.Source = new Bitmap(ms);
            StatusText.Text = $"{dets.Count} detection(s) found.";
        }
    }
}
