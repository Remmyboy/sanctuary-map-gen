using SanctuaryMapConverter.Core;

namespace SanctuaryMapConverter
{
    // Headless mode: the same orchestration the window drives, callable from
    // a terminal. This is what the golden-master tests use to prove the port
    // against the PowerShell pipeline.
    //
    //   SanctuaryMapConverter --convert <sourceFolder> [--cc0] [--name X]
    //       [--out <mapsRoot>] [--deploy] [--prop-ext .santp|.sanprop]
    internal static class Cli
    {
        public static int Run(string[] args)
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
            if (o.Source == null)
            {
                Console.Error.WriteLine("usage: --convert <sourceFolder> [--cc0] [--name X] [--out dir] [--deploy]");
                return 2;
            }

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
    }
}
