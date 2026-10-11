// Services/LiveTelemetryService.cs
//
// Collects everything the vehicle reports over MAVLink into one snapshot for the
// Live Telemetry window, keeps an event log (autopilot STATUSTEXT messages, mode
// and arming changes, waypoint progress, app safety alerts) and can record the
// snapshot to CSV at 4 Hz.
//
// Payload classes and property names verified against Asv.Mavlink v3.4.1
// (src/Asv.Mavlink/Protocol/Messages/common.cs and ardupilotmega.cs).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Asv.Mavlink;
using Asv.Mavlink.V2.Common;

namespace InfraDroneDesktop.Services
{
    public sealed class LiveTelemetrySnapshot
    {
        // Position
        public double Lat, Lon;
        public double AltRel = double.NaN, AltMsl = double.NaN;
        public double HomeLat = double.NaN, HomeLon = double.NaN;
        public double DistHome = double.NaN;
        // Motion
        public double GroundSpeed = double.NaN, AirSpeed = double.NaN, Climb = double.NaN;
        public double Heading = double.NaN, Throttle = double.NaN;
        // Attitude (deg, deg/s)
        public double Roll = double.NaN, Pitch = double.NaN, Yaw = double.NaN;
        public double RollRate = double.NaN, PitchRate = double.NaN, YawRate = double.NaN;
        // Navigation
        public int WpSeq = -1;
        public double WpDist = double.NaN, TargetBearing = double.NaN, XtrackError = double.NaN, AltError = double.NaN;
        // Power
        public double Voltage = double.NaN, Current = double.NaN;
        public int BatteryPct = -1;
        // GPS
        public int GpsFix = 0, GpsSats = 0;
        public double Hdop = double.NaN, Vdop = double.NaN;
        // Wind (ArduPilot WIND message)
        public double WindDir = double.NaN, WindSpeed = double.NaN;
        // System
        public string Mode = "-";
        public bool Armed;
        public double CpuLoadPct = double.NaN;
        public double LinkDropPct = double.NaN;
        public double PacketsPerSec;
        public TimeSpan FlightTime;
        public DateTime LastPacketUtc = DateTime.MinValue;

        public LiveTelemetrySnapshot Clone() => (LiveTelemetrySnapshot)MemberwiseClone();
    }

    public sealed class TelemetryLogEntry
    {
        public DateTime Time { get; init; }
        public string Source { get; init; } = "";   // AP = autopilot, APP = this app
        public string Level { get; init; } = "info"; // info | warn | error
        public string Text { get; init; } = "";
    }

    public sealed class LiveTelemetryService : IDisposable
    {
        /// <summary>App-wide instance, created by MainWindow at startup so the log
        /// collects messages even before the window is opened.</summary>
        public static LiveTelemetryService? Shared { get; set; }

        private readonly AsvMavLinkService _mav;
        private readonly object _lock = new();
        private readonly LiveTelemetrySnapshot _s = new();
        private readonly List<TelemetryLogEntry> _log = new();
        private const int MaxLog = 1000;

        private int _packetCount;
        private DateTime _rateWindowStart = DateTime.UtcNow;
        private DateTime? _armedSince;
        private TimeSpan _flightTimeBefore = TimeSpan.Zero;
        private string _lastMode = "";
        private bool _lastArmed;
        private int _lastWp = -1;

        private StreamWriter? _csv;
        private Timer? _csvTimer;
        public string? RecordingPath { get; private set; }
        public bool IsRecording => _csv != null;

        public event Action<TelemetryLogEntry>? LogAdded;

        public LiveTelemetryService(AsvMavLinkService mav)
        {
            _mav = mav;
            _mav.VehiclePacket += OnPacket;
            _mav.SafetyAlert += OnSafetyAlert;
            _mav.CommandResult += OnCommandResult;
        }

        public LiveTelemetrySnapshot Snapshot()
        {
            lock (_lock)
            {
                var c = _s.Clone();
                c.Mode = _mav.Telemetry.FlightMode;
                c.FlightTime = _flightTimeBefore + (_armedSince.HasValue ? DateTime.UtcNow - _armedSince.Value : TimeSpan.Zero);
                return c;
            }
        }

        public IReadOnlyList<TelemetryLogEntry> RecentLog()
        {
            lock (_lock) return _log.ToList();
        }

