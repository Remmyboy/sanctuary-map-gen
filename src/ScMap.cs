// Reader for Supreme Commander: Forged Alliance map files (.scmap + _save.lua).
//
// The two games turn out to share a coordinate scheme, which is what makes this
// worth doing at all:
//
//   * SupCom stores heights as uint16 scaled by a factor carried in the file,
//     which is 1/128 on every stock map. Sanctuary stores uint16 scaled by
//     map.height/65535. Set Sanctuary's height field to 512 and the two are the
//     same fixed-point encoding - 65535/512 = 128 - so the heightmap copies
//     across without resampling.
//   * One SupCom map unit is one heightmap cell is one Sanctuary metre, so
//     marker positions need no conversion.
//   * SupCom heightmaps are (size+1) square with size a power of two, which is
//     exactly the rule Unity enforces on TerrainData.heightmapResolution.
//
// Only the header, the heightmap and the water block are parsed. Everything
// after the water block - decals, props, texture masks - is SupCom asset paths
// that mean nothing here, so it is left on the floor and Sanctuary's own
// stratums and props are generated over the imported terrain instead.
public static partial class MapGen
{
    public class ScMapInfo
    {
        public int    Size;              // cells per side; heightmap is Size+1
        public float  HeightScale;       // metres per raw unit
        public ushort[,] Raw;            // [row, col], (Size+1) square
        public bool   HasWater;
        public float  WaterElevation, WaterElevationDeep, WaterElevationAbyss;
        public int    VersionMinor;
        public string TerrainShader = "";
        public bool   RowZeroIsNorth = true;   // resolved against the markers

        /// Byte offset just past the water block, so the rest of the file can
        /// be walked without re-parsing what came before.
        public int    AfterWaterOffset;
        /// Textures, decals and props the map author placed. Null if the walk
        /// could not be completed.
        public ScContent Content;
    }

    public class ScMarker
    {
        public string Name = "", Type = "";
        public float X, Y, Z;
    }

    // ---- little-endian primitives ---------------------------------------

    static int   RdI32(byte[] b, ref int p) { int v = b[p] | b[p+1] << 8 | b[p+2] << 16 | b[p+3] << 24; p += 4; return v; }
    static short RdI16(byte[] b, ref int p) { short v = (short)(b[p] | b[p+1] << 8); p += 2; return v; }
    static byte  RdU8 (byte[] b, ref int p) { return b[p++]; }
    static float RdF32(byte[] b, ref int p) { float v = BitConverter.ToSingle(b, p); p += 4; return v; }

    /// Finite, non-denormal, and inside the range a SupCom altitude can occupy.
    static bool Ok(float v)
    {
        return !float.IsNaN(v) && !float.IsInfinity(v) && v >= 0f && v <= 1024f
               && (v == 0f || Math.Abs(v) > 1e-20f);
    }

    static string RdStrZ(byte[] b, ref int p)
    {
        int s = p;
        while (p < b.Length && b[p] != 0) p++;
        string v = System.Text.Encoding.ASCII.GetString(b, s, p - s);
        p++;                                    // step over the terminator
        return v;
    }

    /// Header, size and water only - the heightmap is skipped rather than
    /// decoded. Listing a folder of a few hundred community maps with the full
    /// reader means allocating a few hundred megabytes of ushort arrays for
    /// information that fits on one line, so the browser uses this instead.
    public static ScMapInfo ReadScMapHeader(string path) { return ReadScMap(path, headerOnly: true); }

    public static ScMapInfo ReadScMap(string path) { return ReadScMap(path, headerOnly: false); }

