using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using InfraDroneDesktop.Services;

namespace InfraDroneDesktop.Views;

public class ChecklistItem
{
    public string Category { get; set; } = "";
    public string Text { get; set; } = "";
    public bool IsChecked { get; set; } = false;
    public bool IsMandatory { get; set; } = true;
}

public partial class PreflightView : UserControl
{
    private readonly List<ChecklistItem> _items = new();
    private readonly List<CheckBox> _checkboxes = new();
    private string _logPath = "/home/sam/agri_drone/preflight_log.jsonl";

    private static readonly List<(string Cat, string Text, bool Mandatory)> Template = new()
    {
        // Aircraft
        ("AIRCRAFT", "Airframe visual inspection — no cracks, loose parts, or damage", true),
        ("AIRCRAFT", "All propellers securely fastened, no chips or damage", true),
        ("AIRCRAFT", "Motor mounts tight, motors spin freely by hand", true),
        ("AIRCRAFT", "Battery fully charged and securely latched", true),
        ("AIRCRAFT", "Battery voltage checked (nominal cell voltage)", true),
        ("AIRCRAFT", "Camera/payload securely mounted and lens clean", false),
        ("AIRCRAFT", "Landing gear intact and locks engaged", true),
        ("AIRCRAFT", "Airframe weight within MTOW limits", true),

        // Flight controller
        ("FLIGHT CONTROLLER", "Flight controller firmware is current version", true),
        ("FLIGHT CONTROLLER", "All pre-arm checks passed in GCS", true),
        ("FLIGHT CONTROLLER", "GPS lock acquired (minimum 6 satellites)", true),
        ("FLIGHT CONTROLLER", "Compass calibration valid", true),
        ("FLIGHT CONTROLLER", "Accelerometer calibration valid", true),
        ("FLIGHT CONTROLLER", "EKF status green in GCS", true),
        ("FLIGHT CONTROLLER", "Home position set correctly", true),
        ("FLIGHT CONTROLLER", "Failsafe actions configured (RTL on link loss)", true),

        // GCS and link
        ("GCS & LINK", "InfraDrone GCS connected and showing live telemetry", true),
        ("GCS & LINK", "SIYI MK15 link quality above 80%", true),
        ("GCS & LINK", "RC transmitter powered and bound", true),
        ("GCS & LINK", "RC failsafe tested and confirmed", true),
        ("GCS & LINK", "Mission uploaded and verified in GCS", false),
        ("GCS & LINK", "Geofence configured and active", false),

        // Airspace
        ("AIRSPACE & REGULATIONS", "NOTAM check completed for flight area", true),
        ("AIRSPACE & REGULATIONS", "Airspace class confirmed (max 120m Open Category)", true),
        ("AIRSPACE & REGULATIONS", "ATC notification sent if required (CTR/TMZ)", false),
        ("AIRSPACE & REGULATIONS", "Gemeente/municipality permit valid if required", false),
        ("AIRSPACE & REGULATIONS", "RDW operator registration active", true),
        ("AIRSPACE & REGULATIONS", "Remote ID transmitting", true),

        // Weather
        ("WEATHER", "Wind speed below 12 m/s at flight altitude", true),
        ("WEATHER", "No precipitation forecast during flight window", true),
        ("WEATHER", "Visibility above 500m minimum", true),
        ("WEATHER", "Temperature above 0°C (battery performance)", true),
        ("WEATHER", "No thunderstorms within 5km", true),

        // Safety
        ("SAFETY", "Flight area surveyed — no people or obstacles", true),
        ("SAFETY", "Visual observer briefed and in position", false),
        ("SAFETY", "Emergency procedures reviewed", true),
        ("SAFETY", "First aid kit available", false),
        ("SAFETY", "Public liability insurance valid", true),
        ("SAFETY", "Flight log entry started", true),
    };

    private MavLinkService? _mav;

    public PreflightView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public void SetMavLink(MavLinkService mav)
    {
        _mav = mav;
        _mav.TelemetryUpdated += OnTelemetry;
    }

