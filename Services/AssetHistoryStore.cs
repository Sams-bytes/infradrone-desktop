using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
namespace InfraDroneDesktop.Services;

public class AssetHistoryEntry
{
    public DateTime Date { get; set; }
    public string Type { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Description { get; set; } = "";
    public string LayerName { get; set; } = "";
}

public static class AssetHistoryStore
{
    private static readonly string BaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "infradrone-desktop", "asset_history");

    private static string PathFor(string assetKey)
    {
        Directory.CreateDirectory(BaseDir);
        var safe = string.Join("_", assetKey.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(BaseDir, safe + ".json");
    }

    public static void AppendEvent(string assetKey, AssetHistoryEntry entry)
    {
        var entries = GetHistory(assetKey);
        entries.Add(entry);
        File.WriteAllText(PathFor(assetKey), JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static List<AssetHistoryEntry> GetHistory(string assetKey)
    {
        var path = PathFor(assetKey);
        if (!File.Exists(path)) return new List<AssetHistoryEntry>();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<AssetHistoryEntry>>(json) ?? new List<AssetHistoryEntry>();
        }
        catch
        {
            return new List<AssetHistoryEntry>();
        }
    }
}
