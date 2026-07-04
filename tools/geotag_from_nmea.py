#!/usr/bin/env python3
"""
DAMbv InfraDrone — Image Geotagging Tool (Parrot Sequoia NMEA GPS)
Category: Post-flight processing - Sequoia multispectral
Usage: python3 geotag_from_nmea.py <generated_nmea.txt> <images_folder> [output_folder]
Download nmea: adb pull /data/medias/generated_nmea.txt
"""
import sys, os, glob, math, shutil
from datetime import datetime, timezone
import piexif

def parse_nmea(nmea_path):
    points = []
    with open(nmea_path, errors="ignore") as f:
        for line in f:
            line = line.strip()
            is_gga = line.startswith("$GNGGA") or line.startswith("$GPGGA")
            is_rmc = line.startswith("$GNRMC") or line.startswith("$GPRMC")
            if not is_gga and not is_rmc: continue
            parts = line.split(",")
            try:
                if is_gga:
                    if len(parts) < 7 or parts[6] == "0" or parts[2] == "": continue
                    time_str = parts[1][:6]
                    lat_raw = float(parts[2])
                    lat = math.floor(lat_raw/100) + (lat_raw%100)/60
                    if parts[3] == "S": lat = -lat
                    lon_raw = float(parts[4])
                    lon = math.floor(lon_raw/100) + (lon_raw%100)/60
                    if parts[5] == "W": lon = -lon
                    alt = float(parts[9]) if parts[9] else 0
                    points.append((time_str, lat, lon, alt))
                elif is_rmc:
                    if len(parts) < 7 or parts[2] != "A" or parts[3] == "": continue
                    time_str = parts[1][:6]
                    lat_raw = float(parts[3])
                    lat = math.floor(lat_raw/100) + (lat_raw%100)/60
                    if parts[4] == "S": lat = -lat
                    lon_raw = float(parts[5])
                    lon = math.floor(lon_raw/100) + (lon_raw%100)/60
                    if parts[6] == "W": lon = -lon
                    points.append((time_str, lat, lon, 0))
            except: continue
    print(f"Parsed {len(points)} valid GPS fixes")
    return points

def time_to_secs(t):
    try: return int(t[:2])*3600 + int(t[2:4])*60 + int(t[4:6])
    except: return None

def deg_to_dms_rational(deg):
    d = int(abs(deg))
    m = int((abs(deg)-d)*60)
    s = round(((abs(deg)-d)*60-m)*60*1000)
    return ((d,1),(m,1),(s,1000))

def write_gps_exif(img_path, lat, lon, alt, out_path):
    try:
        exif_dict = {"GPS": {
            piexif.GPSIFD.GPSLatitudeRef: b"N" if lat>=0 else b"S",
            piexif.GPSIFD.GPSLatitude: deg_to_dms_rational(lat),
            piexif.GPSIFD.GPSLongitudeRef: b"E" if lon>=0 else b"W",
            piexif.GPSIFD.GPSLongitude: deg_to_dms_rational(lon),
            piexif.GPSIFD.GPSAltitudeRef: 0,
            piexif.GPSIFD.GPSAltitude: (int(abs(alt)*100),100),
        }}
        shutil.copy2(img_path, out_path)
        piexif.insert(piexif.dump(exif_dict), out_path)
        return True
    except Exception as e:
        print(f"  EXIF error: {e}")
        return False

def geotag(nmea_path, images_folder, output_folder=None):
    print(f"Loading Sequoia NMEA from: {nmea_path}")
    gps_points = parse_nmea(nmea_path)
    if not gps_points:
        print("ERROR: No valid GPS fixes. Check sunshine sensor calibration.")
        sys.exit(1)

    gps_secs = [(time_to_secs(t), lat, lon, alt) for t,lat,lon,alt in gps_points if time_to_secs(t)]

    if output_folder is None:
        output_folder = os.path.join(images_folder, "geotagged")
    os.makedirs(output_folder, exist_ok=True)

    images = []
    for p in ["*.TIF","*.tif","*.JPG","*.jpg"]:
        images.extend(glob.glob(os.path.join(images_folder, p)))
    images.sort()

    print(f"Processing {len(images)} images...")
    success = 0
    for img_path in images:
        fname = os.path.basename(img_path)
        parts = os.path.splitext(fname)[0].split("_")
        if len(parts) < 3:
            print(f"  SKIP {fname}"); continue
        img_secs = time_to_secs(parts[2])
        if img_secs is None:
            print(f"  SKIP {fname}"); continue
        nearest = min(gps_secs, key=lambda p: abs(p[0]-img_secs))
        offset = abs(nearest[0]-img_secs)
        out_path = os.path.join(output_folder, fname)
        if write_gps_exif(img_path, nearest[1], nearest[2], nearest[3], out_path):
            success += 1
            print(f"  OK {fname} -> {nearest[1]:.6f},{nearest[2]:.6f} offset={offset}s")

    print(f"Done: {success}/{len(images)} geotagged -> {output_folder}")

if __name__ == "__main__":
    if len(sys.argv) < 3:
        print("Usage: python3 geotag_from_nmea.py <nmea.txt> <images_folder> [output_folder]")
        sys.exit(1)
    geotag(sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else None)
