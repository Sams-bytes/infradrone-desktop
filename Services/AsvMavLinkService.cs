using System;
using System.Threading;
using System.Threading.Tasks;
using Asv.Mavlink;
using Asv.Mavlink.V2.Common;

namespace InfraDroneDesktop.Services
{
    public class AsvTelemetryData
    {
        public bool Connected { get; set; }
        public string FlightMode { get; set; } = "\u2014";
        public bool Armed { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public double AltRel { get; set; }
        public double Speed { get; set; }
        public double Heading { get; set; }
        public int GpsSats { get; set; }
        public int GpsFix { get; set; }
        public int BatteryPct { get; set; }
        public float BatteryVoltage { get; set; }
        public double Roll { get; set; }
        public double Pitch { get; set; }
    }

    public class AsvMavLinkService
    {
        private IMavlinkV2Connection? _connection;
        private ArduPlaneClient? _vehicle;
        private CommandClient? _commandClient;
        private PositionClient? _positionClient;
        private HeartbeatClient? _heartbeatClient;
        private TelemetryClient? _telemetryClient;
        private GnssClient? _gnssClient;
        private MavlinkDeviceBrowser? _browser;
        private IDisposable? _deviceSubscription;
        private readonly System.Collections.Generic.List<IDisposable> _telemetrySubscriptions = new();

        private static readonly System.Collections.Generic.Dictionary<uint, string> PlaneModes = new()
        {
            {0,"Manual"},{2,"Stabilize"},{5,"FBWA"},{6,"FBWB"},{7,"Cruise"},{10,"Auto"},
            {11,"RTL"},{12,"Loiter"},{15,"Guided"},{17,"QSTABILIZE"},{18,"QHOVER"},
            {19,"QLOITER"},{20,"QLAND"},{21,"QRTL"}
        };

        public AsvTelemetryData Telemetry { get; private set; } = new AsvTelemetryData();
        public event Action<AsvTelemetryData>? TelemetryUpdated;
        public event Action<string, bool>? CommandResult; // command name, accepted
        public event Action<string, string>? SafetyAlert; // title, message
        private bool _lowBatteryAlertSent = false;

        // connectionString example: "udp://127.0.0.1:14571" (listen mode)
        public void Start(string connectionString)
        {
            _connection = MavlinkV2Connection.Create(connectionString);
            Console.WriteLine($"[AsvMavLink] Connection created: {connectionString}");

            _browser = new MavlinkDeviceBrowser(_connection, TimeSpan.FromSeconds(10),
                System.Reactive.Concurrency.Scheduler.Default);

            _deviceSubscription = _browser.Devices.Subscribe(changeSet =>
            {
                foreach (var change in changeSet)
                {
                    if (change.Reason == DynamicData.ChangeReason.Add)
                    {
                        Console.WriteLine($"[AsvMavLink] Device discovered: key={change.Current}");

                        // Only build a vehicle client for the REAL ArduPilot autopilot --
                        // skip GCS heartbeats (our own service, QGC, etc.) so we don't
                        // waste effort or risk ending up targeting the wrong device
                        // depending on discovery order.
                        if (change.Current.Autopilot != MavAutopilot.MavAutopilotArdupilotmega)
                        {
                            Console.WriteLine($"[AsvMavLink] Skipping non-vehicle device (autopilot={change.Current.Autopilot})");
                            continue;
                        }

                        SetupVehicleClient(change.Current);
                    }
                }
            });
        }

