/*
 * NexradConverter.cs — Pure C# NEXRAD L2 V06 → Cartesian .bin converter
 * =========================================================================
 * Converts a NEXRAD WSR-88D Level II V06 binary file into an 800×450
 * float32 .bin file suitable for the NexradRadar WPF chart project.
 *
 * Equivalent to running step2_extract.py + step3_cartesian_bin.py
 * but requires no Python or numpy installation.
 *
 * Dependencies:
 *   SharpCompress NuGet package — for BZ2 decompression
 *   (NEXRAD L2 files are BZ2-compressed internally)
 *
 * Usage:
 *   dotnet run -- KFWS20250304_110319_V06 sweep_ref_800x450.bin
 *
 * Or add as a console project to the solution and call from code.
 *
 * Output .bin layout (identical to Python pipeline output):
 *   800 × 450 IEEE 754 float32 values, little-endian, row-major
 *   Row 0 = south edge, Row 449 = north edge
 *   Col 0 = west edge,  Col 799 = east edge
 *   NaN (0x7FC00000) = no radar data at this pixel
 *   Physical unit: dBZ (reflectivity)
 *
 * C# reading:
 *   float[] data = new float[800 * 450];
 *   using var br = new BinaryReader(File.OpenRead("sweep_ref_800x450.bin"));
 *   for (int i = 0; i < data.Length; i++) data[i] = br.ReadSingle();
 *   // data[row * 800 + col] = dBZ at pixel (col, row)
 */

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using SharpCompress.Compressors.BZip2;

class NexradConverter
{
    // Output grid dimensions
    const int WIDTH  = 800;
    const int HEIGHT = 450;

