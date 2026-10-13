#!/usr/bin/env python3
"""
Failsafe drills for the ArduPlane SIMULATOR (SITL), run by InfraDrone Desktop's
Simulation tab (one button per drill). Can also be run by hand:

    python3 sim_drills.py setup              apply BVLOS-ready failsafe settings
    python3 sim_drills.py traffic [2|5]      fake aircraft on a collision course
                                             (2 = climb/descend, 5 = RTL; default = current)
    python3 sim_drills.py rcloss             radio-control link lost
    python3 sim_drills.py battery            battery drained to low, then critical
    python3 sim_drills.py fence              altitude-fence breach
    python3 sim_drills.py restore            undo anything a stopped drill changed

SAFETY
  - Connects only to TCP 127.0.0.1:5762, a port that exists only in SITL.
  - Refuses to do anything unless the SIM_SPEEDUP parameter exists (SITL only).
  - Every simulator setting a drill changes is saved to sim_drills_state.json
    first and restored at the end, or by 'restore' if the drill was stopped.

OUTPUT
  Short human-readable lines. The last line always starts with
  'RESULT: PASS', 'RESULT: FAIL', 'RESULT: DONE' or 'RESULT: NOT STARTED'.
  Detail goes to sim_drills.log in the working directory.
"""
import json, math, os, sys, time
from pymavlink import mavutil

CONN = "tcp:127.0.0.1:5762"
LOG = "sim_drills.log"
STATE = "sim_drills_state.json"

PLANE_MODES = {0: "MANUAL", 5: "FBWA", 10: "AUTO", 11: "RTL", 12: "LOITER", 13: "TAKEOFF",
               14: "AVOID_ADSB", 15: "GUIDED", 17: "QSTABILIZE", 18: "QHOVER", 19: "QLOITER",
               20: "QLAND", 21: "QRTL", 25: "LOITER_QLAND", 26: "AUTOLAND"}
RETURN_MODES = {"RTL", "QRTL", "AUTOLAND"}
LAND_MODES = {"QLAND", "LOITER_QLAND"}

_log = open(LOG, "a")
def L(msg):
    _log.write(f"{time.strftime('%H:%M:%S')} {msg}\n"); _log.flush()

def say(msg):
    print(msg, flush=True); L(msg)

def result(kind, msg):
    say(f"RESULT: {kind} - {msg}")
    sys.exit(0)

# ---------------------------------------------------------------- connection
def connect():
    try:
        m = mavutil.mavlink_connection(CONN, source_system=250, autoreconnect=False)
    except Exception as e:
        result("NOT STARTED", f"could not reach the simulator on TCP 5762 ({e}). Start the simulator first.")
    if not m.wait_heartbeat(timeout=15):
        result("NOT STARTED", "no heartbeat from the simulator. Start the simulator first.")
    if get_param(m, "SIM_SPEEDUP") is None:
        result("NOT STARTED", "this is not the simulator (no SIM_SPEEDUP). Nothing was changed.")
    # position at 5 Hz on this link
    m.mav.command_long_send(m.target_system, m.target_component,
                            mavutil.mavlink.MAV_CMD_SET_MESSAGE_INTERVAL, 0, 33, 200_000, 0, 0, 0, 0, 0)
    return m

def get_param(m, name, timeout=3.0):
    for _ in range(2):
        m.mav.param_request_read_send(m.target_system, m.target_component, name.encode(), -1)
        end = time.time() + timeout / 2
        while time.time() < end:
            msg = m.recv_match(type="PARAM_VALUE", blocking=True, timeout=0.3)
            if msg and msg.param_id.rstrip("\x00") == name:
                return msg.param_value
    return None

