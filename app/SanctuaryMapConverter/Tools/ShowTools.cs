using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SanctuaryMapConverter.Core;

namespace SanctuaryMapConverter.Tools
{
    // The tools/Show-*.ps1 diagnostics in C#:
    //   Sanmap       - Show-Sanmap.ps1: renders a deployed map from the bytes on
    //                  disk (not from the generator) and prints one line of
    //                  pathability numbers per map.
    //   TextureTones - Show-TextureTones.ps1: mean colour and brightness of every
    //                  stratum albedo in a .sanpack, sorted by luminance.
    //   Stratums     - Show-Stratums.ps1: what each of the eight blended layers
    //                  is painted on - cover, mean slope, mean height.
    //   SplatMap     - Show-SplatMap.ps1: top-down false-colour image of which
    //                  stratum layer wins at each texel.
    //
    // Defaults the scripts resolved from their own location or from F:\ are not
    // baked in here: the CLI dispatcher resolves those (via GamePaths) and passes
    // them; null/0 falls back to the script's non-path defaults where it had one.
    public static class ShowTools
    {
        // PowerShell's [int] cast rounds (banker's) where C#'s truncates; every
        // ported [int](...) goes through this.
        static int PsInt(double v) => (int)Math.Round(v, MidpointRounding.ToEven);