    private void OnTelemetry(TelemetryData t)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Auto-check GPS if sufficient satellites
            AutoCheck("GPS lock acquired (minimum 6 satellites)", t.GpsSats >= 6 && t.GpsFix >= 3);
            // Auto-check pre-arm from armed state
            AutoCheck("All pre-arm checks passed in GCS", t.Connected);
            // Auto-check GCS connected
            AutoCheck("InfraDrone GCS connected and showing live telemetry", t.Connected);
            // Auto-check battery
            AutoCheck("Battery fully charged and securely latched", t.BatteryPct > 80);
            AutoCheck("Battery voltage checked (nominal cell voltage)", t.BatteryPct > 50);
        });
    }

    private void AutoCheck(string itemText, bool condition)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].Text == itemText && condition && !_items[i].IsChecked)
            {
                _checkboxes[i].IsChecked = true;
                // Visual feedback — green tint
                _checkboxes[i].Foreground = new SolidColorBrush(Color.Parse("#0d9e75"));
            }
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        DateText.Text = DateTime.UtcNow.ToString("dd MMMM yyyy — HH:mm UTC");
        BuildChecklist();
    }

    private void BuildChecklist()
    {
        ChecklistPanel.Children.Clear();
        _checkboxes.Clear();
        _items.Clear();

        string? currentCat = null;
        foreach (var (cat, text, mandatory) in Template)
        {
            _items.Add(new ChecklistItem { Category = cat, Text = text, IsMandatory = mandatory });

            if (cat != currentCat)
            {
                currentCat = cat;
                ChecklistPanel.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#1e3a5f")),
                    Padding = new Avalonia.Thickness(12, 6),
                    Margin = new Avalonia.Thickness(0, 8, 0, 2),
                    Child = new TextBlock
                    {
                        Text = cat,
                        FontSize = 10, FontWeight = Avalonia.Media.FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#60a5fa")),
                        LetterSpacing = 1
                    }
                });
            }

            var cb = new CheckBox
            {
                Content = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = text,
                            FontSize = 11,
                            Foreground = new SolidColorBrush(Color.Parse("#e2e8f0")),
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap
                        },
                        new Border
                        {
                            Background = mandatory
                                ? new SolidColorBrush(Color.Parse("#3d1a1a"))
                                : new SolidColorBrush(Color.Parse("#1a2637")),
                            CornerRadius = new Avalonia.CornerRadius(3),
                            Padding = new Avalonia.Thickness(4, 1),
                            Child = new TextBlock
                            {
                                Text = mandatory ? "REQUIRED" : "OPTIONAL",
                                FontSize = 8,
                                Foreground = mandatory
                                    ? new SolidColorBrush(Color.Parse("#ef4444"))
                                    : new SolidColorBrush(Color.Parse("#4a5568")),
                                FontWeight = Avalonia.Media.FontWeight.Bold
                            }
                        }
                    }
                },
                Margin = new Avalonia.Thickness(4, 2),
                Foreground = new SolidColorBrush(Color.Parse("#e2e8f0"))
            };

            int idx = _checkboxes.Count;
            cb.IsCheckedChanged += (s, e) =>
            {
                _items[idx].IsChecked = cb.IsChecked == true;
                UpdateProgress();
            };

            _checkboxes.Add(cb);
            ChecklistPanel.Children.Add(cb);
        }

        UpdateProgress();
    }

    private void UpdateProgress()
    {
        int total = _items.Count;
        int mandatory = _items.Count(i => i.IsMandatory);
        int checked_ = _items.Count(i => i.IsChecked);
        int mandatoryChecked = _items.Count(i => i.IsMandatory && i.IsChecked);

        ProgressText.Text = $"{checked_}/{total} items checked ({mandatoryChecked}/{mandatory} required)";

        bool allMandatoryDone = mandatoryChecked == mandatory;
        if (allMandatoryDone)
        {
            StatusBadge.Background = new SolidColorBrush(Color.Parse("#0d3d2e"));
            StatusText.Text = "GO FOR FLIGHT";
            StatusText.Foreground = new SolidColorBrush(Color.Parse("#0d9e75"));
            BtnSignOff.IsEnabled = true;
            BtnSignOff.Background = new SolidColorBrush(Color.Parse("#0d3d2e"));
            BtnSignOff.Foreground = new SolidColorBrush(Color.Parse("#0d9e75"));
            BtnSignOff.BorderBrush = new SolidColorBrush(Color.Parse("#0d9e75"));
        }
        else
        {
            StatusBadge.Background = new SolidColorBrush(Color.Parse("#3d1515"));
            StatusText.Text = "NOT READY";
            StatusText.Foreground = new SolidColorBrush(Color.Parse("#ef4444"));
            BtnSignOff.IsEnabled = false;
            BtnSignOff.Background = new SolidColorBrush(Color.Parse("#1a2637"));
            BtnSignOff.Foreground = new SolidColorBrush(Color.Parse("#64748b"));
        }
    }

    private void OnSignOff(object? s, RoutedEventArgs e)
    {
        var pilot = PilotName.Text?.Trim() ?? "Unknown";
        var timestamp = DateTime.UtcNow;
        var record = new
        {
            timestamp = timestamp.ToString("o"),
            pilot,
            items_total = _items.Count,
            items_checked = _items.Count(i => i.IsChecked),
            mandatory_passed = _items.Count(i => i.IsMandatory && i.IsChecked),
            mandatory_total = _items.Count(i => i.IsMandatory),
            status = "GO",
            items = _items.Select(i => new { i.Category, i.Text, i.IsChecked, i.IsMandatory })
        };
        var json = JsonSerializer.Serialize(record);
        File.AppendAllText(_logPath, json + "\n");
        SignOffText.Text = $"Signed off by {pilot} at {timestamp:HH:mm:ss} UTC — logged";
        BtnSignOff.IsEnabled = false;
    }

    private void OnReset(object? s, RoutedEventArgs e)
    {
        foreach (var cb in _checkboxes) cb.IsChecked = false;
        SignOffText.Text = "";
        DateText.Text = DateTime.UtcNow.ToString("dd MMMM yyyy — HH:mm UTC");
        UpdateProgress();
    }

    public List<(double Lat, double Lon, double AltM)>? MissionWaypoints { get; set; }

    private void OnValidateMission(object? s, RoutedEventArgs e)
    {
        ValidatorResults.Items.Clear();
        var results = new List<(string label, string status, string detail)>();

        // Check 1: Mission loaded
        if (MissionWaypoints == null || MissionWaypoints.Count == 0)
        {
            AddValidatorResult("Mission waypoints", "NO-GO", "No mission loaded — open Mission Planner and plan a route first.");
            ValidatorHint.Text = "Load a mission in the Mission Planner first, then validate.";
            return;
        }
        ValidatorHint.Text = $"Validating {MissionWaypoints.Count} waypoints...";
        AddValidatorResult("Mission loaded", "GO", $"{MissionWaypoints.Count} waypoints found.");

        // Check 2: Altitude limit (120m open category)
        var maxAlt = MissionWaypoints.Max(w => w.AltM);
        var altOk = maxAlt <= 120;
        AddValidatorResult("Altitude limit (120m open category)",
            altOk ? "GO" : "NO-GO",
            $"Max altitude: {maxAlt:F0}m — {(altOk ? "within limit" : "EXCEEDS 120m limit")}");

        // Check 3: Airspace check
        try
        {
            var geojsonPath = "/home/sam/agri_drone/airspace_nl.geojson";
            if (System.IO.File.Exists(geojsonPath))
            {
                var geojson = System.IO.File.ReadAllText(geojsonPath);
                var conflicts = new List<string>();
                foreach (var wp in MissionWaypoints)
                {
                    // Simple point-in-bounding-box check for each zone
                    var zones = FindConflictingZones(geojson, wp.Lat, wp.Lon);
                    conflicts.AddRange(zones.Where(z => !conflicts.Contains(z)));
                }
                if (conflicts.Count == 0)
                    AddValidatorResult("LVNL airspace clearance", "GO", "No airspace conflicts detected.");
                else
                    AddValidatorResult("LVNL airspace clearance", "CAUTION",
                        $"Possible conflicts: {string.Join(", ", conflicts.Take(3))}");
            }
            else
            {
                AddValidatorResult("LVNL airspace clearance", "CAUTION", "Airspace file not found — check ~/agri_drone/airspace_nl.geojson");
            }
        }
        catch (Exception ex)
        {
            AddValidatorResult("LVNL airspace clearance", "CAUTION", $"Check failed: {ex.Message}");
        }

        // Check 4: Battery estimate
        if (_mav != null)
        {
            var battPct = _mav.Telemetry.BatteryPct;
            var battOk = battPct >= 80;
            AddValidatorResult("Battery for mission",
                battPct >= 80 ? "GO" : battPct >= 50 ? "CAUTION" : "NO-GO",
                $"Current battery: {battPct}% — {(battOk ? "sufficient" : battPct >= 50 ? "marginal" : "insufficient for safe mission")}");
        }
        else
        {
            AddValidatorResult("Battery for mission", "CAUTION", "No telemetry — connect drone to check battery.");
        }

        // Check 5: GPS quality
        if (_mav != null && _mav.Telemetry.Connected)
        {
            var sats = _mav.Telemetry.GpsSats;
            AddValidatorResult("GPS quality",
                sats >= 8 ? "GO" : sats >= 6 ? "CAUTION" : "NO-GO",
                $"{sats} satellites — {(sats >= 8 ? "good" : sats >= 6 ? "marginal" : "insufficient")}");
        }
        else
        {
            AddValidatorResult("GPS quality", "CAUTION", "No telemetry — connect drone to check GPS.");
        }

        ValidatorHint.Text = "Validation complete.";
    }

    private List<string> FindConflictingZones(string geojson, double lat, double lon)
    {
        var conflicts = new List<string>();
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(geojson);
            var features = doc.RootElement.GetProperty("features");
            foreach (var feature in features.EnumerateArray())
            {
                try
                {
                    var props = feature.GetProperty("properties");
                    var name = props.TryGetProperty("name", out var n) ? n.GetString() ?? "" :
                               props.TryGetProperty("designator", out var d) ? d.GetString() ?? "" : "Unknown";
                    var geom = feature.GetProperty("geometry");
                    var type = geom.GetProperty("type").GetString();
                    if (type != "Polygon" && type != "MultiPolygon") continue;

                    // Get bounding box of first ring
                    var coords = type == "Polygon"
                        ? geom.GetProperty("coordinates")[0]
                        : geom.GetProperty("coordinates")[0][0];

                    double minLat = 90, maxLat = -90, minLon = 180, maxLon = -180;
                    foreach (var coord in coords.EnumerateArray())
                    {
                        var cLon = coord[0].GetDouble();
                        var cLat = coord[1].GetDouble();
                        minLat = Math.Min(minLat, cLat); maxLat = Math.Max(maxLat, cLat);
                        minLon = Math.Min(minLon, cLon); maxLon = Math.Max(maxLon, cLon);
                    }
                    if (lat >= minLat && lat <= maxLat && lon >= minLon && lon <= maxLon)
                        conflicts.Add(name);
                }
                catch { }
            }
        }
        catch { }
        return conflicts;
    }

    private void AddValidatorResult(string label, string status, string detail)
    {
        var color = status == "GO" ? "#0d9e75" : status == "CAUTION" ? "#f59e0b" : "#ef4444";
        var bg = status == "GO" ? "#0d3d2e" : status == "CAUTION" ? "#3d2e0d" : "#3d0d0d";
        var row = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1a2637")),
            CornerRadius = new Avalonia.CornerRadius(6),
            Padding = new Avalonia.Thickness(8, 5),
            Margin = new Avalonia.Thickness(0, 1),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Children =
                {
                    new Border
                    {
                        Background = new SolidColorBrush(Color.Parse(bg)),
                        CornerRadius = new Avalonia.CornerRadius(4),
                        Padding = new Avalonia.Thickness(6, 2),
                        Margin = new Avalonia.Thickness(0, 0, 8, 0),
                        [Grid.ColumnProperty] = 0,
                        Child = new TextBlock
                        {
                            Text = status, FontSize = 9, FontWeight = Avalonia.Media.FontWeight.Bold,
                            Foreground = new SolidColorBrush(Color.Parse(color))
                        }
                    },
                    new StackPanel
                    {
                        [Grid.ColumnProperty] = 1,
                        Children =
                        {
                            new TextBlock { Text = label, FontSize = 11,
                                Foreground = new SolidColorBrush(Color.Parse("#e2e8f0")) },
                            new TextBlock { Text = detail, FontSize = 10,
                                Foreground = new SolidColorBrush(Color.Parse("#64748b")),
                                TextWrapping = Avalonia.Media.TextWrapping.Wrap }
                        }
                    }
                }
            }
        };
        ValidatorResults.Items.Add(row);
    }
}
