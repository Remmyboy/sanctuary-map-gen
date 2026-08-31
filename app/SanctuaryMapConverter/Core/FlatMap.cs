using System.Text;
using System.Text.RegularExpressions;

namespace SanctuaryMapConverter.Core
{
    // New-SanctuaryMap.ps1 in C#. Creates a new, perfectly flat Sanctuary:
    // Shattered Sun map from scratch.
    //
    // The demo's map editor has no working "New Map" command (the button
    // exists in the scene but has no onClick handler, and MapEditorMenu has
    // no New() method). This writes a complete, loadable map folder directly:
    //
    //     <MapsRoot>\<Folder>\
    //         <Folder>.sanmap          JSON, fileVersion 3
    //         Textures\heightmap.raw   (Size+1)^2 uint16 LE, constant
    //         Textures\stratums_1_4.tga  Size*2 square, 32bpp BGRA, all zero
    //         Textures\stratums_5_8.tga  ditto
    //         Textures\tint_colors.tga   max(2048,Size*2) square, BGRA 128/128/128/0
    //         Textures\tint_geometry.tga ditto, BGRA 255/128/128/255 (flat normal)
    //
    // Formats were derived from EM.Map.SanMap / EM.Gamedata.Load in the
    // shipped assemblies and cross-checked against the four bundled maps.
    public sealed class FlatMapOptions
    {
        public string Name;               // mandatory
        public string MapsRoot;           // null: the caller resolves the editor maps tree

        // Playable extent in metres. Bundled maps use 512 / 1024 / 2048.
        public int Size = 512;            // 256, 512, 1024 or 2048

        // Full vertical range of the heightmap in metres (raw 65535 == this
        // height). SanMap.height is an int field, so this must stay whole.
        public int MaxHeight = 128;

        // Where the flat plane sits, in metres. Default = 25% of MaxHeight,
        // which leaves headroom to both raise and lower terrain later.
        public double FlatHeight = -1;

        public bool Force;

        // Replays the game's own deserialisation, standing in for the
        // script's Test-Sanmap.ps1 step; null skips it. (The PS ran it with
        // -Path $sanmap -CheckTextures, so set CheckTextures = true to match.)
        public ValidateOptions Validate;
    }

    public static class FlatMap
    {
        // ------------------------------------------------------- writers ---

        static void WriteTga(string path, int width, int height,
            byte b, byte g, byte r, byte a)
        {
            // 18-byte header, image type 2 (uncompressed true-colour), 32bpp,
            // no footer. Matches the bundled maps byte for byte, including
            // descriptor 0.
            var hdr = new byte[18];
            hdr[2] = 2;
            BitConverter.GetBytes((ushort)width).CopyTo(hdr, 12);
            BitConverter.GetBytes((ushort)height).CopyTo(hdr, 14);
            hdr[16] = 32;

            var row = new byte[width * 4];          // TGA stores BGRA
            for (int x = 0; x < width; x++)
            {
                int o = x * 4;
                row[o] = b; row[o + 1] = g; row[o + 2] = r; row[o + 3] = a;
            }

            using var fs = File.Create(path);
            fs.Write(hdr, 0, hdr.Length);
            for (int y = 0; y < height; y++) fs.Write(row, 0, row.Length);
        }

        static void WriteHeightmapRaw(string path, int resolution, ushort value)
        {
            // Load.ReadRaw: Resolution^2 uint16, little-endian,
            // value/65535 * map.height.
            var row = new byte[resolution * 2];
            byte[] pair = BitConverter.GetBytes(value);
            for (int x = 0; x < resolution; x++)
            {
                row[x * 2] = pair[0]; row[x * 2 + 1] = pair[1];
            }

            using var fs = File.Create(path);
            for (int y = 0; y < resolution; y++) fs.Write(row, 0, row.Length);
        }

        // --------------------------------------------------- stratum set ---

