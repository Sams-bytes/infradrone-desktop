using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
namespace InfraDroneDesktop.Views;

public partial class AssetIntelligenceView : UserControl
{
    private string? _beforePath;
    private string? _afterPath;
    private readonly string _venvPython =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "infradrone-desktop", "change_detection_env", "bin", "python3");
    private readonly string _scriptPath =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "infradrone-desktop", "change_detection.py");

    public AssetIntelligenceView()
    {
        InitializeComponent();
    }

    private async void OnSelectBefore(object? s, RoutedEventArgs e) => await SelectImage(isBefore: true);
    private async void OnSelectAfter(object? s, RoutedEventArgs e) => await SelectImage(isBefore: false);

    private async Task SelectImage(bool isBefore)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var downloadsFolder = await top.StorageProvider.TryGetFolderFromPathAsync(
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = isBefore ? "Select 'Before' orthomosaic" : "Select 'After' orthomosaic",
            SuggestedStartLocation = downloadsFolder,
            FileTypeFilter = new[] { new FilePickerFileType("GeoTIFF") { Patterns = new[] { "*.tif", "*.tiff" } } }
        });
        if (files.Count == 0) return;
        var path = files[0].Path.LocalPath;
        if (isBefore)
        {
            _beforePath = path;
            BtnSelectBefore.Content = $"📁 Before: {Path.GetFileName(path)}";
        }
        else
        {
            _afterPath = path;
            BtnSelectAfter.Content = $"📁 After: {Path.GetFileName(path)}";
        }
        BtnCompare.IsEnabled = _beforePath != null && _afterPath != null;
        StatusText.Text = BtnCompare.IsEnabled ? "Ready to compare." : "Select both images.";
    }

    private async void OnCompare(object? s, RoutedEventArgs e)
    {
        if (_beforePath == null || _afterPath == null) return;
        BtnCompare.IsEnabled = false;
        StatusText.Text = "Running change detection (aligning images, computing difference)...";
        var diffOut = Path.Combine(Path.GetTempPath(), $"change_diff_{DateTime.Now.Ticks}.png");
        var psi = new ProcessStartInfo
        {
            FileName = _venvPython,
            Arguments = $"\"{_scriptPath}\" \"{_beforePath}\" \"{_afterPath}\" \"{diffOut}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        try
        {
            var proc = Process.Start(psi);
            var stdout = await proc!.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var beforePngOut = diffOut.Replace(".png", "_before.png");
            if (proc.ExitCode == 0 && File.Exists(diffOut) && File.Exists(beforePngOut))
            {
                // Composite the heatmap over the "before" image so the change
                // is shown in context of the real scene, not floating on
                // transparency by itself. Uses the script's PNG-converted copy
                // of "before" instead of the raw .tif -- Avalonia's Bitmap
                // loader can't reliably read GeoTIFF, even though the Python
                // side (via PIL) reads it fine for the actual computation.
                using var before = new Bitmap(beforePngOut);
                using var heatmap = new Bitmap(diffOut);
                var rt = new Avalonia.Media.Imaging.RenderTargetBitmap(
                    new Avalonia.PixelSize(before.PixelSize.Width, before.PixelSize.Height));
                using (var ctx = rt.CreateDrawingContext())
                {
                    ctx.DrawImage(before, new Avalonia.Rect(0, 0, before.PixelSize.Width, before.PixelSize.Height));
                    ctx.DrawImage(heatmap, new Avalonia.Rect(0, 0, before.PixelSize.Width, before.PixelSize.Height));
                }
                ResultImage.Source = rt;

                var lastLine = stdout.Trim().Split('\n');
                var summary = lastLine.Length >= 2 ? lastLine[^2] : "Comparison complete.";
                StatusText.Text = summary;
            }
            else
            {
                StatusText.Text = $"Change detection failed: {stderr.Substring(0, Math.Min(300, stderr.Length))}";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to run comparison: {ex.Message}";
        }
        finally
        {
            BtnCompare.IsEnabled = true;
        }
    }
}
