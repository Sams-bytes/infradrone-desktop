using Avalonia.Controls;

using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using InfraDroneDesktop.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace InfraDroneDesktop.Views;

public partial class ProcessingView : UserControl
{
    private readonly WebOdmService _odm = new WebOdmService();
    private string[] _imagePaths = Array.Empty<string>();
    private int _projectId = -1;
    private string _taskId = "";

    public ProcessingView()
    {
        InitializeComponent();
    }

    private async void OnConnect(object? s, RoutedEventArgs e)
    {
        ConnStatus.Text = "Connecting...";
        var ok = await _odm.LoginAsync(UsernameInput.Text ?? "", PasswordInput.Text ?? "");
        if (ok)
        {
            ConnDot.Fill = new SolidColorBrush(Color.Parse("#0d9e75"));
            ConnStatus.Text = "Connected to WebODM";
            BtnSelectImages.IsEnabled = true;
        }
        else
        {
            ConnDot.Fill = new SolidColorBrush(Color.Parse("#ef4444"));
            ConnStatus.Text = "Connection failed — check credentials and WebODM is running";
        }
    }

    private async void OnSelectImages(object? s, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select folder with drone images"
        });
        if (folders.Count == 0) return;
        var dir = folders[0].Path.LocalPath;
        _imagePaths = Directory.GetFiles(dir, "*.jpg")
            .Concat(Directory.GetFiles(dir, "*.jpeg"))
            .Concat(Directory.GetFiles(dir, "*.JPG"))
            .ToArray();
        ImageCountText.Text = $"{_imagePaths.Length} images found in {Path.GetFileName(dir)}";
        BtnStartProcessing.IsEnabled = _imagePaths.Length >= 3;
        if (_imagePaths.Length < 3)
            ImageCountText.Text += " — need at least 3 images for processing";
    }

    private async void OnStartProcessing(object? s, RoutedEventArgs e)
    {
        var name = ProjectNameInput.Text?.Trim();
        if (string.IsNullOrEmpty(name)) name = $"InfraDrone-{DateTime.Now:yyyyMMdd-HHmm}";

        ProgressCard.IsVisible = true;
        ProgressLabel.Text = "Creating project...";
        BtnStartProcessing.IsEnabled = false;

        try
        {
            _projectId = await _odm.CreateProjectAsync(name);
            ProgressLabel.Text = $"Uploading {_imagePaths.Length} images...";

            _taskId = await _odm.UploadImagesAsync(_projectId, _imagePaths, (i, total) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ProgressBar.Value = (double)i / total * 30; // upload = 30% of total
                    ProgressDetail.Text = $"Uploading {i}/{total} images...";
                });
            });

            ProgressLabel.Text = "Processing started — this may take 10-60 minutes depending on image count";
            _ = PollStatusAsync();
        }
        catch (Exception ex)
        {
            ProgressLabel.Text = $"Error: {ex.Message}";
        }
    }

    private async Task PollStatusAsync()
    {
        while (true)
        {
            await Task.Delay(5000);
            var task = await _odm.GetTaskStatusAsync(_projectId, _taskId);
            if (task == null) continue;

            Dispatcher.UIThread.Post(() =>
            {
                ProgressDetail.Text = $"Status: {task.StatusLabel} — {task.Progress:F0}%";
                ProgressBar.Value = 30 + (task.Progress * 0.7); // remaining 70%
            });

            if (task.Status == 40) // Completed
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ProgressLabel.Text = "✓ Processing complete!";
                    ResultsCard.IsVisible = true;
                });
                break;
            }
            if (task.Status == 30 || task.Status == 50) // Failed/Cancelled
            {
                Dispatcher.UIThread.Post(() => ProgressLabel.Text = $"✗ {task.StatusLabel}");
                break;
            }
        }
    }

    private void OnDownloadOrtho(object? s, RoutedEventArgs e)
    {
        var url = _odm.GetOrthomosaicUrl(_projectId, _taskId);
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    private void OnDownloadReport(object? s, RoutedEventArgs e)
    {
        var url = _odm.GetReportUrl(_projectId, _taskId);
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    private void OnOpenWebodm(object? s, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = $"http://localhost:8000/3d/project/{_projectId}/task/{_taskId}/",
            UseShellExecute = true
        });
    }
}
