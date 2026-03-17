#!/usr/bin/env python3
"""
Step 2: Extract Reflectivity Data from One Sweep
=================================================
Reads NEXRAD L2 V06 file, extracts REF data from a chosen sweep,
produces a polar numpy array [n_azimuths × n_gates] of physical dBZ values.

Output:
  - ref_polar: float32 array, shape (720, 1832) for sweep 0
  - NaN for gates with no data (raw=0) or range-folded (raw=1)
  - Azimuth angles array (degrees, 0=north, clockwise)
  - Range array (meters from radar)
"""

import struct
import bz2
import numpy as np

FILENAME = "/mnt/user-data/uploads/KFWS20250304_110319_V06"
TARGET_SWEEP = 0       # Sweep index (0 = first, lowest elevation, has REF with 1832 gates)
TARGET_MOMENT = "REF"  # Reflectivity

# ═══════════════════════════════════════════════════════════════════
# 1. DECOMPRESS FILE
# ═══════════════════════════════════════════════════════════════════
with open(FILENAME, "rb") as f:
    raw = f.read()

offset = 24
blocks = []
while offset + 4 <= len(raw):
    bs = abs(struct.unpack(">i", raw[offset:offset + 4])[0])
    if bs == 0:
        break
    try:
        blocks.append(bz2.decompress(raw[offset + 4:offset + 4 + bs]))
    except Exception:
        pass
    offset += 4 + bs

# ═══════════════════════════════════════════════════════════════════
# 2. PARSE MSG31 RADIALS, SEGMENT INTO SWEEPS
# ═══════════════════════════════════════════════════════════════════
CTM = 12
MSG_HDR = 16

radials_all = []

for block in blocks:
    ro = 0
    while ro + CTM + MSG_HDR < len(block):
        msh = struct.unpack(">H", block[ro + 12:ro + 14])[0]
        mt = block[ro + 15]
        rs = (CTM + msh * 2) if mt == 31 else 2432
        if ro + rs > len(block):
            break
        if mt != 31:
            ro += rs
            continue

        ds = ro + CTM + MSG_HDR
        d = block

        az_angle      = struct.unpack(">f", d[ds+12:ds+16])[0]
        radial_status = d[ds + 21]
        elev_number   = d[ds + 22]
        elev_angle    = struct.unpack(">f", d[ds+24:ds+28])[0]
        db_count      = struct.unpack(">H", d[ds+30:ds+32])[0]

        # Parse data moment pointers
        moments = {}
        for bi in range(min(db_count, 12)):
            ptr_off = ds + 32 + bi * 4
            if ptr_off + 4 > ro + rs:
                break
            ptr = struct.unpack(">I", d[ptr_off:ptr_off + 4])[0]
            abs_ptr = ds + ptr
            if abs_ptr + 28 > ro + rs:
                continue
            if chr(d[abs_ptr]) != 'D':
                continue

            mname      = d[abs_ptr+1:abs_ptr+4].decode("ascii", errors="replace").strip("\x00").strip()
            n_gates    = struct.unpack(">H", d[abs_ptr+8:abs_ptr+10])[0]
            first_gate = struct.unpack(">H", d[abs_ptr+10:abs_ptr+12])[0]
            gate_size  = struct.unpack(">H", d[abs_ptr+12:abs_ptr+14])[0]
            word_size  = d[abs_ptr + 19]
            scale      = struct.unpack(">f", d[abs_ptr+20:abs_ptr+24])[0]
            offset_val = struct.unpack(">f", d[abs_ptr+24:abs_ptr+28])[0]
            data_start = abs_ptr + 28

            moments[mname] = {
                'n_gates': n_gates, 'first_gate': first_gate,
                'gate_size': gate_size, 'word_size': word_size,
                'scale': scale, 'offset': offset_val,
                'data_start': data_start,
            }

        radials_all.append({
            'az': az_angle, 'el': elev_angle, 'el_num': elev_number,
            'status': radial_status, 'moments': moments,
            'block_data': d, 'record_end': ro + rs,
        })

        ro += rs

# Segment into sweeps
sweeps = []
current = []
for r in radials_all:
    if r['status'] in (0, 3) and current:
        sweeps.append(current)
        current = []
    current.append(r)
if current:
    sweeps.append(current)

print(f"Total sweeps: {len(sweeps)}")
print(f"Target sweep: {TARGET_SWEEP}")

# ═══════════════════════════════════════════════════════════════════
# 3. EXTRACT POLAR DATA FROM TARGET SWEEP
# ═══════════════════════════════════════════════════════════════════
sweep = sweeps[TARGET_SWEEP]
n_radials = len(sweep)

