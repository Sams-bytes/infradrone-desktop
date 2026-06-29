using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace InfraDroneDesktop.Services;

public class TerrainPoint
{
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double GroundElevM { get; set; }
    public double ClearanceM { get; set; } = 30;
    public double AbsAltM => GroundElevM + ClearanceM;
}

public class TerrainService
{
    private readonly HttpClient _http = new HttpClient();
    private const string API = "https://api.opentopodata.org/v1/eudem25m";

    public TerrainService()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "InfraDrone-GCS/1.0 DAMbv-BV");
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<List<TerrainPoint>> GetElevationsAsync(
        List<(double Lat, double Lon)> coords, double clearanceM = 30)
    {
        var result = new List<TerrainPoint>();
        // Process in batches of 100
        for (int i = 0; i < coords.Count; i += 100)
        {
            var batch = coords.GetRange(i, Math.Min(100, coords.Count - i));
            var locations = string.Join("|", batch.ConvertAll(c => $"{c.Lat},{c.Lon}"));
            var url = $"{API}?locations={locations}";
            var response = await _http.GetStringAsync(url);
            var doc = JsonDocument.Parse(response);
            if (!doc.RootElement.TryGetProperty("results", out var results)) continue;
            int j = 0;
            foreach (var r in results.EnumerateArray())
            {
                var elev = r.TryGetProperty("elevation", out var e) ? e.GetDouble() : 0;
                result.Add(new TerrainPoint
                {
                    Lat = batch[j].Lat,
                    Lon = batch[j].Lon,
                    GroundElevM = elev,
                    ClearanceM = clearanceM
                });
                j++;
            }
            if (i + 100 < coords.Count)
                await Task.Delay(1100); // respect 1 req/sec limit
        }
        return result;
    }
}
