using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SanctuaryMapConverter.Core
{
    // tools/Test-Deployed.ps1 in C#. Three faults this project shipped were
    // all of one kind - something the game needs was absent or wrong, and
    // nothing said so until it was seen in game: editor-extension prop
    // blueprints in the engine tree (no extractors at all), omitted .sanmap
    // fields (black height fog in every hollow), and a splat format the
    // shipped maps do not use. Each was a two-second check away; this runs
    // all of them over both trees.
    public static class DeployedCheck
    {
        static readonly string[] OurNamed = { "Riverbreak", "Cleftwater", "Broken_Mesa", "Serpent_Crossing" };

        static bool IsOurs(string name) =>
            name.StartsWith("~GEN-") || name.StartsWith("~SC-") || OurNamed.Contains(name);

        public static bool Run(string engineMaps, string editorMaps, string gamedata,
            string managedDir, string reference, Action<string> log)
        {
            // A map the developers shipped is the authority on which fields a
            // .sanmap is expected to carry.
            var refFields = new List<string>();
            string refDir = Path.Combine(engineMaps, reference);
            if (Directory.Exists(refDir))
            {
                string refMap = Directory.EnumerateFiles(refDir, "*.sanmap").FirstOrDefault();
                if (refMap != null)
                    using (var doc = JsonDocument.Parse(File.ReadAllText(refMap)))
                        refFields.AddRange(doc.RootElement.EnumerateObject().Select(p => p.Name));
            }

            int problems = 0;
            foreach (var (treeName, root, ext) in new[] { ("engine", engineMaps, ".santp"), ("editor", editorMaps, ".sanprop") })
            {
                if (!Directory.Exists(root)) continue;
                log($"== {treeName} tree");

                foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d))
                {
                    string name = Path.GetFileName(dir);
                    if (!IsOurs(name)) continue;   // leave the shipped maps alone
                    string f = Directory.EnumerateFiles(dir, "*.sanmap").FirstOrDefault();
                    if (f == null) continue;

                    var issues = new List<string>();
                    string raw = File.ReadAllText(f);
                    using var doc = JsonDocument.Parse(raw);
                    var j = doc.RootElement;

                    // 1. prop blueprints must carry this tree's extension
                    string wrong = ext == ".santp" ? ".sanprop" : ".santp";
                    int nWrong = Regex.Matches(raw, Regex.Escape(wrong) + "\"").Count;
                    if (nWrong > 0) issues.Add($"{nWrong} blueprint(s) use {wrong}, this tree wants {ext}");

                    // 2. every field the reference map sets
                    if (refFields.Count > 0)
                    {
                        var have = j.EnumerateObject().Select(p => p.Name).ToHashSet();
                        var missing = refFields.Where(rf => !have.Contains(rf)).ToList();
                        if (missing.Count > 0) issues.Add("missing field(s): " + string.Join(", ", missing));
                    }

                    // 3. heightmap resolution must be a power of two plus one
                    int hmr = j.GetProperty("heightmapResolution").GetInt32();
                    int n = hmr - 1;
                    if (n <= 0 || (n & (n - 1)) != 0) issues.Add($"heightmapResolution {hmr} is not 2^n+1");

                    // 4. splat must match the heightmap grid and use the
                    //    shipped TGA header
                    string t = Path.Combine(dir, "Textures", "stratums_1_4.tga");
                    if (File.Exists(t))
                    {
                        var b = new byte[18];
                        using (var fs = File.OpenRead(t)) fs.ReadExactly(b, 0, 18);
                        int sw = b[12] | (b[13] << 8);
                        if (sw != hmr) issues.Add($"splat {sw} does not match heightmapResolution {hmr}");
                        if (b[17] != 0x28) issues.Add($"TGA descriptor 0x{b[17]:x2}, shipped maps use 0x28");
                    }
                    else issues.Add("stratums_1_4.tga missing");

                    if (issues.Count > 0)
                    {
                        problems++;
                        log($"  FAIL {name}");
                        foreach (var i in issues) log($"         {i}");
                    }
                    else log($"  ok   {name}");
                }
            }

            // Asset resolution is the expensive check, so it runs once over
            // the engine tree, where a missing blueprint actually costs you
            // the map.
            log("");
            log("Asset resolution (engine tree)");
            foreach (var dir in Directory.EnumerateDirectories(engineMaps).OrderBy(d => d))
            {
                string name = Path.GetFileName(dir);
                if (!IsOurs(name)) continue;
                string f = Directory.EnumerateFiles(dir, "*.sanmap").FirstOrDefault();
                if (f == null) continue;

                var lines = new List<string>();
                bool ok = Validator.Check(f, new ValidateOptions
                {
                    Managed = managedDir,
                    CheckTextures = true,
                    GamedataDir = gamedata,
                }, lines.Add);
                if (!ok)
                {
                    problems++;
                    log($"  FAIL {name}");
                    foreach (var l in lines.Where(l => l.TrimStart().StartsWith("Environment/") || l.Contains("missing")))
                        log($"       {l.Trim()}");
                }
                else log($"  ok   {name}");
            }

            log("");
            log(problems > 0 ? $"{problems} problem(s)" : "all deployed maps pass");
            return problems == 0;
        }
    }
}
