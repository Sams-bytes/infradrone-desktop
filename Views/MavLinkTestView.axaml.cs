using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Asv.Mavlink;
using InfraDroneDesktop.Services;

namespace InfraDroneDesktop.Views
{
    public partial class MavLinkTestView : UserControl
    {
        private IMavlinkV2Connection? _connection;
        private MavlinkDeviceBrowser? _browser;
        private int _packetCount = 0;
        private DateTime _startTime;
        private DispatcherTimer? _uiTimer;

        public MavLinkTestView()
        {
            InitializeComponent();
        }

        private Mavlink1SerialService? _v1Service;

        private void OnTestConnect(object? sender, RoutedEventArgs e)
        {
            if (ControllerSelect.SelectedIndex == 1)
            {
                OnTestConnectV1();
                return;
            }

            try
            {
                StatusText.Text = "Connecting...";
                _packetCount = 0;
                _startTime = DateTime.UtcNow;

                _connection = MavlinkV2Connection.Create("udp://127.0.0.1:14571");
                LogAppend("[TEST] Connection object created for udp://127.0.0.1:14571");

                _connection.Subscribe(packet =>
                {
                    _packetCount++;
                });

                _browser = new MavlinkDeviceBrowser(_connection, TimeSpan.FromSeconds(10),
                    System.Reactive.Concurrency.Scheduler.Default);

                _browser.Devices.Subscribe(changeSet =>
                {
                    foreach (var change in changeSet)
                    {
                        if (change.Reason == DynamicData.ChangeReason.Add)
                        {
                            var d = change.Current;
                            Dispatcher.UIThread.Post(() =>
                            {
                                DeviceText.Text = $"Device found: sysid={d.SystemId}, compid={d.ComponentId}, type={d.Type}, autopilot={d.Autopilot}";
                                LogAppend($"[TEST] Real vehicle discovered: sysid={d.SystemId}, compid={d.ComponentId}");
                            });
                        }
                    }
                });

                StatusText.Text = "Connected -- listening for real telemetry...";
                BtnTestConnect.IsEnabled = false;
                BtnCheckFence.IsEnabled = true;
                _mavService = new AsvMavLinkService();
                _mavService.Start("udp://127.0.0.1:14571?rhost=127.0.0.1&rport=14445");

                _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _uiTimer.Tick += (s, args) =>
                {
                    var elapsed = (DateTime.UtcNow - _startTime).TotalSeconds;
                    PacketCountText.Text = $"Packets processed: {_packetCount}";
                    ElapsedText.Text = $"Elapsed: {elapsed:F0}s (still running = no crash)";
                };
                _uiTimer.Start();
            }
            catch (Exception ex)
            {
                StatusText.Text = "ERROR: " + ex.Message;
                LogAppend("[TEST] Exception: " + ex.ToString());
            }
        }

