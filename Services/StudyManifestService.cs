using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
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
        [JsonPropertyName("tag")] public string Tag { get; set; } = "";
        [JsonPropertyName("source")] public string Source { get; set; } = "";
        [JsonPropertyName("table")] public List<List<string>>? Table { get; set; }

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
            "html" => "Report / map",
            "csv" => "Table",
            "image" => "Picture",
            _ => Kind,
        }) + "  |  " + TagLabel + (Exists ? "" : "  |  file missing");

        [JsonIgnore]
        public IBrush TagBrush => StudyColors.ForTag(Tag);
    }

    public class StudySection
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("short")] public string Short { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("intro_nl")] public string IntroNl { get; set; } = "";
        [JsonPropertyName("intro_en")] public string IntroEn { get; set; } = "";
        [JsonPropertyName("items")] public List<StudyItem> Items { get; set; } = new();
    }

    public class StudyManifest
    {
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("generated")] public string Generated { get; set; } = "";
        [JsonPropertyName("sections")] public List<StudySection> Sections { get; set; } = new();
    }

    /// <summary>One ranked candidate site from candidates.csv.</summary>
    public class SiteCandidate
    {
        public string Question { get; set; } = "";
        public int Rank { get; set; }
        public string Label { get; set; } = "";
        public string Score { get; set; } = "";
        public string Reasons { get; set; } = "";
        public double Lat { get; set; }
        public double Lon { get; set; }
        public string ListLine => $"{Rank}. {Label}";
        public string ScoreLine => $"Score {Score}";
        public string Coordinates => string.Format(CultureInfo.InvariantCulture, "{0:0.000000}, {1:0.000000}", Lat, Lon);
    }

    public static class StudyColors
    {
        public static IBrush ForTag(string tag) => Brush.Parse(tag switch
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

        public static IBrush ForQuestion(string q) => Brush.Parse(q switch
        {
            "Q1" => "#6A3D9A",
            "Q2" => "#C8102E",
            "Q3" => "#B08A00",
            _ => "#455A64",
        });
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

        /// <summary>Read candidates.csv (columns: question, rank, label, score, reasons, lat, lon).</summary>
        public static List<SiteCandidate> LoadCandidates(string path)
        {
            var list = new List<SiteCandidate>();
            if (!File.Exists(path)) return list;
            var rows = ParseCsv(File.ReadAllText(path));
            if (rows.Count < 2) return list;
            var h = rows[0];
            int I(string name) => h.FindIndex(c => c.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
            int iq = I("question"), ir = I("rank"), il = I("label"), isc = I("score"), irs = I("reasons"),
                ila = I("lat"), ilo = I("lon");
            foreach (var r in rows.GetRange(1, rows.Count - 1))
            {
                string F(int i) => i >= 0 && i < r.Count ? r[i] : "";
                if (string.IsNullOrWhiteSpace(F(iq))) continue;
                list.Add(new SiteCandidate
                {
                    Question = F(iq),
                    Rank = int.TryParse(F(ir), out var rk) ? rk : 0,
                    Label = F(il),
                    Score = F(isc),
                    Reasons = F(irs),
                    Lat = double.TryParse(F(ila), NumberStyles.Float, CultureInfo.InvariantCulture, out var la) ? la : 0,
                    Lon = double.TryParse(F(ilo), NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) ? lo : 0,
                });
            }
            return list;
        }

        private static List<List<string>> ParseCsv(string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else cell.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
                else if (c == '\n')
                {
                    row.Add(cell.ToString().TrimEnd('\r'));
                    cell.Clear();
                    rows.Add(row);
                    row = new List<string>();
                }
                else cell.Append(c);
            }
            if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
            return rows;
        }

        /// <summary>Open a file, folder or web link with the system's default program.</summary>
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
