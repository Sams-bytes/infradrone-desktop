using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace InfraDroneDesktop.Views
{
    public partial class TrafficBehaviorView : UserControl
    {
        private readonly string _trajectoryMapPath =
            "/home/sam/opendd_dataset/trajectory_map.png";
        private readonly string _nearMissPath =
            "/home/sam/opendd_dataset/near_miss_detection.png";
        private readonly string _behaviorProfilePath =
            "/home/sam/opendd_dataset/pedestrian_behavior_profile.png";

        public TrafficBehaviorView()
        {
            InitializeComponent();
            LoadImages();
        }

        private void LoadImages()
        {
            LoadInto(_trajectoryMapPath, TrajectoryMapImage, TrajectoryMapStatus);
            LoadInto(_nearMissPath, NearMissImage, NearMissStatus);
            LoadInto(_behaviorProfilePath, BehaviorProfileImage, BehaviorProfileStatus);
        }

        private void LoadInto(string path, Image img, TextBlock status)
        {
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

        private void OnOpenTrajectoryMap(object? sender, RoutedEventArgs e) => OpenInSystemViewer(_trajectoryMapPath);
        private void OnOpenNearMiss(object? sender, RoutedEventArgs e) => OpenInSystemViewer(_nearMissPath);
        private void OnOpenBehaviorProfile(object? sender, RoutedEventArgs e) => OpenInSystemViewer(_behaviorProfilePath);

        private void OpenInSystemViewer(string path)
        {
            if (!File.Exists(path)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "xdg-open", Arguments = path,
                UseShellExecute = true
            });
        }
    }
}
