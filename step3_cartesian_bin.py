#!/usr/bin/env python3
"""
Step 3: Polar → Cartesian Resampling + .bin Output
====================================================
Takes the polar REF array from Step 2 and:
  1. Defines an 800×450 Cartesian grid inscribed within the radar circle
  2. Maps each pixel (x,y) → (range, azimuth) in polar space
  3. Bilinear-interpolates from the polar data
  4. Writes a flat float32 .bin file for C# consumption

Output .bin layout:
  - 360,000 IEEE 754 float32 values (little-endian, native for C#)
  - Row-major order: rows are Y (0=south, 449=north), columns are X (0=west, 799=east)
  - NaN (0x7FC00000) = no radar data at that pixel
  - Physical unit: dBZ (reflectivity)

C# usage:
  float[] data = new float[800 * 450];
  using (var fs = File.OpenRead("sweep_ref.bin"))
  using (var br = new BinaryReader(fs))
      for (int i = 0; i < data.Length; i++)
          data[i] = br.ReadSingle();
  // data[y * 800 + x] gives dBZ at pixel (x, y)
  // x: 0..799 = west..east,  y: 0..449 = south..north
  // Each pixel ≈ 1.0 km
"""

import numpy as np

# ═══════════════════════════════════════════════════════════════════
# 1. LOAD POLAR DATA FROM STEP 2
# ═══════════════════════════════════════════════════════════════════
npz = np.load("/home/claude/sweep_polar.npz")
ref_polar = npz['ref_polar']     # (720, 1832) float32, dBZ, NaN=no data
azimuths  = npz['azimuths']      # (720,) degrees, 0=north, clockwise
ranges    = npz['ranges']        # (1832,) meters from radar
station   = str(npz['station'])
mean_elev = float(npz['mean_elev'])

n_az, n_gates = ref_polar.shape
max_range = ranges[-1]           # ~459,875 m

print("=" * 65)
print("  Step 3: Polar → Cartesian + .bin")
print("=" * 65)
print(f"  Polar shape : {ref_polar.shape}")
print(f"  Max range   : {max_range/1000:.1f} km")

# ═══════════════════════════════════════════════════════════════════
# 2. DEFINE CARTESIAN GRID
# ═══════════════════════════════════════════════════════════════════
# Inscribe an 800×450 rectangle (16:9) within the radar circle.
# For circle radius R and aspect ratio W:H = 16:9:
#   sqrt((W/2)² + (H/2)²) = R
#   → W ≈ 801 km, H ≈ 451 km  for R ≈ 460 km
# Each pixel ≈ 1.0 km — clean and practical.

WIDTH  = 800
HEIGHT = 450
aspect = WIDTH / HEIGHT   # 16:9

# Compute inscribed extents so all corners are within max_range
half_h = max_range / np.sqrt(1 + aspect**2)    # half-height in meters
half_w = half_h * aspect                        # half-width in meters

print(f"  Grid size   : {WIDTH} × {HEIGHT}")
print(f"  Extent W×H  : {2*half_w/1000:.1f} × {2*half_h/1000:.1f} km")
print(f"  Pixel size  : {2*half_w/WIDTH/1000:.3f} × {2*half_h/HEIGHT/1000:.3f} km")
print()

# Pixel center coordinates (meters, radar at origin)
# X: west(-) to east(+),  Y: south(-) to north(+)
x_coords = np.linspace(-half_w, half_w, WIDTH, dtype=np.float64)
y_coords = np.linspace(-half_h, half_h, HEIGHT, dtype=np.float64)

# ═══════════════════════════════════════════════════════════════════
# 3. POLAR → CARTESIAN RESAMPLING (vectorized)
# ═══════════════════════════════════════════════════════════════════
# For each pixel (x, y), compute:
#   range = sqrt(x² + y²)
#   azimuth = atan2(x, y)   (north=0, clockwise, in degrees)
# Then bilinear interpolate from the polar grid.

print("  Resampling polar → cartesian ...")

# Build 2D meshgrid
xx, yy = np.meshgrid(x_coords, y_coords)  # both (HEIGHT, WIDTH)

# Polar coordinates for every pixel
pixel_range = np.sqrt(xx**2 + yy**2)
pixel_az    = np.degrees(np.arctan2(xx, yy)) % 360.0   # north=0, CW

# Map range to fractional gate index
gate_spacing = float(ranges[1] - ranges[0])
first_range  = float(ranges[0])
frac_gate = (pixel_range - first_range) / gate_spacing  # fractional index into gates

