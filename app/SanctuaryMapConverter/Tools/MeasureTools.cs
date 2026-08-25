using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SanctuaryMapConverter.Core;

namespace SanctuaryMapConverter.Tools
{
    // The four Measure-*.ps1 corpus tools in C#:
    //
    //   SanTerrain - tools/Measure-SanTerrain.ps1: terrain structure of the
    //                deployed Sanctuary maps, read from the shipped bytes.
    //   Sanmaps    - tools/Measure-Sanmaps.ps1: spawn and alloy geometry of
    //                the deployed Sanctuary maps (JSON only, no heightmaps).
    //   ScTerrain  - tools/Measure-ScTerrain.ps1: terrain structure of a
    //                Supreme Commander map library, judged by Sanctuary's
    //                nav rule.
    //   ScCorpus   - tools/Measure-ScCorpus.ps1: marker statistics of a
    //                Supreme Commander map library (header-only reader).
    //
    // The scripts defaulted their roots to F:\ install paths; here every root
    // is a required parameter - the CLI dispatcher resolves the defaults via
    // GamePaths.
    public static class MeasureTools
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // PowerShell's [int] cast rounds (banker's) where C#'s truncates; the
        // scripts leaned on that for every percentile index and every
        // [int](...), so this is the cast each of those goes through.
        static int PsInt(double v) => (int)Math.Round(v, MidpointRounding.ToEven);

        // PS indexing past the end of an array yields $null, which -f renders
        // as an empty field. The percentile indexes genuinely run off the end
        // on tiny corpora ([int](1 * 0.9) is 1), so the ports must not throw
        // where the scripts printed a blank.
        static object At(List<double> s, int i) => i >= 0 && i < s.Count ? (object)s[i] : null;

        static string S(object v) => v == null ? "" : Convert.ToString(v, Inv);

        // ---- shared plumbing ------------------------------------------------

        // Get-ChildItem sorts by name; PS comparison is case-insensitive.
        static IEnumerable<string> SortedDirs(string root, string filter = "*") =>
            Directory.EnumerateDirectories(root, filter)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

        // Get-ChildItem -Filter x | Select-Object -First 1. quiet mirrors
        // -ErrorAction SilentlyContinue on an unreadable directory.
        static string FirstFile(string dir, string pattern, bool quiet)
        {
            try
            {
                return Directory.EnumerateFiles(dir, pattern)
                                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                                .FirstOrDefault();
            }
            catch when (quiet) { return null; }
        }

        static bool TryTransforms(JsonElement root, string marker, out JsonElement transforms)
        {
            transforms = default;
            return root.TryGetProperty("markers", out var m)
                && m.TryGetProperty(marker, out var g)
                && g.TryGetProperty("transforms", out transforms)
                && transforms.ValueKind == JsonValueKind.Object;
        }

