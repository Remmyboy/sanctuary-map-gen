// Resource layout, derived from measurement rather than taste.
//
// Three independent sources agree on the shape, so it is not a judgement call:
//
//   * Neroxis (FAForever's generator) places base mexes in the annulus 5..15
//     around each spawn at 10 spacing, 3-5 of them, before anything else is
//     placed - then excludes r<24 for the next pass and r<48 for the rest.
//   * 291 Supreme Commander maps on disk: the median spawn has 4 mass inside
//     16 m, and the median map's furthest "nearest mass" is 10 m.
//   * 47 Sanctuary maps shipped by the developers: 3-5 alloys at 6-16 m from
//     each spawn, then a gap - the next nearest is 33 m or more.
//
// The generator used to place its "near" band at 0.055..0.16 of map size,
// which on a 512 m map is 28..82 m. Nothing could land in the ring that
// matters, so every base started with no extractor in sight. Measured against
// the shipped maps our output had zero alloys within 20 m of a spawn where
// they have a median of four.
//
// Resource totals come from the same corpus, split by player count, because
// per-player count is the invariant - not density. Mass per player barely
// moves between a 256 m map and a 4096 m one (12 / 11 / 14 / 13.5 / 17), while
// per-square-kilometre density spans 9.5 to 366.
public static partial class MapGen
{
    /// Alloys in the ring around every commander. The one number a player
    /// notices in the first ten seconds.
    public static int   BaseAlloys        = 4;
    public static float BaseAlloyRMin     = 7f;
    public static float BaseAlloyRMax     = 15f;
    public static float BaseAlloySpacing  = 8f;

    /// Expansions are clusters, not scattered singles - somewhere to go and
    /// something to hold. Neroxis uses 3-4 within a 10-unit radius.
    public static int   ExpansionMin      = 3;
    public static int   ExpansionMax      = 4;
    public static float ExpansionRadius   = 11f;
    public static float ExpansionSpacing  = 9f;

    /// How many alloys each player should get.
    ///
    /// This is Neroxis's own formula rather than a table, because it handles
    /// map size as well as player count, and it lands on the corpus medians:
    /// 2 players on 512 m gives 18, which is exactly the measured median for
    /// 1v1 maps, and 8 players gives 11 against a measured 11.8.
    ///
    ///     spawnCount <= 2   ->  10 + 20 * density
    ///     spawnCount <= 4   ->  12 +  6 * density
    ///     spawnCount <= 10  ->   8 +  7 * density
    ///     otherwise         ->   6 +  7 * density
    ///
    /// then halved below 384 m and multiplied by 1.25-1.75 at 768 m and above,
    /// with a floor of 9. Density 0.4 reproduces the corpus; raise it for a
    /// rich map, drop it for a starvation map.
    public static float ResourceDensity = 0.4f;

    public static int AlloyBudget(int players, int size)
    {
        float d = Clamp01(ResourceDensity);
        float baseCount;
        if (players <= 2)       baseCount = 10f + 20f * d;
        else if (players <= 4)  baseCount = 12f +  6f * d;
        else if (players <= 10) baseCount =  8f +  7f * d;
        else                    baseCount =  6f +  7f * d;

        float mult = 1f;
        if (size < 384) mult = 0.5f;
        else if (size >= 768) mult = players <= 4 ? 1.75f : (players <= 10 ? 1.5f : 1.25f);

        return Math.Max(9, (int)Math.Round(baseCount * mult));
    }

    /// Kept for callers that do not know the map size.
    public static int AlloyBudget(int players) { return AlloyBudget(players, (int)MapSize); }

    /// Median closest-spawn-pair distance in the corpus, as a fraction of map
    /// size. Team maps put allies side by side, which is why this collapses as
    /// the player count rises - it is not a quality signal above 4 players.
    public static float SpawnSeparationTarget(int players)
    {
        switch (players)
        {
            case 2:  return 0.80f;
            case 3:  return 0.25f;
            case 4:  return 0.39f;
            case 6:  return 0.24f;
            case 8:  return 0.15f;
            default: return players <= 4 ? 0.30f : 0.10f;
        }
    }

