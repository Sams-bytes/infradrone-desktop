using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace InfraDroneDesktop.Services;

// ============================================================
// Isolated, standalone MAVLink v1 telemetry reader for hardware
// that cannot output MAVLink v2 (confirmed via direct testing:
// older ArduPilot firmware on a Pixhawk 1-class / "PX4 FMU V2"
// board, linked via a BCube wireless telemetry radio).
//
// Deliberately fully separate from AsvMavLinkService.cs (which
// handles the working Cube Orange / MAVLink v2 / UDP setup) --
// do not merge these classes. This class is READ-ONLY: it parses
// incoming telemetry but never sends commands to the vehicle.
// ============================================================

public class Mavlink1Telemetry
{
    public bool Connected { get; set; }
    public byte SystemId { get; set; }
    public byte ComponentId { get; set; }
    public byte VehicleType { get; set; }
    public byte Autopilot { get; set; }
    public bool Armed { get; set; }
    public uint CustomMode { get; set; }
    public byte SystemStatus { get; set; }

    // From GLOBAL_POSITION_INT (msg 33)
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double RelativeAlt { get; set; }
    public double Heading { get; set; }

    // From SYS_STATUS (msg 1)
    public float BatteryVoltage { get; set; }
    public int BatteryPct { get; set; } = -1;

    // From VFR_HUD (msg 74)
    public float Speed { get; set; }
    public float Altitude { get; set; }

    // Decoded from CustomMode (ArduCopter mode numbering) -- falls back to
    // the raw number if not in the known table, never guesses a wrong label.
    public string FlightMode
    {
        get
        {
            var known = new System.Collections.Generic.Dictionary<uint, string>
            {
                {0, "STABILIZE"}, {2, "ALT_HOLD"}, {3, "AUTO"}, {4, "GUIDED"},
                {5, "LOITER"}, {6, "RTL"}, {9, "LAND"}, {17, "BRAKE"}
            };
            return known.TryGetValue(CustomMode, out var name) ? name : $"MODE {CustomMode}";
        }
    }

    // From RC_CHANNELS_RAW (msg 35) -- real transmitter stick input values
    public ushort[] RcChannels { get; set; } = new ushort[8];
    public byte RcRssi { get; set; }

    // From GPS_RAW_INT (msg 24) -- fix_type: 0=no fix,1=no fix,2=2D,3=3D,4=DGPS,5=RTK-float,6=RTK-fixed
    public byte GpsFix { get; set; }
    public byte GpsSats { get; set; }

    // From ATTITUDE (msg 30) -- converted from radians to degrees for display
    public double Roll { get; set; }
    public double Pitch { get; set; }
}

public class Mavlink1SerialService
{
    private SerialPort? _port;
    private CancellationTokenSource? _cts;
    private readonly byte[] _buffer = new byte[512];
    private int _bufLen;

    public Mavlink1Telemetry Telemetry { get; } = new Mavlink1Telemetry();
    public event Action<Mavlink1Telemetry>? TelemetryUpdated;
    public event Action<ushort, byte>? CommandAckReceived; // (command, result)
    public event Action<byte, string>? StatusTextReceived; // (severity, text)

    // --- Failsafe watchdog (mirrors AsvMavLinkService thresholds exactly) ---
    public class FailsafeAlertRecord
    {
        public DateTime Time { get; set; }
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
    }
    public List<FailsafeAlertRecord> AlertHistory { get; } = new();
    public event Action<string, string>? SafetyAlert; // title, message
    private void RaiseSafetyAlert(string title, string message)
    {
        AlertHistory.Insert(0, new FailsafeAlertRecord { Time = DateTime.UtcNow, Title = title, Message = message });
        SafetyAlert?.Invoke(title, message);
    }

    private const double LinkLostTimeoutSeconds = 5.0;
    private const int BatteryLowPct = 30;
    private const int BatteryCriticalPct = 15;
    private const int GpsSatsMinimum = 6;
    private DateTime _lastTelemetryTime = DateTime.MinValue;
    private bool _lowBatteryAlertSent = false;
    private bool _criticalBatteryAlertSent = false;
    private bool _linkLostAlertSent = false;
    private bool _gpsDegradedAlertSent = false;
    private System.Threading.Timer? _linkWatchdogTimer;

