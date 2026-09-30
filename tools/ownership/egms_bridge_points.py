#!/usr/bin/env python3
"""
egms_bridge_points.py - bridge-level (structure) screening with EGMS Calibrated (level L2b) measurement points.

Why: the Ortho product (egms_screen.py) is a 100 m grid - it screens the ground AROUND a bridge.
EGMS Calibrated (L2b) contains the individual radar measurement points. Bridges are strong radar
reflectors, so there are often points ON the deck. This script compares points on each bridge deck
with points on the ground around it, per satellite track (viewing direction).

Real data only. API calls follow Copernicus' official example (github.com/copernicus-land/egms-api).

Per bridge deck (BGT polygon from the ownership run) and per satellite track:
  deck  = points within DECK_TOLERANCE_M of the deck polygon (allowance for point position uncertainty)
  ring  = points between RING_INNER_M and RING_OUTER_M from the deck (its surroundings)
  diff  = median velocity on deck - median velocity in ring   (mm/year, along the satellite's line of sight)
A track "qualifies" if it has at least MIN_DECK_POINTS on the deck and MIN_RING_POINTS in the ring.
Status uses the largest |diff| over qualifying tracks; tracks_flagging counts how many look directions agree.

Velocities are LINE OF SIGHT (towards/away from the satellite), not vertical. Negative = moving away
from the satellite (usually sinking). Thresholds are PLACEHOLDERS to agree with the province's engineers.

Usage (started by the Asset Monitor tab):
  python3 egms_bridge_points.py --plan    -> only lists the files and total download size
  python3 egms_bridge_points.py           -> downloads (cached) and screens every bridge deck
Outputs in screening_out/: bridge_points_screening.csv, bridge_points_measurements.geojson,
                           bridge_points_details.json (evidence per flagged bridge: outline, points, per-track numbers),
                           bridge_points_audit.json, bridge_points_plan.json, bridge_points_run.log
"""
import argparse
import hashlib
import json
import logging
import time
import zipfile
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

import geopandas as gpd
import jwt
import numpy as np
import pandas as pd
import requests
import shapely
from pyproj import Transformer
from shapely.geometry import Polygon, box, shape

SCRIPT_VERSION = "egms_bridge_points 0.4"
API = "https://egms.land.copernicus.eu/insar-api/archive"

# ---- screening settings: PLACEHOLDERS to be agreed with the province (recorded in the audit file) ----
DECK_TOLERANCE_M = 3.0
ROAD_TOLERANCE_M = 6.0    # roads: NWB is a centre line; half a provincial road width plus point position uncertainty
RING_INNER_M = 20.0
RING_OUTER_M = 300.0
MIN_DECK_POINTS = 3
MIN_RING_POINTS = 10
REVIEW_MM_YR = 2.0
PRIORITY_MM_YR = 4.0
CHUNK_ROWS = 400_000
MAX_RING_POINTS_SAVED = 400   # per flagged bridge, for the evidence picture (random sample, fixed seed)

HERE = Path(__file__).resolve().parent
CACHE = HERE / "egms_cache" / "l2b"
OUT = HERE / "screening_out"
for d in (CACHE, OUT):
    d.mkdir(parents=True, exist_ok=True)
PREFIX = "bridge_points"          # becomes "road_points" with --assets roads
ROAD_FIELDS = {"road_name": ("stt_naam",), "road_number": ("wegnummer",), "municipality": ("gme_naam",)}


def setup_logging():
    logging.basicConfig(filename=OUT / f"{PREFIX}_run.log", filemode="w", level=logging.INFO,
                        format="%(asctime)s %(message)s", force=True)