def set_param(m, name, value):
    for _ in range(2):
        m.mav.param_set_send(m.target_system, m.target_component, name.encode(), float(value),
                             mavutil.mavlink.MAV_PARAM_TYPE_REAL32)
        end = time.time() + 1.5
        while time.time() < end:
            msg = m.recv_match(type="PARAM_VALUE", blocking=True, timeout=0.3)
            if msg and msg.param_id.rstrip("\x00") == name:
                ok = abs(msg.param_value - float(value)) < 1e-3
                L(f"set {name} = {value} {'OK' if ok else 'MISMATCH ' + str(msg.param_value)}")
                return ok
    L(f"set {name} = {value} NO REPLY")
    return False

# ---------------------------------------------------------------- state save/restore
def save_original(m, names):
    state = {}
    if os.path.exists(STATE):
        try: state = json.load(open(STATE))
        except Exception: state = {}
    for n in names:
        if n not in state:
            v = get_param(m, n)
            if v is not None: state[n] = v
    json.dump(state, open(STATE, "w"))
    return state

def restore_all(m, quiet=False):
    if not os.path.exists(STATE):
        if not quiet: say("Nothing to restore.")
        return 0
    state = json.load(open(STATE))
    n = 0
    for name, value in state.items():
        if set_param(m, name, value): n += 1
    os.remove(STATE)
    if not quiet: say(f"Restored {n} simulator setting(s).")
    return n

# ---------------------------------------------------------------- vehicle state
class Vehicle:
    def __init__(self, m):
        self.m = m; self.mode = None; self.armed = False
        self.lat = self.lon = None; self.alt_amsl = 0.0; self.rel_alt = 0.0
        self.voltage = None; self.texts = []

    def pump(self, wait=0.0):
        end = time.time() + wait
        while True:
            # Non-blocking reads must use timeout=None: pymavlink treats timeout=0
            # as "already expired" and returns before reading anything.
            msg = self.m.recv_match(type=["HEARTBEAT", "GLOBAL_POSITION_INT", "SYS_STATUS", "STATUSTEXT"],
                                    blocking=wait > 0, timeout=max(0.05, end - time.time()) if wait > 0 else None)
            if msg is None:
                if time.time() >= end: return
                continue
            t = msg.get_type()
            if t == "HEARTBEAT":
                if msg.get_srcSystem() == self.m.target_system and msg.type != mavutil.mavlink.MAV_TYPE_GCS:
                    new = PLANE_MODES.get(msg.custom_mode, f"mode {msg.custom_mode}")
                    if self.mode is not None and new != self.mode:
                        L(f"mode {self.mode} -> {new}")
                    self.mode = new
                    self.armed = bool(msg.base_mode & mavutil.mavlink.MAV_MODE_FLAG_SAFETY_ARMED)
            elif t == "GLOBAL_POSITION_INT":
                self.lat, self.lon = msg.lat / 1e7, msg.lon / 1e7
                self.alt_amsl = msg.alt / 1000.0; self.rel_alt = msg.relative_alt / 1000.0
            elif t == "SYS_STATUS":
                self.voltage = msg.voltage_battery / 1000.0
            elif t == "STATUSTEXT":
                text = msg.text.rstrip("\x00") if isinstance(msg.text, str) else msg.text.decode(errors="ignore").rstrip("\x00")
                L(f"STATUSTEXT {text}")
                low = text.lower()
                if any(k in low for k in ("failsafe", "fence", "battery", "avoid", "adsb", "rtl")):
                    say(f"  autopilot says: {text}")

def require_flying(v, min_alt=10.0):
    v.pump(4.0)
    if v.mode is None or v.lat is None:
        result("NOT STARTED", "no data from the simulator.")
    if not v.armed or v.rel_alt < min_alt:
        result("NOT STARTED", f"the drone is not flying (needs to be armed and above {min_alt:.0f} m). "
                              "Upload a mission, Arm, switch to AUTO, wait until it flies forward, then run the drill.")

