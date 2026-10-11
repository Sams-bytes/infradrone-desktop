using System;
using System.Collections.Generic;
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
        private ParamsClient? _paramsClient;
        private MissionClient? _missionClient;   // used for geofence upload
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
        public class FailsafeAlertRecord
        {
            public DateTime Time { get; set; }
            public string Title { get; set; } = "";
            public string Message { get; set; } = "";
        }

        public List<FailsafeAlertRecord> AlertHistory { get; } = new();
        private readonly string _alertLogPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "agri_drone", "failsafe_alert_log.jsonl");

        private void RaiseSafetyAlert(string title, string message)
        {
            var record = new FailsafeAlertRecord { Time = DateTime.Now, Title = title, Message = message };
            AlertHistory.Insert(0, record); // newest first
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_alertLogPath)!);
                var json = System.Text.Json.JsonSerializer.Serialize(record);
                System.IO.File.AppendAllText(_alertLogPath, json + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AsvMavLink] Failed to write alert log: {ex.Message}");
            }
            // Previously called RaiseSafetyAlert(title, message) here -- infinite
            // recursion, so the FIRST alert of any kind (low battery, link lost,
            // GPS degraded, geofence) crashed the whole app with a stack overflow.
            SafetyAlert?.Invoke(title, message);
        }

        // Set MAVDEBUG=1 to see per-packet logging. Off by default: printing every
        // packet floods the console and slows the UI thread during real flights.
        private static readonly bool DebugLogging =
            Environment.GetEnvironmentVariable("MAVDEBUG") == "1";

        // Live status for a monitoring UI -- computed on demand, not events.
        public double SecondsSinceLastTelemetry => (DateTime.UtcNow - _lastTelemetryTime).TotalSeconds;
        public bool IsLinkOk => _lastTelemetryTime != DateTime.MinValue && SecondsSinceLastTelemetry <= LinkLostTimeoutSeconds;
        public bool IsBatteryOk => Telemetry.BatteryPct < 0 || Telemetry.BatteryPct > BatteryLowPct;
        public bool IsBatteryCritical => Telemetry.BatteryPct >= 0 && Telemetry.BatteryPct <= BatteryCriticalPct;
        public bool IsGpsOk => Telemetry.GpsFix >= (int)Asv.Mavlink.V2.Common.GpsFixType.GpsFixType3dFix && Telemetry.GpsSats >= GpsSatsMinimum;
        public enum FirmwareType { Unknown, ArduPilot, Px4 }
        public FirmwareType DetectedFirmware { get; private set; } = FirmwareType.Unknown;
        public FirmwareType SelectedFirmware { get; set; } = FirmwareType.ArduPilot; // user's manual choice
        public event Action<FirmwareType>? FirmwareDetected;

        public int LinkTimeoutThreshold => (int)LinkLostTimeoutSeconds;
        public int BatteryLowThreshold => BatteryLowPct;
        public int BatteryCriticalThreshold => BatteryCriticalPct;
        public int GpsMinSatsThreshold => GpsSatsMinimum;

        public event Action<string, string>? SafetyAlert; // title, message
        private bool _lowBatteryAlertSent = false;
        private bool _criticalBatteryAlertSent = false;
        private bool _linkLostAlertSent = false;
        private bool _gpsDegradedAlertSent = false;
        private bool _fenceBreachAlertSent = false;
        private DateTime _lastTelemetryTime = DateTime.MinValue;
        private System.Threading.Timer? _linkWatchdogTimer;

        // Companion-app alert-only monitoring. Real ArduPilot failsafe response
        // (FS_GCS_ENABL, BATT_FS_LOW_ACT/BATT_FS_CRT_ACT, FENCE_ACTION) already
        // executes ONBOARD the flight controller -- that is the actual safety
        // net and works even if this app crashes. This service exists to make
        // sure the operator is never caught unaware, and give a fast manual
        // response option; it deliberately does NOT auto-send commands, since
        // a companion app's own link can drop for reasons unrelated to real
        // vehicle risk (a known false-positive source in GCS-side failsafes).
        private const double LinkLostTimeoutSeconds = 5.0;
        private const int BatteryLowPct = 30;
        private const int BatteryCriticalPct = 15;
        private const int GpsSatsMinimum = 6;

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

                        // Accept real vehicle autopilots (ArduPilot OR PX4) -- skip GCS
                        // heartbeats (our own service, QGC, etc.) so we don't waste effort
                        // or risk targeting the wrong device depending on discovery order.
                        if (change.Current.Autopilot == MavAutopilot.MavAutopilotArdupilotmega)
                        {
                            DetectedFirmware = FirmwareType.ArduPilot;
                            Console.WriteLine("[AsvMavLink] Detected firmware: ArduPilot");
                        }
                        else if (change.Current.Autopilot == MavAutopilot.MavAutopilotPx4)
                        {
                            DetectedFirmware = FirmwareType.Px4;
                            Console.WriteLine("[AsvMavLink] Detected firmware: PX4");
                        }
                        else
                        {
                            Console.WriteLine($"[AsvMavLink] Skipping non-vehicle device (autopilot={change.Current.Autopilot})");
                            continue;
                        }
                        FirmwareDetected?.Invoke(DetectedFirmware);

                        SetupVehicleClient(change.Current);
                    }
                }
            });
        }

        /// <summary>
        /// Builds a fence uploader for the connected vehicle, or null if nothing is
        /// connected yet. Cube Orange only - the BCube link is MAVLink v1 and
        /// ArduPilot will not accept a fence over it.
        /// </summary>
        public FenceUploadService? CreateFenceUploader()
        {
            if (_connection == null || _missionClient == null || _lastIdentity == null || _lastSeq == null)
                return null;
            return new FenceUploadService(_connection, _missionClient, _paramsClient,
                                          _lastIdentity, _lastSeq);
        }

        private MavlinkClientIdentity? _lastIdentity;
        private IPacketSequenceCalculator? _lastSeq;

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
                _lastIdentity = identity; _lastSeq = seq;

                _vehicle = new ArduPlaneClient(_connection!, identity, new VehicleClientConfig(), seq,
                    System.Reactive.Concurrency.Scheduler.Default);
                _commandClient = new CommandClient(_connection!, identity, seq, new CommandProtocolConfig());
                _positionClient = new PositionClient(_connection!, identity, seq);
                _telemetryClient = new TelemetryClient(_connection!, identity, seq);
                _heartbeatClient = new HeartbeatClient(_connection!, identity, seq, new HeartbeatClientConfig(),
                    System.Reactive.Concurrency.Scheduler.Default);
                _gnssClient = new GnssClient(_connection!, identity, seq);
                _paramsClient = new ParamsClient(_connection!, identity, seq, new ParameterClientConfig());
                _missionClient = new MissionClient(_connection!, identity, seq, new MissionClientConfig());

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

                    if (batt.BatteryRemaining < 0)
                    {
                        // -1 (unknown) from ArduPilot -- battery monitor not configured
                        // or not reporting; nothing to alert on here.
                    }
                    else if (batt.BatteryRemaining <= BatteryCriticalPct && !_criticalBatteryAlertSent)
                    {
                        _criticalBatteryAlertSent = true;
                        _lowBatteryAlertSent = true; // critical implies low too
                        RaiseSafetyAlert("CRITICAL BATTERY",
                            $"Battery at {batt.BatteryRemaining}% -- land now. " +
                            "Vehicle's own BATT_FS_CRT_ACT parameter may act independently of this app.");
                    }
                    else if (batt.BatteryRemaining <= BatteryLowPct && !_lowBatteryAlertSent)
                    {
                        _lowBatteryAlertSent = true;
                        RaiseSafetyAlert("LOW BATTERY",
                            $"Battery at {batt.BatteryRemaining}% -- consider RTL. " +
                            "Vehicle's own BATT_FS_LOW_ACT parameter may act independently of this app.");
                    }
                    else if (batt.BatteryRemaining > BatteryLowPct)
                    {
                        // Reset both flags if battery recovers (e.g. new flight, swapped pack)
                        _lowBatteryAlertSent = false;
                        _criticalBatteryAlertSent = false;
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
                    _lastTelemetryTime = DateTime.UtcNow;
                    if (_linkLostAlertSent)
                    {
                        _linkLostAlertSent = false;
                        RaiseSafetyAlert("LINK RESTORED", "Telemetry link to the vehicle has been re-established.");
                    }
                    TelemetryUpdated?.Invoke(Telemetry);
                }));

                _lastTelemetryTime = DateTime.UtcNow;
                _linkWatchdogTimer = new System.Threading.Timer(_ =>
                {
                    var secondsSinceLastPacket = (DateTime.UtcNow - _lastTelemetryTime).TotalSeconds;
                    if (secondsSinceLastPacket > LinkLostTimeoutSeconds && !_linkLostAlertSent)
                    {
                        _linkLostAlertSent = true;
                        RaiseSafetyAlert("LINK LOST",
                            $"No telemetry received for {secondsSinceLastPacket:F0}s. " +
                            "The vehicle's own onboard GCS-failsafe (if enabled) will act independently of this app -- " +
                            "check FS_GCS_ENABL / FS_GCS_TIMEOUT parameters.");
                    }
                }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

                _telemetrySubscriptions.Add(_gnssClient.Main.Subscribe(gps =>
                {
                    Telemetry.GpsSats = gps.SatellitesVisible;
                    Telemetry.GpsFix = (int)gps.FixType;
                    TelemetryUpdated?.Invoke(Telemetry);

                    // GPS_FIX_TYPE: 0/1=no fix, 2=2D, 3=3D, 4+=DGPS/RTK (better).
                    // Degraded = below 3D fix, or 3D but with too few satellites
                    // to trust it fully.
                    bool degraded = gps.FixType < Asv.Mavlink.V2.Common.GpsFixType.GpsFixType3dFix
                                    || gps.SatellitesVisible < GpsSatsMinimum;

                    if (degraded && !_gpsDegradedAlertSent)
                    {
                        _gpsDegradedAlertSent = true;
                        RaiseSafetyAlert("GPS DEGRADED",
                            $"GPS fix type={gps.FixType}, satellites={gps.SatellitesVisible} (minimum {GpsSatsMinimum}) -- " +
                            "consider holding position or landing manually until GPS quality improves.");
                    }
                    else if (!degraded && _gpsDegradedAlertSent)
                    {
                        _gpsDegradedAlertSent = false;
                        RaiseSafetyAlert("GPS RECOVERED",
                            $"GPS fix restored: type={gps.FixType}, satellites={gps.SatellitesVisible}.");
                    }
                }));

                // No dedicated client exposes FENCE_STATUS -- subscribe to the raw
                // connection stream and filter for this specific packet type.
                _telemetrySubscriptions.Add(_connection!.Subscribe(packet =>
                {
                    if (DebugLogging)
                        Console.WriteLine($"[AsvMavLink DEBUG] Raw packet received: {packet.Name} (payload type: {packet.Payload?.GetType().FullName})");

                    if (packet.Payload is Asv.Mavlink.V2.Ardupilotmega.FenceStatusPayload fence)
                    {
                        if (DebugLogging)
                            Console.WriteLine($"[AsvMavLink DEBUG] FENCE_STATUS matched! BreachStatus={fence.BreachStatus}");
                        // FENCE_STATUS arrives about once a second; alert once per
                        // breach instead of once per packet.
                        if (fence.BreachStatus != 0 && !_fenceBreachAlertSent)
                        {
                            _fenceBreachAlertSent = true;
                            RaiseSafetyAlert("GEOFENCE BREACH",
                                $"Drone has breached the geofence! Breach type: {fence.BreachType}. RTL recommended.");
                        }
                        else if (fence.BreachStatus == 0 && _fenceBreachAlertSent)
                        {
                            _fenceBreachAlertSent = false;
                            RaiseSafetyAlert("GEOFENCE CLEAR", "Vehicle is back inside the geofence.");
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

        // ------------------------------------------------------------------
        // MISSION UPLOAD (Cube Orange / ArduPlane QuadPlane, MAVLink 2)
        //
        // What gets uploaded:
        //   seq 0          home placeholder (ArduPilot ALWAYS treats item 0 as
        //                  home and overwrites it at arming -- it is never flown)
        //   seq 1          NAV_VTOL_TAKEOFF to vtolTakeoffAltM (if > 0)
        //   seq 2..        NAV_WAYPOINT for each user waypoint (relative altitude)
        //   last           NAV_RETURN_TO_LAUNCH (if appendRtl). With Q_RTL_MODE
        //                  set on the aircraft, RTL ends in a VTOL landing.
        //
        // Handshake is driven by hand on the raw connection, same reason as
        // FenceUploadService: MissionClient's helpers don't expose MissionType.
        // ArduPilot answers MISSION_COUNT with MISSION_REQUEST_INT (msg 51); old
        // firmware may use MISSION_REQUEST (msg 40). Both are handled here.
        //
        // SAFETY: uploading never arms, never changes mode, never starts AUTO.
        // ------------------------------------------------------------------
        public enum CubeMissionUploadResult { Success, Timeout, Rejected, NotConnected, InvalidMission }

        private readonly record struct MissionItemSpec(
            MavCmd Command, MavFrame Frame, double Lat, double Lon, float Alt,
            float P1 = 0, float P2 = 0, float P3 = 0, float P4 = 0);

        public async Task<(CubeMissionUploadResult Result, string Detail)> UploadMissionAsync(
            IReadOnlyList<(double Lat, double Lon, float AltM)> waypoints,
            float vtolTakeoffAltM,
            bool appendRtl = true,
            int timeoutMs = 20000,
            CancellationToken ct = default)
        {
            if (_connection == null || _missionClient == null || _lastIdentity == null || _lastSeq == null)
                return (CubeMissionUploadResult.NotConnected, "No Cube Orange vehicle connected.");
            if (waypoints == null || waypoints.Count == 0)
                return (CubeMissionUploadResult.InvalidMission, "No waypoints to upload.");
            foreach (var wp in waypoints)
            {
                if (double.IsNaN(wp.Lat) || Math.Abs(wp.Lat) > 90 || double.IsNaN(wp.Lon) || Math.Abs(wp.Lon) > 180)
                    return (CubeMissionUploadResult.InvalidMission, $"Invalid coordinate {wp.Lat},{wp.Lon}.");
                if (wp.AltM <= 0)
                    return (CubeMissionUploadResult.InvalidMission, "Every waypoint needs an altitude above 0 m.");
            }

            // ---- Build the item list ----
            var items = new List<MissionItemSpec>
            {
                new(MavCmd.MavCmdNavWaypoint, MavFrame.MavFrameGlobal, waypoints[0].Lat, waypoints[0].Lon, 0)
            };
            if (vtolTakeoffAltM > 0)
                items.Add(new(MavCmd.MavCmdNavVtolTakeoff, MavFrame.MavFrameGlobalRelativeAlt,
                              waypoints[0].Lat, waypoints[0].Lon, vtolTakeoffAltM));
            foreach (var wp in waypoints)
                items.Add(new(MavCmd.MavCmdNavWaypoint, MavFrame.MavFrameGlobalRelativeAlt,
                              wp.Lat, wp.Lon, wp.AltM));
            if (appendRtl)
                items.Add(new(MavCmd.MavCmdNavReturnToLaunch, MavFrame.MavFrameGlobalRelativeAlt, 0, 0, 0));

            var identity = _lastIdentity;
            var count = (ushort)items.Count;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            var tct = timeoutCts.Token;

            var ackTcs = new TaskCompletionSource<MavMissionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sentSeqs = new HashSet<ushort>();

            async Task SendItem(ushort seq)
            {
                if (seq >= items.Count) return;
                var it = items[seq];
                lock (sentSeqs) sentSeqs.Add(seq);
                await _missionClient!.WriteMissionIntItem(p =>
                {
                    p.TargetSystem = identity.TargetSystemId;
                    p.TargetComponent = identity.TargetComponentId;
                    p.Seq = seq;
                    p.Frame = it.Frame;
                    p.Command = it.Command;
                    p.MissionType = MavMissionType.MavMissionTypeMission;
                    p.Current = 0;
                    p.Autocontinue = 1;
                    p.Param1 = it.P1; p.Param2 = it.P2; p.Param3 = it.P3; p.Param4 = it.P4;
                    p.X = (int)Math.Round(it.Lat * 1e7);
                    p.Y = (int)Math.Round(it.Lon * 1e7);
                    p.Z = it.Alt;
                }, tct);
            }

            // Only listen to packets from the vehicle we target -- QGroundControl or
            // MAVProxy on the same link must not be mistaken for the vehicle.
            using var sub = _connection.Subscribe(packet =>
            {
                if (packet.SystemId != identity.TargetSystemId) return;
                try
                {
                    switch (packet.Payload)
                    {
                        case MissionRequestIntPayload rqi when rqi.MissionType == MavMissionType.MavMissionTypeMission:
                            _ = SendItem(rqi.Seq);
                            break;
                        case MissionRequestPayload rq when rq.MissionType == MavMissionType.MavMissionTypeMission:
                            _ = SendItem(rq.Seq);
                            break;
                        case MissionAckPayload ack when ack.MissionType == MavMissionType.MavMissionTypeMission:
                            ackTcs.TrySetResult(ack.Type);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[AsvMavLink] Mission item send failed: " + ex.Message);
                }
            });

            try
            {
                var pkt = new MissionCountPacket
                {
                    SystemId = identity.SystemId,
                    ComponentId = identity.ComponentId,
                    Sequence = _lastSeq.GetNextSequenceNumber()
                };
                pkt.Payload.TargetSystem = identity.TargetSystemId;
                pkt.Payload.TargetComponent = identity.TargetComponentId;
                pkt.Payload.Count = count;
                pkt.Payload.MissionType = MavMissionType.MavMissionTypeMission;
                await _connection.Send(pkt, tct);

                var result = await ackTcs.Task.WaitAsync(tct);
                int served; lock (sentSeqs) served = sentSeqs.Count;

                if (result != MavMissionResult.MavMissionAccepted)
                    return (CubeMissionUploadResult.Rejected, $"Vehicle rejected the mission: {result}.");
                if (served < count)
                    return (CubeMissionUploadResult.Rejected,
                            $"Vehicle acknowledged after only {served} of {count} items -- treat as failed and re-upload.");

                CommandResult?.Invoke("MISSION UPLOAD", true);
                return (CubeMissionUploadResult.Success,
                        $"Mission accepted: {waypoints.Count} waypoints" +
                        (vtolTakeoffAltM > 0 ? $", VTOL takeoff to {vtolTakeoffAltM:F0} m" : "") +
                        (appendRtl ? ", RTL at end" : "") +
                        $" ({count} items incl. home). Not armed, mode unchanged.");
            }
            catch (OperationCanceledException)
            {
                CommandResult?.Invoke("MISSION UPLOAD", false);
                int served; lock (sentSeqs) served = sentSeqs.Count;
                return (CubeMissionUploadResult.Timeout,
                        $"Timed out after {timeoutMs / 1000}s ({served} of {count} items requested by vehicle).");
            }
            catch (Exception ex)
            {
                CommandResult?.Invoke("MISSION UPLOAD", false);
                return (CubeMissionUploadResult.Rejected, "Upload failed: " + ex.Message);
            }
        }

        public async Task<float?> ReadParamAsync(string name, CancellationToken ct = default)
        {
            if (_paramsClient == null) return null;
            try
            {
                var result = await _paramsClient.Read(name, ct);
                return result.ParamValue;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AsvMavLink] Failed to read param {name}: {ex.Message}");
                return null;
            }
        }

        public void Stop()
        {
            foreach (var sub in _telemetrySubscriptions) sub.Dispose();
            _telemetrySubscriptions.Clear();
            _linkWatchdogTimer?.Dispose();
            _linkWatchdogTimer = null;
            _deviceSubscription?.Dispose();
            _browser?.Dispose();
            _vehicle?.Dispose();
            _commandClient?.Dispose();
            _positionClient?.Dispose();
            _telemetryClient?.Dispose();
            _heartbeatClient?.Dispose();
            _gnssClient?.Dispose();
            _paramsClient?.Dispose();
            _connection?.Dispose();
            Telemetry.Connected = false;
        }
    }
}
