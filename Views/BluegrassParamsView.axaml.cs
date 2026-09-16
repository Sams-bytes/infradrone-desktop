using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using InfraDroneDesktop.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;

namespace InfraDroneDesktop.Views
{
    public partial class BluegrassParamsView : UserControl
    {
        private BluegrassVehicleService? _bluegrass;
        private readonly Dictionary<string, Control> _settingInputs = new();
        private System.Timers.Timer? _telemetryTimer;

        public BluegrassParamsView()
        {
            InitializeComponent();
        }

        private void SetActiveTab(string tab)
        {
            TabOverview.IsVisible = tab == "overview";
            TabCalibration.IsVisible = tab == "calibration";
            TabCamera.IsVisible = tab == "camera";
            TabSequoiaCalibration.IsVisible = tab == "sequoiacal";
            if (tab == "sequoiacal") SequoiaCalibrationViewInstance.RefreshOnOpen();

            var active = new SolidColorBrush(Color.Parse("#0d3d2e"));
            var inactive = new SolidColorBrush(Color.Parse("#1a2637"));
            var activeFg = new SolidColorBrush(Color.Parse("#0d9e75"));
            var inactiveFg = new SolidColorBrush(Color.Parse("#94a3b8"));
            var activeBorder = new SolidColorBrush(Color.Parse("#0d9e75"));
            var inactiveBorder = new SolidColorBrush(Color.Parse("#2d3f52"));

            TabBtnOverview.Background = tab == "overview" ? active : inactive;
            TabBtnOverview.Foreground = tab == "overview" ? activeFg : inactiveFg;
            TabBtnOverview.BorderBrush = tab == "overview" ? activeBorder : inactiveBorder;

            TabBtnCalibration.Background = tab == "calibration" ? active : inactive;
            TabBtnCalibration.Foreground = tab == "calibration" ? activeFg : inactiveFg;
            TabBtnCalibration.BorderBrush = tab == "calibration" ? activeBorder : inactiveBorder;

            TabBtnCamera.Background = tab == "camera" ? active : inactive;
            TabBtnCamera.Foreground = tab == "camera" ? activeFg : inactiveFg;
            TabBtnCamera.BorderBrush = tab == "camera" ? activeBorder : inactiveBorder;

            TabBtnSequoiaCalibration.Background = tab == "sequoiacal" ? active : inactive;
            TabBtnSequoiaCalibration.Foreground = tab == "sequoiacal" ? activeFg : inactiveFg;
            TabBtnSequoiaCalibration.BorderBrush = tab == "sequoiacal" ? activeBorder : inactiveBorder;
        }

