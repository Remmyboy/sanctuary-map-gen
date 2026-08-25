using System;
using System.Collections.Generic;

// Generalisation of the hand-built maps into something that takes a seed and a
// style and produces a playable map: n-fold symmetry, radial spawn placement,
// rejection-sampled resource spots, and the validation gate that decides
// whether a roll is worth keeping.
//
// Symmetry order is restricted to 2 (180 degrees) and 4 (90 degrees) because
// those are the only rotations that map a square onto itself. Six and eight
// player maps therefore use order 2 or 4 with several spawns per sector, which
// is what hand-made maps do anyway.
public static partial class MapGen
{
    /// 2 = 180-degree rotational, 4 = 90-degree rotational.
    public static int SymOrder = 2;

    /// Avalanche a seed. .NET's Random correlates badly across nearby seeds -
    /// seeding with s, s+7919, s+15838 and taking Next(5) returns nearly the
    /// same value each time, so a batch comes out as the same style repeatedly.
    /// Done here rather than in PowerShell, which silently promotes an integer
    /// product past Int64 to Double and destroys the mix.
    public static int MixSeed(int v)
    {
        unchecked
        {
            uint u = (uint)v;
            u ^= u >> 16; u *= 0x45d9f3bu;
            u ^= u >> 16; u *= 0x45d9f3bu;
            u ^= u >> 16;
            return (int)(u & 0x7FFFFFFF);
        }
    }

    // ---- inspect what is actually on disk ---------------------------------

    /// Renders a deployed heightmap.raw exactly as Load.ReadRaw would read it,
    /// with anything over the 30-degree nav limit painted red. Independent of
    /// the generator on purpose: if the two ever disagree, this shows the bytes
    /// the game will actually load.
    public static void RenderHeightmapFile(string rawPath, int hmRes, float mapSize,
                                           float maxHeight, float waterLevel,
                                           float[] markX, float[] markZ, int[] markKind,
                                           string outPng, int res)
    {
        byte[] raw = File.ReadAllBytes(rawPath);
        int need = hmRes * hmRes * 2;
        if (raw.Length < need)
            throw new Exception("heightmap.raw is " + raw.Length + " bytes; hmRes " + hmRes + " needs " + need);

        var h = new float[hmRes, hmRes];
        float lo = float.MaxValue, hi = float.MinValue;
        for (int r = 0; r < hmRes; r++)
            for (int c = 0; c < hmRes; c++)
            {
                int i = (r * hmRes + c) * 2;
                float v = (raw[i] | (raw[i + 1] << 8)) * maxHeight / 65535f;
                h[r, c] = v;
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }

        float step = mapSize / (hmRes - 1);
        var slope = new float[hmRes, hmRes];
        for (int r = 0; r < hmRes; r++)
            for (int c = 0; c < hmRes; c++)
            {
                int r0 = Math.Max(r - 1, 0), r1 = Math.Min(r + 1, hmRes - 1);
                int c0 = Math.Max(c - 1, 0), c1 = Math.Min(c + 1, hmRes - 1);
                float dx = (h[r, c1] - h[r, c0]) / ((c1 - c0) * step);
                float dz = (h[r1, c] - h[r0, c]) / ((r1 - r0) * step);
                slope[r, c] = (float)(Math.Atan(Math.Sqrt(dx * dx + dz * dz)) * 180.0 / Math.PI);
            }

        var rgb = new byte[res * res * 3];
        for (int py = 0; py < res; py++)
        {
            int r = Math.Min(hmRes - 1, py * hmRes / res);
            for (int px = 0; px < res; px++)
            {
                int c = Math.Min(hmRes - 1, px * hmRes / res);
                float v = h[r, c];
                float rr, gg, bb;
                if (v <= waterLevel) { rr = 0.10f; gg = 0.22f; bb = 0.45f; }
                else if (slope[r, c] > MaxNavSlopeDeg) { rr = 0.85f; gg = 0.18f; bb = 0.18f; }
                else
                {
                    float t = Clamp01((v - lo) / Math.Max(0.001f, hi - lo));
                    rr = 0.13f + 0.82f * t; gg = 0.34f + 0.58f * t; bb = 0.16f + 0.74f * t;
                    float band = (v - lo) / 4f;
                    if (band - (float)Math.Floor(band) < 0.12f) { rr *= 0.62f; gg *= 0.62f; bb *= 0.62f; }
                }
                int o = (py * res + px) * 3;
                rgb[o] = (byte)(Clamp01(rr) * 255);
                rgb[o + 1] = (byte)(Clamp01(gg) * 255);
                rgb[o + 2] = (byte)(Clamp01(bb) * 255);
            }
        }

        if (markX != null)
            for (int m = 0; m < markX.Length; m++)
            {
                int px = (int)(markX[m] / mapSize * res);
                int py = (int)((mapSize - markZ[m]) / mapSize * res);
                int rad = markKind[m] == 0 ? 6 : 3;
                byte kr = 255, kg = markKind[m] == 0 ? (byte)40 : (byte)235, kb = 40;
                for (int dy = -rad; dy <= rad; dy++)
                    for (int dx = -rad; dx <= rad; dx++)
                    {
                        if (dx * dx + dy * dy > rad * rad) continue;
                        int yy = py + dy, xx = px + dx;
                        if (yy < 0 || yy >= res || xx < 0 || xx >= res) continue;
                        int o = (yy * res + xx) * 3;
                        rgb[o] = kr; rgb[o + 1] = kg; rgb[o + 2] = kb;
                    }
            }

        WritePng(outPng, res, res, rgb);
    }

