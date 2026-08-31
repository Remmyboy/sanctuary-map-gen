using Microsoft.Win32;

namespace SanctuaryMapConverter.Core
{
    // Locate the two installs the tool works between. Nothing is assumed:
    // Forged Alliance gates the source-texture mode (the user's own env.scd
    // is the only place that art ever comes from), and Sanctuary provides the
    // deploy targets.
    public static class GamePaths
    {
        public static string FindFaInstall()
        {
            foreach (var lib in SteamLibraries())
            {
                string p = Path.Combine(lib, "steamapps", "common", "Supreme Commander Forged Alliance");
                if (File.Exists(Path.Combine(p, "gamedata", "env.scd"))) return p;
            }
            return null;
        }

        public static string FindSanctuaryInstall()
        {
            foreach (var lib in SteamLibraries())
            {
                foreach (var name in new[] { "Sanctuary Shattered Sun Playtest", "Sanctuary Shattered Sun Demo", "Sanctuary Shattered Sun" })
                {
                    string p = Path.Combine(lib, "steamapps", "common", name);
                    if (Directory.Exists(Path.Combine(p, "engine", "Sanctuary_Data", "Maps"))) return p;
                }
            }
            return null;
        }

        public static string ScdPath(string faInstall) =>
            faInstall == null ? null : Path.Combine(faInstall, "gamedata", "env.scd");

        public static string EngineMaps(string sanctuary) =>
            Path.Combine(sanctuary, "engine", "Sanctuary_Data", "Maps");

        public static string EditorMaps(string sanctuary) =>
            Path.Combine(sanctuary, "map-editor", "SanctuaryMapEditor_Data", "Maps");

        /// The Managed dir holding SanMap and Newtonsoft for validation. The
        /// map editor's when the install ships one; the engine's otherwise -
        /// the Playtest build dropped the editor.
        public static string ManagedDir(string sanctuary)
        {
            string editor = Path.Combine(sanctuary, "map-editor", "SanctuaryMapEditor_Data", "Managed");
            if (Directory.Exists(editor)) return editor;
            return Path.Combine(sanctuary, "engine", "Sanctuary_Data", "Managed");
        }

        /// The FAF vault and steam both hold source maps; offer every folder
        /// that exists.
        public static IEnumerable<string> SourceMapRoots(string faInstall)
        {
            if (faInstall != null)
            {
                string steam = Path.Combine(faInstall, "maps");
                if (Directory.Exists(steam)) yield return steam;
            }
            string docs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "My Games", "Gas Powered Games", "Supreme Commander Forged Alliance", "Maps");
            if (Directory.Exists(docs)) yield return docs;
        }

        /// Bundled data (texturepack + substitution table): next to the exe in
        /// production, up the tree in development.
        public static string FindDataDir()
        {
            string exeDir = AppContext.BaseDirectory;
            foreach (var cand in new[]
            {
                Path.Combine(exeDir, "data"),
                // Development layout: repo root holds texturepack/ and docs/.
                Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "..")),
            })
            {
                if (File.Exists(Path.Combine(cand, "texture-map.csv")) ||
                    (File.Exists(Path.Combine(cand, "docs", "texture-map.csv")) &&
                     Directory.Exists(Path.Combine(cand, "texturepack"))))
                    return cand;
            }
            return null;
        }

        /// Resolve the two data files whichever layout FindDataDir returned.
        public static (string packDir, string tableCsv) DataFiles(string dataDir)
        {
            if (dataDir == null) return (null, null);
            string pack = Path.Combine(dataDir, "texturepack");
            string table = Path.Combine(dataDir, "texture-map.csv");
            if (!File.Exists(table)) table = Path.Combine(dataDir, "docs", "texture-map.csv");
            return (pack, table);
        }

        static IEnumerable<string> SteamLibraries()
        {
            string steam = null;
            try
            {
                steam = Registry.GetValue(@"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam", "SteamPath", null) as string;
            }
            catch { }
            if (steam != null)
            {
                steam = steam.Replace('/', Path.DirectorySeparatorChar);
                yield return steam;
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    foreach (System.Text.RegularExpressions.Match m in
                        System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        yield return m.Groups[1].Value.Replace("\\\\", "\\");
                }
            }
            // A plain drive scan as the fallback for non-Steam layouts.
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed) continue;
                string p = Path.Combine(d.RootDirectory.FullName, "SteamLibrary");
                if (Directory.Exists(p)) yield return p;
            }
        }
    }
}
