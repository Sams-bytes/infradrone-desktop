using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace InfraDroneDesktop.Services;

public class OdmProject
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class OdmTask
{
    public string Uuid { get; set; } = "";
    public string Name { get; set; } = "";
    public int Status { get; set; }
    public double Progress { get; set; }
    public string StatusLabel => Status switch
    {
        10 => "Queued", 20 => "Running", 30 => "Failed",
        40 => "Completed", 50 => "Cancelled", _ => "Unknown"
    };
}

public class WebOdmService
{
    private readonly HttpClient _http = new HttpClient();
    private string _token = "";
    private const string BASE = "http://localhost:8000";

    public bool IsAuthenticated => !string.IsNullOrEmpty(_token);

    public async Task<bool> LoginAsync(string username, string password)
    {
        try
        {
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("username", username),
                new KeyValuePair<string, string>("password", password)
            });
            var resp = await _http.PostAsync($"{BASE}/api/token-auth/", form);
            var json = await resp.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("token", out var t))
            {
                _token = t.GetString() ?? "";
                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("JWT", _token);
                return true;
            }
            return false;
        }
        catch { return false; }
    }

    public async Task<List<OdmProject>> GetProjectsAsync()
    {
        var resp = await _http.GetStringAsync($"{BASE}/api/projects/?format=json");
        var doc = JsonDocument.Parse(resp);
        var projects = new List<OdmProject>();
        var results = doc.RootElement.TryGetProperty("results", out var r) ? r : doc.RootElement;
        foreach (var p in results.EnumerateArray())
            projects.Add(new OdmProject
            {
                Id = p.GetProperty("id").GetInt32(),
                Name = p.GetProperty("name").GetString() ?? ""
            });
        return projects;
    }

    public async Task<int> CreateProjectAsync(string name)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(new { name }),
            System.Text.Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"{BASE}/api/projects/", content);
        var json = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("id").GetInt32();
    }

    public async Task<string> UploadImagesAsync(int projectId, string[] imagePaths,
        Action<int, int>? progress = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("InfraDrone Task"), "name");
        form.Add(new StringContent("{}"), "options");

        int i = 0;
        foreach (var path in imagePaths)
        {
            progress?.Invoke(++i, imagePaths.Length);
            var bytes = await File.ReadAllBytesAsync(path);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(content, "images", Path.GetFileName(path));
        }

        var resp = await _http.PostAsync($"{BASE}/api/projects/{projectId}/tasks/", form);
        var json = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("id").GetString() ?? "";
    }

    public async Task<OdmTask?> GetTaskStatusAsync(int projectId, string taskId)
    {
        try
        {
            var json = await _http.GetStringAsync($"{BASE}/api/projects/{projectId}/tasks/{taskId}/?format=json");
            var doc = JsonDocument.Parse(json);
            return new OdmTask
            {
                Uuid = taskId,
                Name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetInt32() : 0,
                Progress = doc.RootElement.TryGetProperty("running_progress", out var p) ? p.GetDouble() * 100 : 0
            };
        }
        catch { return null; }
    }

    public string GetOrthomosaicUrl(int projectId, string taskId) =>
        $"{BASE}/api/projects/{projectId}/tasks/{taskId}/download/orthophoto.tif";

    public string GetReportUrl(int projectId, string taskId) =>
        $"{BASE}/api/projects/{projectId}/tasks/{taskId}/download/report.pdf";
}
