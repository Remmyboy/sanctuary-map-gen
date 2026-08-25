using System;
using System.IO;

// Terrain / splat / preview generation for Sanctuary: Shattered Sun maps.
//
// Row convention, derived from Load.ReadRaw(flipVertically:true) feeding
// TerrainData.SetHeights(heights[y,x]):  file row 0 -> world z = MapSize (top),
// file column 0 -> world x = 0 (left).  So arrays are ordinary top-left-origin
// images.  The stratum TGAs use the same row order (verified by correlating
// height against splat channels on ~TEAM-1v1_Tropical_256_47940).
public static partial class MapGen
{
    // ---- map configuration ---------------------------------------------
    public static float MapSize    = 256f;
    public static float MaxHeight  = 128f;  // raw 65535 == this many metres
    public static float WaterLevel = 16f;
    public static float LandBase   = 21f;
    public static float RiverBed   = 13f;
    public static float DeckHeight = 19.8f;

    public static int   HRes = 257;         // heightmap resolution (MapSize+1)
    public static int   SRes = 512;         // stratum / splat resolution

    /// Terrain mode. The river map uses analytic plateaus and a carved channel;
    /// the organic mode thresholds a domain-warped noise field into branching
    /// mesas instead, and relies on CarveRamps to make them reachable.
    public static bool UseRiver = false;   // opt in; see BridgesPlaced
    public static bool Organic  = false;

    /// Replaces the sine meander with 1-D noise, and lets the channel widen and
    /// narrow along its length. Still exactly 180-degree symmetric: the offset
    /// is antisymmetrised about t=0.5 and the width is symmetrised.
    public static bool OrganicRiver = false;
    /// Lays the organic mesa field over the river map's rolling ground,
    /// suppressed near the channel, the base pads and the bridge approaches.
    public static bool OrganicHills = false;
    public static float HillStrength = 1f;

    /// Central depression, in metres. Past the water level this becomes a lake.
    public static float BowlDepth = 0f;
    public static float BowlRadiusFrac = 0.32f;

    /// Configure for a map size.
    ///
    /// The splat resolution is not a free choice: every map the game ships has
    /// stratums exactly heightmapResolution square - 257, 513, 1025, 2049 - so
    /// the splat is vertex-aligned to the heightmap grid rather than being an
    /// independent power-of-two texture. We were writing 512/1024/2048 sampled
    /// at texel centres, which misregisters the whole splat by half a texel and
    /// stretches it by res/(res-1) on top. That is why rock did not sit on the
    /// rock. splatRes is accepted for call compatibility and ignored.
    public static void Configure(float size, int splatRes)
    {
        int n = (int)size;
        if (n < 64 || (n & (n - 1)) != 0)
            throw new ArgumentException(
                "Map size must be a power of two (256, 512, 1024, 2048); got " + n +
                ", which needs heightmapResolution " + (n + 1) + " and Unity would round it.");
        MapSize = size;
        HRes = n + 1;
        SRes = HRes;
    }

    // River: straight centreline runs corner to corner, TL(0,256) -> BR(256,0),
    // i.e. the line x + z = 256, with a sine meander laid on top.
    const float RiverCoreHalf = 5.5f;       // half-width of the deep channel
    const float ShelfWidth    = 7f;         // shallow margin, gives a beach
    const float ShelfTop      = 17f;        // height at the outer edge of it
    // The bank has to stay under the 30-degree nav limit or the river is ringed
    // by impassable ground and the bridges connect to nothing. Linear, not
    // eased: 4 m over 16 m is 14 degrees the whole way up.
    const float RiverBank     = 16f;
    public static float CurveAmp = 11f;     // raised a lot in organic-river mode
    static float RiverLen { get { return MapSize * 1.41421356f; } }

    // Bridges, as fractions along the river. Chosen so each sits just outside
    // one base. Symmetric about 0.5 so the map stays 180-degree rotational.
    static readonly float[] BridgeT   = { 0.26f, 0.74f };
    const float BridgeHalfLen = 6f;         // deck half-length along the river
    const float BridgeTaper   = 9f;         // matches the natural bank angle
    const float DeckSpan      = 19f;        // deck reach either side of centreline
    const float DeckSpanFade  = 8f;         // (river corridor is ~20.5 m each side)

    // Bases.
    public static float[] BaseX = { 100f, 156f };
    public static float[] BaseZ = { 218f,  38f };
    static float PadRadius { get { return MapSize * 0.094f; } }   // 24 m at 256
    static float PadBlend  { get { return MapSize * 0.055f; } }   // 14 m at 256

    // Cliff-edged plateaus. A short edge blend makes a cliff; the blend is
    // widened across a single arc to cut a walkable ramp, so each plateau has
    // exactly one way up. Angles are degrees, measured with atan2(z-cz, x-cx).
    struct Plateau
    {
        public float X, Z, Radius, Height, CliffEdge, RampAngle, RampArc, RampEdge;
    }

    // One highland in each off-diagonal corner (one per player), plus a mid-field
    // mesa on each bank. Listed for the top-left player; the 180-degree mirror is
    // added automatically.
    // navigationLayers.lua sets maxSlope = 30 degrees for Land, Amphibious and
    // Hover, and NavmapUtils.IsSteepTerrain blocks a cell when ANY neighbour in
    // its 3x3 exceeds that. Ramps are sized for RampSlopeTarget with margin.
    public const float MaxNavSlopeDeg = 30f;
    const float RampSlopeTarget = 17f;      // tan(17) ~ 0.306

    static readonly Plateau[] PlateausSideA =
    {
        // corner highland: the expansion high ground, ramp facing map centre.
        // 15 m over a 50 m linear ramp = 16.7 degrees.
        new Plateau { X = 226f, Z = 226f, Radius = 64f, Height = 15f,
                      CliffEdge = 8f, RampAngle = 225f, RampArc = 46f, RampEdge = 50f },
        // mid-field perch overlooking the river, ramp facing this player's base.
        // 9 m over a 32 m linear ramp = 15.7 degrees.
        // A narrow ramp on a small plateau gets pinched shut by the navmap's
        // 3x3 dilation even when its gradient is fine, so this one opens across
        // a broad 120-degree arc. 9 m over 30 m = 16.7 degrees.
        new Plateau { X = 196f, Z = 172f, Radius = 30f, Height = 9f,
                      CliffEdge = 7f, RampAngle = 154f, RampArc = 120f, RampEdge = 30f },
    };

    public static float[,] Height;          // metres, [row, col]
    static float[,] Slope;                  // degrees