log = logging.info


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# ------------------------------------------------------------------ EGMS access (official notebook pattern)
def access_token(token_file):
    key = json.load(open(token_file, "rb"))
    claims = {"iss": key["client_id"], "sub": key["user_id"], "aud": key["token_uri"],
              "iat": int(time.time()), "exp": int(time.time() + 3600)}
    grant = jwt.encode(claims, key["private_key"].encode("utf-8"), algorithm="RS256")
    r = requests.post(key["token_uri"], headers={"Accept": "application/json",
                      "Content-Type": "application/x-www-form-urlencoded"},
                      data={"grant_type": "urn:ietf:params:oauth:grant-type:jwt-bearer", "assertion": grant}, timeout=60)
    tok = r.json().get("access_token")
    if not tok:
        log(f"token response {r.status_code}: {r.text[:500]}")
        raise RuntimeError("Could not get an EGMS access token - check secrets/token.jwt")
    return tok


def latest_release(headers):
    import re
    rel = requests.get(f"{API}/releases", headers=headers, timeout=60).json()
    log(f"releases: {rel}")
    return max(rel, key=lambda r: max([int(y) for y in re.findall(r"\d{4}", str(r))] or [0]))


def hit_polygon(h):
    """Coverage polygon of a dataset (falls back to its bounding box)."""
    p = h.get("poly")
    try:
        if isinstance(p, dict):
            return shape(p)
        if isinstance(p, list) and p:
            ring = p[0] if isinstance(p[0][0], list) else p
            return Polygon([(float(a), float(b)) for a, b in ring])
    except Exception as e:
        log(f"poly parse failed for {h.get('filename')}: {e}")
    b = h.get("bbox")
    xs, ys = [c[0] for c in b], [c[1] for c in b]
    return box(min(xs), min(ys), max(xs), max(ys))


def track_key(h):
    d = str(h.get("direction", "?")).lower()
    o = h.get("relativeOrbit", h.get("relative_orbit", "?"))
    return f"{d} orbit {o}"


# ------------------------------------------------------------------ bridges
def load_bridges(path, roads=False):
    g = gpd.read_file(path)
    if roads:
        g = g[g["owner_type"].astype(str).str.startswith("Provincie")].copy()
        for new, names in ROAD_FIELDS.items():
            src = next((c for c in g.columns if c.lower() in names), None)
            if src:
                g[new] = g[src].astype(str).replace({"None": "", "nan": ""})
    id_col = (next((c for c in g.columns if c.lower() == "wvk_id"), None) if roads else None) \
        or next((c for c in g.columns if "lokaal" in c.lower() and "id" in c.lower()), None) \
        or next((c for c in g.columns if c.lower() in ("identificatie", "gml_id", "id")), None)
    g["asset_id"] = g[id_col].astype(str) if id_col else ("row_" + g.index.astype(str))
    rp = g.geometry.representative_point()
    g["lon"], g["lat"] = rp.x.round(6), rp.y.round(6)
    g = g.to_crs(28992).reset_index(drop=True)   # positional index == bridge number used below
    log(f"bridges: {len(g)}, id field {id_col}")
    return g, id_col


# ------------------------------------------------------------------ plan + download
def search(headers, release, bridges_wgs_hull):
    minx, miny, maxx, maxy = bridges_wgs_hull.bounds
    q = {"id": None, "bbox": [[minx, miny], [maxx, maxy]], "levels": ["L2B"], "releases": [release]}
    res = requests.post(f"{API}/search", headers=headers, data=json.dumps(q), timeout=180).json()
    log(f"search: status={res.get('status')} hits={len(res.get('hits', []))} msg={res.get('message')}")
    hits = [h for h in res.get("hits", []) if str(h.get("productType", "")).upper() == "CALIBRATED"]
    keep = [h for h in hits if hit_polygon(h).intersects(bridges_wgs_hull)]
    log(f"calibrated hits: {len(hits)}, covering bridges: {len(keep)}")
    return res.get("id"), keep


