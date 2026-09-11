using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using InfraDroneDesktop.Services;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
namespace InfraDroneDesktop.Views;
public partial class TrafficIntelligenceView : UserControl
{
    private AerialDetectionView? _aerialDetectionView;
    private TrafficPlayerView? _trafficPlayerView;
    private TrafficHubView? _trafficHubView;
    private StoryModeView? _storyModeView;
    private ConflictDetectionView? _conflictDetectionView;
    public TrafficIntelligenceView()
    {
        InitializeComponent();
        _aerialDetectionView = new AerialDetectionView();
        ContentHost.Children.Add(_aerialDetectionView);
    }

    // Passthrough for MainWindow's one-click demo prep -- Aerial Detection
    // lives nested inside this view now, not a direct MainWindow field.
    public Task PreloadAerialDemoAsync() => _aerialDetectionView?.PreloadDemoAsync() ?? Task.CompletedTask;
    private void OnSubNav(object? s, RoutedEventArgs e)
    {
        var btn = s as Button;
        ContentHost.Children.Clear();
        if (btn == BtnSubAerial)
        {
            if (_aerialDetectionView == null) _aerialDetectionView = new AerialDetectionView();
            ContentHost.Children.Add(_aerialDetectionView);
        }
        if (btn == BtnSubReplay)
        {
            if (_trafficPlayerView == null) _trafficPlayerView = new TrafficPlayerView();
            ContentHost.Children.Add(_trafficPlayerView);
        }
        if (btn == BtnSubHub)
        {
            if (_trafficHubView == null) _trafficHubView = new TrafficHubView();
            ContentHost.Children.Add(_trafficHubView);
        }
        if (btn == BtnSubStory)
        {
            if (_storyModeView == null) _storyModeView = new StoryModeView();
            ContentHost.Children.Add(_storyModeView);
        }
        if (btn == BtnSubConflict)
        {
            if (_conflictDetectionView == null)
            {
                _conflictDetectionView = new ConflictDetectionView();
                // Precise seek-to-timestamp is a follow-up (TrafficPlayerView has
                // no public seek method yet) -- Play just switches to Replay for now.
                _conflictDetectionView.PlayRequested += OnConflictPlayRequested;
            }
            ContentHost.Children.Add(_conflictDetectionView);
        }
        // Theme-aware: look up current AppAccentBg/AppPanelBg etc. rather than
        // hardcoding hex, so the active/inactive sub-nav colors respect light/dark mode.
        IBrush ResolveBrush(string key) =>
            this.TryFindResource(key, out var res) && res is IBrush brush ? brush : Brushes.Transparent;

        foreach (var b in new[] { BtnSubAerial, BtnSubReplay, BtnSubHub, BtnSubStory, BtnSubConflict })
        {
            bool active = b == btn;
            b.Background = ResolveBrush(active ? "AppAccentBg" : "AppPanelBg");
            b.Foreground = ResolveBrush(active ? "AppAccentFg" : "AppTextMuted");
        }
    }

    // Maps a conflict's roundabout to its db/pgw/image assets on disk.
    private (string db, string pgw, string img) ResolveRecordingAssets(string roundabout)
    {
        if (roundabout == "rdb1")
        {
            return (
                "/home/sam/opendd_dataset/example_data/rdb1_4.sqlite",
                "/home/sam/opendd_dataset/example_data/geo-referenced_images_rdb1/rdb1.pgw",
                "/home/sam/opendd_dataset/example_data/geo-referenced_images_rdb1/rdb1.png"
            );
        }
        return (
            "/home/sam/opendd_dataset/rdb2/rdb2/trajectories_rdb2_v3.sqlite",
            "/home/sam/opendd_dataset/rdb2/rdb2/geo-referenced_images_rdb2/rdb2.pgw",
            "/home/sam/opendd_dataset/rdb2/rdb2/geo-referenced_images_rdb2/rdb2.png"
        );
    }

    // Exports the conflict's recording (cached per table so repeat plays are
    // instant), loads it into Traffic Replay, seeks to the exact conflict
    // moment, and switches to that sub-tab.
    private async void OnConflictPlayRequested(ConflictEvent ev)
    {
        var (db, pgw, img) = ResolveRecordingAssets(ev.Roundabout);
        var outJson = "/home/sam/opendd_dataset/player_data_" + ev.Table + ".json";

        if (_trafficPlayerView == null) _trafficPlayerView = new TrafficPlayerView();

        if (!File.Exists(outJson))
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/home/sam/miniconda3/bin/python3",
                WorkingDirectory = "/home/sam/opendd_dataset",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("export_player_data_generic.py");
            psi.ArgumentList.Add(db);
            psi.ArgumentList.Add(ev.Table);
            psi.ArgumentList.Add(pgw);
            psi.ArgumentList.Add(outJson);
            try
            {
                var proc = Process.Start(psi);
                await proc!.WaitForExitAsync();
            }
            catch
            {
                // Export failed -- LoadRecording below will surface "Data file not found"
                // via TrafficPlayerView's own status text rather than failing silently.
            }
        }

        _trafficPlayerView.LoadRecording(outJson, img, new HashSet<int> { ev.ObjIdA, ev.ObjIdB });
        _trafficPlayerView.SeekToTime(ev.TStart);
        OnSubNav(BtnSubReplay, new RoutedEventArgs());
    }
}
