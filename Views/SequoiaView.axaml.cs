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
using System.Linq;
using System.Threading.Tasks;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using NetTopologySuite.Geometries;

namespace InfraDroneDesktop.Views;

public partial class SequoiaView : UserControl
{
    private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    private string _baseUrl = "http://10.1.1.2";
    private bool _connected = false;
    private List<string> _imageFiles = new();
    private string _lastLoadedFolder = "";
    private List<(DateTime dt, string path)> _sessionFiles = new();
    private Mapsui.Map? _nmeaMap;

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
            ConnDot.Fill = new SolidColorBrush(Avalonia.Media.Color.Parse("#0d9e75"));
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
            ConnDot.Fill = new SolidColorBrush(Avalonia.Media.Color.Parse("#ef4444"));
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
        BtnDeleteAll.IsEnabled = en;
        BtnDownloadNmea.IsEnabled = en;
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
            CalibStatus.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse(
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
                    Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#131f2e")),
                    CornerRadius = new Avalonia.CornerRadius(4),
                    Padding = new Avalonia.Thickness(8, 4),
                    Margin = new Avalonia.Thickness(0, 1),
                    Child = new TextBlock
                    {
                        Text = System.IO.Path.GetFileName(f), FontSize = 10,
                        Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#94a3b8"))
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

    // ── Tab switching ─────────────────────────────────────────────────────────
    private void SetTab(string tab)
    {
        TabImages.IsVisible   = tab == "images";
        TabBands.IsVisible    = tab == "bands";
        TabMap.IsVisible      = tab == "map";
        TabSessions.IsVisible = tab == "sessions";
        TabResults.IsVisible  = tab == "results";
        var active   = new SolidColorBrush(Avalonia.Media.Color.Parse("#0d3d2e"));
        var inactive = new SolidColorBrush(Avalonia.Media.Color.Parse("#1a2637"));
        var activeFg   = new SolidColorBrush(Avalonia.Media.Color.Parse("#0d9e75"));
        var inactiveFg = new SolidColorBrush(Avalonia.Media.Color.Parse("#94a3b8"));
        TabBtnImages.Background   = tab == "images"   ? active : inactive;
        TabBtnBands.Background    = tab == "bands"    ? active : inactive;
        TabBtnMap.Background      = tab == "map"      ? active : inactive;
        TabBtnSessions.Background = tab == "sessions" ? active : inactive;
        TabBtnResults.Background  = tab == "results"  ? active : inactive;
        TabBtnImages.Foreground   = tab == "images"   ? activeFg : inactiveFg;
        TabBtnBands.Foreground    = tab == "bands"    ? activeFg : inactiveFg;
        TabBtnMap.Foreground      = tab == "map"      ? activeFg : inactiveFg;
        TabBtnSessions.Foreground = tab == "sessions" ? activeFg : inactiveFg;
        TabBtnResults.Foreground  = tab == "results"  ? activeFg : inactiveFg;
    }
    private void OnTabImages(object? s, RoutedEventArgs e)   => SetTab("images");
    private void OnTabResults(object? s, RoutedEventArgs e)  => SetTab("results");
    private void OnTabBands(object? s, RoutedEventArgs e)    => SetTab("bands");
    private void OnTabMap(object? s, RoutedEventArgs e)      => SetTab("map");
    private void OnTabSessions(object? s, RoutedEventArgs e) => SetTab("sessions");

    // ── Feature 5: Band preview ───────────────────────────────────────────────
    private async void OnLoadLocalFolder(object? s, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Select folder with Sequoia images" });
        if (folders.Count == 0) return;
        var folderPath = folders[0].Path.LocalPath;
        var localFiles = Directory.GetFiles(folderPath, "*.TIF", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(folderPath, "*.tif", SearchOption.AllDirectories))
            .Concat(Directory.GetFiles(folderPath, "*.JPG", SearchOption.AllDirectories))
            .ToList();
        if (localFiles.Count == 0)
        {
            BandCaptureInfo.Text = "No TIF or JPG images found in selected folder.";
            return;
        }
        BandCaptureInfo.Text = $"Loading {localFiles.Count} images from {System.IO.Path.GetFileName(folderPath)}...";
        ShowBandPreview(localFiles);
        GroupIntoSessions(localFiles);
        _lastLoadedFolder = folderPath;
        RightPanelTitle.Text = $"{localFiles.Count} images loaded from local folder";
        StatusText.Text = $"Loaded {localFiles.Count} local images for preview.";
    }

    private void ShowBandPreview(List<string> localFiles)
    {
        BandGrid.Children.Clear();
        var bands = new[] { "GRE", "RED", "REG", "NIR", "RGB" };
        var colors = new[] { "#22c55e", "#ef4444", "#f97316", "#a855f7", "#378add" };
        var latestByBand = new System.Collections.Generic.Dictionary<string, string>();
        foreach (var band in bands)
        {
            var match = localFiles
                .Where(f => Path.GetFileName(f).Contains($"_{band}."))
                .OrderByDescending(f => f)
                .FirstOrDefault();
            if (match != null) latestByBand[band] = match;
        }
        if (latestByBand.Count == 0)
        {
            BandCaptureInfo.Text = "No band images found in downloaded folder.";
            return;
        }
        BandCaptureInfo.Text = $"Showing latest capture — {latestByBand.Count}/5 bands found.";
        BtnNdviAnalysis.IsVisible = latestByBand.Count > 0;
        for (int i = 0; i < bands.Length; i++)
        {
            var band = bands[i];
            var color = colors[i];
            var card = new Border
            {
                Width = 180, Margin = new Avalonia.Thickness(4),
                Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#131f2e")),
                CornerRadius = new Avalonia.CornerRadius(8),
                BorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse(color)),
                BorderThickness = new Avalonia.Thickness(1),
                Padding = new Avalonia.Thickness(8)
            };
            var sp = new StackPanel { Spacing = 4 };
            sp.Children.Add(new TextBlock
            {
                Text = band, FontSize = 11, FontWeight = Avalonia.Media.FontWeight.Bold,
                Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse(color)),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            });
            if (latestByBand.TryGetValue(band, out var imgPath) && File.Exists(imgPath))
            {
                try
                {
                    // Convert 16-bit TIF to 8-bit PNG for Avalonia preview
                    var previewPath = imgPath.Replace(".TIF", "_preview.png")
                                            .Replace(".tif", "_preview.png");
                    if (!File.Exists(previewPath))
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "/home/sam/agridrone_env/bin/python3",
                            Arguments = $"/home/sam/infradrone-desktop/convert_tif_preview.py \"{imgPath}\" \"{previewPath}\"",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        var proc = System.Diagnostics.Process.Start(psi);
                        proc?.WaitForExit();
                    }
                    if (File.Exists(previewPath))
                    {
                        using var stream = File.OpenRead(previewPath);
                        var bmp = new Avalonia.Media.Imaging.Bitmap(stream);
                        sp.Children.Add(new Avalonia.Controls.Image
                        {
                            Source = bmp, Height = 120,
                            Stretch = Avalonia.Media.Stretch.UniformToFill
                        });
                    }
                    else
                    {
                        sp.Children.Add(new TextBlock { Text = "Preview unavailable",
                            FontSize = 9, Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#64748b")),
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center });
                    }
                }
                catch
                {
                    sp.Children.Add(new TextBlock { Text = "Preview unavailable",
                        FontSize = 9, Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#64748b")),
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center });
                }
            }
            else
            {
                sp.Children.Add(new TextBlock { Text = "Not captured",
                    FontSize = 9, Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#64748b")),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center });
            }
            card.Child = sp;
            BandGrid.Children.Add(card);
        }
    }

    // ── Feature 6: Delete all images ─────────────────────────────────────────
    private async void OnDeleteAll(object? s, RoutedEventArgs e)
    {
        var dlg = new Avalonia.Controls.Window
        {
            Title = "Confirm delete",
            Width = 360, Height = 160,
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#1a2637")),
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20), Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Delete ALL images from Sequoia internal storage?",
                        Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#e2e8f0")),
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12 },
                    new TextBlock { Text = "This cannot be undone. Make sure you have downloaded first.",
                        Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#ef4444")),
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 11 },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "Yes, delete all",
                                Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#3d0d0d")),
                                Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#ef4444")),
                                Padding = new Avalonia.Thickness(12,6),
                                [!Button.TagProperty] = new Avalonia.Data.Binding { Source = "confirm" }
                            },
                            new Button { Content = "Cancel",
                                Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#1a2637")),
                                Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#94a3b8")),
                                Padding = new Avalonia.Thickness(12,6) }
                        }
                    }
                }
            }
        };
        var topWin = TopLevel.GetTopLevel(this) as Avalonia.Controls.Window;
        if (topWin == null) return;

        bool confirmed = false;
        var sp2 = (dlg.Content as StackPanel)!;
        var btnPanel = (sp2.Children[2] as StackPanel)!;
        var confirmBtn = (btnPanel.Children[0] as Button)!;
        var cancelBtn  = (btnPanel.Children[1] as Button)!;
        confirmBtn.Click += (_, _) => { confirmed = true; dlg.Close(); };
        cancelBtn.Click  += (_, _) => dlg.Close();
        await dlg.ShowDialog(topWin);

        if (!confirmed) return;
        StatusText.Text = "Deleting all images...";
        await Task.Run(() => RunAdb("shell rm -rf /data/medias/DCIM/external/"));
        _imageFiles.Clear();
        _sessionFiles.Clear();
        ImageList.Items.Clear();
        SessionList.Items.Clear();
        ImageCountText.Text = "0 images";
        AutoSyncText.Text = "All images deleted from camera.";
        _lastKnownImageCount = 0;
        StatusText.Text = "✓ All images deleted from Sequoia.";
    }

    // ── Feature 7: NMEA GPS track ─────────────────────────────────────────────
    private async void OnDownloadNmea(object? s, RoutedEventArgs e)
    {
        NmeaStatus.Text = "Downloading GPS track...";
        var nmea = await Task.Run(() => RunAdb("shell cat /data/medias/generated_nmea.txt"));
        if (string.IsNullOrWhiteSpace(nmea))
        {
            NmeaStatus.Text = "No GPS track found — fly a mission first.";
            return;
        }
        var points = ParseNmea(nmea);
        if (points.Count == 0)
        {
            NmeaStatus.Text = "GPS track file found but no valid fix points — camera had no GPS lock during this session. Fly outdoors and try again.";
            return;
        }
        NmeaStatus.Text = $"GPS track: {points.Count} points — drawing map...";
        DrawNmeaMap(points);
    }

    private List<(double lat, double lon)> ParseNmea(string nmea)
    {
        var points = new List<(double, double)>();
        foreach (var line in nmea.Split('\n'))
        {
            var isGga = line.StartsWith("$GNGGA") || line.StartsWith("$GPGGA");
            var isRmc = line.StartsWith("$GNRMC") || line.StartsWith("$GPRMC");
            if (!isGga && !isRmc) continue;
            var parts = line.Split(',');
            // GGA: lat=parts[2], latDir=parts[3], lon=parts[4], lonDir=parts[5], fix=parts[6]
            // RMC: status=parts[2], lat=parts[3], latDir=parts[4], lon=parts[5], lonDir=parts[6]
            int latIdx = isGga ? 2 : 3;
            int lonIdx = isGga ? 4 : 5;
            int latDirIdx = isGga ? 3 : 4;
            int lonDirIdx = isGga ? 5 : 6;
            if (parts.Length < lonDirIdx + 1) continue;
            if (parts[latIdx] == "" || parts[lonIdx] == "") continue;
            if (isRmc && parts[2] != "A") continue; // RMC: A=active fix only
            if (isGga && parts[6] == "0") continue;  // GGA: fix quality 0 = no fix
            try
            {
                var latRaw = double.Parse(parts[latIdx], System.Globalization.CultureInfo.InvariantCulture);
                var lonRaw = double.Parse(parts[lonIdx], System.Globalization.CultureInfo.InvariantCulture);
                var latDeg = Math.Floor(latRaw / 100) + (latRaw % 100) / 60.0;
                var lonDeg = Math.Floor(lonRaw / 100) + (lonRaw % 100) / 60.0;
                if (parts[latDirIdx] == "S") latDeg = -latDeg;
                if (parts[lonDirIdx] == "W") lonDeg = -lonDeg;
                points.Add((latDeg, lonDeg));
            }
            catch { }
        }
        return points;
    }

    private void DrawNmeaMap(List<(double lat, double lon)> points)
    {
        var map = new Mapsui.Map();
        map.Layers.Add(Mapsui.Tiling.OpenStreetMap.CreateTileLayer());
        var lineLayer = new Mapsui.Layers.MemoryLayer { Name = "Track" };
        var coords = points.Select(p => {
            var xy = Mapsui.Projections.SphericalMercator.FromLonLat(p.lon, p.lat);
            return new NetTopologySuite.Geometries.Coordinate(xy.x, xy.y);
        }).ToArray();
        if (coords.Length > 1)
        {
            var line = new NetTopologySuite.Geometries.LineString(coords);
            var feature = new Mapsui.Nts.GeometryFeature(line);
            feature.Styles.Add(new Mapsui.Styles.VectorStyle
            {
                Line = new Mapsui.Styles.Pen(new Mapsui.Styles.Color(13, 158, 117), 3)
            });
            lineLayer.Features = new List<Mapsui.IFeature> { feature };
            map.Layers.Add(lineLayer);
            var avgLon = points.Average(p => p.lon);
            var avgLat = points.Average(p => p.lat);
            var center = Mapsui.Projections.SphericalMercator.FromLonLat(avgLon, avgLat);
            map.Navigator.CenterOnAndZoomTo(
                new Mapsui.MPoint(center.x, center.y), map.Navigator.Resolutions[14]);
        }
        var mapControl = new Mapsui.UI.Avalonia.MapControl { Map = map };
        MapContainer.Child = mapControl;
        _nmeaMap = map;
        NmeaStatus.Text = $"Flight path drawn — {points.Count} GPS points.";
    }

    // ── Feature 8: Session grouping ───────────────────────────────────────────
    private void GroupIntoSessions(List<string> localFiles)
    {
        SessionList.Items.Clear();
        _sessionFiles.Clear();
        var dated = new List<(DateTime dt, string path)>();
        foreach (var f in localFiles)
        {
            var name = Path.GetFileNameWithoutExtension(f);
            var parts2 = name.Split('_');
            if (parts2.Length >= 3)
            {
                if (DateTime.TryParseExact(parts2[1] + parts2[2], "yyMMddHHmmss",
                    null, System.Globalization.DateTimeStyles.None, out var dt))
                    dated.Add((dt, f));
            }
        }
        if (dated.Count == 0)
        {
            SessionInfo.Text = "Could not parse timestamps from filenames.";
            return;
        }
        dated.Sort((a, b) => a.dt.CompareTo(b.dt));
        var sessions = new List<List<(DateTime dt, string path)>>();
        var current = new List<(DateTime, string)> { dated[0] };
        for (int i = 1; i < dated.Count; i++)
        {
            if ((dated[i].dt - dated[i - 1].dt).TotalMinutes > 30)
            {
                sessions.Add(current);
                current = new List<(DateTime, string)>();
            }
            current.Add(dated[i]);
        }
        sessions.Add(current);
        SessionInfo.Text = $"{sessions.Count} flight session(s) detected from {localFiles.Count} images.";
        for (int i = 0; i < sessions.Count; i++)
        {
            var sess = sessions[i];
            var first = sess.First().dt;
            var last  = sess.Last().dt;
            var duration = (last - first).TotalMinutes;
            var imageCount = sess.Select(x => Path.GetFileName(x.path))
                .Select(n => n.Split('_').Take(3).Aggregate((a,b) => a+"_"+b))
                .Distinct().Count();
            var card = new Border
            {
                Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#131f2e")),
                CornerRadius = new Avalonia.CornerRadius(8),
                Padding = new Avalonia.Thickness(12, 8),
                Margin = new Avalonia.Thickness(0, 4),
                BorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#1e3a5f")),
                BorderThickness = new Avalonia.Thickness(0.5)
            };
            var sp3 = new StackPanel { Spacing = 4 };
            sp3.Children.Add(new TextBlock
            {
                Text = $"Flight {i + 1} — {first:dd MMM yyyy HH:mm}",
                FontSize = 12, FontWeight = Avalonia.Media.FontWeight.Bold,
                Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#e2e8f0"))
            });
            sp3.Children.Add(new TextBlock
            {
                Text = $"{imageCount} capture set(s) · {duration:F0} min · {sess.Count} band files",
                FontSize = 10, Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#64748b"))
            });
            card.Child = sp3;
            SessionList.Items.Add(card);
        }
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

    private string _ndviMapPath = "";
    private string _ndreMapPath = "";
    private string _histogramPath = "";
    private string _dashboardPath = "";

    private async Task PopulateResultsTab(string scriptOutput, string dashboardPath)
    {
        _dashboardPath = dashboardPath;
        var dir = System.IO.Path.GetDirectoryName(dashboardPath) ?? "";

        // Parse stats from output
        var lines = scriptOutput.Split('\n');
        foreach (var line in lines)
        {
            if (line.Contains("NDVI mean"))
                MetricNdvi.Text = line.Split(':').LastOrDefault()?.Trim() ?? "—";
            if (line.Contains("Healthy veg"))
                MetricHealthy.Text = line.Split(':').LastOrDefault()?.Trim() ?? "—";
            if (line.Contains("Stressed veg"))
                MetricStressed.Text = line.Split(':').LastOrDefault()?.Trim() ?? "—";
            if (line.Contains("Bare soil"))
                MetricBare.Text = line.Split(':').LastOrDefault()?.Trim() ?? "—";
        }

        // Generate individual map PNGs from the analysis script
        var ndviPath   = System.IO.Path.Combine(dir, "ndvi_map.png");
        var ndrePath   = System.IO.Path.Combine(dir, "ndre_map.png");
        var histPath   = System.IO.Path.Combine(dir, "ndvi_histogram.png");

        await Task.Run(() => GenerateSeparateMaps(_lastLoadedFolder, ndviPath, ndrePath, histPath));

        _ndviMapPath = ndviPath;
        _ndreMapPath = ndrePath;
        _histogramPath = histPath;

        LoadImageIntoControl(ndviPath, NdviMapImage, NdviMapStatus, "NDVI map");
        LoadImageIntoControl(ndrePath, NdreMapImage, NdreMapStatus, "NDRE map");
        LoadImageIntoControl(histPath, HistogramImage, HistogramStatus, "Histogram");
    }

    private void GenerateSeparateMaps(string folder, string ndviOut, string ndreOut, string histOut)
    {
        var dir = System.IO.Path.GetDirectoryName(ndviOut) ?? "";
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/home/sam/agridrone_env/bin/python3",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("/home/sam/sequoia_test/generate_maps.py");
        psi.ArgumentList.Add(folder);
        psi.ArgumentList.Add(dir);
                var proc = System.Diagnostics.Process.Start(psi);
        var stdout = proc?.StandardOutput.ReadToEnd() ?? "";
        var stderr = proc?.StandardError.ReadToEnd() ?? "";
        proc?.WaitForExit();
        Console.WriteLine("generate_maps stdout: " + stdout);
        Console.WriteLine("generate_maps stderr: " + stderr);
    }

    private void LoadImageIntoControl(string path, Avalonia.Controls.Image imgControl,
        TextBlock statusBlock, string label)
    {
        if (File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                imgControl.Source = new Avalonia.Media.Imaging.Bitmap(stream);
                imgControl.IsVisible = true;
                statusBlock.IsVisible = false;
            }
            catch { statusBlock.Text = $"{label} failed to load."; }
        }
        else
        {
            statusBlock.Text = $"{label} not generated.";
        }
    }

    private void OpenInSystemViewer(string path)
    {
        if (!File.Exists(path)) { StatusText.Text = "File not found — run analysis first."; return; }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "xdg-open", Arguments = path,
            UseShellExecute = true
        });
    }

    private void OnOpenNdviMap(object? s, RoutedEventArgs e)      => OpenInSystemViewer(_ndviMapPath);
    private void OnOpenNdreMap(object? s, RoutedEventArgs e)      => OpenInSystemViewer(_ndreMapPath);
    private void OnOpenHistogram(object? s, RoutedEventArgs e)    => OpenInSystemViewer(_histogramPath);
    private void OnOpenFullDashboard(object? s, RoutedEventArgs e)=> OpenInSystemViewer(_dashboardPath);

    private async void OnNdviAnalysis(object? s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastLoadedFolder))
        {
            StatusText.Text = "Load a folder of images first.";
            return;
        }
        StatusText.Text = "Running NDVI analysis... please wait.";
        BtnNdviAnalysis.IsEnabled = false;

        var scriptPath = "/home/sam/sequoia_test/analyze_sequoia.py";
        var outPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(_lastLoadedFolder) ?? _lastLoadedFolder,
            "sequoia_analysis.png");

        // Write a temp script that uses the selected folder
        var tempScript = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "analyze_sequoia_temp.py");
        var scriptContent = File.ReadAllText(scriptPath)
            .Replace(
                "folder = os.path.expanduser(\"~/sequoia_test/parrot_sequoia\")",
                $"folder = \"{_lastLoadedFolder}\"")
            .Replace(
                "out_path = os.path.expanduser(\"~/sequoia_test/sequoia_analysis.png\")",
                $"out_path = \"{outPath}\"");
        File.WriteAllText(tempScript, scriptContent);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/home/sam/agridrone_env/bin/python3",
            Arguments = tempScript,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        string output = "";
        await Task.Run(() =>
        {
            var proc = System.Diagnostics.Process.Start(psi);
            output = proc?.StandardOutput.ReadToEnd() ?? "";
            proc?.WaitForExit();
        });

        BtnNdviAnalysis.IsEnabled = true;

        if (File.Exists(outPath))
        {
            SetTab("results");
            await PopulateResultsTab(output, outPath);
            StatusText.Text = "NDVI analysis complete - see Results tab.";
            RightPanelTitle.Text = "NDVI Analysis - " + System.IO.Path.GetFileName(_lastLoadedFolder);
        }
        else
        {
            StatusText.Text = "Analysis failed - check terminal for errors.";
        }
    }
}
