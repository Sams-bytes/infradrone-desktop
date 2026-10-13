using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfraDroneDesktop.Services
{
    /// <summary>
    /// Pre-flight check of the failsafe settings stored ON the autopilot
    /// (ArduPlane / QuadPlane, i.e. the Cube Orange in the Loong 2160).
    ///
    /// Why this exists: for BVLOS (Beyond Visual Line Of Sight) the aircraft
    /// itself must react to link loss, low battery and fence breaches. This app
    /// only raises alerts. So before take-off we read the real parameters back
    /// from the vehicle and grade them GO / CAUTION / NO-GO.
    ///
    /// Parameter names and value meanings were taken from ArduPilot's own
    /// source (ArduPlane/Parameters.cpp, ArduPlane/quadplane.cpp,
    /// libraries/AP_BattMonitor/AP_BattMonitor_Params.cpp,
    /// libraries/AC_Fence/AC_Fence.cpp, libraries/AP_Arming/AP_Arming.cpp,
    /// libraries/GCS_MAVLink/GCS.cpp) - not guessed. Value meanings assume
    /// ArduPlane 4.5 or newer (the version where Plane moved to the shared
    /// AC_Fence library; older FENCE_ACTION numbers mean different things).
    /// Where a parameter was renamed (ARMING_CHECK -> ARMING_SKIPCHK,
    /// SYSID_MYGCS -> MAV_GCS_SYSID) both names are tried.
    ///
    /// This class only READS. It never writes a parameter, arms, or changes mode.
    /// It has no UI and no MAVLink dependency, so the rules are easy to review.
    /// </summary>
    public class FailsafeParamCheck
    {
        public enum Status { Go, Caution, NoGo, Info }

        public record Result(string Area, string Label, Status Status, string Detail, string Fix = "");

        public record Report(IReadOnlyList<Result> Results,
                             IReadOnlyDictionary<string, float> Values,
                             IReadOnlyList<string> Unreadable)
        {
            public int NoGoCount => Results.Count(r => r.Status == Status.NoGo);
            public int CautionCount => Results.Count(r => r.Status == Status.Caution);
            public Status Overall => NoGoCount > 0 ? Status.NoGo
                                   : CautionCount > 0 ? Status.Caution
                                   : Status.Go;
            /// <summary>True when no row in the given area is NO-GO and the area was actually checked.</summary>
            public bool AreaPasses(string area) =>
                Results.Any(r => r.Area == area) &&
                Results.Where(r => r.Area == area).All(r => r.Status != Status.NoGo);
        }

        public const string AreaRead = "READ";
        public const string AreaLink = "LINK LOSS";
        public const string AreaBattery = "BATTERY";
        public const string AreaFence = "GEOFENCE";
        public const string AreaReturn = "RETURN & LANDING";
        public const string AreaArming = "ARMING";

        private readonly Func<string, CancellationToken, Task<float?>> _readParam;
        private readonly Func<CancellationToken, Task<int>>? _readFencePointCount;
        private readonly TimeSpan _perParamTimeout;
        private readonly Dictionary<string, float> _values = new();
        private readonly List<string> _unreadable = new();

        /// <param name="readParam">Reads one parameter by name; returns null if it doesn't exist or can't be read.</param>
        /// <param name="readFencePointCount">Optional: downloads the vehicle's fence store and returns its point count.</param>
        public FailsafeParamCheck(Func<string, CancellationToken, Task<float?>> readParam,
                                  TimeSpan? perParamTimeout = null,
                                  Func<CancellationToken, Task<int>>? readFencePointCount = null)
        {
            _readParam = readParam;
            _readFencePointCount = readFencePointCount;
            _perParamTimeout = perParamTimeout ?? TimeSpan.FromSeconds(4);
        }

        /// <summary>Raised before each parameter read: (name, index, total) - for a progress line.</summary>
        public event Action<string, int, int>? Progress;

        // Everything we may need. Alternates (renamed parameters) are read only if
        // the first name is missing.
        private static readonly string[] Wanted =
        {
            "FS_GCS_ENABL", "THR_FAILSAFE", "FS_LONG_ACTN", "FS_LONG_TIMEOUT",
            "BATT_MONITOR", "BATT_FS_LOW_ACT", "BATT_FS_CRT_ACT",
            "BATT_LOW_VOLT", "BATT_CRT_VOLT", "BATT_LOW_MAH", "BATT_CRT_MAH",
            "FENCE_ENABLE", "FENCE_AUTOENABLE", "FENCE_TYPE", "FENCE_ACTION", "FENCE_ALT_MAX",
            "RTL_ALTITUDE", "RTL_AUTOLAND", "Q_ENABLE", "Q_RTL_MODE", "Q_OPTIONS",
            "ARMING_SKIPCHK", "MAV_GCS_SYSID",
        };

        private static readonly Dictionary<string, string> Fallbacks = new()
        {
            { "ARMING_SKIPCHK", "ARMING_CHECK" },   // renamed in newer firmware
            { "MAV_GCS_SYSID", "SYSID_MYGCS" },     // renamed in newer firmware
        };

        public async Task<Report> RunAsync(CancellationToken ct = default)
        {
            _values.Clear();
            _unreadable.Clear();

            int i = 0;
            foreach (var name in Wanted)
            {
                ct.ThrowIfCancellationRequested();
                Progress?.Invoke(name, ++i, Wanted.Length);
                var v = await ReadOne(name, ct);
                if (v == null && Fallbacks.TryGetValue(name, out var alt))
                {
                    Progress?.Invoke(alt, i, Wanted.Length);
                    v = await ReadOne(alt, ct);
                    if (v != null) { _values[alt] = v.Value; continue; }
                }
                if (v != null) _values[name] = v.Value;
                else _unreadable.Add(name);
            }

            // If polygon fences are switched on, check one is actually loaded:
            // with none, ArduPilot refuses to arm ("Polygon fence(s) invalid").
            int? fencePoints = null;
            bool polygonOn = _values.TryGetValue("FENCE_TYPE", out var ft) && (((int)Math.Round(ft)) & 4) != 0;
            if (polygonOn && _readFencePointCount != null)
            {
                Progress?.Invoke("fence points", Wanted.Length, Wanted.Length);
                try { fencePoints = await _readFencePointCount(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { fencePoints = null; }
            }

            return Evaluate(_values, _unreadable, fencePoints);
        }

        private async Task<float?> ReadOne(string name, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_perParamTimeout);
            try
            {
                // Belt and braces: even if the reader ignores the token, don't hang.
                var readTask = _readParam(name, cts.Token);
                var done = await Task.WhenAny(readTask, Task.Delay(_perParamTimeout + TimeSpan.FromSeconds(1), ct));
                if (done != readTask) return null;
                var v = await readTask;
                if (v == null || float.IsNaN(v.Value)) return null;
                return v;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------
        // Rules. Static and pure so they can be read top to bottom.
        // ------------------------------------------------------------------
        /// <param name="fencePoints">Polygon points stored on the vehicle, or null if not read.</param>
        public static Report Evaluate(IReadOnlyDictionary<string, float> p, IReadOnlyList<string> unreadable,
                                      int? fencePoints = null)
        {
            var r = new List<Result>();
            int? I(string n) => p.TryGetValue(n, out var v) ? (int)Math.Round(v) : null;
            float? F(string n) => p.TryGetValue(n, out var v) ? v : null;

            // Nothing at all came back: the link can't read parameters.
            if (p.Count == 0)
            {
                r.Add(new Result(AreaRead, "Read autopilot parameters", Status.NoGo,
                    "No parameters could be read from the vehicle.",
                    "Check the Cube Orange is connected, and that nothing else (e.g. the mavproxy-router service) is holding the link. Then try again."));
                return new Report(r, p, unreadable);
            }

            bool isQuadPlane = (I("Q_ENABLE") ?? 0) >= 1;

            // ---------------- LINK LOSS ----------------
            var gcsFs = I("FS_GCS_ENABL");
            switch (gcsFs)
            {
                case null:
                    r.Add(Missing(AreaLink, "Telemetry-link failsafe (FS_GCS_ENABL)")); break;
                case 0:
                    r.Add(new Result(AreaLink, "Telemetry-link failsafe (FS_GCS_ENABL)", Status.NoGo,
                        "0 = Disabled. If the ground-station link drops, the aircraft does nothing.",
                        "Set FS_GCS_ENABL = 1 (Heartbeat).")); break;
                case 1:
                case 2:
                    r.Add(new Result(AreaLink, "Telemetry-link failsafe (FS_GCS_ENABL)", Status.Go,
                        gcsFs == 1 ? "1 = Heartbeat: triggers in every flight mode." : "2 = Heartbeat + radio RSSI: triggers in every flight mode.")); break;
                case 3:
                    r.Add(new Result(AreaLink, "Telemetry-link failsafe (FS_GCS_ENABL)", Status.Caution,
                        "3 = Heartbeat, but ONLY while in AUTO. In GUIDED, LOITER or VTOL modes a lost link is ignored.",
                        "Set FS_GCS_ENABL = 1 so it triggers in every mode.")); break;
                default:
                    r.Add(Unknown(AreaLink, "Telemetry-link failsafe (FS_GCS_ENABL)", gcsFs.Value)); break;
            }

            var thrFs = I("THR_FAILSAFE");
            switch (thrFs)
            {
                case null: r.Add(Missing(AreaLink, "RC (radio control) failsafe (THR_FAILSAFE)")); break;
                case 0:
                    r.Add(new Result(AreaLink, "RC (radio control) failsafe (THR_FAILSAFE)", Status.NoGo,
                        "0 = Disabled. Loss of the RC transmitter is not detected.",
                        "Set THR_FAILSAFE = 1 and check THR_FS_VALUE against your receiver's failsafe output.")); break;
                case 1:
                    r.Add(new Result(AreaLink, "RC (radio control) failsafe (THR_FAILSAFE)", Status.Go, "1 = Enabled.")); break;
                case 2:
                    r.Add(new Result(AreaLink, "RC (radio control) failsafe (THR_FAILSAFE)", Status.Caution,
                        "2 = Detects RC loss but takes NO action.", "Set THR_FAILSAFE = 1.")); break;
                default: r.Add(Unknown(AreaLink, "RC (radio control) failsafe (THR_FAILSAFE)", thrFs.Value)); break;
            }

            var longAct = I("FS_LONG_ACTN");
            switch (longAct)
            {
                case null: r.Add(Missing(AreaLink, "Action after link loss (FS_LONG_ACTN)")); break;
                case 1:
                    r.Add(new Result(AreaLink, "Action after link loss (FS_LONG_ACTN)", Status.Go, "1 = Return To Launch (RTL).")); break;
                case 5:
                    r.Add(new Result(AreaLink, "Action after link loss (FS_LONG_ACTN)", Status.Go, "5 = AUTOLAND if possible, otherwise RTL.")); break;
                case 0:
                    r.Add(new Result(AreaLink, "Action after link loss (FS_LONG_ACTN)", Status.Caution,
                        "0 = Continue. In AUTO the mission keeps flying with no link. Only acceptable if the mission ends in RTL (InfraDrone's uploader adds this) AND a geofence is enforced.",
                        "For BVLOS, set FS_LONG_ACTN = 1 (RTL).")); break;
                case 2:
                    r.Add(new Result(AreaLink, "Action after link loss (FS_LONG_ACTN)", Status.NoGo,
                        "2 = Glide: switches to FBWA with the motor off. The aircraft comes down wherever it is.",
                        "Set FS_LONG_ACTN = 1 (RTL).")); break;
                case 3:
                    r.Add(new Result(AreaLink, "Action after link loss (FS_LONG_ACTN)", Status.Caution,
                        "3 = Deploy parachute. Only valid if a parachute is fitted and configured.",
                        "Otherwise set FS_LONG_ACTN = 1 (RTL).")); break;
                case 4:
                    r.Add(new Result(AreaLink, "Action after link loss (FS_LONG_ACTN)", Status.Caution,
                        "4 = Switch to AUTO and continue the mission.",
                        "For BVLOS, set FS_LONG_ACTN = 1 (RTL).")); break;
                default: r.Add(Unknown(AreaLink, "Action after link loss (FS_LONG_ACTN)", longAct.Value)); break;
            }

            var longTo = F("FS_LONG_TIMEOUT");
            if (longTo == null) r.Add(Missing(AreaLink, "Link-loss delay (FS_LONG_TIMEOUT)"));
            else if (longTo.Value > 20)
                r.Add(new Result(AreaLink, "Link-loss delay (FS_LONG_TIMEOUT)", Status.Caution,
                    $"{longTo.Value:0.#} s before the failsafe acts. The aircraft can travel far in that time (about {longTo.Value * 20:0} m at 20 m/s).",
                    "Typical value: 5 to 20 s."));
            else
                r.Add(new Result(AreaLink, "Link-loss delay (FS_LONG_TIMEOUT)", Status.Go, $"{longTo.Value:0.#} s."));

            if (isQuadPlane)
            {
                var qopt = I("Q_OPTIONS");
                if (qopt == null) r.Add(Missing(AreaLink, "Link loss while hovering (Q_OPTIONS)"));
                else
                {
                    bool fsQrtl = (qopt.Value & (1 << 5)) != 0;
                    bool fsRtl = (qopt.Value & (1 << 20)) != 0;
                    if (fsRtl || fsQrtl)
                        r.Add(new Result(AreaLink, "Link loss while hovering (Q_OPTIONS)", Status.Go,
                            fsRtl ? "Bit 20 set: in VTOL hover modes, link loss triggers RTL." : "Bit 5 set: in VTOL hover modes, link loss triggers QRTL (return and land vertically)."));
                    else
                        r.Add(new Result(AreaLink, "Link loss while hovering (Q_OPTIONS)", Status.Caution,
                            "Neither bit 5 nor bit 20 is set: in VTOL hover modes (QHOVER, QLOITER...) link loss makes the aircraft LAND WHERE IT IS (QLAND).",
                            "Set Q_OPTIONS bit 5 (QRTL) or bit 20 (RTL) - add 32 or 1048576 to the current value."));
                }
            }

            var gcsSys = I("MAV_GCS_SYSID") ?? I("SYSID_MYGCS");
            if (gcsSys != null && gcsFs is >= 1 and <= 3)
                r.Add(new Result(AreaLink, "Ground station watched by the failsafe", Status.Info,
                    $"The autopilot only counts heartbeats from system ID {gcsSys.Value}. MAVProxy uses 255 by default; InfraDrone Desktop itself uses 254 and sends none. If your MAVProxy runs with a different ID, the link-loss failsafe never arms."));

            // ---------------- BATTERY ----------------
            var mon = I("BATT_MONITOR");
            if (mon == null) r.Add(Missing(AreaBattery, "Battery monitor (BATT_MONITOR)"));
            else if (mon.Value == 0)
                r.Add(new Result(AreaBattery, "Battery monitor (BATT_MONITOR)", Status.NoGo,
                    "0 = Disabled. The autopilot cannot see the battery, so no battery failsafe can trigger.",
                    "Set BATT_MONITOR for your power module (e.g. 4 = analog voltage and current), then reboot."));
            else
                r.Add(new Result(AreaBattery, "Battery monitor (BATT_MONITOR)", Status.Go, $"{mon.Value} = enabled."));

            if (mon is > 0)
            {
                var lowV = F("BATT_LOW_VOLT") ?? 0; var lowMah = F("BATT_LOW_MAH") ?? 0;
                var crtV = F("BATT_CRT_VOLT") ?? 0; var crtMah = F("BATT_CRT_MAH") ?? 0;

                AddBatteryAction(r, "Low battery", "BATT_FS_LOW_ACT", I("BATT_FS_LOW_ACT"), lowV, lowMah,
                                 "BATT_LOW_VOLT", "BATT_LOW_MAH", critical: false);
                AddBatteryAction(r, "Critical battery", "BATT_FS_CRT_ACT", I("BATT_FS_CRT_ACT"), crtV, crtMah,
                                 "BATT_CRT_VOLT", "BATT_CRT_MAH", critical: true);

                if (lowV > 0 && crtV > 0 && crtV >= lowV)
                    r.Add(new Result(AreaBattery, "Battery thresholds in order", Status.Caution,
                        $"Critical voltage ({crtV:0.0} V) is not below low voltage ({lowV:0.0} V), so 'low' never fires before 'critical'.",
                        "Set BATT_CRT_VOLT lower than BATT_LOW_VOLT."));
                if (lowMah > 0 && crtMah > 0 && crtMah >= lowMah)
                    r.Add(new Result(AreaBattery, "Battery thresholds in order", Status.Caution,
                        $"Critical remaining capacity ({crtMah:0} mAh) is not below low ({lowMah:0} mAh).",
                        "Set BATT_CRT_MAH lower than BATT_LOW_MAH."));
            }

            // ---------------- GEOFENCE ----------------
            var fEn = I("FENCE_ENABLE"); var fAuto = I("FENCE_AUTOENABLE");
            bool fenceOn = (fEn ?? 0) == 1 || (fAuto ?? 0) >= 1;
            if (fEn == null && fAuto == null) r.Add(Missing(AreaFence, "Geofence enabled (FENCE_ENABLE)"));
            else if (!fenceOn)
                r.Add(new Result(AreaFence, "Geofence enabled (FENCE_ENABLE)", Status.NoGo,
                    "Geofence is off (FENCE_ENABLE = 0 and FENCE_AUTOENABLE = 0).",
                    "Set FENCE_ENABLE = 1, or FENCE_AUTOENABLE = 1 (enables on take-off)."));
            else
                r.Add(new Result(AreaFence, "Geofence enabled (FENCE_ENABLE)", Status.Go,
                    (fEn ?? 0) == 1 ? "FENCE_ENABLE = 1." : $"Enabled automatically (FENCE_AUTOENABLE = {fAuto})."));

            if (fenceOn)
            {
                var fType = I("FENCE_TYPE");
                if (fType == null) r.Add(Missing(AreaFence, "Fence types (FENCE_TYPE)"));
                else
                {
                    var parts = new List<string>();
                    if ((fType.Value & 1) != 0) parts.Add("max altitude");
                    if ((fType.Value & 2) != 0) parts.Add("circle around home");
                    if ((fType.Value & 4) != 0) parts.Add("polygons");
                    if ((fType.Value & 8) != 0) parts.Add("min altitude");
                    var desc = parts.Count == 0 ? "none" : string.Join(", ", parts);
                    if ((fType.Value & 1) == 0)
                        r.Add(new Result(AreaFence, "Fence types (FENCE_TYPE)", Status.NoGo,
                            $"{fType.Value} = {desc}. No maximum-altitude fence.",
                            "Add bit 0 (value 1) to FENCE_TYPE."));
                    else if ((fType.Value & 4) == 0)
                        r.Add(new Result(AreaFence, "Fence types (FENCE_TYPE)", Status.Caution,
                            $"{fType.Value} = {desc}. Polygon fences are off, so no SORA (Specific Operations Risk Assessment) boundary is enforced - only the altitude ceiling.",
                            "Plot the route in Mission Planner and click Upload Fence - it uploads the boundary and switches polygons on."));
                    else
                        r.Add(new Result(AreaFence, "Fence types (FENCE_TYPE)", Status.Go, $"{fType.Value} = {desc}."));

                    if ((fType.Value & 4) != 0)
                    {
                        if (fencePoints == null)
                            r.Add(new Result(AreaFence, "Polygon fence loaded", Status.Caution,
                                "Could not read the fence stored on the vehicle.",
                                "Read again, or check the fence in QGroundControl (Plan > Fence)."));
                        else if (fencePoints.Value < 3)
                            r.Add(new Result(AreaFence, "Polygon fence loaded", Status.NoGo,
                                $"Polygon fences are switched on, but the vehicle holds {fencePoints.Value} fence points. ArduPilot will refuse to arm ('Polygon fence(s) invalid').",
                                "Plot the route in Mission Planner and click Upload Fence."));
                        else
                            r.Add(new Result(AreaFence, "Polygon fence loaded", Status.Go,
                                $"{fencePoints.Value} points stored on the vehicle."));
                    }
                }

                var fAct = I("FENCE_ACTION");
                switch (fAct)
                {
                    case null: r.Add(Missing(AreaFence, "Fence breach action (FENCE_ACTION)")); break;
                    case 0:
                        r.Add(new Result(AreaFence, "Fence breach action (FENCE_ACTION)", Status.NoGo,
                            "0 = Report only. The aircraft flies straight through the fence.",
                            "Set FENCE_ACTION = 1 (RTL).")); break;
                    case 1:
                        r.Add(new Result(AreaFence, "Fence breach action (FENCE_ACTION)", Status.Go, "1 = RTL.")); break;
                    case 8:
                        r.Add(new Result(AreaFence, "Fence breach action (FENCE_ACTION)", Status.Go, "8 = AUTOLAND if possible, otherwise RTL.")); break;
                    case 6:
                    case 7:
                        r.Add(new Result(AreaFence, "Fence breach action (FENCE_ACTION)", Status.Caution,
                            fAct == 6 ? "6 = Guided: flies to the fence return point and circles there." : "7 = Guided with pilot throttle: flies to the return point; throttle stays with the pilot.",
                            "For BVLOS, FENCE_ACTION = 1 (RTL) is simpler to justify.")); break;
                    default: r.Add(Unknown(AreaFence, "Fence breach action (FENCE_ACTION)", fAct.Value)); break;
                }

                var fMax = F("FENCE_ALT_MAX");
                if (fMax != null && ((I("FENCE_TYPE") ?? 0) & 1) != 0)
                    r.Add(new Result(AreaFence, "Fence maximum altitude (FENCE_ALT_MAX)",
                        fMax.Value > 120 ? Status.Caution : Status.Go,
                        fMax.Value > 120
                            ? $"{fMax.Value:0} m above home: higher than the 120 m limit. Only valid if your authorisation allows it."
                            : $"{fMax.Value:0} m above home."));
            }

            // ---------------- RETURN & LANDING ----------------
            var rtlAlt = F("RTL_ALTITUDE");
            var fenceMax = F("FENCE_ALT_MAX");
            if (rtlAlt == null) r.Add(Missing(AreaReturn, "RTL altitude (RTL_ALTITUDE)"));
            else if (rtlAlt.Value < 0)
                r.Add(new Result(AreaReturn, "RTL altitude (RTL_ALTITUDE)", Status.Caution,
                    "-1 = return at whatever altitude it is at. It may be too low for obstacles on the way home.",
                    "Set a fixed RTL_ALTITUDE above the highest obstacle on the route."));
            else if (fenceOn && fenceMax != null && ((I("FENCE_TYPE") ?? 0) & 1) != 0 && rtlAlt.Value >= fenceMax.Value)
                r.Add(new Result(AreaReturn, "RTL altitude (RTL_ALTITUDE)", Status.NoGo,
                    $"{rtlAlt.Value:0} m is at or above the fence ceiling ({fenceMax.Value:0} m). RTL would breach the fence it is escaping.",
                    "Set RTL_ALTITUDE below FENCE_ALT_MAX."));
            else
                r.Add(new Result(AreaReturn, "RTL altitude (RTL_ALTITUDE)", Status.Go, $"{rtlAlt.Value:0} m above home."));

            if (isQuadPlane)
            {
                var qrtl = I("Q_RTL_MODE");
                var autoland = I("RTL_AUTOLAND") ?? 0;
                if (qrtl == null) r.Add(Missing(AreaReturn, "VTOL landing at end of RTL (Q_RTL_MODE)"));
                else if (qrtl.Value is >= 1 and <= 3)
                    r.Add(new Result(AreaReturn, "VTOL landing at end of RTL (Q_RTL_MODE)", Status.Go,
                        qrtl.Value switch { 1 => "1 = Switches to QRTL near home and lands vertically.",
                                            2 => "2 = VTOL approach: circles down, then lands vertically into the wind.",
                                            _ => "3 = QRTL always: returns and lands vertically." }));
                else if (autoland >= 1)
                    r.Add(new Result(AreaReturn, "VTOL landing at end of RTL (Q_RTL_MODE)", Status.Caution,
                        $"Q_RTL_MODE = 0; RTL_AUTOLAND = {autoland} relies on a landing sequence in the mission (DO_LAND_START).",
                        "Easier for the Loong: Q_RTL_MODE = 1 or 2."));
                else
                    r.Add(new Result(AreaReturn, "VTOL landing at end of RTL (Q_RTL_MODE)", Status.NoGo,
                        "Q_RTL_MODE = 0 and RTL_AUTOLAND = 0: after RTL the aircraft circles over home until the battery runs out. It will not land by itself.",
                        "Set Q_RTL_MODE = 1 (or 2 for a VTOL approach)."));
            }
            else
            {
                r.Add(new Result(AreaReturn, "Vehicle type", Status.Info,
                    "Q_ENABLE is 0 or missing: treated as a plain fixed-wing (no VTOL checks)."));
            }

            // ---------------- ARMING ----------------
            if (p.TryGetValue("ARMING_SKIPCHK", out var skip))
            {
                int s = (int)Math.Round(skip);
                r.Add(s == 0
                    ? new Result(AreaArming, "Pre-arm checks (ARMING_SKIPCHK)", Status.Go, "0 = no checks skipped.")
                    : s == -1
                        ? new Result(AreaArming, "Pre-arm checks (ARMING_SKIPCHK)", Status.NoGo,
                            "-1 = ALL pre-arm checks skipped.", "Set ARMING_SKIPCHK = 0.")
                        : new Result(AreaArming, "Pre-arm checks (ARMING_SKIPCHK)", Status.Caution,
                            $"{s} = some pre-arm checks are skipped.", "Set ARMING_SKIPCHK = 0 unless you know why each is skipped."));
            }
            else if (p.TryGetValue("ARMING_CHECK", out var chk))
            {
                int c = (int)Math.Round(chk);
                r.Add(c == 1
                    ? new Result(AreaArming, "Pre-arm checks (ARMING_CHECK)", Status.Go, "1 = all checks.")
                    : c == 0
                        ? new Result(AreaArming, "Pre-arm checks (ARMING_CHECK)", Status.NoGo,
                            "0 = pre-arm checks disabled.", "Set ARMING_CHECK = 1.")
                        : new Result(AreaArming, "Pre-arm checks (ARMING_CHECK)", Status.Caution,
                            $"{c} = only some checks enabled.", "Set ARMING_CHECK = 1."));
            }
            else r.Add(Missing(AreaArming, "Pre-arm checks (ARMING_SKIPCHK / ARMING_CHECK)"));

            return new Report(r, p, unreadable);
        }

        private static void AddBatteryAction(List<Result> r, string what, string actName, int? act,
                                             float volt, float mah, string voltName, string mahName, bool critical)
        {
            string label = $"{what} action ({actName})";
            if (act == null) { r.Add(Missing(AreaBattery, label)); return; }

            string meaning = act.Value switch
            {
                0 => "0 = Warn only",
                1 => "1 = RTL",
                2 => "2 = Land",
                3 => "3 = Terminate (motors stop, aircraft falls)",
                4 => "4 = QLand (land vertically where it is)",
                5 => "5 = Parachute release",
                6 => "6 = Loiter down, then QLand",
                7 => "7 = AUTOLAND, otherwise RTL",
                _ => $"{act.Value} = unknown value",
            };

            bool hasThreshold = volt > 0 || mah > 0;
            string threshold = hasThreshold
                ? $" Triggers at {(volt > 0 ? $"{volt:0.0} V" : "")}{(volt > 0 && mah > 0 ? " or " : "")}{(mah > 0 ? $"{mah:0} mAh left" : "")}."
                : "";

            if (act.Value != 0 && !hasThreshold)
            {
                r.Add(new Result(AreaBattery, label, Status.NoGo,
                    $"{meaning}, but {voltName} and {mahName} are both 0, so it can never trigger.",
                    $"Set {voltName} (per-pack voltage) and/or {mahName}."));
                return;
            }

            switch (act.Value)
            {
                case 1: case 2: case 4: case 6: case 7:
                    r.Add(new Result(AreaBattery, label, Status.Go, meaning + "." + threshold)); break;
                case 0:
                    r.Add(new Result(AreaBattery, label, critical ? Status.NoGo : Status.Caution,
                        meaning + ": the aircraft keeps flying." + threshold,
                        critical ? $"Set {actName} = 1 (RTL) or 4 (QLand)." : $"For BVLOS, set {actName} = 1 (RTL).")); break;
                case 3: case 5:
                    r.Add(new Result(AreaBattery, label, Status.Caution,
                        meaning + "." + threshold, "Only use with a parachute / flight-termination system your authorisation covers.")); break;
                default:
                    r.Add(Unknown(AreaBattery, label, act.Value)); break;
            }
        }

        private static Result Missing(string area, string label) =>
            new(area, label, Status.NoGo, "Could not read this parameter from the vehicle.",
                "Check it in QGroundControl (Vehicle Setup > Parameters). If it doesn't exist, the firmware may be older than ArduPlane 4.5.");

        private static Result Unknown(string area, string label, int value) =>
            new(area, label, Status.Caution, $"{value} = value not recognised by this check (firmware may be newer or older).",
                "Look the value up in the ArduPilot parameter list for your firmware version.");
    }
}