    static void Main(string[] args)
    {
        string inputPath  = args.Length > 0 ? args[0] : "KFWS20250304_110319_V06";
        string outputPath = args.Length > 1 ? args[1] : "sweep_ref_800x450.bin";

        Console.WriteLine("=================================================================");
        Console.WriteLine("  NEXRAD L2 V06 → Cartesian .bin Converter (C#)");
        Console.WriteLine("=================================================================");
        Console.WriteLine($"  Input  : {inputPath}");
        Console.WriteLine($"  Output : {outputPath}");
        Console.WriteLine();

        // ---------------------------------------------------------------
        // Step 1 — Read and BZ2-decompress the NEXRAD file
        //
        // NEXRAD L2 V06 structure:
        //   Bytes 0-23: 24-byte file header (station ID, timestamp etc.)
        //   Then: repeated blocks of:
        //     4 bytes: signed int32 (big-endian) = compressed block size
        //     N bytes: BZ2-compressed data containing MSG31 radial records
        // ---------------------------------------------------------------
        Console.Write("  Decompressing...");
        byte[] raw = File.ReadAllBytes(inputPath);
        var blocks = new List<byte[]>();

        int offset = 24; // skip 24-byte file header
        while (offset + 4 <= raw.Length)
        {
            int bs = Math.Abs(ReadInt32BE(raw, offset));
            if (bs == 0) break;

            byte[] compressed = new byte[bs];
            Array.Copy(raw, offset + 4, compressed, 0, bs);

            try
            {
                using var ms   = new MemoryStream(compressed);
                using var bz2  = new BZip2Stream(ms, SharpCompress.Compressors.CompressionMode.Decompress, false);
                using var outMs = new MemoryStream();
                bz2.CopyTo(outMs);
                blocks.Add(outMs.ToArray());
            }
            catch { /* skip corrupt blocks */ }

            offset += 4 + bs;
        }
        Console.WriteLine($" {blocks.Count} blocks");

        // ---------------------------------------------------------------
        // Step 2 — Parse MSG31 radial records from decompressed blocks
        //
        // Each block contains packed radial records. MSG type 31 = radial data.
        // Each MSG31 record contains:
        //   - Azimuth angle, elevation angle, radial status
        //   - Data block pointers for each moment (REF, VEL, SW, etc.)
        //   - Per-moment: gate count, spacing, scale/offset, raw uint8/uint16 values
        // ---------------------------------------------------------------
        Console.Write("  Parsing MSG31 radials...");

        const int CTM     = 12;  // CTM header size
        const int MSG_HDR = 16;  // message header size

        var radials = new List<Radial>();

        foreach (var block in blocks)
        {
            int ro = 0;
            while (ro + CTM + MSG_HDR < block.Length)
            {
                // Message type is at byte 15 of CTM header
                byte mt = block[ro + 15];

                // Record size: MSG31 uses size from header word, others fixed 2432
                int msh = ReadUInt16BE(block, ro + 12);
                int rs  = (mt == 31) ? (CTM + msh * 2) : 2432;

                if (ro + rs > block.Length) break;
                if (mt != 31) { ro += rs; continue; }

                int ds = ro + CTM + MSG_HDR; // data start

                float azAngle      = ReadFloat32BE(block, ds + 12);
                byte  radialStatus = block[ds + 21];
                float elAngle      = ReadFloat32BE(block, ds + 24);
                int   dbCount      = ReadUInt16BE(block, ds + 30);

                // Parse data moment pointers (up to 12 moments per radial)
                var moments = new Dictionary<string, MomentInfo>();
                for (int bi = 0; bi < Math.Min(dbCount, 12); bi++)
                {
                    int ptrOff = ds + 32 + bi * 4;
                    if (ptrOff + 4 > ro + rs) break;

                    int ptr    = (int)ReadUInt32BE(block, ptrOff);
                    int absPtr = ds + ptr;
                    if (absPtr + 28 > ro + rs) continue;
                    if ((char)block[absPtr] != 'D') continue;

                    string mname = System.Text.Encoding.ASCII
                        .GetString(block, absPtr + 1, 3).Trim('\0').Trim();

                    moments[mname] = new MomentInfo
                    {
                        NGates    = ReadUInt16BE(block, absPtr + 8),
                        FirstGate = ReadUInt16BE(block, absPtr + 10),
                        GateSize  = ReadUInt16BE(block, absPtr + 12),
                        WordSize  = block[absPtr + 19],
                        Scale     = ReadFloat32BE(block, absPtr + 20),
                        Offset    = ReadFloat32BE(block, absPtr + 24),
                        DataStart = absPtr + 28,
                        BlockData = block,
                    };
                }

                radials.Add(new Radial
                {
                    Az      = azAngle,
                    El      = elAngle,
                    ElNum   = block[ds + 22],
                    Status  = radialStatus,
                    Moments = moments,
                });

                ro += rs;
            }
        }

        Console.WriteLine($" {radials.Count} radials");

        // ---------------------------------------------------------------
        // Step 3 — Extract sweep 0 (lowest elevation, has REF)
        //
        // Sweeps are delimited by radial status:
        //   0 = start of new elevation, 3 = beginning of volume scan
        // ---------------------------------------------------------------
        var sweeps = new List<List<Radial>>();
        var current = new List<Radial>();
        foreach (var r in radials)
        {
            if ((r.Status == 0 || r.Status == 3) && current.Count > 0)
            {
                sweeps.Add(current);
                current = new List<Radial>();
            }
            current.Add(r);
        }
        if (current.Count > 0) sweeps.Add(current);

        Console.WriteLine($"  Sweeps found: {sweeps.Count}");

        var sweep    = sweeps[0]; // sweep 0 = lowest elevation, has REF
        int nRadials = sweep.Count;

        var firstMoment = sweep[0].Moments["REF"];
        int nGates      = firstMoment.NGates;
        int firstGateM  = firstMoment.FirstGate;
        int gateSizeM   = firstMoment.GateSize;
        float meanElev  = (float)sweep.Average(r => r.El);

        Console.WriteLine($"  Sweep 0: {nRadials} radials, elevation {meanElev:0.00}°, {nGates} gates");

        // ---------------------------------------------------------------
        // Step 4 — Build polar array [nRadials × nGates]
        //
        // Convert raw uint8/uint16 gate values to physical dBZ:
        //   dBZ = (raw - offset) / scale   for raw >= 2
        //   NaN                              for raw < 2 (no data / range folded)
        // ---------------------------------------------------------------
        Console.Write("  Building polar array...");

        float[] azimuths = new float[nRadials];
        float[,] refPolar = new float[nRadials, nGates];

        // Initialize with NaN
        for (int i = 0; i < nRadials; i++)
            for (int g = 0; g < nGates; g++)
                refPolar[i, g] = float.NaN;

        for (int i = 0; i < nRadials; i++)
        {
            azimuths[i] = sweep[i].Az;
            if (!sweep[i].Moments.TryGetValue("REF", out var m)) continue;

            byte[] d    = m.BlockData;
            int    dStart = m.DataStart;
            int    ng   = m.NGates;

            for (int g = 0; g < Math.Min(ng, nGates); g++)
            {
                int rawVal;
                if (m.WordSize == 8)
                    rawVal = d[dStart + g];
                else if (m.WordSize == 16)
                    rawVal = ReadUInt16BE(d, dStart + g * 2);
                else
                    continue;

                if (rawVal >= 2)
                    refPolar[i, g] = (rawVal - m.Offset) / m.Scale;
            }
        }

        // Sort by azimuth
        int[] sortIdx = Enumerable.Range(0, nRadials)
            .OrderBy(i => azimuths[i]).ToArray();
        float[] sortedAz = sortIdx.Select(i => azimuths[i]).ToArray();
        float[,] sortedRef = new float[nRadials, nGates];
        for (int i = 0; i < nRadials; i++)
            for (int g = 0; g < nGates; g++)
                sortedRef[i, g] = refPolar[sortIdx[i], g];

        Console.WriteLine(" done");

        // ---------------------------------------------------------------
        // Step 5 — Polar → Cartesian resampling (bilinear interpolation)
        //
        // For each output pixel (x, y) in the 800×450 Cartesian grid:
        //   1. Convert pixel to meters (radar at origin, north=up)
        //   2. Compute range and azimuth in polar space
        //   3. Bilinear interpolate from the polar grid
        //
        // The output grid is inscribed within the radar circle (max range):
        //   sqrt((W/2)² + (H/2)²) = maxRange
        //   → each pixel ≈ 1 km
        // ---------------------------------------------------------------
        Console.Write("  Resampling polar → cartesian...");

        float maxRange    = firstGateM + nGates * (float)gateSizeM;
        float aspect      = (float)WIDTH / HEIGHT;
        float halfH       = maxRange / (float)Math.Sqrt(1 + aspect * aspect);
        float halfW       = halfH * aspect;
        float gateSpacing = gateSizeM;

        float[] cartRef = new float[WIDTH * HEIGHT];

        for (int row = 0; row < HEIGHT; row++)
        {
            // y increases upward (south=0, north=HEIGHT-1)
            float y = -halfH + (row + 0.5f) / HEIGHT * (2 * halfH);

            for (int col = 0; col < WIDTH; col++)
            {
                float x = -halfW + (col + 0.5f) / WIDTH * (2 * halfW);

                float range = (float)Math.Sqrt(x * x + y * y);
                float az    = (float)(Math.Atan2(x, y) * 180.0 / Math.PI);
                if (az < 0) az += 360.0f;

                // Outside radar range → NaN
                if (range > maxRange || range < firstGateM)
                {
                    cartRef[row * WIDTH + col] = float.NaN;
                    continue;
                }

                // Fractional gate index
                float fracGate = (range - firstGateM) / gateSpacing;
                int g0 = (int)Math.Floor(fracGate);
                int g1 = g0 + 1;
                if (g0 < 0 || g1 >= nGates)
                {
                    cartRef[row * WIDTH + col] = float.NaN;
                    continue;
                }
                float gFrac = fracGate - g0;

                // Fractional azimuth index
                float azSpacing = 360.0f / nRadials;
                float fracAz    = az / azSpacing;
                int az0 = ((int)Math.Floor(fracAz)) % nRadials;
                int az1 = (az0 + 1) % nRadials;
                float azFrac = fracAz - (float)Math.Floor(fracAz);

                // Bilinear interpolation
                float v00 = sortedRef[az0, g0];
                float v01 = sortedRef[az0, g1];
                float v10 = sortedRef[az1, g0];
                float v11 = sortedRef[az1, g1];

                if (float.IsNaN(v00) || float.IsNaN(v01) ||
                    float.IsNaN(v10) || float.IsNaN(v11))
                {
                    cartRef[row * WIDTH + col] = float.NaN;
                    continue;
                }

                float top    = v00 * (1 - gFrac) + v01 * gFrac;
                float bottom = v10 * (1 - gFrac) + v11 * gFrac;
                cartRef[row * WIDTH + col] = top * (1 - azFrac) + bottom * azFrac;
            }
        }

        Console.WriteLine(" done");

        // ---------------------------------------------------------------
        // Step 6 — Write .bin file
        // ---------------------------------------------------------------
        Console.Write($"  Writing {outputPath}...");
        using (var fs = new FileStream(outputPath, FileMode.Create))
        using (var bw = new BinaryWriter(fs))
        {
            foreach (float v in cartRef)
                bw.Write(v);
        }

        int nValid = cartRef.Count(v => !float.IsNaN(v));
        Console.WriteLine($" done ({nValid:N0} / {WIDTH * HEIGHT:N0} valid pixels)");
        Console.WriteLine();
        Console.WriteLine("=================================================================");
        Console.WriteLine($"  Output: {outputPath}");
        Console.WriteLine($"  Layout: {WIDTH}×{HEIGHT} float32 little-endian");
        Console.WriteLine($"  NaN    = no radar data (0x7FC00000)");
        Console.WriteLine("=================================================================");
    }

