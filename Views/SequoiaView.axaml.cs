using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace InfraDroneDesktop.Views;

public partial class SequoiaView : UserControl
{
    private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    private string _baseUrl = "http://10.1.1.2";
    private bool _connected = false;
    private List<string> _imageFiles = new();

    public SequoiaView()
    {
        InitializeComponent();
    }

    private async void OnConnectUsb(object? s, RoutedEventArgs e)
    {
        _baseUrl = "http://10.1.1.2";
        await Connect();
    }

    private async void OnConnectWifi(object? s, RoutedEventArgs e)
    {
        _baseUrl = "http://192.168.47.1";
        await Connect();
    }

    private async Task Connect()
    {
        StatusText.Text = "Connecting...";
        try
        {
            var resp = await _http.GetStringAsync($"{_baseUrl}/version");
            var doc = JsonDocument.Parse(resp);
            var ver = doc.RootElement.GetProperty("version").GetString();
            var sn = doc.RootElement.GetProperty("serial_number").GetString();
            _connected = true;
            ConnDot.Fill = new SolidColorBrush(Color.Parse("#0d9e75"));
            ConnStatus.Text = $"Connected ({(_baseUrl.Contains("10.1") ? "USB" : "WiFi")})";
            FwText.Text = $"FW: {ver} · S/N: {sn}";
            SetControlsEnabled(true);
            StatusText.Text = "Connected. Loading status...";
            await RefreshStatus();
            await LoadConfig();
        }
        catch (Exception ex)
        {
            _connected = false;
            ConnDot.Fill = new SolidColorBrush(Color.Parse("#ef4444"));
            ConnStatus.Text = "Connection failed";
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private void SetControlsEnabled(bool en)
    {
        BtnRefreshStatus.IsEnabled = en;
        BtnApplyConfig.IsEnabled = en;
        BtnCapture.IsEnabled = en;
        BtnListImages.IsEnabled = en;
        BtnDownload.IsEnabled = en;
        BtnSendToWebOdm.IsEnabled = en;
        CaptureMode.IsEnabled = en;
        GpsInterval.IsEnabled = en;
        Overlap.IsEnabled = en;
        TilapseInterval.IsEnabled = en;
        ResolutionRgb.IsEnabled = en;
        BitDepth.IsEnabled = en;
        StorageTarget.IsEnabled = en;
    }

    private async void OnRefreshStatus(object? s, RoutedEventArgs e) => await RefreshStatus();

    private async Task RefreshStatus()
    {
        try
        {
            // Calibration
            var cal = await _http.GetStringAsync($"{_baseUrl}/calibration");
            var calDoc = JsonDocument.Parse(cal);
            var calStatus = calDoc.RootElement.TryGetProperty("calibration_status",
                out var cs) ? cs.GetString() : "unknown";
            CalibStatus.Text = $"Calibration: {calStatus}";
            CalibStatus.Foreground = new SolidColorBrush(Color.Parse(
                calStatus == "calibrated" ? "#0d9e75" : "#ef4444"));

            // Storage
            var stor = await _http.GetStringAsync($"{_baseUrl}/storage");
            var storDoc = JsonDocument.Parse(stor);

            if (storDoc.RootElement.TryGetProperty("internal", out var intern))
            {
                var total = intern.GetProperty("total").GetDouble();
                var free = intern.GetProperty("free").GetDouble();
                var usedPct = total > 0 ? (total - free) / total * 100 : 0;
                StorageInternal.Value = usedPct;
                StorageInternalText.Text = $"{free / 1024:F0} MB free of {total / 1024:F0} MB";
            }

            if (storDoc.RootElement.TryGetProperty("sd", out var sd))
            {
                var total = sd.GetProperty("total").GetDouble();
                var free = sd.GetProperty("free").GetDouble();
                var usedPct = total > 0 ? (total - free) / total * 100 : 0;
                StorageSd.Value = usedPct;
                StorageSdText.Text = total > 0
                    ? $"{free / 1024:F0} MB free of {total / 1024:F0} MB"
                    : "No SD card";
            }
            StatusText.Text = "Status refreshed.";
        }
        catch (Exception ex) { StatusText.Text = $"Status error: {ex.Message}"; }
    }

    private async Task LoadConfig()
    {
        try
        {
            var resp = await _http.GetStringAsync($"{_baseUrl}/config");
            var doc = JsonDocument.Parse(resp);

            if (doc.RootElement.TryGetProperty("capture_mode", out var cm))
            {
                var mode = cm.GetString();
                CaptureMode.SelectedIndex = mode == "gps" ? 0 : mode == "timelapse" ? 1 : 2;
            }
            if (doc.RootElement.TryGetProperty("gps_param", out var gp))
                GpsInterval.Text = gp.GetDouble().ToString("F0");
            if (doc.RootElement.TryGetProperty("overlap_param", out var op))
                Overlap.Text = op.GetDouble().ToString("F0");
            if (doc.RootElement.TryGetProperty("timelapse_param", out var tp))
                TilapseInterval.Text = tp.GetDouble().ToString("F1");
            if (doc.RootElement.TryGetProperty("resolution_rgb", out var rr))
                ResolutionRgb.SelectedIndex = rr.GetDouble() >= 16 ? 0 : 1;
            if (doc.RootElement.TryGetProperty("bit_depth", out var bd))
                BitDepth.SelectedIndex = bd.GetInt32() == 10 ? 0 : 1;
            if (doc.RootElement.TryGetProperty("storage_selected", out var ss))
                StorageTarget.SelectedIndex = ss.GetString() == "internal" ? 0 : 1;

            StatusText.Text = "Configuration loaded.";
        }
        catch (Exception ex) { StatusText.Text = $"Config error: {ex.Message}"; }
    }

    private async void OnApplyConfig(object? s, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Applying configuration...";
            var modes = new[] { "gps", "timelapse", "single" };
            var payload = new
            {
                capture_mode = modes[CaptureMode.SelectedIndex],
                gps_param = double.Parse(GpsInterval.Text ?? "25"),
                overlap_param = double.Parse(Overlap.Text ?? "80"),
                timelapse_param = double.Parse(TilapseInterval.Text ?? "1.5"),
                resolution_rgb = ResolutionRgb.SelectedIndex == 0 ? 16 : 12,
                resolution_mono = 1.2,
                bit_depth = BitDepth.SelectedIndex == 0 ? 10 : 8,
                sensors_mask = 31,
                storage_selected = StorageTarget.SelectedIndex == 0 ? "internal" : "sd",
                auto_select = "off"
            };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{_baseUrl}/config", content);
            StatusText.Text = resp.IsSuccessStatusCode
                ? "✓ Configuration applied successfully."
                : $"✗ Apply failed: {resp.StatusCode}";
        }
        catch (Exception ex) { StatusText.Text = $"Config apply error: {ex.Message}"; }
    }

    private async void OnCapture(object? s, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Triggering capture...";
            await _http.GetAsync($"{_baseUrl}/capture");
            StatusText.Text = "✓ Capture triggered.";
        }
        catch (Exception ex) { StatusText.Text = $"Capture error: {ex.Message}"; }
    }

