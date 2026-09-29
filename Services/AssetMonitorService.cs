using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace InfraDroneDesktop.Services;

/// <summary>
/// Data engine for the 🏛 Asset Monitor tab.
/// All values come from the Python scripts in ~/infradrone-desktop/tools/ownership, which read
/// official registers (NWB = National Road Database, BGT = Large-Scale Topography Register,
/// Kadaster boundaries) and EGMS (European Ground Motion Service). Nothing here estimates or invents data.
/// </summary>
public static class AssetMonitorService
{
    public static readonly string ToolsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "infradrone-desktop", "tools", "ownership");

    public static string PythonExe => Path.Combine(ToolsDir, ".venv", "bin", "python3");
    public static string OwnershipScript => Path.Combine(ToolsDir, "asset_ownership.py");
    public static string ScreeningScript => Path.Combine(ToolsDir, "egms_screen.py");
    public static string OwnershipOut => Path.Combine(ToolsDir, "ownership_out");
    public static string ScreeningOut => Path.Combine(ToolsDir, "screening_out");
    public static string TokenFile => Path.Combine(ToolsDir, "secrets", "token.jwt");
    public static string BridgesFile => Path.Combine(OwnershipOut, "bgt_bridges_by_owner.geojson");
    public static string RoadsFile => Path.Combine(OwnershipOut, "roads_by_owner.geojson");
    public static string RankedCsv => Path.Combine(ScreeningOut, "bridges_ranked.csv");
    public static string ManifestFile => Path.Combine(ScreeningOut, "audit_manifest.json");
    public static string TasksFile => Path.Combine(ToolsDir, "inspection_tasks.json");

    // ------------------------------------------------------------------ running the Python scripts
    public sealed record ScriptResult(int ExitCode, string FullOutput, string Summary, string ConsoleLog);

    public static async Task<ScriptResult> RunScriptAsync(string script, Action<string> onLine)
    {
        if (!File.Exists(PythonExe))
            throw new FileNotFoundException($"Python environment not found at {PythonExe}");
        if (!File.Exists(script))
            throw new FileNotFoundException($"Script not found: {script}");

        var consoleLog = Path.Combine(ToolsDir, Path.GetFileNameWithoutExtension(script) + "_console.log");
        var psi = new ProcessStartInfo
        {
            FileName = PythonExe,
            WorkingDirectory = ToolsDir,      // asset_ownership.py writes to ./ownership_out
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-u");            // unbuffered -> progress lines arrive live
        psi.ArgumentList.Add(script);

        var sb = new StringBuilder();
        var gate = new object();
        void Handle(string? data)
        {
            if (data == null) return;
            lock (gate) sb.AppendLine(data);
            // progress counters use '\r' - show only the newest segment
            var seg = data.Split('\r').LastOrDefault(s => s.Trim().Length > 0);
            if (seg != null) onLine(seg.Trim());
        }

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => Handle(e.Data);
        proc.ErrorDataReceived += (_, e) => Handle(e.Data);
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync();

        string full;
        lock (gate) full = sb.ToString();
        try { File.WriteAllText(consoleLog, full); } catch { /* log is a convenience only */ }
        return new ScriptResult(proc.ExitCode, full, ExtractSummary(full), consoleLog);
    }

    /// <summary>Everything from the "=====" summary header onwards (scripts print a short summary there).</summary>
    public static string ExtractSummary(string full)
    {
        var idx = full.LastIndexOf("=====", StringComparison.Ordinal);
        if (idx < 0) return full.Length > 1500 ? full[^1500..] : full;
        var lineStart = full.LastIndexOf('\n', idx);
        return full[(lineStart + 1)..].Trim();
    }

    // ------------------------------------------------------------------ register (ownership) statistics
    public sealed class OwnerStats
    {
        public int Total;
        public int Flagged;
        public DateTime? FileTime;
        public Dictionary<string, int> ByType = new();
        public Dictionary<string, Dictionary<string, int>> NamesByType = new();
    }

    public static OwnerStats ReadOwnerStats(string path)
    {
        var s = new OwnerStats();
        if (!File.Exists(path)) return s;
        s.FileTime = File.GetLastWriteTime(path);
        using var fs = File.OpenRead(path);
        using var doc = JsonDocument.Parse(fs);
        if (!doc.RootElement.TryGetProperty("features", out var feats)) return s;
        foreach (var f in feats.EnumerateArray())
        {
            if (!f.TryGetProperty("properties", out var p) || p.ValueKind != JsonValueKind.Object) continue;
            var type = Str(p, "owner_type") ?? "Unknown";
            var name = Str(p, "owner_name") ?? "(no name in register)";
            s.Total++;
            s.ByType[type] = s.ByType.GetValueOrDefault(type) + 1;
            if (!s.NamesByType.TryGetValue(type, out var names)) s.NamesByType[type] = names = new();
            names[name] = names.GetValueOrDefault(name) + 1;
            var status = Str(p, "code_status");
            if (status != null && status != "current") s.Flagged++;
        }
        return s;
    }

    private static string? Str(JsonElement p, string key)
    {
        if (!p.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
    }

    // ------------------------------------------------------------------ satellite screening results
    public sealed class ScreenedBridge
    {
        public string AssetId = "", OwnerType = "", OwnerName = "", OwnerCode = "", Screening = "";
        public double? UpLocal, UpGround, UpDiff, EastDiff, MaxAbs, Lon, Lat;
        public int? LocalN, GroundN;
        public int Parts = 1;                       // BGT deck parts combined into this row
        public List<string> PartIds = new();

        /// <summary>"Waterschap · W0646" - manager type is always visible, even when the register has only a code.</summary>
        public string ManagerLabel => OwnerLabel(OwnerType, OwnerName);
    }

    public static string OwnerLabel(string ownerType, string ownerName)
    {
        var shortType = ownerType.Split(' ')[0];    // "Waterschap (water board)" -> "Waterschap"
        if (string.IsNullOrWhiteSpace(ownerName)) return shortType;
        return ownerName.StartsWith(shortType, StringComparison.OrdinalIgnoreCase) ? ownerName : $"{shortType} · {ownerName}";
    }

    /// <summary>
    /// The BGT register splits one physical bridge into several deck parts. Parts with the same manager,
    /// the same screening result and the same satellite numbers within about 100 m are shown as ONE bridge.
    /// Only rows that are identical in every measured value are combined - nothing is averaged or changed.
    /// </summary>
    public static List<ScreenedBridge> GroupParts(IEnumerable<ScreenedBridge> rows)
    {
        string K(double? v, string f) => v.HasValue ? v.Value.ToString(f, CultureInfo.InvariantCulture) : "-";
        var result = new List<ScreenedBridge>();
        foreach (var g in rows.GroupBy(r => string.Join("|", r.OwnerCode, r.Screening,
                     K(r.Lat, "0.000"), K(r.Lon, "0.000"),
                     K(r.UpLocal, "0.000"), K(r.UpGround, "0.000"), K(r.EastDiff, "0.000"))))
        {
            var first = g.First();
            first.Parts = g.Count();
            first.PartIds = g.Select(x => x.AssetId).ToList();
            result.Add(first);
        }
        return result;
    }

    public static List<ScreenedBridge> ReadRanked()
    {
        var list = new List<ScreenedBridge>();
        if (!File.Exists(RankedCsv)) return list;
        using var reader = new StreamReader(RankedCsv);
        var headerLine = reader.ReadLine();
        if (headerLine == null) return list;
        var header = SplitCsv(headerLine);
        int Col(string name) => header.FindIndex(h => h == name);
        int cId = Col("asset_id"), cType = Col("owner_type"), cName = Col("owner_name"), cCode = Col("owner_code"),
            cScr = Col("screening"), cUl = Col("up_local"), cUg = Col("up_ground"), cUd = Col("up_diff"),
            cEd = Col("east_diff"), cMax = Col("max_abs"), cLon = Col("lon"), cLat = Col("lat"),
            cLn = Col("up_local_n"), cGn = Col("up_ground_n");
        string Get(List<string> r, int i) => i >= 0 && i < r.Count ? r[i] : "";
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var r = SplitCsv(line);
            list.Add(new ScreenedBridge
            {
                AssetId = Get(r, cId), OwnerType = Get(r, cType), OwnerName = Get(r, cName),
                OwnerCode = Get(r, cCode), Screening = Get(r, cScr),
                UpLocal = D(Get(r, cUl)), UpGround = D(Get(r, cUg)), UpDiff = D(Get(r, cUd)),
                EastDiff = D(Get(r, cEd)), MaxAbs = D(Get(r, cMax)), Lon = D(Get(r, cLon)), Lat = D(Get(r, cLat)),
                LocalN = D(Get(r, cLn)) is double ln ? (int)ln : null,
                GroundN = D(Get(r, cGn)) is double gn ? (int)gn : null
            });
        }
        return list;
    }

    public static JsonNode? ReadManifest()
    {
        if (!File.Exists(ManifestFile)) return null;
        try { return JsonNode.Parse(File.ReadAllText(ManifestFile)); } catch { return null; }
    }

    private static double? D(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) ? v : null;

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }

    // ------------------------------------------------------------------ inspection tasks + NEN 2767 condition
    public sealed class InspectionTask
    {
        public string AssetId { get; set; } = "";
        public string OwnerType { get; set; } = "";
        public string OwnerName { get; set; } = "";
        public string Screening { get; set; } = "";
        public double? MaxAbsMmPerYear { get; set; }
        public double? Lon { get; set; }
        public double? Lat { get; set; }
        public string EgmsRelease { get; set; } = "";
        public int Parts { get; set; } = 1;
        public List<string> PartIds { get; set; } = new();
        public string Aircraft { get; set; } = "Quadcopter (close-range bridge inspection)";
        public string Status { get; set; } = "Open";
        public int? Nen2767Score { get; set; }
        public string Inspector { get; set; } = "";
        public string Notes { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public string InspectedUtc { get; set; } = "";
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static List<InspectionTask> LoadTasks()
    {
        if (!File.Exists(TasksFile)) return new();
        try { return JsonSerializer.Deserialize<List<InspectionTask>>(File.ReadAllText(TasksFile), JsonOpts) ?? new(); }
        catch { return new(); }
    }

    public static void SaveTasks(List<InspectionTask> tasks)
    {
        Directory.CreateDirectory(ToolsDir);
        var tmp = TasksFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(tasks, JsonOpts));
        File.Move(tmp, TasksFile, overwrite: true);   // atomic replace: never a half-written task file
    }

    /// <summary>Adds a task for every Priority/Review bridge not already in the list. Existing tasks keep their status.</summary>
    public static (int added, int existing) CreateTasksFromScreening()
    {
        var tasks = LoadTasks();
        var known = new HashSet<string>(tasks.SelectMany(t => t.PartIds.Append(t.AssetId)));
        var release = ReadManifest()?["egms_release"]?.ToString() ?? "";
        int added = 0, existing = 0;
        foreach (var b in GroupParts(ReadRanked().Where(b => b.Screening is "Priority" or "Review")))
        {
            if (b.PartIds.Any(known.Contains)) { existing++; continue; }   // one task per physical bridge
            tasks.Add(new InspectionTask
            {
                AssetId = b.AssetId, OwnerType = b.OwnerType, OwnerName = b.OwnerName, Screening = b.Screening,
                MaxAbsMmPerYear = b.MaxAbs, Lon = b.Lon, Lat = b.Lat, EgmsRelease = release,
                Parts = b.Parts, PartIds = b.PartIds,
                CreatedUtc = DateTime.UtcNow.ToString("o")
            });
            foreach (var id in b.PartIds) known.Add(id);
            added++;
        }
        SaveTasks(tasks);
        return (added, existing);
    }
}