def wait_for_reaction(v, start_mode, timeout, good_modes, label):
    t0 = time.time(); last = 0
    while time.time() - t0 < timeout:
        v.pump(0.5)
        if v.mode != start_mode and v.mode in good_modes:
            secs = time.time() - t0
            say(f"AUTOPILOT REACTED after {secs:.0f} s: switched to {v.mode}.")
            return v.mode, secs
        if time.time() - last > 5:
            say(f"  {label} ... {time.time() - t0:3.0f} s, mode {v.mode}, alt {v.rel_alt:.0f} m")
            last = time.time()
    return None, None

# ================================================================ drills
def drill_setup(m):
    say("Applying BVLOS-ready failsafe settings to the simulator ...")
    before_batt = get_param(m, "BATT_MONITOR"); before_avd = get_param(m, "AVD_ENABLE")
    wanted = [("FS_GCS_ENABL", 1), ("THR_FAILSAFE", 1), ("FS_LONG_ACTN", 1), ("FS_LONG_TIMEOUT", 10),
              ("BATT_MONITOR", 4), ("BATT_LOW_VOLT", 10.5), ("BATT_CRT_VOLT", 10.0),
              ("BATT_FS_LOW_ACT", 1), ("BATT_FS_CRT_ACT", 4),
              ("FENCE_ENABLE", 1), ("FENCE_ACTION", 1), ("FENCE_ALT_MAX", 120), ("Q_RTL_MODE", 1),
              ("ADSB_TYPE", 1), ("AVD_ENABLE", 1)]
    ftype = get_param(m, "FENCE_TYPE")
    if ftype is not None and (int(ftype) & 1) == 0:
        wanted.append(("FENCE_TYPE", int(ftype) | 1))
    qopt = get_param(m, "Q_OPTIONS")
    if qopt is not None:
        wanted.append(("Q_OPTIONS", int(qopt) | 32))
    ok = sum(1 for n, val in wanted if set_param(m, n, val))
    act = get_param(m, "AVD_F_ACTION")
    if act is not None and int(act) not in (2, 3, 4, 5, 6):
        if set_param(m, "AVD_F_ACTION", 5): ok += 1
    say(f"Set {ok} settings.")
    needs_restart = (before_batt is None or int(before_batt) != 4 or before_avd is None
                     or int(before_avd) != 1 or act is None)
    if needs_restart:
        result("DONE", "settings applied. Restart the simulator once (Stop, then Start) so the battery "
                       "monitor and traffic avoidance take effect, then run 'Apply' again.")
    result("DONE", "simulator has BVLOS-ready failsafe settings. Check them in Pre-flight > Read failsafe settings.")