def download(headers, qid, hits):
    files = []
    for i, h in enumerate(hits, 1):
        target = CACHE / h["filename"]
        size = h.get("filesize")
        if not (target.exists() and size and target.stat().st_size == size):
            print(f"  downloading {i}/{len(hits)}: {h['filename']} ({(size or 0) / 1e6:.0f} MB)", flush=True)
            tmp = target.with_suffix(target.suffix + ".part")
            with requests.get(f"{API}/download/{h['filename']}?id={qid}", headers=headers, stream=True, timeout=900) as r:
                r.raise_for_status()
                with open(tmp, "wb") as f:
                    for chunk in r.iter_content(1 << 20):
                        f.write(chunk)
            tmp.replace(target)
        files.append({"filename": h["filename"], "track": track_key(h), "burstId": h.get("burstId"),
                      "version": h.get("version"), "release": h.get("release"), "path": str(target),
                      "sha256": sha256(target)})
    return files


# ------------------------------------------------------------------ read points near bridges
def pick(cols, *names, contains=None):
    low = {c.lower(): c for c in cols}
    for n in names:
        if n in low:
            return low[n]
    if contains:
        return next((c for l, c in low.items() if contains in l), None)
    return None


def points_near_bridges(f, decks, keep_tree, keep_bounds):
    """Return DataFrame of (bridge index, distance to deck, velocity, coherence, pid, x, y) for one burst file."""
    z = zipfile.ZipFile(f["path"])
    csvs = [n for n in z.namelist() if n.lower().endswith(".csv")]
    if not csvs:
        raise RuntimeError(f"no CSV inside {Path(f['path']).name}: {z.namelist()[:5]}")
    frames = []
    for name in csvs:
        cols = pd.read_csv(z.open(name), nrows=0).columns
        x = pick(cols, "easting"); y = pick(cols, "northing"); src = "EPSG:3035"
        if not (x and y):
            x = pick(cols, "longitude", "lon"); y = pick(cols, "latitude", "lat"); src = "EPSG:4326"
        v = pick(cols, "mean_velocity")
        coh = pick(cols, "temporal_coherence", contains="coherence")
        pid = pick(cols, "pid")
        log(f"{name}: x={x} y={y} src={src} v={v} coh={coh} pid={pid}")
        if not (x and y and v):
            raise RuntimeError(f"coordinate/velocity columns not found in {name} (see log)")
        use = [c for c in (x, y, v, coh, pid) if c]
        tf = Transformer.from_crs(src, "EPSG:28992", always_xy=True)
        for chunk in pd.read_csv(z.open(name), usecols=use, chunksize=CHUNK_ROWS):
            X, Y = tf.transform(chunk[x].to_numpy(), chunk[y].to_numpy())
            inside = (X >= keep_bounds[0]) & (X <= keep_bounds[2]) & (Y >= keep_bounds[1]) & (Y <= keep_bounds[3])
            if not inside.any():
                continue
            X, Y, sub = X[inside], Y[inside], chunk[inside]
            pts = shapely.points(X, Y)
            ip, ib = keep_tree.query(pts, predicate="intersects")
            if len(ip) == 0:
                continue
            dist = shapely.distance(pts[ip], decks[ib])
            ok = dist <= RING_OUTER_M
            frames.append(pd.DataFrame({
                "bridge": ib[ok], "dist": dist[ok], "vel": sub[v].to_numpy()[ip[ok]],
                "coh": sub[coh].to_numpy()[ip[ok]] if coh else np.nan,
                "pid": sub[pid].to_numpy()[ip[ok]] if pid else ip[ok],
                "x": X[ip[ok]], "y": Y[ip[ok]], "track": f["track"]}))
    return pd.concat(frames, ignore_index=True) if frames else None


