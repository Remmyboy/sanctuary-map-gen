namespace SanctuaryMapConverter.Core
{
    // The whole of src/Biomes.ps1: lighting/fog per biome for every map, plus
    // the generator-side stratum tables. Converted maps take their ground from
    // the source and use only the lighting half; generated maps also take
    // their nine stratum layers from here.
    //
    // BuildLayers gives every slot a fixed meaning, and a biome table that
    // does not respect it produces exactly the sort of nonsense that once sent
    // us looking for a shader bug:
    //
    //   0 base   1 cliff   2 mid slope (VEGETATION, NEVER ROCK)
    //   3 upper slope   4 variation   5 mud   6 sand   7 rock   8 gravel
    public sealed class Biome
    {
        public string Name;
        public double SunRA, SunTemp, Sky, Exposure, Fog;
        public string[] Layers;

        public static Biome Get(string key) => key switch
        {
            "Highlands" => new Biome
            {
                Name = key, SunRA = 96.2, SunTemp = 9200, Sky = 12000, Exposure = 11.5, Fog = 330,
                Layers = new[] { "highlands_100m_grass07", "highlands_60m_rock_basalt01", "highlands_100m_heather03",
                    "highlands_100m_grass03", "highlands_100m_grass02", "highlands_100m_mud02",
                    "highlands_100m_sand02", "highlands_100m_rock_cliff01", "highlands_60m_gravel01" },
            },
            "Winter" => new Biome
            {
                // Highland textures, not the dedicated Winter set: that set
                // ships albedos with no matching _normal and three textures
                // with no _mask, so a table built from it references nine
                // assets that do not exist.
                Name = key, SunRA = 140.0, SunTemp = 11500, Sky = 14000, Exposure = 12.2, Fog = 220,
                Layers = new[] { "highlands_100m_snow01", "highlands_60m_rock_basalt01", "highlands_100m_heather03",
                    "highlands_100m_groundrock_02", "highlands_100m_grass01", "highlands_100m_mud01",
                    "highlands_100m_sand01", "highlands_100m_rock_cliff01", "highlands_60m_gravel01" },
            },
            "Evergreen" => new Biome
            {
                Name = key, SunRA = 110.0, SunTemp = 7600, Sky = 11000, Exposure = 11.6, Fog = 300,
                Layers = new[] { "02_Evergreen/grass", "highlands_60m_rock_basalt01", "highlands_100m_moss01",
                    "highlands_100m_grass01", "highlands_100m_heather03", "highlands_100m_mud01",
                    "highlands_100m_sand02", "02_Evergreen/rock", "highlands_60m_gravel01" },
            },
            "Arid" => new Biome
            {
                // The shipped desert stratum set, all warm tones, so nothing
                // in the ramp fights the sand.
                Name = key, SunRA = 118.0, SunTemp = 7000, Sky = 10500, Exposure = 11.9, Fog = 460,
                Layers = new[] { "10_WhiteDesert/desert_100m_sand_01", "10_WhiteDesert/desert_100m_rock_01",
                    "10_WhiteDesert/desert_100m_sand_03", "10_WhiteDesert/desert_100m_sandstone_01",
                    "10_WhiteDesert/desert_100m_sand_02", "10_WhiteDesert/desert_100m_ground_foliage_04",
                    "10_WhiteDesert/desert_100m_sand_01", "10_WhiteDesert/desert_100m_rock_01",
                    "10_WhiteDesert/desert_100m_sandstone_01" },
            },
            _ => new Biome
            {
                Name = "Tropical", SunRA = 88.0, SunTemp = 8200, Sky = 13000, Exposure = 11.3, Fog = 280,
                Layers = new[] { "highlands_100m_grass07", "highlands_60m_rock_basalt01", "highlands_100m_moss01",
                    "highlands_100m_grass02", "highlands_100m_marsh01", "highlands_100m_mud02",
                    "highlands_100m_sand02", "highlands_100m_rock_cliff01", "highlands_60m_gravel01" },
            },
        };

        // Measured mean luminance of each stratum albedo (Show-TextureTones).
        // diffuseRemap is computed as target tone / texture tone, so a bright
        // texture gets pulled down hard and a dark one barely at all.
        static readonly Dictionary<string, int> TextureLum = new()
        {
            ["highlands_100m_marsh01"] = 49,
            ["highlands_100m_grass01"] = 61,
            ["highlands_50m_heather01"] = 68,
            ["highlands_100m_rock_cliff02"] = 75,
            ["highlands_60m_rock_basalt01"] = 75,
            ["highlands_100m_grass03"] = 83,
            ["highlands_100m_moss01"] = 87,
            ["highlands_100m_mud01"] = 93,
            ["highlands_100m_mud02"] = 99,
            ["highlands_100m_grass02"] = 103,
            ["highlands_100m_rock_cliff01"] = 103,
            ["highlands_100m_rock_sandstone02"] = 106,
            ["highlands_100m_heather03"] = 110,
            ["highlands_100m_grass07"] = 118,
            ["highlands_100m_groundrock_02"] = 122,
            ["highlands_100m_gravel02"] = 127,
            ["highlands_100m_sand02"] = 130,
            ["highlands_60m_gravel01"] = 140,
            ["highlands_100m_snow01"] = 171,
            ["highlands_100m_sand01"] = 177,
            ["highlands_100m_rock_sandstone01"] = 179,
            ["highlands_100m_sand05"] = 183,
            ["10_WhiteDesert/desert_100m_ground_foliage_04"] = 69,
            ["10_WhiteDesert/desert_100m_rock_01"] = 89,
            ["10_WhiteDesert/desert_100m_sand_03"] = 106,
            ["10_WhiteDesert/desert_100m_sandstone_01"] = 120,
            ["10_WhiteDesert/desert_100m_sand_02"] = 124,
            ["10_WhiteDesert/desert_100m_sand_01"] = 130,
            ["Winter/rock"] = 65,
            ["Winter/moss_dry"] = 78,
            ["Winter/dirt_c"] = 112,
            ["Winter/dirt_a"] = 152,
            ["Winter/snow_plain_darker"] = 192,
            ["Winter/dirt_d"] = 193,
            ["Winter/dirt_b"] = 224,
            ["Winter/snow_plain"] = 226,
            ["02_Evergreen/grass"] = 57,
            ["02_Evergreen/rock"] = 68,
        };

        // Effective brightness each slot should land on, read off the shipped
        // maps: ~30 for ground cover, ~45 for rock and cliff.
        static readonly int[] SlotTargetTone = { 38, 45, 32, 44, 40, 34, 36, 45, 44 };

        static JObj GetDiffuseRemap(string texture, int slot)
        {
            int lum = TextureLum.TryGetValue(texture, out int v) ? v : 125;
            double k = Math.Max(0.15, Math.Min(0.90, (double)SlotTargetTone[slot] / lum));
            // A touch warmer on red, cooler on blue, so ground reads as earth
            // rather than flat grey.
            return Json.Rgba(Math.Round(k * 1.06, 3), Math.Round(k, 3), Math.Round(k * 0.90, 3), 1.0);
        }

        // "Winter/rock" resolves into the Winter stratum set, a bare name into
        // 01_Highlands.
        static string ResolveLayerPath(string t)
        {
            int slash = t.IndexOf('/');
            if (slash > 0) return "Environment/" + t.Substring(0, slash) + "/Stratum/" + t.Substring(slash + 1);
            return "Environment/01_Highlands/Stratum/" + t;
        }

        // Per-slot tile size, far blend and remap follow the roles on
        // ~TEAM-1v1_Tropical_256_47940: ground cover darkened, rock left
        // brighter, far-tile blend up on the layers that want close detail.
        public static List<JObj> NewStratumLayers(string biome)
        {
            var b = Get(biome);
            //                         tile  far  triPlan  nScale  nFNB
            double[][] role =
            {
                new[] { 10.0, 64.0, 12.0, 1.00, 0.00 },   // 0 base
                new[] { 12.0, 52.0, 10.0, 1.00, 0.16 },   // 1 cliff
                new[] {  8.0, 32.0, 12.0, 1.00, 0.50 },   // 2 mid slope
                new[] {  8.0, 64.0, 12.0, 1.00, 0.32 },   // 3 upper slope
                new[] {  8.0, 32.0, 12.0, 1.00, 0.53 },   // 4 variation
                new[] {  8.0, 40.0, 12.0, 1.00, 0.57 },   // 5 mud
                new[] { 10.0, 32.0,  8.0, 0.80, 0.06 },   // 6 sand
                new[] { 12.0, 52.0, 10.0, 1.00, 0.16 },   // 7 rock
                new[] { 12.0, 52.0, 10.0, 1.00, 0.16 },   // 8 gravel
            };

            var outLayers = new List<JObj>();
            for (int i = 0; i < 9; i++)
            {
                string p = ResolveLayerPath(b.Layers[i]);
                double[] r = role[i];
                outLayers.Add(Json.Obj(
                    ("name", null),
                    ("albedo", Json.Obj(("path", p + "_albedo.tga"))),
                    ("normal", Json.Obj(("path", p + "_normal.tga"))),
                    ("mask", Json.Obj(("path", p + "_mask.tga"))),
                    ("tileSize", Json.Obj(("x", r[0]), ("y", r[0]))),
                    ("tileSizeFar", Json.Obj(("x", r[1]), ("y", r[1]))),
                    ("tileSizeTriplanar", r[2]),
                    ("tileSizeFarTriplanar", 36.0),
                    ("normalScale", r[3]), ("normalScaleFar", 1.0),
                    ("normalFarNearBlend", r[4]), ("heightFarNearBlend", 0.5),
                    ("diffuseRemap", GetDiffuseRemap(b.Layers[i], i)),
                    ("farColorRemap", Json.Rgba(1.0, 1.0, 1.0, 0.0)),
                    ("maskRemapMin", Json.Quat(0.0, 0.0, 0.0, 0.0)),
                    ("maskRemapMax", Json.Quat(1.0, 1.0, 1.0, 1.0))));
            }
            return outLayers;
        }

        /// The fields the shipped maps set that SanMap would otherwise default
        /// badly - most importantly the height fog band, which follows the
        /// water level.
        public static void AddEnvironment(JObj map, double waterLevel)
        {
            double lo = waterLevel > 0 ? waterLevel : 0.0;
            map["waterWindShoreWavesRemap"] = 0.5;
            map["waterShoreGeneratorBlueprint"] = "";

            map["backgroundFogIntensity"] = 0.425;
            map["backgroundFogRange"] = 1024.0;
            map["backgroundFogMinimum"] = 0.1;
            map["backgroundSkyColorIntensity"] = 0.52;
            map["backgroundColorIntensity"] = 1.0;
            map["backgroundColor"] = Json.Rgba(1.319508, 1.319508, 1.319508, 1.0);
            map["backgroundColorFadeoutRange"] = 15000.0;
            map["backgroundColorFadeoutPower"] = 0.2;

            map["heightFogIntensity"] = 0.195;
            map["heightFogRange"] = Json.Obj(("x", lo), ("y", lo + 45.0));
            map["heightFogStart"] = -10.0;
            map["heightFogEnd"] = 500.0;
            map["heightFogPower"] = 6.0;

            map["linearFogIntensity"] = 0.167;
            map["linearFogStart"] = 100.0;
            map["linearFogEnd"] = 5000.0;
            map["linearFogPower"] = 1.0;
            map["linearFogCameraIntensity"] = 0.0;
            map["linearFogCameraStart"] = 500.0;
            map["linearFogCameraEnd"] = 5000.0;

            map["sunPosition"] = Json.Vec3(512.0, 512.0, -130.0);
            map["sunCookie"] = Json.Obj(("path", ""));
            map["sunCookieSize"] = Json.Obj(("x", 512.0), ("y", 512.0));
            map["skyboxRotation"] = 232.0;
            map["skyboxIntensityMode"] = "Exposure";
            map["skyboxMultiplier"] = 1.0;
            map["skyboxLuxValue"] = 30000.0;
        }
    }
}
