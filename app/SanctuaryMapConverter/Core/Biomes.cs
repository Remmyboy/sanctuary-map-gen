namespace SanctuaryMapConverter.Core
{
    // The lighting half of src/Biomes.ps1. Converted maps take their ground
    // from the source; lighting and fog still come from a biome, and the ~25
    // environment fields below exist because omitting them once left SanMap
    // defaulting the height fog into visible "holes" across every map.
    //
    // The biome texture fallback (New-StratumLayers) is not ported: the two
    // corpus maps whose texture blocks cannot be scanned - both pre-Forged
    // Alliance format - are refused with a clear message instead.
    public sealed class Biome
    {
        public string Name;
        public double SunRA, SunTemp, Sky, Exposure, Fog;

        public static Biome Get(string key) => key switch
        {
            "Highlands" => new Biome { Name = key, SunRA = 96.2, SunTemp = 9200, Sky = 12000, Exposure = 11.5, Fog = 330 },
            "Winter"    => new Biome { Name = key, SunRA = 140.0, SunTemp = 11500, Sky = 14000, Exposure = 12.2, Fog = 220 },
            "Evergreen" => new Biome { Name = key, SunRA = 110.0, SunTemp = 7600, Sky = 11000, Exposure = 11.6, Fog = 300 },
            "Arid"      => new Biome { Name = key, SunRA = 118.0, SunTemp = 6400, Sky = 10000, Exposure = 11.4, Fog = 300 },
            _           => new Biome { Name = "Tropical", SunRA = 88.0, SunTemp = 8200, Sky = 13000, Exposure = 11.3, Fog = 280 },
        };

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
