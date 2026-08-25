using System;
using System.Collections.Generic;

// Mask-based terrain, modelled on FAForever's Neroxis generator.
//
// The approach I had been using - threshold a noise field - can only produce
// two things: plain fbm gives round blobs (its peaks are isolated), and ridged
// fbm gives ribbons that wall the map into a maze. Neither is what an eroded
// plateau looks like, and the second is unplayable.
//
// Neroxis builds shape from *masks* instead, and its plateau pipeline is:
//
//     path in centre bounds  ->  inflate(mapSize/256)  ->  setSize(mapSize/4)
//     ->  dilute(0.5, 4)     ->  setSize(mapSize+1)    ->  blur(12)
//
// Three ideas do the work there:
//   * random walks give a branching skeleton, so the result is connected and
//     limb-like rather than blobby;
//   * the round trip down to quarter resolution and back is what makes the
//     outline organic - detail is destroyed and then interpolated back, which
//     no amount of noise tuning reproduces;
//   * probabilistic dilation roughens the boundary without adding new barriers.
//
// Heights then come from a distance transform of the finished mask rather than
// from noise, which is what finally makes the gradients controllable: the
// height at a cell is a function of how far inside the plateau it is, so the
// cliff width IS the edge slope, exactly and by construction. Ramps are just
// places where that width is locally much larger.
public static partial class MapGen
{
    /// Terrain from path-drawn masks plus distance-field heights.
    public static bool PathedMesas = false;

    public static int   MesaPathCount   = 6;      // per symmetric half
    public static float MesaInflate     = 0.013f; // fraction of map size
    public static float MesaBlurRadius  = 0.011f;
    public static float MesaMinAreaFrac = 0.0016f;
    public static float TierTwoDeflate  = 0.055f; // inset for the upper tier
    public static int   MesaRampCount   = 5;      // ramps per symmetric half
    public static float MesaRampWidth   = 13f;    // metres, half-width
    /// Minimum gap between path starts, as a fraction of map size. Too large
    /// and the rejection sampler starves: a quarter-symmetry sector has little
    /// room once the spawn pads and any basin are excluded.
    public static float MesaMinSep      = 0.20f;
    /// Radius around the map centre where path starts are refused.
    public static float MesaCentreClear = 0.20f;

    /// Cells to shrink the finished plateau mask by, widening every channel
    /// between plateaus by twice this. The lane between spawns was four times
    /// narrower than the corpus before this existed.
    public static int   MesaChannelWiden = 4;

    /// Precomputed plateau height in metres, on the HRes grid.
    static float[,] MesaField;

    // ---- small mask library ---------------------------------------------

    static bool[,] NewMask(int n) { return new bool[n, n]; }

