using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using InfraDroneDesktop.Services;

namespace InfraDroneDesktop.Views
{
    public partial class CalibrationView : UserControl
    {
        private Mavlink1SerialService? _v1;

        public CalibrationView()
        {
            InitializeComponent();
        }

        public void SetMavlinkV1(Mavlink1SerialService v1)
        {
            _v1 = v1;
            _v1.StatusTextReceived += OnStatusText;
            _v1.TelemetryUpdated += OnV1Telemetry;
            _v1.CommandAckReceived += OnCommandAck;
            Dispatcher.UIThread.Post(() =>
            {
                ConnStatusText.Text = "Connected. Ready to calibrate.";
                BtnGyroCal.IsEnabled = true;
                BtnCompassCal.IsEnabled = true;
                BtnAccelCal.IsEnabled = true;
                BtnLevelCal.IsEnabled = true;
            });
        }

        // Real, immediate confirmation for single-step calibrations (Level
        // Horizon, Gyro) that may never send a follow-up STATUSTEXT message --
        // the COMMAND_ACK itself is the real, definitive result for these.
        private void OnCommandAck(ushort command, byte result)
        {
            if (command != 241) return; // only relevant for calibration commands
            Dispatcher.UIThread.Post(() =>
            {
                bool accepted = result == 0;
                ResultBanner.IsVisible = true;
                ResultBanner.Background = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.Parse(accepted ? "#0d3d2e" : "#3d0d0d"));
                ResultBannerText.Text = accepted
                    ? "✓ Command accepted by vehicle (result=0). If no further instructions appear below within a few seconds, this calibration is likely already complete."
                    : $"✗ Command REJECTED by vehicle (result={result}) -- calibration did NOT start.";
                ResultBannerText.Foreground = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.Parse(accepted ? "#0d9e75" : "#ef4444"));
            });
        }

        private void OnV1Telemetry(Mavlink1Telemetry t)
        {
            Dispatcher.UIThread.Post(() =>
            {
                RcCh1.Text = $"CH1: {t.RcChannels[0]}";
                RcCh2.Text = $"CH2: {t.RcChannels[1]}";
                RcCh3.Text = $"CH3: {t.RcChannels[2]}";
                RcCh4.Text = $"CH4: {t.RcChannels[3]}";
                RcCh5.Text = $"CH5: {t.RcChannels[4]}";
                RcCh6.Text = $"CH6: {t.RcChannels[5]}";
                RcCh7.Text = $"CH7: {t.RcChannels[6]}";
                RcCh8.Text = $"CH8: {t.RcChannels[7]}";
                RcRssiText.Text = t.RcRssi == 255 ? "Signal: unknown" : $"Signal: {t.RcRssi}/255";
            });
        }

        private DateTime _lastMessageTime = DateTime.MinValue;
        private DispatcherTimer? _elapsedTimer;

        private void OnStatusText(byte severity, string text)
        {
            _lastMessageTime = DateTime.Now;
            if (_elapsedTimer == null)
            {
                _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _elapsedTimer.Tick += (s, e) =>
                {
                    var secs = (DateTime.Now - _lastMessageTime).TotalSeconds;
                    LastUpdateText.Text = secs < 2 ? "● Receiving live updates now"
                        : $"Last update: {secs:F0}s ago" + (secs > 15 ? " -- may be stalled, check connection" : "");
                    LastUpdateText.Foreground = secs > 15
                        ? Avalonia.Media.Brushes.OrangeRed
                        : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#64748b"));
                };
                _elapsedTimer.Start();
            }

            Dispatcher.UIThread.Post(() =>
            {
                // MAV_SEVERITY: 0-3 critical/error (red), 4-5 warning (amber), 6-7 info (green)
                var color = severity <= 3 ? "#ef4444" : severity <= 5 ? "#eab308" : "#0d9e75";

                CurrentInstructionText.Text = text;
                CurrentInstructionText.Foreground = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.Parse(color));

                // Detect clear success/failure keywords ArduPilot commonly sends,
                // and show a big, unmistakable banner rather than making the
                // user parse plain log text to know if it worked.
                // Real bug fixed: severity alone does NOT reliably indicate failure --
                // ArduPilot sometimes uses high-priority severity just to ensure a
                // message is noticed (e.g. calibration prompts), not because something
                // failed. Only trust actual keywords now, not severity level.
                var lower = text.ToLowerInvariant();
                bool isSuccess = lower.Contains("success") || lower.Contains("complete") || lower.Contains("calibrated");
                bool isFailure = lower.Contains("fail") || lower.Contains("error");
                bool needsAction = lower.Contains("place") || lower.Contains("rotate") || lower.Contains("press");

                if (isSuccess || isFailure)
                {
                    ResultBanner.IsVisible = true;
                    ResultBanner.Background = new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.Parse(isSuccess ? "#0d3d2e" : "#3d0d0d"));
                    ResultBannerText.Text = isSuccess ? "✓ " + text : "✗ " + text;
                    ResultBannerText.Foreground = new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.Parse(isSuccess ? "#0d9e75" : "#ef4444"));
                }
                else if (needsAction)
                {
                    ResultBanner.IsVisible = true;
                    ResultBanner.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3d1e0d"));
                    ResultBannerText.Text = "⚡ ACTION NEEDED: " + text;
                    ResultBannerText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#eab308"));
                }

                LogPanel.Children.Insert(0, new TextBlock
                {
                    Text = $"[{DateTime.Now:HH:mm:ss}] {text}",
                    FontSize = 11,
                    Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(color)),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                });
            });
        }

        private void ClearBanner() => ResultBanner.IsVisible = false;

        private void OnGyroCal(object? sender, RoutedEventArgs e)
        {
            if (_v1 == null) return;
            ClearBanner();
            CurrentInstructionText.Text = "Starting gyro calibration -- keep the vehicle completely still...";
            CurrentInstructionText.Foreground = Avalonia.Media.Brushes.White;
            _v1.StartGyroCalibration();
        }

        private void OnCompassCal(object? sender, RoutedEventArgs e)
        {
            if (_v1 == null) return;
            ClearBanner();
            CurrentInstructionText.Text = "Starting compass calibration -- follow rotation instructions as they appear...";
            CurrentInstructionText.Foreground = Avalonia.Media.Brushes.White;
            _v1.StartCompassCalibration();
        }

        private void OnAccelCal(object? sender, RoutedEventArgs e)
        {
            if (_v1 == null) return;
            ClearBanner();
            CurrentInstructionText.Text = "Starting accelerometer calibration -- follow orientation instructions as they appear...";
            CurrentInstructionText.Foreground = Avalonia.Media.Brushes.White;
            _v1.StartAccelCalibration();
        }

        private void OnLevelCal(object? sender, RoutedEventArgs e)
        {
            if (_v1 == null) return;
            ClearBanner();
            CurrentInstructionText.Text = "Calibrating level horizon -- keep the vehicle still and level...";
            CurrentInstructionText.Foreground = Avalonia.Media.Brushes.White;
            _v1.StartLevelHorizonCalibration();
        }
    }
}