        // Format-Table -AutoSize | Out-String | Write-Host, same shape as the
        // PrintTable in NamedMaps.River: blank line, header, dash underline as
        // long as the header text, rows, blank line; strings left-aligned,
        // numbers right-aligned, one space between columns.
        static void Table(Action<string> log, string[] heads, bool[] rightAlign, List<string[]> cells)
        {
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

        // Export-Csv -NoTypeInformation: every field quoted, embedded quotes
        // doubled; with no rows the file is created empty (PS only writes the
        // header once the first object arrives).
        static void WriteCsv(string path, string[] heads, List<string[]> cells)
        {
            var sb = new StringBuilder();
            if (cells.Count > 0)
            {
                static string Q(string v) => "\"" + v.Replace("\"", "\"\"") + "\"";
                sb.AppendLine(string.Join(",", heads.Select(Q)));
                foreach (var row in cells) sb.AppendLine(string.Join(",", row.Select(Q)));
            }
            File.WriteAllText(path, sb.ToString());
        }

        // The two spawns furthest apart: on a team map that is an actual
        // attack lane rather than the walk to an ally.
        static void FurthestPair(List<float> sx, List<float> sz, out int bi, out int bk)
        {
            bi = 0; bk = 1; double bd = -1.0;
            for (int i = 0; i < sx.Count; i++)
                for (int k = i + 1; k < sx.Count; k++)
                {
                    double dx = (double)sx[i] - sx[k], dz = (double)sz[i] - sz[k];
                    double d = Math.Sqrt(dx * dx + dz * dz);
                    if (d > bd) { bd = d; bi = i; bk = k; }
                }
        }

        // The terrain-composition and route metrics shared by SanTerrain and
        // ScTerrain. Reads MapGen's statics, so the heightmap and BaseX/BaseZ
        // must already be adopted and rs must be RouteStats of the lane.
        static TerrainRow TerrainRowFrom(string name, int size, int spawnCount, float[] rs)
        {
            float[] ts = MapGen.TerrainStats();
            float[] og = MapGen.OpenGroundStats(6f);
            float land = Math.Max(1f, ts[2]);

            return new TerrainRow
            {
                Name        = name,
                Size        = size,
                Spawns      = spawnCount,
                Water       = Math.Round((double)MapGen.WaterFraction(), 2),
                Flat        = Math.Round(ts[3] / (double)land, 2),
                Cliff       = Math.Round(ts[6] / (double)land, 2),
                Open        = Math.Round(og[0] / (double)Math.Max(1f, og[1]), 2),
                Plateau     = Math.Round((double)MapGen.PlateauFraction(6f), 2),
                LaneMedian  = Math.Round((double)rs[2], 1),
                LaneMin     = Math.Round((double)rs[3], 1),
                LaneMedFrac = Math.Round(rs[2] / (double)size, 3),
                Directness  = Math.Round((double)rs[1], 2),
                Chokes      = PsInt(rs[4]),
                ChokePer1k  = Math.Round(rs[4] / (rs[0] / 1000.0), 1),
                HighGround  = Math.Round((double)rs[5], 2),
            };
        }

        // The Stat function of Measure-SanTerrain / Measure-ScTerrain.
        static void StatP(Action<string> log, string name, IEnumerable<double> vals)
        {
            var s = vals.OrderBy(v => v).ToList();
            if (s.Count == 0) return;
            log(string.Format(Inv,
                "  {0,-34} p10 {1,7}  p25 {2,7}  median {3,7}  p75 {4,7}  p90 {5,7}", name,
                At(s, PsInt(s.Count * 0.10)), At(s, PsInt(s.Count * 0.25)), At(s, PsInt(s.Count * 0.5)),
                At(s, PsInt(s.Count * 0.75)), At(s, PsInt(s.Count * 0.90))));
        }

        // Identical summary block in both terrain scripts.
        static void TerrainSummary(List<TerrainRow> rows, Action<string> log)
        {
            log(string.Format(Inv, "{0} maps measured", rows.Count));
            log("");
            log("Terrain composition");
            StatP(log, "water fraction of map",        rows.Select(r => r.Water));
            StatP(log, "flat land (< 6 deg)",          rows.Select(r => r.Flat));
            StatP(log, "cliff land (> 34 deg)",        rows.Select(r => r.Cliff));
            StatP(log, "largest open area / land",     rows.Select(r => r.Open));
            StatP(log, "raised ground (> 6 m up)",     rows.Select(r => r.Plateau));
            log("");
            log("The lane between the two furthest spawns");
            StatP(log, "median clearance, m",          rows.Select(r => r.LaneMedian));
            StatP(log, "median clearance / map size",  rows.Select(r => r.LaneMedFrac));
            StatP(log, "narrowest point, m",           rows.Select(r => r.LaneMin));
            StatP(log, "directness (1 = straight)",    rows.Select(r => r.Directness));
            StatP(log, "chokepoints on the route",     rows.Select(r => (double)r.Chokes));
            StatP(log, "chokepoints per 1000 m",       rows.Select(r => r.ChokePer1k));
            StatP(log, "route overlooked by high grd", rows.Select(r => r.HighGround));
        }

        // ==== Measure-SanTerrain.ps1 =========================================
        // The terrain analysis of ScTerrain, run against deployed Sanctuary
        // maps instead of Supreme Commander ones. Same metrics, same nav rule,
        // read from the shipped bytes on disk.
        public static int SanTerrain(string mapsRoot, string filter, int maxSize,
            bool perMap, string csv, Action<string> log)
        {
            EngineState.Reset();
            var rows = new List<TerrainRow>();

            foreach (var dir in SortedDirs(mapsRoot, filter))
            {
                string name = Path.GetFileName(dir);
                string f = FirstFile(dir, "*.sanmap", quiet: false);
                string raw = Path.Combine(dir, "Textures", "heightmap.raw");
                if (f == null || !File.Exists(raw)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    var j = doc.RootElement;
                    double widthRaw = j.GetProperty("width").GetDouble();
                    int width = PsInt(widthRaw);
                    if (width > maxSize) continue;
                    float water = j.TryGetProperty("hasWater", out var hw) && hw.ValueKind == JsonValueKind.True
                        ? (float)j.GetProperty("waterLevel").GetDouble() : 0f;
                    MapGen.LoadHeightFromFile(raw, PsInt(j.GetProperty("heightmapResolution").GetDouble()),
                        (float)widthRaw, (float)j.GetProperty("height").GetDouble(), water);

                    var sx = new List<float>();
                    var sz = new List<float>();
                    if (TryTransforms(j, "Spawn", out var tr))
                        foreach (var p in tr.EnumerateObject())
                        {
                            var pos = p.Value.GetProperty("position");
                            sx.Add((float)pos.GetProperty("x").GetDouble());
                            sz.Add((float)pos.GetProperty("z").GetDouble());
                        }
                    if (sx.Count < 2) continue;
                    MapGen.BaseX = sx.ToArray(); MapGen.BaseZ = sz.ToArray();

                    FurthestPair(sx, sz, out int bi, out int bk);
                    float[] rs = MapGen.RouteStats(sx[bi], sz[bi], sx[bk], sz[bk]);
                    if (rs[0] <= 0) continue;

                    rows.Add(TerrainRowFrom(name, width, sx.Count, rs));
                }
                catch (Exception ex) { log(string.Format(Inv, "  skip {0}: {1}", name, ex.Message)); }
            }

            if (perMap)
                Table(log, TerrainRow.Heads, TerrainRow.Right,
                    rows.OrderBy(r => r.Size).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(r => r.Cells()).ToList());
            if (!string.IsNullOrEmpty(csv))
                WriteCsv(csv, TerrainRow.Heads, rows.Select(r => r.Cells()).ToList());

            TerrainSummary(rows, log);
            return 0;
        }

        // ==== Measure-Sanmaps.ps1 ============================================
        // Mine deployed Sanctuary maps for spawn and alloy geometry. The maps
        // that ship with the game are evidence; measure those and let them set
        // the generator's targets.
        public static int Sanmaps(string mapsRoot, string filter, bool perMap, Action<string> log)
        {
            var rows = new List<SanmapRow>();

            foreach (var dir in SortedDirs(mapsRoot, filter))
            {
                string f = FirstFile(dir, "*.sanmap", quiet: false);
                if (f == null) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var j = doc.RootElement;

                var spawns = new List<double[]>();
                if (TryTransforms(j, "Spawn", out var st))
                    foreach (var p in st.EnumerateObject())
                    {
                        var pos = p.Value.GetProperty("position");
                        spawns.Add(new[] { pos.GetProperty("x").GetDouble(), pos.GetProperty("z").GetDouble() });
                    }
                var alloys = new List<double[]>();
                if (TryTransforms(j, "Alloys", out var at))
                    foreach (var p in at.EnumerateObject())
                    {
                        var pos = p.Value.GetProperty("position");
                        alloys.Add(new[] { pos.GetProperty("x").GetDouble(), pos.GetProperty("z").GetDouble() });
                    }
                if (spawns.Count == 0) continue;

                // Alloys within each ring of each spawn, and the walk to the
                // nearest one.
                var near20 = new List<int>(); var near40 = new List<int>();
                var near80 = new List<int>(); var nearest = new List<double>();
                foreach (var s in spawns)
                {
                    int n20 = 0, n40 = 0, n80 = 0; double best = double.MaxValue;
                    foreach (var a in alloys)
                    {
                        double d = Math.Sqrt((a[0] - s[0]) * (a[0] - s[0]) + (a[1] - s[1]) * (a[1] - s[1]));
                        if (d < 20) n20++;
                        if (d < 40) n40++;
                        if (d < 80) n80++;
                        if (d < best) best = d;
                    }
                    near20.Add(n20); near40.Add(n40); near80.Add(n80);
                    nearest.Add(best);
                }

                var s40 = near40.OrderBy(v => v).ToList();
                rows.Add(new SanmapRow
                {
                    Name       = Path.GetFileName(dir),
                    Size       = PsInt(j.GetProperty("width").GetDouble()),
                    Spawns     = spawns.Count,
                    Alloys     = alloys.Count,
                    PerSpawn   = Math.Round((double)alloys.Count / spawns.Count, 1),
                    Min20      = near20.Min(),
                    Min40      = near40.Min(),
                    Min80      = near80.Min(),
                    Med40      = s40[PsInt(near40.Count / 2.0)],
                    NearestMax = Math.Round(nearest.Max(), 0),
                });
            }

            // The script's per-map table lists these columns explicitly (no
            // Med40) and keeps the scan order.
            if (perMap)
                Table(log, SanmapRow.Heads, SanmapRow.Right, rows.Select(r => r.Cells()).ToList());

            void Stat(string name, IEnumerable<double> vals)
            {
                var s = vals.OrderBy(v => v).ToList();
                if (s.Count == 0) return;
                log(string.Format(Inv,
                    "{0,-24} min {1,6}   p25 {2,6}   median {3,6}   p75 {4,6}   max {5,6}", name,
                    s[0], At(s, PsInt(s.Count * 0.25)), At(s, PsInt(s.Count * 0.5)),
                    At(s, PsInt(s.Count * 0.75)), s[s.Count - 1]));
            }

            log(string.Format(Inv, "{0} maps", rows.Count));
            Stat("alloys per spawn",       rows.Select(r => r.PerSpawn));
            Stat("worst spawn, r<20 m",    rows.Select(r => (double)r.Min20));
            Stat("worst spawn, r<40 m",    rows.Select(r => (double)r.Min40));
            Stat("worst spawn, r<80 m",    rows.Select(r => (double)r.Min80));
            Stat("furthest nearest alloy", rows.Select(r => r.NearestMax));
            return 0;
        }

        // ==== Measure-ScTerrain.ps1 ==========================================
        // Measure the terrain structure of a Supreme Commander map library,
        // judged by Sanctuary's Land nav rule (30 degrees, 3x3 dilation).
        // Slow: the heightmaps are decoded in full; sample takes every Nth map.
        public static int ScTerrain(string[] mapsRoot, int sample, int maxSize,
            bool perMap, string csv, Action<string> log)
        {
            EngineState.Reset();
            var rows = new List<TerrainRow>();
            int seen = 0;

            foreach (var root in mapsRoot)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in SortedDirs(root))
                {
                    string scmap = FirstFile(dir, "*.scmap", quiet: true);
                    string save = FirstFile(dir, "*_save.lua", quiet: true);
                    if (scmap == null || save == null) continue;
                    seen++;
                    if ((seen - 1) % sample != 0) continue;

                    try
                    {
                        var sc = MapGen.ReadScMap(scmap);
                        if (sc.Size > maxSize) continue;
                        var mk = MapGen.ReadScMarkers(save);
                        var spawns = mk.Where(m => Regex.IsMatch(m.Name, @"^ARMY_\d+$", RegexOptions.IgnoreCase)).ToList();
                        if (spawns.Count < 2) continue;

                        sc.RowZeroIsNorth = MapGen.ResolveScRowOrder(sc, mk, out _, out _);
                        MapGen.MaxHeight = 512f;
                        MapGen.AdoptScMap(sc, 1f);

                        var sx = new List<float>();
                        var sz = new List<float>();
                        foreach (var s in spawns)
                        {
                            sx.Add(s.X); sz.Add(MapGen.ScMarkerZ(sc, s.Z));
                        }
                        MapGen.BaseX = sx.ToArray(); MapGen.BaseZ = sz.ToArray();

                        FurthestPair(sx, sz, out int bi, out int bk);
                        float[] rs = MapGen.RouteStats(sx[bi], sz[bi], sx[bk], sz[bk]);
                        if (rs[0] <= 0) continue;        // no overland route: naval map

                        rows.Add(TerrainRowFrom(Path.GetFileName(dir), sc.Size, spawns.Count, rs));
                    }
                    catch { continue; }
                }
            }

