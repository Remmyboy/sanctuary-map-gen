// The author's playable area and lighting, adopted into Sanctuary's terms.
//
// Both are best-effort translations rather than copies: the playable area is a
// gameplay rectangle that survives the frame change intact, while the lighting
// crosses two different renderers (SupCom's fixed-function sun vs Unity HDRP's
// physical sky) and only the quantities with a physical meaning on both sides
// carry over - sun direction, warmth, brightness, fog thickness. Everything
// else stays with the biome.
public static partial class MapGen
{
    public class ScArea
    {
        public string Name = "";
        public float X0, Z0, X1, Z1;             // SupCom frame, z southward
    }

    // ---- playable area ---------------------------------------------------

    /// The Areas table from _save.lua. The marker regex cannot see these -
    /// an area block has a rectangle, not a type and position.
    public static List<ScArea> ReadScAreas(string saveLuaPath)
    {
        string t = File.ReadAllText(saveLuaPath);
        var found = new List<ScArea>();
        var re = new System.Text.RegularExpressions.Regex(
            @"\['(?<name>[^']+)'\]\s*=\s*\{\s*\['rectangle'\]\s*=\s*RECTANGLE\(\s*" +
            @"(?<a>[-\d.eE+]+)\s*,\s*(?<b>[-\d.eE+]+)\s*,\s*(?<c>[-\d.eE+]+)\s*,\s*(?<d>[-\d.eE+]+)",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        foreach (System.Text.RegularExpressions.Match mm in re.Matches(t))
        {
            float F(string g) => float.Parse(mm.Groups[g].Value, System.Globalization.CultureInfo.InvariantCulture);
            found.Add(new ScArea { Name = mm.Groups["name"].Value, X0 = F("a"), Z0 = F("b"), X1 = F("c"), Z1 = F("d") });
        }
        return found;
    }

    /// The playable rectangle in Sanctuary's frame - {x, z, width, height} -
    /// or null to use the full map.
    ///
    /// AREA_1 is the convention for "the playable area", but the corpus holds
    /// every way the table can lie, so a rectangle is adopted only when it
    /// passes three guards:
    ///
    ///   * sane: at least 16 m on a side once clamped to the map
    ///     (adaptive_corona writes RECTANGLE(0,0,0,0));
    ///   * substantial: at least a quarter of the map's area. Survival maps
    ///     such as final_rush define a 50 m starting box that a script grows
    ///     at run time - adopting it would shrink the map to a postage stamp;
    ///   * consistent: every spawn inside it, with a metre of slack. A
    ///     rectangle the spawns ignore is scripting scenery, not a boundary.
    ///
    /// The z flip matches AdoptScMap: SupCom z grows southward, ours grows
    /// northward, so the rectangle's z range is mirrored about the map centre.
    public static float[] ScPlayableArea(ScMapInfo m, List<ScArea> areas, List<ScMarker> markers)
    {
        if (areas == null || areas.Count == 0) return null;
        ScArea a = areas.Find(x => x.Name == "AREA_1") ?? areas[0];

        float size = m.Size;
        float x0 = Math.Max(0f, Math.Min(a.X0, a.X1)), x1 = Math.Min(size, Math.Max(a.X0, a.X1));
        float z0 = Math.Max(0f, Math.Min(a.Z0, a.Z1)), z1 = Math.Min(size, Math.Max(a.Z0, a.Z1));
        if (x1 - x0 < 16f || z1 - z0 < 16f) return null;
        if ((x1 - x0) * (z1 - z0) < 0.25f * size * size) return null;

        foreach (var k in markers)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(k.Name, "^ARMY_\\d+$")) continue;
            if (k.X < x0 - 1f || k.X > x1 + 1f || k.Z < z0 - 1f || k.Z > z1 + 1f) return null;
        }

        // Nothing inset after clamping: the full map, so say so.
        if (x0 < 1f && z0 < 1f && x1 > size - 1f && z1 > size - 1f) return null;

