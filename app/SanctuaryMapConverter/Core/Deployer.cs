namespace SanctuaryMapConverter.Core
{
    // Mirror a converted map into both game trees. The engine wants .santp
    // prop blueprints and the map editor wants .sanprop - same content,
    // different extension in the blueprint paths - so the editor copy gets
    // its .sanmap rewritten in place.
    public static class Deployer
    {
        public static void Deploy(string mapDir, string sanctuaryInstall, Action<string> log)
        {
            string folder = Path.GetFileName(mapDir.TrimEnd(Path.DirectorySeparatorChar));
            string engineMaps = GamePaths.EngineMaps(sanctuaryInstall);
            string editorMaps = GamePaths.EditorMaps(sanctuaryInstall);

            string engineDest = Path.Combine(engineMaps, folder);
            if (!string.Equals(Path.GetFullPath(mapDir), Path.GetFullPath(engineDest), StringComparison.OrdinalIgnoreCase))
            {
                CopyTree(mapDir, engineDest);
                log?.Invoke($"  deployed -> {engineDest}");
            }

            if (Directory.Exists(editorMaps))
            {
                string editorDest = Path.Combine(editorMaps, folder);
                CopyTree(mapDir, editorDest);
                string sanmap = Path.Combine(editorDest, folder + ".sanmap");
                if (File.Exists(sanmap))
                {
                    string t = File.ReadAllText(sanmap).Replace(".santp\"", ".sanprop\"");
                    File.WriteAllText(sanmap, t, new System.Text.UTF8Encoding(false));
                }
                log?.Invoke($"  deployed -> {editorDest}");
            }
        }

        static void CopyTree(string src, string dest)
        {
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.CreateDirectory(dest);
            foreach (string dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dest + dir.Substring(src.Length));
            foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                File.Copy(file, dest + file.Substring(src.Length));
        }
    }
}
