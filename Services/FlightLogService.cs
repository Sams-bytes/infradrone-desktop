using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using static MAVLink;
using System.Globalization;
using System.Linq;

namespace InfraDroneDesktop.Services;

public class FlightLogPoint
{
    public DateTime Time { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double AltRel { get; set; }
    public double Speed { get; set; }
    public double Heading { get; set; }
    public string Mode { get; set; } = "";
    public bool Armed { get; set; }
}

public class FlightLogSession
{
    public string FileName { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public List<FlightLogPoint> Points { get; set; } = new();
    public double MaxAlt { get; set; }
    public double MaxSpeed { get; set; }
    public double TotalDistKm { get; set; }
    public TimeSpan Duration => End - Start;
}

public class FlightLogService
{
    private static readonly Dictionary<int, string> FlightModes = new()
    {
        {0,"Stabilize"},{1,"Acro"},{2,"AltHold"},{3,"Auto"},
        {4,"Guided"},{5,"Loiter"},{6,"RTL"},{9,"Land"},{16,"PosHold"}
    };

    public async Task<FlightLogSession?> ParseTlogAsync(string path)
    {
        return await Task.Run(() => ParseTlog(path));
    }

    private FlightLogSession? ParseTlog(string path)
    {
        try
        {
            var session = new FlightLogSession { FileName = Path.GetFileName(path) };
            var points = new List<FlightLogPoint>();
            var parser = new MavlinkParse();
            string currentMode = "";
            bool armed = false;

            using var stream = File.OpenRead(path);
            while (stream.Position < stream.Length - 8)
            {
                // Read 8-byte timestamp header
                var tsBuf = new byte[8];
                if (stream.Read(tsBuf, 0, 8) < 8) break;
                var tsUs = BitConverter.ToInt64(tsBuf, 0);
                var time = DateTimeOffset.FromUnixTimeMilliseconds(tsUs / 1000).UtcDateTime;

                var msg = parser.ReadPacket(stream);
                if (msg == null) continue;

                switch (msg.msgid)
                {
                    case (uint)MAVLINK_MSG_ID.GLOBAL_POSITION_INT:
                        var gp = (mavlink_global_position_int_t)msg.data;
                        if (gp.lat == 0 && gp.lon == 0) break;
                        var pt = new FlightLogPoint
                        {
                            Time = time,
                            Lat = gp.lat / 1e7,
                            Lon = gp.lon / 1e7,
                            AltRel = gp.relative_alt / 1000.0,
                            Heading = gp.hdg / 100.0,
                            Mode = currentMode,
                            Armed = armed
                        };
                        points.Add(pt);
                        break;

                    case (uint)MAVLINK_MSG_ID.VFR_HUD:
                        var vfr = (mavlink_vfr_hud_t)msg.data;
                        if (points.Count > 0)
                            points[^1].Speed = vfr.groundspeed;
                        break;

                    case (uint)MAVLINK_MSG_ID.HEARTBEAT:
                        var hb = (mavlink_heartbeat_t)msg.data;
                        armed = (hb.base_mode & (byte)MAV_MODE_FLAG.SAFETY_ARMED) != 0;
                        if (FlightModes.TryGetValue((int)hb.custom_mode, out var m))
                            currentMode = m;
                        break;
                }
            }

            if (points.Count == 0) return null;

            session.Points = points;
            session.Start = points[0].Time;
            session.End = points[^1].Time;
            session.MaxAlt = 0;
            session.MaxSpeed = 0;

            for (int i = 0; i < points.Count; i++)
            {
                if (points[i].AltRel > session.MaxAlt) session.MaxAlt = points[i].AltRel;
                if (points[i].Speed > session.MaxSpeed) session.MaxSpeed = points[i].Speed;
                if (i > 0)
                    session.TotalDistKm += Haversine(points[i-1].Lat, points[i-1].Lon,
                                                      points[i].Lat, points[i].Lon);
            }

            return session;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FlightLog] Parse error: {ex.Message}");
            return null;
        }
    }

    public async Task<FlightLogSession?> ParseCsvAsync(string path)
    {
        return await Task.Run(() =>
        {
            try
            {
                var lines = File.ReadAllLines(path);
                if (lines.Length < 2) return null;
                var session = new FlightLogSession { FileName = System.IO.Path.GetFileName(path) };
                var points = new List<FlightLogPoint>();
                foreach (var line in lines.Skip(1))
                {
                    var cols = line.Split(',');
                    if (cols.Length < 8) continue;
                    if (!double.TryParse(cols[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ts)) continue;
                    if (!double.TryParse(cols[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) continue;
                    if (!double.TryParse(cols[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;
                    if (!double.TryParse(cols[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var alt)) continue;
                    double.TryParse(cols[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var hdg);
                    double.TryParse(cols[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var spd);
                    points.Add(new FlightLogPoint
                    {
                        Time = DateTimeOffset.FromUnixTimeMilliseconds((long)(ts * 1000)).UtcDateTime,
                        Lat = lat, Lon = lon, AltRel = alt, Heading = hdg,
                        Speed = spd, Mode = cols[6], Armed = cols[7].Trim() == "True"
                    });
                }
                if (points.Count == 0) return null;
                session.Points = points;
                session.Start = points[0].Time;
                session.End = points[^1].Time;
                foreach (var p in points)
                {
                    if (p.AltRel > session.MaxAlt) session.MaxAlt = p.AltRel;
                    if (p.Speed > session.MaxSpeed) session.MaxSpeed = p.Speed;
                }
                for (int i = 1; i < points.Count; i++)
                    session.TotalDistKm += Haversine(points[i-1].Lat, points[i-1].Lon, points[i].Lat, points[i].Lon);
                return session;
            }
            catch (Exception ex) { Console.WriteLine("[FlightLog CSV] " + ex.Message); return null; }
        });
    }

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat/2)*Math.Sin(dLat/2) +
                Math.Cos(lat1*Math.PI/180)*Math.Cos(lat2*Math.PI/180)*
                Math.Sin(dLon/2)*Math.Sin(dLon/2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1-a));
    }
}