        return new[] { x0, size - z1, x1 - x0, z1 - z0 };
    }

    // ---- macro overlay ---------------------------------------------------

    // Supreme Commander's UpperStratum is a low-opacity texture (mean alpha
    // ~8-12%) tiled large - median 128 m per repeat - and alpha-blended over
    // the composited ground as its final large-scale variation pass.
    // Sanctuary has no tenth layer, but it has tint_colors: a per-texel colour
    // multiply over the whole terrain. Baking the overlay into the tint
    // carries the author's macro variation at better resolution than the
    // original rendered at.
    //
    // Source-texture mode only: the bake copies GPG pixels into the tint, and
    // a CC0 build must ship none.
    static byte[] MacroBgra;                     // decoded overlay, or null
    static int    MacroW, MacroH;
    static float  MacroScale = 128f;             // metres per repeat

    /// Decode the overlay and hold it for WriteTints. False - with the tint
    /// left procedural - when the texture is undecodable, degenerate, or too
    /// transparent to have ever been visible.
    public static bool AdoptScMacro(byte[] ddsBytes, float scaleMetres)
    {
        MacroBgra = null;
        if (scaleMetres < 8f || scaleMetres > 4096f) return false;
        var px = DecodeDdsToBgra(ddsBytes, out int w, out int h);
        if (px == null || w < 4 || h < 4) return false;

        long sumA = 0;
        for (int i = 3; i < px.Length; i += 4) sumA += px[i];
        if (sumA / (px.Length / 4) < 2) return false;

        MacroBgra = px; MacroW = w; MacroH = h; MacroScale = scaleMetres;
        return true;
    }

    /// Per-channel multiply factors at a world position, bilinear with wrap.
    ///
    /// The original blend is lerp(albedo, macro.rgb, macro.a); as a multiply
    /// that is lerp(1, macro.rgb / albedo, macro.a). The albedo under any
    /// texel is unknown here, so the mid tone the stratum remaps aim for
    /// (0.37/0.35/0.32) stands in for it, and the factor is bounded so a
    /// near-black overlay pixel dims the ground rather than deleting it.
    /// Supreme Commander mask/texture v = 0 is world z = 0 and the import
    /// negates z, matching AdoptScSplat.
    static void SampleMacro(float wx, float wz, out float fb, out float fg, out float fr)
    {
        float u = wx / MacroScale * MacroW - 0.5f;
        float v = (MapSize - wz) / MacroScale * MacroH - 0.5f;
        int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
        float tx = u - x0, ty = v - y0;

        float b = 0, g = 0, r = 0, a = 0;
        for (int dy = 0; dy <= 1; dy++)
            for (int dx = 0; dx <= 1; dx++)
            {
                int xx = ((x0 + dx) % MacroW + MacroW) % MacroW;
                int yy = ((y0 + dy) % MacroH + MacroH) % MacroH;
                int o = (yy * MacroW + xx) * 4;
                float wgt = (dx == 0 ? 1 - tx : tx) * (dy == 0 ? 1 - ty : ty);
                b += MacroBgra[o] * wgt; g += MacroBgra[o + 1] * wgt;
                r += MacroBgra[o + 2] * wgt; a += MacroBgra[o + 3] * wgt;
            }

        float al = a / 255f;
        fb = Math.Clamp(1f + al * (b / 255f / 0.32f - 1f), 0.55f, 1.45f);
        fg = Math.Clamp(1f + al * (g / 255f / 0.35f - 1f), 0.55f, 1.45f);
        fr = Math.Clamp(1f + al * (r / 255f / 0.37f - 1f), 0.55f, 1.45f);
    }

    // ---- lighting --------------------------------------------------------

    /// Sun azimuth (sunRA) and altitude (sunDA) in degrees, from the source's
    /// sun vector with the import's z negation applied.
    ///
    /// Convention assumed for the engine: the sun light's rotation is
    /// Euler(sunDA, sunRA, 0), so the direction toward the sun is
    /// (-cos DA sin RA, sin DA, -cos DA cos RA). The altitude is clamped into
    /// the band the shipped maps use (15..30 degrees) - HDRP's exposure and
    /// the fixed skybox are tuned for a low sun, and a SupCom noon sun pushed
    /// through them just blows the terrain out. The azimuth is the signal
    /// worth carrying: it decides which side of a ridge holds the shadow.
    public static void ScSunAngles(ScMapInfo m, out double sunRA, out double sunDA)
    {
        double sx = m.SunDirection[0], sy = m.SunDirection[1], sz = m.SunDirection[2];
        double len = Math.Sqrt(sx * sx + sy * sy + sz * sz);
        if (len < 1e-4) { sunRA = 0; sunDA = 20; return; }
        sx /= len; sy /= len; sz /= len;
        if (sy < 0) { sx = -sx; sy = -sy; sz = -sz; }      // stored as travel direction
        sz = -sz;                                          // the import's z flip

        sunDA = Math.Clamp(Math.Asin(sy) * 180.0 / Math.PI, 15.0, 30.0);
        sunRA = Math.Atan2(-sx, -sz) * 180.0 / Math.PI;
        sunRA = Math.Round(sunRA, 1);
        sunDA = Math.Round(sunDA, 1);
    }

    /// Colour temperature in kelvin from the sun colour's red/blue balance,
    /// clamped to the shipped band (5000 warm .. 9800 cool). A neutral white
    /// lands at 6500.
    public static double ScSunTemperature(ScMapInfo m)
    {
        double r = m.SunColor[0], b = m.SunColor[2];
        if (r <= 1e-3 || b <= 1e-3) return -1;             // caller keeps the biome's
        double t = 6500.0 - 5000.0 * Math.Log2(r / b);
        return Math.Round(Math.Clamp(t, 5000.0, 9800.0));
    }

    /// Sun intensity in lux. The product of lightingMultiplier and the sun
    /// colour's luminance is the source's whole brightness dial; PIVOT is the
    /// corpus median of that product, so the typical map keeps the shipped
    /// 60000 and only genuinely dark maps drop below it.
    public const double ScLightPivot = 1.94;               // corpus median of the product
    public static double ScSunIntensity(ScMapInfo m)
    {
        double luma = 0.299 * m.SunColor[0] + 0.587 * m.SunColor[1] + 0.114 * m.SunColor[2];
        double product = m.LightingMultiplier * luma;
        if (product <= 1e-3) return 60000.0;
        return Math.Round(Math.Clamp(60000.0 * product / ScLightPivot, 25000.0, 60000.0));
    }

    /// Fog attenuation distance in metres from the source's fog band, or the
    /// biome's value when the source leaves fog off. SupCom's fogEnd is the
    /// distance at which fog saturates; HDRP's attenuation distance is where
    /// it reaches ~63%, so carry the band scaled down, clamped to the shipped
    /// range (24 = pea-soup White_Desert, 500 = clear).
    public static double ScFogAttenuation(ScMapInfo m, double biomeFog)
    {
        double band = m.FogEnd - m.FogStart;
        if (m.FogEnd <= 0f || band <= 1f) return biomeFog;
        return Math.Round(Math.Clamp(band * 0.63, 24.0, 500.0));
    }
}
