using SanctuaryMapConverter.Core;

namespace SanctuaryMapConverter
{
    // Headless mode: the same orchestration the window drives, callable from
    // a terminal. This is what the golden-master tests use to prove the port
    // against the PowerShell pipeline.
    //
    //   SanctuaryMapConverter --convert <sourceFolder> [--cc0] [--name X]
    //       [--out <mapsRoot>] [--deploy] [--prop-ext .santp|.sanprop] [--no-props]
    //   SanctuaryMapConverter --generate [--seed N] [--size 512] [--players 2]
    //       [--style S] [--biome B] [--count N] [--name X] [--out dir]
    //       [--prop-ext E] [--no-props] [--debug-dir D] [--force] [--no-validate]
    //   SanctuaryMapConverter --validate <path.sanmap> [--check-textures]
    //       [--gamedata <dir>] [--lua]
    internal static class Cli
    {
        public static int Run(string[] args) => args[0] switch
        {
            "--convert" => Convert(args),
            "--generate" => Generate(args),
            "--validate" => Validate(args),
            "--deploy-all" => DeployAllCmd(args),
            "--flat" => Flat(args),
            "--named" => NamedCmd(args),
            _ => Usage(),
        };

        static int NamedCmd(string[] args)
        {
            string which = null, outRoot = null, propExt = ".santp";
            bool validate = true;
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--out": outRoot = args[++i]; break;
                    case "--prop-ext": propExt = args[++i]; break;
                    case "--no-validate": validate = false; break;
                    default: which = args[i]; break;
                }
            }
            string sanctuary = GamePaths.FindSanctuaryInstall();
            outRoot ??= sanctuary != null
                ? GamePaths.EngineMaps(sanctuary)
                : Path.Combine(Environment.CurrentDirectory, "generated");
            var v = validate && sanctuary != null ? new ValidateOptions
            {
                Managed = Path.Combine(sanctuary, "map-editor", "SanctuaryMapEditor_Data", "Managed"),
                CheckTextures = true,
                GameRoot = sanctuary,
            } : null;

