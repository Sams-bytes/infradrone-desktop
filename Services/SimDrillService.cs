// Services/SimDrillService.cs
//
// Runs the failsafe drills in tools/sim_drills.py against the SIMULATOR, one at
// a time, and streams their output to the Simulation tab.
//
// Why a Python helper instead of C#: the drills inject simulated failures
// (RC loss, battery drain, fence breach, fake ADS-B traffic) on the simulator's
// own TCP port 5762, using pymavlink, which MAVProxy already installed. The
// script refuses to run against anything that is not the simulator.
//
// Every simulator setting a drill changes is saved first and restored at the
// end. If a drill is stopped, Stop() runs 'sim_drills.py restore' so nothing is
// left changed.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace InfraDroneDesktop.Services
{
    public sealed class SimDrillService
    {
        public static SimDrillService Instance { get; } = new SimDrillService();
        private SimDrillService() { }

        private Process? _proc;
        public bool IsRunning => _proc != null && !_proc.HasExited;

        /// <summary>One line of drill output (raised on a background thread).</summary>
        public event Action<string>? Output;
        /// <summary>Drill finished: the final RESULT line, or an error message.</summary>
        public event Action<string>? Finished;

        public string WorkDir => SimulatorService.Instance.WorkDir;

        /// <summary>Starts a drill: setup, traffic, rcloss, battery, fence (args e.g. "traffic 2").</summary>
        public (bool Ok, string Message) Start(string args)
        {
            if (IsRunning) return (false, "A drill is already running.");
            if (!SimulatorService.Instance.IsRunning)
                return (false, "Start the simulator first.");

            var script = FindScript();
            if (script == null)
                return (false, "tools/sim_drills.py not found next to the app or in ~/infradrone-desktop/tools.");
            var python = FindPython();

            Directory.CreateDirectory(WorkDir);
            var psi = new ProcessStartInfo
            {
                FileName = python,
                // -u: unbuffered, so lines arrive live
                Arguments = $"-u \"{script}\" {args}",
                WorkingDirectory = WorkDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };
            string lastResult = "";
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                if (e.Data.StartsWith("RESULT:")) lastResult = e.Data;
                Output?.Invoke(e.Data);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                // pymavlink prints harmless reconnect noise; show real errors.
                if (e.Data.Contains("sleeping")) return;
                Output?.Invoke("! " + e.Data);
                if (e.Data.Contains("No module named 'pymavlink'"))
                    lastResult = "RESULT: NOT STARTED - pymavlink is missing for " + python +
                                 ". In a terminal run: python3 -m pip install --user pymavlink";
            };
            p.Exited += (_, _) =>
            {
                // WaitForExit() with no timeout also waits until the async output
                // events have all been delivered, so the RESULT line isn't lost.
                try { p.WaitForExit(); } catch { }
                Finished?.Invoke(string.IsNullOrEmpty(lastResult)
                    ? $"RESULT: FAIL - drill stopped (exit {SafeExit(p)}). Details in {Path.Combine(WorkDir, "sim_drills.log")}"
                    : lastResult);
            };
            try
            {
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                _proc = p;
                return (true, $"Running: {args}");
            }
            catch (Exception ex)
            {
                return (false, $"Could not start {python}: {ex.Message}");
            }
        }

        /// <summary>Stops a running drill and restores the simulator settings it changed.</summary>
        public async Task StopAsync()
        {
            var p = _proc;
            if (p != null && !p.HasExited)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                try { await p.WaitForExitAsync(); } catch { }
            }
            _proc = null;

            var script = FindScript();
            if (script == null || !SimulatorService.Instance.IsRunning) return;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = FindPython(),
                    Arguments = $"-u \"{script}\" restore",
                    WorkingDirectory = WorkDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var r = Process.Start(psi);
                if (r == null) return;
                var text = await r.StandardOutput.ReadToEndAsync();
                await r.WaitForExitAsync();
                foreach (var line in text.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) Output?.Invoke(line.TrimEnd());
            }
            catch (Exception ex)
            {
                Output?.Invoke("! restore failed: " + ex.Message);
            }
        }

        // ---- helpers -------------------------------------------------------
        private static string? FindScript()
        {
            // 1) Walk up from the running app (bin/Debug/net8.0 -> project root).
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tools", "sim_drills.py");
                if (File.Exists(candidate)) return candidate;
            }
            // 2) The usual checkout location.
            var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                    "infradrone-desktop", "tools", "sim_drills.py");
            return File.Exists(home) ? home : null;
        }

        /// <summary>
        /// The Python that has pymavlink: the one MAVProxy itself runs with (first
        /// line of mavproxy.py). Override with INFRADRONE_PYTHON.
        /// </summary>
        private static string FindPython()
        {
            var env = Environment.GetEnvironmentVariable("INFRADRONE_PYTHON");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] mavproxyCandidates =
            {
                Path.Combine(home, "venv-ardupilot", "bin", "mavproxy.py"),
                Path.Combine(home, ".local", "bin", "mavproxy.py"),
                "/usr/local/bin/mavproxy.py",
                "/usr/bin/mavproxy.py",
            };
            foreach (var mp in mavproxyCandidates)
            {
                try
                {
                    if (!File.Exists(mp)) continue;
                    using var sr = new StreamReader(mp);
                    var first = sr.ReadLine() ?? "";
                    if (!first.StartsWith("#!")) continue;
                    var interp = first.Substring(2).Trim();
                    // "#!/usr/bin/env python3" -> just "python3"
                    if (interp.StartsWith("/usr/bin/env ")) interp = interp.Substring("/usr/bin/env ".Length).Trim();
                    var exe = interp.Split(' ')[0];
                    if (exe.StartsWith("/") ? File.Exists(exe) : true) return exe;
                }
                catch { }
            }
            return "python3";
        }

        private static string SafeExit(Process p)
        {
            try { return p.ExitCode.ToString(); } catch { return "?"; }
        }
    }
}