def drill_traffic(m, wanted_act=None):
    if get_param(m, "ADSB_TYPE") in (None, 0.0) or get_param(m, "AVD_ENABLE") in (None, 0.0):
        result("NOT STARTED", "ADS-B / avoidance is off. Click 'Apply BVLOS failsafe settings' and restart the simulator.")
    act = get_param(m, "AVD_F_ACTION")
    if act is None:
        result("NOT STARTED", "avoidance settings not visible yet. Restart the simulator (Stop, Start).")
    if wanted_act in (2, 3, 4, 5, 6) and int(act) != wanted_act:
        save_original(m, ["AVD_F_ACTION"])
        set_param(m, "AVD_F_ACTION", wanted_act); act = wanted_act
    names = {2: "climb or descend", 3: "move horizontally", 4: "move perpendicular", 5: "RTL", 6: "hover"}
    if int(act) not in names:
        result("NOT STARTED", f"AVD_F_ACTION = {int(act)} takes no avoiding action. Use 'Apply' first.")
    f_xy = get_param(m, "AVD_F_DIST_XY") or 100.0
    f_z = get_param(m, "AVD_F_DIST_Z") or 100.0

    v = Vehicle(m); require_flying(v)
    start_mode = v.mode
    say(f"Avoidance action {int(act)} = {names[int(act)]}. Danger box: within {f_xy:.0f} m horizontally AND {f_z:.0f} m vertically.")
    i_lat = v.lat + 3000 / 111_320.0; i_lon = v.lon; i_alt = v.alt_amsl
    say(f"Fake aircraft DRILL01 starts 3000 m north at the drone's height, 60 m/s, heading for the drone.")
    t0 = time.time(); last = 0; reacted = None; min3d = 1e9; at_min = (0, 0); safe = True; min_h = 1e9; brg = 0.0
    while time.time() - t0 < 150:
        v.pump(0.0)
        d = math.hypot((v.lat - i_lat) * 111_320.0, (v.lon - i_lon) * 111_320.0 * math.cos(math.radians(i_lat)))
        dz = abs(v.alt_amsl - i_alt); d3 = math.hypot(d, dz)
        min_h = min(min_h, d)
        if d3 < min3d: min3d = d3; at_min = (d, dz)
        if d < f_xy and dz < f_z: safe = False
        if reacted is None and v.mode != start_mode and v.mode in RETURN_MODES | LAND_MODES | {"AVOID_ADSB", "LOITER", "QLOITER", "GUIDED"}:
            reacted = d; say(f"AUTOPILOT REACTED: switched to {v.mode} with the aircraft {d:.0f} m away.")
        if time.time() - last > 5:
            say(f"  aircraft {d:5.0f} m horizontally, {dz:4.0f} m vertically, drone {v.mode}")
            last = time.time()
        if d < 20 or d > min_h + 500: break
        if reacted is None and d > 400:
            brg = math.atan2((v.lon - i_lon) * math.cos(math.radians(i_lat)), v.lat - i_lat)
        step = 0.5
        i_lat += 60 * step * math.cos(brg) / 111_320.0
        i_lon += 60 * step * math.sin(brg) / (111_320.0 * math.cos(math.radians(i_lat)))
        m.mav.adsb_vehicle_send(0xABC123, int(i_lat * 1e7), int(i_lon * 1e7),
                                mavutil.mavlink.ADSB_ALTITUDE_TYPE_GEOMETRIC, int(i_alt * 1000),
                                int((math.degrees(brg) % 360) * 100), 6000, 0, b"DRILL01",
                                mavutil.mavlink.ADSB_EMITTER_TYPE_LIGHT, 1, 31, 1200)
        time.sleep(step)
    restore_all(m, quiet=True)
    say(f"Closest approach {min3d:.0f} m ({at_min[0]:.0f} m horizontal, {at_min[1]:.0f} m vertical).")
    if reacted is None:
        result("FAIL", "the autopilot did not react to the traffic.")
    if not safe:
        result("FAIL", f"detection OK (autopilot reacted at {reacted:.0f} m), but the avoidance manoeuvre "
                       f"did not keep {f_xy:.0f} m / {f_z:.0f} m separation. A head-on aircraft at 60 m/s "
                       "outpaces the drone - plan BVLOS so it does not rely on last-moment avoidance.")
    result("PASS", f"reacted at {reacted:.0f} m and kept safe separation.")

def drill_rcloss(m):
    thr = get_param(m, "THR_FAILSAFE"); long_to = get_param(m, "FS_LONG_TIMEOUT") or 5
    v = Vehicle(m); require_flying(v)
    start_mode = v.mode
    save_original(m, ["SIM_RC_FAIL"])
    say(f"Cutting the radio-control signal (as if the RC link dropped). Long failsafe after {long_to:.0f} s.")
    if thr is None or int(thr) != 1:
        say("  note: THR_FAILSAFE is not 1 - the autopilot may not even notice. That is what this drill tests.")
    set_param(m, "SIM_RC_FAIL", 1)
    mode, secs = wait_for_reaction(v, start_mode, long_to + 20, RETURN_MODES | LAND_MODES, "RC lost")
    restore_all(m, quiet=True)
    say("Radio-control signal restored. The drone stays in its failsafe mode until you change it.")
    if mode is None:
        result("FAIL", f"no return or landing within {long_to + 20:.0f} s of losing RC.")
    result("PASS", f"RC loss -> {mode} after {secs:.0f} s.")

