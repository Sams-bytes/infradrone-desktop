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
            await AutoSyncCheck();
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
        BtnMassStorage.IsEnabled = en;
        BtnRenameSsid.IsEnabled = en;
        SsidBox.IsEnabled = en;
        BtnApplyBands.IsEnabled = en;
        BandGreen.IsEnabled = en;
        BandRed.IsEnabled = en;
        BandRedEdge.IsEnabled = en;
        BandNir.IsEnabled = en;
        BandRgb.IsEnabled = en;
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
            if (doc.RootElement.TryGetProperty("sensors_mask", out var sm))
            {
                var mask = sm.GetInt32();
                BandGreen.IsChecked   = (mask & 1)  != 0;
                BandRed.IsChecked     = (mask & 2)  != 0;
                BandRedEdge.IsChecked = (mask & 4)  != 0;
                BandNir.IsChecked     = (mask & 8)  != 0;
                BandRgb.IsChecked     = (mask & 16) != 0;
                UpdateMaskHint();
            }

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

    // ── Feature 1: WiFi SSID rename ──────────────────────────────────────────
    private async void OnRenameSsid(object? s, RoutedEventArgs e)
    {
        var newSsid = SsidBox.Text?.Trim();
        if (string.IsNullOrEmpty(newSsid)) { StatusText.Text = "Enter a new network name first."; return; }
        try
        {
            StatusText.Text = "Renaming WiFi network...";
            var payload = System.Text.Json.JsonSerializer.Serialize(new { ssid = newSsid });
            var content2 = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{_baseUrl}/wifi", content2);
            StatusText.Text = resp.IsSuccessStatusCode
                ? $"✓ WiFi renamed to '{newSsid}' — takes effect on next camera boot."
                : $"✗ Rename failed: {resp.StatusCode}";
        }
        catch (Exception ex) { StatusText.Text = $"SSID error: {ex.Message}"; }
    }

    // ── Feature 2: Sensors mask from checkboxes ───────────────────────────────
    private int ComputeMask()
    {
        int mask = 0;
        if (BandGreen.IsChecked == true)   mask |= 1;
        if (BandRed.IsChecked == true)     mask |= 2;
        if (BandRedEdge.IsChecked == true) mask |= 4;
        if (BandNir.IsChecked == true)     mask |= 8;
        if (BandRgb.IsChecked == true)     mask |= 16;
        return mask;
    }

    private void UpdateMaskHint()
    {
        var mask = ComputeMask();
        var bands = new System.Collections.Generic.List<string>();
        if ((mask & 1)  != 0) bands.Add("Green");
        if ((mask & 2)  != 0) bands.Add("Red");
        if ((mask & 4)  != 0) bands.Add("RedEdge");
        if ((mask & 8)  != 0) bands.Add("NIR");
        if ((mask & 16) != 0) bands.Add("RGB");
        MaskHint.Text = $"{bands.Count} band(s) active (mask={mask}): {string.Join(", ", bands)}";
    }

    private async void OnApplyBands(object? s, RoutedEventArgs e)
    {
        var mask = ComputeMask();
        if (mask == 0) { StatusText.Text = "Select at least one band."; return; }
        UpdateMaskHint();
        try
        {
            StatusText.Text = $"Applying band mask {mask}...";
            var payload = System.Text.Json.JsonSerializer.Serialize(new { sensors_mask = mask });
            var content2 = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{_baseUrl}/config", content2);
            StatusText.Text = resp.IsSuccessStatusCode
                ? $"✓ Band mask {mask} applied — {32 - mask} band(s) disabled."
                : $"✗ Band apply failed: {resp.StatusCode}";
        }
        catch (Exception ex) { StatusText.Text = $"Band mask error: {ex.Message}"; }
    }

    // ── Feature 3: Auto-sync on connect ──────────────────────────────────────
    private int _lastKnownImageCount = -1;

    private async Task AutoSyncCheck()
    {
        try
        {
            var output = await Task.Run(() =>
                RunAdb("shell find /data/medias/DCIM/ -type f -name '*.TIF' -o -type f -name '*.JPG'"));
            var files = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var count = files.Length;
            if (_lastKnownImageCount < 0)
            {
                _lastKnownImageCount = count;
                AutoSyncText.Text = count > 0
                    ? $"📷 {count} image(s) on camera from previous flights."
                    : "No images on camera yet.";
            }
            else if (count > _lastKnownImageCount)
            {
                var newCount = count - _lastKnownImageCount;
                AutoSyncText.Text = $"🆕 {newCount} new image(s) detected — click Download to save.";
                _lastKnownImageCount = count;
            }
            else
            {
                AutoSyncText.Text = $"📷 {count} image(s) on camera — no new images.";
            }

            // Feature 4: Storage warning for pre-flight check
            var stor = await _http.GetStringAsync($"{_baseUrl}/storage");
            var storDoc = System.Text.Json.JsonDocument.Parse(stor);
            if (storDoc.RootElement.TryGetProperty("internal", out var intern))
            {
                var free = intern.GetProperty("free").GetDouble();
                var freeMb = free / 1024.0;
                StorageWarning.Text = freeMb < 500
                    ? $"⚠ Only {freeMb:F0} MB free — consider downloading before flight."
                    : "";
            }
        }
        catch { AutoSyncText.Text = "Could not check image count."; }
    }
}
