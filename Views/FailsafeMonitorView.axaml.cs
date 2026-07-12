using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using InfraDroneDesktop.Services;

namespace InfraDroneDesktop.Views
{
    public partial class FailsafeMonitorView : UserControl
    {
        private AsvMavLinkService? _mav;
        private DispatcherTimer? _refreshTimer;

        public FailsafeMonitorView()
        {
            InitializeComponent();
        }

        public void SetMavLink(AsvMavLinkService mav)
        {
            _mav = mav;
            _mav.SafetyAlert += (title, message) => Dispatcher.UIThread.Post(RefreshAlertHistory);

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _refreshTimer.Tick += (s, e) => RefreshStatus();
            _refreshTimer.Start();
            RefreshStatus();
            RefreshAlertHistory();
        }

        private static readonly IBrush OkBrush = new SolidColorBrush(Color.Parse("#0d9e75"));
        private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#eab308"));
        private static readonly IBrush BadBrush = new SolidColorBrush(Color.Parse("#ef4444"));
        private static readonly IBrush GreyBrush = new SolidColorBrush(Color.Parse("#64748b"));

        private void RefreshStatus()
        {
            if (_mav == null || !_mav.Telemetry.Connected)
            {
                SetCard(LinkStatusText, LinkCard, "NOT CONNECTED", GreyBrush);
                SetCard(BatteryStatusText, BatteryCard, "NOT CONNECTED", GreyBrush);
                SetCard(GpsStatusText, GpsCard, "NOT CONNECTED", GreyBrush);
                return;
            }

            // Link
            if (_mav.IsLinkOk)
                SetCard(LinkStatusText, LinkCard, "OK", OkBrush);
            else
                SetCard(LinkStatusText, LinkCard, $"LOST ({_mav.SecondsSinceLastTelemetry:F0}s)", BadBrush);
            LinkDetailText.Text = $"Threshold: {_mav.LinkTimeoutThreshold} s of silence";

            // Battery
            var battPct = _mav.Telemetry.BatteryPct;
            if (battPct < 0)
                SetCard(BatteryStatusText, BatteryCard, "UNKNOWN", GreyBrush);
            else if (_mav.IsBatteryCritical)
                SetCard(BatteryStatusText, BatteryCard, $"CRITICAL ({battPct}%)", BadBrush);
            else if (!_mav.IsBatteryOk)
                SetCard(BatteryStatusText, BatteryCard, $"LOW ({battPct}%)", WarnBrush);
            else
                SetCard(BatteryStatusText, BatteryCard, $"OK ({battPct}%)", OkBrush);
            BatteryDetailText.Text = $"Low: {_mav.BatteryLowThreshold}%  Critical: {_mav.BatteryCriticalThreshold}%";

            // GPS
            if (_mav.IsGpsOk)
                SetCard(GpsStatusText, GpsCard, $"OK ({_mav.Telemetry.GpsSats} sats)", OkBrush);
            else
                SetCard(GpsStatusText, GpsCard, $"DEGRADED ({_mav.Telemetry.GpsSats} sats)", BadBrush);
            GpsDetailText.Text = $"Minimum: {_mav.GpsMinSatsThreshold} satellites, 3D fix";
        }

        private void SetCard(TextBlock statusText, Border card, string text, IBrush color)
        {
            statusText.Text = text;
            statusText.Foreground = color;
            card.BorderBrush = color;
        }

        private void RefreshAlertHistory()
        {
            if (_mav == null) return;
            AlertHistoryPanel.Children.Clear();

            if (_mav.AlertHistory.Count == 0)
            {
                AlertHistoryPanel.Children.Add(new TextBlock
                {
                    Text = "No alerts recorded yet this session.",
                    FontSize = 12, Foreground = GreyBrush
                });
                return;
            }

            foreach (var alert in _mav.AlertHistory.Take(50))
            {
                var isRestored = alert.Title.Contains("RESTORED") || alert.Title.Contains("RECOVERED");
                var color = isRestored ? OkBrush : (IBrush)new SolidColorBrush(Color.Parse("#ef4444"));

                var panel = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(0, 0, 0, 8) };
                panel.Children.Add(new TextBlock
                {
                    Text = $"{alert.Time:yyyy-MM-dd HH:mm:ss} -- {alert.Title}",
                    FontSize = 12, FontWeight = Avalonia.Media.FontWeight.Bold, Foreground = color
                });
                panel.Children.Add(new TextBlock
                {
                    Text = alert.Message, FontSize = 11, Foreground = GreyBrush, TextWrapping = Avalonia.Media.TextWrapping.Wrap
                });
                AlertHistoryPanel.Children.Add(panel);
            }
        }
    }
}