    static void Stamp(bool[,] m, int n, float cx, float cy, float r)
    {
        int r0 = (int)Math.Floor(cy - r), r1 = (int)Math.Ceiling(cy + r);
        int c0 = (int)Math.Floor(cx - r), c1 = (int)Math.Ceiling(cx + r);
        float r2 = r * r;
        for (int y = Math.Max(0, r0); y <= Math.Min(n - 1, r1); y++)
            for (int x = Math.Max(0, c0); x <= Math.Min(n - 1, c1); x++)
            {
                float dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy <= r2) m[y, x] = true;
            }
    }

    /// Random walk from a to b with inertia and bounded angle error - Neroxis's
    /// path(). The wander is what stops plateaus looking like drawn lines.
    static void WalkPath(bool[,] m, int n, Random rng,
                         float ax, float ay, float bx, float by,
                         float maxStep, float maxAngleError, float brush)
    {
        float x = ax, y = ay;
        int guard = 0;
        while (guard++ < 4000)
        {
            float dx = bx - x, dy = by - y;
            float d = (float)Math.Sqrt(dx * dx + dy * dy);
            if (d < maxStep) break;
            float ang = (float)Math.Atan2(dy, dx);
            ang += (float)((rng.NextDouble() - 0.5) * 2.0 * maxAngleError);
            float step = maxStep * (0.5f + (float)rng.NextDouble() * 0.5f);
            x += (float)Math.Cos(ang) * step;
            y += (float)Math.Sin(ang) * step;
            Stamp(m, n, x, y, brush);
        }
        Stamp(m, n, bx, by, brush);
    }

    static void Inflate(bool[,] m, int n, int radius)
    {
        if (radius <= 0) return;
        var src = (bool[,])m.Clone();
        int r2 = radius * radius;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                if (!src[y, x]) continue;
                for (int dy = -radius; dy <= radius; dy++)
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (dx * dx + dy * dy > r2) continue;
                        int yy = y + dy, xx = x + dx;
                        if (yy < 0 || yy >= n || xx < 0 || xx >= n) continue;
                        m[yy, xx] = true;
                    }
            }
    }

    static void Deflate(bool[,] m, int n, int radius)
    {
        if (radius <= 0) return;
        var inv = new bool[n, n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                inv[y, x] = !m[y, x];
        Inflate(inv, n, radius);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                m[y, x] = !inv[y, x];
    }

    /// Probabilistic boundary growth. Roughens an outline the way a blur plus
    /// threshold cannot, because the randomness is per-cell.
    static void Dilute(bool[,] m, int n, float strength, int count, Random rng)
    {
        for (int it = 0; it < count; it++)
        {
            var src = (bool[,])m.Clone();
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    if (src[y, x]) continue;
                    bool near = (x > 0 && src[y, x - 1]) || (x < n - 1 && src[y, x + 1]) ||
                                (y > 0 && src[y - 1, x]) || (y < n - 1 && src[y + 1, x]);
                    if (near && rng.NextDouble() < strength) m[y, x] = true;
                }
        }
    }

    /// Nearest-neighbour resize. The destructive round trip to a quarter of the
    /// resolution and back is the point, not a limitation.
    static bool[,] Resize(bool[,] m, int from, int to)
    {
        var outp = new bool[to, to];
        for (int y = 0; y < to; y++)
        {
            int sy = Math.Min(from - 1, (int)((y + 0.5f) * from / to));
            for (int x = 0; x < to; x++)
            {
                int sx = Math.Min(from - 1, (int)((x + 0.5f) * from / to));
                outp[y, x] = m[sy, sx];
            }
        }
        return outp;
    }

    /// Box blur the mask as a field, then re-threshold. Rounds off the stair
    /// steps that the resize leaves behind.
    static void BlurThreshold(bool[,] m, int n, int radius, float level)
    {
        if (radius <= 0) return;
        var f = new float[n, n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                f[y, x] = m[y, x] ? 1f : 0f;

        var tmp = new float[n, n];
        for (int y = 0; y < n; y++)          // horizontal
            for (int x = 0; x < n; x++)
            {
                float s = 0f; int c = 0;
                for (int d = -radius; d <= radius; d++)
                {
                    int xx = x + d; if (xx < 0 || xx >= n) continue;
                    s += f[y, xx]; c++;
                }
                tmp[y, x] = s / c;
            }
        for (int x = 0; x < n; x++)          // vertical
            for (int y = 0; y < n; y++)
            {
                float s = 0f; int c = 0;
                for (int d = -radius; d <= radius; d++)
                {
                    int yy = y + d; if (yy < 0 || yy >= n) continue;
                    s += tmp[yy, x]; c++;
                }
                m[y, x] = (s / c) >= level;
            }
    }

    /// Connected components of the true cells, 4-connected.
    static List<List<int>> Components(bool[,] m, int n)
    {
        var res = new List<List<int>>();
        var seen = new bool[n, n];
        int[] dR = { 1, -1, 0, 0 }, dC = { 0, 0, 1, -1 };
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                if (seen[y, x] || !m[y, x]) continue;
                var cells = new List<int>();
                var st = new Stack<int>();
                seen[y, x] = true; st.Push(y * n + x);
                while (st.Count > 0)
                {
                    int v = st.Pop(); cells.Add(v);
                    int cy = v / n, cx = v % n;
                    for (int i = 0; i < 4; i++)
                    {
                        int yy = cy + dR[i], xx = cx + dC[i];
                        if (yy < 0 || yy >= n || xx < 0 || xx >= n) continue;
                        if (seen[yy, xx] || !m[yy, xx]) continue;
                        seen[yy, xx] = true; st.Push(yy * n + xx);
                    }
                }
                res.Add(cells);
            }
        return res;
    }

    static void RemoveAreasSmallerThan(bool[,] m, int n, int minArea)
    {
        var seen = new bool[n, n];
        int[] dR = { 1, -1, 0, 0 }, dC = { 0, 0, 1, -1 };
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                if (seen[y, x] || !m[y, x]) continue;
                var cells = new List<int>();
                var st = new Stack<int>();
                seen[y, x] = true; st.Push(y * n + x);
                while (st.Count > 0)
                {
                    int v = st.Pop(); cells.Add(v);
                    int cy = v / n, cx = v % n;
                    for (int i = 0; i < 4; i++)
                    {
                        int yy = cy + dR[i], xx = cx + dC[i];
                        if (yy < 0 || yy >= n || xx < 0 || xx >= n) continue;
                        if (seen[yy, xx] || !m[yy, xx]) continue;
                        seen[yy, xx] = true; st.Push(yy * n + xx);
                    }
                }
                if (cells.Count < minArea)
                    foreach (int v in cells) m[v / n, v % n] = false;
            }
    }

    /// OR the mask with its own 180-degree rotation.
    static void SymmetriseMask(bool[,] m, int n)
    {
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                bool v = m[y, x] || m[n - 1 - y, n - 1 - x];
                m[y, x] = v; m[n - 1 - y, n - 1 - x] = v;
            }
    }

    /// Chamfer distance transform: cells inside the mask get their distance to
    /// the nearest outside cell, in grid units. This is what turns a flat mask
    /// into terrain with an exactly known edge gradient.
    static float[,] DistanceInside(bool[,] m, int n)
    {
        const float BIG = 1e9f;
        var d = new float[n, n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                d[y, x] = m[y, x] ? BIG : 0f;

        const float A = 1f, B = 1.41421356f;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                if (d[y, x] == 0f) continue;
                float v = d[y, x];
                if (y > 0)              v = Math.Min(v, d[y - 1, x] + A);
                if (x > 0)              v = Math.Min(v, d[y, x - 1] + A);
                if (y > 0 && x > 0)     v = Math.Min(v, d[y - 1, x - 1] + B);
                if (y > 0 && x < n - 1) v = Math.Min(v, d[y - 1, x + 1] + B);
                d[y, x] = v;
            }
        for (int y = n - 1; y >= 0; y--)
            for (int x = n - 1; x >= 0; x--)
            {
                if (d[y, x] == 0f) continue;
                float v = d[y, x];
                if (y < n - 1)          v = Math.Min(v, d[y + 1, x] + A);
                if (x < n - 1)          v = Math.Min(v, d[y, x + 1] + A);
                if (y < n - 1 && x < n - 1) v = Math.Min(v, d[y + 1, x + 1] + B);
                if (y < n - 1 && x > 0)     v = Math.Min(v, d[y + 1, x - 1] + B);
                d[y, x] = v;
            }
        return d;
    }

    static float[,] BlurField(float[,] f, int n, int radius)
    {
        if (radius <= 0) return f;
        var tmp = new float[n, n];
        var outp = new float[n, n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float s = 0f; int c = 0;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int xx = x + dx; if (xx < 0 || xx >= n) continue;
                    s += f[y, xx]; c++;
                }
                tmp[y, x] = s / c;
            }
        for (int x = 0; x < n; x++)
            for (int y = 0; y < n; y++)
            {
                float s = 0f; int c = 0;
                for (int dy = -radius; dy <= radius; dy++)
                {
                    int yy = y + dy; if (yy < 0 || yy >= n) continue;
                    s += tmp[yy, x]; c++;
                }
                outp[y, x] = s / c;
            }
        return outp;
    }

    // ---- the pipeline ----------------------------------------------------

    /// Keeps mesas off the things that have to stay open.
    /// Where each spawn lane aims. Empty means the map centre.
    ///
    /// Aiming everything at the centre is wrong for the water styles: on Basin
    /// the centre is a flooded bowl and on RiverCrossing it is the channel, so
    /// the corridor was guaranteed to run straight into water the army cannot
    /// cross. Those styles set the target to their crossing instead.
    public static float[] LaneTargetX = new float[0];
    public static float[] LaneTargetZ = new float[0];

    /// Half-width in metres of the guaranteed lane from each spawn to the map
    /// centre. Zero disables it.
    ///
    /// This is the structural difference between our maps and hand-made ones.
    /// A designer lays out the lanes first and puts terrain around them; the
    /// generator was scattering plateaus and hoping a decent route survived.
    /// Measured, it did not: the route between spawns came out 26% longer than
    /// the straight line where the Supreme Commander corpus manages 7%.
    ///
    /// Carving spawn-to-centre rather than spawn-to-spawn keeps it symmetric
    /// for free - the rotations of one spoke are the other lanes, and at
    /// 180 degrees the two spokes are the single diagonal.
    public static float LaneHalfWidth = 20f;

    /// How far the lane wanders off the straight line, in metres.
    ///
    /// A dead straight corridor reads as a motorway, but the wander is not
    /// free: a sine of amplitude A over a spoke of length L lengthens the path
    /// by about (2*pi*A/L)^2/4, so 26 m over a 205 m spoke cost 13% directness
    /// on its own - more than the whole gap we were trying to close. Ten metres
    /// costs about 2% and leaves the rest of the budget for the route weaving
    /// around actual terrain.
    public static float LaneWander = 10f;

    /// Distance from a point to the wandering lane that runs from spawn `i` to
    /// the map centre, or a big number if it is nowhere near.
    static float LaneDistance(float wx, float wz)
    {
        if (LaneHalfWidth <= 0f || BaseX.Length == 0) return 1e9f;
        float cx = MapSize * 0.5f, cz = MapSize * 0.5f;
        float best = 1e9f;

        for (int i = 0; i < BaseX.Length; i++)
        {
            float sx = BaseX[i], sz = BaseZ[i];
            float tx = i < LaneTargetX.Length ? LaneTargetX[i] : cx;
            float tz = i < LaneTargetZ.Length ? LaneTargetZ[i] : cz;
            float vx = tx - sx, vz = tz - sz;
            float len2 = vx * vx + vz * vz;
            if (len2 < 1f) continue;

            float t = ((wx - sx) * vx + (wz - sz) * vz) / len2;
            if (t < 0f || t > 1f) continue;              // beyond the spoke's ends

            // Offset the centreline sideways by a sine in t. One full period
            // over the spoke keeps the wander antisymmetric about the map
            // centre, so the 180-degree rotation maps the lane onto its
            // partner exactly and the map stays fair.
            float len = (float)Math.Sqrt(len2);
            float nx = -vz / len, nz = vx / len;         // unit normal
            float off = LaneWander * (float)Math.Sin(t * Math.PI * 2.0);

            float px = sx + vx * t + nx * off;
            float pz = sz + vz * t + nz * off;
            float d = (float)Math.Sqrt((wx - px) * (wx - px) + (wz - pz) * (wz - pz));
            if (d < best) best = d;
        }
        return best;
    }

    static bool MesaForbidden(float wx, float wz)
    {
        // The lane comes first: nothing may build across it.
        if (LaneDistance(wx, wz) < LaneHalfWidth) return true;

        // Keep mesas out of a central depression, or they fill the basin in and
        // it never floods - the whole point of the style.
        if (BowlDepth > 0f)
        {
            float bcx = MapSize * 0.5f, bcz = MapSize * 0.5f;
            float bd = (float)Math.Sqrt((wx - bcx) * (wx - bcx) + (wz - bcz) * (wz - bcz));
            if (bd < MapSize * BowlRadiusFrac * 0.72f) return true;
        }

        for (int i = 0; i < BaseX.Length; i++)
        {
            float dx = wx - BaseX[i], dz = wz - BaseZ[i];
            if (dx * dx + dz * dz < (PadRadius + PadBlend + 20f) * (PadRadius + PadBlend + 20f)) return true;
        }
        if (UseRiver)
        {
            float corridor = (RiverCoreHalf + ShelfWidth) * WidthScale(wx, wz) + RiverBank;
            if (Math.Abs(RiverDist(wx, wz)) < corridor + 10f) return true;
            for (int i = 0; i < BaseX.Length; i++)
            {
                float ax = BaseX[i], az = BaseZ[i];
                int nb = NearestBridge(ax, az);
                float bx = BridgeX[nb], bz = BridgeZ[nb];
                float vx = bx - ax, vz = bz - az;
                float len2 = vx * vx + vz * vz;
                if (len2 < 1f) continue;
                float t = Clamp01(((wx - ax) * vx + (wz - az) * vz) / len2);
                float px = ax + vx * t, pz = az + vz * t;
                float dd = (wx - px) * (wx - px) + (wz - pz) * (wz - pz);
                if (dd < 22f * 22f) return true;
            }
        }
        return false;
    }

    public static void BuildMesaField(int seed, float tier1Height, float tier2Height)
    {
        int n = HRes;
        float step = MapSize / (n - 1);
        var rng = new Random(seed);

        // 1. branching skeletons from random walks.
        //
        // Paths are drawn on ONE bank only and the 180-degree union supplies the
        // other. Drawing across the whole map and then OR-ing the rotation makes
        // both halves overlap through the middle, which is what fused everything
        // into a single massif per side. Starts are also spaced apart, so the
        // result is several distinct landforms rather than one sprawl.
        var plateau = NewMask(n);
        float bound = n * 0.12f;
        float maxStep = n * 0.028f;
        float minSep = n * MesaMinSep;
        var starts = new List<float[]>();

        for (int p = 0, guard = 0; p < MesaPathCount && guard < 4000; guard++)
        {
            float ax = bound + (float)rng.NextDouble() * (n - 2 * bound);
            float ay = bound + (float)rng.NextDouble() * (n - 2 * bound);
            float wx = ax * step, wz = MapSize - ay * step;

            // side A only, clear of the channel and the keep-outs
            if (UseRiver && RiverDist(wx, wz) < (RiverCoreHalf + ShelfWidth) * WidthScale(wx, wz) + RiverBank + 28f)
                continue;
            if (MesaForbidden(wx, wz)) continue;

            // Every rotation of a start near the middle lands near the middle,
            // so central starts fuse into one blob spanning the map centre - an
            // X at 90-degree symmetry. Keep them out of it.
            float ddx = wx - MapSize * 0.5f, ddz = wz - MapSize * 0.5f;
            if (ddx * ddx + ddz * ddz < (MapSize * MesaCentreClear) * (MapSize * MesaCentreClear)) continue;

            bool tooClose = false;
            foreach (var s in starts)
            {
                float dx = s[0] - ax, dy = s[1] - ay;
                if (dx * dx + dy * dy < minSep * minSep) { tooClose = true; break; }
            }
            if (tooClose) continue;
            starts.Add(new[] { ax, ay });
            p++;

            // Limbs wander to a target rather than running straight: a straight
            // spoke survives the blur as a rectangular tab, a wandering one
            // survives as a limb.
            int mids = 1 + rng.Next(2);
            float cx = ax, cy = ay;
            for (int k = 0; k <= mids; k++)
            {
                float bx = cx, by = cy;
                // Grow outward from the map centre, within a wide cone. Limbs
                // aimed inward converge under rotation and fuse every sector
                // into one central mass - a four-armed X at 90-degree symmetry.
                float owx = cx * step, owz = MapSize - cy * step;
                float outAng = (float)Math.Atan2(owz - MapSize * 0.5f, owx - MapSize * 0.5f);
                for (int tries = 0; tries < 40; tries++)
                {
                    float ang = outAng + (float)((rng.NextDouble() - 0.5) * 2.0 * 1.15);
                    float len = n * (0.06f + (float)rng.NextDouble() * 0.09f);
                    float tx = cx + (float)Math.Cos(ang) * len;
                    float ty = cy + (float)Math.Sin(ang) * len;
                    if (tx < bound || tx > n - bound || ty < bound || ty > n - bound) continue;
                    float twx = tx * step, twz = MapSize - ty * step;
                    if (UseRiver && RiverDist(twx, twz) < (RiverCoreHalf + ShelfWidth) * WidthScale(twx, twz) + RiverBank + 22f) continue;
                    if (MesaForbidden(twx, twz)) continue;
                    bx = tx; by = ty; break;
                }
                WalkPath(plateau, n, rng, cx, cy, bx, by, maxStep, 1.35f, 2f);
                cx = bx; cy = by;
            }
        }

        // 2. inflate the skeleton into ribbons
        Inflate(plateau, n, Math.Max(2, (int)(n * MesaInflate)));

        // 3. the destructive round trip: quarter resolution, roughen, back up
        int low = Math.Max(48, n / 4);
        var lowMask = Resize(plateau, n, low);
        Dilute(lowMask, low, 0.42f, 2, rng);
        plateau = Resize(lowMask, low, n);

        // 4. smooth the resize stair steps back off
        BlurThreshold(plateau, n, Math.Max(2, (int)(n * MesaBlurRadius)), 0.5f);

        // 5. drop specks, enforce symmetry, drop specks again
        RemoveAreasSmallerThan(plateau, n, (int)(n * n * MesaMinAreaFrac));
        SymmetriseMask(plateau, n);

        // 6. clear the channel, the base pads and the bridge approaches
        for (int y = 0; y < n; y++)
        {
            float wz = MapSize - y * step;
            for (int x = 0; x < n; x++)
                if (plateau[y, x] && MesaForbidden(x * step, wz)) plateau[y, x] = false;
        }
        RemoveAreasSmallerThan(plateau, n, (int)(n * n * MesaMinAreaFrac));
        SymmetriseMask(plateau, n);

        // 6b. widen the channels between plateaus.
        //
        // Measured against 217 Supreme Commander maps and 42 shipped Sanctuary
        // maps, the route between two spawns has a median clearance of 15 m and
        // 34 m respectively - ours was 4 to 8. The plateaus were not too large,
        // they were too close together, and the gaps between them were the
        // whole problem. Deflating the finished mask widens every channel by
        // twice the deflate without changing the layout, which is the cheapest
        // lever that moves the metric that matters.
        //
        // Small plateaus vanish under this, which is why the despeckle repeats
        // afterwards.
        if (MesaChannelWiden > 0)
        {
            Deflate(plateau, n, MesaChannelWiden);
            RemoveAreasSmallerThan(plateau, n, (int)(n * n * MesaMinAreaFrac));
            SymmetriseMask(plateau, n);
        }

        // 7. ramps: corridors that straddle the plateau boundary. Cut into the
        //    mask now, as width, rather than bulldozed into the heights later.
        var ramp = NewMask(n);
        var dOut = DistanceInside(plateau, n);

        // One ramp per plateau, not per random edge cell: picking edges at
        // random leaves whole massifs sealed, which is what forced the
        // heightfield-level carver to bulldoze corridors afterwards.
        foreach (var comp in Components(plateau, n))
        {
            var edgeCells = new List<int>();
            foreach (int v in comp)
            {
                int y = v / n, x = v % n;
                if (y == 0 || y == n - 1 || x == 0 || x == n - 1) continue;
                if (!plateau[y - 1, x] || !plateau[y + 1, x] ||
                    !plateau[y, x - 1] || !plateau[y, x + 1]) edgeCells.Add(v);
            }
            if (edgeCells.Count == 0) continue;

            // Two ramps on anything sizeable, spaced apart around the rim so a
            // single blocked approach cannot isolate the top.
            int wanted = comp.Count > n * n * MesaMinAreaFrac * 6 ? 2 : 1;
            var chosen = new List<int>();
            for (int k = 0; k < wanted; k++)
            {
                int best = -1; double bestScore = -1;
                for (int tries = 0; tries < 64; tries++)
                {
                    int cand = edgeCells[rng.Next(edgeCells.Count)];
                    double score = double.MaxValue;
                    foreach (int c in chosen)
                    {
                        int dy = cand / n - c / n, dx = cand % n - c % n;
                        score = Math.Min(score, Math.Sqrt(dy * (double)dy + dx * (double)dx));
                    }
                    if (chosen.Count == 0) { best = cand; break; }
                    if (score > bestScore) { bestScore = score; best = cand; }
                }
                if (best < 0) break;
                chosen.Add(best);
            }

            foreach (int pick in chosen)
            {
                int ey = pick / n, ex = pick % n;
                // inward direction = uphill on the interior distance field
                float gx = 0f, gy = 0f;
                if (ex > 0 && ex < n - 1) gx = dOut[ey, ex + 1] - dOut[ey, ex - 1];
                if (ey > 0 && ey < n - 1) gy = dOut[ey + 1, ex] - dOut[ey - 1, ex];
                float gl = (float)Math.Sqrt(gx * gx + gy * gy);
                if (gl < 1e-3f) { gx = 1f; gy = 0f; gl = 1f; }
                gx /= gl; gy /= gl;
                float half = MesaRampWidth / step;
                float reach = 3.2f * Math.Max(tier1Height, 1f) / step;   // both sides of the edge
                for (float s = -reach; s <= reach; s += 0.5f)
                    Stamp(ramp, n, ex + gx * s, ey + gy * s, half);
            }
        }
        SymmetriseMask(ramp, n);

        // 8. heights from the distance field. Cliff edge is narrow, ramp edge is
        //    wide enough that tier1Height over it stays under the nav limit, and
        //    the ramp profile is linear so its steepest metre equals its average.
        var dist = DistanceInside(plateau, n);
        var rampF = new float[n, n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                rampF[y, x] = ramp[y, x] ? 1f : 0f;
        rampF = BlurField(rampF, n, Math.Max(2, (int)(MesaRampWidth / step * 0.6f)));

        float cliffEdge = 7f / step;                                    // grid units
        float rampEdge  = (tier1Height / (float)Math.Tan(RampSlopeTarget * Math.PI / 180.0)) / step;

        // upper tier: the same mask inset, so it nests inside tier 1
        var upper = (bool[,])plateau.Clone();
        Deflate(upper, n, Math.Max(2, (int)(n * TierTwoDeflate)));
        RemoveAreasSmallerThan(upper, n, (int)(n * n * MesaMinAreaFrac * 0.5f));
        SymmetriseMask(upper, n);
        var distUp = DistanceInside(upper, n);

        MesaField = new float[n, n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float rf = Clamp01(rampF[y, x]);
                float e1 = Lerp(cliffEdge, rampEdge, rf);
                float t1 = Clamp01(dist[y, x] / Math.Max(1f, e1));
                // linear where it is a ramp, eased where it is a cliff
                float p1 = Lerp(Smooth(t1), t1, rf);

                float e2 = Lerp(cliffEdge, rampEdge, rf);
                float t2 = Clamp01(distUp[y, x] / Math.Max(1f, e2));
                float p2 = Lerp(Smooth(t2), t2, rf);

                MesaField[y, x] = tier1Height * p1 + tier2Height * p2;
            }

        // A light blur keeps the chamfer transform's faceting off the surface
        // without rounding the cliff tops away.
        MesaField = BlurField(MesaField, n, 2);
    }

    /// Bilinear sample of the precomputed plateau field.
    public static float MesaAt(float x, float z)
    {
        if (MesaField == null) return 0f;
        int n = HRes;
        float step = MapSize / (n - 1);
        float fc = x / step, fr = (MapSize - z) / step;
        int c0 = Math.Max(0, Math.Min(n - 2, (int)Math.Floor(fc)));
        int r0 = Math.Max(0, Math.Min(n - 2, (int)Math.Floor(fr)));
        float tc = Clamp01(fc - c0), tr = Clamp01(fr - r0);
        return Lerp(Lerp(MesaField[r0, c0], MesaField[r0, c0 + 1], tc),
                    Lerp(MesaField[r0 + 1, c0], MesaField[r0 + 1, c0 + 1], tc), tr);
    }
}