# Get gate geometry from first radial
first_moment = sweep[0]['moments'][TARGET_MOMENT]
n_gates    = first_moment['n_gates']
first_gate = first_moment['first_gate']  # meters
gate_size  = first_moment['gate_size']   # meters
word_size  = first_moment['word_size']   # bits
scale      = first_moment['scale']
offset_val = first_moment['offset']
mean_elev  = np.mean([r['el'] for r in sweep])

print(f"  Elevation   : {mean_elev:.2f}°")
print(f"  Radials     : {n_radials}")
print(f"  Gates       : {n_gates}")
print(f"  First gate  : {first_gate} m")
print(f"  Gate spacing: {gate_size} m")
print(f"  Max range   : {first_gate + n_gates * gate_size} m ({(first_gate + n_gates * gate_size)/1000:.1f} km)")
print(f"  Word size   : {word_size} bits")
print(f"  Scale/Offset: {scale} / {offset_val}")
print(f"  Moment      : {TARGET_MOMENT}")
print()

# Build polar array
azimuths = np.zeros(n_radials, dtype=np.float32)
ref_polar = np.full((n_radials, n_gates), np.nan, dtype=np.float32)

for i, rad in enumerate(sweep):
    azimuths[i] = rad['az']
    
    m = rad['moments'].get(TARGET_MOMENT)
    if m is None:
        continue
    
    d = rad['block_data']
    dstart = m['data_start']
    ng = m['n_gates']
    ws = m['word_size']
    
    # Read raw gate values
    if ws == 8:
        raw_gates = np.frombuffer(d[dstart:dstart + ng], dtype=np.uint8)
    elif ws == 16:
        raw_gates = np.frombuffer(d[dstart:dstart + ng * 2], dtype='>u2')  # big-endian uint16
    else:
        continue
    
    # Ensure we don't exceed array bounds
    usable = min(len(raw_gates), n_gates)
    
    # Convert to physical values: val = (raw - offset) / scale
    gate_floats = np.full(n_gates, np.nan, dtype=np.float32)
    valid = raw_gates[:usable] >= 2  # 0=no data, 1=range folded
    gate_floats[:usable] = np.where(valid, 
                                     (raw_gates[:usable].astype(np.float32) - offset_val) / scale,
                                     np.nan)
    ref_polar[i, :] = gate_floats

# Sort by azimuth for clean polar ordering
sort_idx = np.argsort(azimuths)
azimuths = azimuths[sort_idx]
ref_polar = ref_polar[sort_idx, :]

# Build range array (meters from radar)
ranges = first_gate + np.arange(n_gates, dtype=np.float32) * gate_size

# ═══════════════════════════════════════════════════════════════════
# 4. QUICK STATS & VALIDATION
# ═══════════════════════════════════════════════════════════════════
valid_mask = ~np.isnan(ref_polar)
n_valid = np.sum(valid_mask)
n_total = ref_polar.size

print("─── Polar Array Summary ───────────────────────────────────")
print(f"  Shape        : {ref_polar.shape}  (azimuths × gates)")
print(f"  Azimuth range: {azimuths[0]:.1f}° to {azimuths[-1]:.1f}°")
print(f"  Range extent : {ranges[0]/1000:.1f} km to {ranges[-1]/1000:.1f} km")
print(f"  Valid gates  : {n_valid:,} / {n_total:,} ({100*n_valid/n_total:.1f}%)")

if n_valid > 0:
    valid_vals = ref_polar[valid_mask]
    print(f"  REF min      : {np.nanmin(valid_vals):.1f} dBZ")
    print(f"  REF max      : {np.nanmax(valid_vals):.1f} dBZ")
    print(f"  REF mean     : {np.nanmean(valid_vals):.1f} dBZ")
    print(f"  REF median   : {np.nanmedian(valid_vals):.1f} dBZ")

# Show a few sample radials
print()
print("─── Sample Radials (first 5 non-NaN values) ───────────────")
for si in [0, n_radials//4, n_radials//2, 3*n_radials//4]:
    row = ref_polar[si, :]
    vals = row[~np.isnan(row)][:5]
    val_str = ", ".join(f"{v:.1f}" for v in vals) if len(vals) > 0 else "(all NaN)"
    print(f"  Az {azimuths[si]:6.1f}°: {val_str} ...")

# Save intermediate polar data for Step 3
np.savez("/home/claude/sweep_polar.npz",
         ref_polar=ref_polar, azimuths=azimuths, ranges=ranges,
         mean_elev=mean_elev, station="KFWS",
         first_gate=first_gate, gate_size=gate_size)

print()
print("  Saved: /home/claude/sweep_polar.npz")
print("  Ready for Step 3: Polar → Cartesian resampling + .bin output")
print("=" * 65)
