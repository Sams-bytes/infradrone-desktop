using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Asv.Mavlink;

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

        private void OnTestConnect(object? sender, RoutedEventArgs e)
        {
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