            if (perMap)
                Table(log, TerrainRow.Heads, TerrainRow.Right,
                    rows.OrderBy(r => r.Size).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(r => r.Cells()).ToList());
            if (!string.IsNullOrEmpty(csv))
            {
                WriteCsv(csv, TerrainRow.Heads, rows.Select(r => r.Cells()).ToList());
                log(string.Format(Inv, "wrote {0}", csv));
            }

            TerrainSummary(rows, log);
            return 0;
        }

        // ==== Measure-ScCorpus.ps1 ===========================================
        // Mine a folder of Supreme Commander maps for map-design statistics:
        // resource density against map size and player count, mass clustering
        // around spawns, spawn separation. Header-only reader - marker layout
        // lives in _save.lua, so the heightmaps are skipped.
        public static int ScCorpus(string[] mapsRoot, bool perMap, Action<string> log)
        {
            var rows = new List<CorpusRow>();

            foreach (var root in mapsRoot)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in SortedDirs(root))
                {
                    string scmap = FirstFile(dir, "*.scmap", quiet: true);
                    string save = FirstFile(dir, "*_save.lua", quiet: true);
                    if (scmap == null || save == null) continue;

                    MapGen.ScMapInfo h;
                    try { h = MapGen.ReadScMapHeader(scmap); } catch { continue; }
                    var mk = MapGen.ReadScMarkers(save);

                    // PS -match and -eq are case-insensitive.
                    var spawns = mk.Where(m => Regex.IsMatch(m.Name, @"^ARMY_\d+$", RegexOptions.IgnoreCase)).ToList();
                    var mass = mk.Where(m => string.Equals(m.Type, "Mass", StringComparison.OrdinalIgnoreCase)).ToList();
                    var hydro = mk.Where(m => string.Equals(m.Type, "Hydrocarbon", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (spawns.Count < 2) continue;

                    // Mass in rings around each spawn. FA and Sanctuary both
                    // use metres, so these radii carry over directly.
                    var r16 = new List<int>(); var r24 = new List<int>();
                    var r48 = new List<int>(); var nearest = new List<double>();
                    foreach (var s in spawns)
                    {
                        int a = 0, b = 0, c = 0; double best = double.MaxValue;
                        foreach (var m in mass)
                        {
                            double dx = (double)m.X - s.X, dz = (double)m.Z - s.Z;
                            double d = Math.Sqrt(dx * dx + dz * dz);
                            if (d < 16) a++;
                            if (d < 24) b++;
                            if (d < 48) c++;
                            if (d < best) best = d;
                        }
                        r16.Add(a); r24.Add(b); r48.Add(c);
                        nearest.Add(best);
                    }

                    // Closest pair of spawns, as a fraction of map size.
                    double sep = double.MaxValue;
                    for (int i = 0; i < spawns.Count; i++)
                        for (int k = i + 1; k < spawns.Count; k++)
                        {
                            double dx = (double)spawns[i].X - spawns[k].X, dz = (double)spawns[i].Z - spawns[k].Z;
                            double d = Math.Sqrt(dx * dx + dz * dz);
                            if (d < sep) sep = d;
                        }

                    var s16 = r16.OrderBy(v => v).ToList();
                    var s24 = r24.OrderBy(v => v).ToList();
                    var s48 = r48.OrderBy(v => v).ToList();
                    rows.Add(new CorpusRow
                    {
                        Name       = Path.GetFileName(dir),
                        Size       = h.Size,
                        Spawns     = spawns.Count,
                        Mass       = mass.Count,
                        Hydro      = hydro.Count,
                        MassPer    = Math.Round((double)mass.Count / spawns.Count, 1),
                        // Density is the size-independent number: mass per
                        // player per square kilometre of map.
                        Density    = Math.Round((double)mass.Count / spawns.Count / ((h.Size / 1000.0) * (h.Size / 1000.0)), 1),
                        WorstR16   = r16.Min(),
                        MedR16     = s16[PsInt(r16.Count / 2.0)],
                        MedR24     = s24[PsInt(r24.Count / 2.0)],
                        MedR48     = s48[PsInt(r48.Count / 2.0)],
                        NearestMax = Math.Round(nearest.Max(), 0),
                        SepFrac    = Math.Round(sep / h.Size, 2),
                    });
                }
            }

            if (perMap)
                Table(log, CorpusRow.Heads, CorpusRow.Right,
                    rows.OrderBy(r => r.Size).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(r => r.Cells()).ToList());

            void Stat(string name, IEnumerable<double> vals)
            {
                var s = vals.OrderBy(v => v).ToList();
                if (s.Count == 0) return;
                log(string.Format(Inv,
                    "  {0,-30} min {1,7}  p10 {2,7}  median {3,7}  p90 {4,7}  max {5,7}", name,
                    s[0], At(s, PsInt(s.Count * 0.10)), At(s, PsInt(s.Count * 0.5)),
                    At(s, PsInt(s.Count * 0.90)), s[s.Count - 1]));
            }

            log(string.Format(Inv, "{0} maps with 2+ spawns", rows.Count));
            log("");
            log("Resources");
            Stat("mass per player",         rows.Select(r => r.MassPer));
            Stat("mass per player per km2", rows.Select(r => r.Density));
            Stat("hydrocarbon per map",     rows.Select(r => (double)r.Hydro));
            log("");
            log("Mass around the spawn (worst and median spawn on each map)");
            Stat("worst spawn, r < 16 m",   rows.Select(r => (double)r.WorstR16));
            Stat("median spawn, r < 16 m",  rows.Select(r => (double)r.MedR16));
            Stat("median spawn, r < 24 m",  rows.Select(r => (double)r.MedR24));
            Stat("median spawn, r < 48 m",  rows.Select(r => (double)r.MedR48));
            Stat("furthest nearest mass",   rows.Select(r => r.NearestMax));
            log("");
            log("Layout");
            Stat("closest spawn pair / size", rows.Select(r => r.SepFrac));
            log("");
            log("Mass per player by map size");
            foreach (var g in rows.GroupBy(r => r.Size).OrderBy(g => g.Key))
            {
                var m = g.Select(r => r.MassPer).OrderBy(v => v).ToList();
                log(string.Format(Inv, "  {0,5} m  n={1,4}   median {2,5}   spawns {3}",
                    g.Key, g.Count(), m[PsInt(m.Count / 2.0)],
                    string.Join(",", g.Select(r => r.Spawns).Distinct().OrderBy(v => v))));
            }
            return 0;
        }

        // ---- row shapes (property order = script column order) --------------

        sealed class TerrainRow
        {
            public string Name;
            public int Size, Spawns, Chokes;
            public double Water, Flat, Cliff, Open, Plateau,
                          LaneMedian, LaneMin, LaneMedFrac, Directness, ChokePer1k, HighGround;

            public static readonly string[] Heads =
            {
                "Name", "Size", "Spawns", "Water", "Flat", "Cliff", "Open", "Plateau",
                "LaneMedian", "LaneMin", "LaneMedFrac", "Directness", "Chokes", "ChokePer1k", "HighGround",
            };
            public static readonly bool[] Right =
            {
                false, true, true, true, true, true, true, true,
                true, true, true, true, true, true, true,
            };
            public string[] Cells() => new[]
            {
                Name, S(Size), S(Spawns), S(Water), S(Flat), S(Cliff), S(Open), S(Plateau),
                S(LaneMedian), S(LaneMin), S(LaneMedFrac), S(Directness), S(Chokes), S(ChokePer1k), S(HighGround),
            };
        }

        sealed class SanmapRow
        {
            public string Name;
            public int Size, Spawns, Alloys, Min20, Min40, Min80, Med40;
            public double PerSpawn, NearestMax;

            public static readonly string[] Heads =
                { "Name", "Size", "Spawns", "Alloys", "PerSpawn", "Min20", "Min40", "Min80", "NearestMax" };
            public static readonly bool[] Right =
                { false, true, true, true, true, true, true, true, true };
            public string[] Cells() => new[]
                { Name, S(Size), S(Spawns), S(Alloys), S(PerSpawn), S(Min20), S(Min40), S(Min80), S(NearestMax) };
        }

        sealed class CorpusRow
        {
            public string Name;
            public int Size, Spawns, Mass, Hydro, WorstR16, MedR16, MedR24, MedR48;
            public double MassPer, Density, NearestMax, SepFrac;

            public static readonly string[] Heads =
            {
                "Name", "Size", "Spawns", "Mass", "Hydro", "MassPer", "Density",
                "WorstR16", "MedR16", "MedR24", "MedR48", "NearestMax", "SepFrac",
            };
            public static readonly bool[] Right =
            {
                false, true, true, true, true, true, true,
                true, true, true, true, true, true,
            };
            public string[] Cells() => new[]
            {
                Name, S(Size), S(Spawns), S(Mass), S(Hydro), S(MassPer), S(Density),
                S(WorstR16), S(MedR16), S(MedR24), S(MedR48), S(NearestMax), S(SepFrac),
            };
        }
    }
}
