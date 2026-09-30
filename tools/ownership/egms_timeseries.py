#!/usr/bin/env python3
"""
egms_timeseries.py - "Is it getting worse?" Movement history 2020-2024 for every FLAGGED bridge and provincial road stretch.

EGMS Calibrated (L2b) point files hold, for every radar point, the displacement (mm) on every satellite pass
(roughly every 6-12 days). This script re-reads the point files that were already downloaded by the Bridge/Road Check
(no new download) and, for each flagged asset and each satellite track:
  deck series   = median displacement of the points ON the asset, per date
  ground series = median displacement of the points 20-300 m AROUND it, per date
  difference    = deck - ground   (the asset's own movement, the area's movement removed)
From the difference it computes (all real least-squares fits, no smoothing or gap filling):
  speed in the first half and in the second half of the period (mm/yr)  -> speeding up / steady / slowing down
  acceleration (mm/yr^2) from a quadratic fit
  seasonal swing of the deck (mm) from an annual sine fit (bridges expand/contract with temperature: normal)

PLACEHOLDER settings (to agree with the province): CHANGE_MM_YR, SEASON_MM.
Values are along the satellite's line of sight; negative = moving away from the satellite (usually sinking).
Output: screening_out/timeseries.json, timeseries_run.log. Console: short summary.
"""
import json
import logging
import re
import zipfile
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import pandas as pd
import shapely
from pyproj import Transformer

import egms_bridge_points as B   # re-use the asset loader (same ids, same province filter)

SCRIPT_VERSION = "egms_timeseries 0.1"
HERE = Path(__file__).resolve().parent
OUT = HERE / "screening_out"
OUT.mkdir(parents=True, exist_ok=True)
logging.basicConfig(filename=OUT / "timeseries_run.log", filemode="w", level=logging.INFO, format="%(asctime)s %(message)s", force=True)
log = logging.info

DATE_RE = re.compile(r"^(\d{4})-?(\d{2})-?(\d{2})$")
TOLERANCE = {"bridges": 3.0, "roads": 6.0}
RING_INNER_M, RING_OUTER_M = 20.0, 300.0
MIN_DECK, MIN_RING = 3, 10
CHANGE_MM_YR = 1.0     # placeholder: second-half speed differs by >= this from the first half -> speeding up / slowing down
SEASON_MM = 2.0        # placeholder: seasonal swing >= this is mentioned as "moves with the seasons"
CHUNK = 100_000


def flagged_ids(kind):
    csv = OUT / ("bridge_points_screening.csv" if kind == "bridges" else "road_points_screening.csv")
    if not csv.exists():
        return set()
    df = pd.read_csv(csv, dtype={"asset_id": str})
    return set(df.loc[df["bp_status"].isin(["Priority", "Review"]), "asset_id"].astype(str))


def point_files():
    files = {}
    for audit in ("bridge_points_audit.json", "road_points_audit.json"):
        p = OUT / audit
        if p.exists():
            for f in json.loads(p.read_text()).get("egms_files", []):
                if Path(f["path"]).exists():
                    files[f["path"]] = f["track"]
    return files


def slope(t, y):
    ok = ~np.isnan(y)
    if ok.sum() < 4:
        return None
    return float(np.polyfit(t[ok], y[ok], 1)[0])


def analyse(dates, deck, ground):
    t = np.array([(d - dates[0]).days / 365.25 for d in dates])
    diff = deck - ground
    mid = t[-1] / 2
    v_all, v1, v2 = slope(t, diff), slope(t[t <= mid], diff[t <= mid]), slope(t[t > mid], diff[t > mid])
    ok = ~np.isnan(diff)
    accel = float(2 * np.polyfit(t[ok], diff[ok], 2)[0]) if ok.sum() >= 6 else None
    # seasonal swing of the deck itself: a + b t + s sin(2 pi t) + c cos(2 pi t)
    okd = ~np.isnan(deck)
    season = None
    if okd.sum() >= 12:
        A = np.column_stack([np.ones(okd.sum()), t[okd], np.sin(2 * np.pi * t[okd]), np.cos(2 * np.pi * t[okd])])
        coef, *_ = np.linalg.lstsq(A, deck[okd], rcond=None)
        season = float(np.hypot(coef[2], coef[3]))
    return v_all, v1, v2, accel, season