    /// Loads a deployed heightmap.raw into the generator's own state so the
    /// pathability checks can run against the shipped bytes rather than against
    /// whatever happened to be in memory when the map was written.
    public static void LoadHeightFromFile(string rawPath, int hmResolution, float mapSize,
                                          float maxHeight, float waterLevel)
    {
        byte[] raw = File.ReadAllBytes(rawPath);
        MapSize = mapSize;
        HRes = hmResolution;
        MaxHeight = maxHeight;
        WaterLevel = waterLevel;
        Height = new float[HRes, HRes];
        for (int r = 0; r < HRes; r++)
            for (int c = 0; c < HRes; c++)
            {
                int i = (r * HRes + c) * 2;
                Height[r, c] = (raw[i] | (raw[i + 1] << 8)) * maxHeight / 65535f;
            }
        RebuildSlope();
        BuildWalkable();
    }

    /// Small isolated blocked patches sitting in otherwise open ground.
    ///
    /// These do not show up in a reachability check - the map stays 100%
    /// connected - but in game they are invisible pinch points that units path
    /// around, and a field speckled with them feels broken to play on even
    /// though every metric says it is fine. Counts blocked clusters smaller
    /// than maxCells that are not attached to a real cliff.
    /// Returns { speckCount, speckCells, blockedCells }.
    ///
    /// "Isolated" is the important word. A small chip of blocked ground hanging
    /// off a cliff face is scenery - you walk round the cliff anyway. What hurts
    /// is a small blocked patch standing alone in open ground. Counting both
    /// made the metric fight its own fix: smoothing a patch fragments the cliff
    /// edge beside it into fresh chips, so more smoothing scored worse.
    /// A patch only counts if every cell of it is at least IsolationCells away
    /// from any blocked region too big to be a patch.
    public const int IsolationCells = 6;

