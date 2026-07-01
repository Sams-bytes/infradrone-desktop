using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InfraDroneDesktop.Views;

public class ParamItem
{
    public string Name { get; set; } = "";
    public string ValueStr { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
}

public partial class ParamsView : UserControl
{
    private List<ParamItem> _allParams = new();
    private string _search = "";
    private readonly InfraDroneDesktop.Services.BatteryHealthService _batteryHealth = new();
    private bool _batteryExpanded = false;

    private static readonly Dictionary<string, string> Descriptions = new()
    {
        {"ARMING_CHECK","Pre-arm checks bitmask"},{"ARMING_RUDDER","Rudder arming: 0=off 1=arm 2=arm/disarm"},
        {"FENCE_ENABLE","Geofence: 0=off 1=on"},{"FENCE_TYPE","Fence type bitmask"},
        {"FENCE_ALT_MAX","Max altitude fence (m)"},{"FENCE_RADIUS","Circle fence radius (m)"},
        {"RTL_ALT","RTL altitude (cm)"},{"RTL_SPEED","RTL speed cm/s (0=WPNAV)"},
        {"WPNAV_SPEED","Waypoint speed cm/s"},{"WPNAV_ACCEL","Waypoint acceleration cm/s/s"},
        {"WPNAV_SPEED_UP","Waypoint climb speed cm/s"},{"WPNAV_SPEED_DN","Waypoint descent speed cm/s"},
        {"MOT_SPIN_MIN","Min motor spin (0-1)"},{"MOT_SPIN_ARM","Arm motor spin (0-1)"},
        {"MOT_THST_HOVER","Hover throttle estimate (0-1)"},{"MOT_PWM_MIN","Min PWM output"},
        {"MOT_PWM_MAX","Max PWM output"},{"FRAME_CLASS","Frame: 1=quad 2=hexa 12=dodeca"},
        {"FRAME_TYPE","Frame type: 0=plus 1=X"},{"EKF3_ENABLE","EKF3: 0=off 1=on"},
        {"AHRS_EKF_TYPE","EKF type: 2=EKF2 3=EKF3"},{"GPS_TYPE","GPS type: 1=auto 2=uBlox"},
        {"BATT_CAPACITY","Battery capacity mAh"},{"BATT_LOW_VOLT","Low voltage warning"},
        {"BATT_CRT_VOLT","Critical voltage"},{"FS_BATT_ENABLE","Battery failsafe: 0=off 1=land 2=RTL"},
        {"FS_GCS_ENABLE","GCS failsafe: 0=off 1=RTL"},{"FS_THR_ENABLE","Throttle failsafe"},
        {"PILOT_SPEED_UP","Max climb rate cm/s"},{"PILOT_SPEED_DN","Max descent rate cm/s"},
        {"ANGLE_MAX","Max tilt centidegrees"},{"LOG_BITMASK","Dataflash log bitmask"},
        {"SYSID_THISMAV","MAVLink system ID"},{"INS_ACCEL_FILTER","Accel filter Hz"},
        {"INS_GYRO_FILTER","Gyro filter Hz"},{"COMPASS_USE","Use compass: 0=off 1=on"},
    };

    private static readonly Dictionary<string, string> CategoryMap = new()
    {
        {"ACRO","Acro Mode"},{"AHRS","AHRS / Attitude"},{"ARMING","Arming & Safety"},
        {"AVOID","Avoidance"},{"BATT","Battery"},{"CAM","Camera"},{"COMPASS","Compass"},
        {"EK2","EKF2"},{"EK3","EKF3"},{"FENCE","Geofence"},{"FS","Failsafe"},
        {"GPS","GPS"},{"INS","IMU / Sensors"},{"LOG","Logging"},{"MOT","Motors"},
        {"MNT","Gimbal"},{"PILOT","Pilot Controls"},{"POS","Position Control"},
        {"PSC","Position Controller"},{"RC","RC Input"},{"RELAY","Relay"},
        {"RTL","Return to Launch"},{"SCR","Scripting"},{"SERIAL","Serial Ports"},
        {"SERVO","Servo Output"},{"SR","Stream Rates"},{"SYSID","System"},
        {"WPNAV","Waypoint Navigation"},{"Q","QuadPlane"},{"LGR","Landing Gear"},
        {"NTF","Notify"},{"OSD","OSD"},{"RNGFND","Rangefinder"},{"LAND","Landing"},
    };

