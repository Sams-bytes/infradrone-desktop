using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace InfraDroneDesktop.Views
{
    public partial class TrafficAnalyticsView : UserControl
    {
        private const string Base = "/home/sam/opendd_dataset/";

        private readonly (string Path, string ImgName, string StatusName)[] _charts = new[]
        {
            (Base + "safety_score.png", "SafetyScoreImage", "SafetyScoreStatus"),
            (Base + "analytics_near_miss_hotspot.png", "NearMissHotspotImage", "NearMissHotspotStatus"),
            (Base + "analytics_harsh_braking.png", "HarshBrakingImage", "HarshBrakingStatus"),
            (Base + "analytics_yielding_behavior.png", "YieldingImage", "YieldingStatus"),
            (Base + "analytics_gap_acceptance.png", "GapAcceptanceImage", "GapAcceptanceStatus"),
            (Base + "analytics_time_headway.png", "TimeHeadwayImage", "TimeHeadwayStatus"),
            (Base + "analytics_queue_wait.png", "QueueWaitImage", "QueueWaitStatus"),
            (Base + "analytics_pedestrian_hesitation.png", "HesitationImage", "HesitationStatus"),
            (Base + "analytics_pedestrian_acceleration_spikes.png", "AccelSpikeImage", "AccelSpikeStatus"),
            (Base + "analytics_path_deviation.png", "PathDeviationImage", "PathDeviationStatus"),
            (Base + "analytics_speed_heatmap.png", "SpeedHeatmapImage", "SpeedHeatmapStatus"),
            (Base + "analytics_desire_paths.png", "DesirePathsImage", "DesirePathsStatus"),
            (Base + "analytics_traffic_volume.png", "TrafficVolumeImage", "TrafficVolumeStatus"),
            (Base + "analytics_object_size_check.png", "ObjectSizeImage", "ObjectSizeStatus"),
        };

        public TrafficAnalyticsView()
        {
            InitializeComponent();
            LoadAll();
        }

        private void LoadAll()
        {
            foreach (var (path, imgName, statusName) in _charts)
            {
                var img = this.FindControl<Image>(imgName);
                var status = this.FindControl<TextBlock>(statusName);
                if (img == null || status == null) continue;

                if (File.Exists(path))
                {
                    using var stream = File.OpenRead(path);
                    img.Source = new Avalonia.Media.Imaging.Bitmap(stream);
                    img.IsVisible = true;
                    status.IsVisible = false;
                }
                else
                {
                    status.Text = "File not found: " + path;
                }
            }
        }

        private void OpenInSystemViewer(string path)
        {
            if (!File.Exists(path)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "xdg-open", Arguments = path,
                UseShellExecute = true
            });
        }

        private void OnOpenSafetyScore(object? s, RoutedEventArgs e) => OpenInSystemViewer(Base + "safety_score.png");
        private void OnOpenNearMissHotspot(object? s, RoutedEventArgs e) => OpenInSystemViewer(Base + "analytics_near_miss_hotspot.png");
        private void OnOpenHarshBraking(object? s, RoutedEventArgs e) => OpenInSystemViewer(Base + "analytics_harsh_braking.png");
        private void OnOpenYielding(object? s, RoutedEventArgs e) => OpenInSystemViewer(Base + "analytics_yielding_behavior.png");
        private void OnOpenGapAcceptance(object? s, RoutedEventArgs e) => OpenInSystemViewer(Base + "analytics_gap_acceptance.png");
        private void OnOpenTimeHeadway(object? s, RoutedEventArgs e) => OpenInSystemViewer(Base + "analytics_time_headway.png");
        private void OnOpenQueueWait(object? s, RoutedEventArgs e) => OpenInSystemViewer(Base + "analytics_queue_wait.png");
        private void OnOpenHesitation(object? s, RoutedEventArgs e) => OpenInSystemViewer(Base + "analytics_pedestrian_hesitation.png");
        private void OnOpenAccelSpike(object? s, RoutedEventArgs e) => OpenInSystemViewer(Base + "analytics_pedestrian_acceleration_spikes.png");
        private void OnOpenPathDeviation(object? s, RoutedEventArgs e) => OpenInSystemViewer(Base + "analytics_path_deviation.png");
        private void OnOpenSpeedHeatmap(object? s, RoutedEventArgs e) => OpenInSystemViewer(Base + "analytics_speed_heatmap.png");
        private void OnOpenDesirePaths(object? s, RoutedEventArgs e) => OpenInSystemViewer(Base + "analytics_desire_paths.png");
        private void OnOpenTrafficVolume(object? s, RoutedEventArgs e) => OpenInSystemViewer(Base + "analytics_traffic_volume.png");
        private void OnOpenObjectSize(object? s, RoutedEventArgs e) => OpenInSystemViewer(Base + "analytics_object_size_check.png");
    }
}