            switch (which?.ToLowerInvariant())
            {
                case "serpent": case "serpent-crossing": NamedMaps.RiverMap(outRoot, propExt, Console.WriteLine, v); return 0;
                case "riverbreak": NamedMaps.RiverbreakMap(outRoot, propExt, Console.WriteLine, v); return 0;
                case "cleftwater": case "cleft": NamedMaps.CleftMap(outRoot, propExt, Console.WriteLine, v); return 0;
                case "broken-mesa": case "organic": NamedMaps.OrganicMap(outRoot, propExt, Console.WriteLine, v); return 0;
                default:
                    Console.Error.WriteLine("--named wants one of: serpent, riverbreak, cleftwater, broken-mesa");
                    return 2;
            }
        }

        static int Usage()
        {
            Console.Error.WriteLine("usage: --convert <sourceFolder> [...] | --generate [...] | --validate <map.sanmap> [...] | --deploy-all [--skip-rebuild] | --flat --name <X> [...]");
            return 2;
        }

        static int DeployAllCmd(string[] args)
        {
            bool skipRebuild = Array.IndexOf(args, "--skip-rebuild") >= 0;
            string sanctuary = GamePaths.FindSanctuaryInstall();
            if (sanctuary == null)
            {
                Console.Error.WriteLine("deploy-all needs a Sanctuary install");
                return 2;
            }
            return Core.DeployAll.Run(sanctuary, skipRebuild, Console.WriteLine) ? 0 : 1;
        }

        static int Flat(string[] args)
        {
            var o = new FlatMapOptions();
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--name": o.Name = args[++i]; break;
                    case "--size": o.Size = int.Parse(args[++i]); break;
                    case "--out": o.MapsRoot = args[++i]; break;
                    case "--force": o.Force = true; break;
                    default: Console.Error.WriteLine($"unknown argument: {args[i]}"); return 2;
                }
            }
            if (o.Name == null)
            {
                Console.Error.WriteLine("--flat needs --name <display name>");
                return 2;
            }
            string sanctuary = GamePaths.FindSanctuaryInstall();
            o.MapsRoot ??= sanctuary != null
                ? GamePaths.EditorMaps(sanctuary)
                : Path.Combine(Environment.CurrentDirectory, "generated");
            try
            {
                FlatMap.Run(o, Console.WriteLine);
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("FAIL " + e.Message);
                return 1;
            }
        }

        static int Convert(string[] args)
        {
            var o = new ConvertOptions();
            bool deploy = false;
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--cc0": o.Cc0Textures = true; break;
                    case "--name": o.Name = args[++i]; break;
                    case "--out": o.OutputMapsRoot = args[++i]; break;
                    case "--deploy": deploy = true; break;
                    case "--prop-ext": o.PropExtension = args[++i]; break;
                    case "--no-props": o.NoProps = true; break;
                    default:
                        if (o.Source == null) o.Source = args[i];
                        else { Console.Error.WriteLine($"unknown argument: {args[i]}"); return 2; }
                        break;
                }
            }
            if (o.Source == null) return Usage();

            string fa = GamePaths.FindFaInstall();
            string sanctuary = GamePaths.FindSanctuaryInstall();
            o.ScdPath = GamePaths.ScdPath(fa);
            (o.PackDir, o.TableCsv) = GamePaths.DataFiles(GamePaths.FindDataDir());
            o.OutputMapsRoot ??= sanctuary != null
                ? GamePaths.EngineMaps(sanctuary)
                : Path.Combine(Environment.CurrentDirectory, "converted");

            if (!o.Cc0Textures && o.ScdPath == null)
            {
                Console.Error.WriteLine("source-texture mode needs a Forged Alliance install (env.scd); use --cc0 or install FA");
                return 2;
            }
            if (o.Cc0Textures && (o.PackDir == null || !File.Exists(o.TableCsv)))
            {
                Console.Error.WriteLine("CC0 mode needs the bundled data (texturepack + texture-map.csv) next to the exe");
                return 2;
            }

            try
            {
                var result = new Converter(o, Console.WriteLine).Run();
                if (deploy && sanctuary != null) Deployer.Deploy(result.MapDir, sanctuary, Console.WriteLine);
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("FAIL " + e.Message);
                return 1;
            }
        }

        static int Generate(string[] args)
        {
            var o = new RandomMapOptions();
            bool noValidate = false;
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--seed": o.Seed = int.Parse(args[++i]); break;
                    case "--size": o.Size = int.Parse(args[++i]); break;
                    case "--players": o.Players = int.Parse(args[++i]); break;
                    case "--style": o.Style = args[++i]; break;
                    case "--biome": o.Biome = args[++i]; break;
                    case "--count": o.Count = int.Parse(args[++i]); break;
                    case "--name": o.Name = args[++i]; break;
                    case "--out": o.MapsRoot = args[++i]; break;
                    case "--prop-ext": o.PropExtension = args[++i]; break;
                    case "--no-props": o.NoProps = true; break;
                    case "--debug-dir": o.DebugDir = args[++i]; break;
                    case "--force": o.Force = true; break;
                    case "--no-validate": noValidate = true; break;
                    default: Console.Error.WriteLine($"unknown argument: {args[i]}"); return 2;
                }
            }

            string sanctuary = GamePaths.FindSanctuaryInstall();
            o.MapsRoot ??= sanctuary != null
                ? GamePaths.EngineMaps(sanctuary)
                : Path.Combine(Environment.CurrentDirectory, "generated");
            if (!noValidate && sanctuary != null)
                o.Validate = new ValidateOptions
                {
                    Managed = Path.Combine(sanctuary, "map-editor", "SanctuaryMapEditor_Data", "Managed"),
                    CheckTextures = true,
                    LuaCheck = true,
                    GameRoot = sanctuary,
                };

            try
            {
                var results = RandomMap.Run(o, Console.WriteLine);
                return results.TrueForAll(r => r.Accepted) ? 0 : 1;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("FAIL " + e.Message);
                return 1;
            }
        }

        static int Validate(string[] args)
        {
            var o = new ValidateOptions();
            var paths = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--check-textures": o.CheckTextures = true; break;
                    case "--gamedata": o.GamedataDir = args[++i]; break;
                    case "--lua": o.LuaCheck = true; break;
                    case "--managed": o.Managed = args[++i]; break;
                    default: paths.Add(args[i]); break;
                }
            }
            if (paths.Count == 0) return Usage();

            string sanctuary = GamePaths.FindSanctuaryInstall();
            o.Managed ??= sanctuary != null
                ? Path.Combine(sanctuary, "map-editor", "SanctuaryMapEditor_Data", "Managed")
                : null;
            o.GameRoot ??= sanctuary;
            if (o.Managed == null)
            {
                Console.Error.WriteLine("validation needs a Sanctuary install (EM.Map.dll); pass --managed <dir>");
                return 2;
            }

            int failures = 0;
            foreach (var p in paths)
                if (!Validator.Check(p, o, Console.WriteLine)) failures++;
            return failures == 0 ? 0 : 1;
        }
    }
}
