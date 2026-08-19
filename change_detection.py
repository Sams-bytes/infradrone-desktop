#!/usr/bin/env python3
"""
DAMbv InfraDrone — Time-Lapse Change Detection
Compares two georeferenced orthomosaics of the same location, taken at
different times, and highlights physical change between them.

Usage: python3 change_detection.py <before.tif> <after.tif> <output_diff.png>
"""
import sys
import subprocess
import numpy as np
from PIL import Image
import rasterio

def main():
    before_path, after_path, out_path = sys.argv[1], sys.argv[2], sys.argv[3]

    # Read the "before" image's real extent/resolution/size from its own
    # georeferencing -- this becomes the common grid both images get
    # resampled onto, since two separate flights will never perfectly align.
    with rasterio.open(before_path) as src:
        bounds = src.bounds
        width, height = src.width, src.height
        crs = src.crs

    aligned_after = "/tmp/aligned_after.tif"
    subprocess.run([
        "gdalwarp", "-overwrite",
        "-t_srs", str(crs),
        "-te", str(bounds.left), str(bounds.bottom), str(bounds.right), str(bounds.top),
        "-ts", str(width), str(height),
        after_path, aligned_after
    ], check=True)

    before_img = np.array(Image.open(before_path).convert("RGB"), dtype=np.int16)
    after_img = np.array(Image.open(aligned_after).convert("RGB"), dtype=np.int16)

    if before_img.shape != after_img.shape:
        print(f"ERROR: shape mismatch after alignment: {before_img.shape} vs {after_img.shape}")
        sys.exit(1)

    # Per-pixel absolute difference, collapsed to a single-channel magnitude
    diff = np.abs(before_img - after_img).sum(axis=2)
    diff_norm = np.clip((diff / diff.max() * 255) if diff.max() > 0 else diff, 0, 255).astype(np.uint8)

    # Heatmap: red = high change, transparent/black = no change
    heatmap = np.zeros((*diff_norm.shape, 4), dtype=np.uint8)
    heatmap[..., 0] = 255  # red channel
    heatmap[..., 1] = 255 - diff_norm  # green drops as change increases
    heatmap[..., 3] = diff_norm  # alpha = change magnitude (transparent where nothing changed)

    Image.fromarray(heatmap, "RGBA").save(out_path)

    # Also save a normal PNG copy of the "before" image -- Avalonia's Bitmap
    # loader can't reliably read raw GeoTIFF (embedded georeferencing tags
    # confuse it), even though PIL reads it fine here. C# will display this
    # PNG copy instead of trying to load the original .tif directly.
    before_png_path = out_path.replace(".png", "_before.png")
    Image.open(before_path).convert("RGB").save(before_png_path)

    changed_pct = (diff_norm > 30).sum() / diff_norm.size * 100
    print(f"Change detection complete: {changed_pct:.1f}% of area shows significant change")
    print(f"Saved: {out_path}")
    print(f"BeforePng: {before_png_path}")

if __name__ == "__main__":
    main()
