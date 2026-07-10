using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace InfraDroneDesktop.Views
{
    public partial class NitrogenZonesView : UserControl
    {
        private string _lastLoadedFolder = "";
        private string _layer1Path = "";

        private readonly string _layer2Path =
            "/home/sam/sequoia_test/literature_reference_curve.png";

        public NitrogenZonesView()
        {
            InitializeComponent();
            LoadLayer2();
        }

        private void LoadLayer2()
        {
            if (File.Exists(_layer2Path))
            {
                using var stream = File.OpenRead(_layer2Path);
                Layer2Image.Source = new Avalonia.Media.Imaging.Bitmap(stream);
                Layer2Image.IsVisible = true;
                Layer2Status.IsVisible = false;
            }
            else
            {
                Layer2Status.Text = "File not found: " + _layer2Path;
            }
        }

        private void OnOpenLayer2(object? sender, RoutedEventArgs e)
        {
            if (!File.Exists(_layer2Path)) { Layer2Status.Text = "File not found."; return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "xdg-open", Arguments = _layer2Path,
                UseShellExecute = true
            });
        }

        private async void OnLoadFolder(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "Select Sequoia image folder" });
            if (folders.Count == 0) return;

            _lastLoadedFolder = folders[0].Path.LocalPath;
            StatusText.Text = $"Loaded: {_lastLoadedFolder}. Generating Layer 1...";

            var outDir = Path.Combine(Path.GetTempPath(), "nitrogen_zones_output");
            Directory.CreateDirectory(outDir);
            _layer1Path = Path.Combine(outDir, "ndre_zones_relative.png");

            await System.Threading.Tasks.Task.Run(() => RunGenerateMaps(_lastLoadedFolder, outDir));

            if (File.Exists(_layer1Path))
            {
                using var stream = File.OpenRead(_layer1Path);
                Layer1Image.Source = new Avalonia.Media.Imaging.Bitmap(stream);
                Layer1Image.IsVisible = true;
                Layer1Status.IsVisible = false;
                StatusText.Text = "Layer 1 generated.";
            }
            else
            {
                Layer1Status.Text = "Generation failed — check console output.";
                StatusText.Text = "Generation failed.";
            }
        }

        private void RunGenerateMaps(string folder, string outDir)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/home/sam/agridrone_env/bin/python3",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("/home/sam/sequoia_test/generate_maps.py");
            psi.ArgumentList.Add(folder);
            psi.ArgumentList.Add(outDir);
            var proc = System.Diagnostics.Process.Start(psi);
            var stdout = proc?.StandardOutput.ReadToEnd() ?? "";
            var stderr = proc?.StandardError.ReadToEnd() ?? "";
            proc?.WaitForExit();
            Console.WriteLine("generate_maps stdout: " + stdout);
            Console.WriteLine("generate_maps stderr: " + stderr);
        }

        private void OnOpenLayer1(object? sender, RoutedEventArgs e)
        {
            if (!File.Exists(_layer1Path)) { StatusText.Text = "File not found — run analysis first."; return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "xdg-open", Arguments = _layer1Path,
                UseShellExecute = true
            });
        }
    }
}
