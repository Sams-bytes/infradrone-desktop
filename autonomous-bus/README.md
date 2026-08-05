# Autonomous Bus (Qbuzz)

Fully autonomous 8-passenger shuttle bus, developed for road-legal operation
in the Netherlands with Qbuzz, targeting RDW approval.

**This directory is fully self-contained.** No shared code, configs, or
dependencies with any drone (DAMbv) project. If you ever find yourself
importing something from a drone repo path into this tree, stop — copy the
logic in explicitly instead of sharing a module across domains.

## Status

Planning stage. No hardware mounted yet. Bus not yet delivered by Qbuzz.

## Layout

```
autonomous-bus/
├── docs/                  # RDW-facing documentation
│   ├── conops.md           # Concept of Operations
│   ├── odd.md               # Operational Design Domain definition
│   └── safety-case/         # Safety case artifacts, hazard analysis, test evidence
├── ros2_ws/
│   └── src/
│       ├── bus_perception/    # Sensor drivers, object detection, fusion
│       ├── bus_localization/  # RTK GNSS + IMU + LIDAR localization
│       ├── bus_planning/      # Route/behavior/motion planning
│       ├── bus_control/       # Low-level control, CAN interface to bus actuators
│       └── bus_bringup/       # Launch files, vehicle-wide config
├── hardware/
│   ├── sensors/              # Sensor mounting, calibration files
│   └── can_bus/               # Qbuzz bus CAN bus documentation, DBC files, drivers
├── sim/                       # Simulation environments/configs
└── tools/                     # Dev scripts, data tools
```

## Stack

- ROS 2 (Jazzy)
- Autoware Core/Universe as the base ADS (perception, localization, planning, control)
- Ubuntu 22.04 dev baseline

Stack choice is provisional pending RDW conversations on vehicle
categorization and redundancy requirements — see `docs/conops.md`.

## Hardware notes

Some sensors (LIDAR, RTK GNSS, IMU, CAN bus tooling) may be reused from the
salvaged Navya Arma shuttle hardware. See `hardware/sensors/` once mounted.

## Getting started (once ROS 2 is installed)

```bash
cd ros2_ws
colcon build --symlink-install
source install/setup.bash
```
