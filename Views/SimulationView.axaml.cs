using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using InfraDroneDesktop.Services;

namespace InfraDroneDesktop.Views;

public partial class SimulationView : UserControl
{
    // Asked of MainWindow, which owns the Connect logic and the Flight View.
    public event Action? ConnectRequested;
    public event Action? OpenMissionPlannerRequested;
    public event Action? Open3DViewRequested;

    private readonly SimulatorService _sim = SimulatorService.Instance;
    private readonly SimDrillService _drills = SimDrillService.Instance;
    private readonly System.Text.StringBuilder _drillLog = new();
    private readonly DispatcherTimer _timer;
    private bool _starting;

    // Groningen city centre, same reference point WeatherView uses.
    // Ground altitude is an approximate few metres above NAP (sea level);
    // it only sets the simulated home point.
    private const double GroningenLat = 53.2194;
    private const double GroningenLon = 6.5665;
    private const double GroningenAltM = 5;
    private const double GroningenHdg = 0;

    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#22c55e"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#ef4444"));
    private static readonly IBrush Grey = new SolidColorBrush(Color.Parse("#475569"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#f59e0b"));

    public SimulationView()
    {
        InitializeComponent();
        PresetBox.SelectedIndex = 0;
        ApplyGroningen();

        _sim.StatusMessage += msg => Dispatcher.UIThread.Post(() => MessageText.Text = msg);
        _drills.Output += line => Dispatcher.UIThread.Post(() => AppendDrillLog(line));
        _drills.Finished += res => Dispatcher.UIThread.Post(() => ShowDrillResult(res));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        RefreshStatus();
    }

    private void ApplyGroningen()
    {
        var inv = CultureInfo.InvariantCulture;
        LatBox.Text = GroningenLat.ToString("F5", inv);
        LonBox.Text = GroningenLon.ToString("F5", inv);
        AltBox.Text = GroningenAltM.ToString("F0", inv);
        HdgBox.Text = GroningenHdg.ToString("F0", inv);
    }

    private void OnPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LatBox == null) return; // fired during InitializeComponent
        if (PresetBox.SelectedIndex == 0) ApplyGroningen();
    }

