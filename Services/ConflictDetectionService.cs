using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace InfraDroneDesktop.Services;
public class ConflictEvent
{
    [JsonPropertyName("roundabout")]
    public string Roundabout { get; set; } = "";
    [JsonPropertyName("table")]
    public string Table { get; set; } = "";
    [JsonPropertyName("objid_a")]
    public int ObjIdA { get; set; }
    [JsonPropertyName("objid_b")]
    public int ObjIdB { get; set; }
    [JsonPropertyName("min_distance_m")]
    public double MinDistanceM { get; set; }
    [JsonPropertyName("min_ttc_s")]
    public double MinTtcS { get; set; }
    [JsonPropertyName("n_frames")]
    public int NFrames { get; set; }
    [JsonPropertyName("t_start")]
    public double TStart { get; set; }
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "";
}
public class ConflictDataset
{
    [JsonPropertyName("generated_from")]
    public string GeneratedFrom { get; set; } = "";
    [JsonPropertyName("methodology")]
    public string Methodology { get; set; } = "";
    [JsonPropertyName("recordings_scanned")]
    public int RecordingsScanned { get; set; }
    [JsonPropertyName("total_rows_scanned")]
    public int TotalRowsScanned { get; set; }
    [JsonPropertyName("total_events")]
    public int TotalEvents { get; set; }
    [JsonPropertyName("high_severity_count")]
    public int HighSeverityCount { get; set; }
    [JsonPropertyName("medium_severity_count")]
    public int MediumSeverityCount { get; set; }
    [JsonPropertyName("events")]
    public List<ConflictEvent> Events { get; set; } = new();
}
public static class ConflictDetectionService
{
    // Same base path convention as StoryModeView -- reads directly from the
    // opendd_dataset folder rather than duplicating the file into app storage.
    private const string JsonPath = "/home/sam/opendd_dataset/conflict_events.json";
    public static ConflictDataset? Load()
    {
        if (!File.Exists(JsonPath)) return null;
        try
        {
            var json = File.ReadAllText(JsonPath);
            return JsonSerializer.Deserialize<ConflictDataset>(json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ConflictDetectionService] Failed to load {JsonPath}: {ex.Message}");
            return null;
        }
    }
}
