using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using SanctuaryMapConverter.Core;

namespace SanctuaryMapConverter.Tools
{
    // The five validation/QA scripts from tools/ in C#:
    //
    //   Environment        <- Test-Environment.ps1
    //   SplatAlignment     <- Test-SplatAlignment.ps1
    //   ScMapCheck         <- Test-ScMap.ps1
    //   BiomeTextures      <- Test-BiomeTextures.ps1
    //   CompareMapTextures <- Compare-MapTextures.ps1
    //
    // Parameters mirror each script's param() block; a null (or null
    // nullable) gets the script's default, except the paths that defaulted
    // to F:\ installs - those are required and the CLI dispatcher resolves
    // them via GamePaths. Return values follow the scripts' exit codes.
    public static class CheckTools
    {
        // PowerShell's [int] cast rounds (banker's) where C#'s truncates; the
        // originals leaned on that, so every ported [int](...) goes through
        // this (same as RandomMap.PsInt).
        static int PsInt(double v) => (int)Math.Round(v, MidpointRounding.ToEven);

        // Split-Path -Leaf: tolerant of a trailing separator, unlike
        // Path.GetFileName.
        static string Leaf(string p) => Path.GetFileName(p.TrimEnd('\\', '/'));

        // ------------------------------------------------------------------
        // Test-Environment.ps1: compare a map's lighting/atmosphere fields
        // against the range the shipped maps use. A warning, not a failure -
        // always returns 0 (the script had no exit statement); throws, like
        // the script's `throw`, when no reference map can be read.
        // ------------------------------------------------------------------
        public static int Environment(string[] path, string mapsRoot,
            string[] reference, double? tolerance, Action<string> log)
        {
            path ??= Array.Empty<string>();
            reference ??= new[] { "The_Forge", "White_Desert", "There_Is_Time", "Two_Step_Shuffle" };
            double tol = tolerance ?? 0.25;

            // PS hashtables and -in are case-insensitive for strings.
            var refVals = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in reference)
            {
                string f = Path.Combine(mapsRoot, n, n + ".sanmap");
                if (!File.Exists(f)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    // The script kept only numeric scalars ([double]/[int]/[long]).
                    if (p.Value.ValueKind != JsonValueKind.Number) continue;
                    if (!refVals.TryGetValue(p.Name, out var list))
                        refVals[p.Name] = list = new List<double>();
                    list.Add(p.Value.GetDouble());
                }
            }
            if (refVals.Count == 0) throw new Exception($"no reference maps found under '{mapsRoot}'");
            log(string.Format("Reference range from {0} shipped maps, {1} numeric fields",
                reference.Length, refVals.Count));

            // Size and terrain fields legitimately differ per map; they are
            // not style.
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "width", "length", "height", "heightmapResolution", "mapVersion",
                "waterLevel", "waterDepth", "seed", "maxPlayers",
            };

