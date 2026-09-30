#!/usr/bin/env python3
"""
egms_screen.py - Layer 1 of the Groningen Asset Monitor: satellite ground-motion screening of every bridge.

What it does (real data only, nothing estimated or invented):
  1. Logs in to the official EGMS (European Ground Motion Service) API with your personal CLMS
     (Copernicus Land Monitoring Service) token. API calls follow Copernicus' own example notebook:
     github.com/copernicus-land/egms-api
  2. Finds the LATEST EGMS release, downloads the Ortho (level L3) vertical ("UP") and east-west ("EAST")
     products covering Groningen. Ortho = measurement points resampled to a 100 m grid, velocities in mm/year.
  3. For every bridge from the ownership run (BGT = Large-Scale Topography Register, with its registered owner):
       local  = median velocity of EGMS points within LOCAL_RADIUS_M of the bridge
       ground = median velocity in a ring LOCAL_RADIUS_M .. RING_RADIUS_M around it (its surroundings)
       diff   = local - ground   -> "is it moving differently from its surroundings?"
  4. Flags bridges whose |diff| reaches the review / priority thresholds, for targeted drone inspection.
  5. Writes an audit manifest: exact EGMS files + versions + SHA-256 checksums, input file checksums,
     thresholds used, run time. Anyone can later re-run and verify every flag.

IMPORTANT: the thresholds below are PLACEHOLDER SETTINGS, not facts. They must be agreed with the
province's engineers before any result is used for decisions. They are recorded in every manifest.
Limitation: Ortho is a 100 m grid, so this screens the ground AT and AROUND a bridge, not the deck itself.
Structure-level screening (EGMS Calibrated point data) is the next step.

Lives in ~/infradrone-desktop/tools/ownership/ and is started by the Asset Monitor tab in InfraDrone Desktop.
Inputs : secrets/token.jwt, ownership_out/bgt_bridges_by_owner.geojson   (next to this script)
Outputs: screening_out/bridges_screening.geojson, bridges_ranked.csv, audit_manifest.json, run.log
Cache  : egms_cache/ (downloaded satellite files, re-used if unchanged)
Console: short summary only.
"""
import argparse
import hashlib
import io
import json
import logging
import re
import time
import zipfile
from datetime import datetime, timezone
from pathlib import Path

import geopandas as gpd
import jwt
import pandas as pd
import requests

SCRIPT_VERSION = "egms_screen 0.4"
API = "https://egms.land.copernicus.eu/insar-api/archive"
GRONINGEN_BBOX = [[6.15, 52.84], [7.25, 53.56]]     # lon/lat; API limit is 5 degrees

# ---- screening settings: PLACEHOLDERS to be agreed with the province (recorded in the manifest) ----
LOCAL_RADIUS_M = 150      # Ortho grid is 100 m -> 150 m catches the nearest grid points
RING_RADIUS_M = 1000      # surroundings used as the reference
MIN_LOCAL_POINTS = 1
MIN_RING_POINTS = 10
REVIEW_MM_YR = 2.0        # |local - surroundings| at or above this -> "Review"
PRIORITY_MM_YR = 4.0      # at or above this -> "Priority"

HERE = Path(__file__).resolve().parent
EGMS_DIR = HERE / "egms_cache"
OUT = HERE / "screening_out"
for d in (EGMS_DIR, OUT):
    d.mkdir(parents=True, exist_ok=True)
log = logging.info


def setup_logging(name):
    logging.basicConfig(filename=OUT / name, filemode="w", level=logging.INFO, format="%(asctime)s %(message)s", force=True)


