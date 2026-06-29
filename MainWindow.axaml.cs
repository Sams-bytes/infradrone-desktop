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
    private void OnPreflightView(object? sender, RoutedEventArgs e)
    {
        if (_geofenceView == null)
        {
            _geofenceView = new GeofenceView();
            _geofenceView.SetMavLink(_mav);
        }
        ContentArea.Child = _geofenceView;
    }
    private NotamView? _notamView;
    private void OnWeatherView(object? sender, RoutedEventArgs e)
    {
        if (_notamView == null) _notamView = new NotamView();
        ContentArea.Child = _notamView;
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
