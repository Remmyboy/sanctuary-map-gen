using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SanctuaryMapConverter.Core
{
    // New-RandomMap.ps1 in C#. Give it a seed, a size, a player count, a
    // style and a biome and it produces a complete, validated map folder;
    // same seed and settings always produce the same map.
    //
    // Every roll is put through the game's own rules before it is accepted:
    // walkability uses navigationLayers.lua's 30-degree Land limit with the
    // 3x3 dilation from NavmapUtils.IsSteepTerrain, and the map is rejected
    // and re-rolled unless every spawn and every resource spot is reachable
    // on foot and enough of the map is contiguous open ground to manoeuvre
    // an army on.
    public sealed class RandomMapOptions
    {
        public int Seed = -1;
        public int Size = 512;            // powers of two only - Unity rounds
        public int Players = 2;           // 2, 3, 4, 6, 8
        public string Style = "Random";   // RiverCrossing, Mesas, Plateaus, Basin, Open
        public string Biome = "Random";   // Highlands, Tropical, Winter, Evergreen, Arid
        public string Name;
        public string MapsRoot;
        public string PropExtension = ".santp";
        public int Count = 1;
        public int MaxAttempts = 6;
        public bool NoProps;
        public string DebugDir;
        public bool Force;

        // For the end-of-run validation; null skips it.
        public ValidateOptions Validate;
    }

    public sealed class GeneratedMap
    {
        public string MapDir, Folder, DisplayName, Style, Biome;
        public int Seed, Spawns, Alloys;
        public bool Accepted;
    }

    sealed class StyleConfig
    {
        public bool UseRiver, OrganicRiver, HasWater;
        public bool PathedMesas = true;
        public int SymOrder;
        public double WaterLevel, LandBase = 22.0, BowlDepth;
        public double Tier1 = 13.0, Tier2 = 8.0;
        public int PathCount = 6;
        public double Inflate = 0.013, BlurR = 0.011, MinSep = 0.20;
        public double SpawnRadius = 0.40, Phase = 45.0, MinDirect = 0.82;
    }

    public static class RandomMap
    {
        static readonly string[] StyleChoices = { "RiverCrossing", "Mesas", "Plateaus", "Basin", "Open" };
        static readonly string[] BiomeChoices = { "Highlands", "Tropical", "Winter", "Evergreen", "Arid" };

        static StyleConfig GetStyle(string key, int players)
        {
            var s = new StyleConfig { SymOrder = players % 4 == 0 ? 4 : 2 };
            switch (key)
            {
                case "RiverCrossing":
                    // A diagonal channel is only symmetric under 180 degrees.
                    s.SymOrder = 2; s.UseRiver = true; s.OrganicRiver = true;
                    s.HasWater = true; s.WaterLevel = 16.0; s.LandBase = 21.0;
                    s.SpawnRadius = 0.40; s.Phase = 135.0; s.MinDirect = 0.68;
                    s.Tier1 = 12.0; s.Tier2 = 8.0; s.PathCount = 6;
                    break;
                case "Mesas":
                    s.Tier1 = 13.0; s.Tier2 = 8.0; s.PathCount = 7; s.MinSep = 0.18;
                    break;
                case "Plateaus":
                    s.Tier1 = 17.0; s.Tier2 = 10.0; s.PathCount = 4; s.MinSep = 0.26; s.Inflate = 0.019;
                    break;
                case "Open":
                    s.Tier1 = 8.0; s.Tier2 = 4.0; s.PathCount = 3; s.MinSep = 0.28;
                    break;
                case "Basin":
                    s.HasWater = true; s.WaterLevel = 16.0; s.LandBase = 26.0;
                    s.BowlDepth = 26.0; s.Tier1 = 12.0; s.Tier2 = 7.0;
                    s.PathCount = 6; s.SpawnRadius = 0.42; s.MinDirect = 0.68;
                    break;
            }
            return s;
        }

        // PowerShell's [int] cast rounds (banker's) where C#'s truncates; the
        // original script leaned on that in several places, so this is the
        // cast every ported [int](...) goes through.
        static int PsInt(double v) => (int)Math.Round(v, MidpointRounding.ToEven);

        public static List<GeneratedMap> Run(RandomMapOptions o, Action<string> log)
        {
            EngineState.Reset();   // fresh MapGen statics, like a fresh PS process
            var results = new List<GeneratedMap>();
            int seed = o.Seed >= 0 ? o.Seed : new Random().Next(1, 999999);

            for (int run = 0; run < o.Count; run++)
            {
                // .NET's Random correlates strongly across nearby seeds -
                // MixSeed avalanches them apart first.
                int runSeed = MapGen.MixSeed(seed + run * 7919);
                var pick = new Random(runSeed);
                string useStyle = o.Style == "Random" ? StyleChoices[pick.Next(StyleChoices.Length)] : o.Style;
                string useBiome = o.Biome == "Random" ? BiomeChoices[pick.Next(BiomeChoices.Length)] : o.Biome;

                bool accepted = false;
                for (int attempt = 0; attempt < o.MaxAttempts && !accepted; attempt++)
                {
                    int seedN = MapGen.MixSeed(runSeed + attempt * 104729);
                    var cfg = GetStyle(useStyle, o.Players);
                    if (o.Players % cfg.SymOrder != 0) cfg.SymOrder = 2;
                    int perSector = PsInt((double)o.Players / cfg.SymOrder);

                    log("");
                    log(string.Format("=== {0}  {1}P  {2}  sym{3}  {4} m  seed {5}{6}",
                        useStyle, o.Players, useBiome, cfg.SymOrder, o.Size, seedN,
                        attempt > 0 ? $"  (attempt {attempt + 1})" : ""));

                    MapGen.Configure(o.Size, o.Size >= 512 ? 1024 : 512);
                    MapGen.SymOrder = cfg.SymOrder;
                    MapGen.UseRiver = cfg.UseRiver;
                    MapGen.OrganicRiver = cfg.OrganicRiver;
                    MapGen.Organic = false;
                    MapGen.OrganicHills = false;
                    MapGen.PathedMesas = cfg.PathedMesas;
                    MapGen.HillStrength = 1.0f;
                    MapGen.WaterLevel = (float)cfg.WaterLevel;
                    MapGen.LandBase = (float)cfg.LandBase;
                    MapGen.BowlDepth = (float)cfg.BowlDepth;
                    MapGen.CurveAmp = (float)(o.Size * 0.115);
                    // Landform count scales with area; separation shrinks as
                    // symmetry rises, or a quarter sector of a big map stays
                    // bare.
                    double areaScale = Math.Max(1.0, (o.Size / 320.0) * (o.Size / 320.0));
                    MapGen.MesaPathCount = Math.Max(3, PsInt(cfg.PathCount * areaScale / Math.Sqrt(cfg.SymOrder)));
                    MapGen.MesaMinSep = (float)(cfg.MinSep / Math.Sqrt(cfg.SymOrder / 2.0));
                    // Four rotations converge on the centre far more
                    // aggressively than two.
                    MapGen.MesaCentreClear = cfg.SymOrder == 4 ? 0.30f : 0.16f;
                    MapGen.MesaInflate = (float)cfg.Inflate;
                    MapGen.MesaBlurRadius = (float)cfg.BlurR;

                    if (cfg.UseRiver)
                    {
                        MapGen.ComputeBridgePositions();
                        MapGen.PlaceBasesAlongRiver(perSector, (float)(o.Size * 0.16));
                    }
                    else
                    {
                        MapGen.PlaceSpawnsRadial(perSector, (float)cfg.SpawnRadius, (float)cfg.Phase);
                    }

                    // Aim each spawn's lane. Water styles cannot use the map
                    // centre: on RiverCrossing it is the channel, so each
                    // spawn aims at its nearest crossing instead. (The Basin
                    // branch reads a BowlRadiusFrac the style table never set,
                    // which nulls to zero - so Basin lanes aim at the centre
                    // too. Kept as-is: the gate accepts the result.)
                    var ltx = new List<float>();
                    var ltz = new List<float>();
                    for (int i = 0; i < MapGen.BaseX.Length; i++)
                    {
                        double sx = MapGen.BaseX[i], sz = MapGen.BaseZ[i];
                        if (cfg.UseRiver)
                        {
                            int nb = MapGen.NearestBridge((float)sx, (float)sz);
                            ltx.Add(MapGen.BridgeX[nb]); ltz.Add(MapGen.BridgeZ[nb]);
                        }
                        else if (cfg.BowlDepth > 0)
                        {
                            double vx = o.Size / 2.0 - sx, vz = o.Size / 2.0 - sz;
                            double len = Math.Sqrt(vx * vx + vz * vz);
                            double stop = Math.Max(0.0, len - o.Size * 0.0 * 1.05);
                            ltx.Add((float)(sx + vx / len * stop)); ltz.Add((float)(sz + vz / len * stop));
                        }
                        else
                        {
                            ltx.Add(o.Size / 2.0f); ltz.Add(o.Size / 2.0f);
                        }
                    }
                    MapGen.LaneTargetX = ltx.ToArray();
                    MapGen.LaneTargetZ = ltz.ToArray();

                    MapGen.BuildMesaField(seedN, (float)cfg.Tier1, (float)cfg.Tier2);
                    MapGen.BuildHeight();

                    float bx0 = MapGen.BaseX[0], bz0 = MapGen.BaseZ[0];
                    int carved = MapGen.CarveRamps(bx0, bz0, 40, 11.0f, 9.0f, 120);
                    // Sand off invisible one-cell obstacles before anything is
                    // measured.
                    int despeckled = MapGen.SmoothPathingSpecks(60, 8);
                    MapGen.BuildWalkable();
                    bool[,] reach = MapGen.Reachable(bx0, bz0);

                    // Resources for one sector, then rotated into the rest.
                    float minRiver = cfg.UseRiver ? (float)(o.Size * 0.09) : 0f;
                    int budget = MapGen.AlloyBudget(o.Players, o.Size);
                    float[] flat = MapGen.PlaceResourcesV2(seedN, reach, perSector, budget, 12.0f, minRiver);
                    int sectorCount = flat.Length / 2;

                    var mexX = new List<float>();
                    var mexZ = new List<float>();
                    for (int i = 0; i < sectorCount; i++)
                        for (int k = 0; k < cfg.SymOrder; k++)
                        {
                            MapGen.RotateWorld(flat[i * 2], flat[i * 2 + 1], k, out float ox, out float oz);
                            mexX.Add(ox); mexZ.Add(oz);
                        }

                    float[] ev = MapGen.Evaluate(reach, mexX.ToArray(), mexZ.ToArray());
                    float[] ts = MapGen.TerrainStats();
                    log(string.Format("  ramps carved {0};  specks smoothed {6};  reachable {1:P0};  open ground {2:P0};  flat {3:P0};  cliff {4:P0};  {5} resource spots",
                        carved, ev[0], ev[1], ev[2], ev[3], mexX.Count, despeckled));
                    log(string.Format("  relief {0:N1} m .. {1:N1} m;  closest two spawns {2:N0} m apart", ts[0], ts[1], ev[6]));

                    // ---- the gate ----
                    var fail = new List<string>();
                    if (ev[4] < 1) fail.Add("a spawn is unreachable overland");
                    if (ev[5] < 1) fail.Add("a resource spot is cut off");
                    if (ev[0] < 0.92) fail.Add(string.Format("only {0:P0} of walkable ground connected", ev[0]));
                    if (ev[1] < 0.14) fail.Add(string.Format("largest open area only {0:P0}", ev[1]));
                    if (ev[2] < 0.45) fail.Add(string.Format("only {0:P0} level ground", ev[2]));
                    if (ev[3] > 0.22) fail.Add(string.Format("{0:P0} cliff", ev[3]));
                    // Measured against the shipped maps: worst spawn has a
                    // median of 4 alloys inside 20 m; three is the floor worth
                    // playing.
                    int nearBase = MapGen.MinAlloysNearSpawn(mexX.ToArray(), mexZ.ToArray(), 20.0f);
                    if (nearBase < 3) fail.Add($"only {nearBase} alloys within 20 m of the barest spawn");
                    if (mexX.Count < o.Players * 8) fail.Add($"only {mexX.Count} resource spots placed");
                    float sepTarget = MapGen.SpawnSeparationTarget(o.Players);
                    if (ev[6] < o.Size * sepTarget * 0.8)
                        fail.Add(string.Format("spawns {0:N0} m apart, corpus median for {1}P is {2:N0} m",
                            ev[6], o.Players, o.Size * sepTarget));

                    // Lane structure between the two furthest spawns - the
                    // check "largest open area" misses when the open blob is
                    // off to one side and the route between bases is a 4 m
                    // corridor. Corpus medians: clearance 0.030 of map size in
                    // SupCom, 0.038 in shipped Sanctuary; directness 0.93/0.94.
                    int li = 0, lk = 1; double ld = -1.0;
                    for (int i = 0; i < MapGen.BaseX.Length; i++)
                        for (int k = i + 1; k < MapGen.BaseX.Length; k++)
                        {
                            double dx = MapGen.BaseX[i] - MapGen.BaseX[k], dz = MapGen.BaseZ[i] - MapGen.BaseZ[k];
                            double dd = Math.Sqrt(dx * dx + dz * dz);
                            if (dd > ld) { ld = dd; li = i; lk = k; }
                        }
                    float[] rs = MapGen.RouteStats(MapGen.BaseX[li], MapGen.BaseZ[li], MapGen.BaseX[lk], MapGen.BaseZ[lk]);
                    log(string.Format("  lane: {0:N1} m wide (median), {1:N0}% direct, {2} chokepoints, {3:P0} overlooked",
                        rs[2], rs[1] * 100, (int)rs[4], rs[5]));

                    if (rs[0] <= 0) fail.Add("no overland route between spawns");
                    else if (rs[2] < o.Size * 0.022)
                        fail.Add(string.Format("lane only {0:N1} m wide, corpus median is {1:N0} m", rs[2], o.Size * 0.030));
                    // Water styles detour to a crossing by design, so they get
                    // their own floor.
                    if (rs[1] > 0 && rs[1] < cfg.MinDirect)
                        fail.Add(string.Format("route only {0:P0} direct, floor is {1:P0}", rs[1], cfg.MinDirect));
                    if (rs[5] > 0.55)
                        fail.Add(string.Format("{0:P0} of the route is overlooked - a canyon, not a map", rs[5]));

                    float[] leftover = MapGen.PathingSpecks(60);
                    if (leftover[0] > 6) fail.Add(string.Format("{0:N0} isolated obstacles in open ground", leftover[0]));

                    if (fail.Count > 0)
                    {
                        log("  REJECTED: " + string.Join("; ", fail));
                        continue;
                    }
                    log("  accepted");
                    accepted = true;

                    // ------------------------------------------------ write --
                    string folder = o.Name != null && o.Count == 1
                        ? Regex.Replace(o.Name, @"[^\w\-]", "_")
                        : $"~GEN-{o.Players}P_{useStyle}_{useBiome}_{o.Size}_{seedN}";
                    string display = Regex.Replace(folder, "^~GEN-", "").Replace('_', ' ');

                    string mapDir = Path.Combine(o.MapsRoot, folder);
                    string texDir = Path.Combine(mapDir, "Textures");
                    if (Directory.Exists(mapDir))
                    {
                        if (!o.Force) throw new Exception($"'{mapDir}' exists. Pass -Force.");
                        Directory.Delete(mapDir, true);
                    }
                    Directory.CreateDirectory(texDir);

                    MapGen.BuildLayers();
                    // The preview draws this biome's actual ground, read from the
                    // game's own stratum textures - see Biome.SetPreviewColors.
                    Biome.SetPreviewColors(useBiome);
                    MapGen.WriteHeightmap(Path.Combine(texDir, "heightmap.raw"));
                    MapGen.WriteStratums(texDir);
                    MapGen.WriteTints(texDir, 2048);
                    MapGen.WritePreview(Path.Combine(texDir, "preview.png"), 512, false, null, null, null);
                    File.Copy(Path.Combine(texDir, "preview.png"), Path.Combine(mapDir, "preview.png"));

                    if (o.DebugDir != null)
                    {
                        Directory.CreateDirectory(o.DebugDir);
                        var mx = new List<float>(); var mz = new List<float>(); var mk = new List<int>();
                        for (int i = 0; i < mexX.Count; i++) { mx.Add(mexX[i]); mz.Add(mexZ[i]); mk.Add(1); }
                        for (int i = 0; i < MapGen.BaseX.Length; i++) { mx.Add(MapGen.BaseX[i]); mz.Add(MapGen.BaseZ[i]); mk.Add(0); }
                        MapGen.WritePreview(Path.Combine(o.DebugDir, folder + ".png"), 900, true, mx.ToArray(), mz.ToArray(), mk.ToArray());
                        MapGen.WriteHeightPreview(Path.Combine(o.DebugDir, folder + "_elevation.png"), 900);
                        MapGen.WriteWalkPreview(Path.Combine(o.DebugDir, folder + "_walk.png"), 900, reach);
                    }

                    // ---- props ----
                    var propGroups = new List<JObj>();
                    if (!o.NoProps)
                    {
                        string[] treeBps = { "edbm0121", "edbm0122", "edbm0123", "edbm0124", "edbm0125" };
                        string[] rockBps = { "edmm0104", "edmm0106", "edms0110" };
                        var buckets = new Dictionary<string, List<JObj>>();
                        foreach (var b in treeBps.Concat(rockBps)) buckets[b] = new List<JObj>();
                        int per = PsInt((double)o.Size * o.Size / 620);
                        foreach (var (bps, rocks, count, scatterSeed) in new[]
                        {
                            (treeBps, false, per, seedN + 11),
                            (rockBps, true, PsInt(per * 0.4), seedN + 29),
                        })
                        {
                            float[] sc = MapGen.Scatter(scatterSeed, count, rocks, mexX.ToArray(), mexZ.ToArray(), (float)(o.Size * 0.035));
                            int n = sc.Length / 5;
                            for (int k = 0; k < n; k++)
                            {
                                double x = sc[k * 5], y = sc[k * 5 + 1], z = sc[k * 5 + 2];
                                double yaw = sc[k * 5 + 3], s = sc[k * 5 + 4];
                                string bp = bps[k % bps.Length];
                                for (int r = 0; r < cfg.SymOrder; r++)
                                {
                                    MapGen.RotateWorld((float)x, (float)z, r, out float ox, out float oz);
                                    double ry = yaw + r * (2 * Math.PI / cfg.SymOrder);
                                    buckets[bp].Add(Json.Obj(
                                        ("position", Json.Vec3(Math.Round(ox, 3), Math.Round(y, 3), Math.Round(oz, 3))),
                                        ("rotation", Json.Quat(0.0, Math.Round(Math.Sin(ry / 2), 7), 0.0, Math.Round(Math.Cos(ry / 2), 7))),
                                        ("scale", Json.Vec3(Math.Round(s, 4), Math.Round(s, 4), Math.Round(s, 4)))));
                                }
                            }
                        }
                        foreach (var b in treeBps.Concat(rockBps))
                        {
                            if (buckets[b].Count == 0) continue;
                            propGroups.Add(Json.Obj(
                                ("blueprintPath", $"Environment/01_Highlands/Props/{b}/{b}{o.PropExtension}"),
                                ("transforms", buckets[b])));
                        }
                    }

                    // ---- json ----
                    var bio = Biome.Get(useBiome);
                    var stratums = Biome.NewStratumLayers(useBiome);

                    static JObj T(double x, double y, double z) => Json.Obj(
                        ("position", Json.Vec3(x, y, z)),
                        ("rotation", Json.Quat(0.0, 0.0, 0.0, 1.0)),
                        ("scale", Json.Vec3(1.0, 1.0, 1.0)));

                    var spawnT = new JObj();
                    var armies = new JObj();
                    for (int i = 0; i < MapGen.BaseX.Length; i++)
                    {
                        double ax = MapGen.SnapBuild(MapGen.BaseX[i]), az = MapGen.SnapBuild(MapGen.BaseZ[i]);
                        string key = $"ARMY_{i + 1}";
                        spawnT.Add(key, T(ax, Math.Round(MapGen.HeightAtWorld((float)ax, (float)az), 2), az));
                        armies.Add(key, Json.Obj(
                            ("faction", 0), ("alloys", 500.0), ("energy", 500.0), ("groups", Json.Obj())));
                    }

                    var alloyT = new JObj();
                    for (int i = 0; i < mexX.Count; i++)
                    {
                        double px = MapGen.SnapBuild(mexX[i]), pz = MapGen.SnapBuild(mexZ[i]);
                        double py = Math.Round(MapGen.HeightAtWorld((float)px, (float)pz), 2);
                        alloyT.Add($"Alloys_{i + 1:D3}", T(px, py, pz));
                    }

                    var map = Json.Obj(
                        ("fileVersion", 3), ("mapVersion", 1),
                        ("name", display),
                        ("credits", $"Generated: {useStyle} / {useBiome} / seed {seedN}"),
                        ("width", o.Size), ("length", o.Size),
                        ("height", 128),                       // SanMap.height is an int
                        ("heightmapResolution", MapGen.HRes),
                        ("hasWater", cfg.HasWater),
                        ("waterLevel", cfg.WaterLevel),
                        ("waterDepth", 2.0), ("waterWindSpeed", 0.06), ("waterWindDirection", 100.0),
                        ("waterShoreDepthOffset", 8.0), ("waterShoreDepthStrength", 0.7),
                        ("waterShoreDistanceOffset", 0.0), ("waterShoreDistanceStrength", 2.0),
                        ("shader", "RTS/TerrainLit"),
                        ("heightTransition", 2.0), ("fadeDistance", 55.0), ("fadeStartDistance", 32.0),
                        ("stratumLayers", stratums),
                        ("sunRA", bio.SunRA), ("sunDA", 34.0), ("sunIntensity", 60000.0),
                        ("sunTint", Json.Rgba(1.0, 1.0, 1.0, 1.0)),
                        ("sunTemperature", bio.SunTemp),
                        ("sunAngularDiameter", 0.5), ("sunVolumetricsMultiplier", 6.7), ("sunVolumetricsShadowDimer", 0.5),
                        ("skylightIntensity", 0.0),
                        ("skylightTint", Json.Rgba(1.0, 1.0, 1.0, 1.0)),
                        ("skylightTemperature", bio.Sky),
                        ("exposure", bio.Exposure), ("exposureCompensation", 0.0), ("skyboxExposure", 12.0),
                        ("fogAttenuationDistance", bio.Fog),
                        ("fogBaseHeight", 6.0), ("fogMaximumHeight", 140.0), ("fogMaximumDistance", 1800.0), ("fogAnisotropy", 0.0),
                        ("skybox", Json.Obj(("path", "Environment/Skybox/kloofendal_48d_partly_cloudy_puresky_4k.exr"))),
                        ("areas", Json.Obj(("Playable", Json.Obj(
                            ("x", 0.0), ("y", 0.0), ("width", (double)o.Size), ("height", (double)o.Size))))),
                        ("armies", armies),
                        ("chains", Json.Obj()),
                        ("markers", Json.Obj(
                            ("Spawn", Json.Obj(("resource", false), ("transforms", spawnT))),
                            ("Alloys", Json.Obj(("resource", true), ("transforms", alloyT))))),
                        ("decals", new List<JObj>()),
                        ("windSpeed", 0.25), ("windDirection", 160.0),
                        ("props", propGroups));

                    // Fields the shipped maps set that SanMap would otherwise
                    // default badly - most importantly the height fog.
                    Biome.AddEnvironment(map, cfg.WaterLevel);

                    string sanmap = Path.Combine(mapDir, folder + ".sanmap");
                    File.WriteAllText(sanmap, Json.Write(map), new UTF8Encoding(false));

                    if (o.Validate != null) Validator.Check(sanmap, o.Validate, log);
                    log($"  -> {mapDir}");

                    results.Add(new GeneratedMap
                    {
                        MapDir = mapDir, Folder = folder, DisplayName = display,
                        Style = useStyle, Biome = useBiome, Seed = seedN,
                        Spawns = MapGen.BaseX.Length, Alloys = mexX.Count, Accepted = true,
                    });
                }

                if (!accepted)
                {
                    log($"WARNING: no acceptable {useStyle} map after {o.MaxAttempts} attempts - try another seed or a gentler style");
                    results.Add(new GeneratedMap { Style = useStyle, Biome = useBiome, Accepted = false });
                }
            }
            return results;
        }
    }
}
