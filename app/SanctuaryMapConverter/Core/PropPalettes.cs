namespace SanctuaryMapConverter.Core
{
    // The Sanctuary prop palette, classified by eye from rendered silhouettes
    // of every model the game ships. Only props present in BOTH game builds
    // are usable - the editor pack has no .sanprop for the WhiteDesert set.
    // The converter maps source environment families onto matched palettes so
    // a tundra map gets conifers and a desert map does not get pine trees.
    public static class PropPalettes
    {
        static readonly string[] TreesLarge = { "edbm0121", "edbm0122", "edbm0123", "edbm0141", "edbm0143", "edbm0144", "edbm0145", "edbm0146", "edbm0201" };
        static readonly string[] TreesSmall = { "edbm0101", "edbm0103", "edbm0104", "edbm0105", "edbm0106", "edbm0124", "edbm0125", "edbm0147", "edbm0150" };
        static readonly string[] TreesMixed;
        static readonly string[] TreesConifer = { "edbm0401", "edbm0402" };
        static readonly string[] TreesDry = { "edbm0161", "edbm0148", "edbm0149", "edbm0150" };
        static readonly string[] RocksMed = { "edmm0101", "edmm0102", "edmm0103", "edmm0104", "edmm0105", "edmm0106", "edmm0107", "edmm0108" };
        static readonly string[] RocksDark = { "edmm0110", "edmm0111", "edmm0112", "edmm0113", "edml0111" };
        static readonly string[] RocksOlive = { "edmm0201", "edmm0202", "edmm0203", "edmm0204", "edml0201" };
        static readonly string[] RocksSmall = { "edms0101", "edms0102", "edms0103", "edms0104", "edms0105", "edms0110", "edms0111", "edms0112" };
        static readonly string[] Logs = { "edbs0112", "edbs0113", "edbs0115", "edbs0116" };

        public static readonly string[] FallbackTrees = { "edbm0121", "edbm0122", "edbm0123", "edbm0124", "edbm0125" };
        public static readonly string[] FallbackRocks = { "edmm0104", "edmm0106", "edms0110" };

        static readonly Dictionary<string, string> EnvPrefix = new(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, int> RoundRobin = new();

        static PropPalettes()
        {
            TreesMixed = new string[TreesLarge.Length + TreesSmall.Length];
            TreesLarge.CopyTo(TreesMixed, 0);
            TreesSmall.CopyTo(TreesMixed, TreesLarge.Length);
            foreach (var n in new[] { "edbm0201", "edml0201", "edmm0201", "edmm0202", "edmm0203", "edmm0204" })
                EnvPrefix[n] = "Environment/02_Evergreen";
            foreach (var n in new[] { "edbm0401", "edbm0402" })
                EnvPrefix[n] = "Environment/04_Baikal";
        }

        public static string EnvOf(string name) =>
            EnvPrefix.TryGetValue(name, out var e) ? e : "Environment/01_Highlands";

        public static void ResetRoundRobin() => RoundRobin.Clear();

        /// Pick a Sanctuary prop for one source prop: environment family from
        /// the blueprint path, kind from the classifier, size hints from the
        /// name. Each list round-robins so neighbouring props vary.
        public static string Pick(MapGen.ScPropOut p)
        {
            string bp = (p.Blueprint ?? "").ToLowerInvariant();
            var seg = bp.Split('/');
            string fam = seg.Length > 2 ? seg[2] : "";

            string[] list;
            if (p.Kind == 2)
            {
                if (bp.Contains("/logs/")) list = Logs;
                else if (System.Text.RegularExpressions.Regex.IsMatch(bp, @"sm\d|small|pebble|fieldstone")) list = RocksSmall;
                else if (fam is "desert" or "red barrens" or "redrocks" or "lava" or "geothermal") list = RocksDark;
                else if (fam is "tropical" or "swamp" or "paradise") list = RocksOlive;
                else list = RocksMed;
            }
            else if (fam is "tundra" or "crystalline" or "crystalline-alt") list = TreesConifer;
            else if (fam is "desert" or "red barrens" or "redrocks" or "lava" or "geothermal") list = TreesDry;
            else if (p.Kind == 1) list = TreesLarge;
            else list = TreesMixed;

            string key = list.Length + list[0];
            RoundRobin.TryGetValue(key, out int i);
            RoundRobin[key] = i + 1;
            return list[i % list.Length];
        }
    }
}
