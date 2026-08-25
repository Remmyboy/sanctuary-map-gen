// Locating a .scmap's texture set and its two splat masks.
//
// Anchored rather than walked. The straight walk from the water block has to
// cross wave generators, decals, decal groups and eight length-prefixed images,
// and it desynchronises on Seton's Clutch, where the four bytes after the wave
// textures are not the wave-generator count the format documents. Three sample
// maps were not enough to work out what they are, and the walk is not worth
// perfecting when both things we need announce themselves:
//
//   * the texture set is a contiguous run of eight (path, scale) pairs, the
//     paths under env/ and ending in a layers/ folder;
//   * the masks are DDS images, and the format fixes their size at half the map
//     size.
//
// Both checks reject a wrong guess structurally, which is a stronger guarantee
// than a walk that happens not to crash.
public static partial class MapGen
{
    public class ScTextureSet
    {
        /// Eight layer albedo paths as the map names them, e.g.
        /// "/env/Evergreen/layers/grass001_albedo.dds". Empty where the map
        /// leaves a layer unused.
        /// Ten layer entries as the file stores them: LowerStratum, the eight
        /// masked strata, UpperStratum. Index 0 is the base that shows where
        /// nothing is painted and 1..8 line up with the splat channels - the
        /// same numbering Sanctuary uses. Index 9, the upper macro overlay,
        /// is carried but unused.
        ///
        /// The scanner used to model this block as eight entries plus four
        /// normals. That window happened to parse on most maps because empty
        /// entries give it slack, but it put stratum 8's albedo into
        /// NormalPaths[0], the upper stratum's macrotexture into
        /// NormalPaths[1], and silently dropped layer 8 on any map that
        /// painted it. Ten maps whose blocks were fully populated refused to
        /// parse at all, which is what gave it away.
        public string[] Paths = new string[10];
        public float[] Scales = new float[10];
        public string[] NormalPaths = new string[9];
        public float[] NormalScales = new float[9];

        /// Byte offset just past the four normal entries - the decal block
        /// begins eight bytes after it. Captured because the texture run is
        /// the one anchor in this part of the file that scans reliably, and
        /// decals have no distinctive shape of their own to anchor on.
        public int AfterNormalsOffset;

        /// Offsets and lengths of the two splat images inside the .scmap.
        public int MaskLowOffset, MaskLowLength;
        public int MaskHighOffset, MaskHighLength;
        public int MaskSize;

        public int UsedLayers
        {
            get
            {
                int n = 0;
                foreach (var s in Paths) if (!string.IsNullOrEmpty(s)) n++;
                return n;
            }
        }
    }

    /// A scale is plausible for a layer that is actually used. An unused entry
    /// carries an empty path and usually a scale of zero, so the two cases are
    /// checked separately - requiring a positive scale everywhere rejected 83
    /// maps that simply do not fill all eight layers.
    static bool LooksLikeScale(float f, bool named)
    {
        if (float.IsNaN(f) || float.IsInfinity(f)) return false;
        return named ? (f > 0.05f && f < 4096f) : (f >= 0f && f < 4096f);
    }

    /// ASCII string at `p`, empty string if there is just a terminator, null if
    /// the bytes are not a plausible path.
    static string ScPathAt(byte[] b, int p, int maxLen)
    {
        int s = p;
        while (p < b.Length && b[p] != 0)
        {
            if (b[p] < 0x20 || b[p] > 0x7e) return null;
            if (p - s > maxLen) return null;
            p++;
        }
        if (p >= b.Length) return null;
        return System.Text.Encoding.ASCII.GetString(b, s, p - s);
    }

    static int ScAfterPath(byte[] b, int p) { while (p < b.Length && b[p] != 0) p++; return p + 1; }

