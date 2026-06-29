using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace InfraDroneDesktop.Services;

public class TelemetryData
{
    public bool Connected { get; set; }
    public string FlightMode { get; set; } = "—";
    public bool Armed { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double AltRel { get; set; }
    public double Speed { get; set; }
    public double Heading { get; set; }
    public double Roll { get; set; }
    public double Pitch { get; set; }
    public int GpsSats { get; set; }
    public int GpsFix { get; set; }
    public int BatteryPct { get; set; }
}

public class MavLinkService
{
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private readonly MAVLink.MavlinkParse _parser = new MAVLink.MavlinkParse();
    private readonly EncryptionService _enc = new EncryptionService();

    public TelemetryData Telemetry { get; private set; } = new TelemetryData();
    public event Action<TelemetryData>? TelemetryUpdated;

    private static readonly Dictionary<int, string> FlightModes = new()
    {
        {0,"Stabilize"},{1,"Acro"},{2,"AltHold"},{3,"Auto"},
        {4,"Guided"},{5,"Loiter"},{6,"RTL"},{7,"Circle"},
        {9,"Land"},{16,"PosHold"},{17,"Brake"}
    };

    public async Task StartAsync(int port = 14572)
    {
        _cts = new CancellationTokenSource();
        _udp = new UdpClient(port);
        Console.WriteLine($"[MAVLink] Listening on UDP {port}");
        await ReceiveLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udp?.Close();
        Telemetry.Connected = false;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync(ct);
                var stream = new MemoryStream(result.Buffer);
                var msg = _parser.ReadPacket(stream);
                if (msg != null) ProcessMessage(msg);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"[MAVLink] {ex.Message}"); }
        }
    }

    private void ProcessMessage(MAVLink.MAVLinkMessage msg)
    {
        Telemetry.Connected = true;

        switch (msg.msgid)
        {
            case (uint)MAVLink.MAVLINK_MSG_ID.HEARTBEAT:
                var hb = (MAVLink.mavlink_heartbeat_t)msg.data;
                Telemetry.Armed = (hb.base_mode & (byte)MAVLink.MAV_MODE_FLAG.SAFETY_ARMED) != 0;
                if (FlightModes.TryGetValue((int)hb.custom_mode, out var mode))
                    Telemetry.FlightMode = mode;
                break;

            case (uint)MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT:
                var gp = (MAVLink.mavlink_global_position_int_t)msg.data;
                Telemetry.Lat = gp.lat / 1e7;
                Telemetry.Lon = gp.lon / 1e7;
                Telemetry.AltRel = gp.relative_alt / 1000.0;
                Telemetry.Heading = gp.hdg / 100.0;
                break;

            case (uint)MAVLink.MAVLINK_MSG_ID.VFR_HUD:
                var vfr = (MAVLink.mavlink_vfr_hud_t)msg.data;
                Telemetry.Speed = vfr.groundspeed;
                break;

            case (uint)MAVLink.MAVLINK_MSG_ID.GPS_RAW_INT:
                var gps = (MAVLink.mavlink_gps_raw_int_t)msg.data;
                Telemetry.GpsSats = gps.satellites_visible;
                Telemetry.GpsFix = gps.fix_type;
                break;

            case (uint)MAVLink.MAVLINK_MSG_ID.SYS_STATUS:
                var sys = (MAVLink.mavlink_sys_status_t)msg.data;
                if (sys.battery_remaining >= 0)
                    Telemetry.BatteryPct = sys.battery_remaining;
                break;

            case (uint)MAVLink.MAVLINK_MSG_ID.ATTITUDE:
                var att = (MAVLink.mavlink_attitude_t)msg.data;
                Telemetry.Roll = att.roll * 180.0 / Math.PI;
                Telemetry.Pitch = att.pitch * 180.0 / Math.PI;
                break;
        }

        TelemetryUpdated?.Invoke(Telemetry);
    }

    public async Task SendCommandAsync(string host, int port, ushort command,
        float p1=0,float p2=0,float p3=0,float p4=0,float p5=0,float p6=0,float p7=0)
    {
        var msg = new MAVLink.mavlink_command_long_t
        {
            target_system = 1,
            target_component = 1,
            command = command,
            param1=p1,param2=p2,param3=p3,param4=p4,param5=p5,param6=p6,param7=p7
        };
        var packet = _parser.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, msg);
        var toSend = _enc.IsEncryptionEnabled ? _enc.Encrypt(packet) : packet;
        using var udp = new UdpClient();
        await udp.SendAsync(toSend, toSend.Length, host, port);
    }
}
