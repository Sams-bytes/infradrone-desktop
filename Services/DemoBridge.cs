// Services/DemoBridge.cs
//
// Link between the desktop "Suriname Demo" tab and the demo display page in the
// browser (wwwroot/demo). The tab queues presenter commands (next, prev, auto,
// language, ...); the page polls /demo/cmd and reports its state to /demo/status
// once a second. Both endpoints are served by Cesium3DViewService.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace InfraDroneDesktop.Services
{
    public static class DemoBridge
    {
        private static readonly object Lock = new();
        private static readonly List<(int Id, string Cmd, string? Arg)> Cmds = new();
        private static int _nextId = 1;
        private static string _statusJson = "{}";
        private static DateTime _statusUtc = DateTime.MinValue;

        /// <summary>Display settings chosen in the tab; read by /demo/config.json.</summary>
        public static DemoSettings Settings { get; } = new();

        public static void Send(string cmd, string? arg = null)
        {
            lock (Lock)
            {
                Cmds.Add((_nextId++, cmd, arg));
                if (Cmds.Count > 200) Cmds.RemoveRange(0, Cmds.Count - 200);
            }
        }

        /// <summary>JSON {last, cmds:[{id,cmd,arg}]}. after &lt; 0 returns only 'last' (no replay).</summary>
        public static string CommandsAfterJson(int after)
        {
            lock (Lock)
            {
                var last = Cmds.Count > 0 ? Cmds[^1].Id : 0;
                var list = after < 0
                    ? new List<object>()
                    : Cmds.Where(c => c.Id > after).Select(c => (object)new { id = c.Id, cmd = c.Cmd, arg = c.Arg }).ToList();
                return JsonSerializer.Serialize(new { last, cmds = list });
            }
        }

        public static void SetStatus(string json)
        {
            try { using var _ = JsonDocument.Parse(json); } catch { return; } // ignore malformed
            lock (Lock) { _statusJson = json; _statusUtc = DateTime.UtcNow; }
        }

        /// <summary>Latest status from the display, or null if none in the last 5 seconds.</summary>
        public static JsonElement? Status()
        {
            string json; DateTime t;
            lock (Lock) { json = _statusJson; t = _statusUtc; }
            if ((DateTime.UtcNow - t).TotalSeconds > 5) return null;
            try { return JsonDocument.Parse(json).RootElement.Clone(); } catch { return null; }
        }
    }

    public sealed class DemoSettings
    {
        /// <summary>"google" (photorealistic, internet), "offline" (local package), "online" (satellite via internet).</summary>
        public string Map { get; set; } = "google";
        public string Lang { get; set; } = "en";
        public string Scenario { get; set; } = "albina";
        public int? EnduranceMin { get; set; }
    }
}