            int flagged = 0;
            foreach (var p in path)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(p));
                var issues = new List<string>();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    string k = prop.Name;
                    if (skip.Contains(k) || !refVals.ContainsKey(k)) continue;
                    if (prop.Value.ValueKind != JsonValueKind.Number) continue;
                    double v = prop.Value.GetDouble();

                    double lo = refVals[k].Min(), hi = refVals[k].Max();
                    double span = hi - lo;
                    // A field every shipped map agrees on has no range to be
                    // tolerant about, so any deviation counts.
                    double pad = span > 0 ? span * tol : 0;
                    if (v >= lo - pad && v <= hi + pad) continue;

                    issues.Add(string.Format("{0,-30} {1,12:n2}   shipped {2:n2} .. {3:n2}", k, v, lo, hi));
                }
                string name = Path.GetFileName(p);
                if (issues.Count > 0)
                {
                    flagged++;
                    log(string.Format("WARN  {0}", name));
                    foreach (var i in issues) log("      " + i);
                }
                else log(string.Format("ok    {0}", name));
            }

            log("");
            if (flagged > 0) log(string.Format("{0} map(s) have environment values outside the shipped range", flagged));
            else log("every map sits inside the shipped range");
            return 0;
        }

        // ------------------------------------------------------------------
        // Test-SplatAlignment.ps1: correlate one splat layer's weights
        // against slope over a range of row offsets, both row orders, to see
        // whether the splat is registered to the terrain. Diagnostic output
        // only; returns 0.
        // ------------------------------------------------------------------
        public static int SplatAlignment(string mapDir, int? layer, int? maxShift, Action<string> log)
        {
            int lay = layer ?? 7;          // rock: the layer most tightly tied to slope
            int shift = maxShift ?? 6;

            EngineState.Reset();   // fresh MapGen statics, like a fresh PS process

            string f = Directory.EnumerateFiles(mapDir, "*.sanmap").OrderBy(x => x).FirstOrDefault()
                ?? throw new FileNotFoundException($"no .sanmap in '{mapDir}'");
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            var j = doc.RootElement;
            string tex = Path.Combine(mapDir, "Textures");
            bool hasWater = j.TryGetProperty("hasWater", out var hw) && hw.ValueKind == JsonValueKind.True;
            float water = hasWater ? (float)j.GetProperty("waterLevel").GetDouble() : 0.0f;
            double width = j.GetProperty("width").GetDouble();
            MapGen.LoadHeightFromFile(Path.Combine(tex, "heightmap.raw"),
                PsInt(j.GetProperty("heightmapResolution").GetDouble()),
                (float)width, (float)j.GetProperty("height").GetDouble(), water);

            string file = lay <= 4 ? "stratums_1_4.tga" : "stratums_5_8.tga";
            // BGRA channel that carries each layer's weight, indexed by layer 1..8.
            int off = new[] { 2, 1, 0, 3, 2, 1, 0, 3 }[lay - 1];
            byte[] b = File.ReadAllBytes(Path.Combine(tex, file));
            int sres = b[12] | (b[13] << 8);
            byte descriptor = b[17];

            log(Leaf(mapDir));
            log(string.Format("  splat {0}x{0}, TGA descriptor 0x{1:x2} -> origin {2}", sres, descriptor,
                (descriptor & 0x20) != 0 ? "top-left" : "bottom-left (rows run upward)"));
            log(string.Format("  correlating layer {0} against slope", lay));
            log("");

            double Corr(int dr, bool flip)
            {
                int n = 0; double sx = 0.0, sy = 0.0, sxx = 0.0, syy = 0.0, sxy = 0.0;
                double step = width / sres;
                for (int r = 8; r < sres - 8; r += 3)
                {
                    for (int c = 8; c < sres - 8; c += 3)
                    {
                        int sr = flip ? sres - 1 - r : r;
                        sr += dr;
                        if (sr < 0 || sr >= sres) continue;
                        int v = b[18 + ((sr * sres) + c) * 4 + off];
                        double x = (c + 0.5) * step;
                        double z = width - (r + 0.5) * step;
                        // PS promotes float arithmetic to double; keep the
                        // accumulation in double the same way.
                        double sl = MapGen.SlopeAtWorld((float)x, (float)z);
                        n++; sx += v; sy += sl; sxx += v * v; syy += sl * sl; sxy += v * sl;
                    }
                }
                if (n < 2) return 0.0;
                double num = n * sxy - sx * sy;
                double den = Math.Sqrt((n * sxx - sx * sx) * (n * syy - sy * sy));
                if (den == 0) return 0.0;
                return num / den;
            }

            log("  row offset   as written   rows flipped");
            double bestC = 0.0; int bestDr = 0; bool bestF = false, haveBest = false;
            for (int dr = -shift; dr <= shift; dr++)
            {
                double a = Corr(dr, false), c = Corr(dr, true);
                log(string.Format("   {0,4}         {1,8:N4}      {2,8:N4}", dr, a, c));
                if (!haveBest || a > bestC) { haveBest = true; bestC = a; bestDr = dr; bestF = false; }
                if (c > bestC) { bestC = c; bestDr = dr; bestF = true; }
            }
            log("");
            log(string.Format("  best {0:N4} at row offset {1}, rows {2}", bestC, bestDr,
                bestF ? "FLIPPED" : "as written"));
            return 0;
        }

        // ------------------------------------------------------------------
        // Test-ScMap.ps1: parse every stock .scmap and report what the reader
        // found; every map parsing cleanly is the evidence that the
        // variable-length walk is right. Returns 1 if any map failed to
        // parse.
        // ------------------------------------------------------------------
        public static int ScMapCheck(string mapsRoot, string filter, Action<string> log)
        {
            filter ??= "*";
            int ok = 0, bad = 0;
            foreach (var dir in Directory.EnumerateDirectories(mapsRoot, filter)
                         .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(dir);
                string scmap = Directory.EnumerateFiles(dir, "*.scmap").OrderBy(x => x).FirstOrDefault();
                if (scmap == null) continue;
                string save = Directory.EnumerateFiles(dir, "*_save.lua").OrderBy(x => x).FirstOrDefault();

                try
                {
                    var m = MapGen.ReadScMap(scmap);
                    var markers = save != null ? MapGen.ReadScMarkers(save) : new List<MapGen.ScMarker>();

                    // PS -eq and -match are case-insensitive on strings.
                    int mass = markers.Count(k => string.Equals(k.Type, "Mass", StringComparison.OrdinalIgnoreCase));
                    int hydro = markers.Count(k => string.Equals(k.Type, "Hydrocarbon", StringComparison.OrdinalIgnoreCase));
                    int spawn = markers.Count(k => Regex.IsMatch(k.Name, @"^ARMY_\d+$", RegexOptions.IgnoreCase));

                    bool north = MapGen.ResolveScRowOrder(m, markers, out float eN, out float eS);

                    // Peak terrain, to see whether 1/128 really is the scale
                    // everywhere.
                    int peak = 0;
                    for (int y = 0; y <= m.Size; y += 8)
                        for (int x = 0; x <= m.Size; x += 8)
                            if (m.Raw[y, x] > peak) peak = m.Raw[y, x];

                    log(string.Format(
                        "{0,-28} {1,5}  hs 1/{2,-5:N0}  water {3,-6}  peak {4,6:N1} m   mass {5,3}  hydro {6,2}  spawn {7,2}   rows {8} (dN {9:N2} dS {10:N2})",
                        name, m.Size, 1.0 / m.HeightScale,
                        m.HasWater ? string.Format("{0:N1}", m.WaterElevation) : "none",
                        peak * (double)m.HeightScale, mass, hydro, spawn,
                        north ? "N" : "S", eN, eS));
                    ok++;
                }
                catch (Exception e)
                {
                    log(string.Format("{0,-28} FAILED: {1}", name, e.Message));
                    bad++;
                }
            }
            log("");
            log(string.Format("{0} parsed, {1} failed", ok, bad));
            return bad > 0 ? 1 : 0;
        }

        // ------------------------------------------------------------------
        // Test-BiomeTextures.ps1: check that every texture a biome table
        // names has all three variants (_albedo, _normal, _mask) in
        // Environment.sanpack. Validates the tables in Core.Biomes rather
        // than a separate copy. Returns 1 if any biome fails to resolve.
        // ------------------------------------------------------------------
        public static int BiomeTextures(string sanpack, string[] biomes, Action<string> log)
        {
            biomes ??= new[] { "Highlands", "Tropical", "Winter", "Evergreen", "Arid" };

            // PS hashtable keys are case-insensitive.
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var zip = System.IO.Compression.ZipFile.OpenRead(sanpack))
                foreach (var e in zip.Entries)
                    have.Add(Regex.Replace(e.FullName, @"\.[^.]+$", ""));

            int bad = 0;
            foreach (var bName in biomes)
            {
                var layers = Biome.Get(bName).Layers;
                var issues = new List<string>();
                for (int i = 0; i < layers.Length; i++)
                {
                    string p = ResolveLayerPath(layers[i]);
                    var missing = new List<string>();
                    foreach (var v in new[] { "_albedo", "_normal", "_mask" })
                        if (!have.Contains(p + v)) missing.Add(v);
                    if (missing.Count > 0)
                        issues.Add(string.Format("slot {0} {1} has no {2}", i, layers[i], string.Join(", ", missing)));
                }
                if (issues.Count > 0)
                {
                    bad++;
                    log(string.Format("FAIL {0}", bName));
                    foreach (var i in issues) log("       " + i);
                }
                else log(string.Format("ok   {0}", bName));
            }

            log("");
            if (bad > 0)
            {
                log($"{bad} biome(s) reference textures that do not exist");
                return 1;
            }
            log("every biome resolves");
            return 0;
        }

        // Same rule as Biome.ResolveLayerPath (private there): "Winter/rock"
        // resolves into the Winter stratum set, a bare name into 01_Highlands.
        static string ResolveLayerPath(string t)
        {
            int slash = t.IndexOf('/');
            if (slash > 0) return "Environment/" + t.Substring(0, slash) + "/Stratum/" + t.Substring(slash + 1);
            return "Environment/01_Highlands/Stratum/" + t;
        }

        // ------------------------------------------------------------------
        // Compare-MapTextures.ps1: render two deployed maps' stratum layers
        // side by side at the same world scale with each side's own
        // diffuseRemap applied, so what lands in the image is what the shader
        // is told to draw. Returns 0 once the PNG is written.
        // ------------------------------------------------------------------
        public static int CompareMapTextures(string mapsRoot, string mapA, string mapB,
            int? layers, string outPath, Action<string> log)
        {
            mapA ??= "~SC-Canis_River";
            mapB ??= "~SC-Canis_CC0";
            int nLayers = layers ?? 6;
            outPath ??= Path.Combine(Path.GetTempPath(), "map-tex-compare.png");

            using var docA = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapsRoot, mapA, mapA + ".sanmap")));
            using var docB = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapsRoot, mapB, mapB + ".sanmap")));
            var fa = docA.RootElement.GetProperty("stratumLayers");
            var cc = docB.RootElement.GetProperty("stratumLayers");

            const int sw = 190, pad = 10;
            int rowH = sw + 44;
            using var bmp = new Bitmap(2 * sw + 3 * pad + 240, nLayers * rowH + 40);
            using (var g = Graphics.FromImage(bmp))
            using (var font = new Font("Consolas", 10))
            using (var fontB = new Font("Consolas", 11, FontStyle.Bold))
            {
                g.Clear(Color.FromArgb(16, 20, 24));
                Brush white = Brushes.White, grey = Brushes.Silver;
                g.DrawString(mapA, fontB, white, pad + 40, 8);
                g.DrawString(mapB, fontB, white, sw + 2 * pad + 30, 8);
                g.DrawString("8 m x 8 m of ground each", font, grey, 2 * sw + 3 * pad + 4, 8);

                int y0 = 36;
                for (int i = 0; i < nLayers; i++)
                {
                    var faL = fa[i]; var ccL = cc[i];
                    string faPath = faL.GetProperty("albedo").GetProperty("path").GetString();
                    string ccPath = ccL.GetProperty("albedo").GetProperty("path").GetString();
                    var faT = LoadTex(Path.Combine(mapsRoot, mapA), faPath);
                    var ccT = LoadTex(Path.Combine(mapsRoot, mapB), ccPath);
                    double[] faRemap = ReadRemap(faL);
                    double[] ccRemap = ReadRemap(ccL);
                    double faTile = faL.GetProperty("tileSize").GetProperty("x").GetDouble();
                    double ccTile = ccL.GetProperty("tileSize").GetProperty("x").GetDouble();

                    if (faT != null) DrawSwatch(g, faT.Value, faTile, faRemap, pad, y0, sw, 8.0);
                    if (ccT != null) DrawSwatch(g, ccT.Value, ccTile, ccRemap, sw + 2 * pad, y0, sw, 8.0);

                    int tx = 2 * sw + 3 * pad;
                    g.DrawString(string.Format("L{0}", i), fontB, white, tx, y0);
                    g.DrawString(Path.GetFileName(faPath), font, grey, tx, y0 + 20);
                    g.DrawString(string.Format("  tile {0}m  remap {1:n2}/{2:n2}/{3:n2}",
                        faTile, faRemap[0], faRemap[1], faRemap[2]), font, grey, tx, y0 + 36);
                    g.DrawString(Path.GetFileName(ccPath), font, grey, tx, y0 + 58);
                    g.DrawString(string.Format("  tile {0}m  remap {1:n2}/{2:n2}/{3:n2}",
                        ccTile, ccRemap[0], ccRemap[1], ccRemap[2]), font, grey, tx, y0 + 74);
                    y0 += rowH;
                }
            }
            bmp.Save(outPath, ImageFormat.Png);
            log($"wrote {outPath}");
            return 0;
        }

        static double[] ReadRemap(JsonElement layer)
        {
            var r = layer.GetProperty("diffuseRemap");
            return new[]
            {
                r.GetProperty("r").GetDouble(),
                r.GetProperty("g").GetDouble(),
                r.GetProperty("b").GetDouble(),
            };
        }

        static (byte[] Px, int W, int H)? LoadTex(string mapDir, string path)
        {
            // -replace is case-insensitive in PowerShell.
            string f = Path.Combine(mapDir, Regex.Replace(path, "^map/", "", RegexOptions.IgnoreCase));
            if (!File.Exists(f)) return null;
            byte[] b = File.ReadAllBytes(f);
            var px = MapGen.DecodeDdsToBgra(b, out int w, out int h);
            if (px == null) return null;
            return (px, w, h);
        }

        // One swatch: `metres` of world drawn into `size` pixels, remap
        // applied, a fixed display gain so the remap products are visible.
        // Same gain both sides.
        static void DrawSwatch(Graphics g, (byte[] Px, int W, int H) tex, double tile,
            double[] remap, int ox, int oy, int size, double metres)
        {
            const double gain = 2.6;
            using var bmp2 = new Bitmap(size, size);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double u = (x / (double)size) * metres / tile;
                    double v = (y / (double)size) * metres / tile;
                    int tx = PsInt((u - Math.Floor(u)) * tex.W) % tex.W;
                    int ty = PsInt((v - Math.Floor(v)) * tex.H) % tex.H;
                    int o = (ty * tex.W + tx) * 4;
                    double bb = Math.Min(255.0, tex.Px[o] * remap[2] * gain);
                    double gg = Math.Min(255.0, tex.Px[o + 1] * remap[1] * gain);
                    double rr = Math.Min(255.0, tex.Px[o + 2] * remap[0] * gain);
                    bmp2.SetPixel(x, y, Color.FromArgb(PsInt(rr), PsInt(gg), PsInt(bb)));
                }
            }
            g.DrawImage(bmp2, ox, oy);
        }
    }
}