    /// Try to read ten layer pairs then nine normal pairs starting at `p`.
    /// Fills `set` and returns true only if the whole shape holds AND the
    /// bytes that follow look like the decal block that comes next in the
    /// file - two ints then a plausible decal count. That trailing check is
    /// what stops a partial window part-way into the block from being
    /// accepted, which is exactly the mistake the old eight-plus-four shape
    /// made on every map.
    static bool TryTextureRun(byte[] b, int p, ScTextureSet set)
    {
        var paths = new string[10];
        var scales = new float[10];
        int named = 0;

        for (int k = 0; k < 10; k++)
        {
            string s = ScPathAt(b, p, 300);
            if (s == null) return false;
            if (s.Length > 0)
            {
                // A named layer must actually be a layer texture. This is what
                // stops the scan latching onto some other run of env paths.
                if (s.IndexOf("layers/", StringComparison.OrdinalIgnoreCase) < 0) return false;
                named++;
            }
            p = ScAfterPath(b, p);
            if (p + 4 > b.Length) return false;
            float sc = BitConverter.ToSingle(b, p);
            if (!LooksLikeScale(sc, s.Length > 0)) return false;
            paths[k] = s; scales[k] = sc;
            p += 4;
        }
        if (named == 0) return false;

        var nPaths = new string[9];
        var nScales = new float[9];
        for (int k = 0; k < 9; k++)
        {
            string s = ScPathAt(b, p, 300);
            if (s == null) return false;
            p = ScAfterPath(b, p);
            if (p + 4 > b.Length) return false;
            float sc = BitConverter.ToSingle(b, p);
            // Lenient on purpose: norfair carries a named normal with scale 0,
            // and the strict named-scale rule built for albedos rejected the
            // whole block over it.
            if (!LooksLikeScale(sc, false)) return false;
            nPaths[k] = s; nScales[k] = sc;
            p += 4;
        }

        // Two unused ints, then the decal count. A count outside sanity means
        // this window is not aligned with the real block.
        if (p + 12 > b.Length) return false;
        int decalCount = BitConverter.ToInt32(b, p + 8);
        if (decalCount < 0 || decalCount > 200000) return false;

        set.Paths = paths; set.Scales = scales;
        set.NormalPaths = nPaths; set.NormalScales = nScales;
        set.AfterNormalsOffset = p;
        return true;
    }

    public static ScTextureSet ScanScTextures(byte[] b, int mapSize)
    {
        var set = new ScTextureSet();
        set.Paths[0] = null;

        // A map need not use all eight layers, and an unused one is an empty
        // string: a bare terminator plus its scale, five bytes. So the run can
        // begin before the first path we can see. For each env/ path found, try
        // the run starting there and at each five-byte step back, which is
        // where preceding empty entries would sit.
        bool got = false;
        for (int i = 0; i + 8 < b.Length && !got; i++)
        {
            if (b[i] != '/' || b[i + 1] != 'e' || b[i + 2] != 'n' || b[i + 3] != 'v' || b[i + 4] != '/') continue;

            for (int back = 0; back <= 9 && !got; back++)
            {
                int start = i - back * 5;
                if (start < 0) break;
                bool emptiesOk = true;
                for (int k = 0; k < back; k++) if (b[start + k * 5] != 0) { emptiesOk = false; break; }
                if (!emptiesOk) break;
                got = TryTextureRun(b, start, set);
            }
        }
        if (!got) return null;

        // ---- the two splat images ----
        // The stored images run: preview, terrain normal map, texture mask low,
        // texture mask high, water map. So the masks are the third and fourth,
        // and position is the reliable discriminator - size is not. The stock
        // maps use mapSize/2 but plenty of community maps use full mapSize, and
        // a_new_dawn pairs a 256 normal map with 1024 masks on a 1024 map.
        // Requiring mapSize/2 rejected 83 maps.
        var images = new List<int[]>();          // offset, length, width
        for (int i = 4; i + 128 < b.Length; i++)
        {
            if (b[i] != 'D' || b[i + 1] != 'D' || b[i + 2] != 'S' || b[i + 3] != ' ') continue;
            if (BitConverter.ToInt32(b, i + 4) != 124) continue;      // dwSize
            int h = BitConverter.ToInt32(b, i + 12);
            int w = BitConverter.ToInt32(b, i + 16);
            if (w <= 0 || h <= 0 || w > 8192 || h > 8192) continue;
            // Length-prefixed, so the four bytes before the magic say how long
            // it is. That is what distinguishes a stored image from a chance
            // match inside compressed data.
            int len = BitConverter.ToInt32(b, i - 4);
            if (len < 128 || i + len > b.Length) continue;
            images.Add(new[] { i, len, w });
            i += len - 1;
        }

        // Third and fourth, and they must agree on size - the two halves of one
        // eight-channel splat.
        if (images.Count < 4) return null;
        if (images[2][2] != images[3][2]) return null;
        set.MaskLowOffset = images[2][0]; set.MaskLowLength = images[2][1];
        set.MaskHighOffset = images[3][0]; set.MaskHighLength = images[3][1];
        set.MaskSize = images[2][2];
        return set;
    }
}