    private static string GetCategory(string name)
    {
        foreach (var kv in CategoryMap.OrderByDescending(k => k.Key.Length))
            if (name.StartsWith(kv.Key + "_") || name == kv.Key)
                return kv.Value;
        return "Other";
    }

    public ParamsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e) => LoadParams();

    private void LoadParams()
    {
        _allParams.Clear();
        var paths = new[] {
            "/home/sam/agri_drone/mav.parm",
            "/home/sam/infradrone-desktop/mav.parm"
        };
        var found = paths.FirstOrDefault(File.Exists);
        if (found == null) { StatusText.Text = "mav.parm not found — connect drone first"; return; }

        foreach (var line in File.ReadAllLines(found))
        {
            var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            _allParams.Add(new ParamItem
            {
                Name = parts[0], ValueStr = parts[1],
                Description = Descriptions.TryGetValue(parts[0], out var d) ? d : "",
                Category = GetCategory(parts[0])
            });
        }
        StatusText.Text = $"{_allParams.Count} parameters";
        BuildUI();
    }

    private void BuildUI()
    {
        ParamList.Children.Clear();
        var search = _search.ToUpper();
        var filtered = _allParams
            .Where(p => string.IsNullOrEmpty(search) ||
                        p.Name.Contains(search) || p.Description.ToUpper().Contains(search))
            .GroupBy(p => p.Category)
            .OrderBy(g => g.Key);

        CountText.Text = $"{filtered.Sum(g => g.Count())} shown";

        foreach (var group in filtered)
        {
            // Category header
            var header = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#1e3a5f")),
                Padding = new Avalonia.Thickness(12, 6),
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
                Child = new TextBlock
                {
                    Text = $"{group.Key.ToUpper()}  ({group.Count()})",
                    FontSize = 10, FontWeight = Avalonia.Media.FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#60a5fa")),
                    LetterSpacing = 1
                }
            };
            ParamList.Children.Add(header);

            // Column headers
            var colHeader = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("200,120,*"),
                Background = new SolidColorBrush(Color.Parse("#0f1923"))
            };
            foreach (var (txt, col) in new[] { ("PARAMETER", 0), ("VALUE", 1), ("DESCRIPTION", 2) })
                colHeader.Children.Add(new TextBlock
                {
                    Text = txt, FontSize = 8, FontWeight = Avalonia.Media.FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#4a5568")),
                    Margin = new Avalonia.Thickness(12, 4),
                    [Grid.ColumnProperty] = col
                });
            ParamList.Children.Add(colHeader);

            // Parameter rows
            int rowIdx = 0;
            foreach (var p in group.OrderBy(x => x.Name))
            {
                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("200,120,*"),
                    Background = rowIdx++ % 2 == 0
                        ? new SolidColorBrush(Color.Parse("#131f2e"))
                        : new SolidColorBrush(Color.Parse("#0f1923"))
                };

                row.Children.Add(new TextBlock
                {
                    Text = p.Name, FontFamily = new Avalonia.Media.FontFamily("Consolas"),
                    FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#0d9e75")),
                    Margin = new Avalonia.Thickness(12, 5), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    [Grid.ColumnProperty] = 0
                });

