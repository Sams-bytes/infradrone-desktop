#!/usr/bin/env python3
"""
DAMbv InfraDrone — one-off preprocessing script.
Streams through the national NDW traffic-signs GeoJSON (1.18GB) and
writes out only the features within Groningen province's real
bounding box, to a small file the app can actually load directly.
Uses ijson (streaming parser) so the full 1.18GB file is never held
in memory at once.

Usage: python3 filter_signs_to_groningen.py <input.geojson> <output.geojson>
"""
import sys
import json
import ijson

# Groningen province bounding box, slightly wider than the exact CROW
# pavement-data extent confirmed earlier today (lon 6.19-7.18, lat
# 52.85-53.41), to be safe rather than clip real edge-of-province signs.
MIN_LON, MAX_LON = 6.0, 7.3
MIN_LAT, MAX_LAT = 52.8, 53.5

def main():
    in_path, out_path = sys.argv[1], sys.argv[2]
    kept = 0
    total = 0

    with open(in_path, 'rb') as f, open(out_path, 'w') as out:
        out.write('{"type":"FeatureCollection","features":[')
        first = True
        for feature in ijson.items(f, 'features.item', use_float=True):
            total += 1
            if total % 200000 == 0:
                print(f"  processed {total:,}, kept {kept:,}...", flush=True)
            geom = feature.get('geometry')
            if not geom or geom.get('type') != 'Point':
                continue
            lon, lat = geom['coordinates'][0], geom['coordinates'][1]
            if MIN_LON <= lon <= MAX_LON and MIN_LAT <= lat <= MAX_LAT:
                if not first:
                    out.write(',')
                out.write(json.dumps(feature))
                first = False
                kept += 1
        out.write(']}')

    print(f"Done. Processed {total:,} total signs, kept {kept:,} within Groningen province.")

if __name__ == "__main__":
    main()