def main():
    started = datetime.now(timezone.utc)
    files = point_files()
    if not files:
        raise RuntimeError("No downloaded point files found - run the Bridge Check and/or Road Check first")

    assets, geoms, kinds, idx_of = [], [], [], {}
    for kind in ("bridges", "roads"):
        ids = flagged_ids(kind)
        if not ids:
            continue
        path = HERE / "ownership_out" / ("bgt_bridges_by_owner.geojson" if kind == "bridges" else "roads_by_owner.geojson")
        g, _ = B.load_bridges(str(path), roads=(kind == "roads"))
        g = g[g["asset_id"].isin(ids)]
        for aid, geom in zip(g["asset_id"], g.geometry):
            idx_of[(kind, aid)] = len(assets)
            assets.append((kind, aid))
            geoms.append(geom)
            kinds.append(kind)
    if not assets:
        raise RuntimeError("No flagged bridges or road stretches - nothing to analyse")
    print(f"  flagged assets: {sum(k == 'bridges' for k in kinds)} bridges, {sum(k == 'roads' for k in kinds)} road stretches", flush=True)
    geoms = np.array(geoms, dtype=object)
    keep = shapely.buffer(geoms, RING_OUTER_M)
    tree = shapely.STRtree(keep)
    bounds = shapely.total_bounds(keep)

    track_dates = {}
    parts = {}   # (asset_idx, track) -> list of (dist, pid, matrix)
    for i, (path, track) in enumerate(files.items(), 1):
        print(f"\r  reading point histories {i}/{len(files)}", end="", flush=True)
        z = zipfile.ZipFile(path)
        for name in [n for n in z.namelist() if n.lower().endswith(".csv")]:
            cols = pd.read_csv(z.open(name), nrows=0).columns
            x = B.pick(cols, "easting"); y = B.pick(cols, "northing"); src = "EPSG:3035"
            if not (x and y):
                x = B.pick(cols, "longitude", "lon"); y = B.pick(cols, "latitude", "lat"); src = "EPSG:4326"
            pid = B.pick(cols, "pid")
            dcols = [c for c in cols if DATE_RE.match(str(c))]
            if not dcols:
                log(f"{name}: no date columns found (first columns {list(cols)[:12]})")
                continue
            dates = [datetime(*map(int, DATE_RE.match(str(c)).groups())) for c in dcols]
            if track in track_dates and track_dates[track] != dates:
                common = [d for d in dates if d in set(track_dates[track])]
                log(f"{name}: dates differ from earlier file of {track}; using {len(common)} common dates")
            track_dates.setdefault(track, dates)
            ref = track_dates[track]
            colmap = {d: c for d, c in zip(dates, dcols)}
            use_dates = [d for d in ref if d in colmap]
            tf = Transformer.from_crs(src, "EPSG:28992", always_xy=True)
            use = [c for c in (x, y, pid) if c] + [colmap[d] for d in use_dates]
            for chunk in pd.read_csv(z.open(name), usecols=use, chunksize=CHUNK):
                X, Y = tf.transform(chunk[x].to_numpy(), chunk[y].to_numpy())
                inside = (X >= bounds[0]) & (X <= bounds[2]) & (Y >= bounds[1]) & (Y <= bounds[3])
                if not inside.any():
                    continue
                X, Y, sub = X[inside], Y[inside], chunk[inside]
                pts = shapely.points(X, Y)
                ip, ia = tree.query(pts, predicate="intersects")
                if len(ip) == 0:
                    continue
                dist = shapely.distance(pts[ip], geoms[ia])
                mat = sub[[colmap[d] for d in use_dates]].to_numpy(dtype=np.float32)
                # align to the track's reference dates (missing dates -> NaN)
                full = np.full((len(sub), len(ref)), np.nan, dtype=np.float32)
                pos = [ref.index(d) for d in use_dates]
                full[:, pos] = mat
                pids = sub[pid].to_numpy() if pid else ip
                for a in np.unique(ia):
                    m = ia == a
                    parts.setdefault((int(a), track), []).append((dist[m], pids[ip[m]] if pid else ip[m], full[ip[m]]))
    print()

    result = {"bridges": {}, "roads": {}}
    for (a, track), chunks in parts.items():
        kind, aid = assets[a]
        dist = np.concatenate([c[0] for c in chunks])
        pids = np.concatenate([c[1] for c in chunks])
        mat = np.vstack([c[2] for c in chunks])
        _, first = np.unique(pids, return_index=True)       # overlapping bursts -> each point once
        dist, mat = dist[first], mat[first]
        deck = dist <= TOLERANCE[kind]
        ring = (dist >= RING_INNER_M) & (dist <= RING_OUTER_M)
        if deck.sum() < MIN_DECK or ring.sum() < MIN_RING:
            continue
        dates = track_dates[track]
        d_med = np.nanmedian(mat[deck], axis=0)
        g_med = np.nanmedian(mat[ring], axis=0)
        v_all, v1, v2, accel, season = analyse(dates, d_med, g_med)
        rec = result[kind].setdefault(aid, {"tracks": []})
        rec["tracks"].append({
            "track": track, "deck_n": int(deck.sum()), "ground_n": int(ring.sum()),
            "dates": [d.strftime("%Y-%m-%d") for d in dates],
            "deck": [None if np.isnan(v) else round(float(v), 1) for v in d_med],
            "ground": [None if np.isnan(v) else round(float(v), 1) for v in g_med],
            "v_all": v_all, "v_first": v1, "v_second": v2, "accel": accel, "season_mm": season})

    counts = {"Speeding up": 0, "Steady": 0, "Slowing down": 0}
    for kind in result:
        for aid, rec in result[kind].items():
            best = max(rec["tracks"], key=lambda t: abs(t["v_all"] or 0))
            v1, v2 = best["v_first"], best["v_second"]
            if v1 is None or v2 is None:
                verdict = "Not enough data"
            elif v2 <= v1 - CHANGE_MM_YR:
                verdict = "Speeding up"
            elif v2 >= v1 + CHANGE_MM_YR:
                verdict = "Slowing down"
            else:
                verdict = "Steady"
            rec["verdict"] = verdict
            rec["best_track"] = best["track"]
            rec["seasonal"] = bool(best["season_mm"] and best["season_mm"] >= SEASON_MM)
            counts[verdict] = counts.get(verdict, 0) + 1

    result["meta"] = {"script": SCRIPT_VERSION, "run_utc": started.isoformat(timespec="seconds"),
                      "files": list(files), "settings_placeholders_to_agree_with_province": {
                          "CHANGE_MM_YR": CHANGE_MM_YR, "SEASON_MM": SEASON_MM, "RING_INNER_M": RING_INNER_M,
                          "RING_OUTER_M": RING_OUTER_M, "MIN_DECK": MIN_DECK, "MIN_RING": MIN_RING, "TOLERANCE": TOLERANCE},
                      "counts": counts}
    (OUT / "timeseries.json").write_text(json.dumps(result))
    print("\n===== MOVEMENT HISTORY SUMMARY =====")
    print(f"  bridges with a history: {len(result['bridges'])} · road stretches with a history: {len(result['roads'])}")
    for k, v in counts.items():
        print(f"  {v:>5}  {k}")
    print(f"Files + full log: {OUT}")


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        log(f"FAILED: {e!r}")
        print(f"FAILED: {e}\nDetails: {OUT / 'timeseries_run.log'}")
