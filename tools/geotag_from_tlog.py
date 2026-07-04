#!/usr/bin/env python3
"""
DAMbv InfraDrone — Image Geotagging Tool (MAVLink tlog / CSV)
Category: Post-flight processing
Usage: python3 geotag_from_tlog.py <flight_log> <images_folder> [output_folder]
"""
import sys, os, glob, csv
from datetime import datetime, timezone
import piexif

def parse_csv_log(log_path):
    points = []
    with open(log_path) as f:
        reader = csv.DictReader(f)
        for row in reader:
            try:
                if "lat" in row and "lon" in row:
                    lat = float(row["lat"])
                    lon = float(row["lon"])
                    alt = float(row.get("alt", 0))
                    t = float(row.get("time", 0))
                    if abs(lat) > 0.001 and abs(lon) > 0.001:
                        points.append((t, lat, lon, alt))
            except: pass
    return points

def parse_tlog(log_path):
    try:
        from pymavlink import mavutil
        points = []
        mav = mavutil.mavlink_connection(log_path)
        while True:
            msg = mav.recv_match(type="GPS_RAW_INT", blocking=False)
            if msg is None:
                msg = mav.recv_match(blocking=False)
                if msg is None: break
                continue
            lat = msg.lat / 1e7
            lon = msg.lon / 1e7
            alt = msg.alt / 1000.0
            if abs(lat) > 0.001 and abs(lon) > 0.001:
                points.append((msg._timestamp, lat, lon, alt))
        return points
    except Exception as e:
        print(f"tlog parse error: {e}")
        return []

def find_nearest_gps(timestamp, gps_points):
    if not gps_points: return None
    return min(gps_points, key=lambda p: abs(p[0] - timestamp))

def deg_to_dms_rational(deg):
    d = int(abs(deg))
    m = int((abs(deg) - d) * 60)
    s = round(((abs(deg) - d) * 60 - m) * 60 * 1000)
    return ((d, 1), (m, 1), (s, 1000))

def write_gps_exif(img_path, lat, lon, alt, out_path):
    try:
        exif_dict = {"GPS": {
            piexif.GPSIFD.GPSLatitudeRef: b"N" if lat >= 0 else b"S",
            piexif.GPSIFD.GPSLatitude: deg_to_dms_rational(lat),
            piexif.GPSIFD.GPSLongitudeRef: b"E" if lon >= 0 else b"W",
            piexif.GPSIFD.GPSLongitude: deg_to_dms_rational(lon),
            piexif.GPSIFD.GPSAltitudeRef: 0,
            piexif.GPSIFD.GPSAltitude: (int(alt * 100), 100),
        }}
        exif_bytes = piexif.dump(exif_dict)
        import shutil
        shutil.copy2(img_path, out_path)
        piexif.insert(exif_bytes, out_path)
        return True
    except Exception as e:
        print(f"  EXIF write error: {e}")
        return False

def parse_image_timestamp(filename):
    parts = os.path.splitext(os.path.basename(filename))[0].split("_")
    if len(parts) >= 3:
        try:
            dt = datetime.strptime(parts[1] + parts[2], "%y%m%d%H%M%S")
            return dt.replace(tzinfo=timezone.utc).timestamp()
        except: pass
    return None

def geotag(log_path, images_folder, output_folder=None):
    print(f"Loading GPS track from: {log_path}")
    ext = os.path.splitext(log_path)[1].lower()
    gps_points = parse_tlog(log_path) if ext == ".tlog" else parse_csv_log(log_path)

    if not gps_points:
        print("ERROR: No valid GPS points found in log.")
        sys.exit(1)

    print(f"Found {len(gps_points)} GPS points")

    if output_folder is None:
        output_folder = os.path.join(images_folder, "geotagged")
    os.makedirs(output_folder, exist_ok=True)

    images = []
    for pattern in ["*.TIF","*.tif","*.JPG","*.jpg","*.jpeg"]:
        images.extend(glob.glob(os.path.join(images_folder, pattern)))
    images.sort()

    print(f"Processing {len(images)} images...")
    success = 0
    for img_path in images:
        fname = os.path.basename(img_path)
        img_ts = parse_image_timestamp(img_path)
        if img_ts is None:
            print(f"  SKIP {fname} - cannot parse timestamp")
            continue
        nearest = find_nearest_gps(img_ts, gps_points)
        offset = abs(nearest[0] - img_ts)
        out_path = os.path.join(output_folder, fname)
        ok = write_gps_exif(img_path, nearest[1], nearest[2], nearest[3], out_path)
        if ok:
            success += 1
            print(f"  OK {fname} lat={nearest[1]:.6f} lon={nearest[2]:.6f} offset={offset:.1f}s")

    print(f"Done: {success}/{len(images)} geotagged -> {output_folder}")

if __name__ == "__main__":
    if len(sys.argv) < 3:
        print("Usage: python3 geotag_from_tlog.py <log.tlog|.csv> <images_folder> [output_folder]")
        sys.exit(1)
    geotag(sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else None)
