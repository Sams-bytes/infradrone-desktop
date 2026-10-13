// Services/SimulatorService.cs
//
// One-click ArduPilot SITL (Software-In-The-Loop) simulator for InfraDrone Desktop.
// Replaces the four terminals used during the first manual SITL test:
//
//   1. arduplane SITL binary   (simulated Cube Orange running ArduPlane QuadPlane)
//   2. mavproxy.py             (MAVLink relay; also feeds simulated RC input)
//   3. socat                   -> replaced by the built-in UDP relay below
//   4. the app itself          -> already running; this service only starts 1-3
//
// DATA FLOW (ports defined in LinkPorts.cs)
//   arduplane  --tcp:5760-->  mavproxy  --udp-->  127.0.0.1:14591  [relay socket A]
//                                        --udp-->  127.0.0.1:14552  (scripts, e.g. verify_mission.py)
//   relay socket B (bound 127.0.0.1:14455)  <-->  app on 127.0.0.1:14581
//
// WHY THE RELAY EXISTS
//   In simulator mode AsvMavLinkService connects with LinkPorts.SimulatorConnection
//   ("udp://127.0.0.1:14581?rhost=127.0.0.1&rport=14455"), so it only exchanges
//   packets with 127.0.0.1:14455. MAVProxy sends from a random port, so the app
//   would ignore it. The relay talks to the app FROM port 14455.
//
// PORTS
//   The simulator has its OWN ports, separate from the real drone's (14571/14445,
//   used by the DAMbv "mavproxy-router" systemd service). So the router can keep
//   running and both never collide. If a simulator port is busy anyway, Start()
//   names the program holding it.
//
// SAFETY
//   Simulation only. Nothing here touches serial ports or real radios.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace InfraDroneDesktop.Services
{
    public sealed class SimulatorService
    {
        public static SimulatorService Instance { get; } = new SimulatorService();
        private SimulatorService() { }

        // ---- Configuration -------------------------------------------------
        public string ArduPlaneBinary { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "ardupilot", "build", "sitl", "bin", "arduplane");

        public string WorkDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".infradrone", "sitl");

        public string ArduPlaneLog => Path.Combine(WorkDir, "arduplane.log");
        public string MavProxyLog => Path.Combine(WorkDir, "mavproxy.log");

        private const int AppPort = LinkPorts.SimAppListen;       // AsvMavLinkService listens here (sim mode)
        private const int AppRemotePort = LinkPorts.SimAppSend;   // AsvMavLinkService sends here (relay socket B)
        private const int RelayInPort = LinkPorts.SimRelayIn;     // MAVProxy -> relay socket A
        private const int ScriptPort = LinkPorts.SimScript;       // extra MAVProxy output for scripts
        private const int SitlTcpPort = LinkPorts.SimTcp;         // ArduPlane SERIAL0

        // ---- State ---------------------------------------------------------
        private Process? _arduplane;
        private Process? _mavproxy;
        private UdpClient? _relayA;   // talks to MAVProxy
        private UdpClient? _relayB;   // talks to the app, from port 14455
        private IPEndPoint? _mavproxyEndpoint;
        private CancellationTokenSource? _relayCts;
        private StreamWriter? _arduLogWriter;
        private StreamWriter? _mavLogWriter;
        private bool _stopping;

        public bool IsRunning { get; private set; }

        /// <summary>
        /// Link-degradation drill: percentage (0-100) of vehicle packets the relay
        /// drops on their way to the app. Only affects what THIS APP sees; the
        /// simulated autopilot still hears MAVProxy normally.
        /// </summary>
        public volatile int DropPercent;
        public long PacketsToApp;
        public long PacketsFromApp;
        public bool ArduPlaneAlive => _arduplane != null && !_arduplane.HasExited;
        public bool MavProxyAlive => _mavproxy != null && !_mavproxy.HasExited;
        public bool RelayAlive => _relayA != null && _relayB != null && _mavproxyEndpoint != null;

        /// <summary>Human-readable status line for the UI. Raised from background threads.</summary>
        public event Action<string>? StatusMessage;

        // ---- Start ---------------------------------------------------------
        public async Task<(bool Ok, string Message)> StartAsync(double lat, double lon, double altAmslM, double headingDeg)
        {
            if (IsRunning) return (true, "Simulator is already running.");
            _stopping = false;

            if (!File.Exists(ArduPlaneBinary))
                return (false,
                    $"ArduPlane simulator not found at {ArduPlaneBinary}. " +
                    "Build it once with: cd ~/ardupilot && ./waf configure --board sitl && ./waf plane");

            // Port checks first, so nothing is half-started on failure.
            // These are the simulator's own ports (LinkPorts.cs), separate from the
            // real drone's, so the mavproxy-router service does NOT need stopping.
            if (!TcpPortFree(SitlTcpPort))
                return (false, "Port 5760 is in use — an old simulator is probably still running " +
                               "(e.g. after the app crashed). In a terminal run: pkill -f arduplane; pkill -f mavproxy  " +
                               "— then click Start again.");
            if (!UdpPortFree(AppRemotePort))
                return (false, LinkPorts.BusyMessage(AppRemotePort));
            if (!UdpPortFree(RelayInPort))
                return (false, LinkPorts.BusyMessage(RelayInPort));

            Directory.CreateDirectory(WorkDir);
            PacketsToApp = 0; PacketsFromApp = 0; _mavproxyEndpoint = null;

            var inv = CultureInfo.InvariantCulture;
            var home = string.Format(inv, "{0:F7},{1:F7},{2:F1},{3:F0}", lat, lon, altAmslM, headingDeg);

            try
            {
                // 1. Simulated autopilot
                _arduLogWriter = new StreamWriter(ArduPlaneLog, append: false) { AutoFlush = true };
                _arduplane = StartLogged(ArduPlaneBinary,
                    $"--model quadplane --speedup 1 --home {home} --sim-address=127.0.0.1 -I0",
                    _arduLogWriter, "ArduPlane");
                Report($"ArduPlane simulator started at home {home}.");

                // Give it a moment to open TCP 5760 (MAVProxy also retries).
                await Task.Delay(2000);
                if (!ArduPlaneAlive)
                {
                    Stop();
                    return (false, $"ArduPlane stopped right after starting. See {ArduPlaneLog}");
                }

                // 2. Relay must exist before MAVProxy starts sending to it.
                StartRelay();

                // 3. MAVProxy (feeds simulated RC input and relays MAVLink).
                _mavLogWriter = new StreamWriter(MavProxyLog, append: false) { AutoFlush = true };
                var mavArgs =
                    $"--master tcp:127.0.0.1:{SitlTcpPort} --sitl 127.0.0.1:5501 " +
                    $"--out udp:127.0.0.1:{RelayInPort} --out udp:127.0.0.1:{ScriptPort} " +
                    "--daemon --non-interactive --retries 10";
                var mavExe = FindMavProxy();
                _mavproxy = mavExe != null
                    ? StartLogged(mavExe, mavArgs, _mavLogWriter, "MAVProxy")
                    // Fallback: interactive login shell reads ~/.bashrc, where the
                    // ArduPilot installer puts its Python environment on PATH.
                    : StartLogged("/bin/bash", $"-lic \"exec mavproxy.py {mavArgs}\"", _mavLogWriter, "MAVProxy");
                Report("MAVProxy started" + (mavExe != null ? $" ({mavExe})." : " (via shell PATH)."));

                // Wait up to 20 s for the first packet to reach the relay.
                for (int i = 0; i < 40 && _mavproxyEndpoint == null; i++)
                {
                    await Task.Delay(500);
                    if (!MavProxyAlive)
                    {
                        var code = _mavproxy.ExitCode;
                        Stop();
                        return (false, code == 127
                            ? "mavproxy.py was not found. Install it with: pip install --user MAVProxy"
                            : $"MAVProxy stopped (exit {code}). See {MavProxyLog}");
                    }
                }
                if (_mavproxyEndpoint == null)
                {
                    Stop();
                    return (false, $"No data from MAVProxy after 20 s. See {MavProxyLog} and {ArduPlaneLog}");
                }

                IsRunning = true;
                return (true, "Simulator running. Connect, then open Mission Planner — the drone is waiting on the ground.");
            }
            catch (Exception ex)
            {
                Stop();
                return (false, "Could not start the simulator: " + ex.Message);
            }
        }

        // ---- Stop ----------------------------------------------------------
        public void Stop()
        {
            _stopping = true;
            _relayCts?.Cancel();
            try { _relayA?.Dispose(); } catch { }
            try { _relayB?.Dispose(); } catch { }
            _relayA = null; _relayB = null; _mavproxyEndpoint = null;
            DropPercent = 0;

            Kill(_mavproxy); _mavproxy = null;
            Kill(_arduplane); _arduplane = null;

            try { _arduLogWriter?.Dispose(); } catch { }
            try { _mavLogWriter?.Dispose(); } catch { }
            _arduLogWriter = null; _mavLogWriter = null;

            if (IsRunning) Report("Simulator stopped.");
            IsRunning = false;
        }

        // ---- Relay (replaces socat) ----------------------------------------
        private void StartRelay()
        {
            _relayCts = new CancellationTokenSource();
            var ct = _relayCts.Token;
            _relayA = new UdpClient(new IPEndPoint(IPAddress.Loopback, RelayInPort));
            _relayB = new UdpClient(new IPEndPoint(IPAddress.Loopback, AppRemotePort));
            var appEp = new IPEndPoint(IPAddress.Loopback, AppPort);

            // MAVProxy -> app
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var r = await _relayA!.ReceiveAsync(ct);
                        _mavproxyEndpoint = r.RemoteEndPoint;
                        var drop = DropPercent;
                        if (drop > 0 && Random.Shared.Next(100) < drop) continue; // drill: lose this packet
                        await _relayB!.SendAsync(r.Buffer, r.Buffer.Length, appEp);
                        Interlocked.Increment(ref PacketsToApp);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (SocketException) { /* app not listening yet (ICMP refused) - keep going */ }
                }
            }, ct);

            // app -> MAVProxy
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var r = await _relayB!.ReceiveAsync(ct);
                        var target = _mavproxyEndpoint;
                        if (target == null) continue;
                        await _relayA!.SendAsync(r.Buffer, r.Buffer.Length, target);
                        Interlocked.Increment(ref PacketsFromApp);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (SocketException) { /* transient - keep going */ }
                }
            }, ct);
        }

        // ---- Helpers -------------------------------------------------------
        /// <summary>
        /// Finds mavproxy.py in the usual install locations. Override with the
        /// INFRADRONE_MAVPROXY environment variable. Null = fall back to shell PATH.
        /// </summary>
        private static string? FindMavProxy()
        {
            var env = Environment.GetEnvironmentVariable("INFRADRONE_MAVPROXY");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] candidates =
            {
                Path.Combine(home, "venv-ardupilot", "bin", "mavproxy.py"),
                Path.Combine(home, ".local", "bin", "mavproxy.py"),
                "/usr/local/bin/mavproxy.py",
                "/usr/bin/mavproxy.py",
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        private Process StartLogged(string file, string args, StreamWriter log, string name)
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                WorkingDirectory = WorkDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,   // keeps MAVProxy from reading our terminal
                CreateNoWindow = true
            };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) SafeWrite(log, e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) SafeWrite(log, e.Data); };
            p.Exited += (_, _) =>
            {
                if (!_stopping)
                    Report($"{name} stopped unexpectedly (exit {SafeExit(p)}). Click Stop, then Start. " +
                           $"Details in {WorkDir}");
            };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        private static void SafeWrite(StreamWriter w, string line)
        {
            try { lock (w) w.WriteLine(line); } catch { }
        }

        private static string SafeExit(Process p)
        {
            try { return p.ExitCode.ToString(CultureInfo.InvariantCulture); } catch { return "?"; }
        }

        private static void Kill(Process? p)
        {
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { p.WaitForExit(3000); } catch { }
            try { p.Dispose(); } catch { }
        }

        private static bool UdpPortFree(int port)
        {
            try { using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, port)); return true; }
            catch (SocketException) { return false; }
        }

        private static bool TcpPortFree(int port)
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            try { l.Start(); return true; }
            catch (SocketException) { return false; }
            finally { try { l.Stop(); } catch { } }
        }

        private void Report(string msg)
        {
            Console.WriteLine("[Simulator] " + msg);
            StatusMessage?.Invoke(msg);
        }
    }
}