        // ---- Packet decoding -------------------------------------------------
        private void OnPacket(IPacketV2<IPayload> packet)
        {
            lock (_lock)
            {
                _s.LastPacketUtc = DateTime.UtcNow;
                _packetCount++;
                var elapsed = (DateTime.UtcNow - _rateWindowStart).TotalSeconds;
                if (elapsed >= 1.0)
                {
                    _s.PacketsPerSec = _packetCount / elapsed;
                    _packetCount = 0;
                    _rateWindowStart = DateTime.UtcNow;
                }

                switch (packet.Payload)
                {
                    case HeartbeatPayload hb:
                        var armed = (hb.BaseMode & MavModeFlag.MavModeFlagSafetyArmed) != 0;
                        _s.Armed = armed;
                        if (armed != _lastArmed)
                        {
                            _lastArmed = armed;
                            if (armed)
                            {
                                _armedSince = DateTime.UtcNow;
                                if (double.IsNaN(_s.HomeLat) && _s.Lat != 0) { _s.HomeLat = _s.Lat; _s.HomeLon = _s.Lon; }
                                AddLogLocked("APP", "warn", "ARMED");
                            }
                            else
                            {
                                if (_armedSince.HasValue) _flightTimeBefore += DateTime.UtcNow - _armedSince.Value;
                                _armedSince = null;
                                AddLogLocked("APP", "info", "DISARMED");
                            }
                        }
                        break;

                    case GlobalPositionIntPayload gp:
                        _s.Lat = gp.Lat / 1e7;
                        _s.Lon = gp.Lon / 1e7;
                        _s.AltRel = gp.RelativeAlt / 1000.0;
                        _s.AltMsl = gp.Alt / 1000.0;
                        if (!double.IsNaN(_s.HomeLat))
                            _s.DistHome = DistanceM(_s.HomeLat, _s.HomeLon, _s.Lat, _s.Lon);
                        break;

                    case VfrHudPayload hud:
                        _s.GroundSpeed = hud.Groundspeed;
                        _s.AirSpeed = hud.Airspeed;
                        _s.Climb = hud.Climb;
                        _s.Heading = hud.Heading;
                        _s.Throttle = hud.Throttle;
                        break;

                    case AttitudePayload att:
                        _s.Roll = Deg(att.Roll); _s.Pitch = Deg(att.Pitch); _s.Yaw = Deg(att.Yaw);
                        _s.RollRate = Deg(att.Rollspeed); _s.PitchRate = Deg(att.Pitchspeed); _s.YawRate = Deg(att.Yawspeed);
                        break;

                    case SysStatusPayload sys:
                        _s.Voltage = sys.VoltageBattery == ushort.MaxValue ? double.NaN : sys.VoltageBattery / 1000.0;
                        _s.Current = sys.CurrentBattery < 0 ? double.NaN : sys.CurrentBattery / 100.0;
                        _s.BatteryPct = sys.BatteryRemaining;
                        _s.CpuLoadPct = sys.Load / 10.0;
                        _s.LinkDropPct = sys.DropRateComm / 100.0;
                        break;

                    case GpsRawIntPayload gps:
                        _s.GpsFix = (int)gps.FixType;
                        _s.GpsSats = gps.SatellitesVisible;
                        _s.Hdop = gps.Eph == ushort.MaxValue ? double.NaN : gps.Eph / 100.0;
                        _s.Vdop = gps.Epv == ushort.MaxValue ? double.NaN : gps.Epv / 100.0;
                        break;

                    case NavControllerOutputPayload nav:
                        _s.WpDist = nav.WpDist;
                        _s.TargetBearing = nav.TargetBearing;
                        _s.XtrackError = nav.XtrackError;
                        _s.AltError = nav.AltError;
                        break;

                    case MissionCurrentPayload mc:
                        _s.WpSeq = mc.Seq;
                        if (mc.Seq != _lastWp)
                        {
                            if (_lastWp >= 0 && _s.Armed) AddLogLocked("APP", "info", $"Now flying to mission item {mc.Seq}");
                            _lastWp = mc.Seq;
                        }
                        break;

                    case HomePositionPayload home:
                        _s.HomeLat = home.Latitude / 1e7;
                        _s.HomeLon = home.Longitude / 1e7;
                        break;

                    case Asv.Mavlink.V2.Ardupilotmega.WindPayload wind:
                        _s.WindDir = wind.Direction;
                        _s.WindSpeed = wind.Speed;
                        break;

                    case StatustextPayload st:
                        var text = new string(st.Text).TrimEnd('\0', ' ');
                        if (text.Length > 0)
                        {
                            var lvl = st.Severity <= MavSeverity.MavSeverityError ? "error"
                                    : st.Severity <= MavSeverity.MavSeverityWarning ? "warn" : "info";
                            AddLogLocked("AP", lvl, text);
                        }
                        break;
                }

                var mode = _mav.Telemetry.FlightMode;
                if (mode != _lastMode && !string.IsNullOrEmpty(mode) && mode != "—")
                {
                    if (_lastMode != "") AddLogLocked("APP", "info", $"Mode {_lastMode} -> {mode}");
                    _lastMode = mode;
                }
            }
        }

