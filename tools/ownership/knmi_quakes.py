#!/usr/bin/env python3
"""
knmi_quakes.py - earthquakes in and around the Province of Groningen from the official KNMI catalogue.

Source: KNMI (Royal Netherlands Meteorological Institute) FDSN event web service, https://rdsa.knmi.nl/fdsnws/event/1/
(the successor of KNMI's deprecated catalogue download). Text format, all event types, since 1986.
Output: screening_out/knmi_quakes.csv + knmi_quakes_meta.json (query URL, time, checksum). Console: short summary.
"""
import csv
import hashlib
import io
import json
from datetime import datetime, timezone
from pathlib import Path

import requests

HERE = Path(__file__).resolve().parent
OUT = HERE / "screening_out"
OUT.mkdir(parents=True, exist_ok=True)
URL = "https://rdsa.knmi.nl/fdsnws/event/1/query"
PARAMS = {"format": "text", "minlatitude": 52.75, "maxlatitude": 53.65, "minlongitude": 6.0, "maxlongitude": 7.4,
          "starttime": "1986-01-01", "orderby": "time-asc", "nodata": "404"}


def main():
    r = requests.get(URL, params=PARAMS, timeout=180)
    if r.status_code == 404:
        rows = []
    else:
        r.raise_for_status()
        lines = [l for l in r.text.splitlines() if l.strip()]
        header = [h.strip().lstrip("#").strip() for h in lines[0].split("|")]
        def col(*names):
            low = [h.lower() for h in header]
            for n in names:
                if n in low:
                    return low.index(n)
            return None
        ci = {k: col(*v) for k, v in {"id": ("eventid",), "time": ("time",), "lat": ("latitude",), "lon": ("longitude",),
                                      "depth": ("depth/km", "depth"), "mag": ("magnitude",), "magtype": ("magtype",),
                                      "type": ("eventtype",), "place": ("eventlocationname",)}.items()}
        rows = []
        for l in lines[1:]:
            p = [x.strip() for x in l.split("|")]
            g = lambda k: p[ci[k]] if ci[k] is not None and ci[k] < len(p) else ""
            rows.append({"event_id": g("id"), "time": g("time"), "lat": g("lat"), "lon": g("lon"), "depth_km": g("depth"),
                         "magnitude": g("mag"), "mag_type": g("magtype"), "type": g("type"), "location": g("place")})
    buf = io.StringIO()
    w = csv.DictWriter(buf, fieldnames=["event_id", "time", "lat", "lon", "depth_km", "magnitude", "mag_type", "type", "location"])
    w.writeheader(); w.writerows(rows)
    data = buf.getvalue().encode()
    (OUT / "knmi_quakes.csv").write_bytes(data)
    meta = {"source": "KNMI FDSN event web service", "url": r.url, "retrieved_utc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
            "count": len(rows), "sha256": hashlib.sha256(data).hexdigest()}
    (OUT / "knmi_quakes_meta.json").write_text(json.dumps(meta, indent=2))
    print("\n===== KNMI EARTHQUAKES =====")
    print(f"  {len(rows)} earthquakes since 1986 in and around the province")
    mags = [(float(x["magnitude"]), x) for x in rows if x["magnitude"]]
    if mags:
        m, x = max(mags, key=lambda t: t[0])
        print(f"  largest: magnitude {m:.1f} on {x['time'][:10]} ({x['location']})")
        print(f"  most recent: {rows[-1]['time'][:10]}, magnitude {rows[-1]['magnitude']} ({rows[-1]['location']})")
    print(f"Saved: {OUT / 'knmi_quakes.csv'}")


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        print(f"FAILED: {e}")