        private void OnTabOverview(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SetActiveTab("overview");
        private void OnTabCalibration(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SetActiveTab("calibration");
        private void OnTabCamera(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SetActiveTab("camera");
        private void OnTabSequoiaCalibration(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SetActiveTab("sequoiacal");

        public void SetBluegrass(BluegrassVehicleService bluegrass)
        {
            _bluegrass = bluegrass;
            _ = LoadSettingsAsync();

            _telemetryTimer?.Stop();
            _telemetryTimer = new System.Timers.Timer(2000);
            _telemetryTimer.Elapsed += async (_, _) => await RefreshTelemetryAsync();
            _telemetryTimer.Start();
            _ = RefreshTelemetryAsync();
        }

        private async void OnRefresh(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            await LoadSettingsAsync();
            await RefreshTelemetryAsync();
        }

        private async void OnFlatTrim(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_bluegrass == null) return;
            SetStatus("Running flat trim...");
            var ok = await _bluegrass.FlatTrimAsync();
            SetStatus(ok ? "Flat trim complete" : "Flat trim failed - check drone is connected");
        }

        private async Task LoadSettingsAsync()
        {
            if (_bluegrass == null) return;
            SetStatus("Loading settings...");

            var schema = await _bluegrass.GetSettingsSchemaAsync();
            if (schema == null)
            {
                SetStatus("Bridge unreachable - is bluegrass_bridge.py running?");
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                SettingsPanel.Children.Clear();
                _settingInputs.Clear();

                foreach (var kvp in schema.OrderBy(k => k.Key))
                {
                    var name = kvp.Key;
                    var def = kvp.Value;
                    if (def.Args.Count != 1) continue;

                    var argName = def.Args[0];
                    var rawRange = def.Ranges.TryGetValue(argName, out var r) ? r : null;
                    var range = rawRange?.Select(UnwrapJsonElement).ToList();

                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*,Auto"), Margin = new Avalonia.Thickness(0, 2) };

                    var label = new TextBlock
                    {
                        Text = name.Replace('_', ' '),
                        Foreground = new SolidColorBrush(Color.Parse("#94a3b8")),
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(label, 0);

                    Control inputControl;
                    bool isNumeric = range != null && range.Count == 2 && range[0] is not string;

                    if (isNumeric)
                    {
                        double min = Convert.ToDouble(range![0]);
                        double max = Convert.ToDouble(range[1]);
                        var slider = new Slider
                        {
                            Minimum = min,
                            Maximum = max,
                            Value = min,
                            TickFrequency = (max - min) / 20.0,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        inputControl = slider;
                    }
                    else if (range != null)
                    {
                        var combo = new ComboBox
                        {
                            ItemsSource = range.Select(v => v.ToString()).ToList(),
                            SelectedIndex = 0,
                            HorizontalAlignment = HorizontalAlignment.Stretch
                        };
                        inputControl = combo;
                    }
                    else
                    {
                        var box = new TextBox { Watermark = argName };
                        inputControl = box;
                    }
                    Grid.SetColumn(inputControl, 1);
                    _settingInputs[name] = inputControl;

                    var applyBtn = new Button
                    {
                        Content = "Set",
                        Background = new SolidColorBrush(Color.Parse("#0d3d2e")),
                        Foreground = new SolidColorBrush(Color.Parse("#0d9e75")),
                        BorderBrush = new SolidColorBrush(Color.Parse("#0d9e75")),
                        BorderThickness = new Avalonia.Thickness(0.5),
                        Padding = new Avalonia.Thickness(10, 3),
                        CornerRadius = new Avalonia.CornerRadius(6),
                        Margin = new Avalonia.Thickness(6, 0, 0, 0)
                    };
                    applyBtn.Click += async (_, _) => await ApplySettingAsync(name, argName, inputControl);
                    Grid.SetColumn(applyBtn, 2);

                    row.Children.Add(label);
                    row.Children.Add(inputControl);
                    row.Children.Add(applyBtn);
                    SettingsPanel.Children.Add(row);
                }

                SetStatus($"{schema.Count} settings loaded");
            });
        }

        private async Task ApplySettingAsync(string settingName, string argName, Control input)
        {
            if (_bluegrass == null) return;

            object? value = input switch
            {
                Slider s => s.Value,
                ComboBox c => c.SelectedItem?.ToString(),
                TextBox t => t.Text,
                _ => null
            };
            if (value == null) return;

            SetStatus($"Setting {settingName}...");
            var (ok, error) = await _bluegrass.SetSettingAsync(settingName, new Dictionary<string, object> { [argName] = value });
            SetStatus(ok ? $"{settingName} set OK" : $"Failed: {error}");
        }

        private async Task RefreshTelemetryAsync()
        {
            if (_bluegrass == null) return;
            var telemetry = await _bluegrass.GetFullTelemetryAsync();
            if (telemetry == null) return;

            Dispatcher.UIThread.Post(() =>
            {
                TelemetryList.ItemsSource = telemetry
                    .OrderBy(kv => kv.Key)
                    .Select(kv => $"{kv.Key}: {kv.Value}")
                    .ToList();
                TelemetryCountText.Text = $"{telemetry.Count} fields";

                if (telemetry.TryGetValue("MagnetoCalibrationRequiredState_required", out var reqObj))
                {
                    bool required = UnwrapJsonElement(reqObj) is double d && d != 0;
                    MagnetoStatusText.Text = required
                        ? "Compass calibration status: REQUIRED - calibrate via FreeFlight Pro/AIRINOV before flying"
                        : "Compass calibration status: OK, not required";
                }
            });
        }

        private static object UnwrapJsonElement(object value)
        {
            if (value is JsonElement je)
            {
                return je.ValueKind switch
                {
                    JsonValueKind.Number => je.GetDouble(),
                    JsonValueKind.String => je.GetString() ?? string.Empty,
                    _ => je.ToString()
                };
            }
            return value;
        }

        private void SetStatus(string text)
        {
            Dispatcher.UIThread.Post(() => StatusText.Text = text);
        }
    }
}
