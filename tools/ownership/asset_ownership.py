#!/usr/bin/env python3
"""
asset_ownership.py  (version 3) - classify Groningen infrastructure by managing authority (beheerder).

Open sources (no login):
  NWB  = Nationaal Wegenbestand (National Road Database) road segments -> road-manager type field
  BGT  = Basisregistratie Grootschalige Topografie (Large-Scale Topography Register) bridge decks
         -> "bronhouder" (source-keeper) code: G=gemeente, P=provincie, W=waterschap, L=national body
  Bestuurlijke Gebieden (Kadaster administrative boundaries) -> exact province outline + code-to-name lookup

Changes in version 3 (after the second live run on 29 Sep 2026):
  - province name lookup fixed (version 2 picked a country-name column -> "Provincie Nederland")
  - bridges registered under a municipality that no longer exists get their current successor
    by location (which current municipality the bridge lies in); the old code stays visible
  - water-board codes (W....) get names from the BGT water-board areas, with an agreement check
  - separate water-board bridge service removed (its whole API returns 404; BGT already covers them)
  - new field code_status: "current" or "former municipality - successor by location"

Changes from version 1 (after the first live run on 29 Sep 2026):
  - NWB download is tiled, because PDOK rejects paging beyond 50,000 records (HTTP 400 at startIndex 51000)
  - water-board bridge collection name is discovered from the server instead of guessed
  - everything is filtered to the real Province of Groningen boundary (if that service is reachable)
  - owner codes get readable names; codes of municipalities that no longer exist are flagged, not hidden
  - 400/404 errors are no longer retried pointlessly

Output (WGS84 GeoJSON) in ownership_out/: roads_by_owner, bgt_bridges_by_owner
Added fields per feature: owner_type, owner_code, owner_name, owner_source (+ code_status on bridges)
Full detail -> ownership_out/run.log ; console -> short summary only.
"""
import json
import logging
import time
from collections import Counter
from pathlib import Path

import geopandas as gpd
import requests

OUT = Path("ownership_out")
OUT.mkdir(exist_ok=True)
logging.basicConfig(filename=OUT / "run.log", filemode="w", level=logging.INFO,
                    format="%(asctime)s %(message)s")
log = logging.info

BBOX_RD = (200000, 535000, 280000, 620000)   # EPSG:28992 (Dutch "RD New", metres)
BBOX_WGS = (6.15, 52.84, 7.25, 53.56)        # lon_min, lat_min, lon_max, lat_max

NWB_WFS = "https://service.pdok.nl/rws/nationaal-wegenbestand-wegen/wfs/v1_0"
NWB_LAYER = "nationaal-wegenbestand-wegen:wegvakken"
BGT_API = "https://api.pdok.nl/lv/bgt/ogc/v1"
BESTUUR_API = "https://api.pdok.nl/kadaster/bestuurlijkegebieden/ogc/v1"
WFS_MAX_START = 50000   # observed PDOK limit

NWB_TYPES = {"R": "Rijk (national)", "P": "Provincie (province)", "G": "Gemeente (municipality)",
             "W": "Waterschap (water board)", "T": "Overig (other)"}
BGT_PREFIX = {"G": "Gemeente (municipality)", "P": "Provincie (province)",
              "W": "Waterschap (water board)", "L": "Landelijk (national body)"}

SESSION = requests.Session()
SUMMARY = []
PROVINCE = None      # GeoDataFrame with the Groningen outline (EPSG:4326), or None
GEM_NAMES = {}       # "0014" -> "Groningen"
PROV_NAMES = {}      # "0020" -> "Groningen"
GEM = None           # GeoDataFrame of current municipalities: gem_name + geometry


# ------------------------------------------------------------------ helpers
def get_json(url, params=None):
    for attempt in range(4):
        try:
            r = SESSION.get(url, params=params, timeout=90)
        except requests.RequestException as e:
            log(f"network retry {attempt} {url}: {e}")
            time.sleep(2 * (attempt + 1))
            continue
        if r.status_code in (400, 404):
            log(f"HTTP {r.status_code} (not retried) {r.url} body={r.text[:300]}")
            raise RuntimeError(f"HTTP {r.status_code} from {url} (details in run.log)")
        if r.status_code >= 500:
            log(f"server retry {attempt} HTTP {r.status_code} {r.url}")
            time.sleep(2 * (attempt + 1))
            continue
        r.raise_for_status()
        return r.json()
    raise RuntimeError(f"Request kept failing: {url} (details in run.log)")