# ------------------------------------------------------------------ main
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--plan", action="store_true", help="only list files and download size")
    ap.add_argument("--token", default=str(HERE / "secrets" / "token.jwt"))
    ap.add_argument("--bridges", default=str(HERE / "ownership_out" / "bgt_bridges_by_owner.geojson"))
    ap.add_argument("--assets", choices=["bridges", "roads"], default="bridges",
                    help="roads = provincial road segments: points within ROAD_TOLERANCE_M of the centre line count as 'on the road'")
    ap.add_argument("--roads", default=str(HERE / "ownership_out" / "roads_by_owner.geojson"))
    a = ap.parse_args()
    started = datetime.now(timezone.utc)
    global PREFIX, DECK_TOLERANCE_M
    roads = a.assets == "roads"
    if roads:
        PREFIX = "road_points"
        DECK_TOLERANCE_M = ROAD_TOLERANCE_M
        a.bridges = a.roads
    setup_logging()

    bridges, id_col = load_bridges(a.bridges, roads)
    hull = bridges.to_crs(4326).geometry.union_all().convex_hull if hasattr(bridges.geometry, "union_all") \
        else bridges.to_crs(4326).geometry.unary_union.convex_hull
    headers = {"Authorization": f"Bearer {access_token(a.token)}", "Accept": "application/json"}
    release = latest_release(headers)
    qid, hits = search(headers, release, hull)
    total = sum(h.get("filesize") or 0 for h in hits)
    cached = sum((h.get("filesize") or 0) for h in hits
                 if (CACHE / h["filename"]).exists() and (CACHE / h["filename"]).stat().st_size == h.get("filesize"))
    tracks = Counter(track_key(h) for h in hits)
    plan = {"release": release, "files": len(hits), "total_gb": round(total / 1e9, 2),
            "cached_gb": round(cached / 1e9, 2), "to_download_gb": round((total - cached) / 1e9, 2),
            "tracks": dict(tracks), "checked_utc": started.isoformat(timespec="seconds")}
    (OUT / f"{PREFIX}_plan.json").write_text(json.dumps(plan, indent=2))

    if a.plan:
        print("\n===== DOWNLOAD PLAN =====")
        print(f"  EGMS release {release}, Calibrated (L2b) point data")
        print(f"  {len(hits)} files from {len(tracks)} satellite tracks: " + ", ".join(f"{k} ({n})" for k, n in tracks.items()))
        print(f"  total {plan['total_gb']} GB · already downloaded {plan['cached_gb']} GB · still to download {plan['to_download_gb']} GB")
        return

    files = download(headers, qid, hits)
    out, deck, ring, t = screen(bridges, files, release)
    write_outputs(out, deck, files, release, a.bridges, id_col, len(bridges), started)
    write_details(out, deck, ring, t, bridges)


def screen(bridges, files, release):
    # search area: every bridge deck buffered by the outer ring radius
    decks = bridges.geometry.values
    keep = shapely.buffer(decks, RING_OUTER_M)
    keep_tree = shapely.STRtree(keep)
    keep_bounds = shapely.total_bounds(keep)

    parts = []
    for i, f in enumerate(files, 1):
        print(f"\r  reading points {i}/{len(files)}", end="", flush=True)
        df = points_near_bridges(f, decks, keep_tree, keep_bounds)
        if df is not None:
            parts.append(df)
            log(f"{Path(f['path']).name}: {len(df)} point-bridge pairs")
    print()
    if not parts:
        raise RuntimeError("No measurement points found near any bridge (see log)")
    pts = pd.concat(parts, ignore_index=True).drop_duplicates(["track", "pid", "bridge"])
    log(f"total point-bridge pairs: {len(pts)}")

    deck = pts[pts["dist"] <= DECK_TOLERANCE_M]
    ring = pts[(pts["dist"] >= RING_INNER_M) & (pts["dist"] <= RING_OUTER_M)]
    d = deck.groupby(["bridge", "track"]).agg(deck_n=("vel", "size"), deck_med=("vel", "median"), deck_coh=("coh", "mean"))
    r = ring.groupby(["bridge", "track"]).agg(ring_n=("vel", "size"), ring_med=("vel", "median"))
    t = d.join(r, how="left").reset_index()
    t["qualifies"] = (t["deck_n"] >= MIN_DECK_POINTS) & (t["ring_n"].fillna(0) >= MIN_RING_POINTS)
    t["diff"] = (t["deck_med"] - t["ring_med"]).where(t["qualifies"])
    t["absdiff"] = t["diff"].abs()

    rows = []
    for b_idx, g in t.groupby("bridge"):
        q = g[g["qualifies"]]
        rec = {"bridge": b_idx, "bp_deck_points_total": int(g["deck_n"].sum())}
        if q.empty:
            rec["bp_status"] = "Too few points on structure"
        else:
            best = q.loc[q["absdiff"].idxmax()]
            m = best["absdiff"]
            rec.update({
                "bp_status": "Priority" if m >= PRIORITY_MM_YR else "Review" if m >= REVIEW_MM_YR else "No unusual movement",
                "bp_diff_los": round(best["diff"], 2), "bp_deck_median": round(best["deck_med"], 2),
                "bp_ring_median": round(best["ring_med"], 2), "bp_deck_n": int(best["deck_n"]),
                "bp_ring_n": int(best["ring_n"]), "bp_coherence": round(best["deck_coh"], 2) if pd.notna(best["deck_coh"]) else None,
                "bp_track": best["track"], "bp_tracks_qualifying": int(len(q)),
                "bp_tracks_flagging": int((q["absdiff"] >= REVIEW_MM_YR).sum())})
        rows.append(rec)
    res = pd.DataFrame(rows).set_index("bridge")

    out = bridges[["asset_id", "lon", "lat"] + [c for c in ("owner_type", "owner_name", "owner_code", "road_name", "road_number", "municipality") if c in bridges.columns]].copy()
    out = out.join(res)
    out["bp_status"] = out["bp_status"].fillna("No points on structure")
    out["egms_release"] = release
    return out, deck, ring, t