        private void OnSafetyAlert(string title, string message) =>
            AddLog("APP", title.Contains("RESTORED") || title.Contains("RECOVERED") || title.Contains("CLEAR") ? "info" : "error",
                   $"{title}: {message}");

        private void OnCommandResult(string cmd, bool accepted) =>
            AddLog("APP", accepted ? "info" : "warn", $"Command {cmd}: {(accepted ? "accepted" : "REJECTED")}");

        private void AddLog(string source, string level, string text)
        {
            lock (_lock) AddLogLocked(source, level, text);
        }

        private void AddLogLocked(string source, string level, string text)
        {
            var e = new TelemetryLogEntry { Time = DateTime.Now, Source = source, Level = level, Text = text };
            _log.Add(e);
            if (_log.Count > MaxLog) _log.RemoveAt(0);
            try { LogAdded?.Invoke(e); } catch { }
        }

        // ---- CSV recording ---------------------------------------------------
        public string StartRecording()
        {
            StopRecording();
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                   "agri_drone", "flight_logs");
            Directory.CreateDirectory(dir);
            RecordingPath = Path.Combine(dir, $"telemetry_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            _csv = new StreamWriter(RecordingPath, append: false);
            _csv.WriteLine("time,mode,armed,lat,lon,alt_rel_m,alt_msl_m,ground_speed_ms,air_speed_ms,climb_ms," +
                           "heading_deg,throttle_pct,roll_deg,pitch_deg,yaw_deg,voltage_v,current_a,battery_pct," +
                           "gps_fix,gps_sats,hdop,wp_seq,wp_dist_m,xtrack_m,dist_home_m,wind_dir_deg,wind_speed_ms");
            _csvTimer = new Timer(_ => WriteCsvRow(), null, 0, 250);
            AddLog("APP", "info", "Recording telemetry to " + RecordingPath);
            return RecordingPath;
        }

        public void StopRecording()
        {
            _csvTimer?.Dispose(); _csvTimer = null;
            if (_csv != null)
            {
                try { _csv.Flush(); _csv.Dispose(); } catch { }
                _csv = null;
                AddLog("APP", "info", "Recording stopped: " + RecordingPath);
            }
        }

        private void WriteCsvRow()
        {
            var w = _csv; if (w == null) return;
            var s = Snapshot();
            var inv = CultureInfo.InvariantCulture;
            string F(double v, string fmt = "F2") => double.IsNaN(v) ? "" : v.ToString(fmt, inv);
            var row = string.Join(",",
                DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff", inv), s.Mode.Replace(",", " "), s.Armed ? "1" : "0",
                s.Lat.ToString("F7", inv), s.Lon.ToString("F7", inv), F(s.AltRel), F(s.AltMsl),
                F(s.GroundSpeed), F(s.AirSpeed), F(s.Climb), F(s.Heading, "F0"), F(s.Throttle, "F0"),
                F(s.Roll, "F1"), F(s.Pitch, "F1"), F(s.Yaw, "F1"), F(s.Voltage), F(s.Current),
                s.BatteryPct.ToString(inv), s.GpsFix.ToString(inv), s.GpsSats.ToString(inv), F(s.Hdop),
                s.WpSeq.ToString(inv), F(s.WpDist, "F0"), F(s.XtrackError, "F1"), F(s.DistHome, "F0"),
                F(s.WindDir, "F0"), F(s.WindSpeed, "F1"));
            try { lock (w) w.WriteLine(row); } catch { }
        }

        // ---- Helpers -----------------------------------------------------------
        private static double Deg(double rad) => rad * 180.0 / Math.PI;

        private static double DistanceM(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000;
            var dLat = (lat2 - lat1) * Math.PI / 180; var dLon = (lon2 - lon1) * Math.PI / 180;
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                    Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 2 * R * Math.Asin(Math.Sqrt(a));
        }

        public void Dispose()
        {
            StopRecording();
            _mav.VehiclePacket -= OnPacket;
            _mav.SafetyAlert -= OnSafetyAlert;
            _mav.CommandResult -= OnCommandResult;
        }
    }
}