def drill_battery(m):
    if get_param(m, "BATT_MONITOR") in (None, 0.0):
        result("NOT STARTED", "battery monitor is off. Click 'Apply BVLOS failsafe settings' and restart the simulator.")
    low = get_param(m, "BATT_LOW_VOLT") or 0; crt = get_param(m, "BATT_CRT_VOLT") or 0
    if low <= 0:
        result("FAIL", "BATT_LOW_VOLT is 0 - the low-battery failsafe can never trigger.")
    v = Vehicle(m); require_flying(v)
    save_original(m, ["SIM_BATT_VOLTAGE"])
    start_mode = v.mode
    say(f"Draining the simulated battery to {low - 0.3:.1f} V (low threshold {low:.1f} V). "
        "ArduPilot waits about 10 s (BATT_LOW_TIMER) before acting.")
    set_param(m, "SIM_BATT_VOLTAGE", low - 0.3)
    mode1, s1 = wait_for_reaction(v, start_mode, 40, RETURN_MODES | LAND_MODES, "battery low")
    mode2 = None
    if crt > 0:
        say(f"Now draining to {crt - 0.3:.1f} V (critical threshold {crt:.1f} V).")
        set_param(m, "SIM_BATT_VOLTAGE", crt - 0.3)
        mode2, _ = wait_for_reaction(v, mode1 or start_mode, 40, LAND_MODES | {"QRTL", "AUTOLAND"}, "battery critical")
    restore_all(m, quiet=True)
    say("Battery voltage restored. ArduPilot keeps the battery failsafe latched - restart the simulator before the next flight.")
    if mode1 is None:
        result("FAIL", "no reaction to low battery within 40 s.")
    if crt > 0 and mode2 is None:
        result("FAIL", f"low battery -> {mode1} OK, but no landing on critical battery.")
    result("PASS", f"low battery -> {mode1}" + (f", critical -> {mode2}." if mode2 else "."))

def drill_fence(m):
    en = get_param(m, "FENCE_ENABLE"); ft = get_param(m, "FENCE_TYPE")
    if en in (None, 0.0) or ft is None or (int(ft) & 1) == 0:
        result("NOT STARTED", "the altitude fence is off. Click 'Apply BVLOS failsafe settings' first.")
    v = Vehicle(m); require_flying(v, min_alt=25.0)
    start_mode = v.mode
    save_original(m, ["FENCE_ALT_MAX"])
    new_max = max(10.0, round(v.rel_alt - 10.0))
    say(f"Drone at {v.rel_alt:.0f} m. Lowering the fence ceiling to {new_max:.0f} m, so the drone is suddenly outside it.")
    set_param(m, "FENCE_ALT_MAX", new_max)
    mode, secs = wait_for_reaction(v, start_mode, 25, RETURN_MODES | LAND_MODES | {"GUIDED", "LOITER"}, "fence breached")
    v.pump(3.0)
    restore_all(m, quiet=True)
    say("Fence ceiling restored.")
    if mode is None:
        result("FAIL", "no reaction to the fence breach within 25 s.")
    result("PASS", f"fence breach -> {mode} after {secs:.0f} s.")

# ================================================================ main
if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    if cmd not in ("setup", "traffic", "rcloss", "battery", "fence", "restore"):
        print(__doc__); sys.exit(1)
    L(f"===== {cmd} {' '.join(sys.argv[2:])}")
    m = connect()
    if cmd == "restore":
        restore_all(m); result("DONE", "restore finished.")
    if os.path.exists(STATE):
        restore_all(m, quiet=True)   # leftovers from a stopped drill
    try:
        if cmd == "setup": drill_setup(m)
        elif cmd == "traffic": drill_traffic(m, int(sys.argv[2]) if len(sys.argv) > 2 else None)
        elif cmd == "rcloss": drill_rcloss(m)
        elif cmd == "battery": drill_battery(m)
        elif cmd == "fence": drill_fence(m)
    except SystemExit:
        raise
    except Exception as e:
        restore_all(m, quiet=True)
        result("FAIL", f"drill error: {e}")
