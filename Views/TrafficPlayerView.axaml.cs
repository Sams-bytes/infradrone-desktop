using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace InfraDroneDesktop.Views
{
    public partial class TrafficPlayerView : UserControl
    {
        private const string JsonPath = "/home/sam/opendd_dataset/player_data.json";
        private const string ImagePath = "/home/sam/opendd_dataset/example_data/geo-referenced_images_rdb1/rdb1.png";

        private List<string> _frameTimes = new();
        private Dictionary<string, List<PlayerObj>> _frames = new();
        private int _currentIndex = 0;
        private DispatcherTimer? _timer;
        private bool _isPlaying = false;

        private static readonly Dictionary<string, string> ClassColors = new()
        {
            ["Pedestrian"] = "#dc2626", ["Bicycle"] = "#eab308", ["Car"] = "#3E8E7E",
            ["Bus"] = "#8b5cf6", ["Medium Vehicle"] = "#3E8E7E", ["Heavy Vehicle"] = "#3E8E7E",
            ["Motorcycle"] = "#eab308", ["Trailer"] = "#94a3b8",
        };
        private static readonly HashSet<int> HighlightIds = new() { 910, 942 };

        private static readonly Dictionary<string, string[]> FilterGroups = new()
        {
            ["Pedestrian"] = new[] { "Pedestrian" },
            ["Bicycle"] = new[] { "Bicycle", "Motorcycle" },
            ["Car"] = new[] { "Car", "Van", "Truck", "Medium Vehicle", "Heavy Vehicle" },
            ["Bus"] = new[] { "Bus" },
            ["Trailer"] = new[] { "Trailer" },
        };

        private bool IsClassVisible(string cls)
        {
            foreach (var (filterName, classes) in FilterGroups)
            {
                if (Array.IndexOf(classes, cls) < 0) continue;
                var checkBox = filterName switch
                {
                    "Pedestrian" => FilterPedestrian,
                    "Bicycle" => FilterBicycle,
                    "Car" => FilterCar,
                    "Bus" => FilterBus,
                    "Trailer" => FilterTrailer,
                    _ => null
                };
                return checkBox?.IsChecked ?? true;
            }
            return true; // unknown class -- show by default rather than silently hide
        }

        private void OnFilterChanged(object? sender, RoutedEventArgs e)
        {
            DrawFrame(_currentIndex);
        }

        private record PlayerObj(int Id, string Class, double X, double Y, double V);

        public TrafficPlayerView()
        {
            InitializeComponent();
            LoadData();
        }

        private void LoadData()
        {
            if (!File.Exists(JsonPath))
            {
                StatusText.Text = "Data file not found: " + JsonPath;
                return;
            }
            if (File.Exists(ImagePath))
            {
                using var stream = File.OpenRead(ImagePath);
                var bmp = new Avalonia.Media.Imaging.Bitmap(stream);
                BackgroundImage.Source = bmp;
                BackgroundImage.Width = bmp.PixelSize.Width;
                BackgroundImage.Height = bmp.PixelSize.Height;
                OverlayCanvas.Width = bmp.PixelSize.Width;
                OverlayCanvas.Height = bmp.PixelSize.Height;
            }

            var json = File.ReadAllText(JsonPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            _frameTimes = root.GetProperty("frame_times").EnumerateArray()
                .Select(e => e.GetString()!).ToList();

            var framesElem = root.GetProperty("frames");
            foreach (var t in _frameTimes)
            {
                var arr = framesElem.GetProperty(t);
                var objs = new List<PlayerObj>();
                foreach (var o in arr.EnumerateArray())
                {
                    objs.Add(new PlayerObj(
                        o.GetProperty("id").GetInt32(),
                        o.GetProperty("class").GetString()!,
                        o.GetProperty("x").GetDouble(),
                        o.GetProperty("y").GetDouble(),
                        o.GetProperty("v").GetDouble()));
                }
                _frames[t] = objs;
            }

            TimeSlider.Maximum = _frameTimes.Count - 1;
            TimeSlider.Value = 0;
            StatusText.Text = $"Loaded {_frameTimes.Count} frames.";
            DrawFrame(0);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += (s, e) => Advance();
        }

        private void Advance()
        {
            if (_currentIndex < _frameTimes.Count - 1)
            {
                _currentIndex++;
                TimeSlider.Value = _currentIndex;
            }
            else
            {
                PausePlayback();
            }
        }

        private void OnPlayPause(object? sender, RoutedEventArgs e)
        {
            if (_isPlaying) PausePlayback(); else StartPlayback();
        }

        private void StartPlayback()
        {
            _isPlaying = true;
            PlayPauseButton.Content = "⏸ Pause";
            _timer?.Start();
        }

        private void PausePlayback()
        {
            _isPlaying = false;
            PlayPauseButton.Content = "▶ Play";
            _timer?.Stop();
        }

        private void OnSliderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            _currentIndex = (int)TimeSlider.Value;
            DrawFrame(_currentIndex);
        }

        private void DrawFrame(int index)
        {
            if (index < 0 || index >= _frameTimes.Count) return;
            var t = _frameTimes[index];
            var objs = _frames[t];

            OverlayCanvas.Children.Clear();
            TimeLabel.Text = $"{t}s / {_frameTimes.Last()}s";

            foreach (var o in objs)
            {
                if (!IsClassVisible(o.Class)) continue;
                var color = ClassColors.TryGetValue(o.Class, out var c) ? c : "#ffffff";
                bool isHighlight = HighlightIds.Contains(o.Id);
                double size = isHighlight ? 195 : (o.Class == "Pedestrian" ? 120 : 96);

                var dot = new Ellipse
                {
                    Width = size, Height = size,
                    Fill = new SolidColorBrush(Color.Parse(color)),
                    Stroke = new SolidColorBrush(Colors.White),
                    StrokeThickness = isHighlight ? 2 : 0.8
                };
                Canvas.SetLeft(dot, o.X - size / 2);
                Canvas.SetTop(dot, o.Y - size / 2);
                OverlayCanvas.Children.Add(dot);

                var labelText = isHighlight ? $"{o.Class} {o.Id}" : o.Id.ToString();
                var label = new TextBlock
                {
                    Text = labelText,
                    FontSize = isHighlight ? 48 : 34,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Colors.White),
                    Background = new SolidColorBrush(Color.Parse("#a0000000")),
                    Padding = new Avalonia.Thickness(6, 2)
                };
                Canvas.SetLeft(label, o.X + size / 2 + 6);
                Canvas.SetTop(label, o.Y - size / 2);
                OverlayCanvas.Children.Add(label);
            }
        }
    }
}
