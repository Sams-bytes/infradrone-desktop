using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;

namespace InfraDroneDesktop.Services
{
    /// <summary>One result in the Province of Groningen traffic study (picture, video, report or table).</summary>
    public class StudyItem
    {
        // JsonPropertyName on every field: System.Text.Json is case-sensitive and the manifest uses lowercase keys.
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";       // image | video | html | csv
        [JsonPropertyName("path")] public string FilePath { get; set; } = "";
        [JsonPropertyName("thumb")] public string? Thumb { get; set; }
        [JsonPropertyName("tag")] public string Tag { get; set; } = "";         // measured | modelled | ...
        [JsonPropertyName("source")] public string Source { get; set; } = "";
        [JsonPropertyName("table")] public List<List<string>>? Table { get; set; }

        [JsonIgnore] public string Section { get; set; } = "";
        [JsonIgnore] public bool Exists => File.Exists(FilePath) || Directory.Exists(FilePath);

        [JsonIgnore]
        public string TagLabel => Tag switch
        {
            "measured" => "Measured",
            "modelled" => "Modelled (indication)",
            "designed" => "Designed behaviour",
            "validation" => "Validation",
            "demo" => "Demo",
            "report" => "Report",
            "in_progress" => "In progress",
            _ => Tag,
        };

        [JsonIgnore]
        public string KindLabel => (Kind switch
        {
            "video" => "Video",
            "html" => "Report",
            "csv" => "Table",
            "image" => "Picture",
            _ => Kind,
        }) + "  |  " + TagLabel + (Exists ? "" : "  |  file missing");

        [JsonIgnore]
        public IBrush TagBrush => Brush.Parse(Tag switch
        {
            "measured" => "#1D4E89",
            "modelled" => "#B08A00",
            "designed" => "#4F7F3A",
            "validation" => "#455A64",
            "demo" => "#6A3D9A",
            "report" => "#1F2A33",
            "in_progress" => "#C8102E",
            _ => "#455A64",
        });
    }

    public class StudySection
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("items")] public List<StudyItem> Items { get; set; } = new();
    }

    public class StudyManifest
    {
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("generated")] public string Generated { get; set; } = "";
        [JsonPropertyName("sections")] public List<StudySection> Sections { get; set; } = new();
    }

    public static class StudyManifestService
    {
        public static string StudyFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "DAMbv_Data", "09_Province_Traffic_Study");

        public static string ManifestPath => Path.Combine(StudyFolder, "manifest.json");

        public static StudyManifest? Load(out string error)
        {
            error = "";
            try
            {
                if (!File.Exists(ManifestPath))
                {
                    error = $"No manifest at {ManifestPath}.";
                    return null;
                }
                var m = JsonSerializer.Deserialize<StudyManifest>(File.ReadAllText(ManifestPath));
                if (m == null) error = "Manifest is empty or unreadable.";
                return m;
            }
            catch (Exception ex)
            {
                error = $"Could not read manifest: {ex.Message}";
                return null;
            }
        }

        /// <summary>Open a file or folder with the system's default program (video player, browser, viewer).</summary>
        public static void OpenExternal(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else
            {
                var psi = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
                psi.ArgumentList.Add(path);
                Process.Start(psi);
            }
        }
    }
}
