using System.Linq;
using System.Text;

namespace SanctuaryMapConverter.Core
{
    // New-RiverMap.ps1 in C#. Builds "Serpent Crossing" - a 1v1, 256 m,
    // tropical map.
    //
    //   * river runs corner to corner, top-left to bottom-right
    //   * bases sit back from the TL and BR corners on opposite banks
    //   * two causeways cross the river, one just outside each base
    //   * 180-degree rotational symmetry throughout
    //
    // Terrain, splatmaps and preview come from MapGen.cs; this method places
    // the markers against the finished heightfield, validates them, and writes
    // the .sanmap JSON.
    public static partial class NamedMaps
    {
        // The pscustomobject rows Test-Spot produced for the validation table.
        sealed class RiverSpot
        {
            public string Label;
            public double X, Z, Height, Slope, RiverDist;
            public bool OK;
        }

        // propExtension: prop blueprints ship under different extensions per
        // build: the engine's Environment.sanpack has 94 .santp, the map
        // editor's has 76 .sanprop, and the same maps are exported both ways.
        // Getting this wrong is not a soft failure - see the props section
        // below.
        public static string RiverMap(string mapsRoot, string propExtension = ".santp",
            Action<string> log = null, ValidateOptions validate = null, string debugOut = null,
            string folder = "Serpent_Crossing", string mapName = "Serpent Crossing")
        {
            log ??= _ => { };
            EngineState.Reset();   // fresh MapGen statics, like a fresh PS process

            // Serpent Crossing predates the style config and relies on MapGen's
            // defaults, including the hardcoded BridgeX/BridgeZ that were
            // authored for this map. Now that a river is opt-in it has to say
            // so, and vouch for those coordinates.
            MapGen.UseRiver = true;
            MapGen.BridgesPlaced = true;
            // Serpent Crossing is 256 m and relied on MapGen's field defaults,
            // so it never went through Configure and kept a 512 splat. The
            // splat has to be vertex aligned to the heightmap grid like every
            // shipped map.
            MapGen.Configure(256.0f, 0);

            string mapDir = Path.Combine(mapsRoot, folder);
            string texDir = Path.Combine(mapDir, "Textures");
            // The PS script threw here unless -Force was passed; the callers
            // always passed it, so this port always overwrites.
            if (Directory.Exists(mapDir)) Directory.Delete(mapDir, true);
            Directory.CreateDirectory(texDir);

            log("Building heightfield...");
            MapGen.BuildHeight();
            // Sand off invisible one-cell obstacles: they leave the map 100%
            // reachable but litter it with pinch points units path around.
            log(string.Format("  smoothed {0} isolated blocked patches", MapGen.SmoothPathingSpecks(60, 8)));
            log("Building stratum weights...");
            MapGen.BuildLayers();
            // Slot badges on the preview, in spawn order.
            MapGen.PreviewSpawnX = (float[])MapGen.BaseX.Clone();
            MapGen.PreviewSpawnZ = (float[])MapGen.BaseZ.Clone();

            // ------------------------------------------------------------- markers ---

            // Nine resource spots on the top-left player's bank; the
            // bottom-right set is the exact 180-degree rotation, so the map is
            // mirror-fair by construction.
            var alloySideA = new double[][]
            {
                new double[] { 86, 232 }, new double[] { 116, 234 }, new double[] { 88, 206 }, new double[] { 120, 202 },
                new double[] { 146, 238 }, new double[] { 176, 220 }, new double[] { 58, 238 }, new double[] { 196, 250 },
                new double[] { 156, 156 }          // contested, just north-east of the centre crossing
            };

            double M = 256.0;
            var spawns = new[]
            {
                (Army: "ARMY_1", X: MapGen.BaseX[0], Z: MapGen.BaseZ[0]),
                (Army: "ARMY_2", X: MapGen.BaseX[1], Z: MapGen.BaseZ[1]),
            };

            // A spot is good if it is dry, gentle, and clear of the channel.
            static bool TestOk(double x, double z, double maxSlope, double minRiver)
            {
                if (x < 8 || x > 248 || z < 8 || z > 248) return false;
                return MapGen.HeightAtWorld((float)x, (float)z) > MapGen.WaterLevel + 1.0
                    && MapGen.SlopeAtWorld((float)x, (float)z) <= maxSlope
                    && Math.Abs(MapGen.RiverDist((float)x, (float)z)) >= minRiver;
            }

            // If a hand-placed spot lands somewhere awkward, walk outward in
            // rings until a valid one turns up. Only the side-A point moves;
            // side B is its mirror, so the map stays symmetric whatever the
            // nudge does.
            double[] ResolveSpot(double x, double z, double maxSlope, double minRiver, string label)
            {
                if (TestOk(x, z, maxSlope, minRiver)) return new[] { x, z };
                foreach (int rad in new[] { 4, 8, 12, 16, 20, 26, 32 })
                {
                    for (int deg = 0; deg <= 23; deg++)
                    {
                        double a = deg * 15 * Math.PI / 180;
                        double nx = Math.Round(x + rad * Math.Cos(a));
                        double nz = Math.Round(z + rad * Math.Sin(a));
                        if (TestOk(nx, nz, maxSlope, minRiver))
                        {
                            log(string.Format("  nudged {0}: ({1},{2}) -> ({3},{4})  [{5} m]", label, x, z, nx, nz, rad));
                            return new[] { nx, nz };
                        }
                    }
                }
                log($"WARNING: could not place {label} near ({x},{z})");
                return new[] { x, z };
            }

            log("Placing markers...");
            var resolvedA = new List<double[]>();
            int i = 0;
            foreach (var p in alloySideA)
            {
                i++;
                resolvedA.Add(ResolveSpot(p[0], p[1], 12.0, 22.0, string.Format("alloy A{0}", i)));
            }

            var alloyPts = new List<double[]>();
            foreach (var p in resolvedA) alloyPts.Add(new[] { p[0], p[1] });
            foreach (var p in resolvedA)
            {
                double rx = M - p[0];
                double rz = M - p[1];
                alloyPts.Add(new[] { rx, rz });
            }

            // --------------------------------------------------------- validation ---

            static RiverSpot TestSpot(double x, double z, double maxSlope, string label)
            {
                float h = MapGen.HeightAtWorld((float)x, (float)z);
                float sl = MapGen.SlopeAtWorld((float)x, (float)z);
                float rd = Math.Abs(MapGen.RiverDist((float)x, (float)z));
                bool ok = h > MapGen.WaterLevel + 1.0 && sl <= maxSlope;
                return new RiverSpot
                {
                    Label = label, X = x, Z = z,
                    Height = Math.Round(h, 2), Slope = Math.Round(sl, 1),
                    RiverDist = Math.Round(rd, 1), OK = ok,
                };
            }

            // Format-Table -AutoSize | Out-String | Write-Host, near enough:
            // blank line, header, dash underline (dashes as long as the header
            // text), rows, blank line; strings left-aligned, numbers
            // right-aligned, one space between columns.
            void PrintTable(List<RiverSpot> rows)
            {
                string[] heads = { "Label", "X", "Z", "Height", "Slope", "RiverDist", "OK" };
                bool[] rightAlign = { false, true, true, true, true, true, false };
                var cells = new List<string[]>();
                foreach (var r in rows)
                    cells.Add(new[] { r.Label, r.X.ToString(), r.Z.ToString(), r.Height.ToString(),
                                      r.Slope.ToString(), r.RiverDist.ToString(), r.OK.ToString() });
                var widths = new int[heads.Length];
                for (int c = 0; c < heads.Length; c++)
                {
                    widths[c] = heads[c].Length;
                    foreach (var row in cells) widths[c] = Math.Max(widths[c], row[c].Length);
                }
                string Line(string[] row)
                {
                    var sb = new StringBuilder();
                    for (int c = 0; c < row.Length; c++)
                    {
                        if (c > 0) sb.Append(' ');
                        sb.Append(rightAlign[c] ? row[c].PadLeft(widths[c]) : row[c].PadRight(widths[c]));
                    }
                    return sb.ToString().TrimEnd();
                }
                var dashes = new string[heads.Length];
                for (int c = 0; c < heads.Length; c++) dashes[c] = new string('-', heads[c].Length);
                log("");
                log(Line(heads));
                log(Line(dashes));
                foreach (var row in cells) log(Line(row));
                log("");
            }

            var report = new List<RiverSpot>();
            foreach (var s in spawns) report.Add(TestSpot(s.X, s.Z, 6.0, $"spawn {s.Army}"));
            i = 0;
            foreach (var p in alloyPts) { i++; report.Add(TestSpot(p[0], p[1], 12.0, string.Format("alloy {0:D3}", i))); }

            PrintTable(report);
            var bad = report.Where(r => !r.OK).ToList();
            if (bad.Count > 0)
            {
                log($"WARNING: {bad.Count} marker(s) failed validation:");
                PrintTable(bad);
            }

            // Prove the causeways are the only dry crossings.
            log("Crossings:");
            for (int b = 0; b < 2; b++)
            {
                float[] prof = MapGen.CrossingProfile(b, 161);
                double min = prof.Min();
                int dry = prof.Count(v => v > MapGen.WaterLevel);
                string state = min > MapGen.WaterLevel ? "CONTINUOUS dry land" : "BROKEN - dips below water";
                log(string.Format("  bridge {0}: lowest point {1:N2} m (water {2:N1} m), {3}/161 samples dry -> {4}",
                    b + 1, min, MapGen.WaterLevel, dry, state));
            }
            float riverMax = MapGen.RiverMaxHeightBetweenBridges();
            string sealedState = riverMax < MapGen.WaterLevel ? "sealed" : "LEAKS - fordable somewhere";
            log(string.Format("  river elsewhere: highest bed point {0:N2} m -> {1}", riverMax, sealedState));

            // Pathability, using the game's own Land-layer rule (30 deg, 3x3 dilated).
            MapGen.BuildWalkable();
            bool[,] reach = MapGen.Reachable(MapGen.BaseX[0], MapGen.BaseZ[0]);
            int walk = MapGen.WalkableCount();
            int rc = MapGen.CountTrue(reach);
            log("Pathability (Land layer, maxSlope 30 deg):");
            log(string.Format("  walkable cells {0:N0}, reachable from ARMY_1 {1:N0} ({2:P0} of walkable)",
                walk, rc, (double)rc / walk));

            bool enemyOk = MapGen.IsReachable(reach, MapGen.BaseX[1], MapGen.BaseZ[1]);
            log(string.Format("  enemy spawn reachable overland: {0}",
                enemyOk ? "YES" : "NO - the bridges do not connect!"));

            var unreachable = new List<string>();
            i = 0;
            foreach (var p in alloyPts)
            {
                i++;
                if (!MapGen.IsReachable(reach, (float)p[0], (float)p[1]))
                    unreachable.Add(string.Format("Alloys_{0:D3} ({1},{2})", i, p[0], p[1]));
            }
            if (unreachable.Count > 0)
                log("WARNING: " + string.Format("  {0}/18 alloy spots are cut off: {1}",
                    unreachable.Count, string.Join(", ", unreachable)));
            else
                log("  all 18 alloy spots reachable on foot");

            for (int b = 0; b < 2; b++)
            {
                bool ok = MapGen.IsReachable(reach, MapGen.BridgeX[b], MapGen.BridgeZ[b]);
                log(string.Format("  bridge {0} deck reachable: {1}", b + 1, ok ? "YES" : "NO"));
            }
            log("");

            float[] ts = MapGen.TerrainStats();
            float land = ts[2];
            log(string.Format("Terrain: {0:N1} m .. {1:N2} m ({2:N1} m of relief)", ts[0], ts[1], (double)ts[1] - ts[0]));
            log(string.Format("  dry land slopes: {0:P0} flat (<6 deg), {1:P0} gentle (6-15), {2:P0} steep (15-34), {3:P0} cliff (>34)",
                (double)ts[3] / land, (double)ts[4] / land, (double)ts[5] / land, (double)ts[6] / land));
            log("");

            // ---------------------------------------------------------- textures ----

            log("Writing textures...");
            MapGen.WriteHeightmap(Path.Combine(texDir, "heightmap.raw"));
            MapGen.WriteStratums(texDir);
            MapGen.WriteTints(texDir, 2048);
            MapGen.WritePreview(Path.Combine(texDir, "preview.png"), 512, false, null, null, null);

            // (the annotated layout render happens after the props exist, further down)

            // -------------------------------------------------------------- json ----

            // Palette lifted from ~TEAM-1v1_Tropical_256_47940, with the three
            // textures the map-editor's Environment.sanpack lacks swapped for
            // present equivalents:
            //   sand05 -> sand02, rock_cliff02 -> rock_cliff01, gravel02 -> gravel01.
            // Layer 1 is rock_basalt01 for cliff faces. Every path here is
            // verified present in BOTH the engine and map-editor packs -
            // rock_cliff03 is editor-only.
            static JObj S(string tex, double tile, double far, double tri, double triFar,
                          double nrm, double nrmFar, double nb, double hb,
                          double[] diff, double[] farRemap, double[] mmin, double[] mmax)
            {
                string p = $"Environment/01_Highlands/Stratum/{tex}";
                return Json.Obj(
                    ("name", null),
                    ("albedo", Json.Obj(("path", p + "_albedo.tga"))),
                    ("normal", Json.Obj(("path", p + "_normal.tga"))),
                    ("mask", Json.Obj(("path", p + "_mask.tga"))),
                    ("tileSize", Json.Obj(("x", tile), ("y", tile))),
                    ("tileSizeFar", Json.Obj(("x", far), ("y", far))),
                    ("tileSizeTriplanar", tri),
                    ("tileSizeFarTriplanar", triFar),
                    ("normalScale", nrm),
                    ("normalScaleFar", nrmFar),
                    ("normalFarNearBlend", nb),
                    ("heightFarNearBlend", hb),
                    ("diffuseRemap", Json.Rgba(diff[0], diff[1], diff[2], diff[3])),
                    ("farColorRemap", Json.Rgba(farRemap[0], farRemap[1], farRemap[2], farRemap[3])),
                    ("maskRemapMin", Json.Quat(mmin[0], mmin[1], mmin[2], mmin[3])),
                    ("maskRemapMax", Json.Quat(mmax[0], mmax[1], mmax[2], mmax[3])));
            }

            double[] grass07D = { 0.309759647, 0.319, 0.268598, 1.0 };
            double[] grass07F = { 0.0159015749, 0.007912427, 0.004240413, 0.0 };

            var stratums = new List<JObj>
            {
                S("highlands_100m_grass07",      10, 64, 12, 36, 1.0, 1.22, 0.0,      0.5, grass07D, grass07F, new double[] { 0, 0, 0.1, 0 }, new double[] { 1, 1, 0.9, 1 }),
                S("highlands_60m_rock_basalt01", 10, 44, 10, 36, 1.0, 1.0,  0.2,      0.5, new double[] { 0.58, 0.556, 0.54, 1.0 },                    new double[] { 1, 1, 1, 0 }, new double[] { 0, 0, 0, 0 }, new double[] { 1, 1, 1, 1 }),
                S("highlands_100m_heather03",     8, 32, 12, 36, 1.0, 1.0,  0.5,      0.5, new double[] { 0.298039228, 0.286274523, 0.192156866, 1.0 }, new double[] { 0.0376494564, 0.03465263, 0.03465263, 0.0 }, new double[] { 0, 0, 0, 0 }, new double[] { 1, 1, 1, 1 }),
                S("highlands_100m_grass02",       8, 64, 12, 36, 1.0, 1.0,  0.319,    0.5, new double[] { 0.549019635, 0.58431375, 0.5411765, 1.0 },    new double[] { 0.235482112, 0.2004364, 0.148044586, 0.0 },  new double[] { 0, 0, 0, 0 }, new double[] { 1, 1, 1, 1 }),
                S("highlands_100m_grass03",       8, 32, 12, 36, 1.0, 1.0,  0.533783, 0.5, new double[] { 0.5280531, 0.615026534, 0.701999962, 1.0 },   new double[] { 0.06586576, 0.04347261, 0.0224204231, 0.0 }, new double[] { 0, 0, 0.1, 0 }, new double[] { 1, 1, 0.8, 1 }),
                S("highlands_100m_mud02",         8, 40, 12, 36, 1.0, 1.0,  0.56636,  0.5, new double[] { 0.5280531, 0.615026534, 0.701999962, 1.0 },   new double[] { 1, 1, 1, 0 }, new double[] { 0, 0, 0, 0 }, new double[] { 1, 1, 1, 1 }),
                S("highlands_100m_sand02",       10, 32,  8, 36, 0.8, 0.8,  0.062,    0.5, new double[] { 0.262999982, 0.204119369, 0.109910429, 1.0 }, new double[] { 0.250646025, 0.2133727, 0.138813585, 0.0 }, new double[] { 0, 0, 0, 0 }, new double[] { 1, 1, 1, 1 }),
                S("highlands_100m_rock_cliff01", 12, 52, 10, 36, 1.0, 1.0,  0.164,    0.5, new double[] { 0.6666667, 0.5372549, 0.5137255, 1.0 },       new double[] { 1, 1, 1, 0 }, new double[] { 0, 0, 0, 0 }, new double[] { 1, 1, 1, 1 }),
                S("highlands_60m_gravel01",      12, 52, 10, 36, 1.0, 1.0,  0.164,    0.5, new double[] { 0.6666667, 0.5372549, 0.5137255, 1.0 },       new double[] { 1, 1, 1, 0 }, new double[] { 0, 0, 0, 0 }, new double[] { 1, 1, 1, 1 }),
            };

            static JObj T(double x, double y, double z) => Json.Obj(
                ("position", Json.Vec3(x, y, z)),
                ("rotation", Json.Quat(0.0, 0.0, 0.0, 1.0)),
                ("scale", Json.Vec3(1.0, 1.0, 1.0)));

            var spawnT = new JObj();
            foreach (var s in spawns)
            {
                double y = Math.Round(MapGen.HeightAtWorld(s.X, s.Z), 2);
                spawnT.Add(s.Army, T(s.X, y, s.Z));
            }

            var alloyT = new JObj();
            i = 0;
            foreach (var p in alloyPts)
            {
                i++;
                double y = Math.Round(MapGen.HeightAtWorld((float)p[0], (float)p[1]), 2);
                alloyT.Add(string.Format("Alloys_{0:D3}", i), T(p[0], y, p[1]));

                // alloy_spot decals use the quaternion family (a, b, -b, a) with
                // a^2+b^2=0.5: a flat-to-ground rotation with a free spin about Y.
            }

            static JObj Army() => Json.Obj(
                ("faction", 0), ("alloys", 500.0), ("energy", 500.0), ("groups", Json.Obj()));

            // ------------------------------------------------------------- props ----
            // Every Highlands prop is tagged HARVESTABLE with harvest =
            // { alloys, plasma }, so these are early reclaim as well as
            // scenery. edb* have tall colliders (bushes/trees), edm* are flat
            // (ground rocks).
            //
            // The extension matters enormously. A blueprint the build can't
            // find is not a soft failure: Engine.GetFileContent returns an
            // empty chunk rather than nil, so mapUtils.lua's
            // `if propFileString then` guard passes, pcall "succeeds" with
            // propTemplateData = nil, and CreatePropPrefab(nil) throws on
            // tp.visuals. That aborts RunMapSetup at mapUtils.lua:92 - and the
            // alloy resource spots are created at line 113. One bad prop path
            // silently costs the map every single one of its alloy points,
            // with nothing on screen to say why.
            string[] treeBps = { "edbm0121", "edbm0122", "edbm0123", "edbm0124", "edbm0125" };
            string[] rockBps = { "edmm0104", "edmm0106", "edms0110" };

            float[] avoidX = alloyPts.Select(p => (float)p[0]).ToArray();
            float[] avoidZ = alloyPts.Select(p => (float)p[1]).ToArray();

            log($"Scattering props ({propExtension})...");
            var buckets = new Dictionary<string, List<JObj>>();
            foreach (var b in treeBps.Concat(rockBps)) buckets[b] = new List<JObj>();

            foreach (var (bps, rocks, count, scatterSeed) in new[]
            {
                (treeBps, false, 260, 8081),
                (rockBps, true, 70, 4409),
            })
            {
                float[] flat = MapGen.Scatter(scatterSeed, count, rocks, avoidX, avoidZ, 12.0f);
                int n = flat.Length / 5;
                for (int k = 0; k < n; k++)
                {
                    double x = flat[k * 5], y = flat[k * 5 + 1], z = flat[k * 5 + 2];
                    double yaw = flat[k * 5 + 3], sc = flat[k * 5 + 4];
                    string bp = bps[k % bps.Length];

                    // original, then its 180-degree mirror on the far bank
                    foreach (var (ix, iz, iyaw) in new[] { (x, z, yaw), (M - x, M - z, yaw + Math.PI) })
                    {
                        buckets[bp].Add(Json.Obj(
                            ("position", Json.Vec3(Math.Round(ix, 3), Math.Round(y, 3), Math.Round(iz, 3))),
                            ("rotation", Json.Quat(0.0,
                                Math.Round(Math.Sin(iyaw / 2), 7),
                                0.0,
                                Math.Round(Math.Cos(iyaw / 2), 7))),
                            ("scale", Json.Vec3(Math.Round(sc, 4), Math.Round(sc, 4), Math.Round(sc, 4)))));
                    }
                }
                log(string.Format("  {0}: {1} per bank -> {2} total", rocks ? "rocks" : "trees", n, n * 2));
            }

            var propGroups = new List<JObj>();
            foreach (var b in treeBps.Concat(rockBps))
            {
                if (buckets[b].Count == 0) continue;
                propGroups.Add(Json.Obj(
                    ("blueprintPath", $"Environment/01_Highlands/Props/{b}/{b}{propExtension}"),
                    ("transforms", buckets[b])));
            }
            log(string.Format("  {0} blueprint groups, {1} instances", propGroups.Count, buckets.Values.Sum(l => l.Count)));

            if (debugOut != null)
            {
                static object Get(JObj o, string key) => o.Items.First(kv => kv.Key == key).Value;
                var mx = new List<float>(); var mz = new List<float>(); var mk = new List<int>();
                foreach (var g in propGroups)
                    foreach (var t in (List<JObj>)Get(g, "transforms"))
                    {
                        var pos = (JObj)Get(t, "position");
                        mx.Add((float)(double)Get(pos, "x"));
                        mz.Add((float)(double)Get(pos, "z"));
                        mk.Add(2);
                    }
                foreach (var p in alloyPts) { mx.Add((float)p[0]); mz.Add((float)p[1]); mk.Add(1); }
                foreach (var s in spawns) { mx.Add(s.X); mz.Add(s.Z); mk.Add(0); }
                MapGen.WritePreview(debugOut, 768, true, mx.ToArray(), mz.ToArray(), mk.ToArray());
                log($"  layout render -> {debugOut}");
                string stem = Path.ChangeExtension(debugOut, null).TrimEnd('.');
                MapGen.WriteHeightPreview(stem + "_elevation.png", 768);
                log($"  elevation render -> {stem}_elevation.png");
                MapGen.WriteWalkPreview(stem + "_walk.png", 768, reach);
                log($"  walkability render -> {stem}_walk.png");
            }

            var map = Json.Obj(
                ("fileVersion", 3),
                ("mapVersion", 1),
                ("name", mapName),
                ("credits", ""),
                ("width", 256),
                ("length", 256),
                // SanMap.height is an int. Newtonsoft's ReadAsInt32 rejects
                // "128.0" outright and LoadJson dies before the first progress
                // tick, so the editor sits at 0%.
                ("height", 128),
                ("heightmapResolution", MapGen.HRes),
                // layerResolution is [JsonIgnore] on SanMap - it is always
                // recomputed as `width` on load, so writing it would be dead
                // data. The splat TGAs can be any square size; the loader takes
                // their dimensions from the file itself, and layerResolution
                // only matters for MapEditorTextures.ImportMask().
                ("hasWater", true),
                ("waterLevel", (double)MapGen.WaterLevel),
                ("waterDepth", 2.0),
                ("waterWindSpeed", 0.06),
                ("waterWindDirection", 100.0),
                ("waterShoreDepthOffset", 8.0),
                ("waterShoreDepthStrength", 0.7),
                ("waterShoreDistanceOffset", 0.0),
                ("waterShoreDistanceStrength", 2.0),
                ("shader", "RTS/TerrainLit"),
                ("heightTransition", 2.0),
                ("fadeDistance", 50.0),
                ("fadeStartDistance", 30.0),
                ("stratumLayers", stratums),
                ("sunRA", 96.2),
                ("sunDA", 30.0),
                ("sunIntensity", 60000.0),
                ("sunTint", Json.Rgba(1.0, 1.0, 1.0, 1.0)),
                ("sunTemperature", 9800.0),
                ("sunAngularDiameter", 0.5),
                ("sunVolumetricsMultiplier", 6.7),
                ("sunVolumetricsShadowDimer", 0.5),
                ("skylightIntensity", 0.0),
                ("skylightTint", Json.Rgba(1.0, 1.0, 1.0, 1.0)),
                ("skylightTemperature", 12000.0),
                ("exposure", 11.5),
                ("exposureCompensation", 0.0),
                ("skyboxExposure", 12.0),
                ("fogAttenuationDistance", 251.0),
                ("fogBaseHeight", 5.41),
                ("fogMaximumHeight", 132.5),
                ("fogMaximumDistance", 1500.0),
                ("fogAnisotropy", 0.0),
                ("skybox", Json.Obj(("path", "Environment/Skybox/kloofendal_48d_partly_cloudy_puresky_4k.exr"))),
                ("areas", Json.Obj(("Playable", Json.Obj(
                    ("x", 0.0), ("y", 0.0), ("width", 256.0), ("height", 256.0))))),
                ("armies", Json.Obj(("ARMY_1", Army()), ("ARMY_2", Army()))),
                ("chains", Json.Obj()),
                ("markers", Json.Obj(
                    ("Spawn", Json.Obj(("resource", false), ("transforms", spawnT))),
                    ("Alloys", Json.Obj(("resource", true), ("transforms", alloyT))))),
                ("decals", new List<JObj>()),
                ("windSpeed", 0.25),
                ("windDirection", 160.0),
                ("props", propGroups));

            // Fields the shipped maps set that SanMap would otherwise default
            // badly - most importantly the height fog. (New-MapEnvironment
            // 'Tropical' in the PS script; the lighting half lives in Biomes.cs.)
            Biome.AddEnvironment(map, (double)MapGen.WaterLevel);

            string sanmap = Path.Combine(mapDir, folder + ".sanmap");
            File.WriteAllText(sanmap, Json.Write(map), new UTF8Encoding(false));

            log("");
            foreach (var f in new DirectoryInfo(mapDir).EnumerateFiles("*", SearchOption.AllDirectories))
                log(string.Format("  {0,-22} {1,12:N0}", f.Name, f.Length));
            log("");

            // Replay the game's own deserialisation before claiming this is
            // loadable. (The PS script ran Test-Sanmap.ps1 -Path $sanmap
            // -CheckTextures here.)
            if (validate != null) Validator.Check(sanmap, validate, log);

            log("");
            log($"Open: {sanmap}");
            return mapDir;
        }
    }
}