    private static bool TryParse(string? s, out double v) =>
        double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out v);

    private async void OnStart(object? sender, RoutedEventArgs e)
    {
        if (!TryParse(LatBox.Text, out var lat) || lat < -90 || lat > 90 ||
            !TryParse(LonBox.Text, out var lon) || lon < -180 || lon > 180)
        {
            MessageText.Text = "Please enter a valid latitude (−90…90) and longitude (−180…180).";
            return;
        }
        if (!TryParse(AltBox.Text, out var alt)) alt = 0;
        if (!TryParse(HdgBox.Text, out var hdg)) hdg = 0;
        hdg = ((hdg % 360) + 360) % 360;

        _starting = true;
        BtnStart.IsEnabled = false;
        MessageText.Text = "Starting simulator… (about 10–20 seconds)";
        StateText.Text = "STARTING";
        StateText.Foreground = Amber;
        StateBadge.BorderBrush = Amber;

        var (ok, msg) = await _sim.StartAsync(lat, lon, alt, hdg);
        _starting = false;
        MessageText.Text = msg;
        RefreshStatus();

        if (ok && AutoOpenBox.IsChecked == true)
        {
            ConnectRequested?.Invoke();
            OpenMissionPlannerRequested?.Invoke();
        }
    }

    private async void OnStop(object? sender, RoutedEventArgs e)
    {
        // Undo a running drill's changes while the simulator can still hear us.
        if (_drills.IsRunning) await _drills.StopAsync();
        _sim.Stop();
        MessageText.Text = "Simulator stopped.";
        RefreshStatus();
    }

    private void OnOpenPlanner(object? sender, RoutedEventArgs e)
    {
        if (_sim.IsRunning) ConnectRequested?.Invoke();
        OpenMissionPlannerRequested?.Invoke();
    }

    private void OnTelemetry(object? sender, RoutedEventArgs e) => TelemetryWindow.ShowShared();

    private void OnOpen3D(object? sender, RoutedEventArgs e)
    {
        if (_sim.IsRunning) ConnectRequested?.Invoke();
        Open3DViewRequested?.Invoke();
    }

    private void RefreshStatus()
    {
        var running = _sim.IsRunning;
        DotArdu.Fill = running ? (_sim.ArduPlaneAlive ? Green : Red) : Grey;
        DotMav.Fill = running ? (_sim.MavProxyAlive ? Green : Red) : Grey;
        DotLink.Fill = running ? (_sim.PacketsFromApp > 0 ? Green : (_sim.RelayAlive ? Amber : Red)) : Grey;
        PacketText.Text = running
            ? $"  to app {_sim.PacketsToApp:N0} · from app {_sim.PacketsFromApp:N0}"
              + (_sim.PacketsFromApp == 0 ? "  (click Connect)" : "")
            : "";

        BtnStart.IsEnabled = !running && !_starting;
        BtnStop.IsEnabled = running;
        bool canDrill = running && !_starting && !_drills.IsRunning;
        BtnDrillSetup.IsEnabled = canDrill;
        BtnDrillTrafficRtl.IsEnabled = canDrill;
        BtnDrillTrafficClimb.IsEnabled = canDrill;
        BtnDrillRc.IsEnabled = canDrill;
        BtnDrillBattery.IsEnabled = canDrill;
        BtnDrillFence.IsEnabled = canDrill;
        BtnDrillLink.IsEnabled = canDrill && _linkDrill == null;
        BtnDrillStop.IsEnabled = _drills.IsRunning || _linkDrill != null;
        LockLocation(!running && !_starting);

        if (_starting) return; // keep the STARTING badge until StartAsync returns
        bool healthy = running && _sim.ArduPlaneAlive && _sim.MavProxyAlive;
        StateText.Text = running ? (healthy ? "RUNNING" : "FAULT") : "STOPPED";
        var b = running ? (healthy ? Green : Red) : Red;
        StateText.Foreground = b;
        StateBadge.BorderBrush = b;

        LogText.Text = running ? $"Logs: {_sim.ArduPlaneLog}  ·  {_sim.MavProxyLog}" : "";
    }

    private void LockLocation(bool editable)
    {
        PresetBox.IsEnabled = editable;
        LatBox.IsEnabled = editable;
        LonBox.IsEnabled = editable;
        AltBox.IsEnabled = editable;
        HdgBox.IsEnabled = editable;
    }

    // ------------------------------------------------------------------
    // Failsafe drills (tools/sim_drills.py via SimDrillService)
    // ------------------------------------------------------------------
    private void OnDrillSetup(object? s, RoutedEventArgs e) => RunDrill("setup", "Applying BVLOS failsafe settings");
    private void OnDrillTrafficRtl(object? s, RoutedEventArgs e) => RunDrill("traffic 5", "Traffic conflict (avoid by RTL)");
    private void OnDrillTrafficClimb(object? s, RoutedEventArgs e) => RunDrill("traffic 2", "Traffic conflict (avoid by climb/descend)");
    private void OnDrillRc(object? s, RoutedEventArgs e) => RunDrill("rcloss", "Radio link lost");
    private void OnDrillBattery(object? s, RoutedEventArgs e) => RunDrill("battery", "Battery low, then critical");
    private void OnDrillFence(object? s, RoutedEventArgs e) => RunDrill("fence", "Fence breach");

    private async void OnDrillStop(object? s, RoutedEventArgs e)
    {
        BtnDrillStop.IsEnabled = false;
        if (_linkDrill != null)
        {
            _linkDrill.Cancel();      // the drill's finally block restores the link
            return;
        }
        AppendDrillLog("Stopping the drill and restoring simulator settings ...");
        await _drills.StopAsync();
        SetDrillBadge("STOPPED", "#1e293b", "#94a3b8");
        DrillResultText.Text = "Drill stopped. Simulator settings restored.";
        RefreshStatus();
    }

    // Drill 6: degrade the relay's link to this app, so the Flight View link box
    // and the LINK DEGRADED / LINK LOST alerts can be seen working. Pure C#: it
    // only changes SimulatorService.DropPercent and always resets it to 0.
    private System.Threading.CancellationTokenSource? _linkDrill;

    private async void OnDrillLink(object? s, RoutedEventArgs e)
    {
        if (_linkDrill != null || !_sim.IsRunning) return;
        _linkDrill = new System.Threading.CancellationTokenSource();
        var ct = _linkDrill.Token;
        _drillLog.Clear(); DrillLog.Text = "";
        SetDrillBadge("RUNNING", "#3d2e0d", "#f59e0b");
        DrillResultText.Text = "Bad radio link ...";
        DrillResultText.Foreground = new SolidColorBrush(Color.Parse("#cbd5e1"));
        AppendDrillLog("== Bad radio link (what this app receives) ==");
        AppendDrillLog("Open Mission Planner to watch the LINK box (top left of the map).");
        RefreshStatus();
        try
        {
            (int pct, int secs, string what)[] phases =
            {
                (10, 15, "10% packet loss - expect LINK: FAIR"),
                (40, 15, "40% packet loss - expect LINK: POOR and a LINK DEGRADED alert after 5 s"),
                (100, 12, "100% loss (link cut) - expect LINK: LOST and the LINK LOST alert"),
            };
            foreach (var (pct, secs, what) in phases)
            {
                _sim.DropPercent = pct;
                AppendDrillLog($"{what} ({secs} s)");
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(secs), ct);
            }
            _sim.DropPercent = 0;
            AppendDrillLog("Link restored - expect LINK: GOOD and LINK RESTORED within a few seconds.");
            ShowDrillResult("RESULT: DONE - link drill finished. Note: only this app's view was degraded; " +
                            "the autopilot still heard MAVProxy, so its own link-loss failsafe did not trigger " +
                            "(drill 3 tests that).");
        }
        catch (OperationCanceledException)
        {
            ShowDrillResult("RESULT: DONE - link drill stopped.");
        }
        finally
        {
            _sim.DropPercent = 0;
            _linkDrill.Dispose();
            _linkDrill = null;
            RefreshStatus();
        }
    }

    private void RunDrill(string args, string title)
    {
        _drillLog.Clear();
        DrillLog.Text = "";
        DrillResultText.Text = title + " ...";
        DrillResultText.Foreground = new SolidColorBrush(Color.Parse("#cbd5e1"));
        var (ok, msg) = _drills.Start(args);
        if (!ok)
        {
            DrillResultText.Text = msg;
            DrillResultText.Foreground = Red;
            return;
        }
        SetDrillBadge("RUNNING", "#3d2e0d", "#f59e0b");
        AppendDrillLog($"== {title} ==");
        RefreshStatus();
    }

    private void AppendDrillLog(string line)
    {
        _drillLog.AppendLine(line);
        if (_drillLog.Length > 20000) _drillLog.Remove(0, _drillLog.Length - 15000);
        DrillLog.Text = _drillLog.ToString();
        DrillLog.CaretIndex = DrillLog.Text.Length; // keep the newest line in view
    }

    private void ShowDrillResult(string resultLine)
    {
        var text = resultLine.StartsWith("RESULT:") ? resultLine.Substring(7).Trim() : resultLine;
        DrillResultText.Text = text;
        if (text.StartsWith("PASS"))
        { SetDrillBadge("PASS", "#0d3d2e", "#22c55e"); DrillResultText.Foreground = Green; }
        else if (text.StartsWith("FAIL"))
        { SetDrillBadge("FAIL", "#3d0d0d", "#ef4444"); DrillResultText.Foreground = Red; }
        else if (text.StartsWith("DONE"))
        { SetDrillBadge("DONE", "#0d3d2e", "#22c55e"); DrillResultText.Foreground = Green; }
        else
        { SetDrillBadge("NOT STARTED", "#3d2e0d", "#f59e0b"); DrillResultText.Foreground = Amber; }
        RefreshStatus();
    }

    private void SetDrillBadge(string text, string bg, string fg)
    {
        DrillBadge.Background = new SolidColorBrush(Color.Parse(bg));
        DrillBadgeText.Text = text;
        DrillBadgeText.Foreground = new SolidColorBrush(Color.Parse(fg));
    }
}