    // ---- helpers -------------------------------------------------------
    static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }
    static float Smooth(float t)  { t = Clamp01(t); return t * t * (3f - 2f * t); }
    static float Lerp(float a, float b, float t) { return a + (b - a) * t; }

    static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            int h = x * 374761393 + y * 668265263 + seed * 1274126177;
            h = (h ^ (h >> 13)) * 1274126177;
            return ((h ^ (h >> 16)) & 0x7fffffff) / (float)0x7fffffff;
        }
    }

    static float ValueNoise(float x, float y, int seed)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
        float xf = x - xi, yf = y - yi;
        float u = Smooth(xf), v = Smooth(yf);
        float a = Hash(xi, yi, seed),     b = Hash(xi + 1, yi, seed);
        float c = Hash(xi, yi + 1, seed), d = Hash(xi + 1, yi + 1, seed);
        return Lerp(Lerp(a, b, u), Lerp(c, d, u), v);
    }

    static float Fbm(float x, float y, int seed, int octaves, float scale)
    {
        float sum = 0f, amp = 1f, norm = 0f, f = 1f / scale;
        for (int o = 0; o < octaves; o++)
        {
            sum += ValueNoise(x * f, y * f, seed + o * 7919) * amp;
            norm += amp; amp *= 0.5f; f *= 2f;
        }
        return sum / norm;
    }

    // ---- river geometry ------------------------------------------------
    static float Tparam(float x, float z)     { return (x - z + MapSize) / (2f * MapSize); }
    static float StraightDist(float x, float z) { return (x + z - MapSize) * 0.70710678f; }
    // 1-D fbm along the river, in roughly [-1, 1].
    // Base frequency matters more than it looks: the offset is antisymmetrised
    // as 0.5*(M(t) - M(1-t)), so if M varies slowly the two samples are nearly
    // equal and the whole meander cancels to nothing. It needs several lattice
    // cells across the river's length to produce real bends.
    static float Meander(float t, int seed)
    {
        float v = 0f, amp = 1f, norm = 0f, f = 8.5f;
        for (int o = 0; o < 3; o++)
        {
            v += (ValueNoise(t * f, 17.3f + o * 5.7f, seed + o * 131) - 0.5f) * 2f * amp;
            norm += amp; amp *= 0.5f; f *= 2.3f;
        }
        return v / norm;
    }

    // How much the river is allowed to wander here. Pinned near the crossings so
    // the bridges and the ground in front of the bases stay predictable, free to
    // roam in between. Symmetric because the bridge set is.
    static float MeanderEnvelope(float t)
    {
        float e = 1f;
        foreach (float tb in BridgeT)
            e = Math.Min(e, Smooth(Clamp01(Math.Abs(t - tb) / 0.075f)));
        // Anchor the ends too, so the channel always reaches both corners and
        // can't wander off the top or bottom edge where the diagonal runs close
        // to them.
        e = Math.Min(e, Smooth(Clamp01(t / 0.12f)));
        e = Math.Min(e, Smooth(Clamp01((1f - t) / 0.12f)));
        return e;
    }

    static float CurveOffset(float t)
    {
        if (!OrganicRiver) return -CurveAmp * (float)Math.Sin(2.0 * Math.PI * t);
        // Antisymmetric about t = 0.5: o(1-t) == -o(t), which is exactly what
        // the 180-degree rotation needs.
        float o = 0.5f * (Meander(t, 9001) - Meander(1f - t, 9001));
        // A 3-octave fbm difference only spans about +/-0.25 in practice, so
        // without a gain CurveAmp is misleading - it would never get near its
        // nominal value. Gain then clamp keeps the peaks honest.
        o = Clamp01((o * 3.4f + 1f) * 0.5f) * 2f - 1f;
        return o * CurveAmp * MeanderEnvelope(t);
    }

    /// Channel width multiplier, symmetric about t = 0.5 so both halves match.
    static float WidthScale(float x, float z)
    {
        if (!OrganicRiver) return 1f;
        float t = Tparam(x, z);
        float m = 0.5f * (Meander(t, 4177) + Meander(1f - t, 4177));
        return 1f + 0.42f * m;
    }

    // Signed perpendicular distance from the meandering centreline.
    public static float RiverDist(float x, float z)
    {
        return StraightDist(x, z) - CurveOffset(Tparam(x, z));
    }

    /// River-relative placement: t runs 0..1 from the TL corner to the BR
    /// corner, perp is signed metres from the actual (meandering) centreline,
    /// positive toward the top-left player's bank. Everything positioned this
    /// way keeps its clearance from the water no matter how the river wanders.
    public static void RiverToWorld(float t, float perp, out float x, out float z)
    {
        float n = (CurveOffset(t) + perp) * 0.70710678f;
        x = MapSize * t + n;
        z = MapSize - MapSize * t + n;
    }

    /// Index of the crossing closest to a point. There are always two bridges
    /// however many bases the player count asks for.
    public static int NearestBridge(float x, float z)
    {
        int best = 0;
        float bestD = float.MaxValue;
        for (int i = 0; i < BridgeX.Length; i++)
        {
            float dx = x - BridgeX[i], dz = z - BridgeZ[i];
            float d = dx * dx + dz * dz;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// Bridge deck centres, on the real centreline.
    public static void ComputeBridgePositions()
    {
        BridgesPlaced = true;
        BridgeX = new float[2];
        BridgeZ = new float[2];
        for (int i = 0; i < 2; i++)
        {
            RiverToWorld(BridgeT[i], 0f, out float bx, out float bz);
            BridgeX[i] = bx; BridgeZ[i] = bz;
        }
    }

    /// Several bases per bank, spread along the channel. The two-player case is
    /// just perSector = 1.
    public static void PlaceBasesAlongRiver(int perSector, float perpOffset)
    {
        int total = perSector * 2;
        var bx = new float[total];
        var bz = new float[total];
        for (int i = 0; i < perSector; i++)
        {
            float t = perSector == 1 ? 0.27f : 0.17f + 0.32f * i / (perSector - 1);
            RiverToWorld(t,      perpOffset, out bx[i], out bz[i]);
            RiverToWorld(1f - t, -perpOffset, out bx[i + perSector], out bz[i + perSector]);
        }
        BaseX = bx; BaseZ = bz;
    }

    /// Bases set back from the channel on opposite banks.
    public static void PlaceBases(float tA, float perpOffset)
    {
        var bx = new float[2];
        var bz = new float[2];
        RiverToWorld(tA,      perpOffset, out bx[0], out bz[0]);
        RiverToWorld(1f - tA, -perpOffset, out bx[1], out bz[1]);
        BaseX = bx; BaseZ = bz;
    }

    // Position along the river relative to the nearest bridge: 1 on the deck,
    // 0 once you are past the taper.
    static float BridgeAlong(float x, float z)
    {
        float t = Tparam(x, z), best = 0f;
        foreach (float tb in BridgeT)
        {
            float along = Math.Abs(t - tb) * RiverLen;
            float f = 1f - Clamp01((along - BridgeHalfLen) / BridgeTaper);
            if (f > best) best = f;
        }
        return Smooth(best);
    }

    // How far the deck reaches out from the centreline, so a causeway stays a
    // causeway instead of a band running the full width of the map.
    static float DeckCross(float x, float z)
    {
        float rd = Math.Abs(RiverDist(x, z));
        return Smooth(1f - Clamp01((rd - DeckSpan) / DeckSpanFade));
    }

    // 0 off a bridge, 1 on the deck.
    public static float BridgeFactor(float x, float z)
    {
        if (!UseRiver || !BridgesPlaced) return 0f;
        return BridgeAlong(x, z) * DeckCross(x, z);
    }

    // ---- height field --------------------------------------------------
    static float AngleDiff(float a, float b)
    {
        float d = Math.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }

    static float PlateauHeight(float x, float z, Plateau p)
    {
        float dx = x - p.X, dz = z - p.Z;
        float d = (float)Math.Sqrt(dx * dx + dz * dz);
        if (d > p.Radius + p.RampEdge) return 0f;

        float ang = (float)(Math.Atan2(dz, dx) * 180.0 / Math.PI);
        if (ang < 0f) ang += 360f;

        // Inside the ramp arc the edge stretches out, turning the cliff into a
        // slope. The inner 55% of the arc is fully ramped, so there is a broad
        // constant-gradient core rather than a single passable line that the
        // navmap's 3x3 dilation then pinches shut.
        float da = AngleDiff(ang, p.RampAngle);
        float inner = p.RampArc * 0.55f;
        float rampBlend = Smooth(1f - Clamp01((da - inner) / Math.Max(1f, p.RampArc - inner)));
        float edge = Lerp(p.CliffEdge, p.RampEdge, rampBlend);

        float t = Clamp01((p.Radius - d) / edge);
        // A smoothstep peaks at 1.5*H/d in the middle, so a "30 degree average"
        // ramp is really 45 in its steepest metre. Ramps go linear instead, and
        // only the cliff faces keep the eased profile.
        return p.Height * Lerp(Smooth(t), t, rampBlend);
    }

    // ---- organic terrain -------------------------------------------------
    // Branching mesas instead of analytic discs. A domain-warped fbm field is
    // thresholded: above the threshold is mesa, and the profile across the
    // threshold band becomes the cliff. Because the band is only a few metres
    // wide in world terms, the result is a flat top with a hard edge - eroded
    // plateau, not a dome. Re-thresholding the SAME field higher gives a second
    // tier nested inside the first, which is what makes it read as layered rock.
    public static float MesaTier1Height = 13f;
    public static float MesaTier2Height = 9f;
    public static float OutcropHeight   = 5f;
    public static float MesaThreshold   = 0.60f;
    public static float MesaScale       = 0.21f;   // fraction of map size

    /// Ridged puts the high values along the field's mid-level contours, which
    /// form long connected ribbons - visually closer to eroded spines, but on a
    /// playable-sized map those ribbons are walls: it took Riverbreak to 29%
    /// cliff, 16 separate open areas and a disconnected map. Off by default.
    /// Roundness is broken with ErosionStrength instead, which bites chunks out
    /// of compact massifs without adding new barriers.
    public static bool RidgedMesas = false;
    public static float ErosionStrength = 0f;

    static float OrganicRaw(float x, float z, int seed, float scale, float warp)
    {
        float wx = x + (Fbm(x, z, seed + 11, 3, scale * 1.3f) - 0.5f) * warp;
        float wz = z + (Fbm(x, z, seed + 23, 3, scale * 1.3f) - 0.5f) * warp;
        float f = Fbm(wx, wz, seed, 5, scale);
        return RidgedMesas ? 1f - Math.Abs(f * 2f - 1f) : f;
    }

    // Union of the field with its own 180-degree rotation. Taking the max keeps
    // the mesas crisp; averaging would smear both copies into mush.
    static float OrganicMask(float x, float z, int seed, float scale, float warp)
    {
        return Math.Max(OrganicRaw(x, z, seed, scale, warp),
                        OrganicRaw(MapSize - x, MapSize - z, seed, scale, warp));
    }

    /// The mesa stack on its own, without the ground under it.
    static float MesaStack(float x, float z)
    {
        // Few and large. A lower threshold or a finer scale turns the map into
        // a maze of small buttes with no room to manoeuvre or build.
        float m = OrganicMask(x, z, 3301, MapSize * MesaScale, MapSize * 0.15f);
        // Erosion: subtract a finer mask so the massif outlines get bitten into
        // bays and headlands instead of staying convex.
        if (ErosionStrength > 0f)
            m -= ErosionStrength * Smooth(Clamp01(
                     (OrganicMask(x, z, 6421, MapSize * MesaScale * 0.42f, MapSize * 0.06f) - 0.55f) / 0.12f));
        float h = MesaTier1Height * Smooth(Clamp01((m - MesaThreshold) / 0.045f));
        h += MesaTier2Height * Smooth(Clamp01((m - (MesaThreshold + 0.09f)) / 0.035f));

        float o = OrganicMask(x, z, 7717, MapSize * 0.065f, MapSize * 0.05f);
        h += OutcropHeight * Smooth(Clamp01((o - 0.78f) / 0.04f));
        return h;
    }

    /// 0 where mesas must not grow: in the channel, over the base pads, and
    /// along the base-to-bridge approaches. Without this the hills wall the
    /// bases in and bury the crossings.
    static float HillKeepOut(float x, float z)
    {
        float keep = 1f;

        if (UseRiver)
        {
            float corridor = (RiverCoreHalf + ShelfWidth) * WidthScale(x, z) + RiverBank;
            float rd = Math.Abs(RiverDist(x, z));
            keep *= Smooth(Clamp01((rd - (corridor + 6f)) / 26f));

            for (int i = 0; i < BaseX.Length; i++)
            {
                // There are always two bridges however many bases, so pair each
                // base with its nearest crossing instead of indexing in step.
                float ax = BaseX[i], az = BaseZ[i];
                int nb = NearestBridge(ax, az);
                float bx = BridgeX[nb], bz = BridgeZ[nb];
                float vx = bx - ax, vz = bz - az;
                float len2 = vx * vx + vz * vz;
                if (len2 < 1f) continue;
                float t = Clamp01(((x - ax) * vx + (z - az) * vz) / len2);
                float px = ax + vx * t, pz = az + vz * t;
                float d = (float)Math.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz));
                keep *= Smooth(Clamp01((d - 16f) / 20f));
            }
        }

        for (int i = 0; i < BaseX.Length; i++)
        {
            float dx = x - BaseX[i], dz = z - BaseZ[i];
            float d = (float)Math.Sqrt(dx * dx + dz * dz);
            keep *= Smooth(Clamp01((d - (PadRadius + PadBlend + 12f)) / 24f));
        }
        return keep;
    }

    static float OrganicLand(float x, float z)
    {
        float h = LandBase;
        h += (Fbm(x, z, 1337, 4, MapSize * 0.19f) - 0.5f) * 9f;
        return h + MesaStack(x, z);
    }

    static float LandHeight(float x, float z)
    {
        if (Organic) return OrganicLand(x, z);

        // Plateaus take the max rather than summing: two that overlap should
        // read as one piece of high ground, not stack into a spike.
        float plat = 0f;
        foreach (var p in PlateausSideA)
        {
            plat = Math.Max(plat, PlateauHeight(x, z, p));
            // 180-degree mirror, including the ramp direction
            var m = p;
            m.X = MapSize - p.X; m.Z = MapSize - p.Z;
            m.RampAngle = (p.RampAngle + 180f) % 360f;
            plat = Math.Max(plat, PlateauHeight(x, z, m));
        }

        float h = LandBase + plat;
        // Kept gentle on purpose: rolling ground has to stay well under the
        // 30-degree nav limit, or units path around invisible bumps.
        // Deliberately mild. Hundreds of units have to move and build across
        // this; every metre of relief that isn't doing a job is just terrain
        // for pathfinding to fight. The shape comes from the mesas.
        h += (Fbm(x, z, 1337, 4, MapSize * 0.42f) - 0.5f) * 6f;   // long swells
        h += (Fbm(x, z, 4523, 2, MapSize * 0.15f) - 0.5f) * 1.6f; // faint texture

        // Central depression. Deep enough and it floods into a lake, which is
        // how the Basin style gets its water without a carved channel.
        if (BowlDepth != 0f)
        {
            float cx = MapSize * 0.5f, cz = MapSize * 0.5f;
            float d = (float)Math.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));
            // Wobble the radius, or the shoreline comes out as a perfect circle
            // with concentric contour rings and reads as a crater, not a lake.
            d += (Fbm(x, z, 2711, 3, MapSize * 0.17f) - 0.5f) * MapSize * 0.13f;
            float t = Clamp01(d / (MapSize * BowlRadiusFrac));
            // Flat-bottomed rather than conical: a cone only floods at the very
            // centre, which is how the first attempt produced puddles.
            h -= BowlDepth * Smooth(1f - Clamp01((t - 0.25f) / 0.75f));
        }

        // PathedMesas supplies its own keep-out during mask construction, so it
        // is added straight on; the noise-based hills still need masking off.
        if (PathedMesas)       h += MesaAt(x, z) * HillStrength;
        else if (OrganicHills) h += MesaStack(x, z) * HillStrength * HillKeepOut(x, z);
        return h;
    }

    static float HeightAt(float x, float z, float padA, float padB)
    {
        float h = LandHeight(x, z);

        // Flatten a building pad under each base, before the river is cut so
        // the water always wins where they overlap.
        for (int i = 0; i < BaseX.Length; i++)
        {
            float dx = x - BaseX[i], dz = z - BaseZ[i];
            float d = (float)Math.Sqrt(dx * dx + dz * dz);
            float k = Smooth(1f - Clamp01((d - PadRadius) / PadBlend));
            h = Lerp(h, i == 0 ? padA : padB, k);
        }

        if (!UseRiver) return h;

        // Cut the river: deep channel, then a shallow shelf that carries the
        // waterline out into a beach, then the bank proper.
        float rd = Math.Abs(RiverDist(x, z));
        float ws = WidthScale(x, z);
        float core = RiverCoreHalf * ws, shelfW = ShelfWidth * ws;
        float s1 = Smooth(Clamp01((rd - core) / shelfW));
        float shelf = Lerp(RiverBed, ShelfTop, s1);
        float s2 = Clamp01((rd - core - shelfW) / RiverBank);   // linear
        float hr = Lerp(shelf, h, s2);

        // Lay the causeways over it.
        float bf = BridgeFactor(x, z);
        return Lerp(hr, Math.Max(hr, DeckHeight), bf);
    }

    public static void BuildHeight()
    {
        float step = MapSize / (HRes - 1);
        float padA = LandHeight(BaseX[0], BaseZ[0]);
        float padB = LandHeight(BaseX[1], BaseZ[1]);
        float pad = 0.5f * (padA + padB);        // keep the two bases identical

        Height = new float[HRes, HRes];
        for (int r = 0; r < HRes; r++)
            for (int c = 0; c < HRes; c++)
                Height[r, c] = HeightAt(c * step, MapSize - r * step, pad, pad);

        SymmetriseFieldN(Height, HRes);
        RebuildSlope();
    }

    public static void RebuildSlope()
    {
        float step = MapSize / (HRes - 1);
        // slope in degrees, from central differences
        Slope = new float[HRes, HRes];
        for (int r = 0; r < HRes; r++)
            for (int c = 0; c < HRes; c++)
            {
                int r0 = Math.Max(r - 1, 0), r1 = Math.Min(r + 1, HRes - 1);
                int c0 = Math.Max(c - 1, 0), c1 = Math.Min(c + 1, HRes - 1);
                float dhx = (Height[r, c1] - Height[r, c0]) / ((c1 - c0) * step);
                float dhz = (Height[r1, c] - Height[r0, c]) / ((r1 - r0) * step);
                Slope[r, c] = (float)(Math.Atan(Math.Sqrt(dhx * dhx + dhz * dhz)) * 180.0 / Math.PI);
            }
    }

    static void Symmetrise(float[,] a, int n)
    {
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                int r2 = n - 1 - r, c2 = n - 1 - c;
                if (r2 < r || (r2 == r && c2 < c)) continue;
                float m = 0.5f * (a[r, c] + a[r2, c2]);
                a[r, c] = m; a[r2, c2] = m;
            }
    }

    // Bilinear sample of a HRes grid at world coordinates.
    static float Sample(float[,] g, float x, float z)
    {
        float step = MapSize / (HRes - 1);
        float fc = x / step, fr = (MapSize - z) / step;
        int c0 = (int)Math.Floor(fc), r0 = (int)Math.Floor(fr);
        c0 = Math.Max(0, Math.Min(HRes - 2, c0));
        r0 = Math.Max(0, Math.Min(HRes - 2, r0));
        float tc = Clamp01(fc - c0), tr = Clamp01(fr - r0);
        return Lerp(Lerp(g[r0, c0], g[r0, c0 + 1], tc),
                    Lerp(g[r0 + 1, c0], g[r0 + 1, c0 + 1], tc), tr);
    }

    public static float HeightAtWorld(float x, float z) { return Sample(Height, x, z); }
    public static float SlopeAtWorld (float x, float z) { return Sample(Slope,  x, z); }

    // ---- stratum weights -----------------------------------------------
    // Layer 0 is the base and shows through wherever the others are low.
    //   1 grass07 wash   2 heather03   3 grass02   4 grass03
    //   5 mud02 (bed)    6 sand02 (shore)  7 rock_cliff03 (slope)  8 gravel01 (roads)
    public static byte[][,] Layers;   // Layers[1..8]

    // Bridge centres on the meandering centreline, for the approach tracks.
    /// True once ComputeBridgePositions has run for the current map.
    ///
    /// BridgeX/BridgeZ carry hardcoded defaults sized for a 256 m map. A
    /// converted Supreme Commander map never calls ComputeBridgePositions, so
    /// those stale coordinates landed inside the map and BridgeFactor happily
    /// painted a rectangular bridge deck in the middle of a desert, with
    /// RoadMask drawing approach tracks to it. Nothing else flagged it: the
    /// heightfield was untouched, so every pathing and slope check passed.
    public static bool BridgesPlaced = false;

    public static float[] BridgeX = { 56.7f, 199.3f };
    public static float[] BridgeZ = { 179.5f, 76.5f };

    static float RoadMask(float x, float z)
    {
        if (!UseRiver || !BridgesPlaced) return 0f;
        float road = BridgeFactor(x, z);
        // A worn approach track from each base out to its own crossing.
        for (int i = 0; i < BaseX.Length; i++)
        {
            float ax = BaseX[i], az = BaseZ[i];
            int nb = NearestBridge(ax, az);
            float bx = BridgeX[nb], bz = BridgeZ[nb];
            float vx = bx - ax, vz = bz - az;
            float len2 = vx * vx + vz * vz;
            float t = Clamp01(((x - ax) * vx + (z - az) * vz) / len2);
            float px = ax + vx * t, pz = az + vz * t;
            float d = (float)Math.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz));
            float w = Smooth(1f - Clamp01((d - 2.5f) / 5f)) * 0.6f;
            if (w > road) road = w;
        }
        return road;
    }

    // Height along the crossing at a bridge, sampled across the channel, and
    // the deepest point of the river between bridges. Used to prove the
    // causeways connect and that nothing else does.
    public static float[] CrossingProfile(int bridge, int samples)
    {
        var outp = new float[samples];
        float cx = BridgeX[bridge], cz = BridgeZ[bridge];
        for (int i = 0; i < samples; i++)
        {
            float o = (i / (float)(samples - 1) - 0.5f) * 80f;   // +/- 40 m across
            outp[i] = HeightAtWorld(cx + o * 0.70710678f, cz + o * 0.70710678f);
        }
        return outp;
    }

    // ---- pathability ----------------------------------------------------
    // Reimplements the Land navigation layer: a cell is walkable when it is
    // above water and no cell in its 3x3 exceeds maxSlope, matching
    // NavmapUtils.IsSteepTerrain(range: 1) against navigationLayers.lua's
    // maxSlope = 30. Then flood-fills from a seed so "can units actually get
    // there" is a computed answer rather than a guess from a screenshot.
    public static bool[,] Walkable;

    public static void BuildWalkable()
    {
        var steep = new bool[HRes, HRes];
        for (int r = 0; r < HRes; r++)
            for (int c = 0; c < HRes; c++)
                steep[r, c] = Slope[r, c] > MaxNavSlopeDeg;

        Walkable = new bool[HRes, HRes];
        for (int r = 0; r < HRes; r++)
            for (int c = 0; c < HRes; c++)
            {
                if (Height[r, c] <= WaterLevel) continue;
                bool ok = true;
                for (int dr = -1; dr <= 1 && ok; dr++)
                    for (int dc = -1; dc <= 1 && ok; dc++)
                    {
                        int rr = Math.Min(HRes - 1, Math.Max(0, r + dr));
                        int cc = Math.Min(HRes - 1, Math.Max(0, c + dc));
                        if (steep[rr, cc]) ok = false;
                    }
                Walkable[r, c] = ok;
            }
    }

    static void WorldToCell(float x, float z, out int r, out int c)
    {
        float step = MapSize / (HRes - 1);
        c = Math.Min(HRes - 1, Math.Max(0, (int)Math.Round(x / step)));
        r = Math.Min(HRes - 1, Math.Max(0, (int)Math.Round((MapSize - z) / step)));
    }

    /// Flood-fill of the walkable region containing (seedX, seedZ). If the seed
    /// itself is blocked, the nearest walkable cell within 12 m is used.
    public static bool[,] Reachable(float seedX, float seedZ)
    {
        WorldToCell(seedX, seedZ, out int sr, out int sc);
        if (!Walkable[sr, sc])
        {
            bool found = false;
            for (int rad = 1; rad <= 12 && !found; rad++)
                for (int dr = -rad; dr <= rad && !found; dr++)
                    for (int dc = -rad; dc <= rad && !found; dc++)
                    {
                        int rr = sr + dr, cc = sc + dc;
                        if (rr < 0 || rr >= HRes || cc < 0 || cc >= HRes) continue;
                        if (Walkable[rr, cc]) { sr = rr; sc = cc; found = true; }
                    }
            if (!found) return new bool[HRes, HRes];
        }

        var seen = new bool[HRes, HRes];
        var stack = new System.Collections.Generic.Stack<int>();
        seen[sr, sc] = true;
        stack.Push(sr * HRes + sc);
        int[] dR = { 1, -1, 0, 0 }, dC = { 0, 0, 1, -1 };
        while (stack.Count > 0)
        {
            int v = stack.Pop();
            int r = v / HRes, c = v % HRes;
            for (int i = 0; i < 4; i++)
            {
                int rr = r + dR[i], cc = c + dC[i];
                if (rr < 0 || rr >= HRes || cc < 0 || cc >= HRes) continue;
                if (seen[rr, cc] || !Walkable[rr, cc]) continue;
                seen[rr, cc] = true;
                stack.Push(rr * HRes + cc);
            }
        }
        return seen;
    }

    public static bool IsReachable(bool[,] set, float x, float z)
    {
        WorldToCell(x, z, out int r, out int c);
        for (int dr = -2; dr <= 2; dr++)
            for (int dc = -2; dc <= 2; dc++)
            {
                int rr = r + dr, cc = c + dc;
                if (rr < 0 || rr >= HRes || cc < 0 || cc >= HRes) continue;
                if (set[rr, cc]) return true;
            }
        return false;
    }

    public static int CountTrue(bool[,] a)
    {
        int n = 0;
        foreach (bool b in a) if (b) n++;
        return n;
    }

    public static int WalkableCount() { return CountTrue(Walkable); }

    // ---- automatic ramp carving ------------------------------------------
    // Organic mesas have no ramps by construction, so this finds every walkable
    // region that is cut off from the seed and cuts a constant-gradient corridor
    // down to the nearest reachable ground. Repeats until nothing is stranded,
    // which also handles mesas that only become reachable via another mesa.
    public static int CarveRamps(float seedX, float seedZ, int maxPasses,
                                 float halfWidth, float blendWidth, float minAreaCells)
    {
        float step = MapSize / (HRes - 1);
        float maxGrad = (float)Math.Tan(RampSlopeTarget * Math.PI / 180.0);
        int carved = 0;

        for (int pass = 0; pass < maxPasses; pass++)
        {
            BuildWalkable();
            var reach = Reachable(seedX, seedZ);

            // Largest stranded component this pass.
            var seen = new bool[HRes, HRes];
            int bestSize = 0;
            System.Collections.Generic.List<int> best = null;
            for (int r = 0; r < HRes; r++)
                for (int c = 0; c < HRes; c++)
                {
                    if (seen[r, c] || !Walkable[r, c] || reach[r, c]) continue;
                    var comp = new System.Collections.Generic.List<int>();
                    var st = new System.Collections.Generic.Stack<int>();
                    seen[r, c] = true; st.Push(r * HRes + c);
                    int[] dR = { 1, -1, 0, 0 }, dC = { 0, 0, 1, -1 };
                    while (st.Count > 0)
                    {
                        int v = st.Pop(); comp.Add(v);
                        int cr = v / HRes, cc = v % HRes;
                        for (int i = 0; i < 4; i++)
                        {
                            int rr = cr + dR[i], ccc = cc + dC[i];
                            if (rr < 0 || rr >= HRes || ccc < 0 || ccc >= HRes) continue;
                            if (seen[rr, ccc] || !Walkable[rr, ccc] || reach[rr, ccc]) continue;
                            seen[rr, ccc] = true; st.Push(rr * HRes + ccc);
                        }
                    }
                    if (comp.Count > bestSize) { bestSize = comp.Count; best = comp; }
                }

            if (best == null || bestSize < minAreaCells) break;

            // Closest pair between the stranded component and reachable ground.
            // Both sides are sampled: we need a good landing point, not the
            // provably nearest one, and the exhaustive version is O(n^2) per
            // pass over a 513^2 grid.
            var targets = new System.Collections.Generic.List<int>();
            for (int rr = 0; rr < HRes; rr += 3)
                for (int ccc = 0; ccc < HRes; ccc += 3)
                    if (reach[rr, ccc]) targets.Add(rr * HRes + ccc);
            if (targets.Count == 0) break;

            int strideC = Math.Max(1, best.Count / 300);
            double bestD = double.MaxValue;
            int aR = 0, aC = 0, bR = 0, bC = 0;
            for (int i = 0; i < best.Count; i += strideC)
            {
                int cr = best[i] / HRes, cc = best[i] % HRes;
                foreach (int tv in targets)
                {
                    int rr = tv / HRes, ccc = tv % HRes;
                    double d = (rr - cr) * (double)(rr - cr) + (ccc - cc) * (double)(ccc - cc);
                    if (d < bestD) { bestD = d; aR = cr; aC = cc; bR = rr; bC = ccc; }
                }
            }
            if (bestD == double.MaxValue) break;

            // World-space endpoints, top first.
            float ax = aC * step, az = MapSize - aR * step, ah = Height[aR, aC];
            float bx = bC * step, bz = MapSize - bR * step, bh = Height[bR, bC];
            if (bh > ah) { float t;
                           t = ax; ax = bx; bx = t;
                           t = az; az = bz; bz = t;
                           t = ah; ah = bh; bh = t; }

            float dx = bx - ax, dz = bz - az;
            float len = (float)Math.Sqrt(dx * dx + dz * dz);
            if (len < 1f) break;
            dx /= len; dz /= len;

            // Extend the downhill end until the gradient is within target.
            float need = (ah - bh) / maxGrad;
            if (need > len) { bx = ax + dx * need; bz = az + dz * need; len = need; }

            CarveCorridor(ax, az, ah, bx, bz, bh, halfWidth, blendWidth);
            SymmetriseFieldN(Height, HRes);
            RebuildSlope();
            carved++;
        }

        BuildWalkable();
        return carved;
    }

    static void CarveCorridor(float ax, float az, float ah,
                              float bx, float bz, float bh,
                              float halfWidth, float blendWidth)
    {
        float step = MapSize / (HRes - 1);
        float vx = bx - ax, vz = bz - az;
        float len2 = vx * vx + vz * vz;
        if (len2 < 1e-3f) return;

        float minX = Math.Min(ax, bx) - halfWidth - blendWidth;
        float maxX = Math.Max(ax, bx) + halfWidth + blendWidth;
        float minZ = Math.Min(az, bz) - halfWidth - blendWidth;
        float maxZ = Math.Max(az, bz) + halfWidth + blendWidth;

        for (int r = 0; r < HRes; r++)
        {
            float z = MapSize - r * step;
            if (z < minZ || z > maxZ) continue;
            for (int c = 0; c < HRes; c++)
            {
                float x = c * step;
                if (x < minX || x > maxX) continue;

                float t = Clamp01(((x - ax) * vx + (z - az) * vz) / len2);
                float px = ax + vx * t, pz = az + vz * t;
                float d = (float)Math.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz));
                float w = Smooth(1f - Clamp01((d - halfWidth) / blendWidth));
                if (w <= 0f) continue;

                // Never touch the channel or its shelf: a corridor that clipped
                // the river would fill it in and hand both players a free
                // crossing the bridges were supposed to control.
                if (UseRiver)
                {
                    float ws = WidthScale(x, z);
                    if (Math.Abs(RiverDist(x, z)) < (RiverCoreHalf + ShelfWidth) * ws + 2f) continue;
                }

                float ramp = Lerp(ah, bh, t);
                Height[r, c] = Lerp(Height[r, c], ramp, w);
            }
        }
    }

    // Height range and slope distribution over the dry land, so "is this
    // actually terrain, and is it still buildable" is a number not an opinion.
    public static float[] TerrainStats()
    {
        float lo = 1e9f, hi = -1e9f;
        int land = 0, flat = 0, gentle = 0, steep = 0, cliff = 0;
        for (int r = 0; r < HRes; r++)
            for (int c = 0; c < HRes; c++)
            {
                float h = Height[r, c];
                if (h < lo) lo = h;
                if (h > hi) hi = h;
                if (h <= WaterLevel) continue;
                land++;
                float s = Slope[r, c];
                if (s < 6f) flat++;
                else if (s < 15f) gentle++;
                else if (s < 34f) steep++;
                else cliff++;
            }
        return new[] { lo, hi, land, flat, gentle, steep, cliff };
    }

    /// Open ground: how much of the map is one contiguous, buildable, roughly
    /// level area. This is the number that decides whether a few hundred units
    /// can actually maneouvre - a map can be 100% reachable and still be a
    /// warren of corridors, which the slope histogram alone will not show.
    /// Returns { largestCells, landCells, regionsOver400 }.
    public static float[] OpenGroundStats(float flatDeg)
    {
        var flat = new bool[HRes, HRes];
        int land = 0;
        for (int r = 0; r < HRes; r++)
            for (int c = 0; c < HRes; c++)
            {
                if (Height[r, c] <= WaterLevel) continue;
                land++;
                if (Slope[r, c] < flatDeg && Walkable[r, c]) flat[r, c] = true;
            }

        var seen = new bool[HRes, HRes];
        int largest = 0, regions = 0;
        int[] dR = { 1, -1, 0, 0 }, dC = { 0, 0, 1, -1 };
        for (int r = 0; r < HRes; r++)
            for (int c = 0; c < HRes; c++)
            {
                if (seen[r, c] || !flat[r, c]) continue;
                int size = 0;
                var st = new System.Collections.Generic.Stack<int>();
                seen[r, c] = true; st.Push(r * HRes + c);
                while (st.Count > 0)
                {
                    int v = st.Pop(); size++;
                    int cr = v / HRes, cc = v % HRes;
                    for (int i = 0; i < 4; i++)
                    {
                        int rr = cr + dR[i], ccc = cc + dC[i];
                        if (rr < 0 || rr >= HRes || ccc < 0 || ccc >= HRes) continue;
                        if (seen[rr, ccc] || !flat[rr, ccc]) continue;
                        seen[rr, ccc] = true; st.Push(rr * HRes + ccc);
                    }
                }
                if (size > largest) largest = size;
                if (size > 400) regions++;
            }
        return new float[] { largest, land, regions };
    }

    public static float RiverMaxHeightBetweenBridges()
    {
        float worst = -1e9f;
        for (int i = 0; i <= 2000; i++)
        {
            float t = i / 2000f;
            if (Math.Abs(t - BridgeT[0]) * RiverLen < 18f) continue;
            if (Math.Abs(t - BridgeT[1]) * RiverLen < 18f) continue;
            float bx = MapSize * t, bz = MapSize - MapSize * t;
            float off = CurveOffset(t) * 0.70710678f;
            float h = HeightAtWorld(bx + off, bz + off);
            if (h > worst) worst = h;
        }
        return worst;
    }

    public static void BuildLayers()
    {
        Layers = new byte[9][,];
        for (int i = 1; i <= 8; i++) Layers[i] = new byte[SRes, SRes];
        // Vertex-aligned, matching the heightmap grid.
        float px = MapSize / (SRes - 1);
        var f = new float[9][,];
        for (int i = 1; i <= 8; i++) f[i] = new float[SRes, SRes];

        for (int r = 0; r < SRes; r++)
        for (int c = 0; c < SRes; c++)
        {
            float x = c * px, z = MapSize - r * px;
            float h = Sample(Height, x, z), sl = Sample(Slope, x, z);

            // Damp margin carries a little past the waterline. Gated on there
            // being water, not on there being a river: a converted map has a
            // sea and no river, and used to get the dry-map treatment - no
            // shoreline at all.
            float mud = 0f, sand = 0f;
            if (WaterLevel > 0f)
            {
                mud = Clamp01(((WaterLevel + 0.8f) - h) / 3.5f) * 0.88f;
                float dShore = (h - (WaterLevel + 1.0f)) / 3.0f;
                sand = 0.95f * (float)Math.Exp(-dShore * dShore);
            }
            else
            {
                // Dry map: sand becomes wind-blown drift in the hollows instead
                // of a shoreline, so layer 6 still earns its slot.
                sand = 0.55f * Smooth(Clamp01((Fbm(x, z, 6151, 3, MapSize * 0.14f) - 0.58f) / 0.14f)) * (1f - Smooth(Clamp01((sl - 8f) / 10f)));
            }
            // Rock only where the ground really is steep. At a 15-degree
            // threshold most of the rolling field picked up a partial rock
            // wash, which reads as "cliff texture on flat ground".
            float rock  = Smooth(Clamp01((sl - 26f) / 12f));
            float grav  = RoadMask(x, z);

            // Layer 1 is basalt on the true cliff faces, over the cliff01 on
            // layer 7, so plateau edges read as rock rather than steep grass.
            float cliff = Smooth(Clamp01((sl - 36f) / 12f));

            float cover = Clamp01(mud + sand + rock + grav);
            // Slot 2 covers ground a player reads as flat, so it starts late
            // and stays gentle. The shipped maps run grass 5.9 -> heather 11.7
            // -> grass02 21.3 -> rock 29, and nothing rocky appears below the
            // high twenties. Bands overlap so the handover is not a contour
            // line drawn across the hillside.
            float band2 = Smooth(Clamp01((sl - 7f) / 7f)) * (1f - Smooth(Clamp01((sl - 18f) / 10f)));
            float band3 = Smooth(Clamp01((sl - 14f) / 10f)) * (1f - Smooth(Clamp01((sl - 34f) / 12f)));

            float n2 = Smooth(Clamp01((Fbm(x, z, 5147, 3, 46f) - 0.52f) / 0.14f));
            float n3 = Smooth(Clamp01((Fbm(x, z, 7321, 3, 34f) - 0.44f) / 0.18f));
            float n4 = Smooth(Clamp01((Fbm(x, z, 9109, 4, 21f) - 0.55f) / 0.16f));

            // Layers 2 and 3 are mostly slope with a little noise to break the
            // banding; layer 4 stays pure flat-ground variation.
            float g2 = Clamp01(0.80f * band2 + 0.30f * n2 * (1f - band3));
            float g3 = Clamp01(0.85f * band3 + 0.22f * n3);
            float g4 = 0.72f * n4 * (1f - band2) * (1f - band3);
            float keep = 1f - cover;

            f[1][r, c] = cliff * (1f - grav);
            f[2][r, c] = g2 * keep;
            f[3][r, c] = g3 * keep;
            f[4][r, c] = g4 * keep;
            f[5][r, c] = mud  * (1f - grav);
            f[6][r, c] = sand * (1f - grav);
            f[7][r, c] = rock * (1f - grav);
            f[8][r, c] = grav;
        }

        for (int i = 1; i <= 8; i++)
        {
            Symmetrise(f[i], SRes);
            for (int r = 0; r < SRes; r++)
                for (int c = 0; c < SRes; c++)
                    Layers[i][r, c] = (byte)Math.Round(Clamp01(f[i][r, c]) * 255f);
        }
    }

    // ---- prop scatter --------------------------------------------------
    // Places instances on the top-left player's bank only; the caller mirrors
    // them, so both sides get identical reclaim. Returns a flat array of
    // (x, y, z, yaw, scale) tuples.
    /// Whether Scatter confines itself to one half of the map for the caller
    /// to mirror. False scatters over the whole map, for callers that do not.
    public static bool ScatterHalfOnly = true;

    public static float[] Scatter(int seed, int target, bool rocks,
                                  float[] avoidX, float[] avoidZ, float avoidR)
    {
        var res = new System.Collections.Generic.List<float>();
        var rng = new Random(seed);
        int tries = target * 200;

        for (int i = 0; i < tries && res.Count / 5 < target; i++)
        {
            float x = (float)rng.NextDouble() * MapSize;
            float z = (float)rng.NextDouble() * MapSize;
            // Half the map only, because the symmetric callers mirror the
            // result themselves. A caller that does not mirror - the Supreme
            // Commander converter, where the imported terrain has whatever
            // symmetry it has - gets props on one side of the diagonal and a
            // bare triangle on the other.
            if (ScatterHalfOnly && x + z <= MapSize + 6f) continue;

            float h = HeightAtWorld(x, z);
            if (h < WaterLevel + 1.4f) continue;                 // keep out of the water
            float rd = UseRiver ? Math.Abs(RiverDist(x, z)) : 1e9f;
            if (UseRiver && rd < RiverCoreHalf + ShelfWidth + 2f) continue;
            if (RoadMask(x, z) > 0.12f) continue;                // keep the crossings clear

            bool blocked = false;
            for (int b = 0; b < BaseX.Length && !blocked; b++)
            {
                float dx = x - BaseX[b], dz = z - BaseZ[b];
                if (dx * dx + dz * dz < 34f * 34f) blocked = true;
            }
            for (int a = 0; a < avoidX.Length && !blocked; a++)
            {
                float dx = x - avoidX[a], dz = z - avoidZ[a];
                if (dx * dx + dz * dz < avoidR * avoidR) blocked = true;
            }
            if (blocked) continue;

            float sl = SlopeAtWorld(x, z);
            float p;
            if (rocks)
            {
                float slopeFit = Clamp01((sl - 9f) / 12f);
                float e = (rd - 24f) / 13f;
                float shoreFit = (float)Math.Exp(-e * e);
                p = Clamp01(0.40f * slopeFit + 0.55f * shoreFit);
            }
            else
            {
                if (sl > 26f) continue;
                p = Smooth(Clamp01((Fbm(x, z, 4211, 3, 38f) - 0.48f) / 0.22f));
            }
            if (rng.NextDouble() > p) continue;

            res.Add(x); res.Add(h); res.Add(z);
            res.Add((float)(rng.NextDouble() * Math.PI * 2.0));
            res.Add(rocks ? 0.75f + (float)rng.NextDouble() * 0.55f
                          : 0.85f + (float)rng.NextDouble() * 0.55f);
        }
        return res.ToArray();
    }

    // ---- writers -------------------------------------------------------
    public static void WriteHeightmap(string path)
    {
        using (var fs = File.Create(path))
        {
            var row = new byte[HRes * 2];
            for (int r = 0; r < HRes; r++)
            {
                for (int c = 0; c < HRes; c++)
                {
                    int v = (int)Math.Round(Clamp01(Height[r, c] / MaxHeight) * 65535f);
                    row[c * 2] = (byte)(v & 0xff);
                    row[c * 2 + 1] = (byte)(v >> 8);
                }
                fs.Write(row, 0, row.Length);
            }
        }
    }

    /// TGA header matching the ones the game ships.
    ///
    /// We wrote descriptor 0, which declares a bottom-left origin and - worse -
    /// zero alpha bits on a 32-bit image. Alpha carries stratum layers 4 and 8,
    /// so a decoder that takes the header at its word drops two of the eight
    /// layers. Every map the game ships uses 0x28: bit 5 set for a top-left
    /// origin, low nibble 8 for eight alpha bits. 71 of the 73 shipped stratum
    /// files agree on it.
    static void WriteTgaHeader(Stream fs, int w, int h)
    {
        var hdr = new byte[18];
        hdr[2] = 2;                     // uncompressed truecolour
        hdr[12] = (byte)(w & 0xff); hdr[13] = (byte)(w >> 8);
        hdr[14] = (byte)(h & 0xff); hdr[15] = (byte)(h >> 8);
        hdr[16] = 32;                   // bits per pixel
        hdr[17] = 0x28;                 // 8 alpha bits, top-left origin
        fs.Write(hdr, 0, 18);
    }

    // TGA pixels are BGRA. stratums_1_4 carries layers 1-4 as RGBA, so the file
    // byte order is [L3, L2, L1, L4]; stratums_5_8 is [L7, L6, L5, L8].
    public static void WriteStratums(string dir)
    {
        WriteStratumPair(Path.Combine(dir, "stratums_1_4.tga"), 1, 2, 3, 4);
        WriteStratumPair(Path.Combine(dir, "stratums_5_8.tga"), 5, 6, 7, 8);
    }

    /// One stratum pair.
    ///
    /// Rows are emitted bottom-up. Measured against the shipped maps, their
    /// file row k holds world z = k * step - correlating a shipped rock layer
    /// against slope scores 0.90 read that way and 0.58 read the other. Our
    /// Layers array is indexed row 0 = world z max, the same convention as the
    /// heightmap, so it has to be reversed on the way out.
    static void WriteStratumPair(string path, int lR, int lG, int lB, int lA)
    {
        using (var fs = File.Create(path))
        {
            WriteTgaHeader(fs, SRes, SRes);
            var row = new byte[SRes * 4];
            for (int r = SRes - 1; r >= 0; r--)
            {
                for (int c = 0; c < SRes; c++)
                {
                    int o = c * 4;
                    row[o]     = Layers[lB][r, c];
                    row[o + 1] = Layers[lG][r, c];
                    row[o + 2] = Layers[lR][r, c];
                    row[o + 3] = Layers[lA][r, c];
                }
                fs.Write(row, 0, row.Length);
            }
        }
    }

    /// tint_colors carries a per-texel colour multiply over the whole terrain;
    /// tint_geometry a per-texel normal.
    ///
    /// We used to write both dead flat, which is why our maps look uniform next
    /// to hand-made ones: The_Forge's tint has 45,737 distinct values sampled,
    /// ours had exactly one. This adds a gentle large-scale wash - nothing
    /// structural, just enough that the ground is not one shade from corner to
    /// corner - plus a slight darkening in the hollows and lift on exposed
    ///ground, which is what an artist would paint first.
    ///
    /// Alpha is 127, matching the developers' own generator. We were writing 0.
    public static void WriteTints(string dir, int res)
    {
        WriteTintColors(Path.Combine(dir, "tint_colors.tga"), res);
        WriteFlatTga(Path.Combine(dir, "tint_geometry.tga"), res, 255, 128, 128, 255);
    }

    static void WriteTintColors(string path, int res)
    {
        using (var fs = File.Create(path))
        {
            WriteTgaHeader(fs, res, res);
            var row = new byte[res * 4];
            float px = MapSize / res;

            // Height range, so the shading is relative to this map rather than
            // to an absolute altitude that means nothing on a flat map.
            float lo = 1e9f, hi = -1e9f;
            for (int r = 0; r < HRes; r++)
                for (int c = 0; c < HRes; c++)
                {
                    float h = Height[r, c];
                    if (h < lo) lo = h;
                    if (h > hi) hi = h;
                }
            float span = Math.Max(1f, hi - lo);

            // Bottom-up, matching WriteStratumPair and the shipped files.
            for (int y = res - 1; y >= 0; y--)
            {
                for (int x = 0; x < res; x++)
                {
                    float wx = (x + 0.5f) * px, wz = MapSize - (y + 0.5f) * px;

                    // Two octaves of wash at very different scales: a broad one
                    // that varies across the map, a finer one that breaks it up.
                    float broad = Fbm(wx, wz, 2213, 2, MapSize * 0.55f) - 0.5f;
                    float fine  = Fbm(wx, wz, 8837, 3, MapSize * 0.11f) - 0.5f;
                    float wash  = broad * 0.10f + fine * 0.055f;

                    // A little more light on high ground, a little less in the
                    // hollows.
                    float rel = Clamp01((Sample(Height, wx, wz) - lo) / span) - 0.5f;
                    float lift = rel * 0.07f;

                    float t = Clamp01(0.5f + wash + lift);
                    byte v = (byte)Math.Round(t * 255f);

                    int o = x * 4;
                    row[o] = v; row[o + 1] = v; row[o + 2] = v; row[o + 3] = 127;
                }
                fs.Write(row, 0, row.Length);
            }
        }
    }

    static void WriteFlatTga(string path, int res, byte b, byte g, byte r, byte a)
    {
        using (var fs = File.Create(path))
        {
            WriteTgaHeader(fs, res, res);
            var row = new byte[res * 4];
            for (int x = 0; x < res; x++)
            {
                int o = x * 4;
                row[o] = b; row[o + 1] = g; row[o + 2] = r; row[o + 3] = a;
            }
            for (int y = 0; y < res; y++) fs.Write(row, 0, row.Length);
        }
    }

    // ---- preview -------------------------------------------------------
    // Approximate albedo of each stratum layer, for the preview only.
    //          idx 0 unused, then 1..8
    static readonly float[,] LayerCol = {
        { 0.00f, 0.00f, 0.00f },   // (unused)
        { 0.36f, 0.34f, 0.33f },   // 1 rock_basalt01 (cliff faces)
        { 0.42f, 0.34f, 0.24f },   // 2 heather03
        { 0.48f, 0.53f, 0.27f },   // 3 grass02
        { 0.23f, 0.42f, 0.31f },   // 4 grass03
        { 0.33f, 0.27f, 0.19f },   // 5 mud02
        { 0.78f, 0.70f, 0.50f },   // 6 sand02
        { 0.53f, 0.50f, 0.47f },   // 7 rock_cliff03
        { 0.64f, 0.61f, 0.56f },   // 8 gravel01
    };

    public static void WritePreview(string path, int res, bool annotate,
                                    float[] markX, float[] markZ, int[] markKind)
    {
        var rgb = new byte[res * res * 3];
        float px = MapSize / res;
        for (int r = 0; r < res; r++)
        for (int c = 0; c < res; c++)
        {
            float x = (c + 0.5f) * px, z = MapSize - (r + 0.5f) * px;
            float h = Sample(Height, x, z);
            float rr, gg, bb;

            // Composite the real stratum weights so the preview shows what the
            // terrain shader will actually blend, not a second set of rules.
            int sc = Math.Min(SRes - 1, (int)(x / MapSize * SRes));
            int sr = Math.Min(SRes - 1, (int)((MapSize - z) / MapSize * SRes));
            rr = 0.28f; gg = 0.40f; bb = 0.19f;                       // layer 0, grass07
            for (int L = 1; L <= 8; L++)
            {
                float w = Layers[L][sr, sc] / 255f;
                if (w <= 0.002f) continue;
                float lr = LayerCol[L, 0], lg = LayerCol[L, 1], lb = LayerCol[L, 2];
                rr = Lerp(rr, lr, w); gg = Lerp(gg, lg, w); bb = Lerp(bb, lb, w);
            }

            // cheap directional shading
            float hx = Sample(Height, Math.Min(x + 1f, MapSize), z) - h;
            float hz = Sample(Height, x, Math.Min(z + 1f, MapSize)) - h;
            float shade = Clamp01(0.78f + 0.22f * (hz - hx));
            rr *= shade; gg *= shade; bb *= shade;

            if (h < WaterLevel)
            {
                float d = Clamp01((WaterLevel - h) / 4f);
                float wr = Lerp(0.34f, 0.06f, d), wg = Lerp(0.58f, 0.22f, d), wb = Lerp(0.64f, 0.42f, d);
                float op = Lerp(0.45f, 0.92f, d);
                rr = Lerp(rr, wr, op); gg = Lerp(gg, wg, op); bb = Lerp(bb, wb, op);
            }

            int o = (r * res + c) * 3;
            rgb[o]     = (byte)(Clamp01(rr) * 255);
            rgb[o + 1] = (byte)(Clamp01(gg) * 255);
            rgb[o + 2] = (byte)(Clamp01(bb) * 255);
        }

        if (annotate && markX != null)
        {
            for (int m = 0; m < markX.Length; m++)
            {
                int c = (int)(markX[m] / px), r = (int)((MapSize - markZ[m]) / px);
                int k = markKind[m];
                int rad = k == 0 ? 6 : (k == 1 ? 3 : 1);
                byte kr = k == 0 ? (byte)255 : (k == 1 ? (byte)255 : (byte)20);
                byte kg = k == 0 ? (byte)40  : (k == 1 ? (byte)230 : (byte)70);
                byte kb = k == 0 ? (byte)40  : (k == 1 ? (byte)40  : (byte)25);
                for (int dr = -rad; dr <= rad; dr++)
                for (int dc = -rad; dc <= rad; dc++)
                {
                    if (dr * dr + dc * dc > rad * rad) continue;
                    int rr2 = r + dr, cc2 = c + dc;
                    if (rr2 < 0 || rr2 >= res || cc2 < 0 || cc2 >= res) continue;
                    int o = (rr2 * res + cc2) * 3;
                    rgb[o] = kr; rgb[o + 1] = kg; rgb[o + 2] = kb;
                }
            }
        }

        WritePng(path, res, res, rgb);
    }

    // Elevation view: colour ramp by height with contour bands every 4 m, so
    // the shape of the terrain can be judged without the texturing on top.
    public static void WriteHeightPreview(string path, int res)
    {
        var rgb = new byte[res * res * 3];
        float px = MapSize / res;
        float lo = 1e9f, hi = -1e9f;
        for (int r = 0; r < HRes; r++)
            for (int c = 0; c < HRes; c++)
            {
                if (Height[r, c] < lo) lo = Height[r, c];
                if (Height[r, c] > hi) hi = Height[r, c];
            }

        for (int r = 0; r < res; r++)
        for (int c = 0; c < res; c++)
        {
            float x = (c + 0.5f) * px, z = MapSize - (r + 0.5f) * px;
            float h = Sample(Height, x, z);
            float t = Clamp01((h - lo) / Math.Max(0.001f, hi - lo));

            float rr, gg, bb;
            if (h < WaterLevel) { rr = 0.10f; gg = 0.22f; bb = 0.45f; }
            else
            {
                // dark green -> tan -> white, with a contour line every 4 m
                if (t < 0.5f) { float u = t / 0.5f; rr = Lerp(0.13f, 0.68f, u); gg = Lerp(0.34f, 0.62f, u); bb = Lerp(0.16f, 0.34f, u); }
                else          { float u = (t - 0.5f) / 0.5f; rr = Lerp(0.68f, 0.98f, u); gg = Lerp(0.62f, 0.98f, u); bb = Lerp(0.34f, 0.98f, u); }
                float band = (h - WaterLevel) / 4f;
                if (band - (float)Math.Floor(band) < 0.10f) { rr *= 0.62f; gg *= 0.62f; bb *= 0.62f; }
            }

            float hx = Sample(Height, Math.Min(x + 1f, MapSize), z) - h;
            float hz = Sample(Height, x, Math.Min(z + 1f, MapSize)) - h;
            float shade = Clamp01(0.80f + 0.20f * (hz - hx));
            int o = (r * res + c) * 3;
            rgb[o]     = (byte)(Clamp01(rr * shade) * 255);
            rgb[o + 1] = (byte)(Clamp01(gg * shade) * 255);
            rgb[o + 2] = (byte)(Clamp01(bb * shade) * 255);
        }
        WritePng(path, res, res, rgb);
    }

    // Walkability view: green = reachable from the seed, amber = walkable but
    // cut off, grey = too steep, blue = water.
    public static void WriteWalkPreview(string path, int res, bool[,] reach)
    {
        var rgb = new byte[res * res * 3];
        float step = MapSize / (HRes - 1);
        for (int r = 0; r < res; r++)
        for (int c = 0; c < res; c++)
        {
            float x = (c + 0.5f) * (MapSize / res), z = MapSize - (r + 0.5f) * (MapSize / res);
            int hr = Math.Min(HRes - 1, Math.Max(0, (int)Math.Round((MapSize - z) / step)));
            int hc = Math.Min(HRes - 1, Math.Max(0, (int)Math.Round(x / step)));
            float h = Height[hr, hc];

            byte rr, gg, bb;
            if (h <= WaterLevel)              { rr = 26;  gg = 56;  bb = 115; }
            else if (reach != null && reach[hr, hc]) { rr = 70;  gg = 175; bb = 80; }
            else if (Walkable[hr, hc])        { rr = 225; gg = 170; bb = 45; }
            else                              { rr = 90;  gg = 90;  bb = 96; }

            int o = (r * res + c) * 3;
            rgb[o] = rr; rgb[o + 1] = gg; rgb[o + 2] = bb;
        }
        WritePng(path, res, res, rgb);
    }

    // Minimal PNG writer: RGB8, zlib stored blocks. No external dependency.
    static void WritePng(string path, int w, int h, byte[] rgb)
    {
        var raw = new byte[h * (1 + w * 3)];
        for (int y = 0; y < h; y++)
        {
            raw[y * (1 + w * 3)] = 0;                                    // filter: none
            Buffer.BlockCopy(rgb, y * w * 3, raw, y * (1 + w * 3) + 1, w * 3);
        }

        using (var fs = File.Create(path))
        {
            fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);

            var ihdr = new byte[13];
            BeInt(ihdr, 0, w); BeInt(ihdr, 4, h);
            ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
            Chunk(fs, "IHDR", ihdr);

            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78); ms.WriteByte(0x01);
                int pos = 0;
                while (pos < raw.Length)
                {
                    int n = Math.Min(65535, raw.Length - pos);
                    bool last = pos + n >= raw.Length;
                    ms.WriteByte((byte)(last ? 1 : 0));
                    ms.WriteByte((byte)(n & 0xff)); ms.WriteByte((byte)(n >> 8));
                    ms.WriteByte((byte)(~n & 0xff)); ms.WriteByte((byte)((~n >> 8) & 0xff));
                    ms.Write(raw, pos, n);
                    pos += n;
                }
                uint a = 1, b = 0;
                foreach (byte v in raw) { a = (a + v) % 65521; b = (b + a) % 65521; }
                uint adler = (b << 16) | a;
                ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16));
                ms.WriteByte((byte)(adler >> 8));  ms.WriteByte((byte)adler);
                Chunk(fs, "IDAT", ms.ToArray());
            }

            Chunk(fs, "IEND", new byte[0]);
        }
    }

    static void BeInt(byte[] a, int o, int v)
    {
        a[o] = (byte)(v >> 24); a[o + 1] = (byte)(v >> 16);
        a[o + 2] = (byte)(v >> 8); a[o + 3] = (byte)v;
    }

    static uint[] crcTable;
    static void Chunk(Stream fs, string type, byte[] data)
    {
        if (crcTable == null)
        {
            crcTable = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = ((c & 1) != 0) ? (0xedb88320u ^ (c >> 1)) : (c >> 1);
                crcTable[n] = c;
            }
        }
        var len = new byte[4]; BeInt(len, 0, data.Length);
        fs.Write(len, 0, 4);
        var tb = new byte[4];
        for (int i = 0; i < 4; i++) tb[i] = (byte)type[i];
        fs.Write(tb, 0, 4);
        fs.Write(data, 0, data.Length);
        uint crc = 0xffffffffu;
        foreach (byte v in tb)   crc = crcTable[(crc ^ v) & 0xff] ^ (crc >> 8);
        foreach (byte v in data) crc = crcTable[(crc ^ v) & 0xff] ^ (crc >> 8);
        crc ^= 0xffffffffu;
        var cb = new byte[4]; BeInt(cb, 0, (int)crc);
        fs.Write(cb, 0, 4);
    }
}