                var valueBox = new TextBox
                {
                    Text = p.ValueStr, FontFamily = new Avalonia.Media.FontFamily("Consolas"),
                    FontSize = 11, Background = new SolidColorBrush(Color.Parse("#1a2637")),
                    Foreground = new SolidColorBrush(Color.Parse("#e2e8f0")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#2d3f52")),
                    BorderThickness = new Avalonia.Thickness(0.5),
                    Padding = new Avalonia.Thickness(6, 3), CornerRadius = new Avalonia.CornerRadius(4),
                    Margin = new Avalonia.Thickness(4, 3),
                    [Grid.ColumnProperty] = 1
                };
                row.Children.Add(valueBox);

                row.Children.Add(new TextBlock
                {
                    Text = p.Description, FontSize = 10,
                    Foreground = new SolidColorBrush(Color.Parse("#64748b")),
                    Margin = new Avalonia.Thickness(8, 5), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    [Grid.ColumnProperty] = 2
                });

                ParamList.Children.Add(row);
            }
        }
    }

    private void OnSearch(object? sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text ?? "";
        BuildUI();
    }

    private void OnRefresh(object? s, RoutedEventArgs e) => LoadParams();
    private void OnSave(object? s, RoutedEventArgs e) =>
        StatusText.Text = "Parameter upload via command server — coming soon";

    private void OnToggleBattery(object? s, RoutedEventArgs e)
    {
        _batteryExpanded = !_batteryExpanded;
        BatteryPanel.IsVisible = _batteryExpanded;
        BtnToggleBattery.Content = _batteryExpanded
            ? "🔋 Battery health (tap to collapse)"
            : "🔋 Battery health (tap to expand)";
        if (_batteryExpanded) LoadBatteryHistory();
    }

    private void LoadBatteryHistory()
    {
        var history = _batteryHealth.LoadHistory();
        BatteryHistoryList.Items.Clear();
        if (history.Count == 0)
        {
            BatterySummaryText.Text = "No logged flights yet.";
            return;
        }
        var minVoltages = history.Select(h => h.MinVoltage).Where(v => v > 0).ToList();
        var avgMin = minVoltages.Count > 0 ? minVoltages.Average() : 0;
        BatterySummaryText.Text = $"{history.Count} flight(s) logged — avg min voltage {avgMin:F2}V";

        foreach (var r in history.AsEnumerable().Reverse())
        {
            var dt = DateTime.Parse(r.Timestamp).ToLocalTime();
            BatteryHistoryList.Items.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#1a2637")),
                CornerRadius = new Avalonia.CornerRadius(6),
                Padding = new Avalonia.Thickness(8, 6),
                Margin = new Avalonia.Thickness(0, 2),
                Child = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*"),
                    Children =
                    {
                        new TextBlock { Text = dt.ToString("dd MMM HH:mm"), FontSize = 10,
                            Foreground = new SolidColorBrush(Color.Parse("#94a3b8")),
                            Margin = new Avalonia.Thickness(0,0,12,0), [Grid.ColumnProperty] = 0 },
                        new TextBlock { Text = $"{r.DurationMinutes:F0}min", FontSize = 10,
                            Foreground = new SolidColorBrush(Color.Parse("#64748b")),
                            Margin = new Avalonia.Thickness(0,0,12,0), [Grid.ColumnProperty] = 1 },
                        new TextBlock { Text = $"{r.MinVoltage:F2}V–{r.MaxVoltage:F2}V", FontSize = 10,
                            FontFamily = new FontFamily("Consolas"),
                            Foreground = new SolidColorBrush(Color.Parse("#0d9e75")),
                            Margin = new Avalonia.Thickness(0,0,12,0), [Grid.ColumnProperty] = 2 },
                        new TextBlock { Text = $"{r.StartBatteryPct}%→{r.EndBatteryPct}%", FontSize = 10,
                            Foreground = new SolidColorBrush(Color.Parse("#e2e8f0")),
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                            [Grid.ColumnProperty] = 3 }
                    }
                }
            });
        }
    }
}