    /// The ring around one spawn. Placed first and never rejected wholesale:
    /// if the ideal annulus will not take four spots the radius is widened in
    /// steps rather than the spawn being left bare, because a commander with
    /// no extractor is a worse map than one with a slightly wide ring.
    ///
    /// Returns x,z pairs. Symmetry is the caller's business.
    public static List<float> PlaceBaseRing(int seed, float sx, float sz,
                                            bool[,] reach, float maxSlope)
    {
        var rng = new Random(seed);
        var ring = new List<float>();

        // Start at the measured annulus and give ground grudgingly.
        float[][] attempts =
        {
            new[] { BaseAlloyRMin, BaseAlloyRMax, BaseAlloySpacing },
            new[] { BaseAlloyRMin, BaseAlloyRMax * 1.4f, BaseAlloySpacing * 0.85f },
            new[] { BaseAlloyRMin, BaseAlloyRMax * 2.0f, BaseAlloySpacing * 0.7f },
        };

        foreach (var a in attempts)
        {
            ring.Clear();
            float rMin = a[0], rMax = a[1], spacing = a[2];

            // Spread the starting angles so four spots do not clump on one
            // side; jitter keeps it from looking like a compass rose.
            double phase = rng.NextDouble() * Math.PI * 2.0;
            for (int i = 0; i < BaseAlloys; i++)
            {
                bool got = false;
                for (int tries = 0; tries < 220 && !got; tries++)
                {
                    double slice = Math.PI * 2.0 / BaseAlloys;
                    double ang = phase + i * slice + (rng.NextDouble() - 0.5) * slice * 0.9;
                    float d = rMin + (float)rng.NextDouble() * (rMax - rMin);
                    float x = sx + (float)Math.Cos(ang) * d;
                    float z = sz + (float)Math.Sin(ang) * d;

                    if (!SpotViable(x, z, reach, maxSlope, 0f)) continue;

                    bool clash = false;
                    for (int p = 0; p < ring.Count && !clash; p += 2)
                    {
                        float dx = x - ring[p], dz = z - ring[p + 1];
                        if (dx * dx + dz * dz < spacing * spacing) clash = true;
                    }
                    // And against this spot's own mirror images, or a ring on a
                    // spawn near the map centre collides with its rotation.
                    for (int k = 1; k < SymOrder && !clash; k++)
                    {
                        RotateWorld(x, z, k, out float px, out float pz);
                        float dx = x - px, dz = z - pz;
                        if (dx * dx + dz * dz < spacing * spacing) clash = true;
                    }
                    if (clash) continue;

                    ring.Add(x); ring.Add(z);
                    got = true;
                }
            }
            if (ring.Count / 2 >= BaseAlloys) break;
        }
        return ring;
    }

    /// A cluster of 3-4 alloys at a chosen spot, the thing an expansion is.
    public static List<float> PlaceCluster(Random rng, float cx, float cz,
                                           int count, bool[,] reach, float maxSlope,
                                           List<float> avoid, float avoidSpacing)
    {
        var outp = new List<float>();
        for (int i = 0; i < count; i++)
        {
            for (int tries = 0; tries < 160; tries++)
            {
                double ang = rng.NextDouble() * Math.PI * 2.0;
                float d = (float)Math.Sqrt(rng.NextDouble()) * ExpansionRadius;
                float x = cx + (float)Math.Cos(ang) * d;
                float z = cz + (float)Math.Sin(ang) * d;
                if (!SpotViable(x, z, reach, maxSlope, 0f)) continue;

                bool clash = false;
                for (int p = 0; p < outp.Count && !clash; p += 2)
                {
                    float dx = x - outp[p], dz = z - outp[p + 1];
                    if (dx * dx + dz * dz < ExpansionSpacing * ExpansionSpacing) clash = true;
                }
                for (int p = 0; p < avoid.Count && !clash; p += 2)
                {
                    float dx = x - avoid[p], dz = z - avoid[p + 1];
                    if (dx * dx + dz * dz < avoidSpacing * avoidSpacing) clash = true;
                }
                for (int k = 1; k < SymOrder && !clash; k++)
                {
                    RotateWorld(x, z, k, out float px, out float pz);
                    float dx = x - px, dz = z - pz;
                    if (dx * dx + dz * dz < ExpansionSpacing * ExpansionSpacing) clash = true;
                }
                if (clash) continue;

                outp.Add(x); outp.Add(z);
                break;
            }
        }
        return outp;
    }

