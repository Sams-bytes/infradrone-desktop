using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace InfraDroneDesktop.Services;

public class NotamEntry
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string Start { get; set; } = "";
    public string End { get; set; } = "";
    public string Icao { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public string Classification { get; set; } = "";
}

public class NotamService
{
    private readonly HttpClient _http = new HttpClient();

    public List<NotamEntry> Notams { get; private set; } = new();
    public DateTime LastFetched { get; private set; } = DateTime.MinValue;
    public string Status { get; private set; } = "Not loaded";
    public event Action? NotamsUpdated;

    public NotamService()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "InfraDrone-GCS/1.0 DAMbv-BV");
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task FetchAsync()
    {
        try
        {
            Status = "Fetching NOTAMs...";
            NotamsUpdated?.Invoke();

            var allNotams = new List<NotamEntry>();
            var icaos = new[] { "EHGG", "EHAM", "EHLE", "EHEH" };
            var ids = string.Join(",", icaos);

            // aviationweather.gov - free, no API key, global coverage
            var url = $"https://aviationweather.gov/api/data/notam?location={ids}&format=json";
            var response = await _http.GetStringAsync(url);
            var doc = JsonDocument.Parse(response);

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var entry = new NotamEntry();

                if (item.TryGetProperty("icaoLocation", out var icao))
                    entry.Icao = icao.GetString() ?? "";
                if (item.TryGetProperty("number", out var num))
                    entry.Id = num.GetString() ?? "";
                if (item.TryGetProperty("text", out var text))
                    entry.Text = text.GetString() ?? "";
                if (item.TryGetProperty("effectiveStart", out var sd))
                    entry.Start = sd.GetString() ?? "";
                if (item.TryGetProperty("effectiveEnd", out var ed))
                    entry.End = ed.GetString() ?? "";
                if (item.TryGetProperty("classification", out var cls))
                    entry.Classification = cls.GetString() ?? "";

                // Check if active
                if (DateTime.TryParse(entry.End, out var endDt))
                    entry.IsActive = endDt > DateTime.UtcNow;

                if (!string.IsNullOrWhiteSpace(entry.Text))
                    allNotams.Add(entry);
            }

            Notams = allNotams;
            LastFetched = DateTime.UtcNow;
            Status = allNotams.Count > 0
                ? $"{allNotams.Count} NOTAMs — {LastFetched:HH:mm} UTC"
                : "No NOTAMs found";
            NotamsUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            Status = $"NOTAM fetch failed: {ex.Message}";
            NotamsUpdated?.Invoke();
        }
    }
}