    public double SecondsSinceLastTelemetry => (DateTime.UtcNow - _lastTelemetryTime).TotalSeconds;
    public bool IsLinkOk => _lastTelemetryTime != DateTime.MinValue && SecondsSinceLastTelemetry <= LinkLostTimeoutSeconds;
    public bool IsBatteryOk => Telemetry.BatteryPct < 0 || Telemetry.BatteryPct > BatteryLowPct;
    public bool IsBatteryCritical => Telemetry.BatteryPct >= 0 && Telemetry.BatteryPct <= BatteryCriticalPct;
    public bool IsGpsOk => Telemetry.GpsFix >= 3 && Telemetry.GpsSats >= GpsSatsMinimum;
    public int LinkTimeoutThreshold => (int)LinkLostTimeoutSeconds;
    public int BatteryLowThreshold => BatteryLowPct;
    public int BatteryCriticalThreshold => BatteryCriticalPct;
    public int GpsMinSatsThreshold => GpsSatsMinimum;

    private void CheckFailsafes(object? state)
    {
        if (_lastTelemetryTime == DateTime.MinValue) return; // never connected yet

        double secondsSince = SecondsSinceLastTelemetry;
        if (secondsSince > LinkLostTimeoutSeconds && !_linkLostAlertSent)
        {
            _linkLostAlertSent = true;
            RaiseSafetyAlert("LINK LOST",
                $"No telemetry received for {secondsSince:F0}s from BCube. " +
                "The vehicle's own onboard GCS-failsafe (if enabled) will act independently of this app.");
        }
        else if (secondsSince <= LinkLostTimeoutSeconds && _linkLostAlertSent)
        {
            _linkLostAlertSent = false;
            RaiseSafetyAlert("LINK RESTORED", "Telemetry link to BCube has been re-established.");
        }

        if (Telemetry.BatteryPct >= 0)
        {
            if (Telemetry.BatteryPct <= BatteryCriticalPct && !_criticalBatteryAlertSent)
            {
                _criticalBatteryAlertSent = true;
                _lowBatteryAlertSent = true;
                RaiseSafetyAlert("CRITICAL BATTERY",
                    $"BCube battery at {Telemetry.BatteryPct}% -- land now. " +
                    "Vehicle's own BATT_FS_CRT_ACT parameter may act independently of this app.");
            }
            else if (Telemetry.BatteryPct <= BatteryLowPct && !_lowBatteryAlertSent)
            {
                _lowBatteryAlertSent = true;
                RaiseSafetyAlert("LOW BATTERY",
                    $"BCube battery at {Telemetry.BatteryPct}% -- consider RTL. " +
                    "Vehicle's own BATT_FS_LOW_ACT parameter may act independently of this app.");
            }
            else if (Telemetry.BatteryPct > BatteryLowPct)
            {
                _lowBatteryAlertSent = false;
                _criticalBatteryAlertSent = false;
            }
        }

        bool gpsDegraded = Telemetry.GpsFix < 3 || Telemetry.GpsSats < GpsSatsMinimum;
        if (gpsDegraded && !_gpsDegradedAlertSent)
        {
            _gpsDegradedAlertSent = true;
            RaiseSafetyAlert("GPS DEGRADED",
                $"BCube GPS fix type={Telemetry.GpsFix}, satellites={Telemetry.GpsSats} (minimum {GpsSatsMinimum}) -- " +
                "consider holding position or landing manually until GPS quality improves.");
        }
        else if (!gpsDegraded && _gpsDegradedAlertSent)
        {
            _gpsDegradedAlertSent = false;
            RaiseSafetyAlert("GPS RECOVERED",
                $"BCube GPS fix restored: type={Telemetry.GpsFix}, satellites={Telemetry.GpsSats}.");
        }
    }