    public static float[] PathingSpecks(int maxCells)
    {
        int[] dR = { 1, -1, 0, 0 }, dC = { 0, 0, 1, -1 };

        // Label every blocked cluster, splitting them into small and large.
        var seen = new bool[HRes, HRes];
        var large = new bool[HRes, HRes];
        var smallClusters = new List<List<int>>();
        int blocked = 0;

        for (int r = 0; r < HRes; r++)
            for (int c = 0; c < HRes; c++)
            {
                if (Walkable[r, c] || Height[r, c] <= WaterLevel) continue;
                blocked++;
                if (seen[r, c]) continue;

                var cells = new List<int>();
                var st = new Stack<int>();
                seen[r, c] = true; st.Push(r * HRes + c);
                while (st.Count > 0)
                {
                    int v = st.Pop(); cells.Add(v);
                    int cr = v / HRes, cc = v % HRes;
                    for (int i = 0; i < 4; i++)
                    {
                        int rr = cr + dR[i], ccc = cc + dC[i];
                        if (rr < 0 || rr >= HRes || ccc < 0 || ccc >= HRes) continue;
                        if (seen[rr, ccc] || Walkable[rr, ccc] || Height[rr, ccc] <= WaterLevel) continue;
                        seen[rr, ccc] = true; st.Push(rr * HRes + ccc);
                    }
                }
                if (cells.Count > maxCells) { foreach (int v in cells) large[v / HRes, v % HRes] = true; }
                else smallClusters.Add(cells);
            }

        // Distance from the large regions, so proximity to a real cliff is cheap
        // to test.
        var nearLarge = (bool[,])large.Clone();
        Inflate(nearLarge, HRes, IsolationCells);

        int specks = 0, speckCells = 0;
        foreach (var cluster in smallClusters)
        {
            bool touching = false;
            foreach (int v in cluster)
                if (nearLarge[v / HRes, v % HRes]) { touching = true; break; }
            if (touching) continue;
            specks++; speckCells += cluster.Count;
        }
        return new float[] { specks, speckCells, blocked };
    }

    /// Sands off the specks. Finds every small blocked cluster and locally
    /// smooths the heightfield around it until it drops under the nav limit.
    /// Only tiny areas are touched, so real cliffs are left alone.
    public static int SmoothPathingSpecks(int maxCells, int passes)
    {
        int removed = 0;
        for (int pass = 0; pass < passes; pass++)
        {
            BuildWalkable();
            var sp = PathingSpecks(maxCells);
            if (sp[0] <= 0) break;

            // Collect the cells belonging to small blocked clusters.
            var seen = new bool[HRes, HRes];
            var target = new bool[HRes, HRes];
            int[] dR = { 1, -1, 0, 0 }, dC = { 0, 0, 1, -1 };
            int found = 0;
            for (int r = 0; r < HRes; r++)
                for (int c = 0; c < HRes; c++)
                {
                    if (seen[r, c] || Walkable[r, c] || Height[r, c] <= WaterLevel) continue;
                    var cells = new List<int>();
                    var st = new Stack<int>();
                    seen[r, c] = true; st.Push(r * HRes + c);
                    while (st.Count > 0 && cells.Count <= maxCells * 4)
                    {
                        int v = st.Pop(); cells.Add(v);
                        int cr = v / HRes, cc = v % HRes;
                        for (int i = 0; i < 4; i++)
                        {
                            int rr = cr + dR[i], ccc = cc + dC[i];
                            if (rr < 0 || rr >= HRes || ccc < 0 || ccc >= HRes) continue;
                            if (seen[rr, ccc] || Walkable[rr, ccc] || Height[rr, ccc] <= WaterLevel) continue;
                            seen[rr, ccc] = true; st.Push(rr * HRes + ccc);
                        }
                    }
                    if (cells.Count > maxCells) continue;
                    found++;
                    foreach (int v in cells)
                    {
                        int cr = v / HRes, cc = v % HRes;
                        for (int dy = -4; dy <= 4; dy++)
                            for (int dx = -4; dx <= 4; dx++)
                            {
                                int rr = cr + dy, ccc = cc + dx;
                                if (rr < 0 || rr >= HRes || ccc < 0 || ccc >= HRes) continue;
                                target[rr, ccc] = true;
                            }
                    }
                }
            if (found == 0) break;
            removed += found;

            // Local box blur over just those neighbourhoods.
            var src = (float[,])Height.Clone();
            for (int r = 0; r < HRes; r++)
                for (int c = 0; c < HRes; c++)
                {
                    if (!target[r, c]) continue;
                    float s = 0f; int n = 0;
                    for (int dy = -3; dy <= 3; dy++)
                        for (int dx = -3; dx <= 3; dx++)
                        {
                            int rr = r + dy, cc = c + dx;
                            if (rr < 0 || rr >= HRes || cc < 0 || cc >= HRes) continue;
                            s += src[rr, cc]; n++;
                        }
                    Height[r, c] = s / n;
                }
            SymmetriseFieldN(Height, HRes);
            RebuildSlope();
        }
        BuildWalkable();
        return removed;
    }