    /// Full resource layout for one symmetry sector: base rings first, then
    /// expansion clusters, then singles to make up the budget.
    ///
    /// Ordering matters. Base rings are placed before anything can occupy the
    /// space, which is the whole point - the old code placed a scattered band
    /// and hoped some of it landed near a spawn.
    public static float[] PlaceResourcesV2(int seed, bool[,] reach, int sectorSpawns,
                                           int alloysPerPlayer, float maxSlope, float minRiver)
    {
        var rng = new Random(seed);
        var all = new List<float>();
        float cx = MapSize * 0.5f, cz = MapSize * 0.5f;
        float sectorHalf = (float)(Math.PI / SymOrder);
        float spawnAng = (float)Math.Atan2(BaseZ[0] - cz, BaseX[0] - cx);

        if (sectorSpawns < 1) sectorSpawns = 1;

        // 1. the ring around each commander in this sector
        for (int s = 0; s < sectorSpawns && s < BaseX.Length; s++)
            all.AddRange(PlaceBaseRing(seed + 7919 * (s + 1), BaseX[s], BaseZ[s], reach, maxSlope));

        int budget = alloysPerPlayer * sectorSpawns;
        int remaining = Math.Max(0, budget - all.Count / 2);

        // 2. expansions, at radii that put them between the bases and the
        //    middle - the contested ground worth fighting over
        float expRMin = MapSize * 0.16f, expRMax = MapSize * 0.44f;
        while (remaining >= ExpansionMin)
        {
            int want = Math.Min(remaining, ExpansionMin + rng.Next(ExpansionMax - ExpansionMin + 1));
            bool placed = false;

            for (int tries = 0; tries < 400 && !placed; tries++)
            {
                double ang = spawnAng + (rng.NextDouble() * 2.0 - 1.0) * sectorHalf;
                float d = expRMin + (float)rng.NextDouble() * (expRMax - expRMin);
                float ex = cx + (float)Math.Cos(ang) * d;
                float ez = cz + (float)Math.Sin(ang) * d;

                if (!SpotViable(ex, ez, reach, maxSlope, minRiver)) continue;

                // Clear of the spawns, and of other clusters.
                bool bad = false;
                for (int b = 0; b < BaseX.Length && !bad; b++)
                {
                    float dx = ex - BaseX[b], dz = ez - BaseZ[b];
                    if (dx * dx + dz * dz < (MapSize * 0.10f) * (MapSize * 0.10f)) bad = true;
                }
                for (int p = 0; p < all.Count && !bad; p += 2)
                {
                    float dx = ex - all[p], dz = ez - all[p + 1];
                    if (dx * dx + dz * dz < (MapSize * 0.085f) * (MapSize * 0.085f)) bad = true;
                }
                if (bad) continue;

                var cluster = PlaceCluster(rng, ex, ez, want, reach, maxSlope, all, ExpansionSpacing);
                if (cluster.Count / 2 < ExpansionMin) continue;

                all.AddRange(cluster);
                remaining -= cluster.Count / 2;
                placed = true;
            }
            if (!placed) break;      // no room left for another expansion
        }

        // 3. singles to finish the budget, anywhere sensible in the sector
        for (int i = 0, guard = 0; i < remaining && guard < 6000; guard++)
        {
            double ang = spawnAng + (rng.NextDouble() * 2.0 - 1.0) * sectorHalf;
            float d = MapSize * 0.12f + (float)rng.NextDouble() * MapSize * 0.36f;
            float x = cx + (float)Math.Cos(ang) * d;
            float z = cz + (float)Math.Sin(ang) * d;
            if (!SpotViable(x, z, reach, maxSlope, minRiver)) continue;

            bool bad = false;
            for (int b = 0; b < BaseX.Length && !bad; b++)
            {
                float dx = x - BaseX[b], dz = z - BaseZ[b];
                if (dx * dx + dz * dz < (MapSize * 0.06f) * (MapSize * 0.06f)) bad = true;
            }
            float sp = MapSize * 0.05f;
            for (int p = 0; p < all.Count && !bad; p += 2)
            {
                float dx = x - all[p], dz = z - all[p + 1];
                if (dx * dx + dz * dz < sp * sp) bad = true;
            }
            for (int k = 1; k < SymOrder && !bad; k++)
            {
                RotateWorld(x, z, k, out float px, out float pz);
                float dx = x - px, dz = z - pz;
                if (dx * dx + dz * dz < sp * sp) bad = true;
            }
            if (bad) continue;

            all.Add(x); all.Add(z);
            i++;
        }

        return all.ToArray();
    }

    /// Worst-case alloys within `radius` of any spawn. The gate that would have
    /// caught the bare-base bug, and the one number to check after any change
    /// to resource placement.
    public static int MinAlloysNearSpawn(float[] ax, float[] az, float radius)
    {
        int worst = int.MaxValue;
        for (int b = 0; b < BaseX.Length; b++)
        {
            int n = 0;
            for (int i = 0; i < ax.Length; i++)
            {
                float dx = ax[i] - BaseX[b], dz = az[i] - BaseZ[b];
                if (dx * dx + dz * dz < radius * radius) n++;
            }
            if (n < worst) worst = n;
        }
        return worst == int.MaxValue ? 0 : worst;
    }

    /// Snap a world coordinate to the build grid.
    ///
    /// A resource spot claims a 2x2 block of whole cells - the
    /// GridModifierTemplate with int2(2, 2) in resourceSpotTemplateLoader.lua -
    /// and placementUtils.GetPlacementBounds floors the position to choose that
    /// block:
    ///
    ///     minX = floor(position.x - skirtSize.x / 2)
    ///     maxX = minX + skirtSize.x - 1
    ///
    /// An even footprint therefore centres on a whole metre, so a marker at x.5
    /// sits half a metre from the extractor that lands on it. Whole metres it
    /// is - which is what the hand-authored There_Is_Time uses. The developers'
    /// own generator puts markers on x.5 and is presumably out by the same half
    /// metre; being consistent with the placement maths matters more than being
    /// consistent with them.
    public static float SnapBuild(float v) { return (float)Math.Round(v); }
}
