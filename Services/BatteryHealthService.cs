using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace InfraDroneDesktop.Services;

public class BatteryFlightRecord
{
    public string Timestamp { get; set; } = "";
    public double DurationMinutes { get; set; }
    public float StartVoltage { get; set; }
    public float MinVoltage { get; set; }
    public float MaxVoltage { get; set; }
    public int StartBatteryPct { get; set; }
    public int EndBatteryPct { get; set; }
}

public class BatteryHealthService
{
    private readonly string _logPath;
    private bool _wasArmed = false;
    private DateTime _flightStart;
    private float _minV = float.MaxValue, _maxV = float.MinValue, _startV = 0;
    private int _startPct = 0;

    public BatteryHealthService()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _logPath = Path.Combine(home, "agri_drone", "battery_health_log.jsonl");
    }

    public void OnTelemetryUpdate(TelemetryData t)
    {
        if (t.Armed && !_wasArmed)
        {
            _flightStart = DateTime.Now;
            _minV = t.BatteryVoltage; _maxV = t.BatteryVoltage; _startV = t.BatteryVoltage;
            _startPct = t.BatteryPct;
        }
        if (t.Armed)
        {
            if (t.BatteryVoltage > 0)
            {
                _minV = Math.Min(_minV, t.BatteryVoltage);
                _maxV = Math.Max(_maxV, t.BatteryVoltage);
            }
        }
        if (!t.Armed && _wasArmed)
        {
            var record = new BatteryFlightRecord
            {
                Timestamp = _flightStart.ToString("o"),
                DurationMinutes = (DateTime.Now - _flightStart).TotalMinutes,
                StartVoltage = _startV,
                MinVoltage = _minV == float.MaxValue ? 0 : _minV,
                MaxVoltage = _maxV == float.MinValue ? 0 : _maxV,
                StartBatteryPct = _startPct,
                EndBatteryPct = t.BatteryPct
            };
            AppendRecord(record);
        }
        _wasArmed = t.Armed;
    }

    private void AppendRecord(BatteryFlightRecord r)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        File.AppendAllText(_logPath, JsonSerializer.Serialize(r) + "\n");
    }

    public List<BatteryFlightRecord> LoadHistory()
    {
        if (!File.Exists(_logPath)) return new();
        return File.ReadAllLines(_logPath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<BatteryFlightRecord>(l)!)
            .OrderBy(r => r.Timestamp)
            .ToList();
    }
}