        private void SetupVehicleClient(IMavlinkDevice device)
        {
            try
            {
                // MavlinkClientIdentity(systemId, componentId, targetSystemId, targetComponentId)
                // -- first two are OUR identity, last two are the TARGET vehicle's.
                // Previously backwards: was setting our identity to the discovered
                // device and hardcoding target to 254/1 -- meant every command sent
                // so far would have gone nowhere. Fixed against verified real
                // constructor parameter names.
                var identity = new MavlinkClientIdentity(
                    254, 1, device.SystemId, device.ComponentId);
                var seq = new PacketSequenceCalculator();

                _vehicle = new ArduPlaneClient(_connection!, identity, new VehicleClientConfig(), seq,
                    System.Reactive.Concurrency.Scheduler.Default);
                _commandClient = new CommandClient(_connection!, identity, seq, new CommandProtocolConfig());
                _positionClient = new PositionClient(_connection!, identity, seq);
                _telemetryClient = new TelemetryClient(_connection!, identity, seq);
                _heartbeatClient = new HeartbeatClient(_connection!, identity, seq, new HeartbeatClientConfig(),
                    System.Reactive.Concurrency.Scheduler.Default);
                _gnssClient = new GnssClient(_connection!, identity, seq);

                Console.WriteLine($"[AsvMavLink] Vehicle client ready for sysid={identity.TargetSystemId}, compid={identity.TargetComponentId}");
                Telemetry.Connected = true;

                _telemetrySubscriptions.Add(_positionClient.GlobalPosition.Subscribe(pos =>
                {
                    Telemetry.Lat = pos.Lat / 1e7;
                    Telemetry.Lon = pos.Lon / 1e7;
                    Telemetry.AltRel = pos.RelativeAlt / 1000.0;
                    Telemetry.Heading = pos.Hdg / 100.0;
                    TelemetryUpdated?.Invoke(Telemetry);
                }));

                _telemetrySubscriptions.Add(_positionClient.VfrHud.Subscribe(hud =>
                {
                    Telemetry.Speed = hud.Groundspeed;
                    TelemetryUpdated?.Invoke(Telemetry);
                }));

                _telemetrySubscriptions.Add(_positionClient.Attitude.Subscribe(att =>
                {
                    Telemetry.Roll = att.Roll * (180.0 / Math.PI);
                    Telemetry.Pitch = att.Pitch * (180.0 / Math.PI);
                    TelemetryUpdated?.Invoke(Telemetry);
                }));

                _telemetrySubscriptions.Add(_telemetryClient.Battery.Subscribe(batt =>
                {
                    Telemetry.BatteryPct = batt.BatteryRemaining;
                    TelemetryUpdated?.Invoke(Telemetry);

                    if (batt.BatteryRemaining >= 0 && batt.BatteryRemaining <= 20 && !_lowBatteryAlertSent)
                    {
                        _lowBatteryAlertSent = true;
                        SafetyAlert?.Invoke("LOW BATTERY", $"Battery at {batt.BatteryRemaining}% -- consider RTL");
                    }
                    if (batt.BatteryRemaining > 20)
                    {
                        _lowBatteryAlertSent = false; // reset if it recovers (e.g. new flight)
                    }
                }));

                _telemetrySubscriptions.Add(_telemetryClient.SystemStatus.Subscribe(sys =>
                {
                    Telemetry.BatteryVoltage = sys.VoltageBattery / 1000.0f;
                    TelemetryUpdated?.Invoke(Telemetry);
                }));

                _telemetrySubscriptions.Add(_heartbeatClient.RawHeartbeat.Subscribe(hb =>
                {
                    Telemetry.Armed = (hb.BaseMode & Asv.Mavlink.V2.Common.MavModeFlag.MavModeFlagSafetyArmed) != 0;
                    Telemetry.FlightMode = PlaneModes.TryGetValue(hb.CustomMode, out var mode) ? mode : $"Mode {hb.CustomMode}";
                    TelemetryUpdated?.Invoke(Telemetry);
                }));

                _telemetrySubscriptions.Add(_gnssClient.Main.Subscribe(gps =>
                {
                    Telemetry.GpsSats = gps.SatellitesVisible;
                    Telemetry.GpsFix = (int)gps.FixType;
                    TelemetryUpdated?.Invoke(Telemetry);
                }));

                // No dedicated client exposes FENCE_STATUS -- subscribe to the raw
                // connection stream and filter for this specific packet type.
                _telemetrySubscriptions.Add(_connection!.Subscribe(packet =>
                {
                    if (packet.Payload is Asv.Mavlink.V2.Ardupilotmega.FenceStatusPayload fence)
                    {
                        if (fence.BreachStatus != 0)
                        {
                            SafetyAlert?.Invoke("GEOFENCE BREACH",
                                $"Drone has breached the geofence! Breach type: {fence.BreachType}. RTL recommended.");
                        }
                    }
                }));

                TelemetryUpdated?.Invoke(Telemetry);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AsvMavLink] Failed to set up vehicle client: {ex.Message}");
            }
        }

        public async Task<bool> ArmAsync(bool arm, CancellationToken ct = default)
        {
            if (_commandClient == null) throw new InvalidOperationException("No vehicle connected yet.");
            var result = await _commandClient.CommandLong(MavCmd.MavCmdComponentArmDisarm,
                arm ? 1 : 0, 0, 0, 0, 0, 0, 0, ct);
            var accepted = result.Result == MavResult.MavResultAccepted;
            CommandResult?.Invoke(arm ? "ARM" : "DISARM", accepted);
            return accepted;
        }

        public async Task<bool> TakeoffAsync(double altitudeMeters, CancellationToken ct = default)
        {
            if (_vehicle == null) throw new InvalidOperationException("No vehicle connected yet.");
            await _vehicle.TakeOff(altitudeMeters, ct);
            CommandResult?.Invoke("TAKEOFF", true);
            return true;
        }

        public async Task<bool> RtlAsync(CancellationToken ct = default)
        {
            if (_vehicle == null) throw new InvalidOperationException("No vehicle connected yet.");
            await _vehicle.DoRtl(ct);
            CommandResult?.Invoke("RTL", true);
            return true;
        }

        public async Task<bool> LandAsync(CancellationToken ct = default)
        {
            if (_vehicle == null) throw new InvalidOperationException("No vehicle connected yet.");
            await _vehicle.DoLand(ct);
            CommandResult?.Invoke("LAND", true);
            return true;
        }

        // Mode changes use raw COMMAND_LONG (MavCmdDoSetMode=176) with real ACK
        // confirmation -- matches what the old (working) raw calls actually sent,
        // avoids needing to construct an IVehicleMode object.
        public async Task<bool> SetModeAsync(byte modeId, CancellationToken ct = default)
        {
            if (_commandClient == null) throw new InvalidOperationException("No vehicle connected yet.");
            var result = await _commandClient.CommandLong(MavCmd.MavCmdDoSetMode, 1, modeId, 0, 0, 0, 0, 0, ct);
            var accepted = result.Result == MavResult.MavResultAccepted;
            CommandResult?.Invoke($"MODE {modeId}", accepted);
            return accepted;
        }

        public void Stop()
        {
            foreach (var sub in _telemetrySubscriptions) sub.Dispose();
            _telemetrySubscriptions.Clear();
            _deviceSubscription?.Dispose();
            _browser?.Dispose();
            _vehicle?.Dispose();
            _commandClient?.Dispose();
            _positionClient?.Dispose();
            _telemetryClient?.Dispose();
            _heartbeatClient?.Dispose();
            _gnssClient?.Dispose();
            _connection?.Dispose();
            Telemetry.Connected = false;
        }
    }
}
