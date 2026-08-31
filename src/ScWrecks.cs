// Supreme Commander starting wreckage, converted to Sanctuary wreck props.
//
// 88 of the 300 corpus maps place wreckage - 9,361 wrecks, median 50 a map -
// as a ['WRECKAGE'] group of units inside an army's Units table in _save.lua.
// The Playtest build grew the receiving end: six wreck prop blueprints under
// Environment/Dev/Props/Units, tagged HARVESTABLE/WRECKAGE with harvest
// values, whose meshes form a clean size ladder from tank to experimental.
//
// There is no semantic mapping to make - Sanctuary's unit roster is its own -
// so every FA wreck maps to the nearest-size mesh on that ladder, sized and
// filtered by what the unit was: docs/unit-wrecks.csv carries each FA unit's
// mass cost and hitbox from the game's own blueprints. Mass decides whether a
// wreck is worth placing at all: every Sanctuary wreck blueprint is worth 100
// alloys (dev placeholder values), so a 2-mass wall wreck would be a goldmine
// - a third of all corpus wrecks are walls, and they are skipped.
public static partial class MapGen
{
    public class ScWreck
    {
        public string Type = "";
        public float X, Y, Z;                    // SupCom frame, as written
        public float Yaw;                        // radians about Y, SupCom frame
    }

    /// Every unit entry under a ['WRECKAGE'] group. The group is found by
    /// name and walked with a brace counter - the Units tables nest too
    /// deeply for a regex to hold the shape.
    public static List<ScWreck> ReadScWrecks(string saveLuaPath)
    {
        string t = File.ReadAllText(saveLuaPath);
        var outp = new List<ScWreck>();
        var entryRe = new System.Text.RegularExpressions.Regex(
            @"type\s*=\s*'(?<t>[^']+)'[^{}]*?Position\s*=\s*\{\s*(?<x>[-\d.eE+]+)\s*,\s*(?<y>[-\d.eE+]+)\s*,\s*(?<z>[-\d.eE+]+)\s*\}" +
            @"(?:[^{}]*?Orientation\s*=\s*\{\s*(?<rx>[-\d.eE+]+)\s*,\s*(?<ry>[-\d.eE+]+)\s*,\s*(?<rz>[-\d.eE+]+)\s*\})?",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        int at = 0;
        while ((at = t.IndexOf("['WRECKAGE']", at, StringComparison.Ordinal)) >= 0)
        {
            int open = t.IndexOf('{', at);
            if (open < 0) break;
            int depth = 0, p = open;
            for (; p < t.Length; p++)
            {
                if (t[p] == '{') depth++;
                else if (t[p] == '}' && --depth == 0) break;
            }
            string block = t.Substring(open, Math.Min(p, t.Length - 1) - open + 1);
            foreach (System.Text.RegularExpressions.Match m in entryRe.Matches(block))
            {
                float F(string g) => float.Parse(m.Groups[g].Value, System.Globalization.CultureInfo.InvariantCulture);
                outp.Add(new ScWreck
                {
                    Type = m.Groups["t"].Value.ToLowerInvariant(),
                    X = F("x"), Y = F("y"), Z = F("z"),
                    Yaw = m.Groups["ry"].Success ? F("ry") : 0f,
                });
            }
            at = p;
        }
        return outp;
    }

    /// docs/unit-wrecks.csv -> id -> { mass, sizeX, sizeZ }.
    public static Dictionary<string, float[]> LoadScUnitTable(string csvPath)
    {
        var d = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(csvPath))
        {
            var c = line.Split(',');
            if (c.Length < 4 || c[0] == "id") continue;
            float P(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
            d[c[0]] = new[] { P(c[1]), P(c[2]), P(c[3]) };
        }
        return d;
    }

    /// Minimum mass for a wreck to convert. Walls cost 2, a T1 tank 56; the
    /// gap is wide, so the exact number is uncritical.
    public const float ScWreckMinMass = 30f;

    /// The wreck mesh for a unit, or null to skip it. Chosen by hitbox area
    /// on a six-step ladder; the two mid-size meshes split by aspect - long
    /// hulls take the long mesh, square structures the square one. A unit
    /// missing from the table places the small mesh rather than vanishing.
    public static string ScWreckBlueprint(string type, Dictionary<string, float[]> table)
    {
        float mass = 50f, sx = 1f, sz = 1f;
        if (table != null && table.TryGetValue(type, out var u)) { mass = u[0]; sx = u[1]; sz = u[2]; }
        if (mass < ScWreckMinMass) return null;

        float area = sx * sz;
        float aspect = Math.Max(sx, sz) / Math.Max(0.01f, Math.Min(sx, sz));
        if (area <= 0.5f) return "uel1001";
        if (area <= 2.5f) return "uel2201";
        if (area <= 9f) return aspect > 1.3f ? "uel3001" : "ues1611";
        if (area <= 30f) return "ues2611";
        return "ues3512";
    }
}