# road names from the NWB (National Road Database) - copied to the ranked list so people recognise the road
ROAD_FIELDS = {"road_name": ("stt_naam",), "road_number": ("wegnummer",), "municipality": ("gme_naam",)}


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# ------------------------------------------------------------------ EGMS access (per official notebook)
def access_token(token_file):
    key = json.load(open(token_file, "rb"))
    claims = {"iss": key["client_id"], "sub": key["user_id"], "aud": key["token_uri"],
              "iat": int(time.time()), "exp": int(time.time() + 3600)}
    grant = jwt.encode(claims, key["private_key"].encode("utf-8"), algorithm="RS256")
    r = requests.post(key["token_uri"],
                      headers={"Accept": "application/json",
                               "Content-Type": "application/x-www-form-urlencoded"},
                      data={"grant_type": "urn:ietf:params:oauth:grant-type:jwt-bearer", "assertion": grant},
                      timeout=60)
    tok = r.json().get("access_token")
    if not tok:
        log(f"token response: {r.status_code} {r.text[:500]}")
        raise RuntimeError("Could not get an EGMS access token - check secrets/token.jwt (details in run.log)")
    return tok


def latest_release(headers):
    rel = requests.get(f"{API}/releases", headers=headers, timeout=60).json()
    log(f"available releases: {rel}")
    if not isinstance(rel, list) or not rel:
        raise RuntimeError(f"Unexpected releases answer (see run.log)")
    def end_year(r):
        years = [int(y) for y in re.findall(r"\d{4}", str(r))]
        return max(years) if years else 0
    return max(rel, key=end_year)


def download_ortho(headers, release):
    query = {"id": None, "bbox": GRONINGEN_BBOX, "levels": ["L3"], "releases": [release]}
    res = requests.post(f"{API}/search", headers=headers, data=json.dumps(query), timeout=120).json()
    log(f"search result: status={res.get('status')} hits={len(res.get('hits', []))} msg={res.get('message')}")
    hits = [h for h in res.get("hits", []) if h.get("productType") in ("ORTHO-UP", "ORTHO-EAST")]
    if not hits:
        raise RuntimeError("No EGMS Ortho products found for Groningen (see run.log)")
    files = []
    for h in hits:
        target = EGMS_DIR / h["filename"]
        if target.exists() and target.stat().st_size == h.get("filesize", target.stat().st_size):
            log(f"cached {target.name}")
        else:
            url = f"{API}/download/{h['filename']}?id={res['id']}"
            print(f"  downloading {h['filename']} ({h.get('filesize', 0) / 1e6:.0f} MB)")
            with requests.get(url, headers=headers, stream=True, timeout=600) as r:
                r.raise_for_status()
                with open(target, "wb") as f:
                    for chunk in r.iter_content(1 << 20):
                        f.write(chunk)
        files.append({"filename": h["filename"], "productType": h["productType"],
                      "version": h.get("version"), "release": h.get("release"),
                      "sha256": sha256(target), "path": str(target)})
    return files


# ------------------------------------------------------------------ read EGMS points
def read_points(path):
    """Read an Ortho CSV (inside a zip or plain), keeping only coordinates + mean velocity."""
    def load(fobj):
        header = pd.read_csv(fobj, nrows=0).columns
        return header
    p = Path(path)
    if zipfile.is_zipfile(p):
        z = zipfile.ZipFile(p)
        csvs = [n for n in z.namelist() if n.lower().endswith(".csv")]
        if not csvs:
            raise RuntimeError(f"No CSV inside {p.name}; contents: {z.namelist()[:10]}")
        frames = []
        for n in csvs:
            cols = pd.read_csv(io.TextIOWrapper(z.open(n)), nrows=0).columns
            use = pick_cols(cols, p.name)
            frames.append(pd.read_csv(io.TextIOWrapper(z.open(n)), usecols=list(use.values())).rename(
                columns={v: k for k, v in use.items()}))
        df = pd.concat(frames, ignore_index=True)
    else:
        cols = pd.read_csv(p, nrows=0).columns
        use = pick_cols(cols, p.name)
        df = pd.read_csv(p, usecols=list(use.values())).rename(columns={v: k for k, v in use.items()})
    return df


def pick_cols(cols, name):
    low = {c.lower(): c for c in cols}
    x = low.get("easting") or next((c for l, c in low.items() if "east" in l and "vel" not in l), None)
    y = low.get("northing") or next((c for l, c in low.items() if "north" in l and "vel" not in l), None)
    v = low.get("mean_velocity") or next((c for l, c in low.items() if "velocity" in l and "std" not in l), None)
    log(f"{name}: columns (first 15) {list(cols)[:15]} -> x={x} y={y} v={v}")
    if not (x and y and v):
        raise RuntimeError(f"Could not find coordinate/velocity columns in {name} (see run.log)")
    return {"x": x, "y": y, "vel": v}