        static JObj NewStratumLayer(string baseName, double[] tile, double[] tileFar,
            double nrmScale, double nrmScaleFar, double[] diffuse,
            double[] farRemap, double[] maskMin, double[] maskMax)
        {
            string p = !string.IsNullOrEmpty(baseName)
                ? $"Environment/01_Highlands/Stratum/{baseName}" : "";
            return Json.Obj(
                ("name", null),
                ("albedo", Json.Obj(("path", p != "" ? $"{p}_albedo.tga" : ""))),
                ("normal", Json.Obj(("path", p != "" ? $"{p}_normal.tga" : ""))),
                ("mask", Json.Obj(("path", p != "" ? $"{p}_mask.tga" : ""))),
                ("tileSize", Json.Obj(("x", tile[0]), ("y", tile[1]))),
                ("tileSizeFar", Json.Obj(("x", tileFar[0]), ("y", tileFar[1]))),
                ("tileSizeTriplanar", 12.0),
                ("tileSizeFarTriplanar", 36.0),
                ("normalScale", nrmScale),
                ("normalScaleFar", nrmScaleFar),
                ("normalFarNearBlend", 0.5),
                ("heightFarNearBlend", 0.5),
                ("diffuseRemap", Json.Rgba(diffuse[0], diffuse[1], diffuse[2], diffuse[3])),
                ("farColorRemap", Json.Rgba(farRemap[0], farRemap[1], farRemap[2], farRemap[3])),
                ("maskRemapMin", Json.Quat(maskMin[0], maskMin[1], maskMin[2], maskMin[3])),
                ("maskRemapMax", Json.Quat(maskMax[0], maskMax[1], maskMax[2], maskMax[3])));
        }

        // ----------------------------------------------- markers & armies ---

        static JObj NewTransform(double x, double y, double z) => Json.Obj(
            ("position", Json.Vec3(x, y, z)),
            ("rotation", Json.Quat(0.0, 0.0, 0.0, 1.0)),
            ("scale", Json.Vec3(1.0, 1.0, 1.0)));

        static JObj NewArmy(int faction, string tpid, string unitKey) => Json.Obj(
            ("faction", faction),
            ("alloys", 100.0),
            ("energy", 1000.0),
            ("groups", Json.Obj(
                ("Initial", Json.Obj(
                    ("units", Json.Obj(
                        (unitKey, Json.Obj(
                            ("type", "Unit"),
                            ("tpid", tpid),
                            ("position", Json.Vec3(0.0, 0.0, 0.0)),
                            ("rotation", Json.Quat(0.0, 0.0, 0.0, 0.0)),
                            ("scale", Json.Vec3(1.0, 1.0, 1.0)))))),
                    ("groups", Json.Obj()))))));