    /// Fraction of dry land steeper than the nav limit, straight from a file.
    public static float SteepFractionOnDisk(string rawPath, int hmRes, float mapSize,
                                            float maxHeight, float waterLevel)
    {
        byte[] raw = File.ReadAllBytes(rawPath);
        var h = new float[hmRes, hmRes];
        for (int r = 0; r < hmRes; r++)
            for (int c = 0; c < hmRes; c++)
            {
                int i = (r * hmRes + c) * 2;
                h[r, c] = (raw[i] | (raw[i + 1] << 8)) * maxHeight / 65535f;
            }
        float step = mapSize / (hmRes - 1);
        int land = 0, steep = 0;
        for (int r = 1; r < hmRes - 1; r++)
            for (int c = 1; c < hmRes - 1; c++)
            {
                if (h[r, c] <= waterLevel) continue;
                land++;
                float dx = (h[r, c + 1] - h[r, c - 1]) / (2 * step);
                float dz = (h[r + 1, c] - h[r - 1, c]) / (2 * step);
                if (Math.Atan(Math.Sqrt(dx * dx + dz * dz)) * 180.0 / Math.PI > MaxNavSlopeDeg) steep++;
            }
        return land > 0 ? steep / (float)land : 0f;
    }

    // ---- symmetry --------------------------------------------------------

    /// One quarter-turn on the grid, matching a quarter-turn in world space
    /// under row = (MapSize - z)/step, col = x/step.
    static void RotIdxQuarter(int r, int c, int n, out int rr, out int cc)
    {
        rr = n - 1 - c;
        cc = r;
    }

    static void RotIdx(int r, int c, int n, int quarters, out int rr, out int cc)
    {
        rr = r; cc = c;
        for (int i = 0; i < quarters; i++)
        {
            RotIdxQuarter(rr, cc, n, out int a, out int b);
            rr = a; cc = b;
        }
    }

    /// Quarter-turns between successive symmetric copies.
    static int QuartersPerStep { get { return SymOrder == 4 ? 1 : 2; } }

    public static void RotateWorld(float x, float z, int step, out float ox, out float oz)
    {
        ox = x; oz = z;
        int quarters = step * QuartersPerStep;
        for (int i = 0; i < quarters; i++)
        {
            float nx = MapSize - oz;
            float nz = ox;
            ox = nx; oz = nz;
        }
    }