def points_gdf(files, product):
    frames = [read_points(f["path"]) for f in files if f["productType"] == product]
    if not frames:
        return None
    df = pd.concat(frames, ignore_index=True)
    g = gpd.GeoDataFrame(df[["vel"]], geometry=gpd.points_from_xy(df["x"], df["y"]), crs="EPSG:3035")
    return g.to_crs(28992)   # Dutch RD New, metres


# ------------------------------------------------------------------ screening
def screen(assets, pts, label):
    """local / surroundings / difference for each asset (assets and pts in EPSG:28992)."""
    base = assets[["geometry"]].copy()
    local = gpd.sjoin(pts, gpd.GeoDataFrame(geometry=base.buffer(LOCAL_RADIUS_M), crs=base.crs),
                      predicate="within", how="inner")
    ring_geom = base.buffer(RING_RADIUS_M).difference(base.buffer(LOCAL_RADIUS_M))
    ring = gpd.sjoin(pts, gpd.GeoDataFrame(geometry=ring_geom, crs=base.crs),
                     predicate="within", how="inner")
    out = pd.DataFrame(index=assets.index)
    out[f"{label}_local"] = local.groupby("index_right")["vel"].median()
    out[f"{label}_local_n"] = local.groupby("index_right").size()
    out[f"{label}_ground"] = ring.groupby("index_right")["vel"].median()
    out[f"{label}_ground_n"] = ring.groupby("index_right").size()
    ok = (out[f"{label}_local_n"] >= MIN_LOCAL_POINTS) & (out[f"{label}_ground_n"] >= MIN_RING_POINTS)
    out[f"{label}_diff"] = (out[f"{label}_local"] - out[f"{label}_ground"]).where(ok)
    return out


