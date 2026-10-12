using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using InfraDroneDesktop.Services;

namespace InfraDroneDesktop.Views;

/// <summary>
/// Presenter control panel for the Suriname demo. The demo display itself is the
/// web page in wwwroot/demo, served by a Cesium3DViewService instance and opened in
/// a browser window (ideally on the projector). This tab sends commands to it
/// through DemoBridge and shows the speaker notes for the chapter on screen.
/// </summary>
public partial class SuriDemoView : UserControl
{
    private static readonly string[] Scenarios = { "albina", "nickerie" };
    private static readonly string[] Langs = { "en", "nl" };
    private static readonly string[] Maps = { "google", "offline", "online" };

    private readonly Cesium3DViewService _server = new();
    private readonly DemoOfflineService _offline = new();
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _prepCts;
    private int _expectedTiles = -1;

    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#22c55e"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#f59e0b"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#ef4444"));
    private static readonly IBrush Grey = new SolidColorBrush(Color.Parse("#475569"));
    private static readonly HttpClient Net = new() { Timeout = TimeSpan.FromSeconds(5) };

    public SuriDemoView()
    {
        InitializeComponent();
        var s = DemoBridge.Settings;
        ScenarioBox.SelectedIndex = Math.Max(0, Array.IndexOf(Scenarios, s.Scenario));
        LangBox.SelectedIndex = Math.Max(0, Array.IndexOf(Langs, s.Lang));
        MapBox.SelectedIndex = Math.Max(0, Array.IndexOf(Maps, s.Map));
        EnduranceBox.Text = s.EnduranceMin?.ToString() ?? "";

        _offline.Progress += (msg, frac) => Dispatcher.UIThread.Post(() =>
        {
            OfflineText.Text = msg;
            OfflineBar.Value = Math.Round(frac * 100);
        });

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => PollStatus();
        _timer.Start();

        _ = CheckReadinessAsync();
    }

    // ---- Present -----------------------------------------------------------------------
    private void OnOpen(object? sender, RoutedEventArgs e) => OpenDisplay(false);
    private void OnOpenFull(object? sender, RoutedEventArgs e) => OpenDisplay(true);

    private void OpenDisplay(bool fullscreen)
    {
        try
        {
            ReadSettings();
            _server.EnsureStarted();
            var s = DemoBridge.Settings;
            var url = $"{_server.Url}demo/index.html?scenario={s.Scenario}&lang={s.Lang}";
            LaunchBrowser(url, fullscreen);
            StateText.Text = "Display opened at " + url;
        }
        catch (Exception ex)
        {
            StateText.Text = "Could not open the display: " + ex.Message;
        }
    }

