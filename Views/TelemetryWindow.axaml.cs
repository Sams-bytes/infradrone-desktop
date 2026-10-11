using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using InfraDroneDesktop.Services;

namespace InfraDroneDesktop.Views;

/// <summary>
/// Separate window with everything the vehicle reports: grouped value tiles,
/// rolling charts (altitude, speed, power), a colour-coded message log, and CSV
/// recording. Works for the real Cube Orange and for the SITL simulator.
/// One instance at a time; ShowShared() brings it to the front if already open.
/// </summary>
public partial class TelemetryWindow : Window
{
    private static TelemetryWindow? _open;

    public static void ShowShared()
    {
        var svc = LiveTelemetryService.Shared;
        if (svc == null) { Console.WriteLine("[Telemetry] service not created yet"); return; }
        if (_open != null) { _open.Activate(); return; }
        _open = new TelemetryWindow(svc);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    private readonly LiveTelemetryService _svc;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, TextBlock> _values = new();
    private readonly int _altRel, _gs, _as, _climb, _volt, _amp;
    private const int MaxLogLines = 400;

    private static readonly IBrush Normal = new SolidColorBrush(Color.Parse("#f1f5f9"));
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#34d399"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#fbbf24"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#f87171"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#64748b"));
    private static readonly IBrush Info = new SolidColorBrush(Color.Parse("#cbd5e1"));
    private static readonly IBrush TileBg = new SolidColorBrush(Color.Parse("#1a2637"));

    // Designer needs a parameterless constructor.
    public TelemetryWindow() : this(LiveTelemetryService.Shared!) { }

    public TelemetryWindow(LiveTelemetryService svc)
    {
        InitializeComponent();
        _svc = svc;

        BuildTiles();

        AltChart.Title = "Altitude"; AltChart.Unit = "m";
        _altRel = AltChart.AddSeries("rel", "#22d3ee");
        SpeedChart.Title = "Speed"; SpeedChart.Unit = "m/s";
        _gs = SpeedChart.AddSeries("ground", "#34d399");
        _as = SpeedChart.AddSeries("air", "#a78bfa");
        _climb = SpeedChart.AddSeries("climb", "#fbbf24");
        PowerChart.Title = "Battery"; PowerChart.Unit = "V / A"; PowerChart.IncludeZero = false;
        _volt = PowerChart.AddSeries("V", "#f472b6");
        _amp = PowerChart.AddSeries("A", "#60a5fa");

        foreach (var e in _svc.RecentLog()) AppendLog(e);
        _svc.LogAdded += OnLogAdded;

        BtnRecord.Content = _svc.IsRecording ? "■ Stop recording" : "● Record CSV";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Closed += (_, _) => { _timer.Stop(); _svc.LogAdded -= OnLogAdded; };
    }

    // ---- Tiles -------------------------------------------------------------
    private void BuildTiles()
    {
        Group("FLIGHT", ("mode", "Mode"), ("armed", "Armed"), ("time", "Flight time"),
              ("throttle", "Throttle"), ("wp", "Mission item"), ("wpdist", "To waypoint"));
        Group("ALTITUDE & SPEED", ("altrel", "Alt (above home)"), ("altmsl", "Alt (above sea)"),
              ("climb", "Climb rate"), ("gs", "Ground speed"), ("as", "Air speed"), ("home", "Distance home"));
        Group("ATTITUDE", ("roll", "Roll"), ("pitch", "Pitch"), ("yaw", "Yaw"),
              ("hdg", "Heading"), ("rollrate", "Roll rate"), ("yawrate", "Yaw rate"));
        Group("NAVIGATION", ("bearing", "Target bearing"), ("xtrack", "Cross-track error"),
              ("alterr", "Altitude error"), ("pos", "Position"), ("homepos", "Home"));
        Group("BATTERY", ("volt", "Voltage"), ("amp", "Current"), ("batt", "Remaining"), ("power", "Power"));
        Group("GPS & LINK", ("fix", "GPS fix"), ("sats", "Satellites"), ("hdop", "HDOP"),
              ("rate", "Messages/s"), ("drop", "Link loss"), ("cpu", "Autopilot CPU"));
        Group("WIND (estimated)", ("winddir", "From"), ("windspd", "Speed"));
    }

    private void Group(string title, params (string key, string label)[] tiles)
    {
        TileHost.Children.Add(new TextBlock
        {
            Text = title, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Muted,
            LetterSpacing = 1, Margin = new Avalonia.Thickness(2, 4, 0, 0)
        });
        var wrap = new WrapPanel();
        foreach (var (key, label) in tiles)
        {
            var value = new TextBlock { Text = "-", FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Normal,
                                        FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, monospace") };
            _values[key] = value;
            wrap.Children.Add(new Border
            {
                Background = TileBg, CornerRadius = new Avalonia.CornerRadius(6),
                Padding = new Avalonia.Thickness(10, 6), Margin = new Avalonia.Thickness(0, 0, 8, 8),
                Width = 165,
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = label, FontSize = 10, Foreground = Muted },
                        value
                    }
                }
            });
        }
        TileHost.Children.Add(wrap);
    }

    private void Set(string key, string text, IBrush? brush = null)
    {
        if (!_values.TryGetValue(key, out var tb)) return;
        tb.Text = text;
        tb.Foreground = brush ?? Normal;
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string N(double v, string fmt, string unit) =>
        double.IsNaN(v) ? "-" : v.ToString(fmt, Inv) + unit;

    // ---- Refresh -------------------------------------------------------------
    private void Refresh()
    {
        var s = _svc.Snapshot();
        var age = s.LastPacketUtc == DateTime.MinValue ? double.PositiveInfinity
                                                       : (DateTime.UtcNow - s.LastPacketUtc).TotalSeconds;
        var live = age < 3;

        LinkDot.Fill = live ? Good : (double.IsInfinity(age) ? Muted : Bad);
        LinkText.Text = live ? $"Live · {s.PacketsPerSec:F0} msg/s"
                      : double.IsInfinity(age) ? "No data yet — connect a vehicle or start the simulator"
                      : $"NO DATA for {age:F0} s";
        ArmText.Text = s.Armed ? "ARMED" : "DISARMED";
        ArmText.Foreground = s.Armed ? Bad : Info;
        ArmBadge.Background = new SolidColorBrush(Color.Parse(s.Armed ? "#3b1015" : "#1e293b"));
        ModeHeader.Text = s.Mode;
        if (!live && !double.IsInfinity(age)) return; // keep last values visible, stop charts

        // Flight
        Set("mode", s.Mode, Info);
        Set("armed", s.Armed ? "YES" : "no", s.Armed ? Bad : Info);
        Set("time", $"{(int)s.FlightTime.TotalMinutes:D2}:{s.FlightTime.Seconds:D2}");
        Set("throttle", N(s.Throttle, "F0", " %"));
        Set("wp", s.WpSeq >= 0 ? s.WpSeq.ToString(Inv) : "-");
        Set("wpdist", N(s.WpDist, "F0", " m"));

        // Altitude & speed
        Set("altrel", N(s.AltRel, "F1", " m"));
        Set("altmsl", N(s.AltMsl, "F1", " m"));
        Set("climb", N(s.Climb, "+0.0;-0.0;0.0", " m/s"));
        Set("gs", N(s.GroundSpeed, "F1", " m/s"));
        Set("as", N(s.AirSpeed, "F1", " m/s"));
        Set("home", N(s.DistHome, "F0", " m"));

        // Attitude
        Set("roll", N(s.Roll, "F1", "°"), Math.Abs(s.Roll) > 45 ? Warn : null);
        Set("pitch", N(s.Pitch, "F1", "°"), Math.Abs(s.Pitch) > 30 ? Warn : null);
        Set("yaw", N(s.Yaw, "F0", "°"));
        Set("hdg", N(s.Heading, "F0", "°"));
        Set("rollrate", N(s.RollRate, "F0", "°/s"));
        Set("yawrate", N(s.YawRate, "F0", "°/s"));

        // Navigation
        Set("bearing", N(s.TargetBearing, "F0", "°"));
        Set("xtrack", N(s.XtrackError, "F1", " m"), Math.Abs(s.XtrackError) > 20 ? Warn : null);
        Set("alterr", N(s.AltError, "F1", " m"));
        Set("pos", s.Lat != 0 ? $"{s.Lat:F5}\n{s.Lon:F5}" : "-");
        Set("homepos", double.IsNaN(s.HomeLat) ? "-" : $"{s.HomeLat:F5}\n{s.HomeLon:F5}");
        _values["pos"].FontSize = 13; _values["homepos"].FontSize = 13;

        // Battery
        Set("volt", N(s.Voltage, "F2", " V"));
        Set("amp", N(s.Current, "F1", " A"));
        Set("batt", s.BatteryPct >= 0 ? $"{s.BatteryPct} %" : "-",
            s.BatteryPct < 0 ? null : s.BatteryPct <= 15 ? Bad : s.BatteryPct <= 30 ? Warn : Good);
        Set("power", double.IsNaN(s.Voltage) || double.IsNaN(s.Current) ? "-" : $"{s.Voltage * s.Current:F0} W");

        // GPS & link
        var fixName = s.GpsFix switch
        {
            0 or 1 => "No fix", 2 => "2D", 3 => "3D", 4 => "DGPS", 5 => "RTK float", 6 => "RTK fixed", _ => s.GpsFix.ToString(Inv)
        };
        Set("fix", fixName, s.GpsFix >= 3 ? Good : Bad);
        Set("sats", s.GpsSats.ToString(Inv), s.GpsSats >= 6 ? Good : Bad);
        Set("hdop", N(s.Hdop, "F2", ""), s.Hdop > 2 ? Warn : null);
        Set("rate", $"{s.PacketsPerSec:F0}");
        Set("drop", N(s.LinkDropPct, "F1", " %"), s.LinkDropPct > 5 ? Warn : null);
        Set("cpu", N(s.CpuLoadPct, "F0", " %"));

        // Wind
        Set("winddir", N(s.WindDir < 0 ? s.WindDir + 360 : s.WindDir, "F0", "°"));
        Set("windspd", N(s.WindSpeed, "F1", " m/s"));

        // Charts
        AltChart.Push(_altRel, s.AltRel);
        SpeedChart.Push(_gs, s.GroundSpeed);
        SpeedChart.Push(_as, s.AirSpeed);
        SpeedChart.Push(_climb, s.Climb);
        PowerChart.Push(_volt, s.Voltage);
        PowerChart.Push(_amp, s.Current);
        AltChart.InvalidateVisual(); SpeedChart.InvalidateVisual(); PowerChart.InvalidateVisual();
    }

    // ---- Log -------------------------------------------------------------------
    private void OnLogAdded(TelemetryLogEntry e) => Dispatcher.UIThread.Post(() => AppendLog(e));

    private void AppendLog(TelemetryLogEntry e)
    {
        var brush = e.Level switch { "error" => Bad, "warn" => Warn, _ => Info };
        var line = new TextBlock
        {
            Text = $"{e.Time:HH:mm:ss}  {e.Source,-3}  {e.Text}",
            FontSize = 12, Foreground = brush, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, monospace")
        };
        LogPanel.Children.Insert(0, line); // newest on top
        while (LogPanel.Children.Count > MaxLogLines) LogPanel.Children.RemoveAt(LogPanel.Children.Count - 1);
    }

    // ---- Buttons -----------------------------------------------------------------
    private void OnRecord(object? sender, RoutedEventArgs e)
    {
        if (_svc.IsRecording)
        {
            _svc.StopRecording();
            BtnRecord.Content = "● Record CSV";
        }
        else
        {
            _svc.StartRecording();
            BtnRecord.Content = "■ Stop recording";
        }
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "agri_drone", "flight_logs");
        Directory.CreateDirectory(dir);
        try { Process.Start(new ProcessStartInfo("xdg-open", dir) { UseShellExecute = false }); }
        catch (Exception ex) { Console.WriteLine("[Telemetry] could not open folder: " + ex.Message); }
    }

    private void OnClearCharts(object? sender, RoutedEventArgs e)
    {
        AltChart.Clear(); SpeedChart.Clear(); PowerChart.Clear();
    }

    private void OnToggleTop(object? sender, RoutedEventArgs e) => Topmost = BtnOnTop.IsChecked == true;
}
