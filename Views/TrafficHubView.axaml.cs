using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
namespace InfraDroneDesktop.Views;

public partial class TrafficHubView : UserControl
{
    private const string Base = "/home/sam/opendd_dataset/";
    private const string PythonExe = "/home/sam/miniconda3/bin/python3";

    // (Display name, script filename, output image filename)
    private static readonly (string Label, string Script, string Image)[] Charts = new[]
    {
        ("Safety Score", "safety_score.py", "safety_score.png"),
        ("Near-Miss Hotspot", "analytics_near_miss_hotspot.py", "analytics_near_miss_hotspot.png"),
        ("Harsh Braking", "analytics_harsh_braking.py", "analytics_harsh_braking.png"),
        ("Yielding Behavior", "analytics_yielding_behavior.py", "analytics_yielding_behavior.png"),
        ("Gap Acceptance", "analytics_gap_acceptance.py", "analytics_gap_acceptance.png"),
        ("Time Headway", "analytics_time_headway.py", "analytics_time_headway.png"),
        ("Queue Wait", "analytics_queue_wait.py", "analytics_queue_wait.png"),
        ("Pedestrian Hesitation", "analytics_pedestrian_hesitation.py", "analytics_pedestrian_hesitation.png"),
        ("Pedestrian Accel. Spikes", "analytics_pedestrian_acceleration_spikes.py", "analytics_pedestrian_acceleration_spikes.png"),
        ("Path Deviation", "analytics_path_deviation.py", "analytics_path_deviation.png"),
        ("Speed Heatmap", "analytics_speed_heatmap.py", "analytics_speed_heatmap.png"),
        ("Desire Paths", "analytics_desire_paths.py", "analytics_desire_paths.png"),
        ("Traffic Volume", "analytics_traffic_volume.py", "analytics_traffic_volume.png"),
        ("Object Size Check", "analytics_object_size_check.py", "analytics_object_size_check.png"),
        ("Trajectory Map", "trajectory_map.py", "trajectory_map.png"),
        ("Near-Miss Detection", "near_miss_detection.py", "near_miss_detection.png"),
        ("Pedestrian Behavior Profile", "pedestrian_behavior_profile.py", "pedestrian_behavior_profile.png"),
    };

    public TrafficHubView()
    {
        InitializeComponent();
        foreach (var c in Charts) ChartCombo.Items.Add(c.Label);
        Loaded += (_, _) => LoadCurrentChartImage();
    }

    private (string Label, string Script, string Image) CurrentChart =>
        Charts[Math.Max(0, ChartCombo.SelectedIndex)];

    private void OnChartSelected(object? s, SelectionChangedEventArgs e) => LoadCurrentChartImage();

    private void LoadCurrentChartImage()
    {
        var imgPath = Path.Combine(Base, CurrentChart.Image);
        if (File.Exists(imgPath))
        {
            try
            {
                ChartImage.Source = new Bitmap(imgPath);
                StatusText.Text = $"Loaded {CurrentChart.Image} ({File.GetLastWriteTime(imgPath):g})";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Failed to load image: {ex.Message}";
            }
        }
        else
        {
            ChartImage.Source = null;
            StatusText.Text = "No chart generated yet -- click Regenerate.";
        }
    }

    private async void OnRegenerate(object? s, RoutedEventArgs e)
    {
        var chart = CurrentChart;
        BtnRegenerate.IsEnabled = false;
        StatusText.Text = $"Running {chart.Script}...";
        var scriptPath = Path.Combine(Base, chart.Script);
        var psi = new ProcessStartInfo
        {
            FileName = PythonExe,
            Arguments = $"\"{scriptPath}\"",
            WorkingDirectory = Base,
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
            if (proc.ExitCode == 0)
            {
                LoadCurrentChartImage();
                StatusText.Text = $"Regenerated {chart.Image}.";
            }
            else
            {
                StatusText.Text = $"Script failed: {stderr.Substring(0, Math.Min(300, stderr.Length))}";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to run script: {ex.Message}";
        }
        finally
        {
            BtnRegenerate.IsEnabled = true;
        }
    }
}