    // Prefers a Chrome/Chromium/Edge "app" window (no tabs or address bar); falls back to the default browser.
    private static void LaunchBrowser(string url, bool fullscreen)
    {
        string[] candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "chrome.exe", "msedge.exe" }
            : new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge" };
        var exe = candidates.Select(FindOnPath).FirstOrDefault(p => p != null);
        if (exe != null)
        {
            var args = $"--app={url} --new-window --autoplay-policy=no-user-gesture-required" + (fullscreen ? " --start-fullscreen" : "");
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
            return;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
        else
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            try
            {
                var full = Path.Combine(dir, name);
                if (File.Exists(full)) return full;
            }
            catch { /* odd PATH entry */ }
        }
        return null;
    }

    private void OnNext(object? sender, RoutedEventArgs e) => DemoBridge.Send("next");
    private void OnPrev(object? sender, RoutedEventArgs e) => DemoBridge.Send("prev");
    private void OnHome(object? sender, RoutedEventArgs e) => DemoBridge.Send("home");
    private void OnPause(object? sender, RoutedEventArgs e) => DemoBridge.Send("play");
    private void OnRestart(object? sender, RoutedEventArgs e) => DemoBridge.Send("restart");
    private void OnBlack(object? sender, RoutedEventArgs e) => DemoBridge.Send("black");
    private void OnCam(object? sender, RoutedEventArgs e) => DemoBridge.Send("cam");
    private void OnAuto(object? sender, RoutedEventArgs e) => DemoBridge.Send("auto", BtnAuto.IsChecked == true ? "on" : "off");

    // ---- Setup -----------------------------------------------------------------------------
    private void ReadSettings()
    {
        var s = DemoBridge.Settings;
        s.Scenario = Scenarios[Math.Max(0, ScenarioBox.SelectedIndex)];
        s.Lang = Langs[Math.Max(0, LangBox.SelectedIndex)];
        s.Map = Maps[Math.Max(0, MapBox.SelectedIndex)];
        s.EnduranceMin = int.TryParse((EnduranceBox.Text ?? "").Trim(), out var m) && m >= 20 && m <= 1000 ? m : null;
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        var before = DemoBridge.Settings;
        var (scen, map, endu) = (before.Scenario, before.Map, before.EnduranceMin);
        ReadSettings();
        var s = DemoBridge.Settings;
        DemoBridge.Send("lang", s.Lang);
        if (s.Scenario != scen) DemoBridge.Send("scenario", s.Scenario);           // display reloads at chapter 1
        else if (s.Map != map || s.EnduranceMin != endu) DemoBridge.Send("reload"); // display reloads at the same chapter
        ApplyText.Text = DemoBridge.Status() == null ? "Saved — used when the display opens." : "Sent to the display.";
        _ = CheckReadinessAsync();
    }

    // ---- Status from the display ---------------------------------------------------------------
    private void PollStatus()
    {
        var st = DemoBridge.Status();
        if (st is not JsonElement s)
        {
            LinkText.Text = "DISPLAY NOT OPEN";
            LinkText.Foreground = Grey; LinkBadge.BorderBrush = Grey;
            return;
        }
        string Str(string k) => s.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        bool Bool(string k) => s.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
        int Int(string k) => s.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

        var renderer = Str("renderer") == "google" ? "Google 3D" : "satellite map";
        LinkText.Text = "DISPLAY LIVE · " + renderer.ToUpperInvariant();
        LinkText.Foreground = Green; LinkBadge.BorderBrush = Green;

        ChapterText.Text = $"{Int("chapter") + 1} / {Int("chapters")} · {Str("title")}";
        var state = !Bool("playing") ? "Paused" : Bool("waiting") ? "Chapter finished — press Next" : "Playing";
        if (Bool("auto")) state += " · auto-play on";
        StateText.Text = $"{state} · mission time {Str("clock")} · {(Str("lang") == "nl" ? "Nederlands" : "English")}";
        BtnAuto.IsChecked = Bool("auto");
        BtnPause.Content = Bool("playing") ? "❚❚ Pause" : "▶ Play";

        NotesTitle.Text = Str("title");
        var notes = Str("notes");
        NotesText.Text = string.IsNullOrWhiteSpace(notes) ? "—" : notes;
        var cap = Str("caption");
        OnScreenText.Text = string.IsNullOrEmpty(cap) ? "" : "On screen now: “" + cap + "”";
        var nxt = Str("next");
        NextText.Text = string.IsNullOrEmpty(nxt) ? "Last chapter." : "Next: " + nxt;
    }

    // ---- Readiness ------------------------------------------------------------------------------
    private void OnCheck(object? sender, RoutedEventArgs e) => _ = CheckReadinessAsync();

    private async Task CheckReadinessAsync()
    {
        var key = Cesium3DViewService.GetGoogleKey() != null;
        Set(DotKey, TxtKey, key ? Green : Amber, key ? "Google Maps key saved" : "No Google Maps key — set it once via Mission Planner › 🌍 3D View › Google 3D");

        var filesOk = File.Exists(Cesium3DViewService.DemoScenariosPath);
        Set(DotFiles, TxtFiles, filesOk ? Green : Red, filesOk ? "Demo files present" : "Demo files missing — rebuild the app (dotnet build)");

        var lib = DemoOfflineService.CesiumReady;
        Set(DotLib, TxtLib, lib ? Green : Amber, lib ? "Offline 3D library ready (CesiumJS " + DemoOfflineService.CesiumVersion + ")" : "Offline 3D library not downloaded yet (section 4)");

        var have = await Task.Run(() => DemoOfflineService.TileCount());
        if (_expectedTiles < 0 && filesOk)
        {
            try { _expectedTiles = await Task.Run(() => DemoOfflineService.PlanTiles(Cesium3DViewService.DemoScenariosPath).Count); }
            catch { _expectedTiles = 0; }
        }
        var tilesOk = _expectedTiles > 0 && have >= _expectedTiles * 0.95;
        Set(DotTiles, TxtTiles, tilesOk ? Green : have > 0 ? Amber : Amber,
            $"Offline satellite tiles: {have} of {Math.Max(_expectedTiles, 0)}" + (tilesOk ? "" : " (section 4)"));

        Set(DotNet, TxtNet, Grey, "Internet: checking…");
        bool online;
        try
        {
            using var resp = await Net.GetAsync("https://www.google.com/generate_204");
            online = (int)resp.StatusCode == 204 || resp.IsSuccessStatusCode;
        }
        catch { online = false; }
        Set(DotNet, TxtNet, online ? Green : Amber, online ? "Internet available" : "No internet — use Map: offline package");
    }

    private static void Set(Avalonia.Controls.Shapes.Ellipse dot, TextBlock txt, IBrush brush, string text)
    {
        dot.Fill = brush; txt.Text = text;
    }

    // ---- Offline package ----------------------------------------------------------------------------
    private async void OnPrepare(object? sender, RoutedEventArgs e)
    {
        if (!File.Exists(Cesium3DViewService.DemoScenariosPath))
        {
            OfflineText.Text = "Demo files not found next to the app. Rebuild with dotnet build.";
            return;
        }
        BtnPrepare.IsEnabled = false; BtnCancel.IsEnabled = true;
        _prepCts = new CancellationTokenSource();
        var (ok, msg) = await _offline.PrepareAsync(Cesium3DViewService.DemoScenariosPath, _prepCts.Token);
        OfflineText.Text = msg;
        if (ok) OfflineBar.Value = 100;
        BtnPrepare.IsEnabled = true; BtnCancel.IsEnabled = false;
        await CheckReadinessAsync();
    }

    private void OnCancelPrepare(object? sender, RoutedEventArgs e) => _prepCts?.Cancel();
}