        // Isolated BCube / older-Pixhawk (MAVLink v1) connection path.
        // Completely separate from the working Cube Orange / v2 path above --
        // does not touch _connection, _browser, or _mavService.
        private void OnTestConnectV1()
        {
            V1StatusCard.IsVisible = true;
            V1StatusText.Text = "Connecting on /dev/bcube @ 57600 baud (read-only)...";
            BtnTestConnect.IsEnabled = false;

            _v1Service = new Mavlink1SerialService();
            _v1Service.TelemetryUpdated += t =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    V1StatusText.Text =
                        $"Connected. sysid={t.SystemId} compid={t.ComponentId} " +
                        $"autopilot={t.Autopilot} type={t.VehicleType} armed={t.Armed} " +
                        $"customMode={t.CustomMode} systemStatus={t.SystemStatus}";
                    BtnV1SafeSendTest.IsVisible = true;
                    BtnV1Arm.IsVisible = true;
                    BtnV1Disarm.IsVisible = true;
                });
            };
            _v1Service.CommandAckReceived += (cmd, result) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    V1SendResultText.IsVisible = true;
                    V1SendResultText.Text = $"COMMAND_ACK received: command={cmd}, result={result} " +
                        (result == 0 ? "(0 = MAV_RESULT_ACCEPTED -- real round-trip confirmed working)" : $"(non-zero result, see MAV_RESULT enum)");
                });
            };

            bool ok = _v1Service.Start("/dev/bcube", 57600);
            if (!ok)
            {
                V1StatusText.Text = "Failed to open port -- check the device path and that no other program (e.g. QGroundControl) has it open.";
                BtnTestConnect.IsEnabled = true;
            }
        }

        // MAV_CMD_REQUEST_AUTOPILOT_CAPABILITIES = 520. Purely informational --
        // asks the vehicle to report its capabilities. Zero effect on flight
        // state, arming, or motors. Used here only to prove the send->ACK
        // round-trip genuinely works before ever sending Arm/Takeoff.
        private void OnV1SafeSendTest(object? sender, RoutedEventArgs e)
        {
            if (_v1Service == null) return;
            V1SendResultText.IsVisible = true;
            V1SendResultText.Text = "Sending MAV_CMD_REQUEST_AUTOPILOT_CAPABILITIES (520)...";
            _v1Service.SendCommandLong(
                targetSystem: _v1Service.Telemetry.SystemId,
                targetComponent: _v1Service.Telemetry.ComponentId,
                command: 520, p1: 1);
        }

        // MAV_CMD_COMPONENT_ARM_DISARM = 400. param1: 1 = arm, 0 = disarm.
        // Real, physical-effect command -- only reachable via explicit button
        // click, never sent automatically.
        private void OnV1Arm(object? sender, RoutedEventArgs e)
        {
            if (_v1Service == null) return;
            V1SendResultText.IsVisible = true;
            V1SendResultText.Text = "Sending ARM command...";
            _v1Service.SendCommandLong(
                targetSystem: _v1Service.Telemetry.SystemId,
                targetComponent: _v1Service.Telemetry.ComponentId,
                command: 400, p1: 1);
        }

        private void OnV1Disarm(object? sender, RoutedEventArgs e)
        {
            if (_v1Service == null) return;
            V1SendResultText.IsVisible = true;
            V1SendResultText.Text = "Sending DISARM command...";
            _v1Service.SendCommandLong(
                targetSystem: _v1Service.Telemetry.SystemId,
                targetComponent: _v1Service.Telemetry.ComponentId,
                command: 400, p1: 0);
        }

        private async void OnCheckFence(object? sender, RoutedEventArgs e)
        {
            FenceParamsPanel.Children.Clear();
            var names = new[] { "FENCE_ENABLE", "FENCE_TYPE", "FENCE_ACTION", "FENCE_RADIUS", "FENCE_ALT_MAX", "FENCE_MARGIN" };
            foreach (var name in names)
            {
                var value = await _mavService!.ReadParamAsync(name);
                var text = value.HasValue ? $"{name} = {value.Value}" : $"{name} = FAILED TO READ";
                var color = value.HasValue ? "#0d9e75" : "#ef4444";
                FenceParamsPanel.Children.Add(new TextBlock
                {
                    Text = text, FontSize = 12, FontFamily = new Avalonia.Media.FontFamily("Consolas"),
                    Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(color))
                });
            }

            var enableVal = await _mavService!.ReadParamAsync("FENCE_ENABLE");
            if (enableVal.HasValue && enableVal.Value == 0)
            {
                FenceParamsPanel.Children.Add(new TextBlock
                {
                    Text = "⚠ FENCE_ENABLE = 0 -- geofence is currently OFF on this vehicle.",
                    FontSize = 12, FontWeight = Avalonia.Media.FontWeight.Bold,
                    Foreground = Avalonia.Media.Brushes.OrangeRed, TextWrapping = Avalonia.Media.TextWrapping.Wrap
                });
            }
        }

        private AsvMavLinkService? _mavService;

        private void LogAppend(string line)
        {
            Console.WriteLine(line);
            Dispatcher.UIThread.Post(() =>
            {
                LogText.Text = line + "\n" + LogText.Text;
            });
        }
    }
}