def collections(base):
    return [c["id"] for c in get_json(f"{base}/collections", {"f": "json"})["collections"]]


def find_key(columns, *needles):
    for c in columns:
        if all(n in c.lower() for n in needles):
            return c
    return None


def progress(label, n):
    print(f"\r  {label}: {n} downloaded", end="", flush=True)


def ogc_items(base, collection, label):
    url = f"{base}/collections/{collection}/items"
    params = {"f": "json", "bbox": ",".join(map(str, BBOX_WGS)), "limit": 1000}
    feats = []
    while url:
        data = get_json(url, params)
        params = None
        feats += data.get("features", [])
        progress(label, len(feats))
        url = next((l["href"] for l in data.get("links", []) if l.get("rel") == "next"), None)
    print()
    log(f"{label}: {len(feats)} features")
    return feats


def pick(columns, exact, contains, exclude=()):
    """Prefer an exact column name; otherwise first column containing `contains` but none of `exclude`."""
    cols = list(columns)
    for e in exact:
        if e in cols:
            return e
    for c in cols:
        low = c.lower()
        if contains in low and not any(x in low for x in exclude):
            return c
    return None


def locate_in(gdf, areas, cols):
    """For each feature, the attributes of the area polygon its representative point lies within."""
    pts = gpd.GeoDataFrame(geometry=gdf.geometry.representative_point(), index=gdf.index, crs=gdf.crs)
    j = gpd.sjoin(pts, areas[cols + ["geometry"]], predicate="within", how="left")
    j = j[~j.index.duplicated()]
    return j[cols]


def last4(code):
    digits = "".join(ch for ch in str(code) if ch.isdigit())
    return digits[-4:].zfill(4) if digits else ""


def union(gdf):
    g = gdf.geometry
    return g.union_all() if hasattr(g, "union_all") else g.unary_union


def keep_in_province(gdf, label):
    """Keep features that touch the Province of Groningen. gdf must be EPSG:4326."""
    if PROVINCE is None:
        return gdf
    before = len(gdf)
    joined = gpd.sjoin(gdf, PROVINCE[["geometry"]], predicate="intersects", how="inner")
    joined = joined[~joined.index.duplicated()].drop(columns=["index_right"], errors="ignore")
    log(f"{label}: kept {len(joined)} of {before} inside province")
    SUMMARY.append(f"{label}: {before - len(joined)} features outside the province removed")
    return joined


def summarise(title, gdf):
    SUMMARY.append(f"{title}: {len(gdf)} features in province")
    for k, v in Counter(gdf["owner_type"]).most_common():
        SUMMARY.append(f"    {v:>7}  {k}")
    top = Counter(gdf["owner_name"].dropna().astype(str)).most_common(6)
    if top:
        SUMMARY.append("    top owners: " + "; ".join(f"{n} ({c})" for n, c in top))


# ------------------------------------------------------------------ 0. boundaries + names
def boundaries():
    global PROVINCE, GEM
    try:
        cols = collections(BESTUUR_API)
    except Exception as e:
        log(f"boundary service unavailable: {e}")
        SUMMARY.append("Province boundary: service unreachable -> results NOT filtered to province")
        return
    log(f"Boundary collections: {cols}")
    prov_c = next((c for c in cols if "provincie" in c.lower()), None)
    gem_c = next((c for c in cols if "gemeente" in c.lower()), None)

    if prov_c:
        prov = gpd.GeoDataFrame.from_features(ogc_items(BESTUUR_API, prov_c, "Province outlines"), crs="EPSG:4326")
        code_k = pick(prov.columns, ["code", "statcode", "provinciecode"], "code", ("land",))
        name_k = pick(prov.columns, ["naam", "statnaam", "provincienaam"], "naam", ("land",))
        log(f"province columns {list(prov.columns)} -> code='{code_k}' name='{name_k}'")
        if code_k and name_k:
            PROV_NAMES.update({last4(c): n for c, n in zip(prov[code_k], prov[name_k])})
        is_gron = prov.drop(columns="geometry").apply(
            lambda r: any(str(v).strip().lower() == "groningen" for v in r.values), axis=1)
        if is_gron.any():
            PROVINCE = gpd.GeoDataFrame(geometry=[union(prov[is_gron])], crs="EPSG:4326")
            SUMMARY.append("Province boundary: loaded (results filtered to Province of Groningen)")
        else:
            SUMMARY.append("Province boundary: Groningen not found -> NOT filtered (see run.log)")
            log(f"province columns: {list(prov.columns)}")

    if gem_c:
        gem = gpd.GeoDataFrame.from_features(ogc_items(BESTUUR_API, gem_c, "Municipality outlines"), crs="EPSG:4326")
        code_k = pick(gem.columns, ["code", "statcode", "gemeentecode"], "code", ("land", "provincie"))
        name_k = pick(gem.columns, ["naam", "statnaam", "gemeentenaam"], "naam", ("land", "provincie"))
        log(f"municipality columns {list(gem.columns)} -> code='{code_k}' name='{name_k}'")
        if code_k and name_k:
            GEM_NAMES.update({last4(c): n for c, n in zip(gem[code_k], gem[name_k])})
            GEM = gem.rename(columns={name_k: "gem_name"})[["gem_name", "geometry"]]
        log(f"municipality names loaded: {len(GEM_NAMES)}")


