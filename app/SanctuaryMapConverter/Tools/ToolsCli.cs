using System.Linq;
using SanctuaryMapConverter.Core;

namespace SanctuaryMapConverter.Tools
{
    // Dispatcher for the ported tools/ scripts:
    //
    //   SanctuaryMapConverter --tool <name> [options]
    //
    // The tool methods take explicit paths; the defaults the PowerShell
    // scripts hardcoded (this machine's F:\ install) are resolved here via
    // GamePaths instead, so the exe works on any machine.
    public static class ToolsCli
    {
        public static int Run(string[] args)
        {
            if (args.Length < 2) return Usage();
            string name = args[1].ToLowerInvariant();
            var opt = ParseOptions(args, 2, out var positional);

            string sanctuary = GamePaths.FindSanctuaryInstall();
            string fa = GamePaths.FindFaInstall();
            string engineMaps = sanctuary != null ? GamePaths.EngineMaps(sanctuary) : null;
            string sanpack = sanctuary != null
                ? Path.Combine(sanctuary, "engine", "Sanctuary_Data", "Gamedata", "Environment.sanpack")
                : null;
            string[] scRoots = GamePaths.SourceMapRoots(fa).ToArray();
            var log = (Action<string>)Console.WriteLine;

            string Opt(string key, string fallback = null) => opt.TryGetValue(key, out var v) ? v : fallback;
            int OptInt(string key, int fallback) => opt.TryGetValue(key, out var v) ? int.Parse(v) : fallback;
            string NeedMaps() => Opt("maps", engineMaps) ?? throw new Exception("no Sanctuary install found; pass --maps <dir>");
            string NeedPack() => Opt("sanpack", sanpack) ?? throw new Exception("no Sanctuary install found; pass --sanpack <file>");
            string[] NeedScRoots() => opt.TryGetValue("maps", out var m) ? new[] { m }
                : scRoots.Length > 0 ? scRoots
                : throw new Exception("no Supreme Commander map folders found; pass --maps <dir>");

            try
            {
                switch (name)
                {
                    case "show-sanmap":
                        return ShowTools.Sanmap(
                            positional.Count > 0 ? positional.ToArray() : new[] { NeedMaps() },
                            Opt("out"), OptInt("res", 0), log);
                    case "texture-tones":
                        return ShowTools.TextureTones(NeedPack(), Opt("match"), log);
                    case "show-stratums":
                        return ShowTools.Stratums(Need(positional, "map folder"), log);
                    case "show-splat":
                        return ShowTools.SplatMap(Need(positional, "map folder"), Opt("out"), OptInt("res", 0), log);

                    case "measure-san-terrain":
                        return MeasureTools.SanTerrain(NeedMaps(), Opt("filter", "*"),
                            OptInt("max-size", 2048), opt.ContainsKey("per-map"), Opt("csv"), log);
                    case "measure-sanmaps":
                        return MeasureTools.Sanmaps(NeedMaps(), Opt("filter", "*"), opt.ContainsKey("per-map"), log);
                    case "measure-sc-terrain":
                        return MeasureTools.ScTerrain(NeedScRoots(), OptInt("sample", 1),
                            OptInt("max-size", 2048), opt.ContainsKey("per-map"), Opt("csv"), log);
                    case "measure-sc-corpus":
                        return MeasureTools.ScCorpus(NeedScRoots(), opt.ContainsKey("per-map"), log);

                    case "test-environment":
                        return CheckTools.Environment(
                            positional.Count > 0 ? positional.ToArray() : null,
                            NeedMaps(), null, null, log);
                    case "test-splat-alignment":
                        return CheckTools.SplatAlignment(Need(positional, "map folder"),
                            opt.ContainsKey("layer") ? OptInt("layer", 7) : null,
                            opt.ContainsKey("max-shift") ? OptInt("max-shift", 6) : null, log);
                    case "test-scmap":
                        return CheckTools.ScMapCheck(NeedScRoots()[0], Opt("filter", "*"), log);
                    case "test-biome-textures":
                        return CheckTools.BiomeTextures(NeedPack(), null, log);
                    case "compare-textures":
                        return CheckTools.CompareMapTextures(NeedMaps(),
                            Opt("map-a"), Opt("map-b"),
                            opt.ContainsKey("layers") ? OptInt("layers", 6) : null, Opt("out"), log);

                    case "measure-sc-textures":
                        return TexturePackTools.MeasureScTextures(NeedScRoots(),
                            Opt("scd", GamePaths.ScdPath(fa)) ?? throw new Exception("no Forged Alliance install found; pass --scd <env.scd>"),
                            Opt("csv"), log);
                    case "match-textures":
                        return TexturePackTools.MatchTextures(
                            Need(opt, "sc-csv", "the sc-textures.csv from measure-sc-textures"),
                            Need(opt, "pack", "the texturepack folder"),
                            Need(opt, "out", "the output texture-map.csv"), null, log);
                    case "build-texturepack":
                        return TexturePackTools.BuildTexturePack(
                            Need(opt, "out", "the texturepack output folder"),
                            Opt("variant"), opt.ContainsKey("force"), log);

                    default:
                        return Usage();
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("FAIL " + e.GetBaseException().Message);
                return 1;
            }
        }

        static string Need(List<string> positional, string what) =>
            positional.Count > 0 ? positional[0] : throw new Exception($"this tool needs a {what} argument");

        static string Need(Dictionary<string, string> opt, string key, string what) =>
            opt.TryGetValue(key, out var v) ? v : throw new Exception($"this tool needs --{key} ({what})");

        // "--key value" pairs plus bare positionals; "--flag" followed by
        // another option (or nothing) is a boolean switch.
        static Dictionary<string, string> ParseOptions(string[] args, int start, out List<string> positional)
        {
            var opt = new Dictionary<string, string>();
            positional = new List<string>();
            for (int i = start; i < args.Length; i++)
            {
                if (args[i].StartsWith("--"))
                {
                    string key = args[i].Substring(2);
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) opt[key] = args[++i];
                    else opt[key] = "true";
                }
                else positional.Add(args[i]);
            }
            return opt;
        }

        static int Usage()
        {
            Console.Error.WriteLine(@"usage: --tool <name> [options]
  show-sanmap [mapDir...] [--out dir] [--res N]
  texture-tones [--sanpack file] [--match regex]
  show-stratums <mapDir>
  show-splat <mapDir> [--out png] [--res N]
  measure-san-terrain [--maps dir] [--filter X] [--max-size N] [--per-map] [--csv file]
  measure-sanmaps [--maps dir] [--filter X] [--per-map]
  measure-sc-terrain [--maps dir] [--sample N] [--max-size N] [--per-map] [--csv file]
  measure-sc-corpus [--maps dir] [--per-map]
  test-environment [map.sanmap...] [--maps dir]
  test-splat-alignment <mapDir> [--layer N] [--max-shift N]
  test-scmap [--maps dir] [--filter X]
  test-biome-textures [--sanpack file]
  compare-textures [--maps dir] [--map-a X] [--map-b Y] [--layers N] [--out png]
  measure-sc-textures [--maps dir] [--scd env.scd] [--csv file]
  match-textures --sc-csv file --pack dir --out file
  build-texturepack --out dir [--variant 1K-JPG] [--force]");
            return 2;
        }
    }
}
