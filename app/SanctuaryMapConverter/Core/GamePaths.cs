using Microsoft.Win32;

namespace SanctuaryMapConverter.Core
{
    // Locate the two installs the tool works between. Nothing is assumed:
    // Forged Alliance gates the source-texture mode (the user's own env.scd
    // is the only place that art ever comes from), and Sanctuary provides the
    // deploy targets.
    public static class GamePaths
    {
        // Common install folder names across the ways people own this game:
        // Steam, the retail/GOG installer, and Forged Alliance Forever, which
        // most community mappers use and which installs beside the base game.
        static readonly string[] FaFolderNames =
        {
            "Supreme Commander Forged Alliance",
            "Supreme Commander - Forged Alliance",
            "SupremeCommanderForgedAlliance",
            "Forged Alliance Forever",
            "ForgedAllianceForever",
        };

        /// The user's Forged Alliance install, or null.
        ///
        /// Steam is the common case but far from the only one, and a tool that
        /// only finds Steam installs looks broken to everyone else. So: the
        /// registry keys the retail installer and Steam's uninstall entry
        /// write, then every Steam library, then a scan of likely roots on
        /// each fixed drive. env.scd is the proof - it is the file the
        /// source-texture mode actually reads.
        public static string FindFaInstall()
        {
            foreach (var cand in FaCandidates())
            {
                if (cand == null) continue;
                if (File.Exists(Path.Combine(cand, "gamedata", "env.scd"))) return cand;
            }
            return null;
        }

        static IEnumerable<string> FaCandidates()
        {
            // Registry: the retail installer's own key, and Steam's uninstall
            // entry for app 9420 (Forged Alliance).
            foreach (var (key, valueName) in new[]
            {
                (@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\THQ\Gas Powered Games\Supreme Commander Forged Alliance", "InstallPath"),
                (@"HKEY_LOCAL_MACHINE\SOFTWARE\THQ\Gas Powered Games\Supreme Commander Forged Alliance", "InstallPath"),
                (@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 9420", "InstallLocation"),
                (@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 9420", "InstallLocation"),
            })
            {
                string v = null;
                try { v = Registry.GetValue(key, valueName, null) as string; }
                catch { }
                if (!string.IsNullOrWhiteSpace(v)) yield return v.Trim().Trim('"');
            }

            foreach (var lib in SteamLibraries())
                foreach (var name in FaFolderNames)
                    yield return Path.Combine(lib, "steamapps", "common", name);

            // Non-Steam layouts: the installer's defaults and the obvious
            // hand-made ones, on every fixed drive.
            foreach (var d in FixedDriveRoots())
                foreach (var mid in new[] { "", "Games", "Program Files (x86)", "Program Files",
                                            Path.Combine("Program Files (x86)", "THQ"), Path.Combine("Games", "THQ") })
                    foreach (var name in FaFolderNames)
                        yield return Path.Combine(d, mid, name);
        }

        static IEnumerable<string> FixedDriveRoots()
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed) continue;
                string root = null;
                try { if (d.IsReady) root = d.RootDirectory.FullName; } catch { }
                if (root != null) yield return root;
            }
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

        /// Where the game keeps its asset packs.
        public static string GamedataDir(string sanctuary) =>
            sanctuary == null ? null : Path.Combine(sanctuary, "engine", "Sanctuary_Data", "Gamedata");

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
        /// Where the substitution table and the CC0 texturepack live.
        ///
        /// Two passes, and the order matters: a candidate holding BOTH the
        /// table and the pack beats one holding only the table. The build
        /// copies the CSVs next to the exe, so in a dev tree that folder
        /// exists but has no pack - taking it on the first sighting would
        /// disable CC0 mode against a repo that has the pack right there.
        public static string FindDataDir()
        {
            string exeDir = AppContext.BaseDirectory;
            var cands = new[]
            {
                Path.Combine(exeDir, "data"),
                // Development layout: repo root holds texturepack/ and docs/.
                Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "..")),
            };
            foreach (var cand in cands)
                if (HasTable(cand) && Directory.Exists(Path.Combine(cand, "texturepack"))) return cand;
            foreach (var cand in cands)
                if (HasTable(cand)) return cand;
            return null;
        }

        static bool HasTable(string dir) =>
            File.Exists(Path.Combine(dir, "texture-map.csv")) ||
            File.Exists(Path.Combine(dir, "docs", "texture-map.csv"));

        /// Is the CC0 mode's data actually present? The table alone is not
        /// enough - without the texturepack behind it every layer would
        /// resolve to nothing, which is a failure worth catching before a
        /// conversion starts rather than part-way through one.
        public static bool HaveCc0Data(string packDir, string tableCsv) =>
            packDir != null && tableCsv != null &&
            File.Exists(tableCsv) && Directory.Exists(packDir);

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
