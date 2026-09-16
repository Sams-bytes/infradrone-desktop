using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace InfraDroneDesktop.Views
{
    public partial class SequoiaCalibrationView : UserControl
    {
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        private const string BaseUrl = "http://192.168.42.2";
        private System.Timers.Timer? _pollTimer;

        public SequoiaCalibrationView()
        {
            InitializeComponent();
        }

        public async void RefreshOnOpen()
        {
            await CheckStatus();
        }

        private async void OnCheckStatus(object? s, RoutedEventArgs e) => await CheckStatus();

        private async Task CheckStatus()
        {
            StatusBannerTitle.Text = "Checking...";
            StatusBannerDetail.Text = "";
            BtnStartWizard.IsVisible = false;

            try
            {
                var resp = await _http.GetStringAsync($"{BaseUrl}/calibration");
                var doc = JsonDocument.Parse(resp);
                var body = doc.RootElement.TryGetProperty("body", out var b) ? b.GetString() : "unknown";
                var sunshine = doc.RootElement.TryGetProperty("sunshine", out var sh) ? sh.GetString() : "unknown";

                bool bodyOk = string.Equals(body, "Ok", StringComparison.OrdinalIgnoreCase);
                bool sunshineOk = string.Equals(sunshine, "Ok", StringComparison.OrdinalIgnoreCase);

                if (bodyOk && sunshineOk)
                {
                    StatusBannerTitle.Text = "✓ Calibration OK";
                    StatusBannerTitle.Foreground = new SolidColorBrush(Color.Parse("#0d9e75"));
                    StatusBannerDetail.Text = "Camera is calibrated and ready to capture.";
                }
                else
                {
                    StatusBannerTitle.Text = "⚠ Calibration Required";
                    StatusBannerTitle.Foreground = new SolidColorBrush(Color.Parse("#f59e0b"));
                    StatusBannerDetail.Text = $"Body: {body}  ·  Sunshine sensor: {sunshine}";
                    BtnStartWizard.IsVisible = true;
                }
            }
            catch (Exception ex)
            {
                StatusBannerTitle.Text = "Could not reach camera";
                StatusBannerTitle.Foreground = new SolidColorBrush(Color.Parse("#ef4444"));
                StatusBannerDetail.Text = ex.Message;
            }
        }

        private async void OnStartWizard(object? s, RoutedEventArgs e)
        {
            try
            {
                await _http.GetStringAsync($"{BaseUrl}/calibration/sunshine/start");
            }
            catch (Exception ex)
            {
                StatusBannerDetail.Text = $"Failed to start: {ex.Message}";
                return;
            }

            WizardPanel.IsVisible = true;
            CompletePanel.IsVisible = false;
            WizardCurrentStep.Text = "Starting...";

            _pollTimer?.Stop();
            _pollTimer = new System.Timers.Timer(1000);
            _pollTimer.Elapsed += async (_, _) => await PollWizard();
            _pollTimer.Start();
        }

        private async void OnStopWizard(object? s, RoutedEventArgs e)
        {
            _pollTimer?.Stop();
            try { await _http.GetStringAsync($"{BaseUrl}/calibration/stop"); } catch { }
            WizardPanel.IsVisible = false;
            await CheckStatus();
        }

        private async Task PollWizard()
        {
            string body = "unknown", sunshine = "unknown";
            try
            {
                var resp = await _http.GetStringAsync($"{BaseUrl}/calibration");
                var doc = JsonDocument.Parse(resp);
                body = doc.RootElement.TryGetProperty("body", out var b) ? b.GetString() ?? "unknown" : "unknown";
                sunshine = doc.RootElement.TryGetProperty("sunshine", out var sh) ? sh.GetString() ?? "unknown" : "unknown";
            }
            catch
            {
                return;
            }

            bool bodyOk = string.Equals(body, "Ok", StringComparison.OrdinalIgnoreCase);
            bool sunshineOk = string.Equals(sunshine, "Ok", StringComparison.OrdinalIgnoreCase);

            Dispatcher.UIThread.Post(() =>
            {
                WizardRawStatus.Text = $"body: {body}  ·  sunshine: {sunshine}";

                var activeBrush = new SolidColorBrush(Color.Parse("#0d9e75"));
                var idleBrush = new SolidColorBrush(Color.Parse("#2d3f52"));
                YawDot.Background = idleBrush;
                PitchDot.Background = idleBrush;
                RollDot.Background = idleBrush;

                if (sunshine.Contains("Psi", StringComparison.OrdinalIgnoreCase))
                {
                    YawDot.Background = activeBrush;
                    WizardCurrentStep.Text = "Rotate: YAW";
                }
                else if (sunshine.Contains("Theta", StringComparison.OrdinalIgnoreCase))
                {
                    PitchDot.Background = activeBrush;
                    WizardCurrentStep.Text = "Rotate: PITCH";
                }
                else if (sunshine.Contains("Phi", StringComparison.OrdinalIgnoreCase))
                {
                    RollDot.Background = activeBrush;
                    WizardCurrentStep.Text = "Rotate: ROLL";
                }
                else if (!sunshineOk)
                {
                    WizardCurrentStep.Text = "Rotate slowly through all axes";
                }
            });

            if (bodyOk && sunshineOk)
            {
                _pollTimer?.Stop();
                try { await _http.GetStringAsync($"{BaseUrl}/calibration/stop"); } catch { }

                string captureResult = "unknown";
                try { captureResult = await _http.GetStringAsync($"{BaseUrl}/capture"); } catch { }

                Dispatcher.UIThread.Post(() =>
                {
                    WizardPanel.IsVisible = false;
                    CompletePanel.IsVisible = true;
                    CompleteCaptureCheck.Text = captureResult.Contains("Calibration needed")
                        ? "Note: capture still reports calibration needed - may need a moment to settle, try Check status again."
                        : $"Capture check: {captureResult}";
                });
                await CheckStatus();
            }
        }
    }
}