    public static ScMapInfo ReadScMap(string path, bool headerOnly)
    {
        byte[] b = File.ReadAllBytes(path);
        int p = 0;
        var m = new ScMapInfo();

        if (b[0] != 0x4D || b[1] != 0x61 || b[2] != 0x70 || b[3] != 0x1A)
            throw new InvalidDataException("not a .scmap: wrong magic");
        p = 4;
        RdI32(b, ref p);                        // major version, 2
        int magic2 = RdI32(b, ref p);
        if (magic2 != unchecked((int)0xbeeffeed))
            throw new InvalidDataException("bad second magic 0x" + magic2.ToString("x8"));
        RdI32(b, ref p);                        // always 2

        RdF32(b, ref p);                        // width  as float
        RdF32(b, ref p);                        // height as float
        RdI32(b, ref p);                        // 0
        RdI16(b, ref p);                        // 0

        int previewLen = RdI32(b, ref p);
        p += previewLen;                        // embedded DDS minimap, unused

        m.VersionMinor = RdI32(b, ref p);

        int hmWidth  = RdI32(b, ref p);
        int hmHeight = RdI32(b, ref p);
        m.HeightScale = RdF32(b, ref p);
        if (hmWidth != hmHeight)
            throw new NotSupportedException("non-square map " + hmWidth + "x" + hmHeight);
        m.Size = hmWidth;

        int n = m.Size + 1;
        if (headerOnly)
        {
            p += n * n * 2;
        }
        else
        {
            m.Raw = new ushort[n, n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    m.Raw[y, x] = (ushort)(b[p] | b[p + 1] << 8);
                    p += 2;
                }
        }

        // Strings and the lighting block sit between the heightmap and the water
        // settings. Everything is fixed-size except the strings, so walk them
        // rather than seeking - and the string layout changed at version 56.
        //
        // Verified against both shapes in the wild: the stock campaign and
        // multiplayer maps are version 60, and older community maps such as
        // quantumsea are version 53. Getting this wrong desynchronises the walk
        // and surfaces as an absurd cubemap count a few bytes later.
        if (m.VersionMinor >= 56)
        {
            RdStrZ(b, ref p);                       // unused, empty in practice
            m.TerrainShader = RdStrZ(b, ref p);     // "TTerrain" / "TTerrainXP"
            RdStrZ(b, ref p);                       // background texture
            RdStrZ(b, ref p);                       // sky cubemap
            int cubemaps = RdI32(b, ref p);
            if (cubemaps < 0 || cubemaps > 64)
                throw new InvalidDataException("implausible cubemap count " + cubemaps +
                                               "; the layout walk has desynchronised");
            for (int i = 0; i < cubemaps; i++) { RdStrZ(b, ref p); RdStrZ(b, ref p); }
        }
        else
        {
            m.TerrainShader = RdStrZ(b, ref p);     // no leading empty string
            RdStrZ(b, ref p);                       // background texture
            RdStrZ(b, ref p);                       // sky cubemap
            RdStrZ(b, ref p);                       // one environment cubemap, uncounted
        }

        p += 4;          // lightingMultiplier
        p += 12;         // sunDirection
        p += 12;         // sunAmbience
        p += 12;         // sunColor
        p += 12;         // shadowFillColor
        p += 16;         // specularColor
        p += 4;          // bloom
        p += 12;         // fogColor
        p += 4;          // fogStart
        p += 4;          // fogEnd

        m.HasWater            = RdU8(b, ref p) != 0;
        m.WaterElevation      = RdF32(b, ref p);
        m.WaterElevationDeep  = RdF32(b, ref p);
        m.WaterElevationAbyss = RdF32(b, ref p);

        // The elevations are metres on the terrain's own scale, and deep sits below
        // the surface with abyss below that. If the string walk above lost sync these
        // come out as wild or denormal floats - better to say so than to emit a map
        // with a nonsense sea level.
        if (m.HasWater)
        {
            bool sane = Ok(m.WaterElevation) && Ok(m.WaterElevationDeep) && Ok(m.WaterElevationAbyss)
                        && m.WaterElevationDeep  <= m.WaterElevation + 0.001f
                        && m.WaterElevationAbyss <= m.WaterElevationDeep + 0.001f;
            if (!sane)
                throw new InvalidDataException(
                    "water block looks wrong (elevation " + m.WaterElevation +
                    ", deep " + m.WaterElevationDeep + ", abyss " + m.WaterElevationAbyss +
                    "); the layout walk has desynchronised");

            // A few campaign maps set the flag but leave the elevations at zero.
            // That is a dry map, not a broken parse.
            if (m.WaterElevation <= 0f) m.HasWater = false;
        }

        m.AfterWaterOffset = p;
        if (!headerOnly) m.Content = ReadScContent(b, p, m.VersionMinor);

        return m;
    }

    // ---- markers from _save.lua -----------------------------------------

