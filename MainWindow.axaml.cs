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
    private readonly MavLinkService _mav = new MavLinkService();
    private bool _mavRunning = false;

    public MainWindow()
    {
        InitializeComponent();
        _mav.TelemetryUpdated += OnTelemetry;
        _mav.SafetyAlert += OnSafetyAlert;
    }

    private void OnTelemetry(TelemetryData t)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ConnDot.Fill = t.Connected
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0d9e75"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#ef4444"));
            ConnText.Text = t.Connected ? "Online" : "Offline";
            ModeText.Text = t.FlightMode;
            BattText.Text = t.Connected ? $"{t.BatteryPct}%" : "—";
            GpsText.Text = t.Connected ? $"{t.GpsSats} sat / fix {t.GpsFix}" : "—";
        });
    }

    private FlightView? _flightView;
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
            Task.Run(() => _mav.StartAsync(14572));
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
