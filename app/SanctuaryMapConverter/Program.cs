using System.Windows.Forms;

namespace SanctuaryMapConverter
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // --convert runs headless: the same orchestration the window
            // drives, callable from a terminal and from the golden-master
            // tests that prove the port against the PowerShell pipeline.
            if (args.Length > 0 && args[0] == "--convert")
                return Cli.Run(args);

            ApplicationConfiguration.Initialize();
            Application.Run(new Gui.MainForm());
            return 0;
        }
    }
}