# ------------------------------------------------------------------ 1. NWB roads (tiled)
class TooMany(Exception):
    pass


def nwb_tile(b, seen, counter):
    feats, start, page = [], 0, 1000
    while True:
        if start >= WFS_MAX_START:
            raise TooMany
        params = {"service": "WFS", "version": "2.0.0", "request": "GetFeature",
                  "typeNames": NWB_LAYER, "outputFormat": "application/json",
                  "srsName": "EPSG:28992", "bbox": ",".join(f"{v:.0f}" for v in b) + ",EPSG:28992",
                  "count": page, "startIndex": start}
        batch = get_json(NWB_WFS, params).get("features", [])
        feats += batch
        if len(batch) < page:
            break
        start += page
    new = 0
    for f in feats:   # the same road can cross two tiles -> de-duplicate
        key = f.get("id") or json.dumps(f.get("properties", {}), sort_keys=True)
        if key not in seen:
            seen[key] = f
            new += 1
    counter[0] += new
    progress("NWB road segments", counter[0])
    log(f"NWB tile {b}: {len(feats)} fetched, {new} new")


def nwb_area(b, seen, counter):
    try:
        nwb_tile(b, seen, counter)
    except TooMany:
        x0, y0, x1, y1 = b
        xm, ym = (x0 + x1) / 2, (y0 + y1) / 2
        log(f"NWB tile {b} over {WFS_MAX_START}, splitting in 4")
        for sub in ((x0, y0, xm, ym), (xm, y0, x1, ym), (x0, ym, xm, y1), (xm, ym, x1, y1)):
            nwb_area(sub, seen, counter)


def roads():
    x0, y0, x1, y1 = BBOX_RD
    n = 3   # start with a 3x3 grid; tiles that are still too big split themselves
    dx, dy = (x1 - x0) / n, (y1 - y0) / n
    seen, counter = {}, [0]
    for i in range(n):
        for j in range(n):
            nwb_area((x0 + i * dx, y0 + j * dy, x0 + (i + 1) * dx, y0 + (j + 1) * dy), seen, counter)
    print()
    if not seen:
        SUMMARY.append("NWB roads: 0 features returned - check run.log")
        return
    gdf = gpd.GeoDataFrame.from_features(list(seen.values()), crs="EPSG:28992").to_crs(4326)
    type_key = find_key(gdf.columns, "wegbeh", "srt")
    name_key = find_key(gdf.columns, "wegbeh", "naam")
    if not type_key:
        log(f"NWB columns: {list(gdf.columns)}")
        raise RuntimeError("NWB road-manager type field not found; column list in run.log")
    log(f"NWB fields: type='{type_key}' name='{name_key}'; raw types {Counter(gdf[type_key].astype(str))}")
    codes = gdf[type_key].astype(str).str.strip().str.upper()
    gdf["owner_type"] = codes.map(lambda v: NWB_TYPES.get(v, f"Unknown ({v})"))
    gdf["owner_code"] = codes
    gdf["owner_name"] = gdf[name_key] if name_key else None
    gdf["owner_source"] = "NWB wegbeheerder"
    gdf = keep_in_province(gdf, "NWB roads")
    gdf.to_file(OUT / "roads_by_owner.geojson", driver="GeoJSON")
    summarise("NWB roads", gdf)


# ------------------------------------------------------------------ 2. BGT bridges
def bgt_name(code):
    prefix, num = code[:1], last4(code)
    if prefix == "G":
        return GEM_NAMES.get(num, "FORMER") if GEM_NAMES else code
    if prefix == "P":
        return f"Provincie {PROV_NAMES[num]}" if num in PROV_NAMES else code
    return code   # W and L codes kept as-is (no open lookup table used here)


