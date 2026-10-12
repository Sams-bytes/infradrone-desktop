import json
from shapely.geometry import shape, Point, LineString, Polygon
from shapely.ops import transform, substring
from pyproj import Transformer
to_m = Transformer.from_crs(4326, 32621, always_xy=True).transform
to_ll = Transformer.from_crs(32621, 4326, always_xy=True).transform
g = json.load(open('sur_adm0.geojson'))
SR_ll = shape(g['features'][0]['geometry'])
SR = transform(to_m, SR_ll)
BORDER = SR.exterior
def P(lat, lon): return transform(to_m, Point(lon, lat))
def inside(lat, lon): return SR.contains(P(lat, lon))
def dist_border(lat, lon): return BORDER.distance(P(lat, lon))
def ll(pt): x, y = to_ll(pt.x, pt.y); return (round(y, 6), round(x, 6))
RING = list(SR_ll.exterior.coords)
LAND_ll = LineString(RING[60:3251])
LAND = transform(to_m, LAND_ll)
def dist_land(lat, lon): return LAND.distance(P(lat, lon))

def border_piece(lat_lo, lat_hi, side):
    idx = range(2900, 3251) if side == 'east' else range(40, 400)
    pts = [RING[i] for i in idx if lat_lo <= RING[i][1] <= lat_hi]
    return transform(to_m, LineString(pts))

def patrol_track(lat_lo, lat_hi, side, offset_m, spacing_m=1500):
    seg = border_piece(lat_lo, lat_hi, side)
    for sgn in (1, -1):
        off = seg.offset_curve(sgn * offset_m, join_style=2)
        mid = off.interpolate(0.5, normalized=True)
        if SR.contains(mid): break
    off = off.simplify(120)
    n = max(2, int(off.length // spacing_m) + 1)
    wps = [off.interpolate(i / (n - 1), normalized=True) for i in range(n)]
    return seg, off, wps

def check_line(name, latlons, min_land_m):
    line = transform(to_m, LineString([(b, a) for a, b in latlons]))
    ok_inside = SR.buffer(1).contains(line)
    d = line.distance(LAND)
    print(f'{name:28s} inside={ok_inside} min_dist_to_land_border={d:7.0f} m  length={line.length/1000:6.1f} km  {"OK" if ok_inside and d >= min_land_m else "*** FAIL ***"}')
    return ok_inside and d >= min_land_m