        public static string Run(FlatMapOptions o, Action<string> log)
        {
            // param(): [Parameter(Mandatory)] Name, [ValidateSet(256, 512,
            // 1024, 2048)] Size.
            if (string.IsNullOrEmpty(o.Name)) throw new Exception("Name is required.");
            if (o.Size != 256 && o.Size != 512 && o.Size != 1024 && o.Size != 2048)
                throw new Exception($"Size must be 256, 512, 1024 or 2048 (got {o.Size}).");

            double flatHeight = o.FlatHeight;
            if (flatHeight < 0) flatHeight = o.MaxHeight * 0.25;
            if (flatHeight > o.MaxHeight)
                throw new Exception($"FlatHeight ({flatHeight}) exceeds MaxHeight ({o.MaxHeight}).");

            string folder = Regex.Replace(Regex.Replace(o.Name, @"[^\w\- ]", ""), @"\s+", "_");
            if (folder.Length == 0)
                throw new Exception($"Name '{o.Name}' produced an empty folder name.");

            string mapDir = Path.Combine(o.MapsRoot, folder);
            string texDir = Path.Combine(mapDir, "Textures");

            if (Directory.Exists(mapDir))
            {
                if (!o.Force) throw new Exception($"'{mapDir}' already exists. Pass -Force to overwrite.");
                Directory.Delete(mapDir, true);
            }
            Directory.CreateDirectory(texDir);

            // ----------------------------------------------------- textures ---

            int hmRes = o.Size + 1;
            int splatRes = o.Size * 2;
            int tintRes = Math.Max(2048, o.Size * 2);
            ushort rawValue = (ushort)Math.Round(flatHeight / o.MaxHeight * 65535);

            log($"Generating '{o.Name}' -> {mapDir}");
            log($"  terrain      {o.Size}x{o.Size} m, vertical range 0..{o.MaxHeight} m");
            log($"  flat plane   {flatHeight} m  (raw {rawValue} / 65535)");

            WriteHeightmapRaw(Path.Combine(texDir, "heightmap.raw"), hmRes, rawValue);
            log($"  heightmap.raw       {hmRes}x{hmRes} uint16");

            // All splat weights zero -> only stratum layer 0 (the base) is
            // visible.
            WriteTga(Path.Combine(texDir, "stratums_1_4.tga"), splatRes, splatRes, 0, 0, 0, 0);
            WriteTga(Path.Combine(texDir, "stratums_5_8.tga"), splatRes, splatRes, 0, 0, 0, 0);
            log($"  stratums_*.tga      {splatRes}x{splatRes} (blank)");

            // tint_colors: RGB 128 grey is the neutral point (Two_Step_Shuffle,
            // the least art-directed bundled map, averages RGB 131/123/110).
            // Alpha is the hole mask; two of the three other maps are alpha 0
            // everywhere, so 0 == no holes.
            WriteTga(Path.Combine(texDir, "tint_colors.tga"), tintRes, tintRes, 128, 128, 128, 0);

            // tint_geometry: flat tangent-space normal, RGB 128/128/255,
            // alpha 255.
            WriteTga(Path.Combine(texDir, "tint_geometry.tga"), tintRes, tintRes, 255, 128, 128, 255);
            log($"  tint_*.tga          {tintRes}x{tintRes} (neutral)");

            // -------------------------------------------------- stratum set ---

            // Layer 0 is the base (visible everywhere while the splatmaps are
            // blank); 1-4 give the texture-paint tab something to work with;
            // 5-8 are free slots.
            var stratums = new List<JObj>
            {
                NewStratumLayer("highlands_100m_sand01",           new[] { 8.0, 8.0 },   new[] { 50.0, 50.0 },   1.5, 1.0,
                    new[] { 0.13, 0.121939994, 0.1144, 1.0 },                new[] { 0.0, 0.0, 0.0, 0.0 },                         new[] { 0.0, 0.0, 0.0, 0.0 }, new[] { 1.0, 1.0, 1.5, 1.0 }),
                NewStratumLayer("highlands_100m_rock_sandstone02", new[] { 10.0, 10.0 }, new[] { 64.0, 64.0 },   1.0, 0.2,
                    new[] { 0.19, 0.16669333, 0.1596, 1.0 },                 new[] { 0.0, 0.0, 0.0, 0.0 },                         new[] { 0.0, 0.0, 0.1, 0.0 }, new[] { 1.0, 1.0, 0.9, 1.0 }),
                NewStratumLayer("highlands_100m_grass02",          new[] { 8.0, 8.0 },   new[] { 110.0, 110.0 }, 1.5, 0.5,
                    new[] { 0.5399167, 0.55, 0.495, 1.0 },                   new[] { 0.0, 0.0, 0.0, 0.0 },                         new[] { 0.0, 0.0, 0.0, 0.0 }, new[] { 1.0, 1.0, 1.0, 1.0 }),
                NewStratumLayer("highlands_100m_mud01",            new[] { 10.0, 10.0 }, new[] { 64.0, 64.0 },   0.5, 0.5,
                    new[] { 0.0899999961, 0.0872999951, 0.0872999951, 1.0 }, new[] { 0.3584906, 0.3584906, 0.3584906, 0.0 },       new[] { 0.0, 0.0, 0.0, 0.0 }, new[] { 1.0, 1.0, 1.0, 1.0 }),
                NewStratumLayer("highlands_100m_rock_sandstone02", new[] { 12.0, 12.0 }, new[] { 52.0, 52.0 },   1.0, 0.0,
                    new[] { 0.5, 0.5, 0.5, 1.0 },                            new[] { 1.0, 1.0, 1.0, 0.0 },                         new[] { 0.0, 0.0, 0.0, 0.0 }, new[] { 1.0, 1.0, 1.0, 1.0 }),
            };
            for (int i = 5; i <= 8; i++)
                stratums.Add(NewStratumLayer("", new[] { 1.0, 1.0 }, new[] { 1.0, 1.0 }, 1.0, 1.0,
                    new[] { 0.5, 0.5, 0.5, 1.0 }, new[] { 1.0, 1.0, 1.0, 1.0 }, new[] { 0.0, 0.0, 0.0, 0.0 }, new[] { 1.0, 1.0, 1.0, 1.0 }));

            // -------------------------------------------- markers & armies ---

            // 180-degree rotational symmetry about the map centre. The editor
            // has no marker UI in this build, so these are the starting layout
            // you edit in JSON.
            double q = o.Size / 4.0;                // quarter, e.g. 128 on a 512 map
            double c = o.Size / 2.0;                // centre

            var spawn = Json.Obj(
                ("Army_1", NewTransform(q, flatHeight, q)),
                ("Army_2", NewTransform(o.Size - q, flatHeight, o.Size - q)));

            int[][] alloyOffsets = { new[] { -16, -16 }, new[] { 16, -16 }, new[] { -16, 16 }, new[] { 16, 16 } };
            var alloys = new JObj();
            int n = 0;
            foreach (var off in alloyOffsets)
            {
                n++; alloys.Add(string.Format("Alloys_{0:D3}", n), NewTransform(q + off[0], flatHeight, q + off[1]));
            }
            foreach (var off in alloyOffsets)
            {
                n++; alloys.Add(string.Format("Alloys_{0:D3}", n), NewTransform(o.Size - q - off[0], flatHeight, o.Size - q - off[1]));
            }
            n++; alloys.Add(string.Format("Alloys_{0:D3}", n), NewTransform(c - 48, flatHeight, c - 48));
            n++; alloys.Add(string.Format("Alloys_{0:D3}", n), NewTransform(c + 48, flatHeight, c + 48));

            // --------------------------------------------------------- json ---

            var map = Json.Obj(
                ("fileVersion", 3),
                ("mapVersion", 1),
                ("name", o.Name),
                ("credits", ""),
                ("width", o.Size),
                ("length", o.Size),
                ("height", o.MaxHeight),               // SanMap.height is an int
                ("heightmapResolution", hmRes),
                ("hasWater", false),
                ("waterLevel", 0.0),
                ("waterDepth", 0.0),
                ("waterWindSpeed", 0.25),
                ("waterWindDirection", 160.0),
                ("waterShoreDepthOffset", 8.0),
                ("waterShoreDepthStrength", 0.7),
                ("waterShoreDistanceOffset", 0.0),
                ("waterShoreDistanceStrength", 2.0),
                ("waveGeneratorBlueprint", ""),
                ("shader", "RTS/TerrainLit"),
                ("heightTransition", 2.0),
                ("fadeDistance", 128.0),
                ("fadeStartDistance", 1.0),
                ("stratumLayers", stratums),

                ("sunRA", 128.0),
                ("sunDA", 42.0),
                ("sunIntensity", 60000.0),
                ("sunTint", Json.Rgba(1.0, 1.0, 1.0, 1.0)),
                ("sunTemperature", 5800.0),
                ("sunAngularDiameter", 0.5),
                ("sunVolumetricsMultiplier", 6.7),
                ("sunVolumetricsShadowDimer", 0.5),
                ("skylightIntensity", 0.0),
                ("skylightTint", Json.Rgba(1.0, 1.0, 1.0, 1.0)),
                ("skylightTemperature", 10000.0),
                ("exposure", 12.0),
                ("exposureCompensation", 0.0),
                ("skyboxExposure", 12.0),
                ("fogAttenuationDistance", 350.0),
                ("fogBaseHeight", 10.0),
                ("fogMaximumHeight", 100.0),
                ("fogMaximumDistance", 1500.0),
                ("fogAnisotropy", 0.58),
                ("skybox", Json.Obj(("path", "empty"))),

                ("areas", Json.Obj(("Playable", Json.Obj(
                    ("x", 0.0), ("y", 0.0), ("width", (double)o.Size), ("height", (double)o.Size))))),
                ("armies", Json.Obj(
                    ("Army_1", NewArmy(1, "ues1601", "Unit_001")),
                    ("Army_2", NewArmy(2, "ucl3001", "Unit_002")))),
                ("chains", Json.Obj()),
                ("markers", Json.Obj(
                    ("Spawn", Json.Obj(("resource", false), ("transforms", spawn))),
                    ("Alloys", Json.Obj(("resource", true), ("transforms", alloys))))),
                ("decals", new List<JObj>()),
                ("windSpeed", 0.1),
                ("windDirection", 160.0),
                ("props", new List<JObj>()));

            string sanmap = Path.Combine(mapDir, folder + ".sanmap");
            File.WriteAllText(sanmap, Json.Write(map), new UTF8Encoding(false));

            log($"  {folder}.sanmap      {new FileInfo(sanmap).Length} bytes");
            log("");

            // Replay the game's own deserialisation before claiming this is
            // loadable. (The PS script ran Test-Sanmap.ps1 -Path $sanmap
            // -CheckTextures here.)
            if (o.Validate != null) Validator.Check(sanmap, o.Validate, log);

            log("");
            log($"Done. In the editor: File > Open > {sanmap}");
            return mapDir;
        }
    }
}