def bgt_bridges():
    cols = collections(BGT_API)
    wanted = [c for c in cols if "overbrugging" in c.lower()]
    if not wanted:
        log(f"BGT collections: {cols}")
        SUMMARY.append("BGT bridges: no 'overbrugging' collection found - list in run.log")
        return
    feats = []
    for c in wanted:
        feats += ogc_items(BGT_API, c, f"BGT {c}")
    gdf = gpd.GeoDataFrame.from_features(feats, crs="EPSG:4326")
    key = find_key(gdf.columns, "bronhouder")
    if not key:
        log(f"BGT columns: {list(gdf.columns)}")
        raise RuntimeError("BGT 'bronhouder' field not found; column list in run.log")
    codes = gdf[key].astype(str).str.strip().str.upper()
    gdf["owner_type"] = codes.str[:1].map(lambda p: BGT_PREFIX.get(p, f"Unknown ({p})"))
    gdf["owner_code"] = codes
    gdf["owner_name"] = codes.map(bgt_name)
    gdf["owner_source"] = "BGT bronhouder"
    gdf = keep_in_province(gdf, "BGT bridges")
    gdf["code_status"] = "current"

    # municipalities that no longer exist -> current successor = municipality the bridge lies in
    former = gdf["owner_name"].astype(str) == "FORMER"
    if former.any():
        if GEM is not None:
            loc = locate_in(gdf[former], GEM, ["gem_name"])["gem_name"].fillna("outside current municipalities")
            gdf.loc[former, "owner_name"] = loc + " (register still uses former code " + gdf.loc[former, "owner_code"] + ")"
            gdf.loc[former, "code_status"] = "former municipality - successor by location"
            pairs = Counter(zip(gdf.loc[former, "owner_code"], loc))
            SUMMARY.append(f"BGT bridges: {int(former.sum())} under a former municipality code -> successor by location:")
            SUMMARY.append("    " + "; ".join(f"{c}->{n} ({k})" for (c, n), k in sorted(pairs.items())))
        else:
            gdf.loc[former, "owner_name"] = "Former municipality code " + gdf.loc[former, "owner_code"]

    waterboard_names(gdf)
    log(f"BGT codes in province: {Counter(gdf['owner_code'])}")
    gdf.to_file(OUT / "bgt_bridges_by_owner.geojson", driver="GeoJSON")
    summarise("BGT bridges", gdf)


# ------------------------------------------------------------------ 3. Water-board names
def waterboard_names(gdf):
    """Name W.... codes by which BGT water-board area the bridges lie in (majority vote per code)."""
    w = gdf["owner_code"].str[:1] == "W"
    if not w.any():
        return
    try:
        wb = gpd.GeoDataFrame.from_features(ogc_items(BGT_API, "waterschap", "BGT water-board areas"), crs="EPSG:4326")
    except Exception as e:
        log(f"water-board areas unavailable: {e}")
        SUMMARY.append("Water-board names: area layer unavailable -> W codes left as codes")
        return
    name_k = pick(wb.columns, ["naam", "waterschapnaam", "officielenaam"], "naam", ())
    log(f"BGT waterschap columns {list(wb.columns)} -> name='{name_k}'")
    if not name_k or wb.empty:
        SUMMARY.append("Water-board names: no name field in area layer -> W codes left as codes (see run.log)")
        return
    wb = wb.rename(columns={name_k: "wb_name"})
    located = locate_in(gdf[w], wb, ["wb_name"])["wb_name"]
    tmp = gdf.loc[w, ["owner_code"]].assign(wb_name=located).dropna()
    for code, grp in tmp.groupby("owner_code"):
        top_name, top_n = grp["wb_name"].value_counts().index[0], grp["wb_name"].value_counts().iloc[0]
        share = 100 * top_n / len(grp)
        gdf.loc[gdf["owner_code"] == code, "owner_name"] = f"{top_name} ({code})"
        SUMMARY.append(f"Water-board {code} -> {top_name}: {share:.0f}% of its {len(grp)} bridges lie in that area")


if __name__ == "__main__":
    for step in (boundaries, roads, bgt_bridges):
        try:
            step()
        except Exception as e:
            SUMMARY.append(f"{step.__name__}: FAILED - {e}")
            log(f"{step.__name__} failed: {e!r}")
    print("\n===== OWNERSHIP SUMMARY =====")
    print("\n".join(SUMMARY))
    print(f"Files + full log: {OUT.resolve()}")
