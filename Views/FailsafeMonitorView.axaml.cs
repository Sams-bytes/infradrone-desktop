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
        private static void SpeakAlert(string title)
        {
            // Speaks safety-critical alerts out loud -- link loss, battery,
            // GPS, geofence -- so they're noticed even when looking at the
            // sky rather than the screen. Fire-and-forget; if espeak-ng
            // isn't installed, fails silently and the on-screen alert
            // (already shown via RefreshAlertHistory) still works normally.
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "espeak-ng",
                    Arguments = "\"" + title + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch { }
        }

        private AsvMavLinkService? _mav;
        private Mavlink1SerialService? _v1;
        private DispatcherTimer? _refreshTimer;

        public FailsafeMonitorView()
        {
            InitializeComponent();
        }

        public void SetMavLink(AsvMavLinkService mav)
        {
            _mav = mav;
            _mav.SafetyAlert += (title, message) => { Dispatcher.UIThread.Post(RefreshAlertHistory); SpeakAlert(title); };
            EnsureTimer();
        }

        public void SetMavlinkV1(Mavlink1SerialService v1)
        {
            if (_v1 == v1) return;
            _v1 = v1;
            _v1.SafetyAlert += (title, message) => { Dispatcher.UIThread.Post(RefreshAlertHistory); SpeakAlert(title); };
            EnsureTimer();
        }

        private void EnsureTimer()
        {
            if (_refreshTimer != null) return;
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
                RefreshV1Status();
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

            RefreshV1Status();
        }

        private void RefreshV1Status()
        {
            if (_v1 == null || !_v1.Telemetry.Connected)
            {
                SetCard(V1LinkStatusText, V1LinkCard, "NOT CONNECTED", GreyBrush);
                SetCard(V1BatteryStatusText, V1BatteryCard, "NOT CONNECTED", GreyBrush);
                SetCard(V1GpsStatusText, V1GpsCard, "NOT CONNECTED", GreyBrush);
                return;
            }

            if (_v1.IsLinkOk)
                SetCard(V1LinkStatusText, V1LinkCard, "OK", OkBrush);
            else
                SetCard(V1LinkStatusText, V1LinkCard, $"LOST ({_v1.SecondsSinceLastTelemetry:F0}s)", BadBrush);
            V1LinkDetailText.Text = $"Threshold: {_v1.LinkTimeoutThreshold} s of silence";

            var v1Batt = _v1.Telemetry.BatteryPct;
            if (v1Batt < 0)
                SetCard(V1BatteryStatusText, V1BatteryCard, "UNKNOWN", GreyBrush);
            else if (_v1.IsBatteryCritical)
                SetCard(V1BatteryStatusText, V1BatteryCard, $"CRITICAL ({v1Batt}%)", BadBrush);
            else if (!_v1.IsBatteryOk)
                SetCard(V1BatteryStatusText, V1BatteryCard, $"LOW ({v1Batt}%)", WarnBrush);
            else
                SetCard(V1BatteryStatusText, V1BatteryCard, $"OK ({v1Batt}%)", OkBrush);
            V1BatteryDetailText.Text = $"Low: {_v1.BatteryLowThreshold}%  Critical: {_v1.BatteryCriticalThreshold}%";

            if (_v1.IsGpsOk)
                SetCard(V1GpsStatusText, V1GpsCard, $"OK ({_v1.Telemetry.GpsSats} sats)", OkBrush);
            else
                SetCard(V1GpsStatusText, V1GpsCard, $"DEGRADED ({_v1.Telemetry.GpsSats} sats)", BadBrush);
            V1GpsDetailText.Text = $"Minimum: {_v1.GpsMinSatsThreshold} satellites, 3D fix";

            var lastFenceAlert = _v1.AlertHistory.FirstOrDefault(a => a.Title.Contains("GEOFENCE"));
            if (lastFenceAlert != null && lastFenceAlert.Title == "GEOFENCE BREACH")
                SetCard(V1FenceStatusText, V1FenceCard, "BREACHED", BadBrush);
            else
                SetCard(V1FenceStatusText, V1FenceCard, "Monitoring", OkBrush);
        }

        private void SetCard(TextBlock statusText, Border card, string text, IBrush color)
        {
            statusText.Text = text;
            statusText.Foreground = color;
            card.BorderBrush = color;
        }

        private void RefreshAlertHistory()
        {
            AlertHistoryPanel.Children.Clear();

            var merged = new System.Collections.Generic.List<(DateTime Time, string Title, string Message, string Vehicle)>();
            if (_mav != null)
                merged.AddRange(_mav.AlertHistory.Select(a => (a.Time, a.Title, a.Message, "Cube Orange")));
            if (_v1 != null)
                merged.AddRange(_v1.AlertHistory.Select(a => (a.Time, a.Title, a.Message, "BCube")));
            merged = merged.OrderByDescending(a => a.Time).ToList();

            if (merged.Count == 0)
            {
                AlertHistoryPanel.Children.Add(new TextBlock
                {
                    Text = "No alerts recorded yet this session.",
                    FontSize = 12, Foreground = GreyBrush
                });
                return;
            }

            foreach (var alert in merged.Take(50))
            {
                var isRestored = alert.Title.Contains("RESTORED") || alert.Title.Contains("RECOVERED");
                var color = isRestored ? OkBrush : (IBrush)new SolidColorBrush(Color.Parse("#ef4444"));

                var panel = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(0, 0, 0, 8) };
                panel.Children.Add(new TextBlock
                {
                    Text = $"{alert.Time:yyyy-MM-dd HH:mm:ss} -- [{alert.Vehicle}] {alert.Title}",
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