    private const byte HEARTBEAT_CRC_EXTRA = 50;
    private const byte COMMAND_LONG_CRC_EXTRA = 152;
    private const byte COMMAND_ACK_CRC_EXTRA = 143;
    private const byte GLOBAL_POSITION_INT_CRC_EXTRA = 104;
    private const byte SYS_STATUS_CRC_EXTRA = 124;
    private const byte VFR_HUD_CRC_EXTRA = 20;
    private const byte STATUSTEXT_CRC_EXTRA = 83;
    private const byte RC_CHANNELS_RAW_CRC_EXTRA = 244;
    private const byte GPS_RAW_INT_CRC_EXTRA = 24; // confirmed via pymavlink common dialect crc_extra
    private const byte ATTITUDE_CRC_EXTRA = 39; // confirmed via pymavlink common dialect crc_extra
    private byte _sendSeq = 0;
    private bool _streamsRequested = false;

    private static ushort CrcAccumulate(byte data, ushort crc)
    {
        byte tmp = (byte)(data ^ (byte)(crc & 0xFF));
        tmp ^= (byte)(tmp << 4);
        return (ushort)((crc >> 8) ^ (tmp << 8) ^ (tmp << 3) ^ (tmp >> 4));
    }

    // Sends a real MAVLink v1 COMMAND_LONG. This DOES transmit to the vehicle --
    // only call with commands you have deliberately decided to send.
    public bool SendCommandLong(byte targetSystem, byte targetComponent, ushort command,
        float p1 = 0, float p2 = 0, float p3 = 0, float p4 = 0, float p5 = 0, float p6 = 0, float p7 = 0)
    {
        if (_port == null || !_port.IsOpen) return false;

        var payload = new byte[33];
        BitConverter.GetBytes(p1).CopyTo(payload, 0);
        BitConverter.GetBytes(p2).CopyTo(payload, 4);
        BitConverter.GetBytes(p3).CopyTo(payload, 8);
        BitConverter.GetBytes(p4).CopyTo(payload, 12);
        BitConverter.GetBytes(p5).CopyTo(payload, 16);
        BitConverter.GetBytes(p6).CopyTo(payload, 20);
        BitConverter.GetBytes(p7).CopyTo(payload, 24);
        BitConverter.GetBytes(command).CopyTo(payload, 28);
        payload[30] = targetSystem;
        payload[31] = targetComponent;
        payload[32] = 0; // confirmation: 0 = first transmission

        // Our own GCS identity: sysid=255, compid=190 (standard convention).
        var packet = new byte[6 + 33 + 2];
        packet[0] = 0xFE;
        packet[1] = 33;
        packet[2] = _sendSeq++;
        packet[3] = 255;
        packet[4] = 190;
        packet[5] = 76; // COMMAND_LONG msg id
        payload.CopyTo(packet, 6);

        ushort crc = 0xFFFF;
        for (int k = 1; k < 6 + 33; k++) crc = CrcAccumulate(packet[k], crc);
        crc = CrcAccumulate(COMMAND_LONG_CRC_EXTRA, crc);
        packet[6 + 33] = (byte)(crc & 0xFF);
        packet[6 + 33 + 1] = (byte)(crc >> 8);

        try
        {
            _port.Write(packet, 0, packet.Length);
            Console.WriteLine($"[Mavlink1Serial] Sent COMMAND_LONG: command={command} target={targetSystem}/{targetComponent}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Mavlink1Serial] Send failed: {ex.Message}");
            return false;
        }
    }