    // Generated code with a completely regular shape, so a regex over the
    // innermost brace blocks is enough. Blocks without both a type and a
    // position are skipped, which drops the army and wreckage tables.
    public static List<ScMarker> ReadScMarkers(string saveLuaPath)
    {
        string t = File.ReadAllText(saveLuaPath);
        var found = new List<ScMarker>();
        var block = new System.Text.RegularExpressions.Regex(
            @"\['(?<name>[^']+)'\]\s*=\s*\{(?<body>[^{}]*)\}",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        var typeRe = new System.Text.RegularExpressions.Regex(
            @"\['type'\]\s*=\s*STRING\(\s*'(?<t>[^']*)'");
        var posRe = new System.Text.RegularExpressions.Regex(
            @"\['position'\]\s*=\s*VECTOR3\(\s*(?<x>[-\d.eE+]+)\s*,\s*(?<y>[-\d.eE+]+)\s*,\s*(?<z>[-\d.eE+]+)");

        foreach (System.Text.RegularExpressions.Match mm in block.Matches(t))
        {
            string body = mm.Groups["body"].Value;
            var tm = typeRe.Match(body);
            var pm = posRe.Match(body);
            if (!tm.Success || !pm.Success) continue;
            found.Add(new ScMarker {
                Name = mm.Groups["name"].Value,
                Type = tm.Groups["t"].Value,
                X = float.Parse(pm.Groups["x"].Value, System.Globalization.CultureInfo.InvariantCulture),
                Y = float.Parse(pm.Groups["y"].Value, System.Globalization.CultureInfo.InvariantCulture),
                Z = float.Parse(pm.Groups["z"].Value, System.Globalization.CultureInfo.InvariantCulture),
            });
        }
        return found;
    }

    // ---- adoption into the generator's own field ------------------------

    /// Decide whether raw row 0 is z=0 or z=Size by testing both against the
    /// heights recorded on the markers. Getting this wrong mirrors the terrain
    /// while leaving the markers where they were, so every spawn would land on
    /// ground that is not the ground it was authored on.
    public static bool ResolveScRowOrder(ScMapInfo m, List<ScMarker> markers,
                                         out float errNorth, out float errSouth)
    {
        int n = m.Size + 1;
        double sN = 0, sS = 0; int cnt = 0;
        foreach (var k in markers)
        {
            int col = (int)Math.Round(k.X); if (col < 0 || col >= n) continue;
            int rz  = (int)Math.Round(k.Z); if (rz  < 0 || rz  >= n) continue;
            sN += Math.Abs(m.Raw[rz, col]         * m.HeightScale - k.Y);
            sS += Math.Abs(m.Raw[n - 1 - rz, col] * m.HeightScale - k.Y);
            cnt++;
        }
        errNorth = cnt > 0 ? (float)(sN / cnt) : float.NaN;
        errSouth = cnt > 0 ? (float)(sS / cnt) : float.NaN;

        // Row 0 is z=0 on every stock map, and where the two errors are close the
        // test is not telling us anything - markers on ramps and shorelines sit a
        // metre or two off the terrain either way. Only flip on a clear margin.
        if (cnt == 0) return true;
        return !(errSouth < errNorth * 0.8f);
    }

    /// Copy the imported terrain into MapGen.Height, whose row 0 is world z max.
    ///
    /// The two games run z in opposite directions. SupCom draws heightmap row 0
    /// at the top of the map, so its z grows southward; Sanctuary's z grows
    /// northward (our own maps settle it - Serpent Crossing's top-left base is
    /// the one at BaseZ = 0.78 * size). Mapping SupCom z straight onto Sanctuary
    /// z therefore mirrors the map north-south: a corner-to-corner feature that
    /// runs bottom-left to top-right in SupCom comes out top-left to
    /// bottom-right here.
    ///
    /// So the import negates z: Sanctuary z = Size - SupCom z. That has to be
    /// done to the terrain AND the markers, in step - see ScMarkerZ. Doing it to
    /// only one of them is self-inconsistent and puts every spawn on terrain it
    /// was not authored on, which is survivable-looking right up to the point
    /// where the spawns are in the sea. Convert-ScMap.ps1 asserts the two agree
    /// rather than trusting this comment.
    public static void AdoptScMap(ScMapInfo m, float verticalScale)
    {
        MapSize = m.Size;
        HRes    = m.Size + 1;
        SRes    = HRes;          // splat is vertex-aligned to the heightmap grid
        int n   = HRes;
        Height  = new float[n, n];

        for (int r = 0; r < n; r++)
        {
            // Sanctuary row r is world z = Size - r, which is SupCom z = r once
            // the axis is negated. RowZeroIsNorth says whether SupCom's raw rows
            // are indexed by z directly or from the far edge.
            int src = m.RowZeroIsNorth ? r : (n - 1 - r);
            for (int c = 0; c < n; c++)
                Height[r, c] = m.Raw[src, c] * m.HeightScale * verticalScale;
        }

        WaterLevel = m.HasWater ? m.WaterElevation * verticalScale : 0f;

        // SupCom sizes are always powers of two, but assert it anyway - this is the
        // one path into the generator that does not go through Configure().
        if ((m.Size & (m.Size - 1)) != 0)
            throw new NotSupportedException(
                "map size " + m.Size + " is not a power of two; Unity would round " +
                "the heightmap resolution and leave part of the terrain unwritten");

        RebuildSlope();
        BuildWalkable();
    }

    /// SupCom marker z -> Sanctuary world z. Negated, matching AdoptScMap.
    public static float ScMarkerZ(ScMapInfo m, float z) { return m.Size - z; }

    /// Mean vertical disagreement between the imported terrain and the heights
    /// SupCom recorded on its own markers, in metres.
    ///
    /// This is the check that catches a terrain fold and a marker mapping
    /// drifting out of step, which is the single easiest thing to get wrong
    /// here and does not announce itself. Expect well under a metre on a map
    /// whose markers sit on flat ground; a mirrored import scores several.
    public static float ScMarkerFit(ScMapInfo m, List<ScMarker> markers)
    {
        double sum = 0; int cnt = 0;
        foreach (var k in markers)
        {
            float x = k.X, z = ScMarkerZ(m, k.Z);
            if (x < 0 || x > MapSize || z < 0 || z > MapSize) continue;
            sum += Math.Abs(HeightAtWorld(x, z) - k.Y);
            cnt++;
        }
        return cnt > 0 ? (float)(sum / cnt) : float.NaN;
    }

    /// Highest point after import, so the caller can choose map.height.
    public static float HeightMax()
    {
        float mx = 0f;
        for (int r = 0; r < HRes; r++)
            for (int c = 0; c < HRes; c++)
                if (Height[r, c] > mx) mx = Height[r, c];
        return mx;
    }
}