    /// Average a float field over all symmetric rotations.
    public static void SymmetriseFieldN(float[,] a, int n)
    {
        var outp = new float[n, n];
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                float s = 0f;
                for (int k = 0; k < SymOrder; k++)
                {
                    RotIdx(r, c, n, k * QuartersPerStep, out int rr, out int cc);
                    s += a[rr, cc];
                }
                outp[r, c] = s / SymOrder;
            }
        Array.Copy(outp, a, outp.Length);
    }

    /// Union a boolean mask over all symmetric rotations.
    public static void SymmetriseMaskN(bool[,] m, int n)
    {
        var outp = new bool[n, n];
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                bool v = false;
                for (int k = 0; k < SymOrder && !v; k++)
                {
                    RotIdx(r, c, n, k * QuartersPerStep, out int rr, out int cc);
                    if (m[rr, cc]) v = true;
                }
                outp[r, c] = v;
            }
        Array.Copy(outp, m, outp.Length);
    }

    // ---- spawns ----------------------------------------------------------

    /// Spawns on a ring, `perSector` of them inside one symmetry sector and the
    /// rest generated by rotation, so every player's surroundings are identical.
    public static void PlaceSpawnsRadial(int perSector, float radiusFrac, float phaseDeg)
    {
        int total = perSector * SymOrder;
        var xs = new float[total];
        var zs = new float[total];
        float cx = MapSize * 0.5f, cz = MapSize * 0.5f;
        float r = MapSize * radiusFrac;
        double sector = 2.0 * Math.PI / SymOrder;

        int idx = 0;
        for (int s = 0; s < SymOrder; s++)
            for (int p = 0; p < perSector; p++)
            {
                // spread the sector's spawns across it, kept off its edges
                double frac = (p + 1.0) / (perSector + 1.0);
                double ang = phaseDeg * Math.PI / 180.0 + sector * (s + frac);
                xs[idx] = cx + (float)Math.Cos(ang) * r;
                zs[idx] = cz + (float)Math.Sin(ang) * r;
                idx++;
            }
        BaseX = xs; BaseZ = zs;
    }

    // ---- resource placement ---------------------------------------------

    /// True where a resource spot could sit: dry, level, clear of the channel,
    /// inside the playable border, and reachable on foot.
    static bool SpotViable(float x, float z, bool[,] reach, float maxSlope, float minRiver)
    {
        float border = MapSize * 0.035f;
        if (x < border || x > MapSize - border || z < border || z > MapSize - border) return false;
        if (HeightAtWorld(x, z) <= WaterLevel + 1.0f) return false;
        if (SlopeAtWorld(x, z) > maxSlope) return false;
        if (UseRiver && Math.Abs(RiverDist(x, z)) < minRiver) return false;
        return reach == null || IsReachable(reach, x, z);
    }

    /// Rejection-sampled resource spots for sector 0, returned as a flat
    /// (x, z, x, z, ...) array. Callers rotate them into the other sectors.
    ///
    /// Spots come in three bands so the economy has a shape: a cluster the owner
    /// can take safely, a middle ring worth expanding to, and contested ground.
    public static float[] PlaceResourcesForSector(int seed, bool[,] reach,
                                                  int nearCount, int midCount, int farCount,
                                                  float minSpacing, float maxSlope, float minRiver,
                                                  int sectorSpawns)
    {
        var rng = new Random(seed);
        var placed = new List<float>();
        float cx = MapSize * 0.5f, cz = MapSize * 0.5f;

        if (sectorSpawns < 1) sectorSpawns = 1;
        float sectorHalf = (float)(Math.PI / SymOrder);
        float spawnAng = (float)Math.Atan2(BaseZ[0] - cz, BaseX[0] - cx);

        // The near band is per spawn, not per sector: with more than one spawn
        // in a sector, anchoring it all on BaseX[0] gives that player a home
        // cluster and the others nothing.
        var bands = new[]
        {
            new[] { (float)(nearCount * sectorSpawns), MapSize * 0.055f, MapSize * 0.16f },
            new[] { (float)midCount,  MapSize * 0.18f,  MapSize * 0.34f },
            new[] { (float)farCount,  MapSize * 0.30f,  MapSize * 0.46f },
        };

        bool nearBand = true;
        int nearIdx = 0;
        foreach (var band in bands)
        {
            int want = (int)band[0];
            float rMin = band[1], rMax = band[2];
            for (int i = 0, guard = 0; i < want && guard < 9000; guard++)
            {
                float x, z;
                if (nearBand)
                {
                    // round-robin across this sector's spawns
                    int si = (nearIdx + i) % sectorSpawns;
                    float sx = BaseX[si], sz = BaseZ[si];
                    double a = rng.NextDouble() * Math.PI * 2.0;
                    float d = rMin + (float)rng.NextDouble() * (rMax - rMin);
                    x = sx + (float)Math.Cos(a) * d;
                    z = sz + (float)Math.Sin(a) * d;
                }
                else
                {
                    // anywhere inside this sector's wedge, at this radius
                    double a = spawnAng + (rng.NextDouble() * 2.0 - 1.0) * sectorHalf;
                    float d = rMin + (float)rng.NextDouble() * (rMax - rMin);
                    x = cx + (float)Math.Cos(a) * d;
                    z = cz + (float)Math.Sin(a) * d;
                }

                if (!SpotViable(x, z, reach, maxSlope, minRiver)) continue;

                // keep clear of the spawns themselves, and of each other -
                // including each other's symmetric copies, or spots near the
                // centre end up stacked on their own rotations
                bool bad = false;
                for (int b = 0; b < BaseX.Length && !bad; b++)
                {
                    float dx = x - BaseX[b], dz = z - BaseZ[b];
                    if (dx * dx + dz * dz < (MapSize * 0.05f) * (MapSize * 0.05f)) bad = true;
                }
                for (int p = 0; p < placed.Count && !bad; p += 2)
                    for (int k = 0; k < SymOrder && !bad; k++)
                    {
                        RotateWorld(placed[p], placed[p + 1], k, out float px, out float pz);
                        float dx = x - px, dz = z - pz;
                        if (dx * dx + dz * dz < minSpacing * minSpacing) bad = true;
                    }
                for (int k = 1; k < SymOrder && !bad; k++)
                {
                    RotateWorld(x, z, k, out float px, out float pz);
                    float dx = x - px, dz = z - pz;
                    if (dx * dx + dz * dz < minSpacing * minSpacing) bad = true;
                }
                if (bad) continue;

                placed.Add(x); placed.Add(z);
                i++;
            }
            nearBand = false;
        }
        return placed.ToArray();
    }

    // ---- the quality gate ------------------------------------------------

    /// Everything a caller needs to decide whether a roll is worth keeping.
    /// { reachableFrac, openFrac, flatFrac, cliffFrac, spawnsConnected,
    ///   resourcesReachable, minSpawnSeparation }
    public static float[] Evaluate(bool[,] reach, float[] resourceX, float[] resourceZ)
    {
        int walk = WalkableCount();
        int rc = CountTrue(reach);
        var og = OpenGroundStats(6f);
        var ts = TerrainStats();
        float land = ts[2];

        float connected = 1f;
        for (int i = 1; i < BaseX.Length; i++)
            if (!IsReachable(reach, BaseX[i], BaseZ[i])) connected = 0f;

        float resOk = 1f;
        if (resourceX != null)
            for (int i = 0; i < resourceX.Length; i++)
                if (!IsReachable(reach, resourceX[i], resourceZ[i])) resOk = 0f;

        float minSep = float.MaxValue;
        for (int i = 0; i < BaseX.Length; i++)
            for (int j = i + 1; j < BaseX.Length; j++)
            {
                float dx = BaseX[i] - BaseX[j], dz = BaseZ[i] - BaseZ[j];
                minSep = Math.Min(minSep, (float)Math.Sqrt(dx * dx + dz * dz));
            }
        if (minSep == float.MaxValue) minSep = MapSize;

        return new[]
        {
            walk > 0 ? rc / (float)walk : 0f,
            og[1] > 0 ? og[0] / og[1] : 0f,
            land > 0 ? ts[3] / land : 0f,
            land > 0 ? ts[6] / land : 0f,
            connected, resOk, minSep
        };
    }
}
