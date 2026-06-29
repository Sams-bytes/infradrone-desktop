using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using InfraDroneDesktop.Services;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace InfraDroneDesktop.Views;

public partial class DjiView : UserControl
{
    private readonly HttpClient _http = new HttpClient();
    private string? _lastCsvPath;
    private FlightLogSession? _lastSession;

    public DjiView()
    {
        InitializeComponent();
    }

    private async void OnImportLog(object? s, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import DJI CSV Flight Log",
            FileTypeFilter = new[] { new FilePickerFileType("DJI CSV") { Patterns = new[] { "*.csv", "*.txt" } } }
        });
        if (files.Count == 0) return;
        _lastCsvPath = files[0].Path.LocalPath;
        ImportStatus.Text = "Parsing DJI log...";

        var svc = new FlightLogService();
        _lastSession = await svc.ParseDjiCsvAsync(_lastCsvPath);

        if (_lastSession == null || _lastSession.Points.Count == 0)
        {
            // Try generic CSV
            _lastSession = await svc.ParseCsvAsync(_lastCsvPath);
        }

        if (_lastSession != null && _lastSession.Points.Count > 0)
        {
            ImportStatus.Text = $"✓ Loaded {_lastSession.Points.Count} GPS points — " +
                                $"Duration: {_lastSession.Duration:hh\\:mm\\:ss} — " +
                                $"Max alt: {_lastSession.MaxAlt:F1}m — " +
                                $"Distance: {_lastSession.TotalDistKm:F2}km";
            BtnGenerateReport.IsEnabled = true;
        }
        else
        {
            ImportStatus.Text = "✗ Could not parse log — check format (needs lat/lon columns)";
        }
    }

    private void OnGenerateReport(object? s, RoutedEventArgs e)
    {
        if (_lastCsvPath == null) return;
        var outPath = Path.ChangeExtension(_lastCsvPath, ".pdf");
        var script = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "infradrone-desktop", "generate_report.py");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/home/sam/agridrone_env/bin/python3",
            Arguments = $"{script} {_lastCsvPath} {outPath}",
            UseShellExecute = false,
            RedirectStandardOutput = true
        };
        var proc = System.Diagnostics.Process.Start(psi);
        proc?.WaitForExit();
        ReportStatus.Text = $"✓ Report saved: {outPath}";
    }

    private async void OnDownloadSample(object? s, RoutedEventArgs e)
    {
        SampleStatus.Text = "Searching for sample DJI dataset...";
        try
        {
            // Create a synthetic DJI-format CSV for testing
            var samplePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "infradrone-desktop", "dji_sample_flight.csv");

            var csv = new System.Text.StringBuilder();
            csv.AppendLine("time(millisecond),latitude,longitude,height,speed(m/s),directionOfTravel");

            // Simulate a DJI flight over Groningen
            double lat = 53.2194, lon = 6.5665;
            double alt = 0, speed = 0;
            for (int i = 0; i < 300; i++)
            {
                var t = i * 1000;
                if (i < 20) { alt = i * 1.5; speed = 0; } // takeoff
                else if (i < 280) { lat += 0.00003; alt = 30; speed = 8; } // fly north
                else { alt = Math.Max(0, 30 - (i - 280) * 1.5); speed = 0; } // land
                csv.AppendLine($"{t},{lat.ToString("F7", System.Globalization.CultureInfo.InvariantCulture)}," +
                               $"{lon.ToString("F7", System.Globalization.CultureInfo.InvariantCulture)}," +
                               $"{alt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}," +
                               $"{speed.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)},0");
            }

            await File.WriteAllTextAsync(samplePath, csv.ToString());
            SampleStatus.Text = $"✓ Sample DJI CSV created: {samplePath}\nClick 'Import DJI CSV' to load it.";
        }
        catch (Exception ex)
        {
            SampleStatus.Text = $"Error: {ex.Message}";
        }
    }
}