    private string RunAdb(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "adb", Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var proc = System.Diagnostics.Process.Start(psi);
        var output = proc?.StandardOutput.ReadToEnd() ?? "";
        proc?.WaitForExit();
        return output.Trim();
    }

    private async void OnMassStorage(object? s, RoutedEventArgs e)
    {
        StatusText.Text = "Switching to mass storage mode...";
        await Task.Run(() => RunAdb("shell setprop sys.usb.config mass_storage"));
        StatusText.Text = "Switched — camera will mount as USB drive. Reconnect USB if needed.";
    }

    private async void OnListImages(object? s, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Listing images...";
            ImageList.Items.Clear();
            _imageFiles.Clear();

            var stor = await _http.GetStringAsync($"{_baseUrl}/storage");
            var storDoc = JsonDocument.Parse(stor);
            var storagePath = storDoc.RootElement
                .TryGetProperty("storage_selected", out var sp)
                ? sp.GetString() : "internal";

            // ADB-based listing (REST download endpoint not available on this firmware)
            var adbOut = await Task.Run(() => RunAdb("shell find /data/medias/DCIM/ -type f"));
            var files = adbOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var count = 0;
            foreach (var f2 in files)
            {
                var f = f2.Trim();
                if (!f.EndsWith(".TIF") && !f.EndsWith(".JPG") &&
                    !f.EndsWith(".tif") && !f.EndsWith(".jpg")) continue;
                _imageFiles.Add(f);
                count++;
                ImageList.Items.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#131f2e")),
                    CornerRadius = new Avalonia.CornerRadius(4),
                    Padding = new Avalonia.Thickness(8, 4),
                    Margin = new Avalonia.Thickness(0, 1),
                    Child = new TextBlock
                    {
                        Text = System.IO.Path.GetFileName(f), FontSize = 10,
                        Foreground = new SolidColorBrush(Color.Parse("#94a3b8"))
                    }
                });
            }
            ImageCountText.Text = $"{count} image(s) on camera";
            RightPanelTitle.Text = $"{count} images on Sequoia";
            StatusText.Text = $"Found {count} images.";
            return;
        }
        catch (Exception ex) { StatusText.Text = $"List error: {ex.Message}"; }
    }

    private async void OnDownload(object? s, RoutedEventArgs e)
    {
        if (_imageFiles.Count == 0) { StatusText.Text = "List images first."; return; }
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Select download folder" });
        if (folders.Count == 0) return;
        var destDir = folders[0].Path.LocalPath;
        StatusText.Text = "Downloading all images via ADB...";
        await Task.Run(() => RunAdb($"pull /data/medias/DCIM/ \"{destDir}\""));
        StatusText.Text = $"✓ Download complete → {destDir}";
        BtnSendToWebOdm.IsEnabled = true;
    }

    private void OnSendToWebOdm(object? s, RoutedEventArgs e)
    {
        StatusText.Text = "Open the Processing view and upload the download folder to WebODM.";
    }
}