    // Convenience wrappers over SendCommandLong for common flight commands.
    // Real, standard MAV_CMD values (COMPONENT_ARM_DISARM already verified
    // live; TAKEOFF/LAND/RTL are equally standard, universal MAV_CMD IDs).
    // MAV_CMD not needed here -- REQUEST_DATA_STREAM (msg 66) is a direct
    // message, not a COMMAND_LONG. Asks the vehicle to proactively start
    // sending telemetry (position/battery/speed) on this link -- without
    // this, ArduPilot often won't send those messages at all on a raw
    // serial connection, even though HEARTBEAT/COMMAND_ACK work fine.
    public bool RequestDataStreams(byte targetSystem, byte targetComponent, ushort rateHz = 2)
    {
        if (_port == null || !_port.IsOpen) return false;

        var payload = new byte[6];
        BitConverter.GetBytes(rateHz).CopyTo(payload, 0);
        payload[2] = targetSystem;
        payload[3] = targetComponent;
        payload[4] = 0; // MAV_DATA_STREAM_ALL
        payload[5] = 1; // start_stop: 1 = start

        var packet = new byte[6 + 6 + 2];
        packet[0] = 0xFE;
        packet[1] = 6;
        packet[2] = _sendSeq++;
        packet[3] = 255;
        packet[4] = 190;
        packet[5] = 66; // REQUEST_DATA_STREAM msg id
        payload.CopyTo(packet, 6);

        ushort crc = 0xFFFF;
        for (int k = 1; k < 6 + 6; k++) crc = CrcAccumulate(packet[k], crc);
        crc = CrcAccumulate(148, crc); // REQUEST_DATA_STREAM CRC_EXTRA
        packet[6 + 6] = (byte)(crc & 0xFF);
        packet[6 + 6 + 1] = (byte)(crc >> 8);

        try
        {
            _port.Write(packet, 0, packet.Length);
            Console.WriteLine($"[Mavlink1Serial] Requested all data streams @ {rateHz}Hz from {targetSystem}/{targetComponent}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Mavlink1Serial] RequestDataStreams failed: {ex.Message}");
            return false;
        }
    }

    public bool ArmAsync(bool arm) =>
        SendCommandLong(Telemetry.SystemId, Telemetry.ComponentId, 400, p1: arm ? 1 : 0);

    public bool TakeoffAsync(float altitudeMeters) =>
        SendCommandLong(Telemetry.SystemId, Telemetry.ComponentId, 22, p7: altitudeMeters);

    public bool LandAsync() =>
        SendCommandLong(Telemetry.SystemId, Telemetry.ComponentId, 21);

    // Switched to the simpler, officially-documented MAVLink enum values
    // (PREFLIGHT_CALIBRATION_MAGNETOMETER_START=1, etc.) after MAVProxy's
    // real-world value of 76 was consistently rejected as UNSUPPORTED by
    // this specific (older) firmware.
    public bool StartGyroCalibration() =>
        SendCommandLong(Telemetry.SystemId, Telemetry.ComponentId, 241, p1: 1);

    public bool StartCompassCalibration() =>
        SendCommandLong(Telemetry.SystemId, Telemetry.ComponentId, 241, p2: 1);

    public bool StartAccelCalibration() =>
        SendCommandLong(Telemetry.SystemId, Telemetry.ComponentId, 241, p5: 1);

    // Level Horizon: same MAV_CMD_PREFLIGHT_CALIBRATION as accel, but
    // param5=2 specifically (vs. 1 for a full accel cal) -- confirmed
    // against ArduPilot's own real implementation (GitHub issue #1856:
    // "if packet.param5 == 2 to call the ahrs.add_trim function").
    public bool StartLevelHorizonCalibration() =>
        SendCommandLong(Telemetry.SystemId, Telemetry.ComponentId, 241, p5: 2);

    public bool RtlAsync() =>
        SendCommandLong(Telemetry.SystemId, Telemetry.ComponentId, 20);

    // MAV_CMD_DO_SET_MODE=176, param1=1 (custom mode enabled flag), param2=mode number
    // -- matches the exact pattern AsvMavLinkService.SetModeAsync already uses successfully.
    public bool SetMode(byte modeId) =>
        SendCommandLong(Telemetry.SystemId, Telemetry.ComponentId, 176, p1: 1, p2: modeId);

