// Services/LinkPorts.cs
//
// The ONE place that defines which local UDP ports InfraDrone Desktop uses to
// talk MAVLink to the Cube Orange. Every port here is on 127.0.0.1 (the laptop
// talking to itself), so they are the same on every Wi-Fi network, with or
// without internet.
//
// Two separate, fixed port pairs so the real drone and the simulator can never
// collide - the mavproxy-router service can stay running all the time:
//
//   REAL DRONE   app listens 14571, app sends to 14445  (mavproxy-router service)
//   SIMULATOR    app listens 14581, app sends to 14455  (Simulation tab's relay)
//                relay receives MAVProxy on 14591; scripts still use 14552
//
// Do not reuse 14445 / 14571 / 14561 for anything else: 14445 and 14571 belong
// to the router service, 14561 was the old manual socat relay.

using System;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace InfraDroneDesktop.Services
{
    public static class LinkPorts
    {
        // ---- Real drone (via the mavproxy-router systemd service) ----------
        public const int RealAppListen = 14571;
        public const int RealAppSend = 14445;
        public static readonly string RealConnection =
            $"udp://127.0.0.1:{RealAppListen}?rhost=127.0.0.1&rport={RealAppSend}";

        // ---- Simulator (Simulation tab) -------------------------------------
        public const int SimAppListen = 14581;
        public const int SimAppSend = 14455;   // relay socket B binds this
        public const int SimRelayIn = 14591;   // MAVProxy -> relay socket A
        public const int SimScript = 14552;    // extra MAVProxy output for scripts (verify_mission.py)
        public const int SimTcp = 5760;        // ArduPlane SITL SERIAL0
        public static readonly string SimulatorConnection =
            $"udp://127.0.0.1:{SimAppListen}?rhost=127.0.0.1&rport={SimAppSend}";

        /// <summary>
        /// Best-effort name of the program holding a local port, e.g. "python3 (pid 884)".
        /// Returns null if it can't be found (programs started by root - like a
        /// systemd service - are only visible with sudo).
        /// </summary>
        public static string? WhoUses(int port, bool udp = true)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ss",
                    Arguments = (udp ? "-ulpnH" : "-tlpnH") + $" sport = :{port}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                var m = Regex.Match(output, "users:\\(\\(\"([^\"]+)\",pid=(\\d+)");
                return m.Success ? $"{m.Groups[1].Value} (pid {m.Groups[2].Value})" : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Plain-language message for a busy port, naming the program when possible.</summary>
        public static string BusyMessage(int port, bool udp = true)
        {
            var who = WhoUses(port, udp);
            return who != null
                ? $"Port {port} is already used by {who}. Close that program, then click Start again."
                : $"Port {port} is already used by another program. To see which one, run in a terminal: " +
                  $"sudo ss -{(udp ? "u" : "t")}lpn | grep {port}";
        }
    }
}
