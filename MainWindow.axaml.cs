using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using InfraDroneDesktop.Services;
using InfraDroneDesktop.Views;
using InfraDroneDesktop.Services;
using System.Threading.Tasks;
using System.Linq;

namespace InfraDroneDesktop;

public partial class MainWindow : Window
{
    private readonly AsvMavLinkService _mav = new AsvMavLinkService();
    private readonly BatteryHealthService _batteryHealth = new BatteryHealthService();
    private bool _mavRunning = false;

    public MainWindow()
    {
        InitializeComponent();
        _mav.TelemetryUpdated += OnTelemetry;
        _mav.TelemetryUpdated += (t) => _batteryHealth.OnTelemetryUpdate(t);
        _mav.SafetyAlert += OnSafetyAlert;

        // A "View Health Passport" click anywhere in the app (currently only
        // Flight View's map click-info card) navigates here to Asset
        // Intelligence's Health Passport section, creating that view first
        // if it hasn't been opened yet this session.
        Services.SelectedAssetContext.NavigateToHealthPassportRequested += () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_assetIntelligenceView == null) _assetIntelligenceView = new Views.AssetIntelligenceView();
                ContentArea.Child = _assetIntelligenceView;
                _assetIntelligenceView.ShowHealthPassport();
            });
        };
    }

    private void OnTelemetry(AsvTelemetryData t)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Cube Orange takes priority for the sidebar if it's actually connected,
            // same priority pattern as Flight View's CubeOrangeConnected -- avoids the
            // two controllers fighting over the same display if both are plugged in.
            if (_v1 != null && _v1.Telemetry.Connected && !t.Connected) return;
            ConnDot.Fill = t.Connected
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0d9e75"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#ef4444"));
            ConnText.Text = t.Connected ? "Online" : "Offline";
            ModeText.Text = t.FlightMode;
            BattText.Text = t.Connected ? $"{t.BatteryPct}%" : "—";
            GpsText.Text = t.Connected ? $"{t.GpsSats} sat / fix {t.GpsFix}" : "—";
        });
    }

    private void OnV1TelemetrySidebar(Mavlink1Telemetry t)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Only drive the sidebar from BCube if Cube Orange isn't the one connected.
            if (_mav != null && _mav.Telemetry.Connected) return;
            ConnDot.Fill = t.Connected
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0d9e75"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#ef4444"));
            ConnText.Text = t.Connected ? "Online" : "Offline";
            ModeText.Text = t.FlightMode;
            BattText.Text = t.Connected && t.BatteryPct >= 0 ? $"{t.BatteryPct}%" : "—";
            GpsText.Text = t.Connected ? $"{t.GpsSats} sat / fix {t.GpsFix}" : "—";
        });
    }

    private FlightView? _flightView;
    private Mavlink1SerialService? _v1;
    private CalibrationView? _calibrationView;

    private void OnCalibrationView(object? sender, RoutedEventArgs e)
    {
        if (_calibrationView == null)
        {
            _calibrationView = new CalibrationView();
        }
        // Re-check the connection every time this screen is opened, not just
        // on first creation -- fixes the case where the user opens this
        // screen before clicking Connect.
        if (_v1 != null) _calibrationView.SetMavlinkV1(_v1);
        ContentArea.Child = _calibrationView;
    }
    private void OnSafetyAlert(string title, string message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            var ackBtn = new Avalonia.Controls.Button
            {
                Content = "Acknowledge",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#ef4444")),
                Foreground = Avalonia.Media.Brushes.White,
                Padding = new Avalonia.Thickness(12,6),
            };
            var dialog = new Avalonia.Controls.Window
            {
                Title = $"⚠ {title}",
                Width = 400, Height = 180,
                Background = Avalonia.Media.Brushes.Transparent,
                WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
                Content = new Avalonia.Controls.Border
                {
                    Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3d1515")),
                    BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#ef4444")),
                    BorderThickness = new Avalonia.Thickness(2),
                    CornerRadius = new Avalonia.CornerRadius(8),
                    Child = new Avalonia.Controls.StackPanel
                    {
                        Margin = new Avalonia.Thickness(20),
                        Spacing = 12,
                        Children =
                        {
                            new Avalonia.Controls.TextBlock
                            {
                                Text = $"⚠ {title}",
                                FontSize = 16, FontWeight = Avalonia.Media.FontWeight.Bold,
                                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#ef4444"))
                            },
                            new Avalonia.Controls.TextBlock
                            {
                                Text = message, FontSize = 12,
                                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e2e8f0")),
                                TextWrapping = Avalonia.Media.TextWrapping.Wrap
                            },
                            ackBtn
                        }
                    }
                }
            };
            ackBtn.Click += (_, _) => dialog.Close();
            await dialog.ShowDialog(this);
        });
    }

    private void OnFlightView(object? sender, RoutedEventArgs e)
    {
        if (_flightView == null)
        {
            _flightView = new FlightView();
            _flightView.SetMavLink(_mav);
        }
        if (_v1 != null) _flightView.SetMavlinkV1(_v1);
        ContentArea.Child = _flightView;
    }
    private MissionView? _missionView;
    private void OnMissionView(object? sender, RoutedEventArgs e)
    {
        if (_missionView == null)
        {
            _missionView = new MissionView();
            _missionView.SetMavLink(_mav);
        }
        if (_v1 != null) _missionView.SetMavlinkV1(_v1);
        ContentArea.Child = _missionView;
    }
    private GeofenceView? _geofenceView;
    private PreflightView? _preflightView;
    private void OnGeofenceView(object? sender, RoutedEventArgs e)
    {
        if (_geofenceView == null)
        {
            _geofenceView = new GeofenceView();
            _geofenceView.SetMavLink(_mav);
        }
        ContentArea.Child = _geofenceView;
    }
    private void OnPreflightView(object? sender, RoutedEventArgs e)
    {
        if (_preflightView == null)
        {
            _preflightView = new PreflightView();
            _preflightView.SetMavLink(_mav);
        }
        // Pass current mission waypoints for validation
        if (_missionView != null && _missionView._waypoints.Count > 0)
        {
            _preflightView.MissionWaypoints = _missionView._waypoints
                .Select(w => (w.Lat, w.Lon, w.AltM))
                .ToList();
        }
        ContentArea.Child = _preflightView;
    }
    private NotamView? _notamView;
    private WeatherView? _weatherView;
    private void OnWeatherView(object? sender, RoutedEventArgs e)
    {
        if (_weatherView == null) _weatherView = new WeatherView();
        ContentArea.Child = _weatherView;
    }
    private TerrainView? _terrainView;
    private void OnTerrainView(object? sender, RoutedEventArgs e)
    {
        if (_terrainView == null) _terrainView = new TerrainView();
        // Load waypoints from mission planner if available
        if (_missionView != null && _missionView._waypoints.Count > 0)
            _terrainView.LoadWaypoints(_missionView._waypoints.Select(w => (w.Lat, w.Lon, w.AltM)).ToList());
        ContentArea.Child = _terrainView;
    }

    private ParamsView? _paramsView;
    private void OnParamsView(object? sender, RoutedEventArgs e)
    {
        if (_paramsView == null) _paramsView = new ParamsView();
        ContentArea.Child = _paramsView;
    }
    private FlightLogView? _flightLogView;
    private void OnAuditView(object? sender, RoutedEventArgs e)
    {
        if (_flightLogView == null) _flightLogView = new FlightLogView();
        ContentArea.Child = _flightLogView;
    }

    private LicenseView? _licenseView;
    private DjiView? _djiView;
    private SequoiaView? _sequoiaView;
    private ProcessingView? _processingView;
    private Views.LandEnvironmentView? _landEnvironmentView;
    private void OnLandEnvironmentView(object? sender, RoutedEventArgs e)
    {
        if (_landEnvironmentView == null) _landEnvironmentView = new Views.LandEnvironmentView();
        ContentArea.Child = _landEnvironmentView;
    }
    private Views.AssetIntelligenceView? _assetIntelligenceView;
    private void OnAssetIntelligenceView(object? sender, RoutedEventArgs e)
    {
        if (_assetIntelligenceView == null) _assetIntelligenceView = new Views.AssetIntelligenceView();
        ContentArea.Child = _assetIntelligenceView;
    }
    private Views.SolarInspectionView? _solarInspectionView;
    private void OnSolarInspectionView(object? sender, RoutedEventArgs e)
    {
        if (_solarInspectionView == null) _solarInspectionView = new Views.SolarInspectionView();
        ContentArea.Child = _solarInspectionView;
    }
    private AiView? _aiView;
    private void OnAiView(object? sender, RoutedEventArgs e)
    {
        if (_aiView == null) _aiView = new AiView();
        ContentArea.Child = _aiView;
    }

    private SurveyAndProcessingView? _surveyAndProcessingView;
    private void OnSurveyAndProcessingView(object? sender, RoutedEventArgs e)
    {
        if (_surveyAndProcessingView == null)
        {
            _surveyAndProcessingView = new SurveyAndProcessingView();
            _surveyAndProcessingView.SendToMissionRequested += () =>
            {
                var wps = _surveyAndProcessingView.GetGeneratedWaypoints();
                if (_flightView == null)
                {
                    _flightView = new FlightView();
                    _flightView.SetMavLink(_mav);
                }
                if (_v1 != null) _flightView.SetMavlinkV1(_v1);
                if (wps != null)
                {
                    _flightView._waypoints.Clear();
                    int n = 1;
                    foreach (var (lat, lon, alt) in wps)
                        _flightView._waypoints.Add(new Waypoint { Number = n++, Lat = lat, Lon = lon, AltM = alt });
                    _flightView.RefreshWpMap();
                    _flightView.RefreshWaypointList();
                }
                OnFlightView(this, new RoutedEventArgs());
            };
        }
        if (_flightView == null)
        {
            _flightView = new FlightView();
            _flightView.SetMavLink(_mav);
        }
        if (_v1 != null) _flightView.SetMavlinkV1(_v1);
        _surveyAndProcessingView.SetFlightView(_flightView);
        ContentArea.Child = _surveyAndProcessingView;
    }
    private void OnSequoiaView(object? sender, RoutedEventArgs e)
    {
        if (_sequoiaView == null) _sequoiaView = new SequoiaView();
        ContentArea.Child = _sequoiaView;
    }
    private Views.NitrogenZonesView? _nitrogenZonesView;
    private Views.ValidationEvidenceView? _validationEvidenceView;
    private Views.TrafficPlayerView? _trafficPlayerView;
    private Views.AerialDetectionView? _aerialDetectionView;
    private Views.MavLinkTestView? _mavLinkTestView;
    private Views.FailsafeMonitorView? _failsafeMonitorView;
    private Views.StoryModeView? _storyModeView;

    private void OnNitrogenZonesView(object? sender, RoutedEventArgs e)
    {
        if (_nitrogenZonesView == null) _nitrogenZonesView = new Views.NitrogenZonesView();
        ContentArea.Child = _nitrogenZonesView;
    }
    private void OnValidationView(object? sender, RoutedEventArgs e)
    {
        if (_validationEvidenceView == null) _validationEvidenceView = new Views.ValidationEvidenceView();
        ContentArea.Child = _validationEvidenceView;
    }
    private void OnTrafficPlayerView(object? sender, RoutedEventArgs e)
    {
        if (_trafficPlayerView == null) _trafficPlayerView = new Views.TrafficPlayerView();
        ContentArea.Child = _trafficPlayerView;
    }
    private Views.TrafficHubView? _trafficHubView;
    private void OnTrafficHubView(object? sender, RoutedEventArgs e)
    {
        if (_trafficHubView == null) _trafficHubView = new Views.TrafficHubView();
        ContentArea.Child = _trafficHubView;
    }
    private void OnAerialDetectionView(object? sender, RoutedEventArgs e)
    {
        if (_aerialDetectionView == null) _aerialDetectionView = new Views.AerialDetectionView();
        ContentArea.Child = _aerialDetectionView;
    }
    private void OnMavLinkTestView(object? sender, RoutedEventArgs e)
    {
        if (_mavLinkTestView == null) _mavLinkTestView = new Views.MavLinkTestView();
        ContentArea.Child = _mavLinkTestView;
    }
    private void OnFailsafeMonitorView(object? sender, RoutedEventArgs e)
    {
        if (_failsafeMonitorView == null)
        {
            _failsafeMonitorView = new Views.FailsafeMonitorView();
            _failsafeMonitorView.SetMavLink(_mav);
        }
        if (_v1 != null) _failsafeMonitorView.SetMavlinkV1(_v1);
        ContentArea.Child = _failsafeMonitorView;
    }
    private void OnStoryModeView(object? sender, RoutedEventArgs e)
    {
        if (_storyModeView == null) _storyModeView = new Views.StoryModeView();
        ContentArea.Child = _storyModeView;
    }
    private void OnDjiView(object? sender, RoutedEventArgs e)
    {
        if (_djiView == null) _djiView = new DjiView();
        ContentArea.Child = _djiView;
    }
    private void OnParamsViewLicense(object? sender, RoutedEventArgs e)
    {
        if (_licenseView == null) _licenseView = new LicenseView();
        ContentArea.Child = _licenseView;
    }

    private void OnConnect(object? sender, RoutedEventArgs e)
    {
        if (!_mavRunning)
        {
            _mavRunning = true;
            // SAFETY FIX: plain "udp://host:port" with no rhost/rport can RECEIVE
            // but cannot reliably SEND (no known destination) -- this means Arm/
            // Disarm/Takeoff/RTL/SetMode may have NEVER actually reached the vehicle
            // despite compiling correctly. Fixed by giving it mavproxy's real input
            // port as an explicit destination, same fix that resolved fence-breach
            // testing.
            _mav.Start("udp://127.0.0.1:14571?rhost=127.0.0.1&rport=14445");

            // Also attempt the BCube/older-Pixhawk (MAVLink v1) path -- separate
            // hardware/port, so this is safe to try alongside the Cube Orange
            // connection above. Fails silently (logged only) if that device
            // isn't actually plugged in right now.
            if (_v1 == null) // guard against a stray duplicate click clobbering an already-open port
            {
                try
                {
                    _v1 = new Mavlink1SerialService();
                    bool v1Ok = _v1.Start("/dev/bcube", 57600);
                    if (!v1Ok) _v1 = null;
                    else _v1.TelemetryUpdated += OnV1TelemetrySidebar;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MainWindow] BCube/v1 connection not available: {ex.Message}");
                    // Only speak/alert if BCube's port genuinely exists but something
                    // real went wrong opening it -- if the port simply doesn't exist,
                    // that just means BCube isn't plugged in right now, which is
                    // normal/expected (this button tries both vehicles automatically
                    // every time) and shouldn't sound a false alarm.
                    if (System.IO.File.Exists("/dev/bcube"))
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "espeak-ng",
                                Arguments = "\"BCube connection error\"",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            });
                        }
                        catch { }
                    }
                    _v1 = null;
                }
            }

            ConnText.Text = "Connecting...";
        }
        else
        {
            _mav.Stop();
            _mavRunning = false;
            ConnText.Text = "Offline";
        }
    }
}
