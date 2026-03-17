using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Gigasoft.ProEssentials;
using Gigasoft.ProEssentials.Enums;
using System.Windows.Media;

namespace NexradRadar
{
    /// <summary>
    /// ProEssentials WPF NEXRAD Radar Reflectivity — 2D Contour Chart
    ///
    /// Displays real NEXRAD WSR-88D Level II radar data from KFWS (Dallas/Fort Worth)
    /// as a 2D contour chart using the official NWS standard reflectivity color table.
    ///
    /// Data pipeline:
    ///   KFWS20250304_110319_V06  — raw NEXRAD L2 V06 binary file (NOAA public data)
    ///   step2_extract.py         — parses polar sweep data → sweep_polar.npz
    ///   step3_cartesian_bin.py   — resamples polar → 800×450 Cartesian → .bin
    ///   NexradConverter/         — optional pure C# pipeline (no Python needed)
    ///   sweep_ref_800x450.bin    — pre-generated, ready to load directly
    ///
    /// The chart loads the pre-generated .bin file — no Python required to run.
    /// The V06 file and Python scripts are included so developers can regenerate
    /// data from any NOAA radar station or any time period.
    ///
    /// Features:
    ///   - 800×450 = 360,000 reflectivity values (dBZ), ~1 km/pixel
    ///   - NWS standard color table: 43 bands from -10 to 76 dBZ
    ///     dark → teal → blue → green → yellow → orange → red → magenta → white
    ///   - Custom SubsetColors defined via anchor interpolation — not a preset
    ///   - NullDataValueZ = -999 cleanly handles pixels outside radar range
    ///   - Geographic map background (radar_background.png) at 70% opacity
    ///     with BitBltZooming — scales with zoom without dithering
    ///   - XYZ cursor tooltip: X km, Y km, Z dBZ on hover
    ///   - Mouse wheel zoom, middle-button pan, zoom box
    ///   - GPU ComputeShader + Direct3D rendering
    ///
    /// Controls:
    ///   Left-click drag   — zoom box
    ///   Middle-click drag — pan
    ///   Mouse wheel       — zoom in/out
    ///   Right-click       — context menu (export, print, customize)
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // Wire events
            Pesgo1.MouseMove               += new MouseEventHandler(Pesgo1_MouseMove);
            Pesgo1.PeCustomTrackingDataText += new PesgoWpf.CustomTrackingDataTextEventHandler(Pesgo1_PeCustomTrackingDataText);
        }

        // -----------------------------------------------------------------------
        // Pesgo1_Loaded — chart initialization
        // -----------------------------------------------------------------------
        void Pesgo1_Loaded(object sender, RoutedEventArgs e)
        {
            // =======================================================================
            // Step 1 — Data dimensions
            //
            // 800 columns (X) × 450 rows (Y) = 360,000 Z values.
            // Each pixel represents approximately 1 km.
            // X: west(0) to east(799), Y: south(0) to north(449)
            //
            // DuplicateDataX / DuplicateDataY: only 800 X values and 450 Y values
            // needed — chart duplicates them across all subsets/points internally.
            // =======================================================================
            int nSubsets = 450;  // grid rows — Y dimension (south to north)
            int nPoints  = 800;  // grid columns — X dimension (west to east)
            int nTotal   = nSubsets * nPoints;

            Pesgo1.PeData.Subsets = nSubsets;
            Pesgo1.PeData.Points  = nPoints;

            // =======================================================================
            // Step 2 — Load sweep_ref_800x450.bin
            //
            // Flat float32 little-endian, row-major layout:
            //   data[row * 800 + col] = dBZ at pixel (col, row)
            //   NaN (0x7FC00000) = no radar data at this pixel
            //
            // NaN values are converted to -999 for NullDataValueZ handling.
            // ProEssentials uses NullDataValueZ to render those pixels transparent,
            // allowing the map background to show through where there is no data.
            // =======================================================================
            float[] pMyZData = new float[nTotal];
            string binPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sweep_ref_800x450.bin");

            try
            {
                using (var fs = new FileStream(binPath, FileMode.Open))
                using (var reader = new BinaryReader(fs))
                {
                    for (int i = 0; i < nTotal; i++)
                    {
                        float v = reader.ReadSingle();
                        // Convert NaN to NullDataValueZ sentinel
                        pMyZData[i] = float.IsNaN(v) ? -999.0f : v;
                    }
                }
            }
            catch
            {
                MessageBox.Show(
                    "sweep_ref_800x450.bin not found.\n\nMake sure the .bin file is in the same folder as the executable.",
                    "File Not Found", MessageBoxButton.OK);
                Application.Current.Shutdown();
                return;
            }

            // =======================================================================
            // Step 3 — Build coordinate arrays and transfer data
            //
            // X and Y are simple 1-based integer sequences (pixel indices).
            // DuplicateData must be set BEFORE FastCopyFrom.
            //
            // X[0..799]: pixel column index (west to east)
            // Y[0..449]: pixel row index (south to north)
            // Z[row, col]: dBZ reflectivity value
            // =======================================================================
            float[] pMyXData = new float[nPoints];
            float[] pMyYData = new float[nSubsets];
            for (int p = 0; p < nPoints;  p++) pMyXData[p] = (float)(p + 1);
            for (int s = 0; s < nSubsets; s++) pMyYData[s] = (float)(s + 1);

            // DuplicateData must be set BEFORE FastCopyFrom
            Pesgo1.PeData.DuplicateDataX = DuplicateData.PointIncrement;
            Pesgo1.PeData.DuplicateDataY = DuplicateData.SubsetIncrement;

            Pesgo1.PeData.X.FastCopyFrom(pMyXData, nPoints);
            Pesgo1.PeData.Y.FastCopyFrom(pMyYData, nSubsets);
            Pesgo1.PeData.Z.FastCopyFrom(pMyZData, nTotal);

            // NullDataValueZ: pixels equal to -999 render transparent —
            // the map background shows through where there is no radar coverage
            Pesgo1.PeData.NullDataValueZ = -999.0;

            // =======================================================================
            // Step 4 — NWS WSR-88D Standard Radar Reflectivity Color Table
            //
            // The official NWS color scale used on classic TV weather radar.
            // 43 color bands × 2 dBZ each covering -10 to 76 dBZ.
            //
            // Anchor colors define key dBZ thresholds; colors are linearly
            // interpolated between anchors for smooth band gradients.
            //
            // Color progression:
            //   -10 dBZ: near black (below threshold / noise)
            //     0 dBZ: gray
            //     5 dBZ: bright cyan (very light precip)
            //    10 dBZ: medium blue
            //    20 dBZ: bright green (moderate rain)
            //    30 dBZ: dark green (heavy rain)
            //    35 dBZ: bright yellow (very heavy)
            //    45 dBZ: orange (very intense)
            //    50 dBZ: bright red (severe)
            //    60 dBZ: magenta (extreme)
            //    65 dBZ: purple
            //    76 dBZ: white (maximum reflectivity)
            // =======================================================================

            // Lock contour scale to exact dBZ range — independent of actual data range.
            // This keeps the color table consistent across different radar files.
            Pesgo1.PeGrid.Configure.ManualScaleControlZ = ManualScaleControl.MinMax;
            Pesgo1.PeGrid.Configure.ManualMinZ          = -10.0F;
            Pesgo1.PeGrid.Configure.ManualMaxZ          =  76.0F;

            // NWS anchor colors: { dBZ, R, G, B }
            int[,] anchors = {
                { -10,   8,   8,  12 },  // Below threshold — near black
                {  -5,  60,  60,  72 },  // Noise floor — dark gray-blue
                {   0, 100, 100, 110 },  // Minimal return — gray
                {   5,   0, 236, 236 },  // Very light precip — bright cyan
                {  10,   1, 160, 246 },  // Light precip — medium blue
                {  15,   0,  60, 246 },  // Light-moderate — dark blue
                {  20,   0, 255,   0 },  // Moderate rain — bright green
                {  25,   0, 200,   0 },  // Moderate rain — medium green
                {  30,   0, 139,   0 },  // Heavy rain — dark green
                {  35, 255, 255,   0 },  // Very heavy — bright yellow
                {  40, 231, 192,   0 },  // Intense — gold
                {  45, 255, 144,   0 },  // Very intense — orange
                {  50, 255,   0,   0 },  // Severe — bright red
                {  55, 190,   0,   0 },  // Severe — dark red
                {  60, 255,   0, 255 },  // Extreme — magenta
                {  65, 153,  85, 201 },  // Extreme — purple
                {  70, 200, 200, 255 },  // Max — lavender-white
                {  76, 255, 255, 255 },  // Ceiling — white
            };

            int   nAnchors = anchors.GetLength(0);
            int   nColors  = 43;
            float dBZMin   = -10.0F;
            float dBZMax   =  76.0F;
            float dBZStep  = (dBZMax - dBZMin) / nColors;

            Pesgo1.PeColor.SubsetColors.Clear();

            for (int c = 0; c < nColors; c++)
            {
                float dBZ = dBZMin + (c + 0.5F) * dBZStep; // midpoint of band

                // Find surrounding anchor pair
                int lo = 0, hi = 1;
                for (int a = 0; a < nAnchors - 1; a++)
                {
                    if (dBZ >= anchors[a, 0] && dBZ <= anchors[a + 1, 0])
                    {
                        lo = a; hi = a + 1; break;
                    }
                    if (a == nAnchors - 2) { lo = a; hi = a + 1; }
                }

                // Linear interpolation between anchor colors
                float span = anchors[hi, 0] - anchors[lo, 0];
                float t    = (span > 0) ? (dBZ - anchors[lo, 0]) / span : 0;
                t = Math.Max(0, Math.Min(1, t));

                int r = Math.Max(0, Math.Min(255, (int)(anchors[lo, 1] + t * (anchors[hi, 1] - anchors[lo, 1]))));
                int g = Math.Max(0, Math.Min(255, (int)(anchors[lo, 2] + t * (anchors[hi, 2] - anchors[lo, 2]))));
                int b = Math.Max(0, Math.Min(255, (int)(anchors[lo, 3] + t * (anchors[hi, 3] - anchors[lo, 3]))));

                Pesgo1.PeColor.SubsetColors[c] = Color.FromArgb(255, (byte)r, (byte)g, (byte)b);
            }

            // SubsetShades: 55% darker version of each color — used for contour shadow lines
            Pesgo1.PeColor.SubsetShades.Clear();
            for (int c = 0; c < nColors; c++)
            {
                Color sc = Pesgo1.PeColor.SubsetColors[c];
                Pesgo1.PeColor.SubsetShades[c] = Color.FromArgb(255,
                    (byte)(sc.R * 0.55), (byte)(sc.G * 0.55), (byte)(sc.B * 0.55));
            }

            // =======================================================================
            // Step 5 — Contour color plotting method
            // ContourColorBlends not used here — SubsetColors controls all colors
            // =======================================================================
            Pesgo1.PeLegend.ContourLegendPrecision = ContourLegendPrecision.ZeroDecimals;
            Pesgo1.PeLegend.ContourStyle           = true;
            Pesgo1.PeLegend.Location               = LegendLocation.Left;

            Pesgo1.PePlot.Allow.ContourColors        = true;
            Pesgo1.PePlot.Allow.ContourColorsShadows = true;
            Pesgo1.PePlot.Method                     = SGraphPlottingMethod.ContourColors;
            Pesgo1.PeUserInterface.Menu.DataShadow   = MenuControl.Hide;

            // Disable non-contour plot methods from right-click menu
            Pesgo1.PePlot.Allow.Line             = false;
            Pesgo1.PePlot.Allow.Point            = false;
            Pesgo1.PePlot.Allow.Bar              = false;
            Pesgo1.PePlot.Allow.Area             = false;
            Pesgo1.PePlot.Allow.Spline           = false;
            Pesgo1.PePlot.Allow.SplineArea       = false;
            Pesgo1.PePlot.Allow.PointsPlusLine   = false;
            Pesgo1.PePlot.Allow.PointsPlusSpline = false;
            Pesgo1.PePlot.Allow.BestFitCurve     = false;
            Pesgo1.PePlot.Allow.BestFitLine      = false;
            Pesgo1.PePlot.Allow.Stick            = false;

            // =======================================================================
            // Step 6 — Visual styling
            // =======================================================================
            Pesgo1.PeColor.BitmapGradientMode = true;
            Pesgo1.PeColor.QuickStyle         = QuickStyle.DarkShadow;
            Pesgo1.PeConfigure.BorderTypes    = TABorder.NoBorder;
            Pesgo1.PeColor.GridBold           = true;

            // =======================================================================
            // Step 7 — Grid
            // AutoMinMaxPadding = 0: contour fills to exact edge of grid area
            // =======================================================================
            Pesgo1.PeGrid.InFront                     = true;
            Pesgo1.PeGrid.LineControl                 = GridLineControl.Both;
            Pesgo1.PeGrid.Style                       = GridStyle.Dot;
            Pesgo1.PeGrid.GridBands                   = false;
            Pesgo1.PeGrid.Configure.AutoMinMaxPadding = 0;

            // =======================================================================
            // Step 8 — Zoom and interaction
            // HorzAndVertMb: zoom box with middle-button pan enabled
            // =======================================================================
            Pesgo1.PeUserInterface.Scrollbar.MouseDraggingX           = true;
            Pesgo1.PeUserInterface.Scrollbar.MouseDraggingY           = true;
            Pesgo1.PeUserInterface.Allow.ZoomStyle                     = ZoomStyle.Ro2Not;
            Pesgo1.PeUserInterface.Allow.Zooming                       = AllowZooming.HorzAndVertMb;
            Pesgo1.PeUserInterface.Scrollbar.MouseWheelFunction        = MouseWheelFunction.HorizontalVerticalZoom;
            Pesgo1.PeUserInterface.Scrollbar.MouseWheelZoomSmoothness  = 3;
            Pesgo1.PeUserInterface.Scrollbar.MouseWheelZoomFactor      = 1.2F;
            Pesgo1.PeUserInterface.Scrollbar.ScrollingVertZoom         = true;
            Pesgo1.PeUserInterface.Scrollbar.ScrollingHorzZoom         = true;

            // =======================================================================
            // Step 9 — Titles and fonts
            // =======================================================================
            Pesgo1.PeString.MainTitle  = "NEXRAD Radar Reflectivity (dBZ)";
            Pesgo1.PeString.SubTitle   = "NWS Standard Color Table  |  KFWS Dallas/Fort Worth  |  800×450 Grid  |  ~1 km/pixel";
            Pesgo1.PeString.YAxisLabel = "Range (km)";
            Pesgo1.PeString.XAxisLabel = "Range (km)";

            Pesgo1.PeFont.FontSize = Gigasoft.ProEssentials.Enums.FontSize.Medium;
            Pesgo1.PeFont.Fixed    = true;

            Pesgo1.PeConfigure.TextShadows = TextShadows.BoldText;
            Pesgo1.PeFont.MainTitle.Bold   = true;
            Pesgo1.PeFont.SubTitle.Bold    = true;
            Pesgo1.PeFont.Label.Bold       = true;

            Pesgo1.PeUserInterface.Dialog.Axis    = false;
            Pesgo1.PeUserInterface.Dialog.Style   = false;
            Pesgo1.PeUserInterface.Dialog.Subsets = false;

            // =======================================================================
            // Step 10 — Performance
            // =======================================================================
            Pesgo1.PeConfigure.PrepareImages = true;
            Pesgo1.PeConfigure.CacheBmp      = true;

            // =======================================================================
            // Step 11 — Cursor tooltip
            //
            // TrackingCustomDataText routes tooltip content through
            // Pesgo1_PeCustomTrackingDataText for custom XYZ formatting.
            // TrackingTooltipTitle shows the station name in the tooltip header.
            // CursorValueZ returns the interpolated dBZ at the mouse position.
            // =======================================================================
            Pesgo1.PeUserInterface.HotSpot.Data                      = true;
            Pesgo1.PeUserInterface.Cursor.PromptTracking             = true;
            Pesgo1.PeUserInterface.Cursor.PromptStyle                = CursorPromptStyle.ZValue;
            Pesgo1.PeUserInterface.Cursor.PromptLocation             = CursorPromptLocation.ToolTip;
            Pesgo1.PeUserInterface.Cursor.TrackingCustomDataText     = true;
            Pesgo1.PeUserInterface.Cursor.TrackingTooltipTitle       = "KFWS — Dallas/Fort Worth";
            Pesgo1.PeUserInterface.Cursor.Hand                       = (int)MouseCursorStyles.Arrow;
            Pesgo1.PeUserInterface.Cursor.HourGlassThreshold         = 9999999;

            // =======================================================================
            // Step 12 — Geographic map background
            //
            // GraphBmpFilename: PNG map of the radar coverage area.
            // BitBltZooming: background scales with zoom without dithering —
            //   the high-resolution PNG (4.7MB) maintains quality at any zoom level.
            // GraphBmpOpacity: 70 = radar overlay at 70% opacity, map shows through.
            // GraphBmpAlways: true = background always visible even when zoomed.
            //
            // To align the background precisely to geographic coordinates, uncomment
            // and set the GraphBmpMin/Max properties to the actual lon/lat extents:
            //   Pesgo1.PeGrid.Configure.GraphBmpMinX = westLongitude;
            //   Pesgo1.PeGrid.Configure.GraphBmpMaxX = eastLongitude;
            //   Pesgo1.PeGrid.Configure.GraphBmpMinY = southLatitude;
            //   Pesgo1.PeGrid.Configure.GraphBmpMaxY = northLatitude;
            // =======================================================================
            Pesgo1.PeColor.GraphBmpAlways       = true;
            Pesgo1.PeColor.GraphGradientStyle   = GradientStyle.Horizontal;
            Pesgo1.PeColor.GraphGradientStart   = Color.FromArgb(255, 255, 255, 255);
            Pesgo1.PeColor.GraphGradientEnd     = Color.FromArgb(255, 255, 255, 255);
            Pesgo1.PeColor.GraphBackground      = Color.FromArgb(0, 1, 0, 0);
            Pesgo1.PeColor.GraphBmpFilename     = "radar_background.png";
            Pesgo1.PeColor.GraphBmpStyle        = BitmapStyle.BitBltZooming;
            Pesgo1.PeColor.GraphBmpOpacity      = 70;

            // =======================================================================
            // Step 13 — Export defaults
            // =======================================================================
            Pesgo1.PeSpecial.DpiX = 600;
            Pesgo1.PeSpecial.DpiY = 600;
            Pesgo1.PeUserInterface.Dialog.ExportSizeDef  = ExportSizeDef.NoSizeOrPixel;
            Pesgo1.PeUserInterface.Dialog.ExportTypeDef  = ExportTypeDef.Png;
            Pesgo1.PeUserInterface.Dialog.ExportDestDef  = ExportDestDef.Clipboard;
            Pesgo1.PeUserInterface.Dialog.ExportUnitXDef = "1280";
            Pesgo1.PeUserInterface.Dialog.ExportUnitYDef = "768";
            Pesgo1.PeUserInterface.Dialog.ExportImageDpi = 300;
            Pesgo1.PeUserInterface.Dialog.AllowEmfExport = false;
            Pesgo1.PeUserInterface.Dialog.AllowWmfExport = false;

            // =======================================================================
            // Step 14 — Rendering engine
            // Direct3D + ComputeShader for GPU-accelerated contour rendering
            // =======================================================================
            Pesgo1.PeConfigure.Composite2D3D         = Composite2D3D.Foreground;
            Pesgo1.PeConfigure.RenderEngine          = RenderEngine.Direct3D;
            Pesgo1.PeData.ComputeShader               = true;
            Pesgo1.PeConfigure.AntiAliasGraphics      = true;
            Pesgo1.PeFunction.Force3dxNewColors       = true;
            Pesgo1.PeFunction.Force3dxVerticeRebuild  = true;

            Pesgo1.PeFunction.ReinitializeResetImage();
            Pesgo1.Invalidate();
        }

        // -----------------------------------------------------------------------
        // Pesgo1_MouseMove — show Z value in title bar on hover
        //
        // Two modes depending on HotSpot.Data setting:
        //   false: interpolated Z value at mouse position (CursorValueZ)
        //   true:  exact Z value at nearest data point (GetHotSpot)
        // -----------------------------------------------------------------------
        void Pesgo1_MouseMove(object sender, MouseEventArgs e)
        {
            System.Windows.Point pt   = Pesgo1.PeUserInterface.Cursor.LastMouseMove;
            System.Windows.Rect  rect = Pesgo1.PeFunction.GetRectGraph();

            if (rect.Contains(pt))
            {
                if (Pesgo1.PeUserInterface.HotSpot.Data == false)
                {
                    // Interpolated value — smooth, follows exact mouse position
                    this.Title = "Interpolated dBZ: " +
                        String.Format("{0:0.0}", Pesgo1.PeUserInterface.Cursor.CursorValueZ);
                }
                else
                {
                    // Exact data value at nearest grid point
                    Gigasoft.ProEssentials.Structs.HotSpotData ds = Pesgo1.PeFunction.GetHotSpot();
                    if (ds.Type == HotSpotType.DataPoint)
                    {
                        float z = Pesgo1.PeData.Z[ds.Data1, ds.Data2];
                        this.Title = z <= -998
                            ? "No radar data"
                            : "dBZ: " + z.ToString("0.0");
                    }
                }
            }
        }

        // -----------------------------------------------------------------------
        // Pesgo1_PeCustomTrackingDataText — custom tooltip content
        //
        // Fires when the tracking tooltip needs content.
        // Shows X position (km west-east), Y position (km south-north),
        // and Z (dBZ reflectivity) at the cursor location.
        // -----------------------------------------------------------------------
        void Pesgo1_PeCustomTrackingDataText(object sender, Gigasoft.ProEssentials.EventArg.CustomTrackingDataTextEventArgs e)
        {
            float z = (float)Pesgo1.PeUserInterface.Cursor.CursorValueZ;
            string zStr = z <= -998 ? "No data" : String.Format("{0:0.0} dBZ", z);

            string s  = String.Format("X : {0:0.0} km\n", Pesgo1.PeUserInterface.Cursor.CursorValueX);
            s        += String.Format("Y : {0:0.0} km\n", Pesgo1.PeUserInterface.Cursor.CursorValueY);
            s        += String.Format("Z : {0}", zStr);

            e.TrackingText = s;
        }

        // -----------------------------------------------------------------------
        // Window_Closing
        // -----------------------------------------------------------------------
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
        }
    }
}