def classify(row):
    vals = [abs(row[c]) for c in ("up_diff", "east_diff") if c in row and pd.notna(row[c])]
    if not vals:
        return "No satellite data"
    m = max(vals)
    return "Priority" if m >= PRIORITY_MM_YR else "Review" if m >= REVIEW_MM_YR else "No unusual movement"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--token", default=str(HERE / "secrets" / "token.jwt"))
    ap.add_argument("--bridges", default=str(HERE / "ownership_out" / "bgt_bridges_by_owner.geojson"))
    ap.add_argument("--assets", choices=["bridges", "roads"], default="bridges",
                    help="roads = provincial road segments from the NWB (National Road Database)")
    ap.add_argument("--roads", default=str(HERE / "ownership_out" / "roads_by_owner.geojson"))
    a = ap.parse_args()
    started = datetime.now(timezone.utc)
    roads = a.assets == "roads"
    global LOCAL_RADIUS_M, RING_RADIUS_M
    if roads:
        # a road is a line: points within 100 m of the centre line = "at the road" (Ortho grid is 100 m)
        LOCAL_RADIUS_M, RING_RADIUS_M = 100, 1000
    setup_logging("roads_run.log" if roads else "run.log")
    prefix = "roads_" if roads else "bridges_"
    input_path = a.roads if roads else a.bridges

    headers = {"Authorization": f"Bearer {access_token(a.token)}", "Accept": "application/json"}
    release = latest_release(headers)
    print(f"  EGMS release: {release}")
    files = download_ortho(headers, release)

    bridges = gpd.read_file(input_path).to_crs(28992)
    if roads:
        bridges = bridges[bridges["owner_type"].astype(str).str.startswith("Provincie")].copy()
        if bridges.empty:
            raise RuntimeError("No provincial road segments found in the road register file")
    bridges = bridges.reset_index(drop=True)
    print(f"  {'provincial road segments' if roads else 'bridges'} loaded: {len(bridges)}")
    results = bridges.copy()
    for product, label in (("ORTHO-UP", "up"), ("ORTHO-EAST", "east")):
        pts = points_gdf(files, product)
        if pts is None:
            log(f"no {product} file")
            continue
        log(f"{product}: {len(pts)} points")
        results = results.join(screen(bridges, pts, label))

    results["screening"] = results.apply(classify, axis=1)
    results["egms_release"] = release
    results["screened_utc"] = started.isoformat(timespec="seconds")

    # stable asset identifier from the register (BGT local id) + location for the inspection task list
    id_col = next((c for c in results.columns if c.lower() == "wvk_id"), None) if roads else None
    id_col = id_col or next((c for c in results.columns if "lokaal" in c.lower() and "id" in c.lower()), None) \
        or next((c for c in results.columns if c.lower() in ("identificatie", "gml_id", "id")), None)
    for new, names in ROAD_FIELDS.items():
        src = next((c for c in results.columns if c.lower() in names), None)
        if src:
            results[new] = results[src].astype(str).replace({"None": "", "nan": ""})
    log(f"asset id column: {id_col}")
    results["asset_id"] = results[id_col].astype(str) if id_col else ("row_" + results.index.astype(str))
    rp = results.to_crs(4326).geometry.representative_point()
    results["lon"] = rp.x.round(6)
    results["lat"] = rp.y.round(6)

    results.to_crs(4326).to_file(OUT / f"{prefix}screening.geojson", driver="GeoJSON")
    keep = [c for c in ["asset_id", "lon", "lat", "owner_type", "owner_name", "owner_code", "road_name", "road_number",
                        "municipality", "screening", "up_local", "up_ground",
                        "up_diff", "east_diff", "up_local_n", "up_ground_n"] if c in results.columns]
    ranked = results.assign(max_abs=results[[c for c in ("up_diff", "east_diff") if c in results]].abs().max(axis=1))
    ranked.sort_values("max_abs", ascending=False)[keep + ["max_abs"]].to_csv(OUT / f"{prefix}ranked.csv")

    manifest = {
        "script": SCRIPT_VERSION, "run_utc": started.isoformat(timespec="seconds"),
        "egms_api": API, "egms_release": release, "egms_files": files,
        "asset_id_field": id_col or "row number (no register id column found)",
        ("assets_input" if roads else "bridges_input"): {"path": input_path, "sha256": sha256(input_path), "count": len(bridges),
                                                          "kind": "provincial road segments (NWB)" if roads else "bridge deck parts (BGT)"},
        "settings_placeholders_to_agree_with_province": {
            "LOCAL_RADIUS_M": LOCAL_RADIUS_M, "RING_RADIUS_M": RING_RADIUS_M,
            "MIN_LOCAL_POINTS": MIN_LOCAL_POINTS, "MIN_RING_POINTS": MIN_RING_POINTS,
            "REVIEW_MM_YR": REVIEW_MM_YR, "PRIORITY_MM_YR": PRIORITY_MM_YR},
        "result_counts": results["screening"].value_counts().to_dict(),
    }
    (OUT / ("roads_audit_manifest.json" if roads else "audit_manifest.json")).write_text(json.dumps(manifest, indent=2, default=str))

    print("\n===== SCREENING SUMMARY =====")
    for k, v in results["screening"].value_counts().items():
        print(f"  {v:>6}  {k}")
    top = ranked.sort_values("max_abs", ascending=False).head(5)
    print("  top 5 by movement difference (mm/year, vs. surroundings):")
    for _, r in top.iterrows():
        if pd.notna(r["max_abs"]):
            up, ew = r.get("up_diff"), r.get("east_diff")
            direction = "sideways" if pd.notna(ew) and (pd.isna(up) or abs(ew) >= abs(up)) else "vertical"
            kind = str(r.get("owner_type", "")).split(" ")[0]
            print(f"    {r['max_abs']:5.1f} {direction:<9} {kind} · {r.get('owner_name', '')}")
    print(f"Files, audit manifest + full log: {OUT}")


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        log(f"FAILED: {e!r}")
        print(f"FAILED: {e}\nDetails: {OUT / 'run.log'}")