    // -----------------------------------------------------------------------
    // Big-endian binary helpers (NEXRAD uses big-endian byte order)
    // -----------------------------------------------------------------------
    static int ReadInt32BE(byte[] b, int o) =>
        (b[o] << 24) | (b[o+1] << 16) | (b[o+2] << 8) | b[o+3];

    static int ReadUInt16BE(byte[] b, int o) =>
        (b[o] << 8) | b[o+1];

    static uint ReadUInt32BE(byte[] b, int o) =>
        ((uint)b[o] << 24) | ((uint)b[o+1] << 16) | ((uint)b[o+2] << 8) | b[o+3];

    static float ReadFloat32BE(byte[] b, int o)
    {
        byte[] tmp = { b[o+3], b[o+2], b[o+1], b[o] };
        return BitConverter.ToSingle(tmp, 0);
    }

    // -----------------------------------------------------------------------
    // Data structures
    // -----------------------------------------------------------------------
    class Radial
    {
        public float Az;
        public float El;
        public byte  ElNum;
        public byte  Status;
        public Dictionary<string, MomentInfo> Moments;
    }

    class MomentInfo
    {
        public int   NGates;
        public int   FirstGate;
        public int   GateSize;
        public byte  WordSize;
        public float Scale;
        public float Offset;
        public int   DataStart;
        public byte[] BlockData;
    }
}