# Map azimuth to fractional azimuth index
# Azimuths are sorted, ~0.5° spacing, nearly uniform
# We need to handle the 360°→0° wrap
az_spacing = 360.0 / n_az
frac_az = pixel_az / az_spacing   # fractional index into azimuths

# Bilinear interpolation indices
az0 = np.floor(frac_az).astype(np.int32)
az1 = az0 + 1
g0  = np.floor(frac_gate).astype(np.int32)
g1  = g0 + 1

# Fractional parts
az_frac = (frac_az - az0).astype(np.float32)
g_frac  = (frac_gate - g0).astype(np.float32)

# Wrap azimuth indices (circular)
az0 = az0 % n_az
az1 = az1 % n_az

# Clamp gate indices
valid_mask = (g0 >= 0) & (g1 < n_gates) & (pixel_range <= max_range) & (pixel_range >= first_range)
g0 = np.clip(g0, 0, n_gates - 1)
g1 = np.clip(g1, 0, n_gates - 1)

# Sample 4 corners of bilinear cell
v00 = ref_polar[az0, g0]
v01 = ref_polar[az0, g1]
v10 = ref_polar[az1, g0]
v11 = ref_polar[az1, g1]

# Bilinear blend
top    = v00 * (1 - g_frac) + v01 * g_frac
bottom = v10 * (1 - g_frac) + v11 * g_frac
result = top * (1 - az_frac) + bottom * az_frac

# Apply validity mask — pixels outside range get NaN
result[~valid_mask] = np.nan

# Also propagate NaN from any corner being NaN (conservative)
any_nan = np.isnan(v00) | np.isnan(v01) | np.isnan(v10) | np.isnan(v11)
result[any_nan] = np.nan

cart_ref = result.astype(np.float32)  # (HEIGHT, WIDTH) = (450, 800)

print("  Done.")
print()

# ═══════════════════════════════════════════════════════════════════
# 4. STATISTICS
# ═══════════════════════════════════════════════════════════════════
valid = ~np.isnan(cart_ref)
n_valid = np.sum(valid)
n_total = cart_ref.size

print("─── Cartesian Grid Summary ────────────────────────────────")
print(f"  Shape        : {cart_ref.shape} (rows=Y, cols=X)")
print(f"  Valid pixels : {n_valid:,} / {n_total:,} ({100*n_valid/n_total:.1f}%)")
if n_valid > 0:
    print(f"  REF range    : {np.nanmin(cart_ref):.1f} to {np.nanmax(cart_ref):.1f} dBZ")
    print(f"  REF mean     : {np.nanmean(cart_ref):.1f} dBZ")
print()

# ═══════════════════════════════════════════════════════════════════
# 5. WRITE .BIN FILE
# ═══════════════════════════════════════════════════════════════════
# Flat float32, little-endian (native for C#), row-major
# Row 0 = bottom (south), row 449 = top (north)
# Col 0 = left (west), col 799 = right (east)

outpath = "/mnt/user-data/outputs/sweep_ref_800x450.bin"
cart_ref.tofile(outpath)

file_size = cart_ref.nbytes
print(f"─── Output File ───────────────────────────────────────────")
print(f"  Path         : {outpath}")
print(f"  Size         : {file_size:,} bytes ({file_size/1024:.1f} KB)")
print(f"  Layout       : {WIDTH}×{HEIGHT} float32 little-endian")
print(f"  Total floats : {WIDTH * HEIGHT:,}")
print(f"  Row order    : row 0 = south edge, row {HEIGHT-1} = north edge")
print(f"  Col order    : col 0 = west edge, col {WIDTH-1} = east edge")
print(f"  Pixel size   : ~{2*half_w/WIDTH/1000:.2f} × {2*half_h/HEIGHT/1000:.2f} km")
print(f"  NaN          : 0x7FC00000 = no radar data")
print()

# Quick sanity: print a small ascii heatmap
print("─── Quick ASCII Preview (50×28 downsampled) ───────────────")
preview_h, preview_w = 28, 50
step_y = HEIGHT // preview_h
step_x = WIDTH // preview_w
chars = " ·:;+=*#@"  # 9 levels

for py in range(preview_h - 1, -1, -1):
    row_idx = py * step_y
    line = ""
    for px in range(preview_w):
        col_idx = px * step_x
        val = cart_ref[row_idx, col_idx]
        if np.isnan(val):
            line += " "
        else:
            # Map dBZ range (-20..70) to char index 0..8
            ci = int(np.clip((val + 20) / 90 * 8, 0, 8))
            line += chars[ci]
    print(f"  |{line}|")

print()
print("=" * 65)
print("  Step 3 complete. .bin file ready for C#.")
print("=" * 65)
