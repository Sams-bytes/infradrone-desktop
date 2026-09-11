using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace InfraDroneDesktop.Services
{
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
    }

    // Talks to the local Python bridge (bluegrass_bridge.py) over HTTP.
    // The bridge holds the one live pyparrot connection to the drone;
    // this service is just the C# side client + polling loop.
    public class BluegrassVehicleService : IDisposable
    {
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

        public async Task<bool> ConnectAsync()
        {
            try
            {
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
                    WifiRssi = result.Telemetry?.WifiRssi
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

        public void Dispose()
        {
            _pollTimer?.Dispose();
            _http?.Dispose();
        }

        private class ConnectResult
        {
            [JsonPropertyName("ok")] public bool Ok { get; set; }
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
        }
    }
}
