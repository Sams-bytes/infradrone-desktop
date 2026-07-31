using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InfraDroneDesktop.Services;

public class AdsbAircraft
{
    public string Icao24 { get; set; } = "";
    public string? Callsign { get; set; }
    public double Longitude { get; set; }
    public double Latitude { get; set; }
    public double? BaroAltitude { get; set; }
    public bool OnGround { get; set; }
    public double? Velocity { get; set; }
    public double? TrueTrack { get; set; }
}

public class AdsbService
{
    private readonly HttpClient _http = new HttpClient();
    private CancellationTokenSource? _cts;

    public event Action<List<AdsbAircraft>>? AircraftUpdated;

    // Real OpenSky Network REST API, anonymous access (400 credits/day,
    // 10-second resolution). Field order verified against official docs
    // (18-field positional state vector array), not guessed.
    public void Start(double laMin, double loMin, double laMax, double loMax, int pollSeconds = 15)
    {
        _cts = new CancellationTokenSource();
        _ = PollLoop(laMin, loMin, laMax, loMax, pollSeconds, _cts.Token);
    }

    public void Stop() => _cts?.Cancel();

    private async Task PollLoop(double laMin, double loMin, double laMax, double loMax, int pollSeconds, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var url = $"https://opensky-network.org/api/states/all?lamin={laMin}&lomin={loMin}&lamax={laMax}&lomax={loMax}";
                var response = await _http.GetAsync(url, ct);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    Console.WriteLine("[ADS-B] Rate limited (429) -- daily credit quota likely exhausted, backing off.");
                    await Task.Delay(TimeSpan.FromMinutes(5), ct);
                    continue;
                }
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                var aircraft = ParseStates(json);
                AircraftUpdated?.Invoke(aircraft);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ADS-B] Poll failed: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSeconds), ct);
        }
    }

    // Parses the real, verified OpenSky state vector field order:
    // 0=icao24, 1=callsign, 2=origin_country, 3=time_position, 4=last_contact,
    // 5=longitude, 6=latitude, 7=baro_altitude, 8=on_ground, 9=velocity,
    // 10=true_track, 11=vertical_rate, 12=sensors, 13=geo_altitude,
    // 14=squawk, 15=spi, 16=position_source, 17=category
    private List<AdsbAircraft> ParseStates(string json)
    {
        var result = new List<AdsbAircraft>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("states", out var states) || states.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var state in states.EnumerateArray())
        {
            if (state.ValueKind != JsonValueKind.Array || state.GetArrayLength() < 11) continue;

            var lonEl = state[5];
            var latEl = state[6];
            if (lonEl.ValueKind == JsonValueKind.Null || latEl.ValueKind == JsonValueKind.Null) continue;

            result.Add(new AdsbAircraft
            {
                Icao24 = state[0].GetString() ?? "",
                Callsign = state[1].ValueKind == JsonValueKind.Null ? null : state[1].GetString()?.Trim(),
                Longitude = lonEl.GetDouble(),
                Latitude = latEl.GetDouble(),
                BaroAltitude = state[7].ValueKind == JsonValueKind.Null ? null : state[7].GetDouble(),
                OnGround = state[8].GetBoolean(),
                Velocity = state[9].ValueKind == JsonValueKind.Null ? null : state[9].GetDouble(),
                TrueTrack = state[10].ValueKind == JsonValueKind.Null ? null : state[10].GetDouble(),
            });
        }
        return result;
    }
}
