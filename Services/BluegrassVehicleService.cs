using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace InfraDroneDesktop.Services
{
    public class SettingSchema
    {
        [JsonPropertyName("args")] public List<string> Args { get; set; } = new();
        [JsonPropertyName("ranges")] public Dictionary<string, List<object>> Ranges { get; set; } = new();
    }

    public class BluegrassTelemetry
    {
        public bool Connected { get; set; }
        public double? BatteryPct { get; set; }
        public string? FlyingState { get; set; }
        public int? GpsFixed { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public double? Altitude { get; set; }
        public double? WifiRssi { get; set; }
        public double? Speed { get; set; }
        public double? Heading { get; set; }
        public double? RollDeg { get; set; }
        public double? PitchDeg { get; set; }
    }

    // Talks to the local Python bridge (bluegrass_bridge.py) over HTTP.
    // The bridge holds the one live pyparrot connection to the drone;
    // this service is just the C# side client + polling loop.
    public class BluegrassVehicleService : IDisposable
    {
        private Process? _bridgeProcess;
        private readonly HttpClient _http;
        private readonly System.Timers.Timer _pollTimer;
        private const string BaseUrl = "http://127.0.0.1:5057";

        public bool IsConnected { get; private set; }
        public BluegrassTelemetry Telemetry { get; private set; } = new BluegrassTelemetry();

        public event Action<BluegrassTelemetry>? TelemetryUpdated;
        public event Action<string>? StatusMessage;

        public BluegrassVehicleService()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            _pollTimer = new System.Timers.Timer(1000); // 1Hz, matches bridge's own state cadence
            _pollTimer.Elapsed += async (_, _) => await PollStatusAsync();
        }

        private async Task<bool> EnsureBridgeRunningAsync()
        {
            if (await IsBridgeReachableAsync()) return true;

            StatusMessage?.Invoke("Starting Bluegrass bridge process...");
            if (!StartBridgeProcess())
            {
                StatusMessage?.Invoke("Failed to launch bluegrass_bridge.py - check it exists at ~/bluegrass_bridge.py.");
                return false;
            }

            for (int i = 0; i < 16; i++)
            {
                await Task.Delay(500);
                if (await IsBridgeReachableAsync()) return true;
            }
            return false;
        }

        private async Task<bool> IsBridgeReachableAsync()
        {
            try
            {
                using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                var resp = await probe.GetAsync($"{BaseUrl}/status");
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private bool StartBridgeProcess()
        {
            try
            {
                var scriptPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "bluegrass_bridge.py");

                if (!File.Exists(scriptPath))
                {
                    StatusMessage?.Invoke($"bluegrass_bridge.py not found at {scriptPath}");
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "python3",
                    Arguments = scriptPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                _bridgeProcess = Process.Start(psi);
                if (_bridgeProcess == null) return false;

                _bridgeProcess.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Console.WriteLine($"[bluegrass_bridge] {e.Data}");
                };
                _bridgeProcess.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Console.WriteLine($"[bluegrass_bridge:err] {e.Data}");
                };
                _bridgeProcess.BeginOutputReadLine();
                _bridgeProcess.BeginErrorReadLine();

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BluegrassVehicleService] Failed to start bridge: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                var bridgeUp = await EnsureBridgeRunningAsync();
                if (!bridgeUp)
                {
                    StatusMessage?.Invoke("Could not start Bluegrass bridge (bluegrass_bridge.py).");
                    return false;
                }

                StatusMessage?.Invoke("Connecting to Bluegrass bridge...");
                var resp = await _http.PostAsync($"{BaseUrl}/connect", null);
                var result = await resp.Content.ReadFromJsonAsync<ConnectResult>();
                IsConnected = result?.Ok ?? false;

                if (IsConnected)
                {
                    _pollTimer.Start();
                    StatusMessage?.Invoke("Bluegrass connected.");
                }
                else
                {
                    StatusMessage?.Invoke("Bluegrass connect failed - check SkyController 2 is powered OFF.");
                }
                return IsConnected;
            }
            catch (Exception ex)
            {
                StatusMessage?.Invoke($"Bluegrass bridge unreachable: {ex.Message} (is bluegrass_bridge.py running?)");
                IsConnected = false;
                return false;
            }
        }

        public async Task DisconnectAsync()
        {
            _pollTimer.Stop();
            try { await _http.PostAsync($"{BaseUrl}/disconnect", null); } catch { /* best effort */ }
            IsConnected = false;
        }

        private async Task PollStatusAsync()
        {
            try
            {
                var resp = await _http.GetAsync($"{BaseUrl}/status");
                var result = await resp.Content.ReadFromJsonAsync<StatusResult>();
                if (result is null) return;

                Telemetry = new BluegrassTelemetry
                {
                    Connected = result.Connected,
                    BatteryPct = result.Telemetry?.BatteryPct,
                    FlyingState = result.Telemetry?.FlyingState,
                    GpsFixed = result.Telemetry?.GpsFixed,
                    Latitude = result.Telemetry?.Latitude,
                    Longitude = result.Telemetry?.Longitude,
                    Altitude = result.Telemetry?.Altitude,
                    WifiRssi = result.Telemetry?.WifiRssi,
                    Speed = result.Telemetry?.Speed,
                    Heading = result.Telemetry?.Heading,
                    RollDeg = result.Telemetry?.RollDeg,
                    PitchDeg = result.Telemetry?.PitchDeg
                };
                TelemetryUpdated?.Invoke(Telemetry);
            }
            catch
            {
                // transient poll miss - don't spam status message every second
            }
        }

        public async Task<bool> TakeoffAsync()
        {
            var resp = await _http.PostAsync($"{BaseUrl}/command/takeoff", null);
            return resp.IsSuccessStatusCode;
        }

        public async Task<bool> LandAsync()
        {
            var resp = await _http.PostAsync($"{BaseUrl}/command/land", null);
            return resp.IsSuccessStatusCode;
        }

        public async Task<bool> EmergencyAsync()
        {
            var resp = await _http.PostAsync($"{BaseUrl}/command/emergency", null);
            return resp.IsSuccessStatusCode;
        }

        public async Task<bool> MoveAsync(sbyte roll, sbyte pitch, sbyte yaw, sbyte verticalMovement, double duration = 0.5)
        {
            var body = new { roll, pitch, yaw, vertical_movement = verticalMovement, duration };
            var resp = await _http.PostAsJsonAsync($"{BaseUrl}/command/move", body);
            return resp.IsSuccessStatusCode;
        }

        public async Task<Dictionary<string, SettingSchema>?> GetSettingsSchemaAsync()
        {
            try
            {
                var resp = await _http.GetAsync($"{BaseUrl}/settings");
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadFromJsonAsync<Dictionary<string, SettingSchema>>();
            }
            catch
            {
                return null;
            }
        }

        public async Task<(bool Ok, string? Error)> SetSettingAsync(string name, Dictionary<string, object> args)
        {
            var resp = await _http.PostAsJsonAsync($"{BaseUrl}/settings/{name}", args);
            var result = await resp.Content.ReadFromJsonAsync<SettingResult>();
            if (resp.IsSuccessStatusCode) return (true, null);
            return (false, result?.Error ?? $"HTTP {(int)resp.StatusCode}");
        }

        public async Task<Dictionary<string, object>?> GetFullTelemetryAsync()
        {
            try
            {
                var resp = await _http.GetAsync($"{BaseUrl}/telemetry/full");
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
            }
            catch
            {
                return null;
            }
        }

        public async Task<bool> CameraPanTiltAsync(double tiltDegrees, double panDegrees)
        {
            var body = new { tilt_degrees = tiltDegrees, pan_degrees = panDegrees };
            var resp = await _http.PostAsJsonAsync($"{BaseUrl}/command/camera", body);
            return resp.IsSuccessStatusCode;
        }

        public async Task<bool> FlipAsync(string direction)
        {
            var body = new { direction };
            var resp = await _http.PostAsJsonAsync($"{BaseUrl}/command/flip", body);
            return resp.IsSuccessStatusCode;
        }

        public async Task<bool> StartVideoAsync()
        {
            var resp = await _http.PostAsync($"{BaseUrl}/video/start", null);
            return resp.IsSuccessStatusCode;
        }

        public async Task<bool> StopVideoAsync()
        {
            var resp = await _http.PostAsync($"{BaseUrl}/video/stop", null);
            return resp.IsSuccessStatusCode;
        }

        public async Task<byte[]?> GetVideoFrameAsync()
        {
            try
            {
                var resp = await _http.GetAsync($"{BaseUrl}/video/snapshot");
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsByteArrayAsync();
            }
            catch
            {
                return null;
            }
        }

        public async Task<bool> FlatTrimAsync()
        {
            var resp = await _http.PostAsync($"{BaseUrl}/command/flat_trim", null);
            return resp.IsSuccessStatusCode;
        }

        public void Dispose()
        {
            _pollTimer?.Dispose();
            _http?.Dispose();
            if (_bridgeProcess != null && !_bridgeProcess.HasExited)
            {
                try { _bridgeProcess.Kill(); } catch { /* best effort on app close */ }
            }
        }

        private class ConnectResult
        {
            [JsonPropertyName("ok")] public bool Ok { get; set; }
        }
        private class SettingResult
        {
            [JsonPropertyName("ok")] public bool Ok { get; set; }
            [JsonPropertyName("error")] public string? Error { get; set; }
        }
        private class StatusResult
        {
            [JsonPropertyName("connected")] public bool Connected { get; set; }
            [JsonPropertyName("telemetry")] public TelemetryPayload? Telemetry { get; set; }
        }
        private class TelemetryPayload
        {
            [JsonPropertyName("battery_percent")] public double? BatteryPct { get; set; }
            [JsonPropertyName("flying_state")] public string? FlyingState { get; set; }
            [JsonPropertyName("gps_fixed")] public int? GpsFixed { get; set; }
            [JsonPropertyName("latitude")] public double? Latitude { get; set; }
            [JsonPropertyName("longitude")] public double? Longitude { get; set; }
            [JsonPropertyName("altitude")] public double? Altitude { get; set; }
            [JsonPropertyName("wifi_rssi")] public double? WifiRssi { get; set; }
            [JsonPropertyName("speed")] public double? Speed { get; set; }
            [JsonPropertyName("heading")] public double? Heading { get; set; }
            [JsonPropertyName("roll_deg")] public double? RollDeg { get; set; }
            [JsonPropertyName("pitch_deg")] public double? PitchDeg { get; set; }
        }
    }
}
