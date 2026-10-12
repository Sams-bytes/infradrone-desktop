# Suriname demo — data and tools

The demo display lives in `wwwroot/demo/` and is served by the app (sidebar → 🇸🇷 Suriname Demo).

## Files

| File | What it is |
|---|---|
| `wwwroot/demo/scenarios.json` | All texts (EN/NL), presenter notes, aircraft settings and the geometry of both scenarios. Edit texts freely. |
| `wwwroot/demo/geo.json` | Suriname national border and district outlines for the map (simplified). |
| `wwwroot/demo/*.js, *.css, index.html` | The display: flight engine, map renderers (Google 3D / Cesium), HUD, sensor view. |
| `tools/suriname-demo/geo.py`, `build_scen.py` | How the geometry and `scenarios.json` were produced (Python, shapely, pyproj). |
| `tools/suriname-demo/test-mock-google.js` | Test stand-in for Google 3D Maps (enforces the documented rules) — not used by the app. |

After editing anything in `wwwroot/demo/`, run `dotnet build` so the copy next to the app is updated.

## Where the geometry comes from

* Border: geoBoundaries gbOpen ADM0/ADM1 for Suriname, derived from OpenStreetMap (ODbL 1.0) — attribution is shown on screen.
* Patrol tracks: the border line along the Marowijne / Corantijn, offset **650 m / 700 m into Suriname**. Checked: every route stays inside Suriname, at least 450 m from the land border.
* Transit routes: Paramaribo (Zorg en Hoop) → Moengo → Albina (129 km), and an inland route to Nieuw Nickerie via Totness (204 km; crosses the Coppename river, no open sea).
* Search areas: 2.0 × 1.2 km, search lines every 120 m at 120 m altitude. **Check in Google 3D that these are forest before the meeting**; move `sar.area`, `sar.lines`, `sar.target`, `sar.lkp`, `sar.team_start` together if needed.

## Aircraft numbers

`aircraft.endurance_min` (default 150) drives the battery display. Set the real value in the Suriname Demo tab
(Setup → Endurance). If a ferry flight needs more than 80 % of it, the demo automatically lands at a midway town
(Moengo / Totness) for a battery swap.

## Offline package

The tab's "Prepare offline package" downloads CesiumJS 1.146.0 from the npm registry and Sentinel-2 cloudless 2016
tiles (EOX IT Services GmbH, CC BY 4.0 — commercial use allowed with attribution) for the demo corridors:
about 1,450 tiles, 35 MB, into `~/.infradrone/offline/`.