        static string Leaf(string dir) => Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));

        static double Num(JsonElement j, string name) =>
            j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0.0;

        static bool Flag(JsonElement j, string name) =>
            j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        static string FirstSanmap(string mapDir) =>
            Directory.EnumerateFiles(mapDir, "*.sanmap").FirstOrDefault();

        // markers.<kind>.transforms is an object keyed by marker name; document
        // order matches what ConvertFrom-Json's PSObject.Properties enumerated.
        static List<(double X, double Z)> Markers(JsonElement j, string kind)
        {
            var found = new List<(double, double)>();
            if (j.TryGetProperty("markers", out var m) && m.TryGetProperty(kind, out var k)
                && k.TryGetProperty("transforms", out var t) && t.ValueKind == JsonValueKind.Object)
                foreach (var p in t.EnumerateObject())
                {
                    var pos = p.Value.GetProperty("position");
                    found.Add((pos.GetProperty("x").GetDouble(), pos.GetProperty("z").GetDouble()));
                }
            return found;
        }

        struct Tga { public int W, H; public byte[] Data; }   // pixels start at byte 18, BGRA

        static Tga ReadTga(string p)
        {
            byte[] b = File.ReadAllBytes(p);
            return new Tga
            {
                W = b[12] | (b[13] << 8),
                H = b[14] | (b[15] << 8),
                Data = b,
            };
        }

        // ($path -replace '.*/','') -replace '_albedo\.tga$','' - PS -replace is
        // case-insensitive.
        static string StripAlbedo(string p) =>
            Regex.Replace(Regex.Replace(p ?? "", ".*/", "", RegexOptions.IgnoreCase),
                          @"_albedo\.tga$", "", RegexOptions.IgnoreCase);

        // ---- Show-Sanmap ---------------------------------------------------

        /// Summary of deployed map folders, rendered from the bytes on disk the
        /// way Load.ReadRaw reads them. outDir null = current directory (the
        /// script used its own folder); res 0 = 700.
        public static int Sanmap(string[] mapDir, string outDir, int res, Action<string> log)
        {
            EngineState.Reset();
            if (string.IsNullOrEmpty(outDir)) outDir = Environment.CurrentDirectory;
            if (res <= 0) res = 700;
            Directory.CreateDirectory(outDir);

            foreach (var d in mapDir)
            {
                string name = Leaf(d);
                string sanmap = FirstSanmap(d);
                if (sanmap == null) { log($"WARNING: no .sanmap in {d}"); continue; }
                using var doc = JsonDocument.Parse(File.ReadAllText(sanmap));
                var j = doc.RootElement;

                double hmProp = Num(j, "heightmapResolution");
                int hmRes = PsInt(hmProp != 0 ? hmProp : Num(j, "width") + 1);
                float size = (float)Num(j, "width");
                float maxH = (float)Num(j, "height");
                bool hasWater = Flag(j, "hasWater");
                float water = hasWater ? (float)Num(j, "waterLevel") : -9999f;
                string raw = Path.Combine(d, @"Textures\heightmap.raw");

                var mx = new List<float>();
                var mz = new List<float>();
                var mk = new List<int>();
                foreach (var (x, z) in Markers(j, "Alloys")) { mx.Add((float)x); mz.Add((float)z); mk.Add(1); }
                foreach (var (x, z) in Markers(j, "Spawn")) { mx.Add((float)x); mz.Add((float)z); mk.Add(0); }

                string outPng = Path.Combine(outDir, name + "_ondisk.png");
                MapGen.RenderHeightmapFile(raw, hmRes, size, maxH, water,
                                           mx.ToArray(), mz.ToArray(), mk.ToArray(), outPng, res);
                float steep = MapGen.SteepFractionOnDisk(raw, hmRes, size, maxH, water);

                // Pathability against the shipped bytes, not the generator's memory.
                MapGen.LoadHeightFromFile(raw, hmRes, size, maxH, water);
                var spawnX = new List<float>();
                var spawnZ = new List<float>();
                foreach (var (x, z) in Markers(j, "Spawn")) { spawnX.Add((float)x); spawnZ.Add((float)z); }
                MapGen.BaseX = spawnX.ToArray();
                MapGen.BaseZ = spawnZ.ToArray();
                bool[,] reach = MapGen.Reachable(spawnX[0], spawnZ[0]);
                int walk = MapGen.WalkableCount();
                int rc = MapGen.CountTrue(reach);

                int badSpawn = 0;
                for (int i = 1; i < spawnX.Count; i++)
                    if (!MapGen.IsReachable(reach, spawnX[i], spawnZ[i])) badSpawn++;
                int badMex = 0;
                for (int i = 0; i < mx.Count; i++)
                    if (mk[i] == 1 && !MapGen.IsReachable(reach, mx[i], mz[i])) badMex++;
                float[] og = MapGen.OpenGroundStats(6.0f);
                float[] sp = MapGen.PathingSpecks(60);

                log(string.Format(
                    "{0,-40} {1}x{1}m  water {2,-4}  over-limit {3,4:P0}  reachable {4,4:P0}  open {5,4:P0}  cut off {6}/{7}  specks {8:N0}",
                    name, Num(j, "width"),
                    hasWater ? (object)Num(j, "waterLevel") : "none",
                    steep, (double)rc / Math.Max(1, walk), og[0] / Math.Max(1f, og[1]),
                    badSpawn, badMex, sp[0]));

                // Unity rounds heightmapResolution to a power of two plus one. Anything else and
                // SetHeights fills only a corner of the terrain; the remainder stays at height 0.
                int n = hmRes - 1;
                if (n <= 0 || (n & (n - 1)) != 0)
                    log(string.Format("    *** heightmapResolution {0} is not 2^n+1 - Unity will resize the terrain and leave part of it unwritten", hmRes));
            }
            return 0;
        }

        // ---- Show-TextureTones ---------------------------------------------

        // DXT1/DXT5 keep two RGB565 endpoints at a fixed offset in every block,
        // so averaging them estimates the texture mean closely. Capped like the
        // script: 40000 endpoints - these are 2048 square and we only need a mean.
        static bool BlockTone(byte[] b, int dataStart, int blockBytes, int colourOffset,
                              out double r, out double g, out double bl)
        {
            double sr = 0.0, sg = 0.0, sbl = 0.0; int n = 0;
            for (int i = dataStart; i + blockBytes <= b.Length && n < 40000; i += blockBytes)
            {
                int o = i + colourOffset;
                for (int k = 0; k <= 2; k += 2)
                {
                    int c = b[o + k] | (b[o + k + 1] << 8);
                    sr += ((c >> 11) & 0x1F) * 255.0 / 31.0;
                    sg += ((c >> 5) & 0x3F) * 255.0 / 63.0;
                    sbl += (c & 0x1F) * 255.0 / 31.0;
                    n++;
                }
            }
            r = g = bl = 0.0;
            if (n == 0) return false;
            r = sr / n; g = sg / n; bl = sbl / n;
            return true;
        }

        /// Mean colour and brightness of every stratum albedo in a .sanpack.
        /// BC7 textures go through Bc7.SurfaceMean (endpoint averaging returns
        /// noise on BC7); match null = 'Stratum/.*_albedo'.
        public static int TextureTones(string sanpack, string match, Action<string> log)
        {
            if (string.IsNullOrEmpty(match)) match = "Stratum/.*_albedo";
            var rows = new List<(string Name, string Fmt, string Size, int R, int G, int B, int Lum, int Warm)>();

            using (var zip = ZipFile.OpenRead(sanpack))
                foreach (var e in zip.Entries)
                {
                    // PS -match is case-insensitive.
                    if (!Regex.IsMatch(e.FullName, match, RegexOptions.IgnoreCase)) continue;
                    byte[] b;
                    using (var ms = new MemoryStream())
                    {
                        using (var s = e.Open()) s.CopyTo(ms);
                        b = ms.ToArray();
                    }
                    if (b.Length < 148) continue;
                    if (Encoding.ASCII.GetString(b, 0, 4) != "DDS ") continue;

                    int w = BitConverter.ToInt32(b, 16);
                    int h = BitConverter.ToInt32(b, 12);
                    string fcc = Encoding.ASCII.GetString(b, 84, 4);

                    // DXT1: 8-byte blocks, endpoints first. DXT5 and the BC3
                    // forms behind a DX10 header: 16-byte blocks with the alpha
                    // block first, so the colour endpoints sit at offset 8.
                    double tr, tg, tb;
                    string kind;
                    switch (fcc)
                    {
                        case "DXT1":
                            if (!BlockTone(b, 128, 8, 0, out tr, out tg, out tb)) continue;
                            kind = "DXT1";
                            break;
                        case "DXT5":
                            if (!BlockTone(b, 128, 16, 8, out tr, out tg, out tb)) continue;
                            kind = "DXT5";
                            break;
                        case "DX10":
                            // dxgiFormat 98 is BC7_UNORM - decode for real.
                            int dxgi = BitConverter.ToInt32(b, 128);
                            if (dxgi == 98)
                            {
                                if (!Bc7.SurfaceMean(b, 148, 40000, out float rr, out float gg, out float bb)) continue;
                                tr = rr; tg = gg; tb = bb; kind = "BC7";
                            }
                            else
                            {
                                if (!BlockTone(b, 148, 16, 8, out tr, out tg, out tb)) continue;
                                kind = "DX10:" + dxgi;
                            }
                            break;
                        default: continue;
                    }

                    // The script computes Size but its output lines never print
                    // it; kept for parity with the row objects it built.
                    rows.Add((
                        Regex.Replace(Regex.Replace(e.FullName, ".*/", "", RegexOptions.IgnoreCase),
                                      "_albedo.*", "", RegexOptions.IgnoreCase),
                        kind, w + "x" + h,
                        PsInt(tr), PsInt(tg), PsInt(tb),
                        PsInt(0.299 * tr + 0.587 * tg + 0.114 * tb),
                        PsInt(tr - tb)));
                }

            log(string.Format("  {0,-36} {1,-6} {2,4} {3,4} {4,4}  {5,4} {6,5}",
                "texture", "fmt", "R", "G", "B", "lum", "warm"));
            log(string.Format("  {0,-36} {1,-6} {2,4} {3,4} {4,4}  {5,4} {6,5}",
                new string('-', 36), "------", "----", "----", "----", "----", "-----"));
            foreach (var r in rows.OrderBy(x => x.Lum))
                log(string.Format("  {0,-36} {1,-6} {2,4} {3,4} {4,4}  {5,4} {6,5}",
                    r.Name, r.Fmt, r.R, r.G, r.B, r.Lum, r.Warm));
            return 0;
        }

        // ---- Show-Stratums -------------------------------------------------

        /// Per-layer cover, mean slope and mean height for a deployed map's
        /// eight blended stratum layers, sampled every second texel.
        public static int Stratums(string mapDir, Action<string> log)
        {
            EngineState.Reset();
            string f = FirstSanmap(mapDir) ?? throw new FileNotFoundException("no .sanmap in " + mapDir);
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            var j = doc.RootElement;
            string tex = Path.Combine(mapDir, "Textures");
            bool hasWater = Flag(j, "hasWater");
            float water = hasWater ? (float)Num(j, "waterLevel") : 0.0f;

            MapGen.LoadHeightFromFile(Path.Combine(tex, "heightmap.raw"), PsInt(Num(j, "heightmapResolution")),
                (float)Num(j, "width"), (float)Num(j, "height"), water);

            var t14 = ReadTga(Path.Combine(tex, "stratums_1_4.tga"));
            var t58 = ReadTga(Path.Combine(tex, "stratums_5_8.tga"));
            int sRes = t14.W;

            log(string.Format("{0}   {1}x{1} m, splat {2}x{2}, water {3}",
                Leaf(mapDir), Num(j, "width"), sRes,
                hasWater ? (object)Num(j, "waterLevel") : "none"));
            log("");

            // BGRA in the file: byte0=B=layer3, byte1=G=layer2, byte2=R=layer1, byte3=A=layer4
            var chan = new (int L, byte[] Data, int Byte)[]
            {
                (1, t14.Data, 2), (2, t14.Data, 1), (3, t14.Data, 0), (4, t14.Data, 3),
                (5, t58.Data, 2), (6, t58.Data, 1), (7, t58.Data, 0), (8, t58.Data, 3),
            };

            log("  layer  texture                                cover   mean slope   mean height   slope where weight > 0.5");
            log("  -----  -------------------------------------  ------  ----------   -----------   ------------------------");

            double step = Num(j, "width") / (sRes - 1);   // vertex-aligned, not texel-centred
            var layers = j.GetProperty("stratumLayers");
            foreach (var ch in chan)
            {
                byte[] d = ch.Data; int off = ch.Byte;
                double sumW = 0.0, sumSlope = 0.0, sumH = 0.0, strongSlope = 0.0;
                int strongN = 0;
                for (int r = 0; r < sRes; r += 2)
                    for (int c = 0; c < sRes; c += 2)
                    {
                        int v = d[18 + ((r * sRes) + c) * 4 + off];
                        if (v == 0) continue;
                        double w = v / 255.0;
                        double x = c * step, z = r * step;        // file row 0 is world z min
                        float sl = MapGen.SlopeAtWorld((float)x, (float)z);
                        float hh = MapGen.HeightAtWorld((float)x, (float)z);
                        sumW += w; sumSlope += w * sl; sumH += w * hh;
                        if (w > 0.5) { strongN++; strongSlope += sl; }
                    }
                double total = Math.Pow(Math.Ceiling(sRes / 2.0), 2);
                string path = StripAlbedo(layers[ch.L].GetProperty("albedo").GetProperty("path").GetString());
                if (sumW < 1)
                    log(string.Format("  {0,5}  {1,-37}  {2,6}", ch.L, path, "unused"));
                else
                    log(string.Format("  {0,5}  {1,-37}  {2,5:P1}  {3,8:N1} deg  {4,9:N1} m   {5,8:N1} deg  ({6:P0} of map)",
                        ch.L, path, sumW / total, sumSlope / sumW, sumH / sumW,
                        strongN != 0 ? strongSlope / strongN : 0.0, strongN / total));
            }
            return 0;
        }

        // ---- Show-SplatMap -------------------------------------------------

        /// Top-down false-colour image of which stratum layer wins at each
        /// texel - flat distinct colours on purpose: a diagram of the splat
        /// weights, not a preview. outPng null = maps\{name}_splat.png under the
        /// current directory (the script resolved it off its own location);
        /// res 0 = 900.
        public static int SplatMap(string mapDir, string outPng, int res, Action<string> log)
        {
            if (res <= 0) res = 900;
            string f = FirstSanmap(mapDir) ?? throw new FileNotFoundException("no .sanmap in " + mapDir);
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            var j = doc.RootElement;
            string tex = Path.Combine(mapDir, "Textures");
            string leaf = Leaf(mapDir);
            if (string.IsNullOrEmpty(outPng))
                outPng = Path.Combine(Environment.CurrentDirectory, "maps", leaf + "_splat.png");

            var t14 = ReadTga(Path.Combine(tex, "stratums_1_4.tga"));
            var t58 = ReadTga(Path.Combine(tex, "stratums_5_8.tga"));
            int sRes = t14.W;

            // BGRA in the file: byte0=B=layer3, byte1=G=layer2, byte2=R=layer1, byte3=A=layer4
            var chan = new (int L, byte[] Data, int O)[]
            {
                (1, t14.Data, 2), (2, t14.Data, 1), (3, t14.Data, 0), (4, t14.Data, 3),
                (5, t58.Data, 2), (6, t58.Data, 1), (7, t58.Data, 0), (8, t58.Data, 3),
            };
            // 0 = base showing through, then one colour per layer
            var cols = new[]
            {
                Color.FromArgb(40, 40, 46), Color.FromArgb(220, 60, 60),
                Color.FromArgb(70, 200, 90), Color.FromArgb(60, 130, 240),
                Color.FromArgb(240, 210, 60), Color.FromArgb(180, 90, 220),
                Color.FromArgb(240, 150, 50), Color.FromArgb(80, 220, 220),
                Color.FromArgb(250, 250, 250),
            };

            var counts = new int[9];
            using (var bmp = new Bitmap(res, res))
            {
                // The splat is stored bottom-up, so flip while drawing to get a
                // picture that matches a top-down view of the map.
                for (int y = 0; y < res; y++)
                {
                    int sy = sRes - 1 - PsInt((double)y * sRes / res);
                    for (int x = 0; x < res; x++)
                    {
                        int sx = PsInt((double)x * sRes / res);
                        int best = 0, bestV = 40;        // base wins unless a layer beats this
                        foreach (var c in chan)
                        {
                            int v = c.Data[18 + ((sy * sRes) + sx) * 4 + c.O];
                            if (v > bestV) { bestV = v; best = c.L; }
                        }
                        counts[best]++;
                        bmp.SetPixel(x, y, cols[best]);
                    }
                }
                string dir = Path.GetDirectoryName(outPng);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                bmp.Save(outPng, ImageFormat.Png);
            }

            var layers = j.GetProperty("stratumLayers");
            var names = new string[9];
            names[0] = "(base)";
            for (int i = 0; i < 8; i++)
                names[i + 1] = StripAlbedo(layers[i + 1].GetProperty("albedo").GetProperty("path").GetString());
            int tot = res * res;
            log(string.Format("{0}   splat {1}x{1}", leaf, sRes));
            for (int i = 0; i < 9; i++)
            {
                if (counts[i] == 0) continue;
                log(string.Format("  {0}  {1,-34} {2,6:P1}  rgb({3},{4},{5})",
                    i, names[i], (double)counts[i] / tot, cols[i].R, cols[i].G, cols[i].B));
            }
            log(outPng);
            return 0;
        }
    }
}
