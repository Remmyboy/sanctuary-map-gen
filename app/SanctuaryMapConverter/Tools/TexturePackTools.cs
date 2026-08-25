using System.Globalization;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace SanctuaryMapConverter.Tools
{
    // Ports of the three texture-pack build tools:
    //   tools/Measure-ScTextures.ps1 -> MeasureScTextures
    //   tools/Match-Textures.ps1     -> MatchTextures
    //   tools/Build-TexturePack.ps1  -> BuildTexturePack
    //
    // These regenerate the redistributable CC0 data the converter's CC0 mode
    // consumes (texturepack/ + manifest.csv + docs/texture-map.csv), so the
    // port is bit-faithful: the same inputs must produce the same CSVs and
    // the same DDS bytes as the scripts. CSV output matches PowerShell 7's
    // Export-Csv -NoTypeInformation -Encoding UTF8 exactly - every field
    // quoted, CRLF line endings, UTF-8 without BOM, trailing newline - which
    // is the format SubstitutionTable.Load already parses.
    //
    // The hardcoded F:\ paths from the scripts' param blocks are parameters
    // here; the CLI dispatcher resolves defaults (GamePaths for the FA
    // install, repo/data layout for docs and texturepack).
    public static class TexturePackTools
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // PowerShell's [int] cast rounds (banker's) where C#'s truncates; the
        // scripts leaned on that in the sampling grids, so this is the cast
        // every ported [int](...) goes through.
        static int PsInt(double v) => (int)Math.Round(v, MidpointRounding.ToEven);

        static string F(string fmt, params object[] args) => string.Format(Inv, fmt, args);

        // ---- Measure-ScTextures --------------------------------------------

        // First match wins, so the specific materials come before the generic
        // ones: "sandstone" is rock, and "lavarock" is lava.
        static readonly (string Role, Regex Rx)[] RoleRules =
        {
            ("snow",    new Regex("snow|ice|frost|glacier|melt", RegexOptions.IgnoreCase)),
            ("lava",    new Regex("lava|magma|molten|ribbon|wiers", RegexOptions.IgnoreCase)),
            ("crystal", new Regex("crystal|cryst|^cr_|^cru_", RegexOptions.IgnoreCase)),
            ("grass",   new Regex("grass|moss|turf|heather|foliage|sphagnum|creeper|hostas|fern|jungle", RegexOptions.IgnoreCase)),
            ("gravel",  new Regex("gravel|pebble|shingle|scree|gravil", RegexOptions.IgnoreCase)),
            ("sand",    new Regex("sand|dune|beach", RegexOptions.IgnoreCase)),
            ("crack",   new Regex("crack|barren|waste|dry", RegexOptions.IgnoreCase)),
            ("rock",    new Regex("rock|stone|cliff|boulder|slate|granit|ash|coral|reef|masonry", RegexOptions.IgnoreCase)),
            ("dirt",    new Regex("dirt|soil|mud|earth|ground|clay|dust|silt", RegexOptions.IgnoreCase)),
        };

        static string GetRole(string leaf)
        {
            foreach (var r in RoleRules) if (r.Rx.IsMatch(leaf)) return r.Role;
            return "other";
        }

        sealed class MeasureRow
        {
            public string Stem, Role, Family;
            public int Maps;
            public double Luma, Std, StdC, R, G, B;
            public string Format, Size, Path;
        }

        /// Measure every Supreme Commander stratum texture the map corpus
        /// actually uses, and classify it by material role. `csv` is optional:
        /// null or empty skips the CSV, matching the script's -Csv parameter.
        public static int MeasureScTextures(string[] mapsRoot, string scdPath, string csv, Action<string> log)
        {
            log ??= _ => { };
            try
            {
                log("Scanning the corpus for referenced textures...");
                var counts = new Dictionary<string, int>();
                var order = new List<string>();            // first-seen order, the tie-break
                int scanned = 0;
                foreach (var root in mapsRoot ?? Array.Empty<string>())
                {
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                    var files = new List<string>();
                    FindScmaps(root, files);
                    foreach (var f in files)
                    {
                        try
                        {
                            byte[] b = File.ReadAllBytes(f);
                            var scInfo = MapGen.ReadScMap(f, true);
                            var set = MapGen.ScanScTextures(b, scInfo.Size);
                            if (set == null) continue;
                            scanned++;
                            foreach (var p in set.Paths
                                .Where(s => !string.IsNullOrEmpty(s))
                                .Select(s => s.ToLowerInvariant())
                                .Distinct()
                                .OrderBy(s => s, StringComparer.Ordinal))
                            {
                                if (counts.TryGetValue(p, out int n)) counts[p] = n + 1;
                                else { counts[p] = 1; order.Add(p); }
                            }
                        }
                        catch { }
                    }
                }
                log(F("  {0} maps readable, {1} distinct textures referenced", scanned, counts.Count));

                log("Measuring...");
                var rows = new List<MeasureRow>();
                var failed = new List<string>();
                using (var zip = ZipFile.OpenRead(scdPath))
                {
                    var index = new Dictionary<string, ZipArchiveEntry>();
                    foreach (var e in zip.Entries) index[e.FullName.ToLowerInvariant().TrimStart('/')] = e;

                    // Sorted by reference count descending; LINQ's stable sort
                    // keeps ties in first-seen order.
                    foreach (var path in order.OrderByDescending(p => counts[p]))
                    {
                        string leaf = path.Split('/').Last();
                        string stem = Regex.Replace(leaf, @"_albedo\.dds$|\.dds$", "", RegexOptions.IgnoreCase);
                        string family;
                        if (path.StartsWith("/maps/", StringComparison.OrdinalIgnoreCase)) family = "MAP-LOCAL";
                        else
                        {
                            var parts = path.Split('/');
                            family = parts.Length > 2 ? parts[2] : null;
                        }

                        byte[] bytes = null;
                        if (index.TryGetValue(path.TrimStart('/'), out var entry))
                        {
                            using var ms = new MemoryStream();
                            using (var s = entry.Open()) s.CopyTo(ms);
                            bytes = ms.ToArray();
                        }
                        else if (family == "MAP-LOCAL")
                        {
                            string rel = path.Substring("/maps/".Length).Replace('/', Path.DirectorySeparatorChar);
                            foreach (var root in mapsRoot)
                            {
                                if (string.IsNullOrEmpty(root)) continue;
                                string cand = Path.Combine(root, rel);
                                if (File.Exists(cand)) { bytes = File.ReadAllBytes(cand); break; }
                            }
                        }
                        if (bytes == null) { failed.Add(path); continue; }

                        var i = MapGen.ReadDdsInfo(bytes);
                        if (!i.Ok) { failed.Add(F("{0} ({1})", path, i.Format ?? "")); continue; }

                        string role = GetRole(stem);
                        if (role == "other")
                        {
                            // Colour settles the stragglers: transition blends,
                            // coral, map-local customs. Rough bands, but each
                            // lands on a plausible material where the old path
                            // landed everything on rock.
                            double gx = i.G - (i.R + i.B) / 2;
                            double spread = Math.Max(i.R, Math.Max(i.G, i.B)) - Math.Min(i.R, Math.Min(i.G, i.B));
                            role = gx > 8 ? "grass"
                                 : i.Luma > 150 && spread < 40 ? "snow"
                                 : i.R - i.G > 30 && i.Luma < 95 ? "lava"
                                 : i.Luma < 50 ? "rock"
                                 : i.R > i.G && i.G > i.B && i.R - i.B > 35 ? "dirt"
                                 : "rock";
                        }
                        rows.Add(new MeasureRow
                        {
                            Stem = stem,
                            Role = role,
                            Family = family,
                            Maps = counts[path],
                            Luma = Math.Round(i.Luma, 1),
                            Std = LumaStd(bytes),
                            StdC = LumaStdCoarse(bytes),
                            R = Math.Round(i.R, 1),
                            G = Math.Round(i.G, 1),
                            B = Math.Round(i.B, 1),
                            Format = i.Format,
                            Size = F("{0}x{1}", i.Width, i.Height),
                            Path = path,
                        });
                    }
                }

                log(F("  measured {0}, failed {1}", rows.Count, failed.Count));
                foreach (var f in failed.Take(5)) log("    " + f);

                log("");
                log("By role (stock environments only):");
                foreach (var g in GroupInOrder(rows.Where(r => r.Family != "MAP-LOCAL"), r => r.Role)
                    .OrderByDescending(g => g.Items.Sum(r => (double)r.Maps)))
                {
                    log(F("  {0,-8} {1,3} textures  {2,4} map-refs   luma {3,3:n0} ({4,3:n0}-{5,3:n0})",
                        g.Key, g.Items.Count, g.Items.Sum(r => (double)r.Maps),
                        g.Items.Average(r => r.Luma), g.Items.Min(r => r.Luma), g.Items.Max(r => r.Luma)));
                }

                if (!string.IsNullOrEmpty(csv))
                {
                    var header = new[] { "Stem", "Role", "Family", "Maps", "Luma", "Std", "StdC", "R", "G", "B", "Format", "Size", "Path" };
                    var cells = rows
                        .OrderBy(r => r.Role, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(r => r.Luma)
                        .Select(r => new[]
                        {
                            r.Stem, r.Role, r.Family ?? "", r.Maps.ToString(Inv),
                            D(r.Luma), D(r.Std), D(r.StdC), D(r.R), D(r.G), D(r.B),
                            r.Format ?? "", r.Size, r.Path,
                        })
                        .ToList();
                    ExportCsv(csv, header, cells);
                    log("");
                    log("wrote " + csv);
                }
                return 0;
            }
            catch (Exception ex)
            {
                log("ERROR: " + ex.Message);
                return 1;
            }
        }

        /// Get-ChildItem -Recurse -Filter *.scmap: files of a directory in
        /// name order, then its subdirectories in name order. Unreadable
        /// directories are skipped, as -ErrorAction SilentlyContinue did.
        static void FindScmaps(string dir, List<string> outp)
        {
            string[] files, dirs;
            try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); }
            catch { return; }
            foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                if (f.EndsWith(".scmap", StringComparison.OrdinalIgnoreCase)) outp.Add(f);
            foreach (var d in dirs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                FindScmaps(d, outp);
        }

        // ---- Match-Textures ------------------------------------------------

        // Pairs judged by eye, overriding the scored pick. The metrics measure
        // mean, contrast and feature size; they cannot measure "soft". Canis
        // proved it in the field: its two "gravel" layers cover 80% of the map
        // each and render in FA as gentle warm sand-mottle, and every scored
        // candidate - real gravel, then fine-but-confetti dirt - read busier
        // than the original. Keep this list short and only for pairs actually
        // compared in the field.
        static readonly Dictionary<string, string> EyeOverrides = new(StringComparer.OrdinalIgnoreCase)
        {
            ["/env/desert/layers/des_gravel01_albedo.dds"] = "cc0_ground080",
            ["/env/desert/layers/des_gravel_albedo.dds"] = "cc0_ground054",
        };

        static double[] Chroma(double r, double g, double b)
        {
            double s = r + g + b;
            if (s <= 0) return new[] { 0.333, 0.333, 0.333 };
            return new[] { r / s, g / s, b / s };
        }

        sealed class PackRow
        {
            public string Name, Role;
            public double R, G, B;
            public double? Std, StdC;
        }

        sealed class MatchRow
        {
            public string ScPath, ScStem, ScRole, UsedRole;
            public bool Exact;
            public int Maps;
            public string Cc0;
            public double RemapR, RemapG, RemapB;
            public bool Clamped;
            public double WantLuma, GotLuma;
        }

        /// Map every measured SC texture onto a CC0 substitute with the solved
        /// per-channel diffuseRemap. `baseRemap` null means the script's
        /// default of 0.37/0.35/0.32 - the multiply the source-texture path
        /// applies today, so the substitution is the only change.
        public static int MatchTextures(string scCsv, string packDir, string outCsv, double[] baseRemap, Action<string> log)
        {
            log ??= _ => { };
            try
            {
                var baseR = baseRemap ?? new[] { 0.37, 0.35, 0.32 };
                var sc = CsvTable.Load(scCsv);
                var packT = CsvTable.Load(Path.Combine(packDir, "manifest.csv"));
                var pack = new List<PackRow>();
                foreach (var r in packT.Rows)
                {
                    pack.Add(new PackRow
                    {
                        Name = packT.Get(r, "Name"),
                        Role = packT.Get(r, "Role"),
                        R = double.Parse(packT.Get(r, "R"), Inv),
                        G = double.Parse(packT.Get(r, "G"), Inv),
                        B = double.Parse(packT.Get(r, "B"), Inv),
                        Std = OptNum(packT, r, "Std"),
                        StdC = OptNum(packT, r, "StdC"),
                    });
                }

                // Roles the library does not carry, and the nearest thing it
                // does. Recorded rather than silently folded in, so the report
                // can say which maps are affected. Empty in the script today.
                var fallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                var rows = new List<MatchRow>();
                var substituted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in sc.Rows)
                {
                    string role = sc.Get(t, "Role");
                    string used = fallback.TryGetValue(role, out var fb) ? fb : role;
                    // Loose ground is a continuum, not a taxonomy. Supreme
                    // Commander's desert "gravel" textures are sand with a few
                    // pebbles while its evergreen gravels really are stone
                    // chips; adjacent roles join the pool and the measured
                    // stats pick within it.
                    string[] pool = used.ToLowerInvariant() switch
                    {
                        "sand" => new[] { "sand", "gravel" },
                        "gravel" => new[] { "gravel", "sand", "dirt" },
                        "dirt" => new[] { "dirt", "gravel" },
                        "crack" => new[] { "crack", "dirt" },
                        _ => new[] { used },
                    };
                    var cands = pack.Where(c => pool.Contains(c.Role, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (cands.Count == 0)
                        cands = pack.Where(c => string.Equals(c.Role, "rock", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (cands.Count == 0) continue;

                    double tR = double.Parse(sc.Get(t, "R"), Inv);
                    double tG = double.Parse(sc.Get(t, "G"), Inv);
                    double tB = double.Parse(sc.Get(t, "B"), Inv);
                    var ct = Chroma(tR, tG, tB);
                    double tStd = OptNum(sc, t, "Std") ?? 45.0;
                    double tC = OptNum(sc, t, "StdC") ?? 20.0;
                    PackRow best = null;
                    double bestD = double.MaxValue;
                    foreach (var c in cands)
                    {
                        var cc = Chroma(c.R, c.G, c.B);
                        double d = 0.0;
                        for (int i = 0; i < 3; i++) d += Math.Pow(ct[i] - cc[i], 2);
                        // Contrast term: chroma distances run about 0.000..0.01,
                        // luma-std differences 0..40, so 0.001 per unit prices
                        // character above hue - the per-channel remap corrects
                        // hue exactly, but nothing downstream can fix a texture
                        // that varies twice as loudly as the original.
                        d += Math.Abs(tStd - (c.Std ?? 45.0)) * 0.001;
                        // Feature size, separately from amplitude: coarse-scale
                        // contrast survives downsampling only when the features
                        // are large.
                        d += Math.Abs(tC - (c.StdC ?? 20.0)) * 0.001;
                        if (d < bestD) { bestD = d; best = c; }
                    }

                    string tPath = sc.Get(t, "Path");
                    if (EyeOverrides.TryGetValue(tPath.ToLowerInvariant(), out var ovr))
                    {
                        var o = pack.FirstOrDefault(p => string.Equals(p.Name, ovr, StringComparison.OrdinalIgnoreCase));
                        if (o != null) best = o;
                    }

                    // Per channel: what multiply puts the substitute on the
                    // original's colour? The floor is low on purpose:
                    // saturation lives in the gap between channels, so a red
                    // ground legitimately needs its blue multiplied by very
                    // little; a floor set to protect luminance instead greys
                    // the colour off.
                    var src = new[] { tR, tG, tB };
                    var dst = new[] { best.R, best.G, best.B };
                    var remap = new double[3];
                    bool clamped = false;
                    for (int i = 0; i < 3; i++)
                    {
                        double v = dst[i] > 1 ? baseR[i] * src[i] / dst[i] : baseR[i];
                        double c = Math.Max(0.03, Math.Min(0.95, v));
                        if (Math.Abs(c - v) > 1e-6) clamped = true;
                        remap[i] = Math.Round(c, 4);
                    }

                    substituted[best.Name] = substituted.TryGetValue(best.Name, out int sn) ? sn + 1 : 1;
                    rows.Add(new MatchRow
                    {
                        ScPath = tPath,
                        ScStem = sc.Get(t, "Stem"),
                        ScRole = role,
                        UsedRole = used,
                        Exact = string.Equals(role, used, StringComparison.OrdinalIgnoreCase),
                        Maps = int.Parse(sc.Get(t, "Maps"), Inv),
                        Cc0 = best.Name,
                        RemapR = remap[0],
                        RemapG = remap[1],
                        RemapB = remap[2],
                        Clamped = clamped,
                        // What the substitute will actually render at, against
                        // what the original renders at now. This is the number
                        // that says whether the mapping worked.
                        WantLuma = Math.Round(0.299 * src[0] * baseR[0] + 0.587 * src[1] * baseR[1] + 0.114 * src[2] * baseR[2], 1),
                        GotLuma = Math.Round(0.299 * dst[0] * remap[0] + 0.587 * dst[1] * remap[1] + 0.114 * dst[2] * remap[2], 1),
                    });
                }

                {
                    var header = new[] { "ScPath", "ScStem", "ScRole", "UsedRole", "Exact", "Maps", "Cc0", "RemapR", "RemapG", "RemapB", "Clamped", "WantLuma", "GotLuma" };
                    var cells = rows.Select(r => new[]
                    {
                        r.ScPath, r.ScStem, r.ScRole, r.UsedRole, r.Exact.ToString(), r.Maps.ToString(Inv),
                        r.Cc0, D(r.RemapR), D(r.RemapG), D(r.RemapB), r.Clamped.ToString(),
                        D(r.WantLuma), D(r.GotLuma),
                    }).ToList();
                    ExportCsv(outCsv, header, cells);
                }

                var err = rows.Select(r => Math.Abs(r.GotLuma - r.WantLuma)).ToList();
                log(F("{0} textures mapped onto {1} CC0 materials", rows.Count, substituted.Count));
                log(F("  rendered-tone error: mean {0:n2}, worst {1:n1} (out of 255)", err.Average(), err.Max()));
                log(F("  {0} needed clamping", rows.Count(r => r.Clamped)));
                log("");
                log("Role substitutions that are not like-for-like:");
                foreach (var g in GroupInOrder(rows.Where(r => !r.Exact), r => r.ScRole))
                {
                    log(F("  {0,-8} -> {1,-6} {2,3} textures, {3,3} map-refs",
                        g.Key, g.Items[0].UsedRole, g.Items.Count, g.Items.Sum(r => (double)r.Maps)));
                }
                log("");
                log("Most-used substitutes:");
                foreach (var g in GroupInOrder(rows, r => r.Cc0)
                    .OrderByDescending(g => g.Items.Sum(r => (double)r.Maps))
                    .Take(8))
                {
                    log(F("  {0,-18} {1,3} textures, {2,4} map-refs",
                        g.Key, g.Items.Count, g.Items.Sum(r => (double)r.Maps)));
                }
                log("");
                log("wrote " + outCsv);
                return 0;
            }
            catch (Exception ex)
            {
                log("ERROR: " + ex.Message);
                return 1;
            }
        }

        // ---- Build-TexturePack ---------------------------------------------

        // The ambientCG material set, chosen for material, not for tone - tone
        // is corrected per layer later. Where a role has several entries they
        // differ in pattern and grain, so neighbouring layers on one map do
        // not read as the same surface twice. Downloads resolve as
        // https://ambientcg.com/get?file={Id}_{Variant}.zip and cache under
        // texturepack/cache, so a re-run costs nothing.
        //
        // Rock029 is the red one. Crystalline is stylised sci-fi with no
        // photographic equivalent, but ice is its nearest honest neighbour and
        // Onyx carries the dark glassy end.
        static readonly (string Role, string[] Ids)[] Materials =
        {
            ("rock",    new[] { "Rock030", "Rock051", "Rock058", "Rock020", "Rock029" }),
            ("grass",   new[] { "Grass004", "Grass001", "Ground037" }),
            ("sand",    new[] { "Ground054", "Ground080", "Ground078" }),
            ("gravel",  new[] { "Gravel023", "Gravel040", "Ground110", "Gravel025" }),
            ("dirt",    new[] { "Ground048", "Ground103", "Ground106", "Ground107" }),
            ("snow",    new[] { "Snow006", "Snow010A", "Snow002" }),
            ("crack",   new[] { "Ground093C", "Ground095A" }),
            ("crystal", new[] { "Ice002", "Ice003", "Ice004", "Onyx006" }),
            ("lava",    new[] { "Lava004", "Lava001" }),
        };

        sealed class ManifestRow
        {
            public string Name, Role, Source;
            public double Luma, Std, StdC, R, G, B;
            public string Size;
            public bool Normal, Mask;
        }

        /// Build the CC0 ground-material library: DXT1 albedo and normal, a
        /// DXT5 HDRP mask from AO+roughness, and manifest.csv. `variant` null
        /// means the script's default of "1K-JPG".
        public static int BuildTexturePack(string outDir, string variant, bool force, Action<string> log)
        {
            log ??= _ => { };
            try
            {
                variant ??= "1K-JPG";
                string cache = Path.Combine(outDir, "cache");
                Directory.CreateDirectory(outDir);
                Directory.CreateDirectory(cache);

                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(600);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("sanctuary-map-gen/1.0");

                var rows = new List<ManifestRow>();
                int total = Materials.Sum(m => m.Ids.Length);
                int i = 0;
                foreach (var (role, ids) in Materials)
                {
                    foreach (var id in ids)
                    {
                        i++;
                        string zipPath = Path.Combine(cache, id + "_" + variant + ".zip");
                        if (!File.Exists(zipPath))
                        {
                            string url = "https://ambientcg.com/get?file=" + id + "_" + variant + ".zip";
                            log(F("[{0,2}/{1}] downloading {2}", i, total, id));
                            try
                            {
                                // Buffered like Invoke-WebRequest, and written
                                // only once complete, so a failure never
                                // leaves a partial zip posing as cache.
                                byte[] data = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
                                File.WriteAllBytes(zipPath, data);
                            }
                            catch (Exception ex)
                            {
                                log(F("        FAILED: {0}", ex.Message));
                                continue;
                            }
                        }

                        string albedoOut = Path.Combine(outDir, "cc0_" + id.ToLowerInvariant() + "_albedo.dds");
                        string normalOut = Path.Combine(outDir, "cc0_" + id.ToLowerInvariant() + "_normal.dds");
                        string maskOut = Path.Combine(outDir, "cc0_" + id.ToLowerInvariant() + "_mask.dds");
                        using (var z = ZipFile.OpenRead(zipPath))
                        {
                            foreach (var (match, outPath) in new[]
                            {
                                (@"_Color\.jpg$", albedoOut),
                                (@"_NormalGL\.jpg$", normalOut),
                            })
                            {
                                if (File.Exists(outPath) && !force) continue;
                                var e = z.Entries.FirstOrDefault(en => Regex.IsMatch(en.FullName, match, RegexOptions.IgnoreCase));
                                if (e == null) { log(F("        no {0} in {1}", match, id)); continue; }
                                using var ms = new MemoryStream();
                                using (var st = e.Open()) st.CopyTo(ms);
                                ms.Position = 0;
                                var bgra = GetBgra(ms, out int w, out int h);
                                File.WriteAllBytes(outPath, MapGen.WriteDxt1Dds(bgra, w, h));
                            }

                            // The mask. Sanctuary's stratum mask is Unity
                            // HDRP's mask map, so the layout is fixed:
                            //
                            //     R metallic   G ambient occlusion   B detail   A smoothness
                            //
                            // ambientCG ships an AO map and a roughness map,
                            // which is exactly G and A. Ground is not metal and
                            // there is no detail map, so R and B are zero.
                            // Smoothness is the inverse of roughness.
                            if (!File.Exists(maskOut) || force)
                            {
                                var aoE = z.Entries.FirstOrDefault(en => Regex.IsMatch(en.FullName, @"_AmbientOcclusion\.jpg$", RegexOptions.IgnoreCase));
                                var roE = z.Entries.FirstOrDefault(en => Regex.IsMatch(en.FullName, @"_Roughness\.jpg$", RegexOptions.IgnoreCase));
                                // Four of the packs ship roughness but no AO.
                                // Smoothness is the channel that actually
                                // matters here, so build the mask anyway with
                                // G at 255, meaning no occlusion.
                                if (roE != null)
                                {
                                    byte[] ro;
                                    int rw, rh;
                                    using (var ms2 = new MemoryStream())
                                    {
                                        using (var s2 = roE.Open()) s2.CopyTo(ms2);
                                        ms2.Position = 0;
                                        ro = GetBgra(ms2, out rw, out rh);
                                    }

                                    byte[] ao = null;
                                    if (aoE != null)
                                    {
                                        int aw, ah;
                                        using var ms1 = new MemoryStream();
                                        using (var s1 = aoE.Open()) s1.CopyTo(ms1);
                                        ms1.Position = 0;
                                        ao = GetBgra(ms1, out aw, out ah);
                                        if (aw != rw || ah != rh) ao = null;
                                    }

                                    var mask = new byte[rw * rh * 4];
                                    for (int k = 0; k < mask.Length; k += 4)
                                    {
                                        mask[k] = 0;                                      // B - detail, unused
                                        mask[k + 1] = ao != null ? ao[k + 1] : (byte)255; // G - ambient occlusion
                                        mask[k + 2] = 0;                                  // R - metallic; ground is not metal
                                        mask[k + 3] = (byte)(255 - ro[k + 1]);            // A - smoothness = 1 - roughness
                                    }
                                    File.WriteAllBytes(maskOut, MapGen.WriteDxt5Dds(mask, rw, rh));
                                }
                            }
                        }

                        if (!File.Exists(albedoOut)) continue;
                        byte[] albedoBytes = File.ReadAllBytes(albedoOut);
                        var info = MapGen.ReadDdsInfo(albedoBytes);
                        rows.Add(new ManifestRow
                        {
                            Name = "cc0_" + id.ToLowerInvariant(),
                            Role = role,
                            Source = id,
                            Luma = Math.Round(info.Luma, 1),
                            Std = LumaStd(albedoBytes),
                            StdC = LumaStdCoarse(albedoBytes),
                            R = Math.Round(info.R, 1),
                            G = Math.Round(info.G, 1),
                            B = Math.Round(info.B, 1),
                            Size = F("{0}x{1}", info.Width, info.Height),
                            Normal = File.Exists(normalOut),
                            Mask = File.Exists(maskOut),
                        });
                        log(F("[{0,2}/{1}] {2,-22} {3,-7} luma {4,5:n1}  rgb {5,3:n0},{6,3:n0},{7,3:n0}",
                            i, total, id, role, info.Luma, info.R, info.G, info.B));
                    }
                }

                string manifest = Path.Combine(outDir, "manifest.csv");
                {
                    var header = new[] { "Name", "Role", "Source", "Luma", "Std", "StdC", "R", "G", "B", "Size", "Normal", "Mask" };
                    var cells = rows.Select(r => new[]
                    {
                        r.Name, r.Role, r.Source, D(r.Luma), D(r.Std), D(r.StdC),
                        D(r.R), D(r.G), D(r.B), r.Size, r.Normal.ToString(), r.Mask.ToString(),
                    }).ToList();
                    ExportCsv(manifest, header, cells);
                }
                log("");
                log(F("{0} materials built into {1}", rows.Count, outDir));
                log(F("  {0} with a normal map, {1} with a real mask", rows.Count(r => r.Normal), rows.Count(r => r.Mask)));
                log(F("  manifest: {0}", manifest));
                return 0;
            }
            catch (Exception ex)
            {
                log("ERROR: " + ex.Message);
                return 1;
            }
        }

        /// Decode a JPG/PNG stream to raw BGRA via System.Drawing, exactly as
        /// the script's Get-Bgra did: Format32bppArgb in memory is BGRA.
        static byte[] GetBgra(Stream stream, out int w, out int h)
        {
            using var bmp = (System.Drawing.Bitmap)System.Drawing.Image.FromStream(stream);
            w = bmp.Width;
            h = bmp.Height;
            var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
            var d = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var buf = new byte[bmp.Width * bmp.Height * 4];
            System.Runtime.InteropServices.Marshal.Copy(d.Scan0, buf, 0, buf.Length);
            bmp.UnlockBits(d);
            return buf;
        }

        // ---- contrast metrics (shared by Measure and Build) -----------------

        // Contrast: the standard deviation of luma across the surface. Mean
        // colour says what a texture is on average; this says how loudly it
        // varies, which is what makes a fine sand and a bold pebble field read
        // differently at the same mean. The defaults (45 fine, 20 coarse) are
        // the corpus-typical values, used where the format cannot decode.

        static double LumaStdCoarse(byte[] bytes)
        {
            var px = MapGen.DecodeDdsToBgra(bytes, out int w, out int h);
            if (px == null) return 20.0;
            int cw = Math.Max(4, PsInt(w / 8.0)), ch = Math.Max(4, PsInt(h / 8.0));
            int n = 0;
            double s = 0, s2 = 0;
            for (int cy = 0; cy < ch; cy++)
            {
                int y = PsInt((double)cy * h / ch);
                for (int cx = 0; cx < cw; cx++)
                {
                    int x = PsInt((double)cx * w / cw);
                    // Mean of a small neighbourhood stands in for the box average.
                    double acc = 0;
                    int cnt = 0;
                    for (int dy = 0; dy < 8; dy += 3)
                    {
                        for (int dx = 0; dx < 8; dx += 3)
                        {
                            int xx = Math.Min(w - 1, x + dx), yy = Math.Min(h - 1, y + dy);
                            int o = (yy * w + xx) * 4;
                            acc += 0.299 * px[o + 2] + 0.587 * px[o + 1] + 0.114 * px[o];
                            cnt++;
                        }
                    }
                    double l = acc / cnt;
                    s += l; s2 += l * l; n++;
                }
            }
            if (n < 2) return 20.0;
            return Math.Round(Math.Sqrt(Math.Max(0, s2 / n - (s / n) * (s / n))), 1);
        }

        static double LumaStd(byte[] bytes)
        {
            var px = MapGen.DecodeDdsToBgra(bytes, out int w, out int h);
            if (px == null) return 45.0;
            int n = 0;
            double s = 0, s2 = 0;
            int step = Math.Max(1, PsInt((double)w * h / 65536)) * 4;
            for (int k = 0; k < px.Length; k += step)
            {
                double l = 0.299 * px[k + 2] + 0.587 * px[k + 1] + 0.114 * px[k];
                s += l; s2 += l * l; n++;
            }
            if (n < 2) return 45.0;
            return Math.Round(Math.Sqrt(Math.Max(0, s2 / n - (s / n) * (s / n))), 1);
        }

        // ---- CSV -----------------------------------------------------------

        /// Doubles rendered the way PowerShell 7's Export-Csv renders them:
        /// shortest round-trip, invariant culture, so 45.0 is "45".
        static string D(double v) => v.ToString(Inv);

        /// Export-Csv -NoTypeInformation -Encoding UTF8 under PowerShell 7:
        /// every field quoted, embedded quotes doubled, CRLF line endings,
        /// UTF-8 without BOM, trailing newline. Verified against the CSVs the
        /// scripts actually wrote.
        static void ExportCsv(string path, string[] header, List<string[]> rows)
        {
            var sb = new StringBuilder();
            void Line(string[] cells)
            {
                for (int i = 0; i < cells.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append((cells[i] ?? "").Replace("\"", "\"\"")).Append('"');
                }
                sb.Append("\r\n");
            }
            Line(header);
            foreach (var r in rows) Line(r);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        /// Import-Csv equivalent for the two CSVs this pipeline exchanges:
        /// header-addressed string fields, case-insensitive column names.
        /// Line-based - none of this data carries embedded newlines.
        sealed class CsvTable
        {
            public readonly List<string[]> Rows = new();
            readonly Dictionary<string, int> cols = new(StringComparer.OrdinalIgnoreCase);

            public static CsvTable Load(string path)
            {
                var t = new CsvTable();
                string[] lines = File.ReadAllLines(path);
                if (lines.Length == 0) return t;
                var header = ParseCsvLine(lines[0]);
                for (int i = 0; i < header.Length; i++) t.cols[header[i]] = i;
                for (int i = 1; i < lines.Length; i++)
                {
                    if (lines[i].Length == 0) continue;
                    t.Rows.Add(ParseCsvLine(lines[i]));
                }
                return t;
            }

            public bool Has(string name) => cols.ContainsKey(name);

            public string Get(string[] row, string name)
            {
                if (!cols.TryGetValue(name, out int i) || i >= row.Length) return null;
                return row[i];
            }

            static string[] ParseCsvLine(string line)
            {
                var outp = new List<string>();
                var sb = new StringBuilder();
                bool q = false;
                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    if (q)
                    {
                        if (c == '"')
                        {
                            if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                            else q = false;
                        }
                        else sb.Append(c);
                    }
                    else if (c == '"') q = true;
                    else if (c == ',') { outp.Add(sb.ToString()); sb.Clear(); }
                    else sb.Append(c);
                }
                outp.Add(sb.ToString());
                return outp.ToArray();
            }
        }

        /// PSObject property present and non-empty - the scripts' pattern
        /// `$t.PSObject.Properties['Std'] -and $t.Std` before [double] casting.
        static double? OptNum(CsvTable t, string[] row, string col)
        {
            if (!t.Has(col)) return null;
            string s = t.Get(row, col);
            if (string.IsNullOrEmpty(s)) return null;
            return double.Parse(s, Inv);
        }

        /// Group-Object: groups in first-encounter order, keys compared
        /// case-insensitively, item order preserved within each group.
        static List<(string Key, List<T> Items)> GroupInOrder<T>(IEnumerable<T> src, Func<T, string> key)
        {
            var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var groups = new List<(string Key, List<T> Items)>();
            foreach (var item in src)
            {
                string k = key(item);
                if (!idx.TryGetValue(k, out int i))
                {
                    idx[k] = i = groups.Count;
                    groups.Add((k, new List<T>()));
                }
                groups[i].Items.Add(item);
            }
            return groups;
        }
    }
}
