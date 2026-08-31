using System.Linq;
using System.Text;
using System.Text.Json;

namespace SanctuaryMapConverter.Core
{
    // Deploy-All.ps1 in C#. The engine wants .santp prop blueprints and the
    // map editor wants .sanprop, so each named map is built twice rather than
    // copied. Generated and converted maps already exist under the engine
    // tree, so those are copied across with the extension rewritten in place.
    //
    // Restart the map editor afterwards: it indexes a map folder the first
    // time that map is opened and will not notice files added to a folder it
    // has already scanned.
    public static class DeployAll
    {
        // The named maps, built fresh into each tree. Each entry builds one
        // map into mapsRoot with the given prop extension.
        public static readonly List<(string Name, Action<string, string, Action<string>> Build)> Named = new()
        {
            ("Serpent Crossing", (root, ext, log) => NamedMaps.RiverMap(root, ext, log)),
            ("Riverbreak", (root, ext, log) => NamedMaps.RiverbreakMap(root, ext, log)),
            ("Cleftwater", (root, ext, log) => NamedMaps.CleftMap(root, ext, log)),
            ("Broken Mesa", (root, ext, log) => NamedMaps.OrganicMap(root, ext, log)),
        };

        public static bool Run(string sanctuaryInstall, bool skipRebuild, Action<string> log)
        {
            string engineMaps = GamePaths.EngineMaps(sanctuaryInstall);
            string editorMaps = GamePaths.EditorMaps(sanctuaryInstall);

            if (!skipRebuild)
                foreach (var (name, build) in Named)
                    foreach (var (root, ext) in new[] { (engineMaps, ".santp"), (editorMaps, ".sanprop") })
                    {
                        log($"Building {name} -> {Path.GetFileName(Path.GetDirectoryName(root))}");
                        var lines = new List<string>();
                        build(root, ext, lines.Add);
                        foreach (var l in lines.Where(l => l.StartsWith("PASS") || l.StartsWith("FAIL") || l.StartsWith("LUA-")))
                            log($"    {l}");
                    }

            // Everything else that only lives under the engine tree:
            // generated batches and Supreme Commander conversions.
            foreach (var dir in Directory.EnumerateDirectories(engineMaps))
            {
                string name = Path.GetFileName(dir);
                if (!name.StartsWith("~GEN-") && !name.StartsWith("~SC-")) continue;
                string dest = Path.Combine(editorMaps, name);
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                CopyTree(dir, dest);
                string f = Directory.EnumerateFiles(dest, "*.sanmap").FirstOrDefault();
                if (f != null)
                    File.WriteAllText(f, File.ReadAllText(f).Replace(".santp\"", ".sanprop\""), new UTF8Encoding(false));
                log($"Copied {name}");
            }

            log("");
            log("In the map editor:");
            foreach (var dir in Directory.EnumerateDirectories(editorMaps).OrderBy(d => d))
            {
                string f = Directory.EnumerateFiles(dir, "*.sanmap").FirstOrDefault();
                if (f == null) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var j = doc.RootElement;
                int spawns = 0, alloys = 0;
                if (j.TryGetProperty("markers", out var markers))
                {
                    if (markers.TryGetProperty("Spawn", out var s) && s.TryGetProperty("transforms", out var st))
                        spawns = st.EnumerateObject().Count();
                    if (markers.TryGetProperty("Alloys", out var a) && a.TryGetProperty("transforms", out var at))
                        alloys = at.EnumerateObject().Count();
                }
                bool hasWater = j.TryGetProperty("hasWater", out var hw) && hw.GetBoolean();
                string water = hasWater
                    ? string.Format("water {0:N0}", j.GetProperty("waterLevel").GetDouble())
                    : "dry";
                log(string.Format("  {0,-42} {1,5} m  {2} spawns  {3,3} alloys  {4}",
                    Path.GetFileName(dir), j.GetProperty("width").GetInt32(), spawns, alloys, water));
            }
            log("");
            log("Restart the map editor before opening these.");

            // Validate before declaring victory. Every fault this project
            // shipped was something absent or in the wrong format, and each
            // was a two-second check away.
            string gamedata = Path.Combine(sanctuaryInstall, "engine", "Sanctuary_Data", "Gamedata");
            string managed = GamePaths.ManagedDir(sanctuaryInstall);
            return DeployedCheck.Run(engineMaps, editorMaps, gamedata, managed, "~TEAM-1v1_Tropical_256_47940", log);
        }

        static void CopyTree(string src, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var d in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(src, d)));
            foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                File.Copy(f, Path.Combine(dest, Path.GetRelativePath(src, f)));
        }
    }
}