def write_details(out, deck, ring, t, bridges):
    """Evidence per flagged bridge, in metres relative to the deck centre (RD New):
    deck outline, radar points on the deck, a sample of points around it, and the numbers per satellite track."""
    flagged = out.index[out["bp_status"].isin(["Priority", "Review"])]
    fl = set(flagged)
    dg = {k: g for k, g in deck[deck["bridge"].isin(fl)].groupby("bridge")}
    rg = {k: g for k, g in ring[ring["bridge"].isin(fl)].groupby("bridge")}
    tg = {k: g for k, g in t[t["bridge"].isin(fl)].groupby("bridge")}
    details = {}
    to_ll = Transformer.from_crs("EPSG:28992", "EPSG:4326", always_xy=True)
    for idx in flagged:
        geom = bridges.geometry.iloc[idx]
        c = geom.centroid
        # for a road (line): distance along the centre line, so the app can show WHERE along the stretch it moves
        line = geom if geom.geom_type in ("LineString", "MultiLineString") else None
        if line is not None and line.geom_type == "MultiLineString":
            line = shapely.line_merge(line)
            if line.geom_type != "LineString":
                line = max(line.geoms, key=lambda g: g.length)
        polys = list(geom.geoms) if hasattr(geom, "geoms") else [geom]
        outline = [[[round(x - c.x, 1), round(y - c.y, 1)] for x, y in (p.simplify(0.3).exterior.coords if hasattr(p, "exterior") else p.simplify(0.3).coords)]
                   for p in polys]
        def pts(df, n=None):
            if df is None or len(df) == 0:
                return []
            if n and len(df) > n:
                df = df.sample(n, random_state=0)
            lon, lat = to_ll.transform(df["x"].to_numpy(), df["y"].to_numpy())
            along = (shapely.line_locate_point(line, shapely.points(df["x"].to_numpy(), df["y"].to_numpy()))
                     if line is not None else [None] * len(df))
            # [dx, dy, velocity, track, lon, lat, metres along the road (roads only)]
            return [[round(x - c.x, 1), round(y - c.y, 1), round(float(v), 2), tr, round(float(lo), 6), round(float(la), 6),
                     (round(float(al), 1) if al is not None else None)]
                    for x, y, v, tr, lo, la, al in zip(df["x"], df["y"], df["vel"], df["track"], lon, lat, along)]
        tracks = []
        for _, r in tg.get(idx, pd.DataFrame()).iterrows():
            tracks.append({"track": r["track"], "deck_n": int(r["deck_n"]), "deck_med": round(float(r["deck_med"]), 2),
                           "ring_n": int(r["ring_n"]) if pd.notna(r["ring_n"]) else 0,
                           "ring_med": round(float(r["ring_med"]), 2) if pd.notna(r["ring_med"]) else None,
                           "diff": round(float(r["diff"]), 2) if pd.notna(r["diff"]) else None,
                           "qualifies": bool(r["qualifies"])})
        outline_ll = []
        for ring_xy in outline:
            xs = [c.x + q[0] for q in ring_xy]; ys = [c.y + q[1] for q in ring_xy]
            lo, la = to_ll.transform(xs, ys)
            outline_ll.append([[round(float(a), 6), round(float(b), 6)] for a, b in zip(lo, la)])
        details[str(out.at[idx, "asset_id"])] = {
            "outline": outline, "outline_lonlat": outline_ll,
            "length_m": round(float(line.length), 1) if line is not None else None,
            "deck_points": pts(dg.get(idx)),
            "ring_points": pts(rg.get(idx), MAX_RING_POINTS_SAVED),
            "ring_points_total": int(len(rg[idx])) if idx in rg else 0, "tracks": tracks}
    (OUT / f"{PREFIX}_details.json").write_text(json.dumps(details))
    log(f"details written for {len(details)} flagged bridges")


