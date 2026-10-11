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

    private void OnStop(object? sender, RoutedEventArgs e)
    {
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
}