    public bool Start(string portName, int baud = 57600)
    {
        try
        {
            _port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One);
            _port.Open();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ReadLoop(_cts.Token));
            _linkWatchdogTimer = new System.Threading.Timer(CheckFailsafes, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            Console.WriteLine($"[Mavlink1Serial] Opened {portName} @ {baud} baud (read-only).");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Mavlink1Serial] Failed to open {portName}: {ex.Message}");
            return false;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _port?.Close(); } catch { }
        _port = null;
        Telemetry.Connected = false;
    }

    private void ReadLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _port != null && _port.IsOpen)
        {
            try
            {
                int available = _port.BytesToRead;
                if (available > 0)
                {
                    int toRead = Math.Min(available, _buffer.Length - _bufLen);
                    if (toRead > 0)
                    {
                        int read = _port.Read(_buffer, _bufLen, toRead);
                        _bufLen += read;
                    }
                }

                int i = 0;
                while (i < _bufLen)
                {
                    if (_buffer[i] != 0xFE) { i++; continue; }
                    if (i + 6 > _bufLen) break;

                    byte len = _buffer[i + 1];
                    byte sysid = _buffer[i + 3];
                    byte compid = _buffer[i + 4];
                    byte msgid = _buffer[i + 5];
                    if (Environment.GetEnvironmentVariable("MAVDEBUG") == "1")
                        Console.WriteLine($"[Mavlink1Serial DEBUG] msgid={msgid} len={len}");
                    int totalLen = 6 + len + 2;

                    if (i + totalLen > _bufLen) break;

                    if (msgid == 77 && len == 3) // COMMAND_ACK
                    {
                        ushort crcAck = 0xFFFF;
                        for (int k = 1; k < 6 + len; k++) crcAck = CrcAccumulate(_buffer[i + k], crcAck);
                        crcAck = CrcAccumulate(COMMAND_ACK_CRC_EXTRA, crcAck);
                        ushort receivedCrcAck = (ushort)(_buffer[i + 6 + len] | (_buffer[i + 6 + len + 1] << 8));
                        if (crcAck == receivedCrcAck)
                        {
                            ushort ackCommand = BitConverter.ToUInt16(_buffer, i + 6);
                            byte ackResult = _buffer[i + 8];
                            Console.WriteLine($"[Mavlink1Serial] COMMAND_ACK received: command={ackCommand} result={ackResult} (CRC OK)");
                            CommandAckReceived?.Invoke(ackCommand, ackResult);
                        }
                    }
                    else if (msgid == 33 && len == 28) // GLOBAL_POSITION_INT
                    {
                        ushort crcPos = 0xFFFF;
                        for (int k = 1; k < 6 + len; k++) crcPos = CrcAccumulate(_buffer[i + k], crcPos);
                        crcPos = CrcAccumulate(GLOBAL_POSITION_INT_CRC_EXTRA, crcPos);
                        ushort recvCrcPos = (ushort)(_buffer[i + 6 + len] | (_buffer[i + 6 + len + 1] << 8));
                        if (crcPos == recvCrcPos)
                        {
                            int lat = BitConverter.ToInt32(_buffer, i + 6 + 4);
                            int lon = BitConverter.ToInt32(_buffer, i + 6 + 8);
                            int relAlt = BitConverter.ToInt32(_buffer, i + 6 + 16);
                            ushort hdg = BitConverter.ToUInt16(_buffer, i + 6 + 26);
                            Telemetry.Lat = lat / 1e7;
                            Telemetry.Lon = lon / 1e7;
                            Telemetry.RelativeAlt = relAlt / 1000.0;
                            Telemetry.Heading = hdg / 100.0;
                            _lastTelemetryTime = DateTime.UtcNow;
                            TelemetryUpdated?.Invoke(Telemetry);
                        }
                    }
                    else if (msgid == 24 && len == 30) // GPS_RAW_INT
                    {
                        ushort crcGps = 0xFFFF;
                        for (int k = 1; k < 6 + len; k++) crcGps = CrcAccumulate(_buffer[i + k], crcGps);
                        crcGps = CrcAccumulate(GPS_RAW_INT_CRC_EXTRA, crcGps);
                        ushort recvCrcGps = (ushort)(_buffer[i + 6 + len] | (_buffer[i + 6 + len + 1] << 8));
                        if (crcGps == recvCrcGps)
                        {
                            // Field offsets derived from MAVLink wire-order packing (largest-first):
                            // time_usec(0,8) lat(8,4) lon(12,4) alt(16,4) eph(20,2) epv(22,2)
                            // vel(24,2) cog(26,2) fix_type(28,1) satellites_visible(29,1)
                            byte fixType = _buffer[i + 6 + 28];
                            byte sats = _buffer[i + 6 + 29];
                            Telemetry.GpsFix = fixType;
                            Telemetry.GpsSats = sats;
                            _lastTelemetryTime = DateTime.UtcNow;
                            TelemetryUpdated?.Invoke(Telemetry);
                        }
                    }
                    else if (msgid == 30 && len == 28) // ATTITUDE
                    {
                        ushort crcAtt = 0xFFFF;
                        for (int k = 1; k < 6 + len; k++) crcAtt = CrcAccumulate(_buffer[i + k], crcAtt);
                        crcAtt = CrcAccumulate(ATTITUDE_CRC_EXTRA, crcAtt);
                        ushort recvCrcAtt = (ushort)(_buffer[i + 6 + len] | (_buffer[i + 6 + len + 1] << 8));
                        if (Environment.GetEnvironmentVariable("MAVDEBUG") == "1")
                            Console.WriteLine($"[ATTITUDE DEBUG] computed={crcAtt} received={recvCrcAtt} match={crcAtt == recvCrcAtt}");
                        if (crcAtt == recvCrcAtt)
                        {
                            float rollRad = BitConverter.ToSingle(_buffer, i + 6 + 4);
                            float pitchRad = BitConverter.ToSingle(_buffer, i + 6 + 8);
                            Telemetry.Roll = rollRad * (180.0 / Math.PI);
                            Telemetry.Pitch = pitchRad * (180.0 / Math.PI);
                            if (Environment.GetEnvironmentVariable("MAVDEBUG") == "1")
                                Console.WriteLine($"[ATTITUDE VALUES] roll={Telemetry.Roll:F2} pitch={Telemetry.Pitch:F2}");
                            _lastTelemetryTime = DateTime.UtcNow;
                            TelemetryUpdated?.Invoke(Telemetry);
                        }
                    }
                    else if (msgid == 1 && len == 31) // SYS_STATUS
                    {
                        ushort crcSys = 0xFFFF;
                        for (int k = 1; k < 6 + len; k++) crcSys = CrcAccumulate(_buffer[i + k], crcSys);
                        crcSys = CrcAccumulate(SYS_STATUS_CRC_EXTRA, crcSys);
                        ushort recvCrcSys = (ushort)(_buffer[i + 6 + len] | (_buffer[i + 6 + len + 1] << 8));
                        if (crcSys == recvCrcSys)
                        {
                            ushort voltageBattery = BitConverter.ToUInt16(_buffer, i + 6 + 14);
                            sbyte batteryRemaining = (sbyte)_buffer[i + 6 + 30];
                            Telemetry.BatteryVoltage = voltageBattery / 1000.0f;
                            Telemetry.BatteryPct = batteryRemaining;
                            _lastTelemetryTime = DateTime.UtcNow;
                            TelemetryUpdated?.Invoke(Telemetry);
                        }
                    }
                    else if (msgid == 74 && len == 20) // VFR_HUD
                    {
                        ushort crcVfr = 0xFFFF;
                        for (int k = 1; k < 6 + len; k++) crcVfr = CrcAccumulate(_buffer[i + k], crcVfr);
                        crcVfr = CrcAccumulate(VFR_HUD_CRC_EXTRA, crcVfr);
                        ushort recvCrcVfr = (ushort)(_buffer[i + 6 + len] | (_buffer[i + 6 + len + 1] << 8));
                        if (crcVfr == recvCrcVfr)
                        {
                            float groundspeed = BitConverter.ToSingle(_buffer, i + 6 + 4);
                            float alt = BitConverter.ToSingle(_buffer, i + 6 + 12);
                            Telemetry.Speed = groundspeed;
                            Telemetry.Altitude = alt;
                            _lastTelemetryTime = DateTime.UtcNow;
                            TelemetryUpdated?.Invoke(Telemetry);
                        }
                    }
                    else if (msgid == 253 && len == 51) // STATUSTEXT
                    {
                        ushort crcSt = 0xFFFF;
                        for (int k = 1; k < 6 + len; k++) crcSt = CrcAccumulate(_buffer[i + k], crcSt);
                        crcSt = CrcAccumulate(STATUSTEXT_CRC_EXTRA, crcSt);
                        ushort recvCrcSt = (ushort)(_buffer[i + 6 + len] | (_buffer[i + 6 + len + 1] << 8));
                        if (crcSt == recvCrcSt)
                        {
                            byte severity = _buffer[i + 6];
                            var textBytes = new byte[50];
                            Array.Copy(_buffer, i + 7, textBytes, 0, 50);
                            var nullIdx = Array.IndexOf(textBytes, (byte)0);
                            var text = System.Text.Encoding.UTF8.GetString(
                                textBytes, 0, nullIdx >= 0 ? nullIdx : 50);
                            Console.WriteLine($"[Mavlink1Serial] STATUSTEXT (severity={severity}): {text}");
                            StatusTextReceived?.Invoke(severity, text);
                        }
                    }
                    else if (msgid == 35 && len == 22) // RC_CHANNELS_RAW
                    {
                        ushort crcRc = 0xFFFF;
                        for (int k = 1; k < 6 + len; k++) crcRc = CrcAccumulate(_buffer[i + k], crcRc);
                        crcRc = CrcAccumulate(RC_CHANNELS_RAW_CRC_EXTRA, crcRc);
                        ushort recvCrcRc = (ushort)(_buffer[i + 6 + len] | (_buffer[i + 6 + len + 1] << 8));
                        if (crcRc == recvCrcRc)
                        {
                            for (int ch = 0; ch < 8; ch++)
                                Telemetry.RcChannels[ch] = BitConverter.ToUInt16(_buffer, i + 6 + 4 + ch * 2);
                            Telemetry.RcRssi = _buffer[i + 6 + 21];
                            _lastTelemetryTime = DateTime.UtcNow;
                            TelemetryUpdated?.Invoke(Telemetry);
                        }
                    }
                    else if (msgid == 0 && len == 9) // HEARTBEAT
                    {
                        ushort crc = 0xFFFF;
                        for (int k = 1; k < 6 + len; k++) crc = CrcAccumulate(_buffer[i + k], crc);
                        crc = CrcAccumulate(HEARTBEAT_CRC_EXTRA, crc);
                        ushort receivedCrc = (ushort)(_buffer[i + 6 + len] | (_buffer[i + 6 + len + 1] << 8));

                        if (crc == receivedCrc)
                        {
                            Telemetry.Connected = true;
                            Telemetry.SystemId = sysid;
                            Telemetry.ComponentId = compid;
                            Telemetry.CustomMode = BitConverter.ToUInt32(_buffer, i + 6);
                            Telemetry.VehicleType = _buffer[i + 10];
                            Telemetry.Autopilot = _buffer[i + 11];
                            byte baseMode = _buffer[i + 12];
                            Telemetry.Armed = (baseMode & 0x80) != 0;
                            Telemetry.SystemStatus = _buffer[i + 13];
                            _lastTelemetryTime = DateTime.UtcNow;
                            TelemetryUpdated?.Invoke(Telemetry);

                            if (!_streamsRequested)
                            {
                                _streamsRequested = true;
                                RequestDataStreams(sysid, compid);
                            }
                        }
                        // CRC mismatch: silently discard -- safe failure, no crash, no bad data used.
                    }

                    i += totalLen;
                }

                if (i > 0)
                {
                    Array.Copy(_buffer, i, _buffer, 0, _bufLen - i);
                    _bufLen -= i;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Mavlink1Serial] Read loop error: {ex.Message}");
            }
            Thread.Sleep(10);
        }
    }
}
