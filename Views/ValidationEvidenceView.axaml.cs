using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace InfraDroneDesktop.Views
{
    public partial class ValidationEvidenceView : UserControl
    {
        private readonly string _groundTruthPath =
            "/home/sam/sequoia_test/wur_output_F3_7_55/layer3_ground_truth_validation.png";
        private readonly string _diurnalPath =
            "/home/sam/sequoia_test/diurnal_variability.png";
        private readonly string _crossSensorPath =
            "/home/sam/sequoia_test/cross_sensor_validation.png";
        private readonly string _thermalFusionPath =
            "/home/sam/sequoia_test/thermal_ndre_fusion.png";
        private readonly string _calibrationCheckPath =
            "/home/sam/sequoia_test/calibration_accuracy_check.png";

        private readonly (string Label, string Path)[] _flights = new (string, string)[]
        {
            ("F2  — 7:42am",  "/home/sam/wur_dataset/extracted/2017-06-21_Wageningen_TestField_Sequoia_F2_7_42"),
            ("F3  — 7:55am",  "/home/sam/wur_dataset/extracted/2017-06-21_Wageningen_TestField_Sequoia_F3_7_55"),
            ("F7  — 9:24am",  "/home/sam/wur_dataset/extracted/2017-06-21_Wageningen_TestField_Sequoia_F7_9_24"),
            ("F10 — 10:22am", "/home/sam/wur_dataset/extracted/2017-06-21_Wageningen_TestField_Sequoia_F10_10_22"),
            ("F15 — 12:15pm", "/home/sam/wur_dataset/extracted/2017-06-21_Wageningen_TestField_Sequoia_F15_12_15"),
            ("F19 — 2:40pm",  "/home/sam/wur_dataset/extracted/2017-06-21_Wageningen_TestField_Sequoia_F19_14_40"),
            ("F24 — 4:37pm",  "/home/sam/wur_dataset/extracted/2017-06-21_Wageningen_TestField_Sequoia_F24_16_37"),
            ("F30 — 6:34pm",  "/home/sam/wur_dataset/extracted/2017-06-21_Wageningen_TestField_Sequoia_F30_18_34"),
            ("F34 — 7:59pm",  "/home/sam/wur_dataset/extracted/2017-06-21_Wageningen_TestField_Sequoia_F34_19_59"),
        };

        private readonly string _pixelLevelPath =
            "/home/sam/sequoia_test/layer3_pixel_level_F3_7_55.png";

        public ValidationEvidenceView()
        {
            InitializeComponent();
            LoadEvidenceImages();
            foreach (var f in _flights) FlightSelector.Items.Add(f.Label);
            FlightSelector.SelectedIndex = 1;
            foreach (var f in _flights) PixelFlightSelector.Items.Add(f.Label);
            PixelFlightSelector.SelectedIndex = 1;
            LoadPixelLevelImage(_pixelLevelPath);
        }

        private void LoadPixelLevelImage(string path)
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                PixelLevelImage.Source = new Avalonia.Media.Imaging.Bitmap(stream);
                PixelLevelImage.IsVisible = true;
                PixelLevelStatus.IsVisible = false;
            }
            else
            {
                PixelLevelStatus.Text = "File not found: " + path;
            }
        }

        private async void OnRunPixelValidation(object? sender, RoutedEventArgs e)
        {
            if (PixelFlightSelector.SelectedIndex < 0) return;
            var selected = _flights[PixelFlightSelector.SelectedIndex];
            PixelRunStatusText.Text = $"Running pixel-level validation against {selected.Label}...";

            var outPath = "/home/sam/sequoia_test/wur_output_dynamic/layer3_pixel_level.png";
            System.IO.Directory.CreateDirectory("/home/sam/sequoia_test/wur_output_dynamic");

            await System.Threading.Tasks.Task.Run(() =>
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/home/sam/agridrone_env/bin/python3",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("/home/sam/sequoia_test/layer3_pixel_level.py");
                psi.ArgumentList.Add(selected.Path);
                psi.ArgumentList.Add(outPath);
                var proc = System.Diagnostics.Process.Start(psi);
                var stdout = proc?.StandardOutput.ReadToEnd() ?? "";
                var stderr = proc?.StandardError.ReadToEnd() ?? "";
                proc?.WaitForExit();
                Console.WriteLine("pixel validation stdout: " + stdout);
                Console.WriteLine("pixel validation stderr: " + stderr);
            });

            if (File.Exists(outPath))
            {
                LoadPixelLevelImage(outPath);
                PixelRunStatusText.Text = $"Updated: showing {selected.Label}.";
            }
            else
            {
                PixelRunStatusText.Text = "Validation failed — check console output.";
            }
        }

        private void OnOpenPixelLevel(object? sender, RoutedEventArgs e) => OpenInSystemViewer(_pixelLevelPath);

        private async void OnRunValidation(object? sender, RoutedEventArgs e)
        {
            if (FlightSelector.SelectedIndex < 0) return;
            var selected = _flights[FlightSelector.SelectedIndex];
            RunStatusText.Text = $"Running validation against {selected.Label}...";

            var outPath = "/home/sam/sequoia_test/wur_output_dynamic/layer3_ground_truth_validation.png";
            System.IO.Directory.CreateDirectory("/home/sam/sequoia_test/wur_output_dynamic");

            await System.Threading.Tasks.Task.Run(() =>
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/home/sam/agridrone_env/bin/python3",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("/home/sam/sequoia_test/ground_truth_validation.py");
                psi.ArgumentList.Add(selected.Path);
                psi.ArgumentList.Add(outPath);
                var proc = System.Diagnostics.Process.Start(psi);
                var stdout = proc?.StandardOutput.ReadToEnd() ?? "";
                var stderr = proc?.StandardError.ReadToEnd() ?? "";
                proc?.WaitForExit();
                Console.WriteLine("validation stdout: " + stdout);
                Console.WriteLine("validation stderr: " + stderr);
            });

            if (File.Exists(outPath))
            {
                using var stream = File.OpenRead(outPath);
                GroundTruthImage.Source = new Avalonia.Media.Imaging.Bitmap(stream);
                GroundTruthImage.IsVisible = true;
                GroundTruthStatus.IsVisible = false;
                RunStatusText.Text = $"Updated: showing {selected.Label}.";
            }
            else
            {
                RunStatusText.Text = "Validation failed — check console output.";
            }
        }

        private void LoadEvidenceImages()
        {
            if (File.Exists(_groundTruthPath))
            {
                using var stream = File.OpenRead(_groundTruthPath);
                GroundTruthImage.Source = new Avalonia.Media.Imaging.Bitmap(stream);
                GroundTruthImage.IsVisible = true;
                GroundTruthStatus.IsVisible = false;
            }
            else
            {
                GroundTruthStatus.Text = "File not found: " + _groundTruthPath;
            }

            if (File.Exists(_diurnalPath))
            {
                using var stream2 = File.OpenRead(_diurnalPath);
                DiurnalImage.Source = new Avalonia.Media.Imaging.Bitmap(stream2);
                DiurnalImage.IsVisible = true;
                DiurnalStatus.IsVisible = false;
            }
            else
            {
                DiurnalStatus.Text = "File not found: " + _diurnalPath;
            }

            if (File.Exists(_crossSensorPath))
            {
                using var stream3 = File.OpenRead(_crossSensorPath);
                CrossSensorImage.Source = new Avalonia.Media.Imaging.Bitmap(stream3);
                CrossSensorImage.IsVisible = true;
                CrossSensorStatus.IsVisible = false;
            }
            else
            {
                CrossSensorStatus.Text = "File not found: " + _crossSensorPath;
            }

            if (File.Exists(_thermalFusionPath))
            {
                using var stream4 = File.OpenRead(_thermalFusionPath);
                ThermalFusionImage.Source = new Avalonia.Media.Imaging.Bitmap(stream4);
                ThermalFusionImage.IsVisible = true;
                ThermalFusionStatus.IsVisible = false;
            }
            else
            {
                ThermalFusionStatus.Text = "File not found: " + _thermalFusionPath;
            }

            if (File.Exists(_calibrationCheckPath))
            {
                using var stream5 = File.OpenRead(_calibrationCheckPath);
                CalibrationCheckImage.Source = new Avalonia.Media.Imaging.Bitmap(stream5);
                CalibrationCheckImage.IsVisible = true;
                CalibrationCheckStatus.IsVisible = false;
            }
            else
            {
                CalibrationCheckStatus.Text = "File not found: " + _calibrationCheckPath;
            }
        }

        private void OnOpenGroundTruth(object? sender, RoutedEventArgs e) => OpenInSystemViewer(_groundTruthPath);
        private void OnOpenDiurnal(object? sender, RoutedEventArgs e) => OpenInSystemViewer(_diurnalPath);
        private void OnOpenCrossSensor(object? sender, RoutedEventArgs e) => OpenInSystemViewer(_crossSensorPath);
        private void OnOpenThermalFusion(object? sender, RoutedEventArgs e) => OpenInSystemViewer(_thermalFusionPath);
        private void OnOpenCalibrationCheck(object? sender, RoutedEventArgs e) => OpenInSystemViewer(_calibrationCheckPath);

        private void OpenInSystemViewer(string path)
        {
            if (!File.Exists(path)) { GroundTruthStatus.Text = "File not found."; return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "xdg-open", Arguments = path,
                UseShellExecute = true
            });
        }
    }
}
