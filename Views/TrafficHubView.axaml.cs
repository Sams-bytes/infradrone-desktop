using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
namespace InfraDroneDesktop.Views;

public partial class TrafficHubView : UserControl
{
    private const string Base = "/home/sam/opendd_dataset/";
    private const string PythonExe = "/home/sam/miniconda3/bin/python3";

    private const string Rdb1Db = "/home/sam/opendd_dataset/example_data/rdb1_4.sqlite";
    private const string Rdb1Table = "rdb1_4";
    private const string Rdb1Img = "/home/sam/opendd_dataset/example_data/geo-referenced_images_rdb1/rdb1.png";
    private const string Rdb1Pgw = "/home/sam/opendd_dataset/example_data/geo-referenced_images_rdb1/rdb1.pgw";

    private const string Rdb2Db = "/home/sam/opendd_dataset/rdb2/rdb2/trajectories_rdb2_v3.sqlite";
    private const string Rdb2Img = "/home/sam/opendd_dataset/rdb2/rdb2/geo-referenced_images_rdb2/rdb2.png";
    private const string Rdb2Pgw = "/home/sam/opendd_dataset/rdb2/rdb2/geo-referenced_images_rdb2/rdb2.pgw";

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
        ("Summary Chart", "summary_chart.py", "summary_chart.png"),
    };

    private enum AnalysisType { FlowRate, FlowVectorMap, VvNearMiss, HarshBrakingGeneric }
    private static readonly (string Label, AnalysisType Type, string Script)[] Analyses = new[]
    {
        ("Flow Rate Over Time", AnalysisType.FlowRate, "analytics_flow_rate.py"),
        ("Flow Vector Map", AnalysisType.FlowVectorMap, "analytics_flow_vector_map.py"),
        ("Vehicle-Vehicle Near-Miss", AnalysisType.VvNearMiss, "analytics_vehicle_vehicle_near_miss.py"),
        ("Harsh Braking (Generic)", AnalysisType.HarshBrakingGeneric, "analytics_harsh_braking_generic.py"),
    };

    public TrafficHubView()
    {
        InitializeComponent();
        foreach (var c in Charts) ChartCombo.Items.Add(c.Label);
        SceneCombo.Items.Add("rdb1");
        SceneCombo.Items.Add("rdb2");
        SceneCombo.SelectedIndex = 0;
        foreach (var a in Analyses) AnalysisCombo.Items.Add(a.Label);
        AnalysisCombo.SelectedIndex = 0;
        Loaded += (_, _) => LoadCurrentChartImage();
    }

    private void OnSubNav(object? s, RoutedEventArgs e)
    {
        var btn = s as Button;
        ChartsPanel.IsVisible = btn == BtnSubCharts;
        MultiScenePanel.IsVisible = btn == BtnSubMultiScene;
        VideoPanel.IsVisible = btn == BtnSubVideo;
        foreach (var b in new[] { BtnSubCharts, BtnSubMultiScene, BtnSubVideo })
        {
            bool active = b == btn;
            b.Background = new SolidColorBrush(Color.Parse(active ? "#0d3d2e" : "#1a2637"));
            b.Foreground = new SolidColorBrush(Color.Parse(active ? "#0d9e75" : "#94a3b8"));
        }
        ChartImage.Source = null;
        StatusText.Text = "";
    }

    private (string Label, string Script, string Image) CurrentChart =>
        Charts[Math.Max(0, ChartCombo.SelectedIndex)];

    private void OnChartSelected(object? s, SelectionChangedEventArgs e) => LoadCurrentChartImage();

    private void LoadCurrentChartImage() => LoadImageIfExists(Path.Combine(Base, CurrentChart.Image), CurrentChart.Image);

    private void LoadImageIfExists(string imgPath, string label)
    {
        if (File.Exists(imgPath))
        {
            try
            {
                ChartImage.Source = new Bitmap(imgPath);
                StatusText.Text = $"{label} ({File.GetLastWriteTime(imgPath):g})";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Failed to load image: {ex.Message}";
            }
        }
        else
        {
            ChartImage.Source = null;
            StatusText.Text = "Not generated yet -- click Regenerate/Run Analysis.";
        }
    }

    private async void OnRegenerate(object? s, RoutedEventArgs e)
    {
        var chart = CurrentChart;
        BtnRegenerate.IsEnabled = false;
        StatusText.Text = $"Running {chart.Script}...";
        var ok = await RunScriptAsync(chart.Script, "");
        if (ok) LoadCurrentChartImage();
        BtnRegenerate.IsEnabled = true;
    }

    private void OnSceneChanged(object? s, SelectionChangedEventArgs e)
    {
        bool isRdb2 = SceneCombo.SelectedIndex == 1;
        SegmentCombo.IsVisible = isRdb2;
        if (isRdb2 && SegmentCombo.Items.Count == 0)
        {
            for (int i = 154; i <= 209; i++) SegmentCombo.Items.Add($"rdb2_{i}");
            SegmentCombo.SelectedIndex = 0;
        }
    }

    private async void OnRegenerateMultiScene(object? s, RoutedEventArgs e)
    {
        bool isRdb2 = SceneCombo.SelectedIndex == 1;
        string dbPath = isRdb2 ? Rdb2Db : Rdb1Db;
        string table = isRdb2 ? (SegmentCombo.SelectedItem as string ?? "rdb2_154") : Rdb1Table;
        string sceneLabel = isRdb2 ? table : "rdb1";
        var analysis = Analyses[Math.Max(0, AnalysisCombo.SelectedIndex)];
        string outImage = $"multiscene_{analysis.Type}_{sceneLabel}.png";
        string outPath = Path.Combine(Base, outImage);

        string args;
        if (analysis.Type == AnalysisType.FlowVectorMap)
        {
            string img = isRdb2 ? Rdb2Img : Rdb1Img;
            string pgw = isRdb2 ? Rdb2Pgw : Rdb1Pgw;
            args = $"\"{dbPath}\" \"{table}\" \"{img}\" \"{pgw}\" \"{outPath}\" \"{sceneLabel}\"";
        }
        else
        {
            args = $"\"{dbPath}\" \"{table}\" \"{outPath}\" \"{sceneLabel}\"";
        }

        BtnRegenerateMultiScene.IsEnabled = false;
        StatusText.Text = $"Running {analysis.Script} on {sceneLabel}...";
        var ok = await RunScriptAsync(analysis.Script, args);
        if (ok) LoadImageIfExists(outPath, outImage);
        BtnRegenerateMultiScene.IsEnabled = true;
    }

    private void OnPlayVideo(object? s, RoutedEventArgs e)
    {
        var path = Path.Combine(Base, "trajectory_animation_preview.mp4");
        if (!File.Exists(path))
        {
            StatusText.Text = "No video yet -- click Regenerate first.";
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        StatusText.Text = "Opening video in system player...";
    }

    private async void OnRegenerateVideo(object? s, RoutedEventArgs e)
    {
        BtnRegenerateVideo.IsEnabled = false;
        StatusText.Text = "Rendering trajectory animation (~70s)...";
        var ok = await RunScriptAsync("trajectory_animation.py", "");
        StatusText.Text = ok ? "Video regenerated. Click Play to view." : "Video generation failed.";
        BtnRegenerateVideo.IsEnabled = true;
    }

    private async Task<bool> RunScriptAsync(string scriptName, string args)
    {
        var scriptPath = Path.Combine(Base, scriptName);
        var psi = new ProcessStartInfo
        {
            FileName = PythonExe,
            Arguments = $"\"{scriptPath}\" {args}",
            WorkingDirectory = Base,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        try
        {
            var proc = Process.Start(psi);
            var stderr = await proc!.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode == 0) return true;
            StatusText.Text = $"Script failed: {stderr.Substring(0, Math.Min(300, stderr.Length))}";
            return false;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to run script: {ex.Message}";
            return false;
        }
    }
}
