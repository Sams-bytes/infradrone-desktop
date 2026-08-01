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
    private FlightView? _flightView;
    private readonly System.Collections.Generic.List<(int ProjectId, string TaskId, string Label)> _existingTasks = new();
    public void SetFlightView(FlightView fv) => _flightView = fv;

    public ProcessingView()
    {
        InitializeComponent();
    }

    private async void OnLoadOnMap(object? s, RoutedEventArgs e)
    {
        if (_flightView == null)
        {
            ProgressDetail.Text = "Flight View not available -- open it once first.";
            return;
        }
        try
        {
            ProgressDetail.Text = "Downloading orthomosaic...";
            var url = _odm.GetOrthomosaicUrl(_projectId, _taskId);
            var httpClient = _odm.GetHttpClient();
            var bytes = await httpClient.GetByteArrayAsync(url);
            var rawPath = Path.Combine(Path.GetTempPath(), $"ortho_{_taskId}.tif");
            await File.WriteAllBytesAsync(rawPath, bytes);

            ProgressDetail.Text = "Reprojecting to Web Mercator...";
            var warpedPath = Path.Combine(Path.GetTempPath(), $"ortho_{_taskId}_3857.tif");
            if (File.Exists(warpedPath)) File.Delete(warpedPath);
            var warpPsi = new ProcessStartInfo
            {
                FileName = "gdalwarp",
                Arguments = $"-t_srs EPSG:3857 \"{rawPath}\" \"{warpedPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using (var warpProc = Process.Start(warpPsi))
            {
                await warpProc!.WaitForExitAsync();
                if (warpProc.ExitCode != 0)
                {
                    var err = await warpProc.StandardError.ReadToEndAsync();
                    ProgressDetail.Text = $"gdalwarp failed: {err.Substring(0, Math.Min(200, err.Length))}";
                    return;
                }
            }

            ProgressDetail.Text = "Reading extent...";
            var infoPsi = new ProcessStartInfo
            {
                FileName = "gdalinfo",
                Arguments = $"\"{warpedPath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            string gdalInfoOutput;
            using (var infoProc = Process.Start(infoPsi))
            {
                gdalInfoOutput = await infoProc!.StandardOutput.ReadToEndAsync();
                await infoProc.WaitForExitAsync();
            }

            double minX = 0, minY = 0, maxX = 0, maxY = 0;
            foreach (var line in gdalInfoOutput.Split('\n'))
            {
                if (line.TrimStart().StartsWith("Upper Left"))
                {
                    var coords = ParseCoords(line);
                    minX = coords.Item1; maxY = coords.Item2;
                }
                else if (line.TrimStart().StartsWith("Lower Right"))
                {
                    var coords = ParseCoords(line);
                    maxX = coords.Item1; minY = coords.Item2;
                }
            }
            if (minX == 0 && maxX == 0)
            {
                ProgressDetail.Text = "Could not parse extent from gdalinfo output.";
                return;
            }

            var imageBytes = await File.ReadAllBytesAsync(warpedPath);
            _flightView.AddOrthomosaicLayer(imageBytes, minX, minY, maxX, maxY);
            ProgressDetail.Text = "Orthomosaic loaded on Flight View map.";
        }
        catch (Exception ex)
        {
            ProgressDetail.Text = $"Load on map failed: {ex.Message}";
        }
    }

    private static (double, double) ParseCoords(string gdalinfoLine)
    {
        var openParen = gdalinfoLine.IndexOf('(');
        var closeParen = gdalinfoLine.IndexOf(')');
        var inner = gdalinfoLine.Substring(openParen + 1, closeParen - openParen - 1);
        var parts = inner.Split(',');
        return (double.Parse(parts[0].Trim()), double.Parse(parts[1].Trim()));
    }

    private async void OnRefreshProjects(object? s, RoutedEventArgs e)
    {
        ExistingProjectsStatus.Text = "Loading projects...";
        _existingTasks.Clear();
        ExistingTasksCombo.Items.Clear();
        try
        {
            var projects = await _odm.GetProjectsAsync();
            foreach (var proj in projects)
            {
                var tasks = await _odm.GetTasksAsync(proj.Id);
                foreach (var task in tasks)
                {
                    var label = $"{proj.Name} / {(string.IsNullOrEmpty(task.Name) ? task.Uuid.Substring(0, 8) : task.Name)} -- {task.StatusLabel} ({task.Progress:F0}%)";
                    _existingTasks.Add((proj.Id, task.Uuid, label));
                    ExistingTasksCombo.Items.Add(label);
                }
            }
            ExistingProjectsStatus.Text = $"{_existingTasks.Count} task(s) found across {projects.Count} project(s).";
        }
        catch (Exception ex)
        {
            ExistingProjectsStatus.Text = $"Failed to load projects: {ex.Message}";
        }
    }

    private void OnLoadExisting(object? s, RoutedEventArgs e)
    {
        var idx = ExistingTasksCombo.SelectedIndex;
        if (idx < 0 || idx >= _existingTasks.Count)
        {
            ExistingProjectsStatus.Text = "Select a task from the list first.";
            return;
        }
        var (projectId, taskId, label) = _existingTasks[idx];
        _projectId = projectId;
        _taskId = taskId;
        ResultsCard.IsVisible = true;
        ProgressCard.IsVisible = false;
        ExistingProjectsStatus.Text = $"Loaded: {label}";
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
    private async void OnGeotag(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var top = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (top == null) return;

        // Select flight log
        var logs = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Select flight log (.tlog or .csv)",
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Flight logs")
                { Patterns = new[] { "*.tlog", "*.csv" } } }
        });
        if (logs.Count == 0) return;
        var logPath = logs[0].Path.LocalPath;

        // Select image folder
        var folders = await top.StorageProvider.OpenFolderPickerAsync(
            new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "Select image folder to geotag" });
        if (folders.Count == 0) return;
        var imgFolder = folders[0].Path.LocalPath;

        GeotagStatus.Text = "Geotagging images from flight log...";
        var script = "/home/sam/infradrone-desktop/tools/geotag_from_tlog.py";
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/home/sam/agridrone_env/bin/python3",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(logPath);
        psi.ArgumentList.Add(imgFolder);
        string output = "";
        await System.Threading.Tasks.Task.Run(() => {
            var proc = System.Diagnostics.Process.Start(psi);
            output = proc?.StandardOutput.ReadToEnd() ?? "";
            output += proc?.StandardError.ReadToEnd() ?? "";
            proc?.WaitForExit();
        });
        GeotagStatus.Text = output.Contains("Done:") ?
            "✓ " + output.Split('\n').LastOrDefault(l => l.Contains("Done:")) :
            "Error — check terminal output";
    }

    private async void OnGeotagNmea(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var top = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (top == null) return;

        // Select NMEA file
        var files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Select Sequoia NMEA file (generated_nmea.txt)",
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("NMEA files")
                { Patterns = new[] { "*.txt", "*nmea*" } } }
        });
        if (files.Count == 0) return;
        var nmea = files[0].Path.LocalPath;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(
            new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "Select Sequoia image folder" });
        if (folders.Count == 0) return;
        var imgFolder = folders[0].Path.LocalPath;

        GeotagNmeaStatus.Text = "Geotagging from Sequoia NMEA...";
        var script = "/home/sam/infradrone-desktop/tools/geotag_from_nmea.py";
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/home/sam/agridrone_env/bin/python3",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(nmea);
        psi.ArgumentList.Add(imgFolder);
        string output = "";
        await System.Threading.Tasks.Task.Run(() => {
            var proc = System.Diagnostics.Process.Start(psi);
            output = proc?.StandardOutput.ReadToEnd() ?? "";
            output += proc?.StandardError.ReadToEnd() ?? "";
            proc?.WaitForExit();
        });
        GeotagNmeaStatus.Text = output.Contains("Done:") ?
            "✓ " + output.Split('\n').LastOrDefault(l => l.Contains("Done:")) :
            "Error — " + output.Split('\n').FirstOrDefault(l => l.Contains("ERROR")) ?? "check terminal";
    }

}