def write_outputs(out, deck, files, release, bridges_path, id_col, n_bridges, started):
    out.drop(columns="geometry", errors="ignore").to_csv(OUT / f"{PREFIX}_screening.csv", index=False)

    # the actual measurement points on flagged decks -> shown on the map, verifiable one by one
    flagged_idx = set(out.index[out["bp_status"].isin(["Priority", "Review"])])
    fp = deck[deck["bridge"].isin(flagged_idx)].copy()
    if len(fp):
        g = gpd.GeoDataFrame(fp[["vel", "coh", "track", "pid"]].assign(asset_id=out.loc[fp["bridge"], "asset_id"].to_numpy()),
                             geometry=gpd.points_from_xy(fp["x"], fp["y"]), crs=28992).to_crs(4326)
        g["pid"] = g["pid"].astype(str)
        g.to_file(OUT / f"{PREFIX}_measurements.geojson", driver="GeoJSON")

    counts = out["bp_status"].value_counts().to_dict()
    audit = {
        "script": SCRIPT_VERSION, "run_utc": started.isoformat(timespec="seconds"), "egms_api": API,
        "egms_release": release, "product": "EGMS Calibrated (L2b), line-of-sight velocities",
        "egms_files": files, "bridges_input": {"path": bridges_path, "sha256": sha256(bridges_path), "count": n_bridges,
                                               "asset_id_field": id_col},
        "settings_placeholders_to_agree_with_province": {
            "DECK_TOLERANCE_M": DECK_TOLERANCE_M, "RING_INNER_M": RING_INNER_M, "RING_OUTER_M": RING_OUTER_M,
            "MIN_DECK_POINTS": MIN_DECK_POINTS, "MIN_RING_POINTS": MIN_RING_POINTS,
            "REVIEW_MM_YR": REVIEW_MM_YR, "PRIORITY_MM_YR": PRIORITY_MM_YR},
        "result_counts": counts}
    (OUT / f"{PREFIX}_audit.json").write_text(json.dumps(audit, indent=2, default=str))

    print("\n===== " + ("ON-ROAD SUMMARY (provincial roads)" if PREFIX == "road_points" else "STRUCTURE-LEVEL SUMMARY") + " =====")
    for k, v in sorted(counts.items(), key=lambda kv: -kv[1]):
        print(f"  {v:>6}  {k}")
    both = int((out["bp_tracks_flagging"].fillna(0) >= 2).sum())
    print(f"  flagged by 2 or more satellite viewing directions: {both}")
    print(f"Files + audit + full log: {OUT}")


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        log(f"FAILED: {e!r}")
        print(f"FAILED: {e}\nDetails: {OUT / 'bridge_points_run.log'}")
